using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SheepCode;

internal static class SkillDiagnostics
{
    internal static Form CreatePcFixture()
    {
        var form = new Form { Text = "SheepCode · Ventana de prueba de Kuky", Size = new(470, 280), StartPosition = FormStartPosition.CenterParent, ControlBox = false, BackColor = Color.FromArgb(246, 225, 237) };
        var input = new TextBox { Name = "fixtureName", AccessibleName = "Texto para Kuky", Location = new(24, 38), Width = 380, Text = "Sheep" };
        var button = new Button { Name = "fixtureGreet", Text = "Saludar a Kuky", Location = new(24, 87), Size = new(170, 40) };
        var result = new Label { Name = "fixtureResult", AccessibleName = "Resultado de Kuky", Text = "Esperando saludo", Location = new(24, 155), Size = new(400, 45) };
        button.Click += (_, _) => { result.Text = "Kuky saluda a " + input.Text; result.AccessibleName = result.Text; };
        form.Controls.AddRange([input, button, result]); return form;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task RunAsync(MainForm form, AgentController agent, Preferences preferences, EngineHost engine, NeuralVoice voice, bool withAgent)
    {
        var started = DateTimeOffset.UtcNow; var passed = new List<string>();
        var savedPreferences = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences, AppPaths.Json), AppPaths.Json)!;
        var report = Path.Combine(AppPaths.Root, "checks", withAgent ? "skills-agent.json" : "skills-components.json");
        AppPaths.SaveJson(report, new { status = "running", started, installedExe = Environment.ProcessPath, scope = withAgent ? "Flujo conversacional de SheepCode instalado" : "Componentes integrados y configuración por texto; sin inferencia de IA" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9)); var token = timeout.Token;
        var custom = "diagnostic-" + Guid.NewGuid().ToString("N")[..8];
        var tools = new List<object>();
        void Observe(string name, string result) { tools.Add(new { name, result }); AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "skills-tools.json"), tools); }
        agent.ToolResult += Observe;
        form.VerificationMode(true, timeout);
        try
        {
            var project = Path.Combine(AppPaths.Root, "checks", "skills-project-" + started.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "README.md"), "Proyecto propio para verificar las skills integradas de SheepCode.\n"); agent.OpenProject(project);
            foreach (var skill in new[] { "code", "desktop", "browser", "models" }) agent.Skills.SetEnabled(skill, true);
            var catalog = await agent.SubmitAsync("Lista las skills", token); Assert(catalog.Contains("desktop") && catalog.Contains("browser"), "Las skills no llegaron al diálogo.");
            Assert(agent.Skills.Match("Lee esta página web https://example.com").Contains("browser"), "La skill de navegador no se descubre automáticamente.");
            Assert(agent.Skills.LoadInstructions("desktop").Contains("pc_read"), "No se cargaron las instrucciones de la skill de PC.");
            await agent.SubmitAsync("Crea la skill " + custom + " con descripción: Revisar resultados de la prueba de Kuky; instrucciones: Lee los resultados reales y confirma los controles observados. No cambies permisos.", token);
            Assert(agent.Skills.Match("$" + custom).Contains(custom), "La skill creada por texto no fue descubierta.");
            passed.Add("skills: descubrimiento, carga progresiva, creación por texto y registro en capacidades");
            var previousReasoning = preferences.Reasoning; await agent.SubmitAsync("Pon el razonamiento bajo", token);
            Assert(preferences.Reasoning == "low", "El ajuste por texto no se guardó.");
            var models = await agent.SubmitAsync("Lista los modelos", token); Assert(models.Contains("strata-local") && models.Contains("8192"), "El modelo real no figura en el catálogo.");
            await agent.SubmitAsync("Desactiva la skill models", token); var blocked = false;
            try { await agent.ControlAsync("Lista los modelos", token); } catch (InvalidOperationException) { blocked = true; }
            Assert(blocked, "La skill de modelos desactivada siguió disponible."); await agent.SubmitAsync("Activa la skill models", token);
            preferences.Reasoning = previousReasoning; preferences.Save(); passed.Add("modelos: perfil integrado, ajuste y desactivación reales; no se ejecutó otra IA");

            using var fixture = CreatePcFixture();
            var fixtureLog = Path.Combine(AppPaths.Logs, "pc-fixture-handles.log");
            fixture.HandleCreated += (_, _) => File.AppendAllText(fixtureLog, DateTimeOffset.UtcNow + " created " + fixture.Handle + "\n");
            fixture.HandleDestroyed += (_, _) => File.AppendAllText(fixtureLog, DateTimeOffset.UtcNow + " destroyed\n" + new System.Diagnostics.StackTrace() + "\n");
            fixture.Show(form);
            var windows = await agent.Desktop.WindowsAsync(token);
            var window = windows.Single(w => w.Title == fixture.Text && w.Pid == Environment.ProcessId);
            await agent.SubmitAsync("Selecciona la ventana " + window.Id, token); await agent.SubmitAsync("Lee la ventana", token);
            var edit = agent.Desktop.Observation!.Nodes.First(n => n.Editable && n.Name == "Texto para Kuky");
            await agent.Desktop.ActAsync("type", edit.Id, "SheepCode", "Escribe SheepCode en el campo de Kuky", token);
            var button = agent.Desktop.Observation!.Nodes.First(n => n.Clickable && n.Name == "Saludar a Kuky");
            var view = await agent.Desktop.ActAsync("click", button.Id, "", "Pulsa Saludar a Kuky", token);
            Assert(view.Nodes.Any(n => n.Name == "Kuky saluda a SheepCode"), "La acción de PC no tuvo un resultado real observable.");
            await agent.SubmitAsync("Desactiva la skill desktop", token); Assert(agent.Desktop.Selected is null, "Desactivar desktop no liberó la ventana.");
            blocked = false; try { await agent.Desktop.ReadAsync(token); } catch (InvalidOperationException) { blocked = true; }
            Assert(blocked, "Desktop desactivada no bloqueó sus herramientas."); await agent.SubmitAsync("Activa la skill desktop", token); await agent.SubmitAsync("Selecciona la ventana " + window.Id, token);
            passed.Add("PC: ventana propia, selección humana, lectura UIA, escritura, botón, resultado observado y desactivación");

            await using var server = new FixtureWebsite(); await server.StartAsync();
            var open = await agent.SubmitAsync("Abre la web " + server.Url, token);
            using var openDoc = JsonDocument.Parse(open); var tab = openDoc.RootElement.GetProperty("tab").GetString()!;
            var browser = agent.Browser!; var page = await browser.ReadAsync(tab, token);
            Assert(!JsonSerializer.Serialize(page).Contains("private-password"), "Se expuso una contraseña de la web.");
            var input = page.Nodes.Single(n => n.Tag == "input" && n.Type == "text");
            page = await browser.ActAsync("fill", tab, input.Id, "Sheep y Kuky", "Rellena el nombre", token);
            var greeting = page.Nodes.Single(n => n.Text == "Saludar"); page = await browser.ActAsync("click", tab, greeting.Id, "", "Pulsa Saludar", token);
            Assert(page.Text.Contains("Kuky saluda a Sheep y Kuky"), "No se verificó el saludo de la web.");
            var link = page.Nodes.Single(n => n.Text == "Segunda página"); page = await browser.ActAsync("click", tab, link.Id, "", "Abre la segunda página", token);
            Assert(page.Title == "Segunda página de Kuky", "No se navegó al enlace real."); await browser.BackAsync(tab, token);
            Assert((await browser.ReadAsync(tab, token)).Title == "El jardín de Kuky", "Volver no recuperó la página.");
            blocked = false; try { BrowserTools.ValidateUrl("javascript:alert(1)"); } catch (ArgumentException) { blocked = true; } Assert(blocked, "Se admitió un esquema de código.");
            await agent.SubmitAsync("Desactiva la skill browser", token); blocked = false;
            try { await agent.ControlAsync("Lista las pestañas", token); } catch (InvalidOperationException) { blocked = true; }
            Assert(blocked, "El navegador siguió operativo con la skill desactivada."); await agent.SubmitAsync("Activa la skill browser", token);
            passed.Add("Web: pestaña propia, DOM, campo, botón, enlace, volver, protección de contraseña y desactivación");

            object? agentEvidence = null;
            if (withAgent)
            {
                preferences.Reasoning = "low"; preferences.Save();
                var browserReply = await agent.SubmitAsync("$browser Lee la pestaña " + tab + " y dime el título exacto que devuelve browser_read. Usa esa herramienta para verificarlo y termina.", token);
                var browserActions = agent.LastActions.ToArray(); Assert(browserActions.Contains("browser_read") && browserReply.Contains("El jardín de Kuky", StringComparison.OrdinalIgnoreCase), "El agente no usó la lectura real del navegador.");
                AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "pc-fixture-state.json"), new { expected = window, currentHandle = (long)fixture.Handle, fixture.IsDisposed, fixture.Visible,
                    actual = (await agent.Desktop.WindowsAsync(token)).Where(w => w.Pid == Environment.ProcessId && w.Title == fixture.Text).ToArray(), browserActions });
                await agent.ControlAsync("Selecciona la ventana " + (long)fixture.Handle, token);
                fixture.WindowState = FormWindowState.Normal; fixture.Show(); fixture.Activate();
                await agent.Desktop.ReadAsync(token);
                var desktopReply = await agent.SubmitAsync("$desktop En la ventana seleccionada escribe Experimento en el campo Texto para Kuky y pulsa Saludar a Kuky. Inspecciona primero con pc_read y verifica el saludo final antes de terminar.", token);
                var desktopActions = agent.LastActions.ToArray(); var finalView = await agent.Desktop.ReadAsync(token);
                Assert(desktopActions.Contains("pc_type") && desktopActions.Contains("pc_click") && finalView.Nodes.Any(n => n.Name == "Kuky saluda a Experimento"), "El agente no completó las herramientas reales del PC.");
                var modelReply = await agent.SubmitAsync("$models Consulta model_status y confirma el modelo integrado y su contexto. Usa la herramienta y termina.", token);
                var modelActions = agent.LastActions.ToArray(); Assert(modelActions.Contains("model_status"), "El agente no consultó el estado real del modelo.");
                var wav = await voice.SpeakAsync("Desactiva la skill browser.", "neutral", token);
                using var voiceInput = new VoiceInput(); var transcript = await voiceInput.TranscribeAsync(wav, token);
                await agent.SubmitAsync(transcript.Trim().TrimEnd('.'), token);
                Assert(!agent.Skills.Enabled("browser"), "El dictado de la desactivación no cambió la skill.");
                await agent.SubmitAsync("Activa la skill browser", token);
                agentEvidence = new { browserActions, browserReply, desktopActions, desktopReply, modelActions, modelReply, voiceTranscript = transcript, engine = engine.Snapshot(), voice = voice.ReadyPacket };
                passed.Add("Flujo SheepCode: Strata eligió herramientas de web, PC y modelos; voz neuronal RX 580 -> dictado -> desactivación de skill");
            }
            await browser.CloseAsync(tab, token); Assert((await browser.TabsAsync(token)) == "[]", "La pestaña no se cerró.");
            await agent.SubmitAsync("Deja de controlar el PC", token); Assert(agent.Desktop.Selected is null, "La ventana no quedó liberada."); fixture.Close();
            form.VerificationMode(false); form.ShowSkillPanel(); await Task.Delay(350, token); var screenshot = Path.Combine(AppPaths.Root, "checks", "sheep-kuky-skills.png"); form.CaptureWindow(screenshot);
            AppPaths.SaveJson(report, new { status = "complete", started, completed = DateTimeOffset.UtcNow, passed, agentEvidence, screenshot, installedExe = Environment.ProcessPath,
                scope = withAgent ? "Agente instalado con herramientas propias y entrada por texto/voz" : "Componentes instalados; sin inferencia de IA", limits = "UI Automation en ventana elegida; pestañas WebView2 propias; un modelo Strata integrado" });
        }
        catch (Exception e)
        { form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", "skills-failed.png")); AppPaths.SaveJson(report, new { status = "failed", started, passed, tools, error = e.ToString(), installedExe = Environment.ProcessPath }); }
        finally
        {
            savedPreferences.Save();
            agent.ToolResult -= Observe;
            form.VerificationMode(false);
            agent.Desktop.Release(); var folder = Path.Combine(AppPaths.State, "skills", custom);
            if (Directory.Exists(folder) && Directory.GetFiles(folder).Length == 1 && File.Exists(Path.Combine(folder, "SKILL.md"))) { File.Delete(Path.Combine(folder, "SKILL.md")); Directory.Delete(folder); }
        }
    }

    private sealed class FixtureWebsite : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _running;
        internal string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
        internal Task StartAsync() { _listener.Start(); _running = Serve(); return Task.CompletedTask; }
        private async Task Serve()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Respond(client); } }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        private async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var line = await reader.ReadLineAsync(_stop.Token) ?? ""; var route = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                var html = route.StartsWith("/second") ? "<html><head><meta charset='utf-8'><title>Segunda página de Kuky</title></head><body><h1>La flor de sakura</h1></body></html>" :
                    "<html><head><meta charset='utf-8'><title>El jardín de Kuky</title><style>body{font:18px system-ui;background:#fceaf3;color:#583b69;padding:32px}input,button{padding:10px;margin:6px}</style></head><body><h1>🐑 El jardín de Kuky 🌸</h1><p>Una web local propia para comprobar herramientas.</p><input id='name' type='text' placeholder='Nombre'><button type='button' onclick=\"document.getElementById('result').textContent='Kuky saluda a '+document.getElementById('name').value\">Saludar</button><p id='result'>Esperando saludo</p><a href='/second'>Segunda página</a><input type='password' value='private-password'></body></html>";
                var body = Encoding.UTF8.GetBytes(html); var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, _stop.Token); await stream.WriteAsync(body, _stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); if (_running is not null) await _running; _stop.Dispose(); }
    }
}
