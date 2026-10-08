using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using SheepCode.Distribution;

namespace SheepCode;

internal static class SetupDiagnostics
{
    internal static int GpuUiCheck()
    {
        var checks = Path.Combine(AppContext.BaseDirectory, "checks"); Directory.CreateDirectory(checks); var rows = new List<object>(); Exception? error = null;
        const long gib = 1073741824;
        var dxgi = new GraphicsAdapter(0, "Intel(R) Graphics", 0x8086, 128L * 1048576, false, SharedBytes: 16 * gib, DeviceId: 0x7d55);
        var gpus = GraphicsInventory.Merge([dxgi], [new("Intel(R) Graphics", 0x8086, 0x7d55, 0, true), new("NVIDIA GeForce RTX 4070 Laptop GPU", 0x10de, 0x2860, 0, true)]);
        var fixture = new SystemHardware("fixture Windows", true, 16, "Intel Core Ultra 7 · fixture", true, 32 * gib, 16 * gib, 602 * gib, "C:\\", gpus, true, new(true, true, 100, false), "SMBIOS 10");
        using var form = new SetupForm(false, fixture);
        form.Shown += async (_, _) =>
        {
            try
            {
                foreach (var size in new[] { new Size(990, 900), new Size(800, 600) })
                {
                    form.Size = size; await Task.Delay(150);
                    var snapshot = form.HardwareLayoutSnapshot(true); rows.Add(snapshot);
                    using var data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(snapshot)); var state = data.RootElement;
                    if (!state.GetProperty("hardwareText").GetString()!.Contains("RTX 4070") || state.GetProperty("gpuCount").GetInt32() != 2 ||
                        !state.GetProperty("scrollable").GetBoolean() || !state.GetProperty("lastLineReachable").GetBoolean() || !state.GetProperty("installButtonVisible").GetBoolean())
                        throw new IOException("Se recortó el inventario de gráficas o quedó oculto el botón del setup.");
                    if (!state.GetProperty("modelChoices").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "granite-4.0-h-350m" && p.GetProperty("label").GetString()!.Contains("experimental")) ||
                        state.GetProperty("selectedModel").GetString() == "granite-4.0-h-350m") throw new IOException("El setup no ofrece Granite experimental como alternativa manual.");
                    if (!state.GetProperty("modelChoices").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "granite-4.0-h-1b" && p.GetProperty("label").GetString()!.Contains("1.5B") && p.GetProperty("label").GetString()!.Contains("experimental")) ||
                        state.GetProperty("selectedModel").GetString() == "granite-4.0-h-1b") throw new IOException("El setup no ofrece Granite 1.5B experimental como alternativa manual.");
                    form.CaptureWindow(Path.Combine(checks, $"setup-hybrid-{size.Width}.png"));
                }
            }
            catch (Exception e) { error = e; }
            finally { form.Close(); }
        };
        Application.Run(form);
        DistributionJson.Save(Path.Combine(checks, "setup-gpu-ui.json"), new { status = error is null ? "complete" : "failed", rows, error = error?.ToString(),
            scope = "Ventanas reales del setup con inventario híbrido simulado; no certifica la GPU del portátil de la captura ni ejecuta IA." });
        return error is null ? 0 : 1;
    }
    internal static async Task<int> RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "checks", "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var checks = new List<object>();
        try
        {
            static void Assert(bool value, string error) { if (!value) throw new IOException(error); }
            checks.AddRange(PortableChecks.Run());
            foreach (var (ram, expected) in new[] { (4, "qwen3-0.6b"), (8, "qwen3-4b"), (16, "qwen3-8b"), (32, "qwen3-14b"), (64, "qwen3-32b") })
            {
                var hardware = new SystemHardware("fixture", true, 8, "CPU", true, ram * 1073741824L, 1073741824, 200L * 1073741824, "C:\\", []);
                var plan = ModelCatalog.Recommend(hardware); Assert(plan.Id == expected, $"Perfil incorrecto para {ram} GB: {plan.Id}"); checks.Add(new { ram, selected = plan.Id });
            }
            var strong = new SystemHardware("fixture", true, 16, "CPU", true, 64L * 1073741824, 32L * 1073741824, 200L * 1073741824, "C:\\", [new(0, "NVIDIA RTX 4080", 0x10de, 16L * 1073741824, false)]);
            Assert(ModelCatalog.Recommend(strong).Kind == "strata", "Un equipo compatible no recibió Strata.");
            Assert(ModelCatalog.Recommend(strong, "existing").Kind == "reuse", "No se priorizó la instalación existente.");
            Assert(ModelCatalog.Recommend(strong with { Avx2 = false }).Kind == "strata-cpu", "La CPU sin AVX2 no recibió Strata CPU portable.");
            try { ModelCatalog.Recommend(strong with { X64 = false }); throw new IOException("Se aceptó arquitectura no soportada."); } catch (PlatformNotSupportedException) { }
            try { SafeFiles.Child(root, @"..\escaped.txt"); throw new IOException("Ruta fuera de destino aceptada."); } catch (IOException e) when (e.Message.StartsWith("Ruta fuera")) { }
            var zip = Path.Combine(root, "unsafe.zip"); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()); writer.Write("test"); }
            try { SafeFiles.ExtractZip(zip, Path.Combine(root, "unzip"), CancellationToken.None); throw new IOException("ZIP fuera de destino aceptado."); } catch (IOException e) when (e.Message.StartsWith("Ruta fuera")) { }
            checks.Add(new { check = "hardware tiers, legacy preservation, old CPU, architecture and ZIP paths", passed = true });
            var bytes = Encoding.UTF8.GetBytes(new string('s', 8192) + "Sheep & Kuky"); var hash = Convert.ToHexString(SHA256.HashData(bytes));
            await using var server = new DownloadFixture(bytes); using var downloader = new VerifiedDownloader();
            var target = Path.Combine(root, "model.bin"); await File.WriteAllBytesAsync(target + ".part", bytes[..1200]);
            await downloader.DownloadAsync(server.Url, target, bytes.Length, hash, null, CancellationToken.None, true);
            Assert(File.ReadAllBytes(target).SequenceEqual(bytes) && server.RangeSeen, "No se reanudó el archivo correctamente.");
            var bad = Path.Combine(root, "bad.bin");
            try { await downloader.DownloadAsync(server.Url, bad, bytes.Length, new string('0', 64), null, CancellationToken.None, true); throw new IOException("Hash incorrecto aceptado."); }
            catch (InvalidDataException) { }
            Assert(!File.Exists(bad) && !File.Exists(bad + ".part"), "Se conservó un archivo con hash inválido.");
            checks.Add(new { check = "HTTP range resume and rejection of corrupt download", passed = true });
            DistributionJson.Save(Path.Combine(AppContext.BaseDirectory, "checks", "setup-components.json"), new { status = "complete", scope = "Validación determinista de instalación y selección de perfiles; no ejecuta modelos ni mide rendimiento.", checks, fixture = root, actualHardware = HardwareScanner.Scan(root) }); return 0;
        }
        catch (Exception e) { DistributionJson.Save(Path.Combine(AppContext.BaseDirectory, "checks", "setup-components.json"), new { status = "failed", error = e.ToString(), checks }); return 1; }
    }
    private sealed class DownloadFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource stop = new(); private readonly Task run;
        internal string Url { get; } internal bool RangeSeen { get; private set; }
        internal DownloadFixture(byte[] bytes)
        {
            listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/model";
            run = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token); await using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                        var offset = 0; string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                            if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) { offset = int.Parse(line[13..].TrimEnd('-')); RangeSeen = true; }
                        var header = $"HTTP/1.1 {(offset > 0 ? "206 Partial Content" : "200 OK")}\r\nContent-Length: {bytes.Length - offset}\r\nConnection: close\r\n" + (offset > 0 ? $"Content-Range: bytes {offset}-{bytes.Length - 1}/{bytes.Length}\r\n" : "") + "\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), stop.Token); await stream.WriteAsync(bytes.AsMemory(offset), stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); try { await run; } catch (SocketException) { } stop.Dispose(); }
    }
}
