param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Type = 'Button',
    [int]$Index = 0,
    [int]$WaitMs = 1200
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    (Get-Process -Name LatentFolio | Select-Object -First 1).Id)

$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$found = $null
foreach ($w in $wins) {
    $tc = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::$Type)))
    $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)
    if ($all.Count -gt $Index) { $found = $all[$Index]; break }
}

if (-not $found) { Write-Error "找不到 $Type '$Name'"; exit 2 }

$p = $null
if ($found.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) {
    $p.Invoke()
}
elseif ($found.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) {
    $p.Select()
}
else {
    $r = $found.Current.BoundingRectangle
    & "$PSScriptRoot\click.ps1" -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)) -Button left -WaitMs 200
}
Start-Sleep -Milliseconds $WaitMs
Write-Output "invoked $Type '$Name'"
