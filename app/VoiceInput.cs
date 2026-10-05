using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SheepCode;

internal sealed class VoiceInput : IDisposable
{
    private WaveInEvent? _capture;
    private WaveFileWriter? _writer;
    private string? _path;
    private TaskCompletionSource? _stopped;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    internal bool Recording => _capture is not null;
    internal string Backend => _processor is null ? "Sin iniciar" : RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan ? "Whisper · RX 580 / Vulkan" : "Whisper · CPU (Vulkan no disponible)";
    internal void Start()
    {
        if (Recording) return;
        if (!File.Exists(Path.Combine(AppPaths.ModelRoot, "ggml-small-q5_1.bin"))) throw new FileNotFoundException("El dictado está sin configurar. Di por texto «Instala el dictado» o selecciona Dictado en el setup.");
        _path = Path.Combine(AppPaths.Temp, "dictation-" + Guid.NewGuid().ToString("N") + ".wav");
        _capture = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 80 };
        _writer = new WaveFileWriter(_path, _capture.WaveFormat);
        _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.DataAvailable += (_, e) => { if (_writer is { Length: < 16000 * 2 * 45 }) _writer.Write(e.Buffer, 0, e.BytesRecorded); else _capture?.StopRecording(); };
        _capture.RecordingStopped += (_, e) => { _writer?.Dispose(); _writer = null; if (e.Exception is null) _stopped.TrySetResult(); else _stopped.TrySetException(e.Exception); };
        try { _capture.StartRecording(); }
        catch { _writer?.Dispose(); _writer = null; _capture.Dispose(); _capture = null; throw; }
    }
    internal async Task<string> StopAsync(CancellationToken token)
    {
        if (_capture is null) return "";
        _capture.StopRecording();
        await _stopped!.Task.WaitAsync(token);
        _capture.Dispose(); _capture = null;
        return await TranscribeAsync(_path!, token);
    }
    internal async Task<string> TranscribeAsync(string audioPath, CancellationToken token)
    {
        if (_processor is null)
        {
            WhisperRuntime.Configure();
            _factory = WhisperFactory.FromPath(Path.Combine(AppPaths.ModelRoot, "ggml-small-q5_1.bin"), new WhisperFactoryOptions { UseGpu = true, GpuDevice = 0 });
            _processor = _factory.CreateBuilder().WithLanguage("es").WithNoContext().WithThreads(6).Build();
        }
        // Convert arbitrary installed voice samples to Whisper's required mono 16 kHz float PCM.
        using var reader = new AudioFileReader(audioPath);
        var source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 2) source = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(source);
        if (source.WaveFormat.SampleRate != 16000) source = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, 16000);
        var samples = new List<float>(); var buffer = new float[16000]; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); samples.AddRange(buffer.Take(read)); }
        var text = new System.Text.StringBuilder();
        await foreach (var segment in _processor.ProcessAsync(samples.ToArray(), token)) text.Append(segment.Text).Append(' ');
        return text.ToString().Trim();
    }
    public void Dispose() { _capture?.Dispose(); _writer?.Dispose(); _processor?.Dispose(); _factory?.Dispose(); }
}
