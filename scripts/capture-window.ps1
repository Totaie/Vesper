# Captures one window to PNG for checking Vesper's UI during development: brings the window to
# the front, then copies its on-screen rectangle (WinUI's composition layer isn't visible to
# PrintWindow). Nothing outside the window's bounds is captured.
param(
    [Parameter(Mandatory)] [string] $TitleLike,
    [Parameter(Mandatory)] [string] $OutFile,
    [int] $ProcessId = 0,
    [int] $SettleMs = 600
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Cap {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$found = [IntPtr]::Zero
$cb = [Cap+EnumProc]{
    param($h, $l)
    if (-not [Cap]::IsWindowVisible($h)) { return $true }
    $sb = New-Object System.Text.StringBuilder 512
    [void][Cap]::GetWindowText($h, $sb, 512)
    $pidOut = 0
    [void][Cap]::GetWindowThreadProcessId($h, [ref]$pidOut)
    if ($sb.ToString() -like $TitleLike -and ($ProcessId -eq 0 -or $pidOut -eq $ProcessId)) { $script:found = $h; return $false }
    return $true
}
[void][Cap]::EnumWindows($cb, [IntPtr]::Zero)
if ($found -eq [IntPtr]::Zero) { Write-Output "NOT FOUND: $TitleLike"; exit 1 }
[void][Cap]::ShowWindow($found, 9)   # SW_RESTORE
[Cap]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Cap]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)  # lets SetForegroundWindow succeed
[void][Cap]::SetForegroundWindow($found)
Start-Sleep -Milliseconds $SettleMs
$v = New-Object Cap+RECT
[void][Cap]::DwmGetWindowAttribute($found, 9, [ref]$v, 16)
$w = $v.R - $v.L; $h = $v.B - $v.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($v.L, $v.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "captured $($w)x$($h) at $($v.L),$($v.T) -> $OutFile"
