using System.Text;
using System.Text.Json;
using SheepCode.Distribution;

namespace SheepCode;

internal static class ModelDiagnostics
{
    internal static async Task<int> RunAsync()
    {
        var originalRoot = AppPaths.Root; var originalHome = Environment.GetEnvironmentVariable("SHEEPCODE_HOME");
        var root = SafeFiles.Child(originalRoot, @"checks\model-fixture-" + Guid.NewGuid().ToString("N"));
        var rows = new List<object>();
        void Check(bool value, string name) { if (!value) throw new IOException(name); rows.Add(new { check = name, passed = true }); }
        try
        {
            Environment.SetEnvironmentVariable("SHEEPCODE_HOME", root); AppPaths.Initialize();
            var unset = new RuntimeProfile();
            Check(!ModelInstallationInspector.Inspect(root, unset).FilesReady, "fresh-device-has-no-selected-engine");
            var catalog = ModelInstallationInspector.Catalog(root, unset);
            Check(catalog.All(m => !m.Present && !m.Selected) && catalog.Single(m => m.Id == "qwen3-32b").State.StartsWith("catálogo;"), "catalog-does-not-claim-models-are-installed");
            var asset = ModelCatalog.Models.First(); var path = SafeFiles.Child(root, Path.Combine("models", asset.Id, asset.File));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path + ".part", "synthetic partial download");
            Check(ModelInstallationInspector.Catalog(root, unset).Single(m => m.Id == asset.Id).Partial, "partial-download-is-distinct-from-installed-weights");
            File.WriteAllText(path, "synthetic wrong-size fixture");
            Check(ModelInstallationInspector.Catalog(root, unset).Single(m => m.Id == asset.Id).State.Contains("tamaño distinto"), "incorrect-size-is-not-reported-as-an-installed-model");
            var profile = new RuntimeProfile { Kind = "llama", ModelId = "fixture-without-ai", Label = "Fixture de rutas · sin IA", Executable = @"custom-runtime\fixture.exe", ModelFile = @"models\fixture\weights.bin", Context = 4096, Threads = 1 };
            DistributionJson.Save(Path.Combine(AppPaths.State, "engine.json"), profile);
            var missing = ModelInstallationInspector.Inspect(root, profile);
            Check(!missing.FilesReady && missing.Message.Contains(Path.Combine(root, "custom-runtime")), "missing-runtime-reports-the-exact-directory");
            var prefs = new Preferences(); await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
            var agent = new AgentController(engine, voice, prefs);
            try { await engine.EnsureAsync(CancellationToken.None); throw new IOException("missing runtime launched a process"); }
            catch (FileNotFoundException e) { Check(e.Message.Contains(Path.Combine(root, "custom-runtime")) && !engine.Ready && engine.LastLoadError == e.Message, "failed-startup-preserves-the-exact-cause-without-inference"); }
            using (var status = JsonDocument.Parse(await agent.SubmitAsync("Diagnostica el modelo", CancellationToken.None)))
                Check(status.RootElement.GetProperty("installation").GetProperty("state").GetString() == "incomplete" && status.RootElement.GetProperty("engine").GetProperty("lastLoadError").GetString() == engine.LastLoadError,
                    "shared-text-and-accepted-dictation-report-real-file-status");
            Directory.CreateDirectory(SafeFiles.Child(root, "custom-runtime")); File.WriteAllText(SafeFiles.Child(root, profile.Executable), "synthetic executable; never run");
            var weights = SafeFiles.Child(root, profile.ModelFile); Directory.CreateDirectory(Path.GetDirectoryName(weights)!); File.WriteAllText(weights, "synthetic weights; never run");
            Check(ModelInstallationInspector.Inspect(root, profile).FilesReady && !engine.Ready, "file-presence-is-distinct-from-a-loaded-model");
            var absentProject = SafeFiles.Child(root, "missing-project");
            try { _ = new ProjectWorkspace(absentProject); throw new IOException("absent project accepted"); }
            catch (DirectoryNotFoundException e) { Check(e.Message.Contains(absentProject) && e.Message.Contains("distinta de los pesos"), "project-folder-errors-are-distinct-from-model-files"); }
            var registry = await agent.SubmitAsync("¿Qué puedes hacer?", CancellationToken.None);
            Check(registry.Contains("Diagnostica el modelo") && registry.Contains("Repara el motor"), "model-diagnosis-and-repair-are-registered-capabilities");
            Check(await agent.ControlAsync("Lee literalmente: Repara el motor", CancellationToken.None) is null, "quoted-repair-text-is-not-authorization");
            agent.Skills.SetEnabled("models", false);
            try { await agent.ControlAsync("Repara el motor", CancellationToken.None); throw new IOException("disabled model skill allowed repair"); }
            catch (InvalidOperationException e) when (e.Message.Contains("models", StringComparison.OrdinalIgnoreCase)) { Check(true, "disabled-model-skill-blocks-human-repair"); }
            agent.Skills.SetEnabled("models", true);
            var configFile = Path.Combine(AppPaths.State, "engine.json"); var customBefore = File.ReadAllBytes(configFile);
            try { await agent.ControlAsync("Repara el motor", CancellationToken.None); throw new IOException("custom runtime overwritten"); }
            catch (InvalidOperationException e) when (e.Message.Contains("personalizada"))
            { Check(customBefore.SequenceEqual(File.ReadAllBytes(configFile)), "custom-runtime-is-preserved-and-repair-is-explicitly-refused"); }
            Directory.CreateDirectory(SafeFiles.Child(root, @"runtime\packages")); Directory.CreateDirectory(SafeFiles.Child(root, "app"));
            foreach (var name in new[] { "llama-cpu.zip", "llama-vulkan.zip" })
                File.Copy(SafeFiles.Child(originalRoot, Path.Combine("runtime", "packages", name)), SafeFiles.Child(root, Path.Combine("runtime", "packages", name)));
            foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                File.Copy(SafeFiles.Child(originalRoot, Path.Combine("app", name)), SafeFiles.Child(root, Path.Combine("app", name)));
            profile.Executable = @"runtime\llama\llama-server.exe"; DistributionJson.Save(configFile, profile);
            var configBefore = File.ReadAllBytes(configFile); var prefsBefore = File.ReadAllBytes(Preferences.PathName); var weightHash = await SafeFiles.HashAsync(weights, CancellationToken.None);
            var repaired = await agent.SubmitAsync("Repara el motor", CancellationToken.None);
            Check(repaired.Contains("No se han descargado pesos") && ModelInstallationInspector.Inspect(root, profile).FilesReady && !engine.Ready,
                "human-repair-restores-the-native-cpu-runtime-without-starting-ai");
            Check(configBefore.SequenceEqual(File.ReadAllBytes(configFile)) && prefsBefore.SequenceEqual(File.ReadAllBytes(Preferences.PathName)) && weightHash == await SafeFiles.HashAsync(weights, CancellationToken.None),
                "repair-preserves-selected-profile-permissions-and-weight-bytes");
            var crt = SafeFiles.Child(root, @"runtime\llama\vcruntime140.dll"); var displacedCrt = SafeFiles.Child(root, @"temp\displaced-vcruntime140.dll"); File.Move(crt, displacedCrt);
            await ModelProvisioner.RepairRuntimeAsync(root, null, CancellationToken.None);
            Check(File.Exists(crt), "repair-restores-a-missing-crt-even-when-the-archive-files-exist");
            var dll = SafeFiles.Child(root, @"runtime\llama\ggml-base.dll"); File.WriteAllText(dll, "synthetic truncated DLL");
            await ModelProvisioner.RepairRuntimeAsync(root, null, CancellationToken.None);
            Check(new FileInfo(dll).Length > 1000 && Directory.EnumerateFiles(SafeFiles.Child(root, "backups"), "manifest.json", SearchOption.AllDirectories).Any(),
                "incomplete-native-libraries-are-restored-with-a-backup");
            var runtime = SafeFiles.Child(root, @"runtime\llama"); var displacedRuntime = SafeFiles.Child(root, @"temp\displaced-llama-runtime"); Directory.Move(runtime, displacedRuntime);
            var hardware = new SystemHardware("deterministic fixture", true, 2, "fixture CPU", false, 4L * 1073741824, 2L * 1073741824, 20L * 1073741824, "C:\\", []);
            await ModelProvisioner.InstallAsync(root, Path.Combine(root, "models"), ModelCatalog.EditorOnly(), hardware, null, CancellationToken.None);
            Check(ModelInstallationInspector.Inspect(root, profile).FilesReady && configBefore.SequenceEqual(File.ReadAllBytes(configFile)), "setup-upgrade-repairs-a-missing-selected-llama-folder");
            var package = SafeFiles.Child(root, @"runtime\packages\llama-cpu.zip"); var validPackage = SafeFiles.Child(root, @"runtime\packages\llama-cpu.valid.zip"); File.Move(package, validPackage); File.WriteAllText(package, "synthetic corrupt archive");
            var runtimeBefore = await SafeFiles.HashAsync(dll, CancellationToken.None);
            try { await ModelProvisioner.RepairRuntimeAsync(root, null, CancellationToken.None); throw new IOException("corrupt local runtime package accepted"); }
            catch (InvalidDataException) { Check(runtimeBefore == await SafeFiles.HashAsync(dll, CancellationToken.None) && configBefore.SequenceEqual(File.ReadAllBytes(configFile)), "invalid-runtime-sha256-blocks-repair-before-any-replacement"); }
            File.Move(package, SafeFiles.Child(root, @"temp\corrupt-archive-fixture.bin")); File.Move(validPackage, package);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await ModelProvisioner.RepairRuntimeAsync(root, null, cancelled.Token); throw new IOException("cancelled repair continued"); }
            catch (OperationCanceledException) { Check(configBefore.SequenceEqual(File.ReadAllBytes(configFile)), "cancelled-repair-keeps-the-profile"); }
            profile.Devices = "Vulkan0"; profile.GpuLayers = 5; DistributionJson.Save(configFile, profile); var vulkanProfile = File.ReadAllBytes(configFile);
            await ModelProvisioner.RepairRuntimeAsync(root, null, CancellationToken.None);
            Check(File.Exists(SafeFiles.Child(root, @"runtime\llama\ggml-vulkan.dll")) && vulkanProfile.SequenceEqual(File.ReadAllBytes(configFile)) && !engine.Ready,
                "selected-vulkan-runtime-is-restored-without-changing-devices-or-running-ai");
            AppPaths.SaveJson(Path.Combine(originalRoot, "checks", "model-installation.json"), new { status = "complete", passed = rows.Count, rows,
                scope = "Filesystem, real shipped archives and human-control fixtures. No model inference, network downloads or performance measurements; synthetic weight files cannot run.", fixture = root });
            return 0;
        }
        catch (Exception e) { AppPaths.SaveJson(Path.Combine(originalRoot, "checks", "model-installation.json"), new { status = "failed", rows, error = e.ToString(), fixture = root }); return 1; }
        finally { Environment.SetEnvironmentVariable("SHEEPCODE_HOME", originalHome); }
    }
    internal static async Task GuiAsync(MainForm form, AgentController agent, EngineHost engine)
    {
        var rows = new List<object>();
        try
        {
            if (engine.Profile.Configured) throw new InvalidOperationException("La prueba GUI requiere una instalación aislada sin IA seleccionada.");
            try { await agent.SubmitAsync("Activa el motor", CancellationToken.None); throw new IOException("An unconfigured GUI started an engine"); }
            catch (FileNotFoundException) { }
            foreach (var size in new[] { new Size(1024, 650), new Size(1520, 900) })
            {
                form.Size = size; form.ShowModelsPanel(); form.RefreshModelPanel(); await Task.Delay(120);
                var text = form.ModelPanelText();
                if (!text.StartsWith("🧠 Modelo seleccionado:") || !text.Contains("Último error de carga") || text.Contains("qwen3-32b") || !form.ModelSummaryVisible()) throw new IOException("El panel repite el catálogo o no muestra el fallo.");
                var file = Path.Combine(AppPaths.Root, "checks", "model-panel-" + size.Width + ".png"); form.CaptureWindow(file);
                rows.Add(new { width = size.Width, passed = true, screenshot = file });
            }
            AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "model-panel.json"), new { status = "complete", rows, scope = "Real GUI of an isolated editor-only install; no model or audio inference." });
        }
        catch (Exception e) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "model-panel.json"), new { status = "failed", rows, error = e.ToString() }); }
        finally { form.Close(); }
    }
}