using System.Diagnostics;

namespace SheepCode;

internal sealed record CheckSpec(string Id, string Label, string Executable, string[] Arguments);
internal sealed record CheckResult(string Id, int ExitCode, string Output, double Seconds);
internal sealed class ChecksRunner(ProjectWorkspace workspace)
{
    internal IReadOnlyList<CheckSpec> Available()
    {
        var files = workspace.Files();
        var result = new List<CheckSpec>();
        if (AppPaths.ToolExecutable("dotnet.exe") is { } dotnet && files.Any(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new("dotnet-build", "Compilar .NET", dotnet, ["build", "--nologo", "--verbosity", "minimal"]));
            result.Add(new("dotnet-test", "Pruebas .NET", dotnet, ["test", "--nologo", "--verbosity", "minimal"]));
        }
        if (AppPaths.ToolsPython is { } python && files.Any(x => x.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new("python-test", "Pruebas Python (unittest)", python, ["-m", "unittest", "discover", "-v"]));
        }
        if (files.Contains("package.json"))
        {
            // Invoke npm's CLI through node, never a shell or a string-built command line.
            var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            var npm = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(node) && File.Exists(npm))
            {
                using var package = System.Text.Json.JsonDocument.Parse(workspace.Read("package.json").Text);
                if (package.RootElement.TryGetProperty("scripts", out var scripts))
                    foreach (var script in new[] { "test", "lint", "check", "build" })
                        if (scripts.TryGetProperty(script, out _)) result.Add(new("npm-" + script, "npm run " + script, node, [npm, "run", script]));
            }
        }
        return result;
    }
    internal async Task<CheckResult> RunAsync(string id, bool permitted, Action<string>? output, CancellationToken token)
    {
        if (!permitted) throw new InvalidOperationException("Las comprobaciones están desactivadas. Actívalas en la interfaz o por una petición explícita.");
        var check = Available().FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Comprobación no disponible en este proyecto: " + id);
        var start = new ProcessStartInfo(check.Executable)
        {
            WorkingDirectory = workspace.Root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in check.Arguments) start.ArgumentList.Add(arg);
        var log = new System.Text.StringBuilder();
        var sync = new object();
        void Receive(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null) return;
            lock (sync) { if (log.Length < 64000) log.AppendLine(args.Data); }
            output?.Invoke(args.Data);
        }
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += Receive; process.ErrorDataReceived += Receive;
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        if (!process.Start()) throw new InvalidOperationException("No se pudo iniciar la comprobación.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(limit.Token); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var result = new CheckResult(id, process.ExitCode, log.ToString(), clock.Elapsed.TotalSeconds);
        AppPaths.SaveJson(Path.Combine(AppPaths.Logs, "check-" + Guid.NewGuid().ToString("N")[..8] + ".json"), new { project = workspace.Root, result });
        return result;
    }
}
