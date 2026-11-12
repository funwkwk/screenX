# Builds dist\screenX-Setup-<version>.exe: a single self-contained file (no .NET needed on the target PC)
# that installs screenX for the current user. Run:  powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$proj = Get-Content screenX.csproj
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$out = Join-Path $PSScriptRoot 'dist\publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish screenX.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$setup = Join-Path $PSScriptRoot "dist\screenX-Setup-$version.exe"
Copy-Item (Join-Path $out 'screenX.exe') $setup -Force
Remove-Item $out -Recurse -Force
"{0}  ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)
