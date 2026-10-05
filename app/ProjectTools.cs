using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SheepCode;

internal static class ProjectTools
{
    internal static async Task<string> GitAsync(ProjectWorkspace workspace, string operation, CancellationToken token)
    {
        if (!Directory.Exists(Path.Combine(workspace.Root, ".git")) && !File.Exists(Path.Combine(workspace.Root, ".git"))) throw new IOException("La carpeta abierta no es la raíz de un repositorio Git.");
        var git = AppPaths.ToolExecutable("git.exe") ?? throw new IOException("Git está sin configurar; instala Git para Windows y recarga SheepCode.");
        var arguments = operation switch {
            "status" => new[] { "status", "--short", "--branch", "--untracked-files=normal" },
            "log" => ["log", "-12", "--oneline", "--decorate"],
            "diff" => ["diff", "--no-ext-diff", "--no-textconv", "--stat"],
            "branches" => ["branch", "--list"],
            _ => throw new ArgumentException("Git admite status, log, diff y branches; publicación y merge requieren una conexión autorizada.")
        };
        var start = new ProcessStartInfo(git) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "--no-pager", "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false" }.Concat(arguments)) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GCM_INTERACTIVE"] = "never";
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar Git.");
        var output = process.StandardOutput.ReadToEndAsync(token); var errors = process.StandardError.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(limit.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output; var error = await errors;
        return JsonSerializer.Serialize(new { operation, exitCode = process.ExitCode, text = text[..Math.Min(6000, text.Length)], error = error[..Math.Min(1000, error.Length)], scope = "Consulta local; no ejecuta hooks, cambia archivos ni contacta remotos." });
    }
    internal static string Backup(ProjectWorkspace workspace)
    {
        var target = Path.Combine(AppPaths.Root, "backups", "projects", "project-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); var files = workspace.Files(2001);
        if (files.Count > 2000) throw new IOException("Más de 2000 archivos: usa el respaldo completo fuera del agente.");
        var total = 0L; var count = 0;
        try
        {
            using (var output = File.Create(target)) using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var path in files)
                {
                    var file = workspace.Resolve(path); total += new FileInfo(file).Length;
                    if (total > 100 * 1024 * 1024) throw new IOException("El respaldo del proyecto supera 100 MiB.");
                    zip.CreateEntryFromFile(file, path, CompressionLevel.Optimal); count++;
                }
            using var verification = ZipFile.OpenRead(target); if (verification.Entries.Count != count) throw new IOException("El respaldo no se verificó.");
            return JsonSerializer.Serialize(new { path = target, files = count, bytes = total, sha256 = AppPaths.Hash(File.ReadAllBytes(target)), scope = "Copia de los archivos accesibles del proyecto; excluye secretos, .git, dependencias y enlaces. No sustituye un respaldo completo." });
        }
        catch { if (File.Exists(target)) File.Delete(target); throw; }
    }
}
