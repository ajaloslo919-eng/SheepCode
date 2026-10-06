using System.Text.Json;
using SheepCode.Distribution;

namespace SheepCode;

internal static class PortableDiagnostics
{
    internal static async Task<int> RunAsync()
    {
        var path = Path.Combine(AppPaths.Root, "checks", "laptop-components.json");
        var saved = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        var checks = new List<object>();
        try
        {
            checks.AddRange(PortableChecks.Run());
            var prefs = Preferences.Load(); await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
            var agent = new AgentController(engine, voice, prefs);
            await agent.SubmitAsync("Activa la skill models", CancellationToken.None);
            await agent.SubmitAsync("Activa el modo ahorro", CancellationToken.None);
            if (Preferences.Load().PortableMode != "eco") throw new IOException("No se guardó el modo ahorro.");
            using (var status = JsonDocument.Parse(await agent.SubmitAsync("Estado del portátil", CancellationToken.None)))
                if (status.RootElement.GetProperty("mode").GetString() != "eco") throw new IOException("El diálogo no entregó el modo real.");
            await agent.SubmitAsync("Desactiva el modo ahorro", CancellationToken.None);
            if (Preferences.Load().PortableMode != "performance") throw new IOException("No se desactivó el ahorro.");
            await agent.SubmitAsync("Modo portátil automático", CancellationToken.None);
            if (Preferences.Load().PortableMode != "auto") throw new IOException("No se restauró el modo automático.");
            if (await agent.ControlAsync("No actives el modo ahorro", CancellationToken.None) is not null || await agent.ControlAsync("Lee literalmente: Activa el modo ahorro", CancellationToken.None) is not null)
                throw new IOException("El texto literal cambió el modo portátil.");
            await agent.SubmitAsync("Desactiva la skill models", CancellationToken.None);
            var blocked = false; try { await agent.ControlAsync("Activa el modo ahorro", CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
            if (!blocked) throw new IOException("La función ignoró la skill desactivada.");
            using var action = JsonDocument.Parse("{\"action\":\"portable_status\"}");
            if (ModelProtocol.ValidateAction(action.RootElement) is not null || !agent.Capabilities.Any(c => c.Name == "portable")) throw new IOException("Falta registrar la herramienta o la capacidad portátil.");
            checks.Add(new { check = "shared-text-accepted-dictation-mode-config-disable-and-permissions", passed = true });
            AppPaths.SaveJson(path, new { status = "complete", scope = "Fixtures de portátiles y controles del programa instalado; no usa modelos alternativos, batería física ni benchmarks.", checks, actualHardware = HardwareScanner.Scan(AppPaths.ModelRoot), installedExecutable = Environment.ProcessPath }); return 0;
        }
        catch (Exception e) { AppPaths.SaveJson(path, new { status = "failed", checks, error = e.ToString() }); return 1; }
        finally { if (saved is not null) File.WriteAllBytes(Preferences.PathName, saved); else if (File.Exists(Preferences.PathName)) File.Delete(Preferences.PathName); }
    }
}
