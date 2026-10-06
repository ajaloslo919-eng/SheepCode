using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SheepCode.Distribution;

namespace SheepCode;

internal static class CpuDiagnostics
{
    internal static async Task<int> RunAsync()
    {
        var report = new Dictionary<string, object?> { ["status"] = "starting", ["scope"] = "Motor Strata CPU seleccionado y activo en esta instalación. Herramientas/control y cancelación reales; sin modelos alternativos ni comparativas.", ["executable"] = Environment.ProcessPath };
        var path = Path.Combine(AppPaths.Root, "checks", "strata-cpu-controls.json");
        void Save() => AppPaths.SaveJson(path, report);
        var previous = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
        try
        {
            if (engine.Profile.Kind != "strata-cpu") throw new IOException("Esta comprobación solo ejecuta el perfil Strata CPU instalado y seleccionado.");
            var prefs = Preferences.Load(); prefs.ReadAloud = false;
            var agent = new AgentController(engine, voice, prefs); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await agent.SubmitAsync("Activa la skill models", timeout.Token);
            await agent.SubmitAsync("Activa el modo ahorro", timeout.Token);
            await agent.SubmitAsync("Activa el motor", timeout.Token);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            using var health = JsonDocument.Parse(await http.GetStringAsync(new Uri(EngineHost.Endpoint, "health"), timeout.Token));
            var data = health.RootElement;
            if (data.GetProperty("backend").GetString() != "dense-cpu" || data.GetProperty("compiled_avx").GetInt32() != 0 || data.GetProperty("compiled_avx2").GetInt32() != 0 ||
                data.GetProperty("max_context").GetInt32() != 4096 || data.GetProperty("threads").GetInt32() > 2) throw new IOException("El motor activo no verificó CPU sin AVX y el presupuesto previsto.");
            using var status = JsonDocument.Parse(await agent.SubmitAsync("Estado del modelo", timeout.Token));
            if (status.RootElement.GetProperty("engine").GetProperty("lowMemoryCpu").GetProperty("memoryLimitMiB").GetInt32() != 1536) throw new IOException("La entrada compartida texto/dictado no informó del presupuesto.");
            report["health"] = data.Clone(); report["modelStatus"] = status.RootElement.Clone();
            using var huge = new StringContent(JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = string.Join(' ', Enumerable.Repeat("hola", 5000)) } }, max_tokens = 512 }), Encoding.UTF8, "application/json");
            using var rejected = await http.PostAsync(new Uri(EngineHost.Endpoint, "v1/chat/completions"), huge, timeout.Token);
            var rejection = await rejected.Content.ReadAsStringAsync(timeout.Token);
            if ((int)rejected.StatusCode != 400 || !rejection.Contains("exceed_context_size_error")) throw new IOException("No se rechazó el contexto excesivo antes de generar.");
            report["oversizedContext"] = new { rejected = true, response = rejection };
            using var body = new StringContent(JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = "Cuenta todos los números del uno al mil y explica cada uno." } }, max_tokens = 1536, temperature = 0.15 }), Encoding.UTF8, "application/json");
            var running = http.PostAsync(new Uri(EngineHost.Endpoint, "v1/chat/completions"), body, timeout.Token);
            await Task.Delay(1500, timeout.Token);
            var clock = Stopwatch.StartNew(); using var cancel = await http.PostAsync(new Uri(EngineHost.Endpoint, "cancel"), new StringContent("{}"), timeout.Token);
            using var cancelled = await running.WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
            var cancelledBody = await cancelled.Content.ReadAsStringAsync(timeout.Token);
            if (!cancel.IsSuccessStatusCode || cancelled.IsSuccessStatusCode || !cancelledBody.Contains("generation cancelled")) throw new IOException("La cancelación nativa no interrumpió el cómputo.");
            report["cancel"] = new { seconds = clock.Elapsed.TotalSeconds, response = cancelledBody, engineAlive = engine.Ready };
            report["afterCancel"] = engine.Snapshot();
            await agent.SubmitAsync("Desactiva el motor", timeout.Token);
            if (engine.Ready || engine.State != "Sin iniciar") throw new IOException("Desactivar el motor no terminó su proceso propio.");
            await agent.SubmitAsync("Desactiva el modo ahorro", timeout.Token);
            if (Preferences.Load().PortableMode != "performance") throw new IOException("No se desactivó el ajuste por entrada humana.");
            report["adjustDisable"] = true; report["status"] = "complete"; Save(); return 0;
        }
        catch (Exception e) { report["status"] = "failed"; report["error"] = e.ToString(); Save(); return 1; }
        finally { if (previous is not null) File.WriteAllBytes(Preferences.PathName, previous); else if (File.Exists(Preferences.PathName)) File.Delete(Preferences.PathName); }
    }
}
