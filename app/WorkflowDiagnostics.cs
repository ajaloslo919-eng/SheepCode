using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SheepCode;

internal static class WorkflowDiagnostics
{
    private sealed class ContextGateway : HttpMessageHandler
    {
        internal List<ModelMessage[]> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Add(document.RootElement.GetProperty("messages").EnumerateArray().Select(m => new ModelMessage(m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!)).ToArray());
            if (Requests.Count == 1) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":400,\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":4228,\"n_ctx\":4096}}") };
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"action\\\":\\\"finish\\\",\\\"message\\\":\\\"La pestaña sigue abierta.\\\"}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":20}}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class SlowGateway : HttpMessageHandler
    {
        internal bool Started, Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started = true;
            try { await Task.Delay(TimeSpan.FromSeconds(60), token); return new(HttpStatusCode.OK); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
    private static void Assert(bool value, string error) { if (!value) throw new IOException(error); }
    internal static int Run()
    {
        var saved = File.ReadAllBytes(Preferences.PathName); var rows = new List<object>(); var report = Path.Combine(AppPaths.Root, "checks", "workflow-regression.json");
        try
        {
            CheckContextAsync(rows).GetAwaiter().GetResult(); CheckProposalAsync(rows).GetAwaiter().GetResult(); CheckStopButton(rows);
            AppPaths.SaveJson(report, new { status = "complete", rows,
                scope = "Herramientas y GUI reales de la aplicación instalada con HTTP y acciones programadas; proceso auxiliar propio cancelado. No mide IA ni certifica la página externa de Discord." }); return 0;
        }
        catch (Exception e) { AppPaths.SaveJson(report, new { status = "failed", rows, error = e.ToString() }); return 1; }
        finally { File.WriteAllBytes(Preferences.PathName, saved); }
    }
    private static async Task CheckContextAsync(List<object> rows)
    {
        using var gateway = new ContextGateway(); using var http = new HttpClient(gateway); await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice, http);
        var agent = new AgentController(engine, voice, Preferences.Load()); var system = agent.BuildSystemPrompt("abre la pestaña de discord", out _, 4096);
        var human = "Petición humana: quiero que me abras la pestaña de discord";
        var page = new BrowserView("a7e3f723", "https://discord.com/", "Discord", string.Concat(Enumerable.Repeat("Página larga con emojis 🐑, enlaces y texto. ", 200)),
            Enumerable.Range(1, 100).Select(i => new BrowserNode("n" + i, "a", "", "Enlace " + i, "", "https://example.com/" + i)).ToArray());
        var messages = new ModelMessage[] { new("system", system), new("user", human), new("user", "[Contexto opcional]" + new string('h', 6000)),
            new("assistant", "{\"action\":\"browser_open\",\"url\":\"https://discord.com/\"}"), new("user", "Resultado de browser_open (datos de la herramienta):\n{\"tab\":\"a7e3f723\"}"),
            new("assistant", "{\"action\":\"browser_read\",\"tab\":\"a7e3f723\"}"), new("user", "Resultado de browser_read (datos de la herramienta):\n" + JsonSerializer.Serialize(page)) };
        var packed = await PromptContext.FitAsync(messages, 4096, 1280, (items, _) => Task.FromResult(PromptContext.Estimate(items)), CancellationToken.None);
        Assert(packed[0].Content == system && packed[1].Content == human && PromptContext.Estimate(packed) + 1280 + 96 <= 4096, "No se conservó la petición o el margen de salida.");
        using (var json = JsonDocument.Parse(packed[^1].Content.Split('\n', 2)[1])) Assert(json.RootElement.GetProperty("Tab").GetString() == page.Tab && json.RootElement.GetProperty("Truncated").GetBoolean(), "La compactación perdió la pestaña o produjo JSON inválido.");
        await engine.CompleteTransportAsync(messages, "none", CancellationToken.None);
        Assert(gateway.Requests.Count == 2 && gateway.Requests[1][1].Content == human && engine.EffectiveContext == 4096, "El HTTP 400 no se recuperó de forma acotada.");
        var part = BrowserTools.Fragment(page, 1000, 500, 40, 6);
        Assert(part.Text == page.Text.Substring(1000, 500) && part.Nodes.Length == 6 && part.Nodes[0].Id == "n41" && part.Truncated, "La lectura por fragmentos no conservó los offsets.");
        rows.Add(new { check = "4096-context-output-reserve-valid-web-fragments-and-bounded-http400-retry", passed = true, estimate = PromptContext.Estimate(packed), requests = gateway.Requests.Count });
    }
    private static async Task CheckProposalAsync(List<object> rows)
    {
        var root = Path.Combine(AppPaths.Root, "checks", "proposal-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "saludos.py"); var original = "for i in range(5):\n    print(882)\n"; File.WriteAllText(path, original);
        const string code = "cantidad = int(input('¿A cuántos amigos quieres saludar? '))\ncontador = 0\nwhile contador < cantidad:\n    nombre = input('Nombre de tu amigo: ')\n    print('Hola, ' + nombre + '!')\n    contador += 1\n";
        var prefs = Preferences.Load(); prefs.DisabledSkills.Remove("code"); var calls = 0; var readSeen = false;
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
        var agent = new AgentController(engine, voice, prefs, (messages, _, token) =>
        {
            token.ThrowIfCancellationRequested(); calls++;
            if (calls == 2) { readSeen = messages.Any(m => m.Content.Contains("Resultado de read_file") && m.Content.Contains("print(882)")); Assert(readSeen, "No se leyó el archivo antes de generar la segunda propuesta."); }
            return Task.FromResult(calls <= 2 ? JsonSerializer.Serialize(new { action = "write_file", path = "saludos.py", content = code, message = "Propuesta del programa con while." }) : "{\"action\":\"finish\",\"message\":\"Código preparado para revisión.\"}");
        });
        agent.OpenProject(root); agent.ActiveFile = "saludos.py";
        await agent.SubmitAsync("Programa Python para saludar a tantos amigos como indique input, con while.", CancellationToken.None);
        Assert(readSeen && calls == 3 && agent.LastActions.Contains("read_file") && agent.Changes!.Items.Single().After == code && File.ReadAllText(path) == original, "No quedó una propuesta revisable sin modificar el archivo.");
        rows.Add(new { check = "existing-python-file-read-before-write-proposal-without-overwrite", passed = true });
    }
    private static void CheckStopButton(List<object> rows)
    {
        var prefs = Preferences.Load(); prefs.ReadAloud = false;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--cancellation-helper"); using var helper = Process.Start(start) ?? throw new IOException("No se inició el auxiliar propio.");
        var helperPid = helper.Id;
        bool HelperExited() { try { using var process = Process.GetProcessById(helperPid); return process.HasExited; } catch (ArgumentException) { return true; } }
        using var gateway = new SlowGateway(); using var http = new HttpClient(gateway); var voice = new NeuralVoice(); var engine = new EngineHost(voice, http, helper);
        var agent = new AgentController(engine, voice, prefs, engine.CompleteTransportAsync); using var form = new MainForm(prefs, voice, engine, agent); Exception? error = null; var elapsed = 0d;
        string Proposals() => JsonSerializer.Serialize(agent.Changes?.Items.Select(c => new { c.Id, c.Status, c.After }));
        var proposalsBefore = Proposals();
        form.Shown += async (_, _) =>
        {
            try
            {
                var pending = form.SubmitFromGuiAsync("Revisa el proyecto sin cambiar archivos."); var wait = Stopwatch.StartNew();
                while (!gateway.Started && wait.ElapsedMilliseconds < 5000) await Task.Delay(20);
                Assert(gateway.Started && !HelperExited(), "No empezó la solicitud de prueba del botón Parar.");
                var clock = Stopwatch.StartNew(); form.StopFromGui(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
                while (!HelperExited() && clock.ElapsedMilliseconds < 5000) await Task.Delay(20); elapsed = clock.Elapsed.TotalMilliseconds;
                Assert(gateway.Cancelled && HelperExited() && form.SendEnabled && elapsed < 5000 && Proposals() == proposalsBefore,
                    $"Parar: HTTP cancelado={gateway.Cancelled}, auxiliar terminado={HelperExited()}, Enviar disponible={form.SendEnabled}, tiempo={elapsed:F0} ms, propuestas conservadas={Proposals() == proposalsBefore}.");
            }
            catch (Exception e) { error = e; }
            finally { form.Close(); }
        };
        Application.Run(form); engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); voice.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (error is not null) throw error;
        rows.Add(new { check = "real-stop-button-cancels-http-owned-process-and-restores-send", passed = true, cancellationMilliseconds = elapsed });
    }
}
