using System.Text.Json;
using SheepCode.Distribution;

namespace SheepCode;

internal static class CodeValidationDiagnostics
{
    private const string Request = "Crea saludos.py en Python. Pregunta con input a cuántos amigos quiere saludar. Repite tantas veces preguntar el nombre con input y saludar con print. Primero usa un bloque while.";
    private const string Good = "cantidad = int(input('¿Cuántos amigos quieres saludar? '))\ncontador = 0\nwhile contador < cantidad:\n    nombre = input('Nombre del amigo: ')\n    print(f'Hola, {nombre}!')\n    contador += 1\n";
    private const string WrongLoop = "cantidad = int(input('¿Cuántos amigos? '))\n# while no cuenta\nfor contador in range(cantidad):\n    nombre = input('Nombre: ')\n    print(f'Hola, {nombre}!')\n";
    private static void Assert(bool value, string message) { if (!value) throw new IOException(message); }
    private static string Write(string content, string path = "saludos.py") => JsonSerializer.Serialize(new { action = "write_file", path, content, message = "Ya guardé el programa y pasó todas las pruebas." });
    private static string Finish => "{\"action\":\"finish\",\"message\":\"Ya guardé todo y pasó las pruebas.\"}";
    internal static async Task<int> RunAsync()
    {
        var installed = AppPaths.Root; var environment = Environment.GetEnvironmentVariable("SHEEPCODE_HOME");
        var output = Path.Combine(installed, "checks", "code-review.json");
        var results = new List<object>();
        var report = new Dictionary<string, object?> { ["status"] = "starting", ["scope"] = "Analizadores reales y flujo de AgentController instalado con respuestas deterministas simuladas. No se ejecutan otras IAs ni se mide rendimiento de modelos. El código candidato nunca se ejecuta.", ["checks"] = results };
        try
        {
            var originalPython = CodeValidator.PythonExecutable ?? throw new IOException("Falta Python para verificar la integración instalada.");
            var profile = RuntimeProfile.Load(installed);
            var fixture = Path.Combine(installed, "checks", "code-review-home-" + Guid.NewGuid().ToString("N")[..8]);
            Environment.SetEnvironmentVariable("SHEEPCODE_HOME", fixture); AppPaths.Initialize();
            var paths = InstallationPaths.Load(installed); paths.ToolsPython = originalPython;
            paths.Models = InstallationPaths.Resolve(installed, paths.Models);
            AppPaths.SaveJson(Path.Combine(AppPaths.State, "paths.json"), paths);
            AppPaths.SaveJson(AppPaths.EngineConfig, profile);
            var required = CodeRequirements.FromHuman(Request);
            Assert(required.Loops.SequenceEqual(new[] { "while" }) && required.Input && required.Print && required.Repeated, "No se extrajeron los requisitos explícitos del ejercicio.");
            async Task Check(string name, string path, string source, string status, CodeRequirements? requirements = null)
            {
                var validation = await CodeValidator.CheckAsync(path, source, requirements ?? required, true, CancellationToken.None);
                Assert(validation.Status == status, name + ": estado inesperado " + JsonSerializer.Serialize(validation));
                results.Add(new { name, validation });
            }
            await Check("python-requested-while-input-print", "saludos.py", Good, "passed");
            await Check("python-colon-and-error-line", "saludos.py", Good.Replace("while contador < cantidad:", "while contador < cantidad"), "failed");
            await Check("python-indentation", "saludos.py", Good.Replace("    nombre", "  nombre"), "failed");
            await Check("python-invalid-return-scope", "saludos.py", "return 4\n", "failed");
            await Check("python-invalid-break-scope", "saludos.py", "break\n", "failed");
            await Check("python-filename-heading", "saludos.py", "saludos.py\n" + Good, "failed");
            await Check("python-markdown-envelope", "saludos.py", "```python\n" + Good + "```", "failed");
            await Check("python-numbered-source", "saludos.py", "1: print('Hola')\n", "failed");
            await Check("for-does-not-satisfy-while", "saludos.py", WrongLoop, "failed");
            await Check("while-string-is-not-a-loop", "saludos.py", "text = 'while'\nprint(text)\n", "failed");
            await Check("while-false-is-not-the-requested-repetition", "saludos.py", Good.Replace("while contador < cantidad:", "while False:"), "failed");
            await Check("accented-filename-keeps-human-requirements", "salúd.py", WrongLoop, "failed", CodeRequirements.FromHuman(Request.Replace("saludos.py", "salúd.py")));
            await Check("python-required-input", "saludos.py", Good.Replace("input('Nombre del amigo: ')", "'Ana'").Replace("int(input('¿Cuántos amigos quieres saludar? '))", "2"), "failed");
            await Check("python-required-print", "saludos.py", Good.Replace("    print(f'Hola, {nombre}!')\n", ""), "failed");
            await Check("python-input-must-repeat-in-loop", "saludos.py", Good.Replace("    nombre = input('Nombre del amigo: ')\n", "    nombre = 'Ana'\n"), "failed");
            await Check("simple-counted-while-needs-counter-update", "saludos.py", Good.Replace("    contador += 1\n", ""), "failed");
            await Check("duplicate-interactive-program", "saludos.py", Good + Good, "failed");
            var extraOutput = await CodeValidator.CheckAsync("saludos.py", Good + "print('Saludos a todos')\n", required, true, CancellationToken.None);
            Assert(extraOutput.Status == "passed" && extraOutput.Diagnostics.Any(d => d.Code == "additional_output" && d.Severity == "warning"), "La salida adicional pasó sin un aviso sobre los límites de sintaxis.");
            results.Add(new { name = "output-outside-requested-loop-is-a-visible-warning-not-a-runtime-pass", validation = extraOutput });
            var forRequest = CodeRequirements.FromHuman("Crea saludos.py usando un for, sin while. Pregunta con input y saluda con print repetidas veces.");
            await Check("explicit-for-without-while", "saludos.py", WrongLoop, "passed", forRequest);
            await Check("for-request-blocks-while", "saludos.py", Good, "failed", forRequest);
            var none = CodeRequirements.FromHuman("");
            await Check("csharp-valid-syntax-and-while", "main.cs", "int i = 0; while (i < 2) { i++; }", "passed", CodeRequirements.FromHuman("Crea main.cs con un while."));
            await Check("csharp-missing-semicolon", "main.cs", "while(true) { System.Console.WriteLine(1) }", "failed", none);
            await Check("javascript-valid-module-and-while", "main.mjs", "export const value = 2; let i = 0; while(i < value) { i++; }", "passed", CodeRequirements.FromHuman("Crea main.mjs usando while."));
            await Check("javascript-invalid-syntax", "main.js", "while(true) { const x = ; }", "failed", none);
            await Check("javascript-comment-does-not-satisfy-while", "main.js", "// while\nfor(let i=0;i<2;i++){}", "failed", CodeRequirements.FromHuman("Crea main.js usando while."));
            await Check("json-valid", "data.json", "{\"ok\":true}", "passed", none);
            await Check("json-invalid", "data.json", "{\"ok\":}", "failed", none);
            await Check("svg-valid", "image.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>", "passed", none);
            await Check("xml-broken-tag", "image.svg", "<svg><path></svg>", "failed", none);
            await Check("xml-external-entities-disabled", "data.xml", "<!DOCTYPE a [<!ENTITY secret SYSTEM 'file:///private'>]><a>&secret;</a>", "failed", none);
            await Check("typescript-never-claims-validated", "main.ts", "const x: string = 4;", "unsupported", none);
            var disabled = await CodeValidator.CheckAsync("saludos.py", "while ?", required, false, CancellationToken.None);
            Assert(disabled.Status == "disabled" && !disabled.Summary.Contains("Sintaxis revisada"), "El ajuste desactivado inventó un resultado.");
            results.Add(new { name = "disabled-honest-status", validation = disabled });
            var sentinel = Path.Combine(fixture, "candidate-executed.txt");
            await Check("python-candidate-never-executed", "danger.py", "from pathlib import Path\nPath(" + JsonSerializer.Serialize(sentinel) + ").write_text('not allowed')\n", "passed", none);
            Assert(!File.Exists(sentinel), "Se ejecutó código candidato durante la validación.");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.CancelAfter(30);
                try { await CodeValidator.CheckAsync("large.py", string.Concat(Enumerable.Repeat("x = 1\n", 30000)), none, true, cancel.Token); throw new IOException("La cancelación de la revisión no se respetó."); }
                catch (OperationCanceledException) { results.Add(new { name = "parser-cancellation", passed = true }); }
            }
            var concurrentProject = Path.Combine(fixture, "concurrent"); Directory.CreateDirectory(concurrentProject);
            File.WriteAllText(Path.Combine(concurrentProject, "data.py"), "value = 1\n");
            var workspace = new ProjectWorkspace(concurrentProject); var store = new ChangeStore(workspace); var before = workspace.Read("data.py");
            var validated = await CodeValidator.CheckAsync("data.py", "value = 2\n", none, true, CancellationToken.None);
            var pending = store.ProposeValidated("data.py", "value = 2\n", "Fixture", before.Hash, validated);
            File.WriteAllText(Path.Combine(concurrentProject, "data.py"), "value = 3\n");
            try { store.ProposeValidated("data.py", "value = 2\n", "Fixture", before.Hash, validated); throw new InvalidOperationException("No se bloqueó la edición concurrente."); }
            catch (IOException) { }
            Assert(store.Items.Count == 1 && pending.Status == "pending" && workspace.Read("data.py").Text == "value = 3\n", "Se sobrescribió o sustituyó un cambio durante la revisión.");
            try { store.ProposeValidated("data.py", "value = 4\n", "Fixture", workspace.Read("data.py").Hash, validated); throw new IOException("Se aceptó una revisión de otro contenido."); }
            catch (InvalidOperationException) { }
            results.Add(new { name = "concurrency-and-validation-content-hash", passed = true });
            var repeatedProject = Path.Combine(fixture, "repeated-store"); Directory.CreateDirectory(repeatedProject);
            var repeatedStore = new ChangeStore(new ProjectWorkspace(repeatedProject));
            var basic = await CodeValidator.CheckAsync("saludos.py", Good, none, true, CancellationToken.None);
            var firstProposal = repeatedStore.ProposeValidated("saludos.py", Good, "Fixture", null, basic);
            var explicitReport = await CodeValidator.CheckAsync("saludos.py", Good, required, true, CancellationToken.None);
            var repeatedProposal = repeatedStore.ProposeValidated("saludos.py", Good, "Fixture", null, explicitReport);
            Assert(firstProposal.Id == repeatedProposal.Id && repeatedStore.Items.Count == 1 && repeatedProposal.Validation!.Checks.Any(c => c.Contains("Bloque while")), "Una propuesta idéntica perdió la revisión de los requisitos de la petición actual.");
            results.Add(new { name = "identical-source-retains-current-request-validation-with-same-id", passed = true });
            await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
            foreach (var kind in new[] { "strata-cpu", "llama", "strata", "strata-dual" })
            foreach (var fast in kind == "strata-cpu" ? new[] { true, false } : new[] { false })
            {
                // Only routing/configuration fixtures. Never loads this profile or any weights.
                profile.Kind = kind; profile.Context = kind == "strata-cpu" ? 4096 : 8192;
                AppPaths.SaveJson(AppPaths.EngineConfig, profile);
                var prefs = new Preferences { ValidateCode = true, AllowChecks = false, ReadAloud = false, FastCpuMode = fast, MaximumSteps = 8 };
                var calls = 0; var visible = 0;
                var agent = new AgentController(engine, voice, prefs, (messages, _, _) =>
                {
                    calls++;
                    if (calls == 1) return Task.FromResult(Write(Good.Replace("while contador < cantidad:", "while contador < cantidad")));
                    if (calls == 2) { Assert(messages.Any(m => m.Content.Contains("VALIDATION_FAILED") && m.Content.Contains("python_syntax")), "El modelo no recibió los errores de sintaxis."); return Task.FromResult(Write(WrongLoop)); }
                    if (calls == 3) { Assert(messages.Any(m => m.Content.Contains("required_while")), "El modelo no recibió el requisito while incumplido."); return Task.FromResult(Write(Good)); }
                    return Task.FromResult(Finish);
                });
                var project = Path.Combine(fixture, "projects", kind + "-" + fast); Directory.CreateDirectory(project); agent.OpenProject(project);
                agent.ChangeProposed += change => { visible++; Assert(change.Validation?.Status == "passed" && change.After == Good, "Una propuesta inválida llegó al diff."); };
                var result = await agent.SubmitAsync(Request, CancellationToken.None);
                var proposed = agent.Changes!.Items.Single();
                Assert(calls == 4 && visible == 1 && proposed.After == Good && !File.Exists(Path.Combine(project, "saludos.py")), "No se completó la reparación revisable sin escribir el proyecto.");
                Assert(!prefs.AllowChecks && !result.Contains("pasó las pruebas"), "Se concedió ejecución o se afirmó una prueba no realizada.");
                await agent.SubmitAsync("Aplica el cambio " + proposed.Id, CancellationToken.None);
                Assert(File.ReadAllText(Path.Combine(project, "saludos.py")) == Good, "La aprobación humana no aplicó el contenido revisado.");
                await agent.SubmitAsync("Deshaz el cambio " + proposed.Id, CancellationToken.None);
                Assert(!File.Exists(Path.Combine(project, "saludos.py")), "No se pudo deshacer el archivo nuevo.");
                agent.Connections.Dispose(); agent.Updates.Dispose();
                results.Add(new { name = "agent-routing-repair-before-diff", kind, fast, completionsSimulated = calls, proposalsVisible = visible, humanApplyUndo = true, passed = true });
            }
            profile.Kind = "strata-cpu"; profile.Context = 4096; AppPaths.SaveJson(AppPaths.EngineConfig, profile);
            var controlPrefs = new Preferences { MaximumSteps = 8, ValidateCode = true }; var tries = 0;
            var persistent = new AgentController(engine, voice, controlPrefs, (_, _, _) => { tries++; return Task.FromResult(Write(WrongLoop)); });
            var boundedProject = Path.Combine(fixture, "bounded"); Directory.CreateDirectory(boundedProject); persistent.OpenProject(boundedProject);
            var failure = await persistent.SubmitAsync(Request, CancellationToken.None);
            Assert(tries == 3 && persistent.Changes!.Items.Count == 0 && failure.Contains("rechazados"), "Los reintentos persistentes no quedaron acotados sin propuestas.");
            results.Add(new { name = "persistent-invalid-candidates-stop-after-three", tries, passed = true });
            var repeatCalls = 0; var repeatEvents = 0;
            var repeatAgent = new AgentController(engine, voice, new Preferences { ValidateCode = true }, (_, _, _) => { repeatCalls++; return Task.FromResult(Write(Good)); });
            var repeatProject = Path.Combine(fixture, "repeat"); Directory.CreateDirectory(repeatProject); repeatAgent.OpenProject(repeatProject);
            repeatAgent.ChangeProposed += _ => repeatEvents++;
            var repeatReply = await repeatAgent.SubmitAsync(Request, CancellationToken.None);
            Assert(repeatCalls == 2 && repeatEvents == 1 && repeatAgent.Changes!.Items.Count == 1 && repeatReply.Contains("repitió"), "El modelo repitió propuestas idénticas sin detenerse o cambió su ID.");
            repeatAgent.Connections.Dispose(); repeatAgent.Updates.Dispose(); results.Add(new { name = "identical-reviewed-proposal-stops-repeated-generation", passed = true });
            var stalledCalls = 0;
            var stalled = new AgentController(engine, voice, new Preferences { ValidateCode = true }, (_, _, _) => Task.FromResult(++stalledCalls == 1 ? Write(WrongLoop) : JsonSerializer.Serialize(new { action = "edit_file", path = "saludos.py", find = "for", replace = "while", message = "Corrijo" })));
            var stalledProject = Path.Combine(fixture, "stalled"); Directory.CreateDirectory(stalledProject); stalled.OpenProject(stalledProject);
            await stalled.SubmitAsync(Request, CancellationToken.None);
            Assert(stalledCalls == 3 && stalled.Changes!.Items.Count == 0, "La reparación se atascó en edit_file sobre un archivo inexistente.");
            stalled.Connections.Dispose(); stalled.Updates.Dispose(); results.Add(new { name = "failed-edit-of-rejected-new-file-is-bounded", passed = true });
            var prematureCalls = 0;
            var premature = new AgentController(engine, voice, new Preferences { ValidateCode = true }, (_, _, _) => Task.FromResult(++prematureCalls == 1 ? Finish : prematureCalls == 2 ? Write(Good) : Finish));
            var prematureProject = Path.Combine(fixture, "premature"); Directory.CreateDirectory(prematureProject); premature.OpenProject(prematureProject);
            await premature.SubmitAsync("Haz un programa en Python que pregunte con input cuántos amigos quiere saludar y repite tantas veces el nombre con input y saludo con print; primero usa un bloque while.", CancellationToken.None);
            Assert(prematureCalls == 3 && premature.Changes!.Items.Single().Validation?.Status == "passed", "finish permitió saltarse la propuesta y su revisión sin nombre de archivo explícito.");
            premature.Connections.Dispose(); premature.Updates.Dispose(); results.Add(new { name = "finish-cannot-bypass-review-for-script-without-explicit-filename", passed = true });
            var editCalls = 0; var editEvents = 0;
            var editAgent = new AgentController(engine, voice, new Preferences { ValidateCode = true }, (_, _, _) =>
            {
                editCalls++;
                return Task.FromResult(editCalls == 1 ? JsonSerializer.Serialize(new { action = "edit_file", path = "saludos.py", find = "while contador < cantidad:", replace = "while contador < cantidad", message = "Edito" }) : editCalls == 2 ? JsonSerializer.Serialize(new { action = "edit_file", path = "saludos.py", find = "Hola,", replace = "Saludos,", message = "Corrijo" }) : Finish);
            });
            var editProject = Path.Combine(fixture, "edit"); Directory.CreateDirectory(editProject); File.WriteAllText(Path.Combine(editProject, "saludos.py"), Good);
            editAgent.OpenProject(editProject); editAgent.ActiveFile = "saludos.py";
            editAgent.ChangeProposed += change => { editEvents++; Assert(change.Validation?.Status == "passed", "edit_file mostró código inválido."); };
            await editAgent.SubmitAsync(Request.Replace("Crea", "Corrige") + " En el archivo abierto.", CancellationToken.None);
            Assert(editCalls == 3 && editEvents == 1 && editAgent.Changes!.Items.Single().After == Good.Replace("Hola,", "Saludos,") && File.ReadAllText(Path.Combine(editProject, "saludos.py")) == Good, "edit_file no revisó el archivo completo antes del diff.");
            results.Add(new { name = "edit-file-validates-complete-result-and-repairs", passed = true });
            editAgent.Connections.Dispose(); editAgent.Updates.Dispose();
            var artifactCalls = 0;
            var artifactAgent = new AgentController(engine, voice, new Preferences { ValidateCode = true }, (_, _, _) =>
                Task.FromResult(++artifactCalls <= 2 ? JsonSerializer.Serialize(new { action = "artifact_propose", path = "data.json", format = "json", content = artifactCalls == 1 ? "{\"value\":}" : "{\"value\":2}", skill = "code", message = "Documento" }) : Finish));
            var artifactProject = Path.Combine(fixture, "artifact"); Directory.CreateDirectory(artifactProject); artifactAgent.OpenProject(artifactProject);
            await artifactAgent.SubmitAsync("Crea data.json con value igual a 2", CancellationToken.None);
            Assert(artifactCalls == 3 && artifactAgent.Changes!.Items.Single().Validation?.Status == "passed" && !File.Exists(Path.Combine(artifactProject, "data.json")), "artifact_propose eludió la revisión de JSON.");
            results.Add(new { name = "artifact-json-cannot-bypass-validation", passed = true });
            artifactAgent.Connections.Dispose(); artifactAgent.Updates.Dispose();
            File.WriteAllText(Path.Combine(boundedProject, "existing.py"), Good);
            var syntax = await persistent.SubmitAsync("Comprueba la sintaxis de existing.py", CancellationToken.None);
            Assert(syntax.Contains("Sintaxis revisada") && tries == 3, "La comprobación humana no llamó al analizador real sin IA.");
            await persistent.SubmitAsync("Desactiva la revisión de código", CancellationToken.None);
            Assert(!controlPrefs.ValidateCode && !Preferences.Load().ValidateCode && !controlPrefs.AllowChecks, "La desactivación humana no persistió o concedió permisos.");
            await persistent.ControlAsync("Lee literalmente Activa la revisión de código", CancellationToken.None);
            await persistent.ControlAsync("No activa la revisión de código", CancellationToken.None);
            Assert(!controlPrefs.ValidateCode, "Una cita o negación habilitó el ajuste.");
            await persistent.SubmitAsync("Activa la revisión de código", CancellationToken.None);
            var state = await persistent.SubmitAsync("Estado de la revisión de código", CancellationToken.None);
            Assert(controlPrefs.ValidateCode && state.Contains("validation_status") && !controlPrefs.AllowChecks, "Texto/dictado no conecta la capacidad y sus herramientas.");
            persistent.Skills.SetEnabled("code", false);
            try { await persistent.SubmitAsync("Desactiva la revisión de código", CancellationToken.None); throw new IOException("Una skill desactivada se ignoró."); }
            catch (InvalidOperationException) { }
            Assert(controlPrefs.ValidateCode, "Se alteró la revisión con code desactivada.");
            persistent.Skills.SetEnabled("code", true);
            var statusCalls = 0;
            var stateAgent = new AgentController(engine, voice, controlPrefs, (_, _, _) => Task.FromResult(++statusCalls == 1 ? "{\"action\":\"validation_status\",\"enabled\":false,\"message\":\"Consulto estado\"}" : Finish));
            stateAgent.OpenProject(boundedProject); await stateAgent.SubmitAsync("Usa validation_status", CancellationToken.None);
            Assert(controlPrefs.ValidateCode && !controlPrefs.AllowChecks, "El modelo cambió el ajuste o el permiso de ejecución.");
            var validateCalls = 0; string? toolReport = null;
            var validateAgent = new AgentController(engine, voice, controlPrefs, (_, _, _) => Task.FromResult(++validateCalls == 1 ? JsonSerializer.Serialize(new { action = "validate_code", path = "existing.py", content = Good, message = "Reviso sintaxis" }) : Finish));
            validateAgent.OpenProject(boundedProject); validateAgent.ToolResult += (tool, result) => { if (tool == "validate_code") toolReport = result; };
            await validateAgent.SubmitAsync("Revisa el código sin guardarlo", CancellationToken.None);
            Assert(toolReport is not null && toolReport.Contains("passed") && validateAgent.Changes!.Items.Count == 0, "La herramienta real validate_code no quedó conectada.");
            persistent.Connections.Dispose(); persistent.Updates.Dispose(); stateAgent.Connections.Dispose(); stateAgent.Updates.Dispose(); validateAgent.Connections.Dispose(); validateAgent.Updates.Dispose();
            results.Add(new { name = "human-text-and-accepted-dictation-controls-model-readonly-tools-and-disabled-skill", passed = true });
            report["status"] = "complete"; report["passed"] = results.Count; report["fixtureHome"] = fixture;
            report["installedPreferencesPreserved"] = true; report["installedModelProfilePreserved"] = true;
            return 0;
        }
        catch (Exception e) { report["status"] = "failed"; report["error"] = e.ToString(); return 1; }
        finally { Environment.SetEnvironmentVariable("SHEEPCODE_HOME", environment); AppPaths.SaveJson(output, report); }
    }
}
