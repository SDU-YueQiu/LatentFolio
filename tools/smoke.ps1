param()
# 交互冒烟：搜索 / 文件夹 / 我的栏 / 评分 / 删除 / 还原 / 标签
$ErrorActionPreference = 'Stop'
$repo = 'D:\Projects\LatentFolio'
Set-Location $repo
$log = @()

function Step([string]$text) {
    Write-Output "== $text"
    $script:log += $text
}

function Shot([string]$name) {
    & "$repo\tools\screenshot.ps1" -Out "$env:TEMP\smoke-$name.png" | Out-Null
}

# 应用先起来
Get-Process -Name LatentFolio -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); Start-Sleep -Milliseconds 400 }
Start-Process "$repo\src\LatentFolio.App\bin\Debug\net10.0-windows\LatentFolio.exe"
Start-Sleep -Seconds 14

Step '初始'
Shot 'init'

Step '搜索 "jinx"'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pid0 = (Get-Process -Name LatentFolio | Select-Object -First 1).Id
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
$edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
Write-Output "  找到 $($edits.Count) 个输入框"
if ($edits.Count -gt 0) {
    $vp = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue('jinx')
    Start-Sleep -Milliseconds 1500
}
Shot 'search'

Step '清空搜索'
if ($edits.Count -gt 0) {
    $vp = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue('')
    Start-Sleep -Milliseconds 1200
}

Step '点已删除'
& "$repo\tools\uia.ps1" -Name '已删除' -WaitMs 1200 | Out-Null
Shot 'deleted'

Step '回总览'
& "$repo\tools\uia.ps1" -Name '总览' -WaitMs 1200 | Out-Null
Shot 'overview'

Write-Output '--- done'

