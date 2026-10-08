using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed record ImageInfo(string Path, string Name, string Format, int Width, int Height, long Bytes, string Sha256,
    bool FirstFrameOnly, bool OrientationApplied);
internal sealed record OcrReply(string Status, string Text = "", string Language = "", string[]? Languages = null,
    int OcrWidth = 0, int OcrHeight = 0, int TotalCharacters = 0, bool Truncated = false, int MaxDimension = 0, string Error = "");
internal sealed record ImageRead(ImageInfo Image, string Status, string Text, int TextStart, int TotalTextCharacters, bool Truncated,
    string Language, int OcrWidth, int OcrHeight, string Notice, string Error);

internal sealed class ImageTools(Preferences preferences, SkillRegistry skills, Func<ProjectWorkspace?> workspace) : IDisposable
{
    internal const int MaximumBytes = 16 * 1024 * 1024, MaximumPixels = 16000000, MaximumImages = 4;
    internal const string Formats = "PNG, JPG/JPEG, BMP, GIF y TIFF (primera imagen)";
    internal const string VisionLimit = "image_read reconoce texto con OCR local; image_analyze interpreta objetos y dibujos con el componente visual local instalado y activado. El motor de texto recibe sus resultados, no los píxeles. Sin subida de imágenes a Internet. OCR y visión pueden equivocarse.";
    private sealed record Snapshot(ImageInfo Info, byte[] Png);
    private readonly List<Snapshot> pending = [], accepted = [];
    private readonly Dictionary<string, OcrReply> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private CancellationTokenSource? reading;
    private OcrReply? availability;
    internal event Action? Changed;
    internal IReadOnlyList<ImageInfo> Pending => pending.Select(i => i.Info).ToArray();
    internal IReadOnlyList<ImageInfo> Attached => accepted.Select(i => i.Info).ToArray();
    internal static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";
    internal bool Enabled => skills.Enabled("images");

