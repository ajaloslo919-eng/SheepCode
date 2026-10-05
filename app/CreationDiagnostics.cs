namespace SheepCode;

internal static class CreationDiagnostics
{
    internal static async Task<int> RunAsync(bool existingFile = false)
    {
        var reportPath = Path.Combine(AppPaths.Root, "checks", existingFile ? "preflight-agent-result.json" : "creation-result.json");
        var report = new Dictionary<string, object?> { ["status"] = "starting", ["scope"] = "Comprobación funcional del agente y motor activos de la instalación; sin comparativas de rendimiento.", ["installedExecutable"] = Environment.ProcessPath };
        void Save() => AppPaths.SaveJson(reportPath, report);
        Save(); var saved = Preferences.Load(); var original = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice); var agent = new AgentController(engine, voice, saved);
        try
        {
            saved.ReadAloud = false;
            var project = Path.Combine(AppPaths.Root, "checks", "create-project-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "README.md"), "# Prueba de creación de un archivo\n");
            const string previous = "for i in range(5):\n    print(882)\n";
            if (existingFile) File.WriteAllText(Path.Combine(project, "saludos.py"), previous);
            agent.OpenProject(project); agent.Output += (role, text) => File.AppendAllText(Path.Combine(AppPaths.Logs, "creation-check.log"), role + ": " + text + "\n");
            if (existingFile) agent.ActiveFile = "saludos.py";
            engine.Progress += p => { report["phase"] = p; Save(); };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var file = existingFile ? "saludos.py" : "hello.py";
            var reply = await agent.SubmitAsync(existingFile ? "En el archivo abierto saludos.py, deja un programa Python que pregunte con input a cuántos amigos quiere saludar el usuario. Usa un bloque while para repetir esa cantidad de veces: preguntar el nombre con input y saludar a ese amigo con print. Prepara el código completo como propuesta y termina para que lo revise. No apliques ni ejecutes el archivo." : "Crea hello.py con una única línea exacta: print('Hola Sheep y Kuky'). El archivo no existe. Usa write_file con content y termina para que revise la propuesta. No apliques ni ejecutes el archivo.", timeout.Token);
            var proposal = agent.Changes!.Items.Single(c => c.Path == file && c.Status == "pending");
            if (existingFile ? !agent.LastActions.Contains("read_file") || !proposal.After.Contains("while") || File.ReadAllText(Path.Combine(project, file)) != previous : !agent.LastActions.Contains("write_file") || proposal.After.Trim() != "print('Hola Sheep y Kuky')" || File.Exists(Path.Combine(project, file))) throw new IOException("No se produjo una propuesta válida sin modificar el archivo.");
            var actions = agent.LastActions.ToArray(); await agent.SubmitAsync("Aplica el cambio " + proposal.Id, timeout.Token);
            if (File.ReadAllText(Path.Combine(project, file)) != proposal.After) throw new IOException("No se aplicó la propuesta aprobada.");
            if (existingFile)
            {
                File.WriteAllText(Path.Combine(project, "test_saludos.py"), "import ast, contextlib, io, pathlib, runpy, unittest\nfrom unittest.mock import patch\nclass SaludosTest(unittest.TestCase):\n    def test_while(self):\n        tree = ast.parse(pathlib.Path('saludos.py').read_text(encoding='utf-8-sig'))\n        self.assertTrue(any(isinstance(n, ast.While) for n in ast.walk(tree)))\n    def test_input_and_greetings(self):\n        for names in ([], ['Ana', 'Luis'], ['Sheep', 'Kuky', 'Eva', 'Max', 'Zoe']):\n            with self.subTest(names=names):\n                output = io.StringIO()\n                with patch('builtins.input', side_effect=[str(len(names))] + names) as reader, contextlib.redirect_stdout(output):\n                    runpy.run_path('saludos.py', run_name='__main__')\n                self.assertEqual(reader.call_count, 1 + len(names))\n                greetings = [s for s in output.getvalue().splitlines() if s.strip()]\n                self.assertEqual(len(greetings), len(names))\n                for name, greeting in zip(names, greetings):\n                    self.assertIn(name, greeting)\n");
                var result = await new ChecksRunner(agent.Workspace!).RunAsync("python-test", true, null, timeout.Token);
                if (result.ExitCode != 0) throw new IOException("El programa no pasó las entradas 0, 2 y 5 amigos: " + result.Output);
                report["pythonCheck"] = result;
                var artifactReply = await agent.SubmitAsync("Usa $spreadsheets. Prepara gastos.xlsx, que no existe, con artifact_propose. format es xlsx, skill es spreadsheets y content es una cadena JSON con sheet Gastos y rows [[\"Concepto\",\"Importe\"],[\"Luz\",30]]. Prepara la propuesta y termina; no la apliques.", timeout.Token);
                var artifact = agent.Changes.Items.Single(c => c.Path == "gastos.xlsx" && c.Status == "pending"); var artifactActions = agent.LastActions.ToArray();
                if (artifact.ArtifactFormat != "xlsx" || !artifactActions.Contains("artifact_propose") || File.Exists(Path.Combine(project, "gastos.xlsx"))) throw new IOException("El agente no preparó el XLSX como propuesta revisable.");
                await agent.SubmitAsync("Aplica el cambio " + artifact.Id, timeout.Token);
                var artifactRead = await agent.SubmitAsync("Lee el documento gastos.xlsx", timeout.Token);
                if (!artifactRead.Contains("Luz") || !artifactRead.Contains("B2=30")) throw new IOException("El XLSX del agente no conserva los datos solicitados.");
                report["artifactCheck"] = new { path = Path.Combine(project, "gastos.xlsx"), artifactActions, reply = artifactReply, read = artifactRead, appliedBy = "Petición humana del arnés de pruebas, en proyecto propio" };
            }
            report["status"] = "complete"; report["project"] = project; report["reply"] = reply; report["actions"] = actions; report["proposal"] = proposal; report["engine"] = engine.Snapshot(); report["completed"] = DateTimeOffset.UtcNow; Save(); return 0;
        }
        catch (Exception e) { report["status"] = "failed"; report["error"] = e.ToString(); Save(); return 1; }
        finally { if (original is not null) File.WriteAllBytes(Preferences.PathName, original); }
    }
}
