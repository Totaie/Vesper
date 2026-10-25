# Publishes a Vesper release to the public releases repository, Totaie/Vesper-releases: what
# installed copies update from. Run scripts\package.ps1 first, and put the Arch Linux package
# (built by the Linux workflow) in .\dist too. This signs the update description with the release
# key, which never leaves this PC, checks it against the key built into Vesper, and uploads.
param(
    # A line or two for the update notice and the release page.
    [Parameter(Mandatory)] [string] $Summary,
    # Extra Markdown for the release page (what's new in detail).
    [string] $NotesFile,
    [switch] $Draft
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'Totaie/Vesper-releases'
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
$dist = Join-Path $root 'dist'

$assets = @(
    "Vesper-$version-Setup-x64.exe",
    "Vesper-$version-Setup-arm64.exe",
    "Vesper-$version-win-x64.zip",
    "Vesper-$version-win-arm64.zip",
    "vesper-$version-1-x86_64.pkg.tar.zst"
) | ForEach-Object { Join-Path $dist $_ }
$missing = $assets | Where-Object { -not (Test-Path $_) }
if ($missing) { throw "Missing from dist: $($missing -join ', ')" }

# Sign what's being shipped.
$dotnet = 'dotnet'
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
}
$tool = Join-Path $root 'tools\Vesper.ReleaseTool'
& $dotnet run --project $tool -c Release -- manifest $dist $version $Summary
if ($LASTEXITCODE -ne 0) { throw 'Describing the release failed' }
$manifest = Join-Path $dist 'vesper-update.json'
& $dotnet run --project $tool -c Release --no-build -- sign $manifest
if ($LASTEXITCODE -ne 0) { throw 'Signing the release failed' }

$sums = foreach ($file in $assets) {
    "$((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $file -Leaf)"
}
$sumsFile = Join-Path $dist 'SHA256SUMS.txt'
Set-Content -Path $sumsFile -Value $sums -Encoding ascii

$details = if ($NotesFile) { (Get-Content $NotesFile -Raw).Trim() + "`n`n" } else { '' }
$notes = @"
$Summary

$details## Download

| Your computer | Download |
|---|---|
| Windows, Intel or AMD (most PCs) | **Vesper-$version-Setup-x64.exe** (installs for you, no admin rights) or Vesper-$version-win-x64.zip (portable) |
| Windows on Snapdragon / ARM | **Vesper-$version-Setup-arm64.exe** or Vesper-$version-win-arm64.zip |
| Arch Linux (x86_64) | **vesper-$version-1-x86_64.pkg.tar.zst**: ``sudo pacman -U vesper-$version-1-x86_64.pkg.tar.zst`` |

Windows 10 version 2004 or later, or Windows 11. The Windows downloads aren't code-signed yet, so the first run may show "Windows protected your PC": choose **More info**, then **Run anyway**.

On Arch, install **libsecret** and a keyring (gnome-keyring, or KWallet on KDE) to have Vesper remember Mac passwords, and **polkit** to install updates from inside the app.

Vesper updates itself after this: it checks this page twice a day and installs a new version when you restart or quit it, only if it carries Vesper's release signature (vesper-update.json and its .sig).

## Checksums (SHA-256)

``````
$($sums -join "`n")
``````
"@
$notesPath = Join-Path $dist 'release-notes.md'
[IO.File]::WriteAllText($notesPath, $notes, (New-Object Text.UTF8Encoding($false)))

$upload = @($assets) + @($manifest, "$manifest.sig", $sumsFile)
$ghArgs = @('release', 'create', "v$version", '--repo', $repo, '--title', "Vesper $version", '--notes-file', $notesPath)
if ($Draft) { $ghArgs += '--draft' } else { $ghArgs += '--latest' }
& gh @ghArgs @upload
if ($LASTEXITCODE -ne 0) { throw 'Creating the GitHub release failed' }
Write-Host "Released Vesper $version to https://github.com/$repo/releases/tag/v$version"
