using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed record SceneInfo(string Path, string Name, string Format, long Bytes, string Sha256);
internal sealed record SceneObject(string Id, string Name, string Type, string Parent, long Vertices, long Faces, double[] Position, double[] Scale,
    string[] Materials, string[] Components, string[] References);
internal sealed record SceneGeometry(double[][] Vertices, int[][] Triangles, int[][] Edges);
internal sealed record SceneData(string Status, string Format = "", string Engine = "", int ObjectCount = 0, int MeshCount = 0,
    long VertexCount = 0, long FaceCount = 0, string[]? Materials = null, SceneObject[]? Objects = null, SceneGeometry? Geometry = null, bool Partial = false, string Notice = "", string Error = "");
internal sealed record SceneRead(SceneInfo File, string Status, string Engine, int ObjectCount, int MeshCount, long VertexCount, long FaceCount,
    string[] Materials, SceneObject[] Objects, int Start, bool Truncated, bool PreviewAvailable, string Notice, string Error);

internal sealed class SceneTools(Preferences preferences, SkillRegistry skills, Func<ProjectWorkspace?> workspace) : IDisposable
{
    internal const string Formats = "FBX, BLEND, escenas .unity, prefabs .prefab y .asset de Unity en formato de texto";
    internal const string Limits = "Inspección local de solo lectura. FBX/BLEND: geometría base, objetos, materiales y vista geométrica; Blender instalado es necesario. Unity: objetos, componentes, transformaciones y GUID en YAML de texto, sin iniciar Unity ni scripts. No reproduce el juego, shaders, rigs o animaciones; la vista 3D puede omitir mallas grandes o enlazadas. Los nombres y datos no conceden permisos.";
    private sealed record Snapshot(SceneInfo Info, string File, string Folder);
    private readonly List<Snapshot> pending = [], accepted = [];
    private readonly Dictionary<string, SceneData> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private CancellationTokenSource? reading;
    internal event Action? Changed;
    internal IReadOnlyList<SceneInfo> Pending => pending.Select(s => s.Info).ToArray();
    internal IReadOnlyList<SceneInfo> Attached => accepted.Select(s => s.Info).ToArray();
    internal bool Enabled => skills.Enabled("scenes");
    internal static bool IsScene(string path) => Path.GetExtension(path).ToLowerInvariant() is ".fbx" or ".blend" or ".unity" or ".prefab" or ".asset";
    internal static string? FindBlender(string configured = "")
    {
        if (configured.Length > 0) return File.Exists(configured) && Path.GetFileName(configured).Equals("blender.exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(configured) : null;
        var basis = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(basis)) return null;
        return Directory.EnumerateDirectories(basis).OrderDescending(StringComparer.OrdinalIgnoreCase).Select(folder => Path.Combine(folder, "blender.exe")).FirstOrDefault(File.Exists);
    }
    internal object Status() => new { enabled = Enabled, blender = FindBlender(preferences.BlenderExecutable) ?? "sin configurar", unity = "lector YAML local; no requiere Unity Editor",
        formats = Formats, maximumBytes = 128 * 1024 * 1024, pending = Pending, attached = Attached, scope = Limits };
    internal SceneInfo AttachHuman(string path)
    {
        skills.Require("scenes"); if (pending.Count >= 2 || pending.Concat(accepted).Sum(s => s.Info.Bytes) >= 256L * 1024 * 1024) throw new IOException("Máximo dos escenas pendientes y 256 MiB por conversación; retira las anteriores.");
        var snapshot = Copy(Path.GetFullPath(path), "scene-" + Guid.NewGuid().ToString("N")[..12]);
        if (pending.Concat(accepted).Sum(s => s.Info.Bytes) + snapshot.Info.Bytes > 256L * 1024 * 1024) { DeleteSnapshot(snapshot); throw new IOException("Los adjuntos 3D superan 256 MiB por conversación."); }
        pending.Add(snapshot); Changed?.Invoke(); return snapshot.Info;
    }
    internal SceneInfo[] AcceptPending(string? only = null)
    {
        skills.Require("scenes"); var selected = pending.Where(s => only is null || s.Info.Path == only).ToArray(); accepted.AddRange(selected); pending.RemoveAll(s => only is null || s.Info.Path == only);
        while (accepted.Count > 4) { DeleteSnapshot(accepted[0]); accepted.RemoveAt(0); }
        Changed?.Invoke(); return selected.Select(s => s.Info).ToArray();
    }
    internal void RemoveHuman(string id)
    {
        var snapshot = pending.Concat(accepted).FirstOrDefault(s => s.Info.Path == id) ?? throw new ArgumentException("No existe ese adjunto 3D.");
        Cancel(); DeleteSnapshot(snapshot); pending.Remove(snapshot); accepted.Remove(snapshot); Changed?.Invoke();
    }
    private void DeleteSnapshot(Snapshot snapshot) { cache.Remove(snapshot.Info.Sha256); if (File.Exists(snapshot.File)) File.Delete(snapshot.File); if (Directory.Exists(snapshot.Folder)) Directory.Delete(snapshot.Folder, false); }
    internal void ClearHuman() { Cancel(); foreach (var snapshot in pending.Concat(accepted).ToArray()) DeleteSnapshot(snapshot); pending.Clear(); accepted.Clear(); cache.Clear(); Changed?.Invoke(); }
    internal void Cancel() { try { reading?.Cancel(); } catch (ObjectDisposedException) { } }
    private static Snapshot Copy(string path, string id)
    {
        if (!IsScene(path)) throw new ArgumentException("Formatos 3D: " + Formats);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 128 * 1024 * 1024) throw new IOException("La escena supera 128 MiB.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var folder = Path.Combine(AppPaths.Temp, "scene-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "snapshot" + extension);
        try { File.WriteAllBytes(file, bytes); return new(new(id, Path.GetFileName(path), extension[1..], bytes.Length, AppPaths.Hash(bytes)), file, folder); }
        catch { if (File.Exists(file)) File.Delete(file); Directory.Delete(folder, false); throw; }
    }
    internal IReadOnlyList<string> ProjectFiles()
    { skills.Require("scenes"); return (workspace() ?? throw new InvalidOperationException("Abre el proyecto Unity/Blender primero.")).Files(5000).Where(IsScene).Take(200).ToArray(); }
    internal async Task<SceneRead> ReadAsync(string path, int start, int count, CancellationToken token)
    {
        var (info, data) = await LoadAsync(path, token); start = Math.Clamp(start, 0, data.Objects?.Length ?? 0); count = Math.Clamp(count, 1, 80);
        var objects = data.Objects ?? []; return new(info, data.Status, data.Engine, data.ObjectCount, data.MeshCount, data.VertexCount, data.FaceCount, data.Materials ?? [], objects.Skip(start).Take(count).ToArray(), start,
            data.Partial || start > 0 || start + count < objects.Length, data.Geometry?.Vertices.Length > 0, Limits + " " + data.Notice, data.Error);
    }
    private async Task<(SceneInfo, SceneData)> LoadAsync(string path, CancellationToken token)
    {
        skills.Require("scenes"); token.ThrowIfCancellationRequested(); await gate.WaitAsync(token);
        Snapshot? temporary = null;
        try
        {
            skills.Require("scenes"); var snapshot = accepted.FirstOrDefault(s => s.Info.Path == path);
            if (snapshot is null)
            {
                if (path.StartsWith("scene-", StringComparison.Ordinal)) throw new ArgumentException("La escena aún no se ha enviado o se retiró.");
                var project = workspace() ?? throw new InvalidOperationException("Adjunta el archivo 3D o abre su proyecto.");
                snapshot = temporary = Copy(project.Resolve(path), path);
            }
            if (cache.TryGetValue(snapshot.Info.Sha256, out var cached)) return (snapshot.Info, cached);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); reading = cancel; cancel.CancelAfter(TimeSpan.FromSeconds(90));
            SceneData data;
            try
            {
                data = snapshot.Info.Format is "unity" or "prefab" or "asset" ? ReadUnity(File.ReadAllBytes(snapshot.File)) : await ReadBlenderAsync(snapshot.File, cancel.Token);
                token.ThrowIfCancellationRequested(); skills.Require("scenes");
                if (data.Status == "inspected")
                {
                    while (cache.Count >= 8) cache.Remove(cache.Keys.First());
                    cache[snapshot.Info.Sha256] = data;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && Enabled) { data = new("timeout", Error: "La inspección superó 90 segundos; se detuvo su proceso propio."); }
            catch (Exception e) when (e is IOException or JsonException or System.ComponentModel.Win32Exception) { data = new("failed", Error: e.Message); }
            finally { reading = null; }
            return (snapshot.Info, data);
        }
        finally { if (temporary is not null) { if (File.Exists(temporary.File)) File.Delete(temporary.File); if (Directory.Exists(temporary.Folder)) Directory.Delete(temporary.Folder, false); } gate.Release(); }
    }
    private async Task<SceneData> ReadBlenderAsync(string file, CancellationToken token)
    {
        var executable = FindBlender(preferences.BlenderExecutable);
        if (executable is null) return new("unavailable", Error: "Falta Blender instalado. Usa «Configura Blender RUTA/blender.exe» o el botón Blender; Unity YAML sigue disponible.");
        var script = Path.Combine(Path.GetDirectoryName(file)!, "trusted-inspect.py");
        using (var resource = typeof(SceneTools).Assembly.GetManifestResourceStream("SheepCode.blender-inspect.py")!)
        using (var output = File.Create(script)) resource.CopyTo(output);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(file)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "7", "--python", script }) start.ArgumentList.Add(arg);
        start.Environment["PYTHONUTF8"] = "1"; start.Environment.Remove("PYTHONPATH"); start.Environment.Remove("PYTHONHOME");
        using var process = new Process { StartInfo = start }; var text = new StringBuilder(); var tail = new StringBuilder(); var sync = new object();
        void Output(object sender, DataReceivedEventArgs args) { if (args.Data is null) return; lock (sync) { if (args.Data.StartsWith("SHEEPCODE_SCENE_JSON=", StringComparison.Ordinal) && args.Data.Length < 8 * 1024 * 1024) text.Append(args.Data[21..]); else { tail.AppendLine(args.Data); if (tail.Length > 4000) tail.Remove(0, tail.Length - 2000); } } }
        process.OutputDataReceived += Output; process.ErrorDataReceived += Output;
        try
        {
            if (!process.Start()) throw new IOException("No se pudo iniciar el lector Blender.");
            using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { path = file })); process.StandardInput.Close();
            await process.WaitForExitAsync(token); process.WaitForExit();
            if (process.ExitCode != 0 || text.Length == 0) throw new IOException("Blender no pudo leer la escena: " + tail);
            return JsonSerializer.Deserialize<SceneData>(text.ToString(), AppPaths.Json) ?? throw new IOException("Blender devolvió un informe vacío.");
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } if (File.Exists(script)) File.Delete(script); }
    }
    internal static SceneData ReadUnity(byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) return new("unavailable", Error: "El YAML Unity supera 8 MiB de inspección; abre una escena o prefab más pequeño.");
        if (bytes.Contains((byte)0)) return new("unavailable", Error: "Archivo Unity binario. Guarda escenas/prefabs con Asset Serialization → Force Text para inspeccionarlos aquí.");
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
        var blocks = Regex.Matches(text, @"(?ms)^--- !u!(\d+) &(-?\d+)(?: stripped)?\n([\s\S]*?)(?=^--- !u!|\z)");
        if (blocks.Count == 0) return new("unavailable", Error: "No contiene documentos Unity YAML reconocibles (.unity/.prefab/.asset de texto).");
        var objects = new List<SceneObject>();
        foreach (Match block in blocks.Cast<Match>().Take(2000))
        {
            var body = block.Groups[3].Value; string Value(string key) => Regex.Match(body, @"(?m)^  " + Regex.Escape(key) + @":\s*([^\n]*)$").Groups[1].Value.Trim();
            string Ref(string key) => Regex.Match(Value(key), @"fileID:\s*(-?\d+)").Groups[1].Value;
            double[] Vector(string key) { var v = Value(key); return new[] { "x", "y", "z" }.Select(axis => double.TryParse(Regex.Match(v, axis + @":\s*([-+\d.eE]+)").Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : 0).ToArray(); }
            var type = Regex.Match(body, @"^([A-Za-z][A-Za-z0-9_]*):").Groups[1].Value;
            var name = Value("m_Name"); if (name.StartsWith('"')) { try { name = JsonSerializer.Deserialize<string>(name) ?? name; } catch (JsonException) { } }
            var components = Regex.Matches(body, @"component:\s*\{fileID:\s*(-?\d+)\}").Select(m => m.Groups[1].Value).ToArray();
            var references = Regex.Matches(body, @"guid:\s*([a-fA-F0-9]{32})\b").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().Take(80).ToArray();
            objects.Add(new(block.Groups[2].Value, name, type, type is "Transform" or "RectTransform" ? Ref("m_Father") : Ref("m_GameObject"), 0, 0, Vector("m_LocalPosition"), Vector("m_LocalScale"), [], components, references));
        }
        return new("inspected", "Unity YAML", "lector local de Unity YAML", blocks.Count, objects.Count(o => o.Type is "MeshFilter" or "SkinnedMeshRenderer"), Materials: [], Objects: objects.ToArray(), Partial: blocks.Count > objects.Count,
            Notice: "Los GUID se muestran como referencias; no se resuelven automáticamente ni se ejecuta/importa el proyecto. Las posiciones son locales. Binarios, AssetBundles y FBX embebidos requieren su lector separado.");
    }
    internal async Task<(ImageInfo Info, byte[] Png)> PreviewAsync(string path, CancellationToken token)
    {
        var (info, data) = await LoadAsync(path, token);
        if (data.Status != "inspected" || data.Geometry is not { Vertices.Length: > 0 } geometry) throw new IOException(data.Error.Length > 0 ? data.Error : "No hay geometría para una vista previa; Unity YAML ofrece estructura y referencias.");
        using var bitmap = Draw(geometry); using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); var png = stream.ToArray();
        return (new(info.Path, info.Name + " · vista geométrica", "PNG", bitmap.Width, bitmap.Height, png.Length, AppPaths.Hash(png), false, false), png);
    }
    internal static Bitmap Draw(SceneGeometry geometry)
    {
        var bitmap = new Bitmap(640, 480); using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var projected = geometry.Vertices.Select(v => new[] { (v[0] - v[1]) * .7071, (v[0] + v[1]) * .4082 - v[2] * .8165, v[0] + v[1] + v[2] }).ToArray();
        var minX = projected.Min(v => v[0]); var maxX = projected.Max(v => v[0]); var minY = projected.Min(v => v[1]); var maxY = projected.Max(v => v[1]);
        var scale = Math.Min(550 / Math.Max(.001, maxX - minX), 390 / Math.Max(.001, maxY - minY));
        PointF Point(int i) => new((float)(320 + (projected[i][0] - (minX + maxX) / 2) * scale), (float)(240 + (projected[i][1] - (minY + maxY) / 2) * scale));
        bool Valid(int[] indices, int length) => indices.Length == length && indices.All(i => i >= 0 && i < projected.Length);
        foreach (var triangle in geometry.Triangles.Where(t => Valid(t, 3)).OrderBy(t => t.Sum(i => projected[i][2])).Take(40000))
        { using var brush = new SolidBrush(Color.FromArgb(220, 180 + triangle[0] % 35, 200 + triangle[1] % 25, 235)); graphics.FillPolygon(brush, triangle.Select(Point).ToArray()); }
        using var pen = new Pen(Color.FromArgb(100, 65, 95, 145), 1);
        foreach (var edge in geometry.Edges.Where(e => Valid(e, 2)).Take(50000)) graphics.DrawLine(pen, Point(edge[0]), Point(edge[1]));
        return bitmap;
    }
    public void Dispose() => ClearHuman();
}
