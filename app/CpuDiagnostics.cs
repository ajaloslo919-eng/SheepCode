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
            var granite = engine.Profile.ModelId is "granite-4.0-h-350m" or "granite-4.0-h-1b";
            if (data.GetProperty("backend").GetString() != (granite ? "hybrid-cpu" : "dense-cpu") || data.GetProperty("compiled_avx").GetInt32() != 0 || data.GetProperty("compiled_avx2").GetInt32() != 0 ||
                data.GetProperty("max_context").GetInt32() != 4096 || data.GetProperty("threads").GetInt32() > 2 ||
                data.GetProperty("q8_kernel").GetString() != "SSE2" || !data.GetProperty("q8_kernel_verified").GetBoolean()) throw new IOException("El motor activo no verificó CPU sin AVX, cálculo Q8 SSE2 y el presupuesto previsto.");
            using var status = JsonDocument.Parse(await agent.SubmitAsync("Estado del modelo", timeout.Token));
            if (status.RootElement.GetProperty("engine").GetProperty("lowMemoryCpu").GetProperty("memoryLimitMiB").GetInt32() != 1536) throw new IOException("La entrada compartida texto/dictado no informó del presupuesto.");
            report["health"] = data.Clone(); report["modelStatus"] = status.RootElement.Clone();
            if (engine.Profile.ModelId == "granite-4.0-h-1b" && (data.GetProperty("quantization").GetString() != "Q4_K_M" ||
                data.GetProperty("parameters").GetUInt64() < 1400000000UL || data.GetProperty("parameters").GetUInt64() > 1600000000UL))
                throw new IOException("La arquitectura integrada no verificó el Granite de 1,5B Q4_K_M seleccionado.");
            using var templateResponse = await http.PostAsync(new Uri(EngineHost.Endpoint, "apply-template"), new StringContent("{\"messages\":[{\"role\":\"assistant\",\"content\":\"{\\\"action\\\":\\\"finish\\\"}\"},{\"role\":\"user\",\"content\":\"hola\"}]}", Encoding.UTF8, "application/json"), timeout.Token);
            using var templateData = JsonDocument.Parse(await templateResponse.Content.ReadAsStringAsync(timeout.Token));
            var template = templateData.RootElement.GetProperty("prompt").GetString()!;
            var qwen2 = engine.Profile.ModelId == "qwen2.5-coder-0.5b";
            var expectedPrefix = granite ? "<|start_of_role|>assistant<|end_of_role|>" : qwen2 ? "<|im_start|>assistant\n" : "<|im_start|>assistant\n<think>\n\n</think>\n\n";
            if (data.GetProperty("model").GetString() != engine.Profile.ModelId || data.GetProperty("architecture").GetString() != (granite ? "granitehybrid" : qwen2 ? "qwen2" : "qwen3") ||
                template.Contains("<think>") != (!granite && !qwen2) || !template.EndsWith(expectedPrefix) ||
                granite && (template.Contains("<|im_start|>") || data.GetProperty("chat_template").GetString() != "granite-4.0"))
                throw new IOException("La plantilla del motor no corresponde al modelo seleccionado.");
            report["template"] = new { matchesSelectedArchitecture = true, granite, qwen2ChatMl = qwen2, thinkingDisabled = true };
            var shortMessages = new List<object> { new { role = "user", content = "Responde solo con un objeto JSON que diga hola." } };
            using var counted = await http.PostAsync(new Uri(EngineHost.Endpoint, "prompt-count"), new StringContent(JsonSerializer.Serialize(new { messages = shortMessages }), Encoding.UTF8, "application/json"), timeout.Token);
            using var countData = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(timeout.Token));
            async Task<JsonDocument> Generate() {
                using var request = new StringContent(JsonSerializer.Serialize(new { messages = shortMessages, max_tokens = 48, temperature = 0.15, response_format = new { type = "json_object" } }), Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(new Uri(EngineHost.Endpoint, "v1/chat/completions"), request, timeout.Token);
                response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            }
            using var first = await Generate(); var firstData = first.RootElement;
            var firstPrompt = firstData.GetProperty("usage").GetProperty("prompt_tokens").GetInt32();
            if (firstPrompt != countData.RootElement.GetProperty("count").GetInt32()) throw new IOException("El conteo nativo no coincide con la inferencia real.");
            var assistant = firstData.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (firstData.GetProperty("choices")[0].GetProperty("finish_reason").GetString() == "length") throw new IOException("La acción de caché quedó incompleta.");
            shortMessages.Add(new { role = "assistant", content = assistant }); shortMessages.Add(new { role = "user", content = "Ahora responde un objeto JSON que diga adiós." });
            using var second = await Generate(); var secondData = second.RootElement;
            var prefix = secondData.GetProperty("strata").GetProperty("cached_prompt_tokens").GetInt32();
            if (prefix < firstPrompt + firstData.GetProperty("usage").GetProperty("completion_tokens").GetInt32() - 1) throw new IOException("El siguiente paso descartó los tokens del asistente ya procesados.");
            report["prefixReuse"] = new { first = firstData.Clone(), second = secondData.Clone(), exactCount = true, includesPreviousAssistant = true, scope = "Componente activo instalado, modelo seleccionado " + engine.Profile.ModelId + "; sin herramientas del agente ni GUI." };
            if (granite)
            {
                shortMessages.Clear(); shortMessages.Add(new { role = "user", content = "Responde solo un objeto JSON con la palabra nuevo." });
                using var reset = await Generate();
                if (reset.RootElement.GetProperty("strata").GetProperty("cached_prompt_tokens").GetInt32() != 0 ||
                    data.GetProperty("prefix_cache_mode").GetString() != "append-or-reset") throw new IOException("El estado recurrente no se reinició al cambiar el historial.");
                report["hybridHistoryReset"] = reset.RootElement.Clone();
            }
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
