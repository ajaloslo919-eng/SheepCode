$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot 'app\SheepCode.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'La compilación de SheepCode falló.' }
Write-Output ('Aplicación compilada en ' + (Join-Path $PSScriptRoot 'app\bin\Release\net8.0-windows'))