    internal ImageInfo AttachFileHuman(string path)
    {
        skills.Require("images");
        var full = Path.GetFullPath(path);
        if (!IsImage(full)) throw new ArgumentException("Formatos de imágenes: " + Formats + ". SVG/WebP/HEIC aún no se decodifican aquí.");
        return Stage(Decode(ReadBounded(full), Path.GetFileName(full), ""));
    }
    internal ImageInfo AttachClipboardHuman(Image image)
    {
        skills.Require("images"); CheckSize(image.Width, image.Height);
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        if (stream.Length > MaximumBytes) throw new IOException("La captura supera 16 MiB.");
        return Stage(Decode(stream.ToArray(), "Captura pegada.png", ""));
    }
    private ImageInfo Stage(Snapshot snapshot)
    {
        if (pending.Count >= MaximumImages) throw new IOException("Máximo cuatro imágenes por mensaje. Quita una antes de adjuntar otra.");
        if (pending.Sum(i => (long)i.Png.Length) + snapshot.Png.Length > 32 * 1024 * 1024 || pending.Sum(i => (long)i.Info.Width * i.Info.Height) + (long)snapshot.Info.Width * snapshot.Info.Height > 24000000)
            throw new IOException("Los adjuntos del mensaje superan 32 MiB o 24 millones de píxeles.");
        var item = snapshot with { Info = snapshot.Info with { Path = "img-" + Guid.NewGuid().ToString("N")[..12] } };
        pending.Add(item); Changed?.Invoke(); return item.Info;
    }
    internal ImageInfo[] AcceptPending(string? only = null)
    {
        skills.Require("images"); var selected = pending.Where(i => only is null || i.Info.Path == only).ToArray(); var added = selected.Select(i => i.Info).ToArray();
        accepted.AddRange(selected); pending.RemoveAll(i => only is null || i.Info.Path == only);
        // A bounded in-memory conversation cache; originals and project files are never changed.
        while (accepted.Count > MaximumImages && (accepted.Count > 8 || accepted.Sum(i => (long)i.Png.Length) > 32 * 1024 * 1024 || accepted.Sum(i => (long)i.Info.Width * i.Info.Height) > 24000000))
            accepted.RemoveAt(0);
        TrimCache(); Changed?.Invoke(); return added;
    }
    internal void RemoveHuman(string id)
    {
        if (pending.RemoveAll(i => i.Info.Path == id) + accepted.RemoveAll(i => i.Info.Path == id) == 0) throw new ArgumentException("No existe ese adjunto.");
        TrimCache(); Changed?.Invoke();
    }
    internal void ClearHuman() { Cancel(); pending.Clear(); accepted.Clear(); cache.Clear(); Changed?.Invoke(); }
    internal void Cancel() { try { reading?.Cancel(); } catch (ObjectDisposedException) { } }
    private void TrimCache() { var keys = accepted.Concat(pending).Select(i => i.Info.Sha256 + ":" + preferences.ImageOcrLanguage).ToHashSet(); foreach (var key in cache.Keys.Where(k => !keys.Contains(k)).ToArray()) cache.Remove(key); }
    internal Bitmap Preview(string path)
    {
        var item = Get(path, true); using var stream = new MemoryStream(item.Png, false); using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
    internal ImageInfo Describe(string path, bool humanPreview = false) => Get(path, humanPreview).Info;
    internal (ImageInfo Info, byte[] Png) VisualInput(string path) { var snapshot = Get(path); return (snapshot.Info, snapshot.Png.ToArray()); }
    private Snapshot Get(string path, bool humanPreview = false)
    {
        skills.Require("images");
        var item = accepted.FirstOrDefault(i => i.Info.Path == path) ?? (humanPreview ? pending.FirstOrDefault(i => i.Info.Path == path) : null);
        if (item is not null) return item;
        if (Regex.IsMatch(path, @"^img-[a-f0-9]{12}$")) throw new ArgumentException("Ese adjunto no está enviado en esta conversación. Adjunta o vuelve a enviar la imagen.");
        var project = workspace() ?? throw new InvalidOperationException("Adjunta la imagen o abre un proyecto para leer una imagen de su carpeta.");
        if (!IsImage(path)) throw new ArgumentException("Formatos de imágenes: " + Formats);
        return Decode(ReadBounded(project.Resolve(path)), Path.GetFileName(path), path);
    }
    private static byte[] ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumBytes) throw new IOException("La imagen supera 16 MiB.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static void CheckSize(int width, int height)
    { if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > MaximumPixels) throw new IOException("Máximo 8192 píxeles por lado y 16 millones de píxeles por imagen."); }
    private static Snapshot Decode(byte[] bytes, string name, string path)
    {
        try
        {
            using var input = new MemoryStream(bytes, false); using var original = Image.FromStream(input, false, false);
            CheckSize(original.Width, original.Height);
            var allowed = new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp, ImageFormat.Gif, ImageFormat.Tiff };
            var format = allowed.FirstOrDefault(f => f.Guid == original.RawFormat.Guid) ?? throw new IOException("El contenido no es una imagen raster admitida.");
            var frames = original.FrameDimensionsList.Select(d => original.GetFrameCount(new FrameDimension(d))).DefaultIfEmpty(1).Max();
            var rotated = false;
            if (original.PropertyIdList.Contains(0x112))
            {
                var value = original.GetPropertyItem(0x112)?.Value;
                var orientation = value is { Length: >= 2 } ? BitConverter.ToUInt16(value, 0) : 1;
                var transform = orientation switch { 2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.Rotate180FlipX,
                    5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX, 8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone };
                if (transform != RotateFlipType.RotateNoneFlipNone) { original.RotateFlip(transform); rotated = true; }
            }
            // A fresh PNG snapshot strips metadata, preserves transparency and never points back at a mutable original.
            using var clean = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(clean)) { graphics.Clear(Color.Transparent); graphics.DrawImage(original, new Rectangle(0, 0, clean.Width, clean.Height)); }
            using var output = new MemoryStream(); clean.Save(output, ImageFormat.Png);
            if (output.Length > MaximumBytes) throw new IOException("La copia PNG de la imagen supera 16 MiB.");
            var png = output.ToArray();
            return new(new(path, name, format.ToString(), clean.Width, clean.Height, bytes.LongLength, AppPaths.Hash(png), frames > 1, rotated), png);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException)
        { throw new IOException("La imagen no se pudo decodificar o está dañada.", e); }
    }
    internal object Status() => new { enabled = Enabled, ocrEnabled = preferences.ImageOcr, language = preferences.ImageOcrLanguage,
        ocr = availability?.Status ?? "sin comprobar", availableLanguages = availability?.Languages ?? [], error = availability?.Error ?? "",
        formats = Formats, maximumImages = MaximumImages, maximumBytes = MaximumBytes, pending = Pending, attached = Attached,
        vision = "componente visual separado; consulta vision_status para su configuración", scope = VisionLimit, storage = "Copias temporales de la conversación; al cerrar o cambiar de proyecto hay que volver a adjuntar." };
    internal async Task<object> StatusAsync(CancellationToken token)
    {
        skills.Require("images"); availability = await WindowsImageOcr.RequestAsync(new { action = "status" }, token);
        return Status();
    }
    internal async Task SetLanguageHumanAsync(string language, CancellationToken token)
    {
        skills.Require("images");
        if (!Regex.IsMatch(language, @"\A(?:auto|[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*)\z")) throw new ArgumentException("Usa auto o un idioma OCR instalado, por ejemplo es-ES.");
        await StatusAsync(token);
        if (language != "auto" && !(availability?.Languages ?? []).Contains(language, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Ese idioma OCR no está instalado. Disponibles: " + string.Join(", ", availability?.Languages ?? []) + ". Instálalo desde los ajustes de idioma de Windows.");
        preferences.ImageOcrLanguage = language; preferences.Save(); cache.Clear(); Changed?.Invoke();
    }
    internal async Task<ImageRead> ReadAsync(string path, int start, int textLength, CancellationToken token)
    {
        skills.Require("images"); token.ThrowIfCancellationRequested(); var item = Get(path);
        if (!preferences.ImageOcr) return Fragment(item.Info, new("disabled", Error: "La lectura OCR está desactivada por la persona."), start, textLength);
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested(); skills.Require("images");
            var language = preferences.ImageOcrLanguage; var key = item.Info.Sha256 + ":" + language;
            if (!cache.TryGetValue(key, out var read))
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); reading = cancel;
                var folder = Path.Combine(AppPaths.Temp, "image-ocr-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                var temporary = Path.Combine(folder, "snapshot.png");
                try
                {
                    // White compositing is only for OCR; previews keep the original transparency.
                    using var preview = new MemoryStream(item.Png, false); using var image = Image.FromStream(preview);
                    using var white = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
                    using (var graphics = Graphics.FromImage(white)) { graphics.Clear(Color.White); graphics.DrawImage(image, new Rectangle(0, 0, white.Width, white.Height)); }
                    white.Save(temporary, ImageFormat.Png);
                    read = await WindowsImageOcr.RequestAsync(new { action = "read", path = temporary, language }, cancel.Token);
                    token.ThrowIfCancellationRequested(); skills.Require("images");
                    if (preferences.ImageOcr && language == preferences.ImageOcrLanguage && read.Status is "read" or "no_text") cache[key] = read;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && (!Enabled || !preferences.ImageOcr))
                { read = new("disabled", Error: "La lectura se detuvo al desactivar imágenes/OCR."); }
                finally { reading = null; if (File.Exists(temporary)) File.Delete(temporary); if (Directory.Exists(folder)) Directory.Delete(folder, false); }
            }
            if (!preferences.ImageOcr || !Enabled) read = new("disabled", Error: "La lectura OCR está desactivada.");
            return Fragment(item.Info, read, start, textLength);
        }
        finally { gate.Release(); }
    }
    internal static ImageRead Fragment(ImageInfo info, OcrReply read, int start, int textLength)
    {
        start = Math.Clamp(start, 0, read.Text.Length); textLength = Math.Clamp(textLength, 128, 4000);
        var text = read.Text.Substring(start, Math.Min(textLength, read.Text.Length - start));
        return new(info, read.Status, text, start, Math.Max(read.Text.Length, read.TotalCharacters), read.Truncated || start != 0 || text.Length != read.Text.Length,
            read.Language, read.OcrWidth, read.OcrHeight, VisionLimit + (info.FirstFrameOnly ? " Se lee solo la primera imagen de GIF/TIFF." : "") +
            (read.OcrWidth > 0 && (read.OcrWidth != info.Width || read.OcrHeight != info.Height) ? " Se redujo la resolución para el límite del OCR de Windows." : ""), read.Error);
    }
    public void Dispose() { Cancel(); pending.Clear(); accepted.Clear(); cache.Clear(); }
}

