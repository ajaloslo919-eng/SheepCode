param([string]$DependencySource='', [string]$CMake='', [string]$Ninja='', [string]$WindowsSdk=$env:SHEEPCODE_WINDOWS_SDK, [string]$StrataSourceRoot='')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource=Join-Path $taskRoot 'engine\strata-cpu'
if($StrataSourceRoot){$taskSource=[IO.Path]::GetFullPath($StrataSourceRoot)}
$taskBuild=Join-Path $taskSource 'build'
if(!$CMake){$CMake=(Get-Command cmake -ErrorAction Stop).Source}
if(!$Ninja){$Ninja=(Get-Command ninja -ErrorAction Stop).Source}
$taskVs='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
if(!(Test-Path -LiteralPath $taskVs)){throw 'Necesitas Visual Studio 2022 con C++ para recompilar el motor.'}
New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
$taskArgs=@('"'+$CMake+'"','-S','"'+$taskSource+'"','-B','"'+$taskBuild+'"','-G','Ninja','-DCMAKE_BUILD_TYPE=Release','"-DCMAKE_MAKE_PROGRAM='+$Ninja+'"')
if($DependencySource){$taskArgs+= '"-DSTRATA_GGML_DIR='+[IO.Path]::GetFullPath($DependencySource)+'"'}
if($StrataSourceRoot){$taskArgs+= '-DSTRATA_LOW_MEMORY_CPU=ON'}
$taskLines=@('@echo off',('call "'+$taskVs+'" >nul'),'if errorlevel 1 exit /b 1')
if($WindowsSdk){
    $taskLines+= 'set "INCLUDE='+$WindowsSdk+'\c\Include\10.0.26100.0\ucrt;'+$WindowsSdk+'\c\Include\10.0.26100.0\shared;'+$WindowsSdk+'\c\Include\10.0.26100.0\um;%INCLUDE%"'
    $taskLines+= 'set "LIB='+$WindowsSdk+'\c\um\x64;'+$WindowsSdk+'\c\ucrt\x64;%LIB%"'
    $taskLines+= 'set "PATH='+$WindowsSdk+'\c\bin\10.0.26100.0\x64;%PATH%"'
}
$taskLines+= ($taskArgs -join ' '),'if errorlevel 1 exit /b 1',('"'+$CMake+'" --build "'+$taskBuild+'" --target strata-cpu --parallel 4'),'exit /b %errorlevel%'
$taskScript=Join-Path $taskBuild 'build-local.cmd'; [IO.File]::WriteAllLines($taskScript,$taskLines,[Text.Encoding]::Default)
$taskLog=Join-Path $taskBuild 'build.log'
& $env:ComSpec /d /c $taskScript *> $taskLog
if($LASTEXITCODE -ne 0){Select-String -LiteralPath $taskLog -Pattern 'error C|error LNK|fatal error|FAILED:' | Select-Object -First 10; throw 'No se pudo compilar Strata CPU.'}
$taskPackage=Join-Path $taskRoot 'packaging\downloads\strata-cpu.zip'
if(Test-Path -LiteralPath $taskPackage){Remove-Item -LiteralPath $taskPackage}
Compress-Archive -LiteralPath (Join-Path $taskBuild 'strata-cpu.exe') -DestinationPath $taskPackage
$taskMetadata=[pscustomobject]@{version='0.1.1';dependency='3cf03257f219afbe7334045ff7c6a06ac68c627d';isa='x64/SSE2';size=(Get-Item -LiteralPath $taskPackage).Length;sha256=(Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash;executableSha256=(Get-FileHash -LiteralPath (Join-Path $taskBuild 'strata-cpu.exe')).Hash}
$taskMetadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'strata-cpu.json') -Encoding utf8
$taskMetadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'shared\strata-cpu.json') -Encoding utf8
$taskMetadata | ConvertTo-Json
