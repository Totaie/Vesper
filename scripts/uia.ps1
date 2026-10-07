# UI Automation helpers for driving Vesper in development tests. Dot-source this file.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function Get-VWindow([string] $NameLike, [int] $TimeoutMs = 10000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    do {
        $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
        foreach ($w in $UIA::RootElement.FindAll($Scope::Children, $cond)) {
            if ($w.Current.Name -like $NameLike) { return $w }
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Window '$NameLike' not found"
}

function Find-V($Parent, [string] $Name, [int] $TimeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    do {
        $el = $Parent.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $Name)))
        if ($el) { return $el }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Element '$Name' not found"
}

function Invoke-V($El) { $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Toggle-V($El) { $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Set-VText($El, [string] $Text) {
    $p = $null
    if ($El.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.SetValue($Text); return }
    if ($El.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$p)) { $p.SetValue([double]$Text); return }
    throw "Element '$($El.Current.Name)' takes no text"
}
function Expand-V($El) { $El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
function Type-V($El, [string] $Text) { $El.SetFocus(); Start-Sleep -Milliseconds 150; [System.Windows.Forms.SendKeys]::SendWait($Text) }
function Snap([string] $TitleLike, [string] $Name) {
    & "$PSScriptRoot\capture-window.ps1" -TitleLike $TitleLike -OutFile "E:\Folders\Coding\CSharp\Vesper\.screenshots\$Name.png"
}
