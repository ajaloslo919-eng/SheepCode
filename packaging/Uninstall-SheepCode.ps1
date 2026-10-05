$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$taskInfo = Join-Path $taskRoot 'installation.json'
if (!(Test-Path -LiteralPath $taskInfo) -or (Get-Content -LiteralPath $taskInfo -Raw | ConvertFrom-Json).product -ne 'SheepCode') { throw 'Esta carpeta no es una instalación registrada de SheepCode.' }
Add-Type -AssemblyName System.Windows.Forms
$taskChoice = [Windows.Forms.MessageBox]::Show("Quitar SheepCode de esta carpeta:`n$taskRoot`n`nSe conservan código editable, modelos, proyectos, ajustes, sesiones, cambios y respaldos.", 'Sheep & Kuky · quitar aplicación', 'YesNo', 'Question')
if ($taskChoice -ne 'Yes') { return }
$taskHasher = [Security.Cryptography.SHA256]::Create()
try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskRoot.ToUpperInvariant()))).Replace('-','').Substring(0,12) } finally { $taskHasher.Dispose() }
try { $taskMutex = [Threading.Mutex]::OpenExisting('Local\SheepCode.GUI.'+$taskHash); if (!$taskMutex.WaitOne(0)) { $taskMutex.Dispose(); throw 'Cierra SheepCode y guarda el editor antes de quitarlo.' }; $taskMutex.ReleaseMutex(); $taskMutex.Dispose() } catch [Threading.WaitHandleCannotBeOpenedException] { }
foreach ($taskRelative in @('app','runtime\dotnet','runtime\llama')) {
    $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (!$taskTarget.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Destino de desinstalación fuera de SheepCode.' }
    if (Test-Path -LiteralPath $taskTarget) {
        if ((Get-Item -LiteralPath $taskTarget).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Se conserva una carpeta enlazada; retírala manualmente.' }
        Remove-Item -LiteralPath $taskTarget -Recurse -Force
    }
}
$taskLauncher = Join-Path $taskRoot 'SheepCode.exe'; if(Test-Path -LiteralPath $taskLauncher){Remove-Item -LiteralPath $taskLauncher}
$taskShell = New-Object -ComObject WScript.Shell
$taskLinks = @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'SheepCode.lnk'),(Join-Path ([Environment]::GetFolderPath('Desktop')) 'SheepCode 0.2.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) 'SheepCode\SheepCode.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) 'SheepCode\SheepCode 0.2.lnk'))
foreach($taskLink in $taskLinks){if((Test-Path -LiteralPath $taskLink) -and $taskShell.CreateShortcut($taskLink).TargetPath -eq $taskLauncher){Remove-Item -LiteralPath $taskLink}}
$taskRegistration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SheepCode-'+$taskHash
if(Test-Path -LiteralPath $taskRegistration){Remove-Item -LiteralPath $taskRegistration}
[Windows.Forms.MessageBox]::Show('Aplicación quitada. Tus archivos y modelos se han conservado.', 'SheepCode') | Out-Null
