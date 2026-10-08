using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SheepCode.Distribution;

namespace SheepCode;

internal sealed record GenerationCatalog(string Id, string Label, string Repository, string Revision, string License, VisionAsset Model, VisionAsset RuntimeCpu, VisionAsset RuntimeGpu)
{
    internal static GenerationCatalog Load() { using var stream = typeof(GenerationCatalog).Assembly.GetManifestResourceStream("SheepCode.image-generation.json")!; return JsonSerializer.Deserialize<GenerationCatalog>(stream, AppPaths.Json)!; }
}
internal sealed record GenerationProfile(string Model, string Variant, string Device, string Description, Dictionary<string, string> Hashes);
internal sealed record GeneratedImage(string Id, string Status, string Prompt, int Width, int Height, int Seed, string Model, string Backend, double Seconds, string Sha256, string Notice, string Error = "");
internal sealed class ImageGeneration(Preferences preferences, SkillRegistry skills, Func<CancellationToken, Task>? releaseLanguage = null) : IDisposable
{
    internal const string Limits = "Creación local de una imagen PNG 512 × 512 con SD-Turbo. Necesita el paquete de 2,02 GB y al menos 8 GB de RAM; CPU puede tardar varios minutos. Produce una vista previa temporal; solo la persona guarda un PNG. No garantiza texto, manos, cantidades, transparencia o fidelidad exacta. El modelo funciona principalmente con descripciones en inglés. Sin subida de datos a Internet ni cambio de GPU de voz.";
    private sealed record Stored(GeneratedImage Result, byte[] Png);
    private readonly List<Stored> generated = [];
    private readonly SemaphoreSlim gate = new(1);
    private CancellationTokenSource? working;
    private Process? process;
    private static string Config => Path.Combine(AppPaths.State, "image-generation.json");
    private static string Runtime(string variant) => SafeFiles.Child(AppPaths.Root, "runtime/image-generation/" + (variant == "vulkan" ? "vulkan" : "cpu"));
    internal event Action<string>? Progress;
    internal event Action? Changed;
    internal bool Enabled => skills.Enabled("imagegen") && preferences.GenerateImages;
    internal bool Configured => File.Exists(Config);
    internal IReadOnlyList<GeneratedImage> Images => generated.Select(g => g.Result).ToArray();
    internal string State { get; private set; } = "Sin iniciar";
    internal object Status()
    {
        GenerationProfile? profile = null; try { if (Configured) profile = JsonSerializer.Deserialize<GenerationProfile>(File.ReadAllText(Config), AppPaths.Json); } catch (Exception e) when (e is IOException or JsonException) { }
        return new { enabled = Enabled, configured = profile is not null, state = !Enabled ? "desactivado" : profile is null ? "sin configurar" : State,
            model = "SD-Turbo F16/Q8 · 512×512", backend = profile?.Description ?? "sin configurar", previews = Images, scope = Limits,
            controls = "Instala el generador de imágenes; Activa/Desactiva la generación de imágenes; Configura generación CPU/auto; Genera una imagen: DESCRIPCIÓN; GUI 🎨 Crear → Guardar PNG." };
    }
    private async Task VerifyRuntimeAsync(GenerationProfile profile, CancellationToken token)
    {
        if (profile.Hashes.Count == 0 || !profile.Hashes.ContainsKey("sd-cli.exe")) throw new IOException("El runtime de imágenes no tiene manifiesto verificado.");
        foreach (var (name, hash) in profile.Hashes) if (!(await SafeFiles.HashAsync(SafeFiles.Child(Runtime(profile.Variant), name), token)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cambió el runtime de imágenes; repara la instalación.");
    }
    internal async Task InstallHumanAsync(string folder, CancellationToken token, string packageFolder = "")
    {
        skills.Require("imagegen"); Cancel(); await gate.WaitAsync(token);
        try
        {
            var catalog = GenerationCatalog.Load(); using var download = new VerifiedDownloader(); var progress = new Progress<InstallProgress>(p => Progress?.Invoke(p.Stage + " · " + p.Detail));
            var target = SafeFiles.Child(Path.GetFullPath(folder), catalog.Id); var model = SafeFiles.Child(target, catalog.Model.File);
            await download.DownloadAsync(catalog.Model.Url, model, catalog.Model.Size, catalog.Model.Sha256, progress, token);
            var profiles = new Dictionary<string, GenerationProfile>();
            foreach (var (variant, asset) in new[] { ("cpu", catalog.RuntimeCpu), ("vulkan", catalog.RuntimeGpu) })
            {
                var package = SafeFiles.Child(AppPaths.Root, "runtime/packages/" + asset.File);
                if (packageFolder.Length > 0) { var source = SafeFiles.Child(Path.GetFullPath(packageFolder), asset.File); if (!File.Exists(package) && File.Exists(source) && (await SafeFiles.HashAsync(source, token)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) { Directory.CreateDirectory(Path.GetDirectoryName(package)!); File.Copy(source, package); } }
                await download.DownloadAsync(asset.Url, package, asset.Size, asset.Sha256, progress, token);
                var stage = SafeFiles.Child(AppPaths.Root, "temp/imagegen-install-" + Guid.NewGuid().ToString("N"));
                try
                {
                    SafeFiles.ExtractZip(package, stage, token); var runtime = Runtime(variant); Directory.CreateDirectory(runtime); var hashes = new Dictionary<string, string>();
                    foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p) == "sd-cli.exe"))
                    { var name = Path.GetFileName(file); var to = SafeFiles.Child(runtime, name); File.Copy(file, to, true); hashes[name] = await SafeFiles.HashAsync(to, token); }
                    if (!hashes.ContainsKey("sd-cli.exe")) throw new IOException("El paquete no contiene sd-cli.exe.");
                    profiles[variant] = new(model, variant, "cpu", "CPU local · hasta 2 hilos", hashes);
                }
                finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            }
            AppPaths.SaveJson(Path.Combine(AppPaths.State, "image-generation-runtimes.json"), profiles);
            AppPaths.SaveJson(Config, profiles["cpu"]); await SelectHumanCoreAsync("auto", token); State = "Instalado · carga bajo petición";
        }
        finally { gate.Release(); }
    }
    internal async Task SelectHumanAsync(string mode, CancellationToken token)
    { skills.Require("imagegen"); Cancel(); await gate.WaitAsync(token); try { await SelectHumanCoreAsync(mode, token); } finally { gate.Release(); } }
    private async Task SelectHumanCoreAsync(string mode, CancellationToken token)
    {
        if (mode is not ("auto" or "cpu")) throw new ArgumentException("Usa Configura generación auto o CPU.");
        var registry = JsonSerializer.Deserialize<Dictionary<string, GenerationProfile>>(File.ReadAllText(Path.Combine(AppPaths.State, "image-generation-runtimes.json")), AppPaths.Json)!;
        var selected = registry["cpu"];
        if (mode == "auto")
        {
            var gpu = registry["vulkan"]; await VerifyRuntimeAsync(gpu, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var text = await ModelProvisioner.RunAsync(SafeFiles.Child(Runtime("vulkan"), "sd-cli.exe"), ["--list-devices"], Runtime("vulkan"), null, limit.Token);
                var actual = Regex.Match(text, @"(?im)^(vulkan\d+)\t([^\r\n]*NVIDIA[^\r\n]*RTX[^\r\n]*)\r?$");
                if (actual.Success) selected = gpu with { Device = actual.Groups[1].Value.ToLowerInvariant(), Description = actual.Groups[2].Value.Trim() + " · " + actual.Groups[1].Value + " · texto auxiliar CPU; RX 580 reservada para voz" };
                else Progress?.Invoke("RTX no verificada; seleccionado CPU explícitamente.");
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is IOException or OperationCanceledException) { Progress?.Invoke("Vulkan no disponible; CPU explícitamente. " + e.Message); }
        }
        AppPaths.SaveJson(Config, selected);
    }
    internal void Cancel() { try { working?.Cancel(); } catch (ObjectDisposedException) { } KillOwned(); }
    private void KillOwned() { try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    internal async Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken token, int? seed = null)
    {
        skills.Require("imagegen"); if (!preferences.GenerateImages) return Failure(prompt, "disabled", "Generación desactivada por la persona.");
        if (!Configured) return Failure(prompt, "unavailable", "Falta el generador local; usa «Instala el generador de imágenes» o 🎨 Crear → 📦.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1200 || prompt.Contains('\0')) throw new ArgumentException("Describe la imagen con entre 1 y 1200 caracteres.");
        if (HardwareScanner.Scan(AppPaths.Root).RamGiB < 7.5) return Failure(prompt, "unavailable", "El generador local necesita al menos 8 GB de RAM; el editor, OCR y el motor de código siguen disponibles.");
        await gate.WaitAsync(token); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); working = cancel; cancel.CancelAfter(TimeSpan.FromMinutes(10)); var clock = Stopwatch.StartNew();
        var folder = SafeFiles.Child(AppPaths.Root, "temp/imagegen-" + Guid.NewGuid().ToString("N"));
        try
        {
            token.ThrowIfCancellationRequested(); skills.Require("imagegen"); if (!preferences.GenerateImages) return Failure(prompt, "disabled", "Generación desactivada.");
            var catalog = GenerationCatalog.Load(); var profile = JsonSerializer.Deserialize<GenerationProfile>(File.ReadAllText(Config), AppPaths.Json)!;
            await VerifyRuntimeAsync(profile, cancel.Token);
            if (!File.Exists(profile.Model) || new FileInfo(profile.Model).Length != catalog.Model.Size || !(await SafeFiles.HashAsync(profile.Model, cancel.Token)).Equals(catalog.Model.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Los pesos del generador no coinciden con el catálogo.");
            if (!Regex.IsMatch(profile.Device, @"\A(?:cpu|vulkan\d+)\z") || profile.Device != "cpu" && !profile.Description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) throw new IOException("Backend no autorizado; reconfigura CPU o auto.");
            if (releaseLanguage is not null) await releaseLanguage(cancel.Token);
            Directory.CreateDirectory(folder); var output = Path.Combine(folder, "preview.png"); var chosenSeed = seed ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var start = new ProcessStartInfo(SafeFiles.Child(Runtime(profile.Variant), "sd-cli.exe")) { WorkingDirectory = Runtime(profile.Variant), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "-m", profile.Model, "-p", prompt, "-o", output, "-W", "512", "-H", "512", "--steps", "8", "--cfg-scale", "1", "--sampling-method", "euler", "--scheduler", "karras", "--seed", chosenSeed.ToString(), "--threads", "2",
                "--backend", profile.Device == "cpu" ? "cpu" : "te=cpu,vae=" + profile.Device + ",diffusion=" + profile.Device, "--vae-tiling", "--auto-fit", "off" }) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SD_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            var tail = new StringBuilder(); var sync = new object();
            void Log(object sender, DataReceivedEventArgs args) { if (args.Data is null) return; lock (sync) { tail.AppendLine(args.Data); if (tail.Length > 12000) tail.Remove(0, tail.Length - 8000); } if (Regex.IsMatch(args.Data, @"\d+/8|sampling|decode", RegexOptions.IgnoreCase)) Progress?.Invoke(args.Data[..Math.Min(160, args.Data.Length)]); }
            using var owned = new Process { StartInfo = start }; owned.OutputDataReceived += Log; owned.ErrorDataReceived += Log; process = owned; State = "Generando…"; Progress?.Invoke(State + " · " + profile.Description);
            if (!owned.Start()) throw new IOException("No se pudo iniciar el generador."); using var stop = cancel.Token.Register(KillOwned); owned.BeginOutputReadLine(); owned.BeginErrorReadLine();
            await owned.WaitForExitAsync(cancel.Token); owned.WaitForExit(); if (owned.ExitCode != 0 || !File.Exists(output)) throw new IOException("El generador no produjo un PNG: " + tail.ToString()[Math.Max(0, tail.Length - 2400)..]);
            var png = File.ReadAllBytes(output); if (png.Length > 16 * 1024 * 1024) throw new IOException("La salida supera 16 MiB.");
            using (var bytes = new MemoryStream(png, false)) using (var image = Image.FromStream(bytes)) if (image.Width != 512 || image.Height != 512) throw new IOException("El generador devolvió dimensiones inesperadas.");
            token.ThrowIfCancellationRequested(); skills.Require("imagegen"); if (!preferences.GenerateImages) return Failure(prompt, "disabled", "La persona desactivó la generación durante el trabajo.");
            var result = new GeneratedImage("gen-" + Guid.NewGuid().ToString("N")[..12], "generated", prompt, 512, 512, chosenSeed, catalog.Id, profile.Description, clock.Elapsed.TotalSeconds, AppPaths.Hash(png), Limits);
            generated.Add(new(result, png)); while (generated.Count > 4) generated.RemoveAt(0); Changed?.Invoke(); return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !Enabled) { return Failure(prompt, "disabled", "Generación detenida al desactivar la función."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure(prompt, "timeout", "El generador superó diez minutos y se detuvo su proceso propio."); }
        catch (Exception error) when (error is IOException or JsonException or System.ComponentModel.Win32Exception) { return Failure(prompt, "failed", error.Message); }
        finally { KillOwned(); process = null; working = null; State = "Descargado de RAM"; if (Directory.Exists(folder)) Directory.Delete(folder, true); gate.Release(); }
    }
    private static GeneratedImage Failure(string prompt, string status, string error) => new("", status, prompt, 0, 0, 0, "sd-turbo-q8", "", 0, "", Limits, error);
    internal byte[] Preview(string id) { skills.Require("imagegen"); return (generated.FirstOrDefault(g => g.Result.Id == id) ?? throw new ArgumentException("No existe esa vista previa de esta conversación.")).Png.ToArray(); }
    internal static string ExistingHashHuman(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)); }
    internal string? SaveHuman(string id, string destination, string? expectedHash = null)
    {
        var png = Preview(id); var full = Path.GetFullPath(destination); if (Path.GetExtension(full).ToLowerInvariant() != ".png") throw new ArgumentException("Guarda la imagen como PNG.");
        // External destinations are chosen by the human dialog; model tools have no save operation.
        bool Changed() => expectedHash is null ? File.Exists(full) : !File.Exists(full) || ExistingHashHuman(full) != expectedHash;
        if (Changed()) throw new IOException("El archivo cambió desde la confirmación; vuelve a elegir dónde guardar.");
        var temporary = full + ".sheepcode-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = expectedHash is null ? null : full + ".sheepcode-" + Guid.NewGuid().ToString("N") + ".bak";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(png); file.Flush(true); }
            if (Changed()) throw new IOException("Edición concurrente: se conserva la imagen actual.");
            if (expectedHash is null) File.Move(temporary, full);
            else File.Replace(temporary, full, backup);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void ClearHuman() { Cancel(); generated.Clear(); Changed?.Invoke(); }
    internal GeneratedImage LoadDiagnosticPreview(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.Combine(AppPaths.Root, "checks") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("La muestra sale de checks.");
        var png = File.ReadAllBytes(path); if (png.Length > 16 * 1024 * 1024) throw new IOException("Muestra demasiado grande.");
        var result = new GeneratedImage("gen-" + Guid.NewGuid().ToString("N")[..12], "generated", "Muestra real del diagnóstico", 512, 512, 0, "sd-turbo-q8", "vista sin nueva inferencia", 0, AppPaths.Hash(png), Limits); generated.Add(new(result, png)); return result;
    }
    public void Dispose() => ClearHuman();
}
