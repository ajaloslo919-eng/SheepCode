using System.Text;
using System.Text.Json;

namespace SheepCode;

internal static class Diagnostics
{
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { return; }
        throw new InvalidOperationException(message);
    }
    private static Preferences Clone(Preferences preferences) => JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences, AppPaths.Json), AppPaths.Json)!;
    internal static async Task<int> SelfTestAsync()
    {
        var rows = new List<object>(); var prefs = Preferences.Load(); var saved = Clone(prefs);
        var savedBytes = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
        var agent = new AgentController(engine, voice, prefs);
        try
        {
            var root = Path.Combine(AppPaths.Root, "checks", "self-test-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
            var before = "def add(a, b):\r\n    return a - b\r\n";
            File.WriteAllText(Path.Combine(root, "calculator.py"), before, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(root, "test_calculator.py"), "import unittest\nfrom calculator import add\nclass SumTest(unittest.TestCase):\n    def test_sum(self):\n        self.assertEqual(add(8, 3), 11)\n");
            File.WriteAllText(Path.Combine(root, ".env"), "SAMPLE_TEST_SECRET=synthetic\n");
            Directory.CreateDirectory(Path.Combine(root, "node_modules")); File.WriteAllText(Path.Combine(root, "node_modules", "hidden.js"), "synthetic");
            agent.OpenProject(root); var workspace = agent.Workspace!;
            Reject(() => workspace.Resolve("../outside.txt"), "Se permitió salir del proyecto.");
            Reject(() => workspace.Resolve(@"C:\outside.txt"), "Se permitió una ruta absoluta.");
            Reject(() => workspace.Read(".env"), "Se permitió leer un secreto.");
            Assert(!workspace.Files().Any(p => p.Contains(".env") || p.Contains("node_modules")), "Se mostraron rutas excluidas.");
            rows.Add(new { check = "project-boundaries-secrets-and-ignored-folders", passed = true });
            var read = await agent.SubmitAsync("Lee el archivo calculator.py", CancellationToken.None);
            Assert(read == before, "La herramienta no entregó el contenido real.");
            rows.Add(new { check = "shared-text-and-accepted-voice-input-file-read", passed = true });
            var originalBytes = File.ReadAllBytes(Path.Combine(root, "calculator.py"));
            var change = agent.Changes!.Propose("calculator.py", "def add(a, b):\n    return a + b\n", "Comprobación determinista de la revisión de cambios.");
            Assert(originalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "calculator.py"))), "La propuesta escribió el proyecto antes de aprobarla.");
            Assert(change.Diff().Contains("+    return a + b"), "El diff no muestra el cambio.");
            await agent.SubmitAsync("Aplica el cambio " + change.Id, CancellationToken.None);
            var fixedFile = workspace.Read("calculator.py"); Assert(fixedFile.Bom && fixedFile.Newline == "\r\n" && fixedFile.Text.Contains("return a + b"), "No se conservó la codificación o el contenido.");
            rows.Add(new { check = "proposal-review-apply-with-bom-and-crlf", passed = true });
            prefs.AllowChecks = false;
            try { await new ChecksRunner(workspace).RunAsync("python-test", false, null, CancellationToken.None); throw new Exception("Se ejecutaron comprobaciones desactivadas."); }
            catch (InvalidOperationException) { }
            await agent.SubmitAsync("Activa las comprobaciones", CancellationToken.None);
            var result = await new ChecksRunner(workspace).RunAsync("python-test", prefs.AllowChecks, null, CancellationToken.None);
            Assert(result.ExitCode == 0 && result.Output.Contains("test_sum"), "La comprobación instalada no pasó.");
            rows.Add(new { check = "checks-permission-and-real-python-unittest", passed = true, exitCode = result.ExitCode });
            await agent.SubmitAsync("Deshaz el cambio " + change.Id, CancellationToken.None);
            Assert(originalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "calculator.py"))), "Deshacer no restauró los bytes originales.");
            rows.Add(new { check = "undo-preserves-original-file", passed = true });
            var concurrent = agent.Changes.Propose("calculator.py", "def add(a, b):\n    return a + b\n", "Prueba de edición concurrente.");
            File.WriteAllText(Path.Combine(root, "calculator.py"), "# edición posterior del usuario\n");
            Reject(() => agent.Changes.Apply(concurrent.Id), "Se sobrescribió una edición posterior.");
            Assert(workspace.Read("calculator.py").Text.Contains("edición posterior"), "Se perdió la edición posterior.");
            rows.Add(new { check = "concurrent-edits-protected", passed = true });
            await agent.SubmitAsync("Desactiva las comprobaciones", CancellationToken.None);
            await agent.SubmitAsync("Activa la voz", CancellationToken.None);
            Assert(prefs.ReadAloud && !prefs.AllowChecks, "No se verificó la activación o desactivación.");
            await agent.SubmitAsync("Desactiva la voz", CancellationToken.None);
            await agent.SubmitAsync("Ajusta el razonamiento a bajo", CancellationToken.None);
            Assert(!prefs.ReadAloud && prefs.Reasoning == "low", "No se configuró el diálogo.");
            await agent.SubmitAsync("Desactiva el razonamiento", CancellationToken.None);
            Assert(prefs.Reasoning == "none", "No se desactivó el razonamiento.");
            Assert(await agent.ControlAsync("No actives la voz", CancellationToken.None) is null, "No se respetó una negación.");
            Assert(await agent.ControlAsync("Lee literalmente: Activa las comprobaciones", CancellationToken.None) is null, "Se interpretó un texto literal como permiso.");
            rows.Add(new { check = "capabilities-configuration-deactivation-and-command-guards", passed = true });
            await agent.SubmitAsync("Activa la skill models", CancellationToken.None);
            using (var system = JsonDocument.Parse(await agent.SubmitAsync("Analiza el sistema", CancellationToken.None)))
            {
                Assert(system.RootElement.GetProperty("hardware").GetProperty("ramBytes").GetInt64() > 0, "El diálogo no entregó la memoria física real.");
                Assert(system.RootElement.TryGetProperty("recommendation", out _) || system.RootElement.TryGetProperty("unavailable", out _), "El análisis no entregó la recomendación o su límite concreto.");
            }
            await agent.SubmitAsync("Desactiva la skill models", CancellationToken.None);
            try { await agent.ControlAsync("Analiza el sistema", CancellationToken.None); throw new Exception("El análisis siguió activo con la skill desactivada."); }
            catch (InvalidOperationException) { }
            await agent.SubmitAsync("Activa la skill models", CancellationToken.None);
            Assert(await agent.ControlAsync("No instales el modelo recomendado", CancellationToken.None) is null, "Una negación inició una instalación.");
            Assert(await agent.ControlAsync("Lee literalmente: Instala el modelo recomendado", CancellationToken.None) is null, "Un texto literal inició una instalación.");
            using (var model = JsonDocument.Parse(await agent.SubmitAsync("Estado del modelo", CancellationToken.None)))
                Assert(model.RootElement.GetProperty("profile").GetString() == engine.Profile.Kind, "El diálogo no refleja el perfil real de esta instalación.");
            rows.Add(new { check = "hardware-analysis-model-state-skill-toggle-and-install-command-guards", passed = true });
            var registry = await agent.SubmitAsync("¿Qué puedes hacer?", CancellationToken.None);
            Assert(registry.Contains("changes") && registry.Contains("voice") && registry.Contains("checks") && registry.Contains("system_info"), "El diálogo no conoce las capacidades.");
            rows.Add(new { check = "integrated-capabilities-in-shared-dialogue", passed = true });
            AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "self-test.json"), new { status = "complete", passed = rows.Count, rows,
                installedExe = Environment.ProcessPath, scope = "Herramientas deterministas de la aplicación instalada; no ejecuta modelos ni mide rendimiento de IA." });
            return 0;
        }
        catch (Exception e) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "self-test.json"), new { status = "failed", rows, error = e.ToString() }); return 1; }
        finally { if (savedBytes is not null) File.WriteAllBytes(Preferences.PathName, savedBytes); else saved.Save(); }
    }
    internal static async Task<int> AgentCheckAsync()
    {
        var prefs = Preferences.Load(); var saved = Clone(prefs);
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
        using var input = new VoiceInput();
        var agent = new AgentController(engine, voice, prefs);
        var report = new Dictionary<string, object?> { ["status"] = "preparing", ["started"] = DateTimeOffset.UtcNow,
            ["installedExe"] = Environment.ProcessPath, ["scope"] = "Agente de código derivado de SheepGPT: lectura, propuesta, aplicación, prueba y voz reales del programa instalado. Sin comparativa de modelos; ASR se prueba con un WAV, no con captura humana del micrófono." };
        var reportPath = Path.Combine(AppPaths.Root, "checks", "agent-result.json");
        void Save() => AppPaths.SaveJson(reportPath, report);
        Save();
        try
        {
            var root = Path.Combine(AppPaths.Root, "checks", "agent-project-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "calculator.py"), "def add(a, b):\n    return a - b\n");
            File.WriteAllText(Path.Combine(root, "test_calculator.py"), "import unittest\nfrom calculator import add\nclass SumTest(unittest.TestCase):\n    def test_sum(self):\n        self.assertEqual(add(8, 3), 11)\n        self.assertEqual(add(-2, 5), 3)\n");
            prefs.ReadAloud = false; prefs.AllowChecks = true; prefs.Reasoning = "none"; agent.OpenProject(root);
            agent.Output += (role, text) => File.AppendAllText(Path.Combine(AppPaths.Logs, "agent-check.log"), role + ": " + text + "\n");
            engine.Progress += s => { report["phase"] = s; Save(); };
            report["project"] = root; report["status"] = "running-agent"; Save();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            var reply = await agent.SubmitAsync("Corrige calculator.py: add(a, b) debe sumar, pero ahora resta. Lee el archivo y prepara una sustitución mínima. Termina para que revise el cambio; no lo apliques ni ejecutes pruebas todavía.", timeout.Token);
            var change = agent.Changes!.Items.LastOrDefault(c => c.Status == "pending" && c.Path == "calculator.py") ?? throw new InvalidOperationException("El agente no propuso la corrección del archivo.");
            Assert(agent.LastActions.Contains("read_file") && agent.LastActions.Any(x => x is "edit_file" or "write_file"), "El agente no usó las herramientas de lectura y cambio.");
            Assert(agent.Workspace!.Read("calculator.py").Text.Contains("return a - b"), "El agente modificó el archivo antes de aprobarlo.");
            report["agentReply"] = reply; report["agentActions"] = agent.LastActions.ToArray(); report["proposal"] = change;
            report["phase"] = "apply-and-check"; Save();
            // Explicit approval on this owned fixture uses the same direct-input tool as the GUI and accepted voice.
            await agent.SubmitAsync("Aplica el cambio " + change.Id, timeout.Token);
            var check = await new ChecksRunner(agent.Workspace).RunAsync("python-test", prefs.AllowChecks, null, timeout.Token);
            Assert(check.ExitCode == 0 && check.Output.Contains("OK"), "La corrección generada no pasó las pruebas reales.");
            report["check"] = check; report["engine"] = engine.Snapshot();
            var peer = engine.Peer() ?? throw new InvalidOperationException("No hay prueba de la RX 580.");
            Assert(peer.GetProperty("parityPassed").GetBoolean() && peer.GetProperty("expertTokenProjections").GetInt64() > 0, "No se verificó cálculo real en la RX 580.");
            report["phase"] = "voice-and-shared-control"; Save();
            var audio = await voice.SpeakAsync("Desactiva las comprobaciones.", "calmness", timeout.Token);
            var transcript = await input.TranscribeAsync(audio, timeout.Token);
            await agent.SubmitAsync(transcript, timeout.Token);
            Assert(!prefs.AllowChecks, "La transcripción no activó el control real de comprobaciones.");
            report["voice"] = voice.Snapshot(); report["voiceTranscript"] = transcript; report["asrBackend"] = input.Backend;
            report["audioPlayed"] = audio; report["checksDisabledByAcceptedVoice"] = !prefs.AllowChecks; Save();
            using var interruption = new CancellationTokenSource();
            var speaking = voice.SpeakAsync("Estoy preparando otra respuesta para comprobar que puedes interrumpirme y seguir trabajando con tu proyecto.", "amusement", interruption.Token);
            await Task.Delay(700); interruption.Cancel(); voice.Interrupt();
            try { await speaking; throw new InvalidOperationException("La voz no aceptó la interrupción."); } catch (OperationCanceledException) { }
            Assert(voice.ReadyPacket is not null, "La interrupción perdió el backend de voz.");
            report["interruption"] = "passed";
            await agent.SubmitAsync("Desactiva Strata", CancellationToken.None);
            Assert(!engine.Ready && engine.Peer() is null, "No se desactivó el motor propio.");
            report["engineDeactivation"] = "passed"; report["status"] = "complete"; report["completed"] = DateTimeOffset.UtcNow; Save(); return 0;
        }
        catch (Exception e) { report["status"] = "failed"; report["error"] = e.ToString(); Save(); return 1; }
        finally { saved.Save(); }
    }
}
