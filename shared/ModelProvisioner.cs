using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;

namespace SheepCode.Distribution;

internal static class ModelProvisioner
{
    private static async Task<string> InstallCpuRuntimeAsync(string root, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        using var metadata = JsonDocument.Parse(typeof(ModelProvisioner).Assembly.GetManifestResourceStream("SheepCode.strata-cpu.json") ?? throw new IOException("Falta el catálogo del motor Strata CPU."));
        var package = SafeFiles.Child(root, @"runtime\packages\strata-cpu.zip");
        if (!(await SafeFiles.HashAsync(package, token)).Equals(metadata.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("El paquete de Strata CPU no coincide con SHA-256.");
        var executable = SafeFiles.Child(root, @"runtime\strata-cpu\strata-cpu.exe");
        var expected = metadata.RootElement.GetProperty("executableSha256").GetString();
        if (File.Exists(executable) && (await SafeFiles.HashAsync(executable, token)).Equals(expected, StringComparison.OrdinalIgnoreCase)) return executable;
        progress?.Report(new("Preparando Strata CPU", "Motor nativo SSE2 optimizado · conservando modelo y ajustes"));
        var stage = SafeFiles.Child(root, @"temp\cpu-runtime-" + Guid.NewGuid().ToString("N"));
        SafeFiles.ExtractZip(package, stage, token);
        var replacement = SafeFiles.Child(stage, "strata-cpu.exe");
        if (!(await SafeFiles.HashAsync(replacement, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("El ejecutable de Strata CPU no coincide con SHA-256.");
        if (File.Exists(executable))
        {
            var backup = SafeFiles.Child(root, @"backups\before-cpu-runtime-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(backup); File.Copy(executable, Path.Combine(backup, "strata-cpu.exe"));
            DistributionJson.Save(Path.Combine(backup, "manifest.json"), new { path = "runtime/strata-cpu/strata-cpu.exe", sha256 = await SafeFiles.HashAsync(executable, token) });
        }
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        SafeFiles.Child(root, @"runtime\strata-cpu\strata-cpu.exe"); File.Move(replacement, executable, true);
        return executable;
    }
    private static async Task<string> InstallLlamaRuntimeAsync(string root, bool vulkan, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        using var metadata = JsonDocument.Parse(typeof(ModelProvisioner).Assembly.GetManifestResourceStream("SheepCode.native-runtime.json") ?? throw new IOException("Falta el catálogo del runtime nativo."));
        var asset = metadata.RootElement.GetProperty(vulkan ? "vulkan" : "cpu");
        var package = SafeFiles.Child(root, Path.Combine("runtime", "packages", asset.GetProperty("file").GetString()!));
        if (!File.Exists(package)) throw new FileNotFoundException("Falta el paquete local del motor: " + package + ". Reinstala el setup; no hace falta volver a descargar los pesos.", package);
        if (!(await SafeFiles.HashAsync(package, token)).Equals(asset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El paquete del runtime no coincide con SHA-256: " + package);
        var runtime = SafeFiles.Child(root, @"runtime\llama");
        string[] entries; bool complete;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(package))
        {
            entries = archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).ToArray();
            complete = archive.Entries.Where(e => !e.FullName.EndsWith('/')).All(e =>
            { var file = SafeFiles.Child(runtime, e.FullName); return File.Exists(file) && new FileInfo(file).Length == e.Length; });
        }
        if (!complete)
        {
            progress?.Report(new("Restaurando runtime nativo", (vulkan ? "Vulkan" : "CPU") + " · paquete local verificado; pesos y perfil conservados"));
            var backup = SafeFiles.Child(root, @"backups\before-native-runtime-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            var preserved = new List<object>();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested(); var file = SafeFiles.Child(runtime, entry);
                if (!File.Exists(file)) continue;
                var relative = Path.GetRelativePath(root, file); var copy = SafeFiles.Child(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(file, copy);
                preserved.Add(new { path = relative, sha256 = await SafeFiles.HashAsync(copy, token) });
            }
            if (preserved.Count > 0) DistributionJson.Save(Path.Combine(backup, "manifest.json"), preserved);
            SafeFiles.ExtractZip(package, runtime, token);
        }
        var app = SafeFiles.Child(root, "app");
        if (Directory.Exists(app)) foreach (var crt in Directory.EnumerateFiles(app, "*140*.dll"))
        {
            var target = SafeFiles.Child(runtime, Path.GetFileName(crt));
            if (File.Exists(target) && (await SafeFiles.HashAsync(target, token)).Equals(await SafeFiles.HashAsync(crt, token), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(target))
            {
                var copy = SafeFiles.Child(root, @"backups\before-native-crt-" + Guid.NewGuid().ToString("N") + "\\" + Path.GetFileName(crt));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(target, copy);
            }
            File.Copy(crt, target, true);
        }
        var executable = SafeFiles.Child(runtime, "llama-server.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("El paquete no contiene el ejecutable esperado: " + executable);
        return executable;
    }
    private static async Task RefreshSelectedRuntimeAsync(string root, RuntimeProfile profile, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (!profile.NativeExecutable) return;
        var standard = SafeFiles.Child(root, profile.Kind == "strata-cpu" ? @"runtime\strata-cpu\strata-cpu.exe" : @"runtime\llama\llama-server.exe");
        if (!InstallationPaths.Resolve(root, profile.Executable).Equals(standard, StringComparison.OrdinalIgnoreCase))
        { progress?.Report(new("Conservando motor personalizado", "El runtime usa otra ruta; no se sobrescribe automáticamente: " + profile.Executable)); return; }
        if (profile.Kind == "strata-cpu") await InstallCpuRuntimeAsync(root, progress, token);
        else await InstallLlamaRuntimeAsync(root, profile.Devices != "none" || profile.GpuLayers > 0, progress, token);
    }
    internal static async Task<RuntimeProfile> RepairRuntimeAsync(string root, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var profile = RuntimeProfile.Load(root);
        if (!profile.NativeExecutable) throw new InvalidOperationException("La reparación local requiere un perfil nativo seleccionado. Usa el setup para configurar el modelo o reparar Strata.");
        var standard = SafeFiles.Child(root, profile.Kind == "strata-cpu" ? @"runtime\strata-cpu\strata-cpu.exe" : @"runtime\llama\llama-server.exe");
        if (!InstallationPaths.Resolve(root, profile.Executable).Equals(standard, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El motor seleccionado usa una ruta personalizada; no se modifica: " + profile.Executable + ". Restaura ese runtime o reinstala el perfil desde el setup.");
        await RefreshSelectedRuntimeAsync(root, profile, progress, token); return profile;
    }
    internal static int AvailablePort()
    {
        for (var port = 8088; port < 8108; port++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); return port; } catch (SocketException) { } finally { listener.Stop(); }
        }
        throw new IOException("No hay un puerto local libre entre 8088 y 8107.");
    }
    internal static async Task<string> RunAsync(string exe, IEnumerable<string> args, string directory, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); start.Environment["PYTHONUTF8"] = "1";
        using var process = new Process { StartInfo = start }; var output = new System.Text.StringBuilder(); var sync = new object();
        void Receive(object sender, DataReceivedEventArgs data)
        {
            if (data.Data is null) return;
            lock (sync) { if (output.Length > 200000) output.Remove(0, 100000); output.AppendLine(data.Data); }
            progress?.Report(new("Preparando motor", data.Data.Length > 400 ? data.Data[..400] : data.Data));
        }
        process.OutputDataReceived += Receive; process.ErrorDataReceived += Receive;
        if (!process.Start()) throw new IOException("No se pudo iniciar " + Path.GetFileName(exe));
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(token); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(exe)} terminó con código {process.ExitCode}.\n" + output.ToString()[Math.Max(0, output.Length - 2000)..]);
        return output.ToString();
    }
    internal static async Task<RuntimeProfile> InstallAsync(string root, string modelFolder, InstallPlan plan, SystemHardware hardware, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        root = Path.GetFullPath(root); modelFolder = Path.GetFullPath(modelFolder);
        var oldPaths = InstallationPaths.Load(root); var paths = new InstallationPaths { Models = modelFolder, VoicePython = oldPaths.VoicePython, ToolsPython = oldPaths.ToolsPython };
        var current = RuntimeProfile.Load(root); RuntimeProfile profile;
        if (plan.Kind == "none")
        {
            await RefreshSelectedRuntimeAsync(root, current, progress, token);
            return current;
        }
        var packages = SafeFiles.Child(root, @"runtime\packages");
        if (plan.Kind == "reuse")
        {
            if (current.Kind == "strata-dual" && File.Exists(Path.Combine(root, "state", "engine.json"))) return current;
            var original = plan.ReuseRoot ?? throw new InvalidOperationException("Falta la instalación para reutilizar.");
            var originalPaths = InstallationPaths.Load(original); paths = new()
            { Models = InstallationPaths.Resolve(original, originalPaths.Models), StrataPython = InstallationPaths.Resolve(original, originalPaths.StrataPython),
                VoicePython = InstallationPaths.Resolve(original, originalPaths.VoicePython), ToolsPython = originalPaths.ToolsPython.Length > 0 ? InstallationPaths.Resolve(original, originalPaths.ToolsPython) : "" };
            if (!string.Equals(root, Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new("Reutilizando Strata", "Copiando el motor propio; los pesos y la voz se conservan en sus rutas originales."));
                SafeFiles.CopyTree(Path.Combine(original, "runtime", "Strata"), Path.Combine(root, "runtime", "Strata"), token);
                var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(original, "state", "strata.json")))!;
                cfg["exe"] = Path.Combine(root, "runtime", "Strata", "engine", "strata-dual.exe"); cfg["cwd"] = Path.Combine(root, "runtime", "Strata");
                cfg["log"] = Path.Combine(root, "logs", "engine-native.log"); cfg["port"] = AvailablePort();
                var args = cfg["args"]!.AsArray();
                for (var i = 0; i + 1 < args.Count; i++) if (args[i]!.GetValue<string>() == "--expert-profile") args[i + 1] = Path.Combine(root, "runtime", "Strata", "data", "expert-profile.bin");
                cfg["env"]!["STRATA_RX580_STATUS_FILE"] = Path.Combine(root, "logs", "strata-rx580.json");
                DistributionJson.Save(Path.Combine(root, "state", "strata.json"), cfg);
                foreach (var file in new[] { "tts-config.json" }) if (File.Exists(Path.Combine(original, "state", file)) && !File.Exists(Path.Combine(root, "state", file))) File.Copy(Path.Combine(original, "state", file), Path.Combine(root, "state", file), false);
                profile = RuntimeProfile.Load(original); profile.Port = cfg["port"]!.GetValue<int>();
            }
            else profile = current;
        }
        else if (plan.Kind == "strata-cpu")
        {
            var model = plan.Model ?? throw new InvalidOperationException("Falta el modelo pequeño del catálogo.");
            if (!ModelCatalog.CpuSupported(model.Id) || ModelCatalog.Models.Single(m => m.Id == model.Id) != model)
                throw new InvalidOperationException("Modelo fuera del catálogo verificado Strata de 4 GB.");
            if (!hardware.X64 || hardware.RamGiB < 3.5) throw new PlatformNotSupportedException("Strata CPU necesita Windows x64 y al menos 4 GB de RAM.");
            var modelPath = SafeFiles.Child(modelFolder, Path.Combine(model.Id, model.File));
            using var downloader = new VerifiedDownloader(); await downloader.DownloadAsync(model.Url, modelPath, model.Size, model.Sha256, progress, token);
            var executable = await InstallCpuRuntimeAsync(root, progress, token);
            profile = new() { Kind = "strata-cpu", ModelId = model.Id, Label = plan.Label, Executable = Path.GetRelativePath(root, executable), ModelFile = modelPath,
                DeviceDescription = "CPU x64/SSE2 · compatible sin AVX · presupuesto 1536 MiB", Context = 4096, Threads = Math.Clamp(hardware.Threads, 1, 2),
                MemoryMiB = 1536, Port = current.Configured ? current.Port : AvailablePort() };
        }
        else if (plan.Kind == "llama")
        {
            var model = plan.Model ?? throw new InvalidOperationException("Falta el modelo del catálogo.");
            var modelPath = SafeFiles.Child(modelFolder, Path.Combine(model.Id, model.File));
            using var downloader = new VerifiedDownloader(); await downloader.DownloadAsync(model.Url, modelPath, model.Size, model.Sha256, progress, token);
            var runtime = SafeFiles.Child(root, @"runtime\llama"); Directory.CreateDirectory(runtime);
            var wantsGpu = hardware.Gpus.Any(g => !g.Software && g.Usable && (g.DedicatedBytes >= 1536L * 1048576 || !g.DxgiVisible && g.VendorId == 0x10de || g.Integrated && hardware.RamGiB >= 7.5 && g.SharedBytes >= 2L * 1073741824));
            var package = wantsGpu ? "llama-vulkan.zip" : "llama-cpu.zip";
            progress?.Report(new("Preparando motor", wantsGpu ? "Vulkan · verificando las gráficas reales" : "CPU · perfil para equipo sin GPU dedicada"));
            var exe = await InstallLlamaRuntimeAsync(root, wantsGpu, progress, token); var devices = "none"; var description = "CPU"; var layers = 0;

            using var probeLimit = CancellationTokenSource.CreateLinkedTokenSource(token); probeLimit.CancelAfter(TimeSpan.FromSeconds(25));
            if (wantsGpu)
            {
                try
                {
                    var probe = await RunAsync(exe, ["--list-devices"], runtime, progress, probeLimit.Token);
                    var verified = Regex.Matches(probe, @"(?m)^\s*(Vulkan\d+): (.+?) \((\d+) MiB, (\d+) MiB free\)").Cast<Match>()
                        .Select(m => new VerifiedGpu(m.Groups[1].Value, m.Groups[2].Value, long.Parse(m.Groups[4].Value) * 1048576, long.Parse(m.Groups[3].Value) * 1048576)).ToArray();
                    var gpu = GpuPlanner.Choose(hardware, verified, model.Size);
                    devices = gpu.Devices; description = gpu.Description; layers = gpu.Layers;
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or OperationCanceledException)
                { progress?.Report(new("GPU no disponible", "Se configurará CPU explícitamente. " + e.Message)); }
                if (devices == "none") { await InstallLlamaRuntimeAsync(root, false, progress, token); description = "CPU · Vulkan no verificado"; }
            }
            profile = new() { Kind = "llama", ModelId = model.Id, Label = plan.Label, Executable = Path.GetRelativePath(root, exe), ModelFile = modelPath,
                Context = plan.Context, Devices = devices, DeviceDescription = description, GpuLayers = layers, Threads = Math.Clamp(hardware.Threads / 2, 1, 16), Port = current.Configured ? current.Port : AvailablePort() };
            DistributionJson.Save(Path.Combine(modelFolder, model.Id, "model.json"), new { model, downloaded = DateTimeOffset.UtcNow, verifiedSha256 = model.Sha256 });
        }
        else if (plan.Kind == "strata")
        {
            if (!ModelCatalog.StrataHardware(hardware) || hardware.DiskFreeBytes < 100L * 1073741824) throw new InvalidOperationException("El equipo no cumple los requisitos de este perfil Strata.");
            var runtime = SafeFiles.Child(root, @"runtime\Strata"); var staging = SafeFiles.Child(root, @"temp\strata-source");
            SafeFiles.ExtractZip(Path.Combine(packages, "strata-source.zip"), staging, token);
            var source = Directory.GetDirectories(staging).Single(); SafeFiles.CopyTree(source, runtime, token);
            var pythonDir = SafeFiles.Child(root, @"runtime\python"); var python = Path.Combine(pythonDir, "python.exe");
            if (!File.Exists(python)) await RunAsync(Path.Combine(packages, "python-3.12.10-amd64.exe"),
                ["/quiet", "InstallAllUsers=0", "TargetDir=" + pythonDir, "PrependPath=0", "Include_launcher=0", "Include_test=0", "Include_doc=0", "Shortcuts=0"], root, progress, token);
            if (!File.Exists(python)) throw new IOException("No se instaló el Python privado.");
            var venv = Path.Combine(runtime, ".venv"); var venvPython = Path.Combine(venv, "Scripts", "python.exe");
            if (!File.Exists(venvPython)) await RunAsync(python, ["-m", "venv", venv], root, progress, token);
            await RunAsync(venvPython, ["-m", "pip", "install", "-r", Path.Combine(runtime, "requirements.txt")], runtime, progress, token);
            var nvidia = hardware.Gpus.Where(g => !g.Software && g.VendorId == 0x10de && g.Name.Contains("RTX") && g.DedicatedBytes >= 11L * 1073741824).ToArray();
            var port = current.Configured ? current.Port : AvailablePort();
            var arguments = new List<string> { Path.Combine(runtime, "setup.py"), "--yes", "--family", "qwen", "--model", "IQ2_XS", "--context", "8192", "--vision", "no", "--data-dir", modelFolder, "--port", port.ToString(), "--no-start", "--backend", nvidia.Length > 0 ? "cuda" : "hip" };
            if (nvidia.Length > 1) { arguments.Add("--gpus"); arguments.Add("all"); }
            await RunAsync(venvPython, arguments, runtime, progress, token);
            var config = Directory.EnumerateFiles(runtime, "strata-*.json").FirstOrDefault(p => p.Contains("iq2_xs", StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("Strata no creó su configuración IQ2_XS.");
            var cfg = JsonNode.Parse(File.ReadAllText(config))!; cfg["port"] = port; cfg["open_browser"] = false;
            DistributionJson.Save(Path.Combine(root, "state", "strata.json"), cfg); paths.StrataPython = Path.GetRelativePath(root, venvPython); paths.ToolsPython = paths.StrataPython;
            profile = new() { Kind = "strata", ModelId = "qwen3.8-flash-next-iq2_xs", Label = plan.Label, DeviceDescription = nvidia.Length > 0 ? "NVIDIA / CUDA" : "AMD / HIP", Port = port };
        }
        else throw new InvalidOperationException("Perfil no registrado.");
        token.ThrowIfCancellationRequested();
        var profilePath = Path.Combine(root, "state", "engine.json");
        if (File.Exists(profilePath)) File.Copy(profilePath, profilePath + ".previous", true);
        if (File.Exists(Path.Combine(root, "state", "paths.json"))) File.Copy(Path.Combine(root, "state", "paths.json"), Path.Combine(root, "state", "paths.json.previous"), true);
        DistributionJson.Save(Path.Combine(root, "state", "paths.json"), paths); DistributionJson.Save(profilePath, profile);
        DistributionJson.Save(Path.Combine(root, "state", "hardware.json"), new { hardware, plan, installed = DateTimeOffset.UtcNow, profile });
        progress?.Report(new("Modelo instalado", profile.Label, 1)); return profile;
    }
    internal static async Task InstallDictationAsync(string root, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        using var stream = typeof(ModelProvisioner).Assembly.GetManifestResourceStream("SheepCode.dictation-model.json")!;
        var model = await JsonSerializer.DeserializeAsync<ModelAsset>(stream, DistributionJson.Options, token) ?? throw new IOException("Falta el catálogo de dictado.");
        var models = InstallationPaths.Resolve(root, InstallationPaths.Load(root).Models); var file = SafeFiles.Child(models, model.File);
        using var downloader = new VerifiedDownloader(); await downloader.DownloadAsync(model.Url, file, model.Size, model.Sha256, progress, token);
    }
}
