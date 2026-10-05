using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace SheepCode;

internal sealed class NeuralVoice : IAsyncDisposable
{
    private readonly SemaphoreSlim _startGate = new(1, 1), _speechGate = new(1, 1);
    private readonly object _inputGate = new();
    private Process? _worker;
    private CancellationTokenSource? _utterance;
    private WasapiOut? _player;
    private string? _activeId;
    internal JsonElement? ReadyPacket { get; private set; }
    internal JsonElement? LastSynthesis { get; private set; }
    internal string State { get; private set; } = "Sin iniciar";
    internal event Action<string>? Progress;
    internal IReadOnlyList<string> Emotions
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "tts", "emotion-styles.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        }
    }
    internal object Snapshot() => new { state = State, ready = ReadyPacket, lastSynthesis = LastSynthesis,
        playback = "WASAPI, salida predeterminada; frases completas", auxiliaryCpu = new[] { "protocolo", "reproducción", "control de cancelación" } };
    private void SetState(string value) { State = value; Progress?.Invoke(value); }
    internal async Task EnsureAsync(CancellationToken token)
    {
        await _startGate.WaitAsync(token);
        try
        {
            if (ReadyPacket is not null && _worker is { HasExited: false }) return;
            var script = Path.Combine(AppContext.BaseDirectory, "tts", "qwen_directml_worker.py");
            var config = Path.Combine(AppPaths.State, "tts-config.json");
            var adapter = DxgiAmdDeviceSelector.FindAmdAdapter();
            if (adapter < 0) throw new InvalidOperationException("No se encontró la RX 580 física para la voz neuronal.");
            if (!File.Exists(AppPaths.VoicePython) || !File.Exists(script) || !File.Exists(config))
                throw new FileNotFoundException("Falta la instalación de la voz neuronal de SheepCode.");
            Reset(); SetState("Cargando voz RX 580…");
            var start = new ProcessStartInfo(AppPaths.VoicePython)
            {
                WorkingDirectory = Path.GetDirectoryName(script)!, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add(script);
            start.Environment["SHEEPGPT_TTS_CONFIG"] = config;
            start.Environment["SHEEPGPT_TTS_MODEL"] = Path.Combine(AppPaths.ModelRoot, "tts", "Qwen3-TTS-12Hz-1.7B-CustomVoice");
            start.Environment["SHEEPGPT_TTS_DML_DEVICE"] = adapter.ToString();
            start.Environment["SHEEPGPT_TTS_TARGET"] = "rx580-directml";
            start.Environment["SHEEPGPT_TTS_DEVICE_NAME"] = "RX 580 / DirectML";
            start.Environment["SHEEPGPT_TTS_WARM_EMOTION"] = "calmness";
            start.Environment["SHEEPGPT_TTS_WARM_INTENSITY"] = "0.5";
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["HF_HOME"] = Path.Combine(AppPaths.ModelRoot, "huggingface");
            start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
            _worker = new Process { StartInfo = start };
            var log = Path.Combine(AppPaths.Logs, "voice-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
            _worker.ErrorDataReceived += (_, e) => { if (e.Data is not null) File.AppendAllText(log, e.Data + Environment.NewLine); };
            if (!_worker.Start()) throw new InvalidOperationException("No se pudo iniciar la voz neuronal.");
            _worker.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(4));
            var packet = await ReadPacketAsync(_worker, timeout.Token);
            if (!packet.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
                throw new InvalidOperationException(packet.TryGetProperty("error", out var error) ? error.GetString() : "La voz no quedó preparada.");
            if (!packet.GetProperty("weightsVerified").GetBoolean() || packet.GetProperty("backend").GetString() != "sheepgpt-directml" ||
                !packet.GetProperty("device").GetString()!.Contains("RX 580", StringComparison.OrdinalIgnoreCase) ||
                packet.GetProperty("speaker").GetString() != "Ono_Anna" ||
                packet.GetProperty("talkerBackend").GetString() != "onnx-directml" ||
                packet.GetProperty("predictorBackend").GetString() != "onnx-directml" ||
                packet.GetProperty("codecBackend").GetString() != "onnx-directml")
                throw new InvalidOperationException("No se verificaron los componentes de voz originales en la RX 580.");
            ReadyPacket = packet; SetState("RX 580 · Ono_Anna");
        }
        catch { Reset(); SetState("Error de voz"); throw; }
        finally { _startGate.Release(); }
    }
    private static async Task<JsonElement> ReadPacketAsync(Process worker, CancellationToken token)
    {
        for (var i = 0; i < 64; i++)
        {
            var line = await worker.StandardOutput.ReadLineAsync(token) ?? throw new IOException("La voz cerró su protocolo.");
            if (!line.TrimStart().StartsWith('{')) continue;
            using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone();
        }
        throw new InvalidDataException("La voz emitió diagnósticos sin un paquete JSON.");
    }
    private void Send(object packet)
    {
        lock (_inputGate)
        {
            if (_worker is not { HasExited: false }) return;
            _worker.StandardInput.WriteLine(JsonSerializer.Serialize(packet)); _worker.StandardInput.Flush();
        }
    }
    internal async Task<string> SpeakAsync(string text, string emotion, CancellationToken token, bool playback = true)
    {
        await EnsureAsync(token);
        await _speechGate.WaitAsync(token);
        var output = Path.Combine(AppPaths.Temp, "voice-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            if (!Emotions.Contains(emotion)) throw new ArgumentException("Estilo emocional desconocido.");
            using var current = CancellationTokenSource.CreateLinkedTokenSource(token);
            _utterance = current; _activeId = Guid.NewGuid().ToString("N");
            using var cancellation = current.Token.Register(() =>
            { try { Send(new { cancel = true, id = _activeId }); _player?.Stop(); } catch (Exception e) when (e is IOException or InvalidOperationException) { } });
            SetState("Generando voz en RX 580…");
            Send(new { id = _activeId, text, output, emotion, intensity = 0.5, stream = false, expressiveDialogue = true, bufferedPlayback = false });
            // Drain the worker's cancelled response before releasing the protocol to the next utterance.
            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(210));
            var result = await ReadPacketAsync(_worker!, responseTimeout.Token);
            current.Token.ThrowIfCancellationRequested();
            if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.TryGetProperty("error", out var error) ? error.GetString() : "Voz cancelada.");
            if (result.GetProperty("id").GetString() != _activeId || result.GetProperty("output").GetString() != output || !File.Exists(output))
                throw new InvalidDataException("La voz devolvió un audio de otra petición.");
            LastSynthesis = result;
            if (playback)
            {
                SetState("Hablando…");
                using var reader = new AudioFileReader(output);
                using var player = new WasapiOut(); _player = player;
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, e) => { if (e.Exception is null) stopped.TrySetResult(); else stopped.TrySetException(e.Exception); };
                player.Init(reader); player.Play(); await stopped.Task.WaitAsync(current.Token);
            }
            return output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && _utterance?.IsCancellationRequested != true)
        { Reset(); throw new TimeoutException("El backend neuronal no respondió; se reiniciará en la próxima petición."); }
        finally { _player = null; _activeId = null; _utterance = null; SetState(ReadyPacket is null ? "Sin iniciar" : "RX 580 · Ono_Anna"); _speechGate.Release(); }
    }
    internal void Interrupt() { try { _utterance?.Cancel(); _player?.Stop(); } catch (ObjectDisposedException) { } }
    private void Reset()
    {
        ReadyPacket = null;
        if (_worker is null) return;
        try { if (!_worker.HasExited) _worker.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _worker.Dispose(); _worker = null;
    }
    public ValueTask DisposeAsync() { Interrupt(); Reset(); return ValueTask.CompletedTask; }
}
