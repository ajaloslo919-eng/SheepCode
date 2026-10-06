using System.Text.Json;

namespace SheepCode;

internal static class FastCpuDiagnostics
{
    internal static async Task<int> RunAsync()
    {
        var path = Path.Combine(AppPaths.Root, "checks", "fast-cpu-workflow.json");
        var report = new Dictionary<string, object?> { ["status"] = "starting", ["scope"] = "Pruebas controladas del flujo y permisos; respuestas del modelo simuladas, sin medidas de rendimiento de IA." };
        var original = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
        try
        {
            if (engine.Profile.Kind != "strata-cpu") throw new IOException("Este control requiere el perfil CPU seleccionado.");
            var prefs = Preferences.Load(); prefs.ReadAloud = false; prefs.FastCpuMode = true; prefs.DisabledSkills.RemoveAll(s => s is "models" or "code");
            var project = Path.Combine(AppPaths.Root, "checks", "fast-project-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(project);
            var file = Path.Combine(project, "sample.py"); File.WriteAllText(file, "value = 1\n");
            var calls = 0;
            var agent = new AgentController(engine, voice, prefs, (messages, _, _) => {
                calls++;
                if (calls == 1)
                {
                    if (!messages.Any(m => m.Content.Contains("Raw file sample.py:\nvalue = 1"))) throw new IOException("Falta la lectura antes de la primera acción.");
                    return Task.FromResult("{\"action\":\"edit_file\",\"path\":\"sample.py\",\"find\":\"value = 1\",\"replace\":\"value = 2\",\"message\":\"Propongo el cambio.\"}");
                }
                return Task.FromResult("{\"action\":\"finish\",\"message\":\"Propuesta lista.\"}");
            });
            agent.OpenProject(project); agent.ActiveFile = "sample.py";
            await agent.SubmitAsync("En el archivo abierto sample.py, cambia value a 2 y prepara la propuesta.", CancellationToken.None);
            var proposed = agent.Changes!.Items.Single(c => c.Status == "pending");
            if (proposed.After != "value = 2\n" || File.ReadAllText(file) != "value = 1\n" || agent.LastActions.Count(s => s == "read_file") != 1) throw new IOException("La prelectura no conservó una propuesta revisable.");
            // A concurrent edit after the pre-read must be read again and retained.
            agent.Changes.Reject(proposed.Id); calls = 0;
            var concurrent = new AgentController(engine, voice, prefs, (messages, _, _) => {
                calls++;
                if (calls == 1) { File.WriteAllText(file, "value = 7\n"); return Task.FromResult("{\"action\":\"edit_file\",\"path\":\"sample.py\",\"find\":\"value = 1\",\"replace\":\"value = 3\",\"message\":\"Cambio.\"}"); }
                if (calls == 2)
                {
                    if (!messages.Any(m => m.Content.Contains("Raw file sample.py:\nvalue = 7"))) throw new IOException("No se releyó la edición concurrente.");
                    return Task.FromResult("{\"action\":\"edit_file\",\"path\":\"sample.py\",\"find\":\"value = 7\",\"replace\":\"value = 3\",\"message\":\"Propongo sobre la versión actual.\"}");
                }
                return Task.FromResult("{\"action\":\"finish\",\"message\":\"Propuesta lista.\"}");
            });
            concurrent.OpenProject(project); concurrent.ActiveFile = "sample.py";
            await concurrent.SubmitAsync("En el archivo abierto sample.py, cambia value a 3.", CancellationToken.None);
            var updated = concurrent.Changes!.Items.Single(c => c.Status == "pending");
            if (updated.Before != "value = 7\n" || updated.After != "value = 3\n" || File.ReadAllText(file) != "value = 7\n") throw new IOException("Se perdió la edición concurrente.");
            await concurrent.SubmitAsync("Desactiva el modo rápido", CancellationToken.None);
            if (Preferences.Load().FastCpuMode) throw new IOException("El modo rápido no se desactivó.");
            await concurrent.ControlAsync("Lee literalmente Activa el modo rápido", CancellationToken.None);
            if (prefs.FastCpuMode) throw new IOException("Una cita literal alteró la configuración.");
            await concurrent.SubmitAsync("Activa el modo rápido", CancellationToken.None);
            using var state = JsonDocument.Parse(await concurrent.SubmitAsync("Estado del rendimiento", CancellationToken.None));
            if (!state.RootElement.GetProperty("active").GetBoolean()) throw new IOException("La entrada de texto/dictado no informó del modo activo.");
            concurrent.Skills.SetEnabled("models", false);
            try { await concurrent.SubmitAsync("Desactiva el modo rápido", CancellationToken.None); throw new IOException("Se ignoró la skill models desactivada."); }
            catch (InvalidOperationException) { }
            report["status"] = "complete"; report["preReadBeforeInference"] = true; report["concurrentEditPreserved"] = true;
            report["humanToggle"] = true; report["disabledModelsGuard"] = true; report["literalGuard"] = true;
            AppPaths.SaveJson(path, report); return 0;
        }
        catch (Exception e) { report["status"] = "failed"; report["error"] = e.ToString(); AppPaths.SaveJson(path, report); return 1; }
        finally { if (original is not null) File.WriteAllBytes(Preferences.PathName, original); else if (File.Exists(Preferences.PathName)) File.Delete(Preferences.PathName); }
    }
}
