using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;

namespace SheepCode.Distribution;

internal static class ModelProvisioner
{
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
        if (plan.Kind == "none") return current;
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
        else if (plan.Kind == "llama")
        {
            var model = plan.Model ?? throw new InvalidOperationException("Falta el modelo del catálogo.");
            var modelPath = SafeFiles.Child(modelFolder, Path.Combine(model.Id, model.File));
            using var downloader = new VerifiedDownloader(); await downloader.DownloadAsync(model.Url, modelPath, model.Size, model.Sha256, progress, token);
            var runtime = SafeFiles.Child(root, @"runtime\llama"); Directory.CreateDirectory(runtime);
            var wantsGpu = hardware.Gpus.Any(g => !g.Software && g.Usable && (g.DedicatedBytes >= 1536L * 1048576 || !g.DxgiVisible && g.VendorId == 0x10de || g.Integrated && hardware.RamGiB >= 7.5 && g.SharedBytes >= 2L * 1073741824));
            var package = wantsGpu ? "llama-vulkan.zip" : "llama-cpu.zip";
            progress?.Report(new("Preparando motor", wantsGpu ? "Vulkan · verificando las gráficas reales" : "CPU · perfil para equipo sin GPU dedicada"));
            SafeFiles.ExtractZip(Path.Combine(packages, package), runtime, token);
            foreach (var crt in Directory.EnumerateFiles(Path.Combine(root, "app"), "*140*.dll")) File.Copy(crt, Path.Combine(runtime, Path.GetFileName(crt)), true);
            var exe = Path.Combine(runtime, "llama-server.exe"); var devices = "none"; var description = "CPU"; var layers = 0;
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
                if (devices == "none") { SafeFiles.ExtractZip(Path.Combine(packages, "llama-cpu.zip"), runtime, token); description = "CPU · Vulkan no verificado"; }
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
