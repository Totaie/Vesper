# Installs a published Vesper for the current user: %LOCALAPPDATA%\Programs\Vesper plus a
# Start menu shortcut. No admin rights, no registry changes. -Uninstall removes both again;
# your Macs, settings and password vault in %LOCALAPPDATA%\Vesper are left alone.
param(
    [string] $From = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\win-x64'),
    [switch] $Uninstall
)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Vesper'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Vesper.lnk'

$running = Get-Process Vesper -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" }
if ($running) {
    Write-Host "Closing the running Vesper first..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if ($Uninstall) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    Write-Host "Vesper removed. Your Macs and saved passwords are still in $env:LOCALAPPDATA\Vesper."
    return
}

if (-not (Test-Path (Join-Path $From 'Vesper.exe'))) {
    throw "No published build in $From. Run scripts\publish.ps1 first."
}

New-Item -ItemType Directory -Force $dest | Out-Null
robocopy $From $dest /MIR /NJH /NJS /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying failed (robocopy $LASTEXITCODE)" }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $dest 'Vesper.exe'
$link.WorkingDirectory = $dest
$link.IconLocation = (Join-Path $dest 'Vesper.exe') + ',0'
$link.Description = 'Vesper: a fast, secure Apple Remote Desktop client'
$link.Save()

Write-Host "Installed to $dest"
Write-Host "Start menu: Vesper"
