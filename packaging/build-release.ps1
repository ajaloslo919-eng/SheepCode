param([switch]$ReuseDownloads)
$ErrorActionPreference = 'Stop'
$taskPackaging = [IO.Path]::GetFullPath($PSScriptRoot)
$taskWork = Split-Path $taskPackaging -Parent
$taskApp = if(Test-Path -LiteralPath (Join-Path $taskWork 'app\SheepCode.csproj')){Join-Path $taskWork 'app'}else{Join-Path $taskWork 'source'}
$taskDownloads = Join-Path $taskPackaging 'downloads'
$taskBuild = [IO.Path]::GetFullPath((Join-Path $taskPackaging 'build'))
if (!$taskBuild.StartsWith($taskPackaging+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $taskBuild -Leaf) -ne 'build') { throw 'Carpeta de empaquetado fuera del destino permitido.' }
if(Test-Path -LiteralPath $taskBuild){Remove-Item -LiteralPath $taskBuild -Recurse -Force}
New-Item -ItemType Directory -Path $taskBuild,$taskDownloads -Force|Out-Null
$taskMeta=Get-Content (Join-Path $taskPackaging 'upstream-metadata.json') -Raw|ConvertFrom-Json
$taskValidation=Get-Content (Join-Path $taskPackaging 'code-validation.json') -Raw|ConvertFrom-Json
$taskPinned=@(
    @{file='dotnet-runtime-latest.zip';url=$taskMeta.dotnet.url;hash=$taskMeta.dotnet.hash;algorithm='SHA512'},
    @{file='windowsdesktop-runtime-latest.zip';url=$taskMeta.windowsdesktop.url;hash=$taskMeta.windowsdesktop.hash;algorithm='SHA512'},
    @{file='llama-cpu.zip';url='https://github.com/ggml-org/llama.cpp/releases/download/b11146/llama-b11146-bin-win-cpu-x64.zip';hash='14cf1303ca9ac3abd94816850532f9f9a69ac66fbaca3776fc6f9061c2fac1d1';algorithm='SHA256'},
    @{file='llama-vulkan.zip';url='https://github.com/ggml-org/llama.cpp/releases/download/b11146/llama-b11146-bin-win-vulkan-x64.zip';hash='55a378aa095b466979d85075234f66d7655c7a7483222af0c006c0e55b4d7bd6';algorithm='SHA256'},
    @{file='strata-source.zip';url=('https://github.com/Niko1221/Strata/archive/'+$taskMeta.strataRevision+'.zip');hash=$taskMeta.strataSha256;algorithm='SHA256'},
    @{file='python-3.12.10-amd64.exe';url='https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe';hash=$taskMeta.pythonSha256;algorithm='SHA256'},
    @{file=$taskValidation.python.name;url=$taskValidation.python.url;hash=$taskValidation.python.sha256;algorithm='SHA256'},
    @{file='MicrosoftEdgeWebview2Setup.exe';url='https://go.microsoft.com/fwlink/p/?LinkId=2124703';hash=(Get-Content (Join-Path $taskPackaging 'webview2.json') -Raw|ConvertFrom-Json).webview2Sha256;algorithm='SHA256'})
