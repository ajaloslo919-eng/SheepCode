namespace SheepCode;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--cancellation-helper")) { Thread.Sleep(60000); return 0; }
        if (args.Contains("--mcp-fixture")) return CatalogDiagnostics.McpFixture();
        if (args.Length == 2 && args[0] == "--write-brand-icon") { MascotBanner.SaveBrandIcon(args[1]); return 0; }
        if (args.Contains("--pc-helper"))
        {
            var result = 1; var thread = new Thread(() => result = PcHelper.Run()); thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join(); return result;
        }
        if (args.Contains("--pc-fixture")) { ApplicationConfiguration.Initialize(); Application.Run(SkillDiagnostics.CreatePcFixture()); return 0; }
        AppPaths.Initialize();
        if (args.Length > 0 && args[0] == "--apply-update") return UpdateManager.RunHandoff(args);
        if (args.Contains("--catalog-check")) return CatalogDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--update-check"))
        {
            try { using var updater = new UpdateManager(); var message = updater.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "updater-live.json"), new { status = "complete", message, state = updater.Status(), scope = "Consulta HTTP real de la última release pública de SheepCode; sin instalar ni ejecutar IA." }); return 0; }
            catch (Exception e) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "updater-live.json"), new { status = "failed", error = e.Message }); return 1; }
        }
        if (args.Contains("--self-test")) return Diagnostics.SelfTestAsync().GetAwaiter().GetResult();
        if (args.Contains("--protocol-check")) return ProtocolDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--workflow-check")) { ApplicationConfiguration.Initialize(); return WorkflowDiagnostics.Run(); }
        if (args.Contains("--laptop-check")) return PortableDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--cpu-check")) return CpuDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--speed-workflow-check")) return FastCpuDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--creation-check")) return CreationDiagnostics.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--preflight-agent-check")) return CreationDiagnostics.RunAsync(true).GetAwaiter().GetResult();
        if (args.Contains("--agent-check")) return Diagnostics.AgentCheckAsync().GetAwaiter().GetResult();
        using var single = new Mutex(true, "Local\\SheepCode.GUI." + AppPaths.HashText(AppPaths.Root.ToUpperInvariant())[..12], out var first);
        if (!first) { MessageBox.Show("SheepCode ya está abierto.", "SheepCode"); return 0; }
        ApplicationConfiguration.Initialize();
        var preferences = Preferences.Load();
        var savedPreferences = System.Text.Json.JsonSerializer.Serialize(preferences, AppPaths.Json);
        var savedPreferenceBytes = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        var voice = new NeuralVoice(); var engine = new EngineHost(voice); var agent = new AgentController(engine, voice, preferences);
        using var form = new MainForm(preferences, voice, engine, agent);
        if (args.Contains("--integrations-ui-check"))
        {
            form.Shown += async (_, _) =>
            {
                try { await Task.Delay(120); var state = form.CheckIntegrationsGui(); form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", "skills-catalog-gui.png")); AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "integrations-gui.json"), new { status = "complete", state, scope = "GUI real con catálogo, filtro y diálogo de actualizador; sin consulta externa ni IA." }); }
                catch (Exception e) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "integrations-gui.json"), new { status = "failed", error = e.ToString() }); }
                finally { form.Close(); }
            };
        }
        if (args.Contains("--laptop-ui-check"))
        {
            form.Shown += async (_, _) =>
            {
                var rows = new List<object>();
                try
                {
                    foreach (var size in new[] { new Size(1366, 728), new Size(1024, 650), new Size(800, 600), new Size(1520, 900) })
                    {
                        form.Size = size; await Task.Delay(150); var state = form.ResponsiveSnapshot();
                        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(state));
                        if (!json.RootElement.GetProperty("inputVisible").GetBoolean() || !json.RootElement.GetProperty("sendVisible").GetBoolean()) throw new IOException("El compositor quedó fuera de la ventana " + size);
                        form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", $"laptop-{size.Width}.png")); rows.Add(state);
                        if (size.Width == 1024) { form.ShowModelsPanel(); await Task.Delay(80); form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", "laptop-models-1024.png")); form.ShowSkillPanel(); }
                    }
                    AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "laptop-gui.json"), new { status = "complete", rows, scope = "Ventanas reales de la GUI instalada, sin inferencia ni cambiar la resolución del escritorio." });
                }
                catch (Exception e) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "laptop-gui.json"), new { status = "failed", rows, error = e.ToString() }); }
                finally { form.Close(); }
            };
        }
        if (args.Contains("--skills-check") || args.Contains("--skills-agent-check"))
        {
            form.Shown += async (_, _) =>
            {
                await SkillDiagnostics.RunAsync(form, agent, preferences, engine, voice, args.Contains("--skills-agent-check"));
                form.Close();
            };
        }
        var projectArgument = Array.IndexOf(args, "--project");
        if (projectArgument >= 0 && projectArgument + 1 < args.Length)
            try { agent.OpenProject(args[projectArgument + 1]); }
            catch (Exception e) { MessageBox.Show(e.Message, "SheepCode · Proyecto"); }
        if (args.Contains("--ui-check") || args.Contains("--ui-agent-check"))
        {
            form.Shown += async (_, _) =>
            {
                try
                {
                    AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "gui-result.json"), new { status = "running", started = DateTimeOffset.UtcNow, installedExe = Environment.ProcessPath });
                    var root = Path.Combine(AppPaths.Root, "checks", "ui-project"); Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "calculator.py"), "def add(a, b):\n    return a - b\n");
                    File.WriteAllText(Path.Combine(root, "README.md"), "# Proyecto de prueba de la interfaz\nUna función de suma para verificar el editor y los cambios.\n");
                    agent.OpenProject(root); form.OpenFile("calculator.py");
                    var read = await agent.SubmitAsync("Lee el archivo calculator.py", CancellationToken.None);
                    if (!read.Contains("return a - b")) throw new InvalidOperationException("El diálogo no leyó el archivo real.");
                    string[]? agentActions = null;
                    if (args.Contains("--ui-agent-check"))
                    {
                        File.WriteAllText(Path.Combine(root, "test_calculator.py"), "import unittest\nfrom calculator import add\nclass SumTest(unittest.TestCase):\n    def test_sum(self):\n        self.assertEqual(add(8, 3), 11)\n");
                        agent.OpenProject(root); form.OpenFile("calculator.py");
                        await form.SubmitFromGuiAsync("Corrige calculator.py: add debe sumar y ahora resta. Lee el archivo y prepara una sustitución mínima para que la revise.");
                        agentActions = agent.LastActions.ToArray();
                        var proposed = agent.Changes!.Items.LastOrDefault(c => c.Status == "pending" && c.Path == "calculator.py") ?? throw new InvalidOperationException("La GUI no recibió el cambio del agente.");
                        form.ApplyFromGui();
                        if (proposed.Status != "applied" || !agent.Workspace!.Read("calculator.py").Text.Contains("return a + b")) throw new InvalidOperationException("El botón Aplicar no guardó el cambio después de finalizar la tarea.");
                        await form.SubmitFromGuiAsync("Activa las comprobaciones");
                        await form.CheckFromGuiAsync();
                        var log = Directory.EnumerateFiles(AppPaths.Logs, "check-*.json").OrderByDescending(File.GetLastWriteTimeUtc).First();
                        using var check = System.Text.Json.JsonDocument.Parse(File.ReadAllText(log));
                        if (check.RootElement.GetProperty("project").GetString() != root || check.RootElement.GetProperty("result").GetProperty("exitCode").GetInt32() != 0)
                            throw new InvalidOperationException("El botón Ejecutar no completó la prueba del proyecto abierto.");
                    }
                    else agent.Changes!.Propose("calculator.py", "def add(a, b):\n    return a + b\n", "Demostración de revisión: reemplazar resta por suma. No se ejecutó una IA en esta comprobación de GUI.");
                    form.ShowChanges(); await Task.Delay(400);
                    var screenshot = Path.Combine(AppPaths.Root, "checks", "gui.png"); form.CaptureWindow(screenshot);
                    AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "gui-result.json"), new { status = "complete", screenshot, agentActions, engine = args.Contains("--ui-agent-check") ? engine.Snapshot() : null,
                        controls = "explorador, editor, diff, chat, configuración y consola", testedButtons = args.Contains("--ui-agent-check") ? new[] { "Enviar tarea", "Aplicar", "Ejecutar" } : [], installedExe = Environment.ProcessPath });
                    form.Close();
                }
                catch (Exception e)
                {
                    form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", "gui-failed.png"));
                    AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "gui-result.json"), new { status = "failed", error = e.ToString() }); form.Close();
                }
            };
        }
        try
        {
            Application.Run(form);
            if (args.Any(a => a is "--skills-check" or "--skills-agent-check"))
            {
                var name = args.Contains("--skills-agent-check") ? "skills-agent.json" : "skills-components.json";
                using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.Root, "checks", name)));
                return report.RootElement.GetProperty("status").GetString() == "complete" ? 0 : 1;
            }
            return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(AppPaths.Logs, "gui-error.log"), e.ToString()); MessageBox.Show(e.Message, "SheepCode"); return 1; }
        finally
        {
            if (args.Any(a => a is "--ui-check" or "--ui-agent-check" or "--skills-check" or "--skills-agent-check" or "--laptop-ui-check" or "--integrations-ui-check"))
            { if (savedPreferenceBytes is not null) File.WriteAllBytes(Preferences.PathName, savedPreferenceBytes); else System.Text.Json.JsonSerializer.Deserialize<Preferences>(savedPreferences, AppPaths.Json)!.Save(); }
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); voice.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
