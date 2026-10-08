using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed record UpdateRelease(string Tag, string Page, string SetupUrl, long Size, string Sha256);
internal sealed class UpdateManager : IDisposable
{
    internal const string Repository = "ajaloslo919-eng/SheepCode";
    internal const string CurrentVersion = "0.5.0";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _persist;
    private static string CachePath => Path.Combine(AppPaths.State, "updater-cache.json");
    private sealed record Cache(UpdateRelease? Available, string? Downloaded, DateTimeOffset? LastChecked);
    internal UpdateRelease? Available { get; private set; }
    internal string? Downloaded { get; private set; }
    internal DateTimeOffset? LastChecked { get; private set; }
    internal string Message { get; private set; } = "Todavía no se buscaron actualizaciones.";
    internal UpdateManager(HttpMessageHandler? handler = null)
    {
        _persist = handler is null;
        _http = handler is null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
        _http.Timeout = TimeSpan.FromMinutes(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SheepCode/" + CurrentVersion);
        if (_persist && File.Exists(CachePath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath), AppPaths.Json);
                if (saved?.Available is { } release && Regex.IsMatch(release.Tag, @"\Av\d+\.\d+\.\d+\z") && Version.Parse(release.Tag[1..]) > Version.Parse(CurrentVersion) && Regex.IsMatch(release.Sha256, @"\A[A-F0-9]{64}\z") && release.Size is > 0 and <= 512L * 1024 * 1024 && ReleaseUrl(new Uri(release.SetupUrl)))
                { Available = release; if (saved.Downloaded is { } path && File.Exists(path)) { CheckPath(path); Downloaded = path; VerifyDownloaded(); } LastChecked = saved.LastChecked; Message = "Hay una versión descargable en caché. Busca actualizaciones para comprobar si sigue siendo la última."; }
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { Available = null; Downloaded = null; }
        }
    }
    private void Save() { if (_persist) AppPaths.SaveJson(CachePath, new Cache(Available, Downloaded, LastChecked)); }
    internal string Status() => JsonSerializer.Serialize(new { installed = CurrentVersion, lastChecked = LastChecked, message = Message, available = Available, downloaded = Downloaded,
        scope = "Releases públicas de " + Repository + "; descarga verificada por SHA-256. Instalar requiere una petición humana y cerrar el editor; el setup respalda app y source." });
    private static bool ReleaseUrl(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal);
    private static bool DownloadHost(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" || ReleaseUrl(uri));
    private async Task<HttpResponseMessage> FetchAsync(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        for (var redirects = 0; redirects < 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location is { } location ? new Uri(uri, location) : throw new IOException("Falta el destino de la descarga.");
                response.Dispose(); if (!DownloadHost(next)) throw new IOException("La actualización redirige fuera de los servidores de GitHub permitidos."); uri = next; continue;
            }
            if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new IOException("GitHub respondió HTTP " + status + ". Vuelve a buscar más tarde."); }
            return response;
        }
        throw new IOException("Demasiadas redirecciones al descargar la actualización.");
    }
    private async Task<string> TextAsync(string url, int maximum, CancellationToken token)
    {
        using var response = await FetchAsync(url, token); using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int read; while ((read = await stream.ReadAsync(buffer, token)) > 0) { if (output.Length + read > maximum) throw new IOException("Metadatos de actualización demasiado grandes."); output.Write(buffer, 0, read); }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    internal async Task<string> CheckAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(45));
            using var release = JsonDocument.Parse(await TextAsync("https://api.github.com/repos/" + Repository + "/releases/latest", 256 * 1024, limit.Token));
            var data = release.RootElement; var tag = data.GetProperty("tag_name").GetString()!;
            if (!Regex.IsMatch(tag, @"\Av\d+\.\d+\.\d+\z") || data.GetProperty("draft").GetBoolean() || data.GetProperty("prerelease").GetBoolean()) throw new IOException("La release no es una versión estable válida.");
            if (Version.Parse(tag[1..]) <= Version.Parse(CurrentVersion)) { Available = null; Downloaded = null; LastChecked = DateTimeOffset.UtcNow; Save(); return Message = "🐑 SheepCode " + CurrentVersion + " está al día."; }
            var assets = data.GetProperty("assets").EnumerateArray().ToArray();
            JsonElement Asset(string name) => assets.Single(a => a.GetProperty("name").GetString() == name);
            var setup = Asset("SheepCode-Setup.exe"); var sums = Asset("SHA256SUMS.txt");
            string Url(JsonElement a, string filename) { var value = a.GetProperty("browser_download_url").GetString()!; var uri = new Uri(value); if (!ReleaseUrl(uri) || uri.AbsolutePath != "/" + Repository + "/releases/download/" + tag + "/" + filename || uri.Query.Length > 0) throw new IOException("Destino de actualización inválido."); return value; }
            var size = setup.GetProperty("size").GetInt64(); if (size is < 1 or > 512L * 1024 * 1024) throw new IOException("Tamaño del setup fuera del límite de 512 MiB.");
            var hashes = await TextAsync(Url(sums, "SHA256SUMS.txt"), 16 * 1024, limit.Token);
            var matches = Regex.Matches(hashes, @"(?m)^([a-fA-F0-9]{64})[ \t]+\*?SheepCode-Setup\.exe\r?$"); if (matches.Count != 1) throw new IOException("Falta el hash único de SheepCode-Setup.exe.");
            var sha = matches[0].Groups[1].Value.ToUpperInvariant();
            if (setup.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String && digest.GetString() is { Length: > 0 } published && !published.Equals("sha256:" + sha, StringComparison.OrdinalIgnoreCase)) throw new IOException("El hash de GitHub no coincide con SHA256SUMS.");
            var available = new UpdateRelease(tag, "https://github.com/" + Repository + "/releases/tag/" + tag, Url(setup, "SheepCode-Setup.exe"), size, sha);
            if (Available != available) Downloaded = null; Available = available; LastChecked = DateTimeOffset.UtcNow;
            Save();
            return Message = "🌷 Disponible SheepCode " + tag[1..] + ". Descarga: " + (size / 1048576d).ToString("F1") + " MiB. Usa «Descarga la actualización» y «Instala la actualización».";
        }
        catch { Message = "No se pudo verificar la última versión. Conserva la instalación actual y vuelve a buscar."; throw; }
        finally { _gate.Release(); }
    }
    internal async Task<string> DownloadAsync(IProgress<int>? progress, CancellationToken token)
    {
        if (Available is null) await CheckAsync(token);
        await _gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            var release = Available ?? throw new IOException("No hay una actualización más reciente.");
            if (Downloaded is not null) { VerifyDownloaded(); return "La actualización ya está descargada y verificada."; }
            var folder = Path.Combine(AppPaths.Temp, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); CheckPath(folder);
            temporary = Path.Combine(folder, "SheepCode-Setup.exe.part");
            using var response = await FetchAsync(release.SetupUrl, token);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new IOException("La descarga tiene un tamaño distinto al publicado.");
            using (var input = await response.Content.ReadAsStreamAsync(token))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; long count = 0; int read; int last = -1;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    count += read; if (count > release.Size) throw new IOException("La descarga supera el tamaño publicado.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
                    var percentage = (int)(count * 100 / release.Size); if (percentage != last) { progress?.Report(percentage); last = percentage; }
                }
                if (count != release.Size || Convert.ToHexString(hash.GetHashAndReset()) != release.Sha256) throw new IOException("El setup descargado no coincide con su SHA-256; no se instalará.");
            }
            Downloaded = Path.Combine(folder, "SheepCode-Setup.exe"); File.Move(temporary, Downloaded); temporary = null;
            Save();
            return Message = "🐑 Actualización descargada y SHA-256 verificado. Usa «Instala la actualización» para abrir el setup en " + AppPaths.Root + ".";
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
    private static void CheckPath(string path)
    {
        var root = Path.GetFullPath(Path.Combine(AppPaths.Temp, "updates")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("El setup debe estar en la caché de actualizaciones de SheepCode.");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("La actualización no atraviesa enlaces.");
    }
    internal void VerifyDownloaded()
    {
        var release = Available ?? throw new IOException("Busca y descarga la actualización primero."); var path = Downloaded ?? throw new IOException("Descarga la actualización primero.");
        CheckPath(path); using var file = File.OpenRead(path);
        if (file.Length != release.Size || Convert.ToHexString(SHA256.HashData(file)) != release.Sha256) throw new IOException("El setup descargado fue alterado; busca y descarga la actualización de nuevo.");
    }
    internal void StartHandoff()
    {
        VerifyDownloaded();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppPaths.Root };
        start.Environment["SHEEPCODE_HOME"] = AppPaths.Root;
        foreach (var arg in new[] { "--apply-update", Environment.ProcessId.ToString(), Downloaded!, Available!.Sha256, Available.Size.ToString() }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new IOException("No se pudo iniciar el ayudante de actualización.");
    }
    internal static int RunHandoff(string[] args)
    {
        try
        {
            if (args.Length != 5 || !int.TryParse(args[1], out var pid) || !Regex.IsMatch(args[3], @"\A[A-F0-9]{64}\z") || !long.TryParse(args[4], out var size) || size is < 1 or > 512L * 1024 * 1024) throw new IOException("Solicitud de actualización inválida.");
            CheckPath(args[2]);
            try { using var parent = Process.GetProcessById(pid); if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) || !parent.WaitForExit(45000)) throw new IOException("El editor debe cerrarse antes de actualizar."); } catch (ArgumentException) { }
            using (var file = File.OpenRead(args[2])) if (file.Length != size || Convert.ToHexString(SHA256.HashData(file)) != args[3]) throw new IOException("El setup cambió antes de instalarlo.");
            var start = new ProcessStartInfo(args[2]) { UseShellExecute = false, WorkingDirectory = AppPaths.Root };
            foreach (var arg in new[] { "--target", AppPaths.Root, "--upgrade" }) start.ArgumentList.Add(arg);
            Process.Start(start)?.Dispose(); return 0;
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(AppPaths.Logs, "updater-error.log"), e.Message); return 1; }
    }
    public void Dispose() => _http.Dispose();
}
