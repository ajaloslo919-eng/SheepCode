using System.Text;
using System.Text.Json;

namespace SheepCode;

internal sealed record FileText(string Path, string Text, string Hash, bool Bom, string Newline);
internal sealed class ProjectWorkspace
{
    internal string Root { get; }
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", "node_modules", "bin", "obj", "dist", "build", "vendor", ".venv", "venv", "__pycache__", ".idea", ".vs", ".aws", ".ssh", "Library", "Temp", "UserSettings", "Logs" };
    private static bool Secret(string name) => name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("api.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("credentials", StringComparison.OrdinalIgnoreCase);
    private static bool Managed(string path) => new[] { "state", "temp", "sessions", "changes", "logs", "runtime", "backups", "app" }.Any(folder =>
        path.Equals(Path.Combine(AppPaths.Root, folder), StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.Combine(AppPaths.Root, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    internal ProjectWorkspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("No existe la carpeta del proyecto.");
        CheckLinks(Root);
    }
    internal string Resolve(string relative, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidOperationException("Usa una ruta relativa dentro del proyecto.");
        var result = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (Managed(result)) throw new InvalidOperationException("Los ajustes, permisos y binarios instalados solo se modifican desde sus controles humanos. Abre source para editar el código.");
        if (!result.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !(directory && result.Equals(Root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("La ruta sale del proyecto abierto.");
        foreach (var segment in Path.GetRelativePath(Root, result).Split(Path.DirectorySeparatorChar))
            if (Ignored.Contains(segment) || Secret(segment)) throw new InvalidOperationException("Ruta excluida del agente: " + relative);
        CheckLinks(result);
        return result;
    }
    private static void CheckLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("El agente no atraviesa enlaces ni junctions: " + current);
    }
    internal IReadOnlyList<string> Files(int maximum = 1200)
    {
        var files = new List<string>();
        void Visit(string dir)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (files.Count >= maximum) return;
                var name = Path.GetFileName(path);
                if (Ignored.Contains(name) || Secret(name) || Managed(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(path)) Visit(path);
                else files.Add(Path.GetRelativePath(Root, path).Replace('\\', '/'));
            }
        }
        Visit(Root); return files;
    }
    internal FileText Read(string relative)
    {
        var bytes = File.ReadAllBytes(Resolve(relative));
        if (bytes.Length > 256 * 1024) throw new InvalidOperationException("Este archivo supera los 256 KiB del editor del agente.");
        if (bytes.Contains((byte)0)) throw new InvalidOperationException("Este archivo es binario o usa una codificación que no es UTF-8.");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var text = new UTF8Encoding(false, true).GetString(bytes.AsSpan(bom ? 3 : 0));
        return new(relative, text, AppPaths.Hash(bytes), bom, text.Contains("\r\n") ? "\r\n" : "\n");
    }
    internal byte[] ReadBytes(string relative)
    {
        var path = Resolve(relative); if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Máximo 4 MiB por artefacto.");
        return File.ReadAllBytes(path);
    }
    internal void WriteBytes(string relative, byte[] bytes, string? expectedHash)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("Máximo 4 MiB por artefacto.");
        var path = Resolve(relative);
        bool Changed() => expectedHash is null ? File.Exists(path) : !File.Exists(path) || AppPaths.Hash(ReadBytes(relative)) != expectedHash;
        if (Changed()) throw new IOException("El archivo cambió desde su lectura; conserva la edición actual.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); CheckLinks(path);
        var temporary = path + ".sheepcode-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); if (Changed()) throw new IOException("Edición concurrente; no se sobrescribió el archivo."); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal string ReadLines(string path, int start = 1, int count = 100)
    {
        var text = Read(path).Text.Replace("\r\n", "\n").Split('\n');
        start = Math.Clamp(start, 1, Math.Max(1, text.Length)); count = Math.Clamp(count, 1, 120);
        var result = string.Join('\n', text.Skip(start - 1).Take(count).Select((line, i) => $"{start + i}: {line}"));
        return result.Length > 9000 ? result[..9000] + "\n[Usa start_line para leer el resto.]" : result;
    }
    internal string Search(string text, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) throw new ArgumentException("La búsqueda debe tener entre 1 y 200 caracteres.");
        var results = new List<string>();
        foreach (var file in path is null ? Files(600) : new[] { path })
        {
            FileText content;
            try { content = Read(file); } catch (Exception e) when (e is IOException or InvalidOperationException or DecoderFallbackException) { continue; }
            foreach (var (line, index) in content.Text.Replace("\r\n", "\n").Split('\n').Select((l, i) => (l, i)))
            {
                if (!line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add($"{file}:{index + 1}: {line[..Math.Min(line.Length, 220)]}");
                if (results.Count >= 35) return string.Join('\n', results);
            }
        }
        return results.Count == 0 ? "Sin coincidencias." : string.Join('\n', results);
    }
    internal void Write(string relative, string text, string? expectedHash, bool bom = false, string newline = "\n")
    {
        if (Encoding.UTF8.GetByteCount(text) > 256 * 1024) throw new InvalidOperationException("El cambio supera los 256 KiB.");
        var path = Resolve(relative);
        if (expectedHash is null ? File.Exists(path) : !File.Exists(path) || AppPaths.Hash(File.ReadAllBytes(path)) != expectedHash)
            throw new InvalidOperationException("El archivo cambió desde su lectura. Revisa de nuevo antes de guardar.");
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", newline);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckLinks(path);
        var temporary = path + ".sheepcode-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, normalized, new UTF8Encoding(bom));
            // Recheck after the temporary write as well; never overwrite a concurrently changed editor file.
            if (expectedHash is null ? File.Exists(path) : !File.Exists(path) || AppPaths.Hash(File.ReadAllBytes(path)) != expectedHash)
                throw new InvalidOperationException("Hay una edición concurrente del archivo.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class ProposedChange
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Project { get; set; } = "";
    public string Path { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string? BeforeHash { get; set; }
    public string? AppliedHash { get; set; }
    public bool Bom { get; set; }
    public string Newline { get; set; } = "\n";
    public string Status { get; set; } = "pending";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Reason { get; set; } = "";
    public string? ArtifactFormat { get; set; }
    public string? BeforeBinary { get; set; }
    public string? AfterBinary { get; set; }
    public CodeValidation? Validation { get; set; }
    internal void Save() => AppPaths.SaveJson(System.IO.Path.Combine(AppPaths.Changes, Id + ".json"), this);
    internal string Diff()
    {
        if (ArtifactFormat is not null) return $"🐑 Artefacto {ArtifactFormat.ToUpperInvariant()} · {Path}\n\nActual: {Before}\n\nContenido propuesto:\n{After}\n\nAplicar guarda el documento; Deshacer conserva las ediciones posteriores.";
        var oldLines = Before.Replace("\r\n", "\n").Split('\n');
        var newLines = After.Replace("\r\n", "\n").Split('\n');
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        var builder = new StringBuilder($"--- {Path} (actual)\n+++ {Path} (propuesto)\n@@ desde línea {prefix + 1} @@\n");
        foreach (var line in oldLines.Skip(Math.Max(0, prefix - 3)).Take(Math.Min(prefix, 3))) builder.AppendLine(" " + line);
        foreach (var line in oldLines.Skip(prefix).Take(oldLines.Length - prefix - suffix)) builder.AppendLine("-" + line);
        foreach (var line in newLines.Skip(prefix).Take(newLines.Length - prefix - suffix)) builder.AppendLine("+" + line);
        foreach (var line in newLines.Skip(newLines.Length - suffix).Take(3)) builder.AppendLine(" " + line);
        return builder.ToString();
    }
}

internal sealed class ChangeStore
{
    private readonly ProjectWorkspace _workspace;
    internal List<ProposedChange> Items { get; } = [];
    internal event Action<ProposedChange>? Changed;
    internal ChangeStore(ProjectWorkspace workspace)
    {
        _workspace = workspace;
        foreach (var path in Directory.EnumerateFiles(AppPaths.Changes, "*.json"))
        {
            var item = JsonSerializer.Deserialize<ProposedChange>(File.ReadAllText(path), AppPaths.Json);
            if (item?.Project.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase) == true) Items.Add(item);
        }
    }
    internal ProposedChange Propose(string path, string content, string reason)
        => ProposeCore(path, content, reason, null, null, false);
    internal ProposedChange ProposeValidated(string path, string content, string reason, string? expectedHash, CodeValidation validation)
    {
        if (!validation.CanPropose || validation.Path != path || validation.ContentHash != AppPaths.HashText(content))
            throw new InvalidOperationException("La revisión no corresponde al contenido propuesto.");
        return ProposeCore(path, content, reason, expectedHash, validation, true);
    }
    private ProposedChange ProposeCore(string path, string content, string reason, string? expectedHash, CodeValidation? validation, bool compareHash)
    {
        FileText? before = File.Exists(_workspace.Resolve(path)) ? _workspace.Read(path) : null;
        if (compareHash && (expectedHash is null ? before is not null : before?.Hash != expectedHash))
            throw new IOException("El archivo cambió durante la revisión automática. Lee su versión actual antes de volver a proponerlo.");
        if (content == before?.Text) throw new InvalidOperationException("La propuesta no cambia el archivo.");
        if (Encoding.UTF8.GetByteCount(content) > 256 * 1024) throw new InvalidOperationException("La propuesta supera los 256 KiB.");
        // Repeated tool output must not replace a reviewed proposal or create a
        // new ID. Only reuse a report for the same current file and exact source.
        if (validation is not null && Items.LastOrDefault(c => c.Status == "pending" && c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            c.ArtifactFormat is null && c.BeforeHash == before?.Hash && c.After == content && c.Validation?.Status == validation.Status &&
            c.Validation.ContentHash == validation.ContentHash) is { } identical)
        {
            identical.Validation = validation; identical.Save(); return identical;
        }
        foreach (var old in Items.Where(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Status == "pending")) { old.Status = "superseded"; old.Save(); }
        var item = new ProposedChange { Project = _workspace.Root, Path = path, Before = before?.Text ?? "", After = content,
            BeforeHash = before?.Hash, Bom = before?.Bom ?? false, Newline = before?.Newline ?? "\n", Reason = reason, Validation = validation };
        item.Save(); Items.Add(item); Changed?.Invoke(item); return item;
    }
    internal ProposedChange Get(string id) => Items.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("No existe ese cambio en el proyecto abierto.");
    internal ProposedChange ProposeArtifact(string path, string format, string content, string reason, string? expectedHash = null, CodeValidation? validation = null)
    {
        if (validation is not null && (!validation.CanPropose || validation.Path != path || validation.ContentHash != AppPaths.HashText(content)))
            throw new InvalidOperationException("La revisión no corresponde al artefacto propuesto.");
        if (Path.GetExtension(path).TrimStart('.').ToLowerInvariant() != format.ToLowerInvariant()) throw new ArgumentException("La extensión debe coincidir con el formato.");
        var target = _workspace.Resolve(path); var before = File.Exists(target) ? _workspace.ReadBytes(path) : null;
        if (expectedHash is null ? before is not null : before is null || AppPaths.Hash(before) != expectedHash) throw new IOException("Lee el documento actual antes de proponer una sustitución; cambió desde la última lectura.");
        var after = ArtifactTools.Create(format, content);
        if (before is not null && before.SequenceEqual(after)) throw new IOException("La propuesta no cambia el documento.");
        foreach (var old in Items.Where(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Status == "pending")) { old.Status = "superseded"; old.Save(); }
        var item = new ProposedChange { Project = _workspace.Root, Path = path, ArtifactFormat = format, Before = before is null ? "No existe" : "SHA-256 " + AppPaths.Hash(before), After = content,
            BeforeHash = before is null ? null : AppPaths.Hash(before), BeforeBinary = before is null ? null : Convert.ToBase64String(before), AfterBinary = Convert.ToBase64String(after), Reason = reason, Validation = validation };
        item.Save(); Items.Add(item); Changed?.Invoke(item); return item;
    }
    internal void Apply(string id)
    {
        var item = Get(id);
        if (item.Status != "pending") throw new InvalidOperationException("Este cambio ya no está pendiente.");
        if (item.ArtifactFormat is not null) _workspace.WriteBytes(item.Path, Convert.FromBase64String(item.AfterBinary!), item.BeforeHash);
        else _workspace.Write(item.Path, item.After, item.BeforeHash, item.Bom, item.Newline);
        item.AppliedHash = AppPaths.Hash(_workspace.ReadBytes(item.Path)); item.Status = "applied"; item.Save(); Changed?.Invoke(item);
    }
    internal void Reject(string id) { var item = Get(id); if (item.Status != "pending") throw new InvalidOperationException("Este cambio ya no está pendiente."); item.Status = "rejected"; item.Save(); Changed?.Invoke(item); }
    internal void Undo(string id)
    {
        var item = Get(id);
        if (item.Status != "applied") throw new InvalidOperationException("Este cambio no está aplicado.");
        if (item.BeforeHash is null)
        {
            var path = _workspace.Resolve(item.Path);
            if (AppPaths.Hash(File.ReadAllBytes(path)) != item.AppliedHash) throw new InvalidOperationException("El archivo cambió después; se conserva tu edición.");
            File.Delete(path);
        }
        else if (item.ArtifactFormat is not null) _workspace.WriteBytes(item.Path, Convert.FromBase64String(item.BeforeBinary!), item.AppliedHash);
        else _workspace.Write(item.Path, item.Before, item.AppliedHash, item.Bom, item.Newline);
        item.Status = "undone"; item.Save(); Changed?.Invoke(item);
    }
}
