using System.Diagnostics;
using System.Text.Json;
using System.IO.Compression;
using SheepCode.Distribution;

namespace SheepCode;

internal sealed record PayloadFile(string Path, string Sha256, long Size);
internal sealed record InstallRequest(string Root, string Models, InstallPlan Plan, bool DesktopShortcut, bool MenuShortcut, bool Register = true, bool Dictation = true);
internal static class Installer
{
    internal static string BundleRoot => AppContext.BaseDirectory;
    internal static async Task InstallAsync(InstallRequest request, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var root = Path.GetFullPath(request.Root).TrimEnd('\\');
        if (root == Path.GetPathRoot(root)!.TrimEnd('\\') || root.Length < 8) throw new IOException("Elige una carpeta propia para SheepCode.");
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("La carpeta de instalación no puede ser un enlace.");
        var mutexName = "Local\\SheepCode.GUI." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12];
        try
        {
            using var mutex = Mutex.OpenExisting(mutexName); bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Cierra SheepCode en esta carpeta y guarda el editor antes de actualizarlo.");
            mutex.ReleaseMutex();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        foreach (var process in Process.GetProcessesByName("SheepCode"))
        {
            using (process)
                try { if (!process.HasExited && process.MainModule?.FileName?.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) == true) throw new IOException("Cierra SheepCode en esta carpeta para actualizarlo y guardar el editor."); }
                catch (System.ComponentModel.Win32Exception) { }
        }
        Directory.CreateDirectory(root);
        var stage = SafeFiles.Child(root, @"temp\install-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(BundleRoot, "SheepCode.payload.zip");
        progress?.Report(new("Comprobando el paquete", "Verificando todos los archivos antes de instalarlos."));
        SafeFiles.ExtractZip(bundle, stage, token);
        var files = JsonSerializer.Deserialize<PayloadFile[]>(File.ReadAllText(Path.Combine(stage, "payload-manifest.json")), DistributionJson.Options)!;
        foreach (var item in files)
        {
            var file = SafeFiles.Child(stage, item.Path);
            if (new FileInfo(file).Length != item.Size || !string.Equals(await SafeFiles.HashAsync(file, token), item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archivo del paquete alterado: " + item.Path);
        }
        var backup = SafeFiles.Child(root, @"backups\before-setup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var oldFiles = new List<PayloadFile>();
        foreach (var relative in new[] { "app", "source" })
        {
            var existing = SafeFiles.Child(root, relative);
            if (!Directory.Exists(existing)) continue;
            progress?.Report(new("Guardando respaldo", "Conservando tu versión anterior de " + relative));
            foreach (var file in Directory.EnumerateFiles(existing, "*", SearchOption.AllDirectories))
            {
                var path = Path.GetRelativePath(root, file); SafeFiles.Child(root, path);
                var copy = SafeFiles.Child(backup, path); Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(file, copy);
                oldFiles.Add(new(path, await SafeFiles.HashAsync(copy, token), new FileInfo(copy).Length));
            }
        }
        if (oldFiles.Count > 0) DistributionJson.Save(Path.Combine(backup, "manifest.json"), oldFiles);
        progress?.Report(new("Instalando Sheep y Kuky", "Editor, skills y código fuente editable 🌸"));
        var managed = new[] { "app", "source" }; var moved = new List<string>(); var committed = new List<string>();
        try
        {
            foreach (var folder in managed)
            {
                token.ThrowIfCancellationRequested(); var old = SafeFiles.Child(root, folder); var preserved = SafeFiles.Child(backup, "original-" + folder);
                if (Directory.Exists(old)) { Directory.CreateDirectory(backup); Directory.Move(old, preserved); moved.Add(folder); }
                Directory.Move(SafeFiles.Child(stage, folder), old); committed.Add(folder);
            }
            foreach (var item in files.Where(f => !managed.Any(d => f.Path.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))))
            {
                token.ThrowIfCancellationRequested(); var target = SafeFiles.Child(root, item.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(SafeFiles.Child(stage, item.Path), target, true);
            }
        }
        catch
        {
            foreach (var folder in committed.AsEnumerable().Reverse()) Directory.Move(SafeFiles.Child(root, folder), SafeFiles.Child(stage, "interrupted-" + folder));
            foreach (var folder in moved) Directory.Move(SafeFiles.Child(backup, "original-" + folder), SafeFiles.Child(root, folder));
            throw;
        }
        SafeFiles.CopyTree(Path.Combine(BundleRoot, "runtime", "dotnet"), Path.Combine(root, "runtime", "dotnet"), token);
        foreach (var folder in new[] { "state", "logs", "sessions", "changes", "checks" }) Directory.CreateDirectory(SafeFiles.Child(root, folder));
        var pathsFile = Path.Combine(root, "state", "paths.json");
        // Let reuse resolve the original paths before saving them. Writing defaults here
        // would hide the legacy installation's shared model and neural-voice locations.
        if (!File.Exists(pathsFile) && request.Plan.Kind != "reuse") DistributionJson.Save(pathsFile, new InstallationPaths { Models = Path.GetFullPath(request.Models) });
        var prefs = Path.Combine(root, "state", "preferences.json");
        if (!File.Exists(prefs)) DistributionJson.Save(prefs, new { lastProject = Path.Combine(root, "source"), reasoning = "none", readAloud = false, allowChecks = false, emotion = "calmness", maximumSteps = 10, disabledSkills = Array.Empty<string>() });
        DistributionJson.Save(Path.Combine(root, "installation.json"), new { product = "SheepCode", version = "0.3.0", installed = DateTimeOffset.UtcNow, source = Path.Combine(root, "source"), models = request.Models, selected = request.Plan.Id, setup = Environment.ProcessPath });
        await WriteShortcutsAsync(root, request.DesktopShortcut, request.MenuShortcut, token);
        if (request.Register)
        {
            try { Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                progress?.Report(new("Preparando las pestañas", "Instalando WebView2 de Microsoft para este usuario."));
                await ModelProvisioner.RunAsync(Path.Combine(root, "runtime", "packages", "MicrosoftEdgeWebview2Setup.exe"), ["/silent", "/install"], root, progress, token);
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            using var registration = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SheepCode-" + mutexName[^12..]);
            registration.SetValue("DisplayName", "SheepCode · Sheep & Kuky"); registration.SetValue("DisplayVersion", "0.3.0"); registration.SetValue("InstallLocation", root);
            registration.SetValue("DisplayIcon", Path.Combine(root, "app", "SheepCode.exe")); registration.SetValue("Publisher", "SheepCode contributors");
            registration.SetValue("UninstallString", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(root, "Uninstall-SheepCode.ps1") + "\"");
        }
        var hardware = HardwareScanner.Scan(request.Models);
        await ModelProvisioner.InstallAsync(root, request.Models, request.Plan, hardware, progress, token);
        if (request.Dictation) await ModelProvisioner.InstallDictationAsync(root, progress, token);
        DistributionJson.Save(Path.Combine(root, "checks", "setup-result.json"), new { status = "complete", installed = DateTimeOffset.UtcNow, root, editableSource = File.Exists(Path.Combine(root, "source", "app", "SheepCode.csproj")), selected = request.Plan, hardware,
            coreFilesVerified = files.Length, profile = RuntimeProfile.Load(root), neuralVoice = request.Plan.Kind == "reuse" ? "Voz neuronal RX 580 original conservada" : "Sin configurar; no se sustituye la voz neuronal original" });
        progress?.Report(new("Todo preparado 🐑🐱", "SheepCode y su código editable están listos.", 1));
        // Only remove files extracted by this invocation; do not recursively remove a computed install path.
        foreach (var item in files) { var file = SafeFiles.Child(stage, item.Path); if (File.Exists(file)) File.Delete(file); }
        File.Delete(Path.Combine(stage, "payload-manifest.json"));
    }
    private static async Task WriteShortcutsAsync(string root, bool desktop, bool menu, CancellationToken token)
    {
        if (!desktop && !menu) return;
        var script = "param([string]$InstallRoot,[switch]$Desktop,[switch]$Menu)\n" +
            "$ErrorActionPreference='Stop'\n$w=New-Object -ComObject WScript.Shell\n" +
            "$targets=@(); if($Desktop){$targets+=Join-Path ([Environment]::GetFolderPath('Desktop')) 'SheepCode.lnk'}; if($Menu){$folder=Join-Path ([Environment]::GetFolderPath('Programs')) 'SheepCode'; New-Item -ItemType Directory -Path $folder -Force|Out-Null; $targets+=Join-Path $folder 'SheepCode.lnk'}\n" +
            "foreach($target in $targets){if(Test-Path -LiteralPath $target){$old=$w.CreateShortcut($target); if($old.TargetPath -ne (Join-Path $InstallRoot 'SheepCode.exe')){ $target=$target.Replace('.lnk',' 0.2.lnk')}}; $s=$w.CreateShortcut($target); $s.TargetPath=Join-Path $InstallRoot 'SheepCode.exe'; $s.WorkingDirectory=$InstallRoot; $s.IconLocation=(Join-Path $InstallRoot 'app\\SheepCode.exe')+',0'; $s.Description='Sheep & Kuky · agente local de código'; $s.Save()}\n";
        var file = Path.Combine(root, "temp", "shortcuts.ps1"); File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, "-InstallRoot", root }; if (desktop) args.Add("-Desktop"); if (menu) args.Add("-Menu");
        await ModelProvisioner.RunAsync(shell, args, root, null, token);
    }
    internal static void Launch(string root) => Process.Start(new ProcessStartInfo(Path.Combine(root, "SheepCode.exe")) { WorkingDirectory = root, UseShellExecute = true });
}
