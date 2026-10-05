using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO.Compression;

namespace SheepCode.Distribution;

internal sealed record InstallProgress(string Stage, string Detail, double? Fraction = null);
internal static class SafeFiles
{
    internal static string Child(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative)) throw new IOException("Ruta fuera de la carpeta de instalación: " + relative);
        var current = Path.GetDirectoryName(full);
        while (current is not null && current.Length >= fullRoot.Length - 1)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("No se instalan archivos a través de enlaces: " + current);
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Archivo enlazado: " + full);
        return full;
    }
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
    internal static void ExtractZip(string zip, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(Child(destination, entry.FullName.TrimEnd('/'))); continue; }
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new IOException("El paquete contiene un enlace.");
            var path = Child(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, true);
        }
    }
    internal static void CopyTree(string source, string target, CancellationToken token)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file); Child(source, relative);
            var path = Child(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(file, path, true);
        }
    }
}
internal sealed class VerifiedDownloader(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient http = client ?? new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    internal async Task DownloadAsync(string url, string destination, long expectedSize, string expectedHash, IProgress<InstallProgress>? progress, CancellationToken token, bool testLoopback = false)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" && !(testLoopback && uri.IsLoopback && uri.Scheme == "http")) throw new ArgumentException("Solo se descargan archivos HTTPS del catálogo.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && new FileInfo(destination).Length == expectedSize)
        {
            progress?.Report(new("Verificando", Path.GetFileName(destination)));
            if (string.Equals(await SafeFiles.HashAsync(destination, token), expectedHash, StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("Un archivo existente no coincide con el catálogo. Muévelo o elimínalo antes de reintentar: " + destination);
        }
        if (File.Exists(destination)) throw new InvalidDataException("Hay un archivo de tamaño distinto en la ruta del modelo: " + destination);
        var partial = destination + ".part";
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > expectedSize) { File.Delete(partial); offset = 0; }
                if (offset < expectedSize)
                {
                    var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
                    if (disk.AvailableFreeSpace < expectedSize - offset + 512_000_000) throw new IOException("No queda espacio para completar la descarga.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.UserAgent.ParseAdd("SheepCode/1.1"); if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.From != offset || range.Length != expectedSize) throw new InvalidDataException("Rango de descarga inesperado.");
                    }
                    else offset = 0; // A server that ignores Range sends a complete file; replace the partial.
                    await using var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, true);
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[1024 * 1024]; var completed = offset; var reportAt = DateTime.MinValue;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(TimeSpan.FromSeconds(90), token);
                        if (count == 0) break;
                        if (completed + count > expectedSize) throw new InvalidDataException("La descarga supera el tamaño del catálogo.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token); completed += count;
                        if (DateTime.UtcNow > reportAt)
                        {
                            progress?.Report(new("Descargando", $"{Path.GetFileName(destination)} · {completed / 1048576d:F0} / {expectedSize / 1048576d:F0} MB", completed / (double)expectedSize)); reportAt = DateTime.UtcNow.AddMilliseconds(300);
                        }
                    }
                    await output.FlushAsync(token);
                    if (completed != expectedSize) throw new HttpRequestException("Descarga interrumpida; se conservará para continuar.");
                }
                progress?.Report(new("Verificando SHA-256", Path.GetFileName(destination)));
                if (!string.Equals(await SafeFiles.HashAsync(partial, token), expectedHash, StringComparison.OrdinalIgnoreCase))
                { File.Delete(partial); throw new InvalidDataException("El SHA-256 no coincide; se descartó la descarga incompleta."); }
                File.Move(partial, destination); return;
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException && attempt < 2)
            { progress?.Report(new("Reintentando", e.Message)); await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), token); }
        }
    }
    public void Dispose() { if (client is null) http.Dispose(); }
}