foreach($taskPin in $taskPinned){
    $taskFile=Join-Path $taskDownloads $taskPin.file
    if(!(Test-Path -LiteralPath $taskFile)){if($ReuseDownloads){throw ('Falta el archivo fijado '+$taskPin.file)}; Invoke-WebRequest -Uri $taskPin.url -OutFile $taskFile}
    if((Get-FileHash -LiteralPath $taskFile -Algorithm $taskPin.algorithm).Hash -ne $taskPin.hash){throw ('Hash incorrecto: '+$taskPin.file)}
}
$taskCpuPackage=Join-Path $taskDownloads 'strata-cpu.zip'
$taskCpuMeta=Get-Content -LiteralPath (Join-Path $taskWork 'shared\strata-cpu.json') -Raw | ConvertFrom-Json
if(!(Test-Path -LiteralPath $taskCpuPackage) -or (Get-FileHash -LiteralPath $taskCpuPackage).Hash -ne $taskCpuMeta.sha256){throw 'Compila primero Strata CPU con packaging/build-strata-cpu.ps1; falta su paquete verificado.'}
& dotnet build (Join-Path $taskApp 'SheepCode.csproj') -c Release --nologo --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Falló la compilación de SheepCode.'}
& dotnet build (Join-Path $taskWork 'setup\SheepCode.Setup.csproj') -c Release --nologo --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Falló la compilación del setup.'}
if($taskApp -like '*\app'){Set-Content (Join-Path $taskPackaging 'launcher.rc') ('1 ICON "../app/sheep.ico"'+"`r`n"+'1 24 "../app/app.manifest"') -Encoding ascii}
& $env:ComSpec /d /c (Join-Path $taskPackaging 'build-native.cmd') launcher
if($LASTEXITCODE -ne 0){throw 'Falló el lanzador nativo.'}
$taskPayload=Join-Path $taskBuild 'payload';$taskBundle=Join-Path $taskBuild 'bundle'
New-Item -ItemType Directory -Path $taskPayload,$taskBundle,(Join-Path $taskPayload 'source\app'),(Join-Path $taskPayload 'runtime\packages') -Force|Out-Null
function Copy-SourceTree([string]$From,[string]$To){
    foreach($taskSourceFile in Get-ChildItem -LiteralPath $From -File -Recurse){
        $taskRelative=[IO.Path]::GetRelativePath($From,$taskSourceFile.FullName)
        if($taskRelative -match '(^|[\\/])(bin|obj|build(?:-[^\\/]+)?|downloads|tool-probe|backups|checks|__pycache__)([\\/]|$)' -or $taskRelative -match '\.(obj|res|exe|zip|pyc)$' -or $taskRelative -eq 'bootstrap.rc'){continue}
        $taskCopy=Join-Path $To $taskRelative;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskCopy)) -Force|Out-Null;Copy-Item -LiteralPath $taskSourceFile.FullName -Destination $taskCopy
    }
}
Copy-SourceTree $taskApp (Join-Path $taskPayload 'source\app')
Copy-SourceTree (Join-Path $taskWork 'shared') (Join-Path $taskPayload 'source\shared')
Copy-SourceTree (Join-Path $taskWork 'setup') (Join-Path $taskPayload 'source\setup')
Copy-SourceTree (Join-Path $taskWork 'engine') (Join-Path $taskPayload 'source\engine')
Copy-SourceTree $taskPackaging (Join-Path $taskPayload 'source\packaging')
Expand-Archive -LiteralPath (Join-Path $taskDownloads $taskValidation.python.name) -DestinationPath (Join-Path $taskPayload 'runtime\code-validation\python') -Force
Copy-Item (Join-Path $taskPackaging 'build-source.ps1') (Join-Path $taskPayload 'source\build-source.ps1')
Copy-Item (Join-Path $taskPackaging 'SheepCode.sln') (Join-Path $taskPayload 'source\SheepCode.sln')
Copy-Item (Join-Path $taskPackaging 'GUIDE.md') (Join-Path $taskPayload 'README.md')
Copy-Item (Join-Path $taskPackaging 'LICENSE') (Join-Path $taskPayload 'LICENSE')
Copy-Item (Join-Path $taskPackaging 'Uninstall-SheepCode.ps1') (Join-Path $taskPayload 'Uninstall-SheepCode.ps1')
Copy-Item (Join-Path $taskPackaging 'SheepCode.exe') (Join-Path $taskPayload 'SheepCode.exe')
Copy-Item (Join-Path $taskApp 'bin\Release\net8.0-windows') (Join-Path $taskPayload 'app') -Recurse
Copy-Item (Join-Path $taskPackaging 'licenses') (Join-Path $taskPayload 'licenses') -Recurse
# Windows x64 distribution: omit unused native platforms while retaining code and package references for rebuilding.
$taskRuntimeRoot=Join-Path $taskPayload 'app\runtimes'
if(Test-Path -LiteralPath $taskRuntimeRoot){foreach($taskPlatform in Get-ChildItem -LiteralPath $taskRuntimeRoot -Directory){if($taskPlatform.Name -ne 'win-x64'){$taskSafe=[IO.Path]::GetFullPath($taskPlatform.FullName);if(!$taskSafe.StartsWith([IO.Path]::GetFullPath($taskRuntimeRoot)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime fuera del paquete'};Remove-Item -LiteralPath $taskSafe -Recurse -Force}}}
foreach($taskPackage in @('llama-cpu.zip','llama-vulkan.zip','strata-source.zip','python-3.12.10-amd64.exe','MicrosoftEdgeWebview2Setup.exe')){Copy-Item (Join-Path $taskDownloads $taskPackage) (Join-Path $taskPayload 'runtime\packages')}
Copy-Item -LiteralPath $taskCpuPackage -Destination (Join-Path $taskPayload 'runtime\packages')
$taskCrt='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT'
if(!(Test-Path -LiteralPath $taskCrt)){throw 'Se requieren los redistribuibles CRT x64 de Visual Studio para la instalación local de los motores.'}
Get-ChildItem -LiteralPath $taskCrt -Filter '*.dll'|Copy-Item -Destination (Join-Path $taskPayload 'app')
$taskManifest=@(Get-ChildItem -LiteralPath $taskPayload -File -Recurse|ForEach-Object{[pscustomobject]@{path=[IO.Path]::GetRelativePath($taskPayload,$_.FullName).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$taskManifest|ConvertTo-Json -Depth 4|Set-Content (Join-Path $taskPayload 'payload-manifest.json') -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($taskPayload,(Join-Path $taskBundle 'SheepCode.payload.zip'),[IO.Compression.CompressionLevel]::Optimal,$false)
Copy-Item (Join-Path $taskWork 'setup\bin\Release\net8.0-windows\*') $taskBundle -Recurse -Exclude checks,setup-preview.png,install-test-error.txt
Copy-Item (Join-Path $taskApp 'sheep.ico') $taskBundle
$taskDotnet=Join-Path $taskBundle 'runtime\dotnet';New-Item -ItemType Directory -Path $taskDotnet -Force|Out-Null
Expand-Archive (Join-Path $taskDownloads 'dotnet-runtime-latest.zip') $taskDotnet
Expand-Archive (Join-Path $taskDownloads 'windowsdesktop-runtime-latest.zip') $taskDotnet -Force
$taskBundleZip=Join-Path $taskBuild 'SetupBundle.zip';[IO.Compression.ZipFile]::CreateFromDirectory($taskBundle,$taskBundleZip,[IO.Compression.CompressionLevel]::Optimal,$false)
$taskResource = '1 ICON "../source/sheep.ico"'+"`r`n"+'1 24 "../source/app.manifest"'+"`r`n"+'101 RCDATA "build/SetupBundle.zip"'+"`r`n"
if($taskApp -like '*\app'){$taskResource=$taskResource.Replace('../source/','../app/')}
Set-Content (Join-Path $taskPackaging 'bootstrap.rc') $taskResource -Encoding ascii
& $env:ComSpec /d /c (Join-Path $taskPackaging 'build-native.cmd')
if($LASTEXITCODE -ne 0){throw 'Falló el empaquetador nativo.'}
$taskDist=Join-Path $taskWork 'dist';New-Item -ItemType Directory -Path $taskDist -Force|Out-Null
Copy-Item (Join-Path $taskPackaging 'SheepCode-Setup.exe') $taskDist -Force
Copy-Item (Join-Path $taskPackaging 'GUIDE.md') (Join-Path $taskDist 'LEEME.md') -Force
$taskSources=Join-Path $taskDist 'SheepCode-source.zip';if(Test-Path -LiteralPath $taskSources){Remove-Item -LiteralPath $taskSources}
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $taskPayload 'source'),$taskSources,[IO.Compression.CompressionLevel]::Optimal,$false)
$taskSums=@();foreach($taskExport in @('SheepCode-Setup.exe','SheepCode-source.zip','LEEME.md')){$taskPath=Join-Path $taskDist $taskExport;$taskSums+= (Get-FileHash -LiteralPath $taskPath).Hash+'  '+$taskExport};Set-Content (Join-Path $taskDist 'SHA256SUMS.txt') $taskSums -Encoding ascii
[pscustomobject]@{setup=Join-Path $taskDist 'SheepCode-Setup.exe';source=$taskSources;payloadFiles=$taskManifest.Count;setupMiB=[math]::Round((Get-Item (Join-Path $taskDist 'SheepCode-Setup.exe')).Length/1MB,1);runtime=$taskMeta.dotnet.url}|ConvertTo-Json
