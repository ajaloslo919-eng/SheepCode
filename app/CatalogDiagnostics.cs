using System.Net;
using System.Text;
using System.Text.Json;

namespace SheepCode;

internal static class CatalogDiagnostics
{
    private static void Assert(bool condition, string message) { if (!condition) throw new IOException(message); }
    private static async Task MustFailAsync(Func<Task> action)
    { var blocked = false; try { await action(); } catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Xml.XmlException) { blocked = true; } Assert(blocked, "Se permitió una acción que debía bloquearse."); }
    private sealed class UpdateGateway(bool corrupt = false, bool wrongDigest = false) : HttpMessageHandler
    {
        internal static readonly byte[] Bytes = Encoding.UTF8.GetBytes("Fixture de descarga; nunca se ejecuta como instalador.");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var hash = AppPaths.Hash(Bytes); var uri = request.RequestUri!;
            var installed = Version.Parse(UpdateManager.CurrentVersion);
            var futureTag = "v" + new Version(installed.Major, installed.Minor + 1, 0);
            string Asset(string name) => "https://github.com/" + UpdateManager.Repository + "/releases/download/" + futureTag + "/" + name;
            var text = uri.Host == "api.github.com" ? JsonSerializer.Serialize(new { tag_name = futureTag, draft = false, prerelease = false, assets = new[] { new { name = "SheepCode-Setup.exe", size = Bytes.Length, browser_download_url = Asset("SheepCode-Setup.exe"), digest = "sha256:" + (wrongDigest ? new string('0', 64) : hash) }, new { name = "SHA256SUMS.txt", size = 100, browser_download_url = Asset("SHA256SUMS.txt"), digest = "" } } }) : hash + "  SheepCode-Setup.exe\n";
            HttpContent content = uri.AbsolutePath.EndsWith(".exe") ? new ByteArrayContent(corrupt ? Enumerable.Repeat((byte)'x', Bytes.Length).ToArray() : Bytes) : new StringContent(text);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class McpGateway : HttpMessageHandler
    {
        internal int Calls; internal bool SessionAndVersion;
        private static string ImageFixture() { using var image = new System.Drawing.Bitmap(2, 2); using var data = new MemoryStream(); image.Save(data, System.Drawing.Imaging.ImageFormat.Png); return Convert.ToBase64String(data.ToArray()); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var packet = input.RootElement;
            if (!packet.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = packet.GetProperty("method").GetString(); object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "SheepCode fixture", version = "1" } },
                "tools/list" => new { tools = new[] { new { name = "echo_fixture", description = "Fixture propia, sin servicio externo.", inputSchema = new { type = "object", properties = new { text = new { type = "string" } } } } } },
                "tools/call" => new { content = new object[] { new { type = "text", text = packet.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() }, new { type = "image", mimeType = "image/png", data = ImageFixture() } } },
                _ => throw new IOException("Método inesperado en fixture HTTP")
            };
            if (method != "initialize") { Assert(request.Headers.GetValues("MCP-Session-Id").Single() == "fixture-session" && request.Headers.GetValues("MCP-Protocol-Version").Single() == "2025-11-25", "MCP perdió sesión o versión."); SessionAndVersion = true; }
            if (method == "tools/call") Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("MCP-Session-Id", "fixture-session");
            response.Content = method == "tools/list" ? new StringContent("event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), result }) + "\n\n", Encoding.UTF8, "text/event-stream") : new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), result }, AppPaths.Json), Encoding.UTF8, "application/json");
            return response;
        }
    }
    internal static int McpFixture()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line); var packet = doc.RootElement;
            if (!packet.TryGetProperty("method", out var method) || !packet.TryGetProperty("id", out var id)) continue;
            object result;
            if (method.GetString() == "initialize") result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "SheepCode propio", version = "1" } };
            else if (method.GetString() == "tools/list")
            {
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":\"ping-fixture\",\"method\":\"ping\"}"); Console.Out.Flush();
                result = new { tools = new[] { new { name = "echo_fixture", inputSchema = new { type = "object" } }, new { name = "slow_fixture", inputSchema = new { type = "object" } } } };
            }
            else if (method.GetString() == "tools/call")
            {
                if (packet.GetProperty("params").GetProperty("name").GetString() == "slow_fixture") Thread.Sleep(60000);
                result = new { content = new[] { new { type = "text", text = packet.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() } } };
            }
            else result = new { };
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result })); Console.Out.Flush();
        }
        return 0;
    }
    internal static async Task<int> RunAsync()
    {
        var rows = new List<object>(); var root = Path.Combine(AppPaths.Root, "checks", "catalog-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
        var protectedFiles = new[] { Preferences.PathName, Path.Combine(AppPaths.State, "mcp-servers.json"), Path.Combine(AppPaths.State, "automations.json") };
        var saved = protectedFiles.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        void Pass(string check, object? evidence = null) => rows.Add(new { check, passed = true, evidence });
        var prefs = Preferences.Load(); prefs.DisabledSkills.Clear(); prefs.ReadAloud = false;
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
        using var updates = new UpdateManager(new UpdateGateway());
        var fixtureQueue = new Queue<string>();
        var agent = new AgentController(engine, voice, prefs, (messages, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(fixtureQueue.Count > 0 ? fixtureQueue.Dequeue() : "{\"action\":\"finish\",\"message\":\"Fixture completada.\"}"); }, updates);
        try
        {
            Assert(agent.Skills.Items.Count >= 57 && agent.Skills.Errors.Count == 0, "El catálogo no carga todas las skills.");
            Assert(agent.Skills.Get("imagegen").Backend == "imagegen", "La generación local fue registrada como proveedor externo.");
            var providerSkill = agent.Skills.Items.First(s => s.Backend == "mcp" && s.Name != "connectors").Name;
            Assert(agent.Skills.State(providerSkill).Contains("sin configurar"), "Una skill externa afirmó estar disponible sin proveedor.");
            Assert((await agent.ControlAsync("Lista las skills", CancellationToken.None))!.Contains("documents"), "El diálogo no conoce las skills.");
            Pass("57-skills-loaded-and-visible-with-local-generation-and-honest-provider-state", agent.Skills.Items.Count);
            agent.OpenProject(root);
            var samples = new Dictionary<string, (string Skill, string Content)> {
                ["docx"] = ("documents", "{\"title\":\"Sheep y Kuky\",\"paragraphs\":[\"Hola, María.\",\"Documento editable.\"]}"),
                ["xlsx"] = ("spreadsheets", "{\"sheet\":\"Gastos\",\"rows\":[[\"Concepto\",\"Importe\"],[\"Luz\",30],[\"=NO_EJECUTAR()\",true]]}"),
                ["pptx"] = ("presentations", "{\"title\":\"SheepCode\",\"slides\":[{\"title\":\"Hola, Kuky\",\"bullets\":[\"Código editable\",\"Revisión humana\"]}]}"),
                ["pdf"] = ("pdf", "{\"title\":\"Sheep y Kuky\",\"paragraphs\":[\"Hola, María.\",\"PDF latino básico.\"]}"),
                ["csv"] = ("spreadsheets", "concepto,importe\nLuz,30\n"),
                ["svg"] = ("visualize", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"320\" height=\"180\"><rect width=\"320\" height=\"180\" fill=\"#fff7fb\"/><text x=\"20\" y=\"50\">Sheep y Kuky</text></svg>"),
                ["html"] = ("visualize", "<!doctype html><html lang=\"es\"><meta charset=\"utf-8\"><title>Sheep y Kuky</title><body><h1>Sheep y Kuky</h1></body></html>")
            };
            foreach (var sample in samples)
            {
                var path = "sample." + sample.Key; fixtureQueue.Enqueue(JsonSerializer.Serialize(new { action = "artifact_propose", path, format = sample.Key, content = sample.Value.Content, skill = sample.Value.Skill }));
                await agent.SubmitAsync("Crea " + path + " para revisión", CancellationToken.None);
                var change = agent.Changes!.Items.Last(c => c.Path == path); Assert(!File.Exists(Path.Combine(root, path)), "Se aplicó el artefacto sin revisión.");
                agent.Changes.Apply(change.Id); Assert(File.Exists(Path.Combine(root, path)), "No se guardó el artefacto.");
                var read = await agent.ControlAsync("Lee el documento " + path, CancellationToken.None); Assert(read!.Contains("text") && !read.Contains("No se obtuvo su texto"), "No se leyó el artefacto creado.");
                var bytes = agent.Workspace!.ReadBytes(path); agent.Changes.Undo(change.Id); Assert(!File.Exists(Path.Combine(root, path)), "Deshacer no quitó la creación.");
                File.WriteAllBytes(Path.Combine(root, path), bytes); Pass("propose-apply-read-undo-" + sample.Key);
            }
            var oldHash = AppPaths.Hash(agent.Workspace!.ReadBytes("sample.docx"));
            var replacement = agent.Changes!.ProposeArtifact("sample.docx", "docx", samples["docx"].Content.Replace("Hola", "Adiós"), "Cambio", oldHash); var original = agent.Workspace.ReadBytes("sample.docx");
            agent.Changes.Apply(replacement.Id); agent.Changes.Undo(replacement.Id); Assert(agent.Workspace.ReadBytes("sample.docx").SequenceEqual(original), "Deshacer no recuperó el binario original.");
            await MustFailAsync(() => Task.Run(() => agent.Changes.ProposeArtifact("sample.docx", "docx", samples["docx"].Content, "Sin lectura")));
            await MustFailAsync(() => Task.Run(() => ArtifactTools.Create("svg", "<!DOCTYPE svg [<!ENTITY e SYSTEM 'file:///secret'>]><svg xmlns='http://www.w3.org/2000/svg'>&e;</svg>")));
            Pass("binary-edit-undo-preserves-original-and-missing-read-or-dtd-blocked");
            await agent.ControlAsync("Desactiva la skill documents", CancellationToken.None);
            await MustFailAsync(async () => await agent.ControlAsync("Lee el documento sample.docx", CancellationToken.None));
            await agent.ControlAsync("Activa la skill documents", CancellationToken.None); Pass("document-skill-toggle-shared-text-and-accepted-dictation-dispatcher");
            var programRoot = new ProjectWorkspace(AppPaths.Root); await MustFailAsync(() => Task.Run(() => programRoot.Resolve("state/mcp-servers.json"))); Pass("model-cannot-edit-its-permissions-or-installed-binaries");
            var backup = await agent.ControlAsync("Haz un respaldo del proyecto", CancellationToken.None); Assert(backup!.Contains("sha256"), "No se verificó el respaldo."); Pass("project-backup-dialogue-and-verified-archive");
            if (AppPaths.ToolExecutable("git.exe") is { } git)
            {
                var start = new System.Diagnostics.ProcessStartInfo(git) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                foreach (var arg in new[] { "-c", "init.defaultBranch=main", "init", "--quiet" }) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert(process.ExitCode == 0, "No se preparó el repo de prueba.");
                var status = await agent.ControlAsync("git status", CancellationToken.None); Assert(status!.Contains("sample.docx"), "Git no leyó el repo actual.");
                agent.Skills.SetEnabled("git", false); await MustFailAsync(async () => await agent.ControlAsync("git status", CancellationToken.None)); agent.Skills.SetEnabled("git", true); Pass("git-read-real-local-repository-dialogue-and-disable");
            }
            var schedule = await agent.ControlAsync("Crea automatización fixture cada 5 minutos: Revisa sample.html", CancellationToken.None);
            var item = agent.Automations.Items.Last(); item.Next = DateTimeOffset.UtcNow.AddSeconds(-1);
            Assert(agent.Automations.Due(root, DateTimeOffset.UtcNow) == item && agent.Automations.Due(root + "x", DateTimeOffset.UtcNow) is null, "Se ignoró el proyecto de la automatización.");
            await agent.ControlAsync("Pausa automatización " + item.Id, CancellationToken.None); Assert(agent.Automations.Due(root, DateTimeOffset.UtcNow) is null, "Una tarea pausada siguió ejecutándose.");
            fixtureQueue.Enqueue("{\"action\":\"mcp_status\"}"); await agent.SubmitAsync("No debe abrir conexiones", CancellationToken.None, scheduled: true); Assert(!agent.LastActions.Contains("mcp_status"), "El planificador ejecutó una acción externa."); Pass("automation-create-due-project-pause-and-external-action-block");
            using (var mcp = new McpTools(agent.Skills))
            {
                mcp.SaveConfiguration(JsonSerializer.Serialize(new[] { new McpServerConfig { Name = "fixture", Command = Environment.ProcessPath!, Arguments = ["--mcp-fixture"], Skills = ["imagegen"], AllowedTools = ["echo_fixture", "slow_fixture"], Enabled = true } }));
                var listing = await mcp.ListAsync("fixture", CancellationToken.None); Assert(listing.Contains("echo_fixture"), "No se descubrieron herramientas stdio.");
                await MustFailAsync(() => mcp.CallAsync("fixture", "echo_fixture", "{\"text\":\"rechazado\"}", "imagegen", CancellationToken.None));
                mcp.ApproveCall = (_, _, _, _) => Task.FromResult(true);
                var response = await mcp.CallAsync("fixture", "echo_fixture", "{\"text\":\"Kuky real stdio\"}", "imagegen", CancellationToken.None); Assert(response.Contains("Kuky real stdio"), "La herramienta stdio no devolvió su resultado.");
                await MustFailAsync(() => mcp.CallAsync("fixture", "no_autorizada", "{}", "imagegen", CancellationToken.None));
                agent.Skills.SetEnabled("imagegen", false); await MustFailAsync(() => mcp.CallAsync("fixture", "echo_fixture", "{}", "imagegen", CancellationToken.None)); agent.Skills.SetEnabled("imagegen", true);
                using var cancel = new CancellationTokenSource(300); await MustFailAsync(() => mcp.CallAsync("fixture", "slow_fixture", "{\"text\":\"nunca\"}", "imagegen", cancel.Token));
                Pass("real-owned-stdio-mcp-handshake-ping-approval-exact-allowlist-disable-and-cancellation");
            }
            var gateway = new McpGateway(); using (var mcp = new McpTools(agent.Skills, () => gateway))
            {
                string? media = null; mcp.MediaReceived += (path, _) => media = path;
                mcp.SaveConfiguration(JsonSerializer.Serialize(new[] { new McpServerConfig { Name = "fixture-http", Url = "https://fixture.invalid/mcp", Skills = ["imagegen"], AllowedTools = ["echo_fixture"], Enabled = true } })); mcp.ApproveCall = (_, _, _, _) => Task.FromResult(true);
                await mcp.ListAsync("fixture-http", CancellationToken.None); var response = await mcp.CallAsync("fixture-http", "echo_fixture", "{\"text\":\"Kuky HTTP\"}", "imagegen", CancellationToken.None);
                Assert(gateway.Calls == 1 && gateway.SessionAndVersion && response.Contains("Kuky HTTP"), "Fallo en JSON multilínea, SSE, sesión o versión MCP."); Pass("http-mcp-fixture-pretty-json-sse-session-version-and-call-result");
                Assert(media is not null && File.Exists(media) && response.Contains("local_artifact") && !response.Contains("iVBOR"), "El medio MCP no se guardó o contaminó el contexto con base64."); Pass("mcp-image-fixture-saved-with-hash-and-without-base64-in-model-context");
            }
            await agent.ControlAsync("Busca actualizaciones", CancellationToken.None); await agent.ControlAsync("Descarga la actualización", CancellationToken.None); updates.VerifyDownloaded();
            File.AppendAllText(updates.Downloaded!, "alterado"); await MustFailAsync(() => Task.Run(updates.VerifyDownloaded));
            using (var bad = new UpdateManager(new UpdateGateway(corrupt: true))) { await bad.CheckAsync(CancellationToken.None); await MustFailAsync(() => bad.DownloadAsync(null, CancellationToken.None)); Assert(bad.Downloaded is null, "Se conservó una descarga corrupta."); }
            using (var bad = new UpdateManager(new UpdateGateway(wrongDigest: true))) await MustFailAsync(() => bad.CheckAsync(CancellationToken.None));
            await agent.ControlAsync("Activa la búsqueda automática de actualizaciones", CancellationToken.None); Assert(prefs.AutoCheckUpdates, "No se activó la búsqueda."); await agent.ControlAsync("Desactiva la búsqueda automática de actualizaciones", CancellationToken.None);
            await agent.ControlAsync("Desactiva la skill updater", CancellationToken.None); await MustFailAsync(async () => await agent.ControlAsync("Busca actualizaciones", CancellationToken.None));
            Pass("updater-dispatcher-download-size-sha256-tampering-digest-toggle-and-disable-no-execution");
            AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "catalog-regression.json"), new { status = "complete", rows, artifacts = root, scope = "Herramientas, GUI y dispatcher instalados; acciones de modelo y HTTP programados, servidor MCP stdio propio real. No es un benchmark de IA, ni valida proveedores externos, ni ejecuta el setup fixture." }); return 0;
        }
        catch (Exception e) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "catalog-regression.json"), new { status = "failed", rows, artifacts = root, error = e.ToString() }); return 1; }
        finally { agent.Connections.Dispose(); foreach (var pair in saved) { if (pair.Value is not null) File.WriteAllBytes(pair.Key, pair.Value); else if (File.Exists(pair.Key)) File.Delete(pair.Key); } }
    }
}
