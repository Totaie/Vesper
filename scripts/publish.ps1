# Builds a self-contained Release copy of Vesper (no .NET or Windows App SDK install needed).
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Prefer a per-user .NET SDK if one is installed (global.json pins the 10.0.1xx band).
$dotnet = 'dotnet'
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
}

$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$out = Join-Path $root "publish\$rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish "$root\src\Vesper.App\Vesper.App.csproj" -c Release -r $rid -p:Platform=$Platform --self-contained -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$hash = (Get-FileHash (Join-Path $out 'Vesper.exe') -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Published to $out"
Write-Host "Vesper.exe SHA-256: $hash"
