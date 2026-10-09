using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SheepCode.Distribution;

namespace SheepCode;

internal sealed class EngineHost(NeuralVoice voice, HttpClient? transport = null, Process? ownedServer = null, RuntimeProfile? transportProfile = null) : IAsyncDisposable
{
    internal static Uri Endpoint => new($"http://127.0.0.1:{RuntimeProfile.Load(AppPaths.Root).Port}/");
    internal RuntimeProfile Profile => transportProfile is null ? RuntimeProfile.Load(AppPaths.Root) : transport is not null ? transportProfile : throw new InvalidOperationException("El perfil simulado requiere un transporte inyectado.");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = transport ?? new() { Timeout = TimeSpan.FromMinutes(20) };
    private Process? _server = ownedServer;
    private int? _reportedContext;
    private bool _tokenizerUnavailable;
    internal string State { get; private set; } = "Sin iniciar";
    internal string? LastLoadError { get; private set; }
    internal ModelInstallation InstallationStatus() => ModelInstallationInspector.Inspect(AppPaths.Root, Profile);
    internal string Model { get; private set; } = transportProfile?.ModelId ?? RuntimeProfile.Load(AppPaths.Root).ModelId;
    internal object? LastGeneration { get; private set; }
    internal List<object> Generations { get; } = [];
    internal LaunchPolicy? ActiveLaunch { get; private set; }
    internal int EffectiveContext => Math.Min(_reportedContext ?? int.MaxValue, ActiveLaunch?.Context ?? LaunchPolicy.For(Profile, Preferences.Load().PortableMode, PowerScanner.Read()).Context);
    internal bool Ready => _server is { HasExited: false } && State == "Preparado";
    internal event Action<string>? Progress;
    private void SetState(string state) { State = state; Progress?.Invoke(state); }
    internal JsonElement? Peer()
    {
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(AppPaths.PeerStatus));
            using var process = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32());
            var exe = Path.Combine(AppPaths.Runtime, "engine", "strata-dual.exe");
            if (process.HasExited || !string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return null;
            return data.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    internal object Snapshot() => new { state = State, ready = Ready, model = Ready ? Model : Profile.ModelId, endpoint = Endpoint.ToString(),
        profile = Profile.Kind, configured = Profile.Configured, configuredGpu = Profile.DeviceDescription, lastLoadError = LastLoadError,
        contextTokens = EffectiveContext, configuredContextTokens = Profile.Context, activeLaunch = ActiveLaunch, portable = PortableStatus(), rx580Execution = Profile.Kind == "strata-dual" ? Peer() : null,
        lowMemoryCpu = CpuMemory(), lastGeneration = LastGeneration, voice = voice.Snapshot(), automaticFallback = false };
    private object? CpuMemory()
    {
        if (Profile.Kind != "strata-cpu") return null;
        long? resident = null, peak = null, committed = null;
        try
        {
            if (_server is { HasExited: false }) { _server.Refresh(); resident = _server.WorkingSet64; peak = _server.PeakWorkingSet64; committed = _server.PrivateMemorySize64; }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return new { isaFloor = "x64/SSE2", memoryLimitMiB = Profile.MemoryMiB, residentBytes = resident, peakResidentBytes = peak,
            privateCommittedBytes = committed, reasoning = "none", scope = "Proceso propio Strata CPU; el límite Windows cubre memoria privada, no todas las páginas mapeadas ni la GUI/pestañas/Windows." };
    }
    internal object PortableStatus()
    {
        var power = PowerScanner.Read(); var mode = Preferences.Load().PortableMode;
        return new { power, mode, nextLaunch = LaunchPolicy.For(Profile, mode, power), activeLaunch = ActiveLaunch,
            scope = "Límites aplicados al cargar la IA y prioridad de su proceso propio; no modifica Windows, el modelo ni la voz RX 580." };
    }
    internal void RefreshPowerPriority()
    {
        var server = _server; if (server is null) return;
        try { if (server.HasExited) return; var saving = LaunchPolicy.IsSaving(Preferences.Load().PortableMode, PowerScanner.Read()); server.PriorityClass = saving ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal async Task EnsureAsync(CancellationToken token)
    {
        if (transportProfile is not null) throw new InvalidOperationException("El transporte simulado no puede iniciar motores.");
        await _gate.WaitAsync(token);
        try
        {
            if (Ready) return;
            var profile = Profile;
            LastLoadError = null;
            var installation = ModelInstallationInspector.Inspect(AppPaths.Root, profile);
            if (!installation.FilesReady) throw new FileNotFoundException(installation.Message);
            if (!profile.NativeExecutable && (!File.Exists(AppPaths.EngineConfig) || !File.Exists(AppPaths.StrataPython)))
                throw new FileNotFoundException("Falta el runtime de Strata configurado. Repara la instalación desde el setup.");
            if (profile.Kind != "strata-cpu" && Process.GetProcessesByName("VrcLocalCompanion").Any(p => !p.HasExited))
                throw new InvalidOperationException("Cierra SheepGPT antes de cargar SheepCode: ambas aplicaciones necesitan la memoria de estas GPU.");
            if (profile.RequireRx580Voice) { SetState("Preparando voz RX 580…"); await voice.EnsureAsync(token); }
            if (_server is null || _server.HasExited)
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, profile.Port);
                try { listener.Start(); } catch (System.Net.Sockets.SocketException) { throw new InvalidOperationException($"El puerto privado {profile.Port} está ocupado. Cierra la otra instancia o ajusta el perfil."); } finally { listener.Stop(); }
                if (profile.Kind == "strata-dual" && File.Exists(AppPaths.PeerStatus)) File.Delete(AppPaths.PeerStatus);
                ActiveLaunch = LaunchPolicy.For(profile, Preferences.Load().PortableMode, PowerScanner.Read());
                var start = new ProcessStartInfo(profile.NativeExecutable ? InstallationPaths.Resolve(AppPaths.Root, profile.Executable) : AppPaths.StrataPython)
                {
                    WorkingDirectory = profile.NativeExecutable ? Path.GetDirectoryName(InstallationPaths.Resolve(AppPaths.Root, profile.Executable))! : AppPaths.Runtime, UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                var arguments = profile.Kind == "strata-cpu" ? new[] { "--model", InstallationPaths.Resolve(AppPaths.Root, profile.ModelFile), "--alias", profile.ModelId,
                    "--port", profile.Port.ToString(), "--ctx-size", ActiveLaunch.Context.ToString(), "--threads", ActiveLaunch.Threads.ToString(), "--memory-mib", profile.MemoryMiB.ToString() } :
                    profile.Kind == "llama" ? new[] { "--model", InstallationPaths.Resolve(AppPaths.Root, profile.ModelFile), "--alias", profile.ModelId,
                    "--host", "127.0.0.1", "--port", profile.Port.ToString(), "--ctx-size", ActiveLaunch.Context.ToString(), "--parallel", "1", "--threads", ActiveLaunch.Threads.ToString(),
                    "--device", ActiveLaunch.Devices, "--gpu-layers", ActiveLaunch.GpuLayers.ToString(), "--split-mode", "layer", "--jinja", "--reasoning-budget", "256", "--no-webui" } :
                    new[] { "-u", Path.Combine(AppPaths.Runtime, "serve", "server.py"), "--engine", "strata", "--config", AppPaths.EngineConfig, "--host", "127.0.0.1", "--port", profile.Port.ToString() };
                foreach (var arg in arguments) start.ArgumentList.Add(arg);
                start.Environment["PYTHONUTF8"] = "1";
                var logPath = Path.Combine(AppPaths.Logs, "strata-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
                var logGate = new object();
                void Log(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data is null) return;
                    lock (logGate) File.AppendAllText(logPath, args.Data + Environment.NewLine);
                }
                _server = new Process { StartInfo = start };
                _server.OutputDataReceived += Log; _server.ErrorDataReceived += Log;
                if (!_server.Start()) throw new InvalidOperationException("No se pudo iniciar Strata.");
                RefreshPowerPriority();
                _server.BeginOutputReadLine(); _server.BeginErrorReadLine();
            }
            SetState("Cargando " + profile.Label + "…");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(3))
            {
                token.ThrowIfCancellationRequested();
                if (_server.HasExited) throw new InvalidOperationException("El motor terminó al cargar. Consulta los registros de SheepCode.");
                if (await HealthAsync(token) is { } health && (profile.Kind == "llama" || health.GetProperty("loaded").GetBoolean()))
                {
                    if (profile.Kind == "strata-dual")
                    {
                        var peer = Peer() ?? throw new InvalidOperationException("No se verificó el proceso del backend RX 580.");
                        if (peer.GetProperty("vendorId").GetInt32() != 0x1002 || !peer.GetProperty("adapter").GetString()!.Contains("RX 580") || peer.GetProperty("state").GetString() == "failed")
                            throw new InvalidOperationException("Strata no verificó el adaptador AMD RX 580 real.");
                    }
                    using var models = JsonDocument.Parse(await _http.GetStringAsync(new Uri(Endpoint, "v1/models"), token));
                    Model = models.RootElement.GetProperty("data")[0].GetProperty("id").GetString()!;
                    SetState("Preparado"); return;
                }
                await Task.Delay(1000, token);
            }
            throw new TimeoutException("Strata no terminó de cargar en tres minutos.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { LastLoadError = null; SetState("Carga cancelada"); StopProcess(); throw; }
        catch (Exception e) { LastLoadError = e.Message; SetState("Falló la carga"); StopProcess(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<JsonElement?> HealthAsync(CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync(new Uri(Endpoint, "health"), token);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (Profile.Kind == "llama")
            { if (!doc.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ok") return null; }
            else if (doc.RootElement.GetProperty("service").GetString() != "strata" || doc.RootElement.GetProperty("max_context").GetInt32() != Profile.Context)
                throw new InvalidOperationException("El puerto no pertenece al runtime esperado.");
            return doc.RootElement.Clone();
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }
    internal async Task<string> CompleteAsync(IReadOnlyList<ModelMessage> messages, string reasoning, CancellationToken token)
    {
        await EnsureAsync(token);
        return await CompleteTransportAsync(messages, reasoning, token);
    }
    internal async Task<string> CompleteTransportAsync(IReadOnlyList<ModelMessage> messages, string reasoning, CancellationToken token)
    {
        if (Profile.Kind == "strata-cpu") reasoning = "none";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Profile.Kind == "strata-cpu" ? TimeSpan.FromMinutes(20) : TimeSpan.FromSeconds(360));
        token = timeout.Token;
        var contextRetries = 0; var estimateScale = 1d;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var budget = reasoning == "none" ? 0 : 256;
            var mandatory = await CountPromptAsync(messages.Take(2).ToArray(), reasoning, token, estimateScale);
            var available = EffectiveContext - mandatory - budget - 96 - 512;
            if (available < 256) throw new InvalidOperationException("La petición y las instrucciones no dejan espacio para una acción. Acorta la petición o la skill.");
            var limit = Math.Min(ModelProtocol.OutputBudget(attempt, EffectiveContext), available);
            if (Profile.Kind == "strata-cpu") limit = Math.Min(limit, Preferences.Load().FastCpuMode ? new[] { 512, 1024, 1536 }[attempt] : 1536);
            var packed = await PromptContext.FitAsync(messages, EffectiveContext, limit + budget,
                (items, ct) => CountPromptAsync(items, reasoning, ct, estimateScale), token);
            var body = new { model = Model, messages = packed.Select(m => new { role = m.Role, content = m.Content }),
                max_tokens = limit + budget, temperature = 0.15, stream = false, reasoning_effort = reasoning,
                reasoning_budget_tokens = budget, chat_template_kwargs = new { enable_thinking = reasoning != "none" },
                response_format = Profile.Kind == "strata-cpu" ? (object)new { type = "json_object", grammar = ModelProtocol.CpuActionGrammar } : new { type = "json_object" } };
            var clock = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "v1/chat/completions"))
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var raw = await response.Content.ReadAsStringAsync(token);
            if ((int)response.StatusCode == 400 && PromptContext.ContextFailure(raw, out var actualContext, out var promptTokens) && contextRetries++ == 0)
            {
                _reportedContext = Math.Min(EffectiveContext, actualContext);
                estimateScale = Math.Max(1d, (double)promptTokens / PromptContext.Estimate(packed) * 1.10);
                Progress?.Invoke("La lectura excedía el contexto; reduciendo los datos y conservando la pestaña y la petición…");
                attempt--; continue; // Retry the same inference only; never repeat an executed browser or PC action.
            }
            if ((int)response.StatusCode == 502 && ModelProtocol.StructuredFailure(raw) && attempt < 2)
            {
                Progress?.Invoke("La acción quedó incompleta; reintentando con más espacio de salida (" + ModelProtocol.OutputBudget(attempt + 1, EffectiveContext) + " tokens)…");
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"El motor respondió HTTP {(int)response.StatusCode}: {raw[..Math.Min(raw.Length, 500)]}");
            using var data = JsonDocument.Parse(raw);
            var choice = data.RootElement.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            // Hybrid recurrent caches require the exact generated token history.
            // Trimming trailing JSON whitespace forces a full reset on the next tool step.
            var text = Profile.Kind == "strata-cpu" ? content : content.Trim();
            var finish = choice.GetProperty("finish_reason").GetString();
            LastGeneration = new { seconds = clock.Elapsed.TotalSeconds, model = Model, finishReason = finish, reasoning,
                contextTokens = EffectiveContext, outputBudget = limit, promptCompacted = !packed.SequenceEqual(messages),
                usage = data.RootElement.GetProperty("usage").Clone(), backend = data.RootElement.TryGetProperty("strata", out var stats) ? (JsonElement?)stats.Clone() : null,
                scope = "Generación del componente " + Profile.Kind + " activo e integrado en SheepCode; excluye herramientas, voz y reproducción." };
            Generations.Add(LastGeneration); if (Generations.Count > 32) Generations.RemoveAt(0);
            if (finish != "length" && text.Length > 0) return text;
        }
        throw new InvalidOperationException("El motor no terminó una acción JSON tras tres intentos. Pide un archivo pequeño o una sustitución concreta; no se ejecutó la acción incompleta.");
    }
    private async Task<int> CountPromptAsync(IReadOnlyList<ModelMessage> messages, string reasoning, CancellationToken token, double estimateScale = 1d)
    {
        int Estimate() => (int)Math.Ceiling(PromptContext.Estimate(messages) * estimateScale);
        if (!Profile.NativeExecutable || _tokenizerUnavailable) return Estimate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        async Task<JsonDocument?> Query(string path, object body)
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri(Endpoint, path), content, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        try
        {
            if (Profile.Kind == "strata-cpu")
            {
                using var counted = await Query("prompt-count", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }) });
                if (counted is not null && counted.RootElement.TryGetProperty("count", out var count) && count.TryGetInt32(out var exact) && exact >= 0) return exact + 32;
            }
            using var template = await Query("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                chat_template_kwargs = new { enable_thinking = reasoning != "none" } });
            if (template is not null && template.RootElement.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            {
                using var tokens = await Query("tokenize", new { content = prompt.GetString(), add_special = true, parse_special = true });
                if (tokens is not null && tokens.RootElement.TryGetProperty("tokens", out var list) && list.ValueKind == JsonValueKind.Array) return list.GetArrayLength() + 32;
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is OperationCanceledException && !token.IsCancellationRequested) { }
        _tokenizerUnavailable = true; return Estimate();
    }
    internal async Task StopAsync(CancellationToken token)
    {
        var server = _server;
        try { if (server is not null && !server.HasExited) server.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        await _gate.WaitAsync(token);
        try { StopProcess(); SetState("Sin iniciar"); }
        finally { _gate.Release(); }
    }
    private void StopProcess()
    {
        ActiveLaunch = null;
        _reportedContext = null; _tokenizerUnavailable = false;
        if (_server is null) return;
        try { if (!_server.HasExited) _server.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _server.Dispose(); _server = null;
    }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); _http.Dispose(); }
}

internal sealed record ModelMessage(string Role, string Content);
