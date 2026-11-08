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
elseif ($found.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) {
    $p.Toggle()
}
elseif ($found.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) {
    $p.Select()
}
else {
    # 不要退化成模拟鼠标点击：那会把光标挪到用户正在用的别的程序上去。
    Write-Error "$Type '$Name' 不支持 Invoke/Toggle/Select，拒绝用鼠标模拟"
    exit 3
}
Start-Sleep -Milliseconds $WaitMs
Write-Output "invoked $Type '$Name'"
