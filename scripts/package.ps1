# Builds the portable Vesper zips: self-contained Release builds that run from any folder, with no
# install and no .NET or Windows App SDK needed. One zip per platform, in .\dist, with SHA-256 sums.
# Each unzips to a folder holding just Vesper.exe (a small launcher), Start here.txt, and an app
# folder with the program itself. The launcher needs Visual Studio's C++ build tools.
param(
    [ValidateSet('x64', 'ARM64')] [string[]] $Platforms = @('x64', 'ARM64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$readme = @"
Vesper $version - Apple Remote Desktop for Windows
==================================================

Portable: nothing to install. Extract this whole folder anywhere and run Vesper.exe.
The "app" folder beside it is the program itself, so keep the two together.

Requirements: Windows 10 version 2004 or later, or Windows 11. Pick the zip for your PC:
win-x64 for Intel and AMD processors, win-arm64 for Snapdragon / ARM PCs.

The first time you run it, Windows may say "Windows protected your PC", because this build
isn't code-signed. Click "More info", then "Run anyway". If the zip came by email or download,
you can also right-click it > Properties > Unblock before extracting.

Setting up the Mac
  1. On the Mac: System Settings > General > Sharing, turn on Screen Sharing (or Remote
     Management).
  2. Click the (i) next to it and make sure your Mac account is allowed. With Remote
     Management, add your account and allow Observe and Control.
  3. In Vesper: Add a Mac, with its name (for example studio.local) or IP address and your
     Mac account. The first time, Vesper shows the Mac's identity and asks you to trust it.

Your Macs and settings are kept in %LOCALAPPDATA%\Vesper on this PC, and saved passwords are
encrypted to your Windows account. To remove everything, delete Vesper's folder and that one.

Vesper talks only to the Macs you add: no accounts, no telemetry, no update checks.
Licences for code Vesper ports are in app\THIRD-PARTY-NOTICES.md.
"@

# One launcher serves every platform: it's 32-bit x86, which ARM PCs run under emulation.
$launcher = Join-Path $root 'obj\launcher'
& (Join-Path $root 'tools\Launcher\build.cmd') $launcher ($version -replace '-.*$')
if ($LASTEXITCODE -ne 0) { throw "launcher build failed ($LASTEXITCODE)" }

$sums = @()
foreach ($platform in $Platforms) {
    $rid = if ($platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    & (Join-Path $PSScriptRoot 'publish.ps1') -Platform $platform
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "publish failed for $platform" }

    $name = "Vesper-$version-$rid"
    $stage = Join-Path $dist $name
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item (Join-Path $root "publish\$rid") (Join-Path $stage 'app') -Recurse
    # Debug symbols aren't needed to run it.
    Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force
    Copy-Item (Join-Path $launcher 'Vesper.exe') $stage
    Set-Content -Path (Join-Path $stage 'Start here.txt') -Value $readme -Encoding utf8

    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Each entry is named explicitly: Windows PowerShell's Compress-Archive (and ZipFile on its
    # .NET Framework) write backslashes into entry names, which breaks the ZIP format for anything
    # but File Explorer.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $entry = "$name/" + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    Remove-Item $stage -Recurse -Force

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash  $name.zip"
    Write-Host ("{0}: {1:N1} MB, SHA-256 {2}" -f "$name.zip", ((Get-Item $zip).Length / 1MB), $hash)
}
Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Value $sums -Encoding ascii
Write-Host "Packaged to $dist"