internal static class WindowsImageOcr
{
    private static readonly string command = LoadCommand();
    private static string LoadCommand()
    {
        using var stream = typeof(WindowsImageOcr).Assembly.GetManifestResourceStream("SheepCode.windows-ocr.ps1") ?? throw new IOException("Falta el componente local de OCR.");
        using var reader = new StreamReader(stream, Encoding.UTF8); return Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
    }
    internal static async Task<OcrReply> RequestAsync(object request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) return new("unavailable", Error: "No está disponible Windows PowerShell con OCR de Windows.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("No arrancó el componente de imágenes.");
        using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try
        {
            var output = ReadLimited(process.StandardOutput, 128 * 1024, timeout.Token); var errors = ReadLimited(process.StandardError, 8192, timeout.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var raw = await output; var error = await errors;
            return JsonSerializer.Deserialize<OcrReply>(raw.Trim(), AppPaths.Json) ?? new("unavailable", Error: error.Length > 0 ? error : "El OCR no devolvió un resultado.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("unavailable", Error: "El OCR excedió 20 segundos; su proceso se detuvo."); }
        catch (JsonException) { return new("unavailable", Error: "El OCR devolvió una respuesta inválida; no se reconoció texto."); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }
    private static async Task<string> ReadLimited(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[2048]; int size;
        while ((size = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) { if (result.Length + size > maximum) throw new IOException("El componente OCR excedió el tamaño de respuesta."); result.Append(buffer, 0, size); }
        return result.ToString();
    }
}
