$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '关屏时间切换.exe'
$backupPath = Join-Path $PSScriptRoot 'brightness-backup.xml'
if (Test-Path -LiteralPath $backupPath) { throw '已有待恢复的亮度记录，请先用工具切回「从不」再测试。' }
function Get-Brightness {
    return @(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness | Where-Object Active | ForEach-Object {
        [pscustomobject]@{ InstanceName = $_.InstanceName; Value = [byte]$_.CurrentBrightness; Minimum = [byte](($_.Level | Measure-Object -Minimum).Minimum) }
    })
}
function Assert-Brightness($Expected, [switch]$Minimum) {
    $actual = @(Get-Brightness)
    foreach ($monitor in $Expected) {
        $found = @($actual | Where-Object InstanceName -EQ $monitor.InstanceName)
        $target = if ($Minimum) { $monitor.Minimum } else { $monitor.Value }
        if ($found.Count -ne 1 -or $found[0].Value -ne $target) { throw "亮度不符合预期：$target%" }
    }
}
function Set-Brightness($Monitor, [byte]$Value) {
    $method = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Where-Object InstanceName -EQ $Monitor.InstanceName
    if (-not $method) { throw '没有找到原屏幕。' }
    $result = Invoke-CimMethod -InputObject $method -MethodName WmiSetBrightness -Arguments @{Timeout=[uint32]0; Brightness=$Value}
    if ($null -ne $result.ReturnValue -and $result.ReturnValue -ne 0) { throw '设置测试亮度失败。' }
}
function Get-Snapshot {
    $result = & powercfg /query SCHEME_CURRENT
    if ($LASTEXITCODE -ne 0) { throw '读取电源计划失败。' }
    return ($result -join "`n")
}
function Get-Timeout {
    $result = & powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE
    if ($LASTEXITCODE -ne 0) { throw '读取关屏时间失败。' }
    $hex = [regex]::Matches(($result -join "`n"), '0x([0-9a-fA-F]{8})')
    return [pscustomobject]@{
        AC = [Convert]::ToUInt32($hex[$hex.Count - 2].Groups[1].Value, 16)
        DC = [Convert]::ToUInt32($hex[$hex.Count - 1].Groups[1].Value, 16)
    }
}
function Invoke-Tool([string]$Mode, [int]$ExpectedCode = 0) {
    $process = Start-Process -FilePath $exe -ArgumentList @($Mode, '--quiet') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne $ExpectedCode) { throw "执行 $Mode 失败，退出码 $($process.ExitCode)" }
}
$initialPlan = & powercfg /getactivescheme
$scheme = [regex]::Match(($initialPlan -join ''), '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}').Value
$baseline = Get-Snapshot
$initial = Get-Timeout
$brightness = @(Get-Brightness)
if ($brightness.Count -eq 0) { throw '本机没有可供亮度联动测试的屏幕。' }
$baseline | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'initial-power-plan.txt') -Encoding UTF8
$results = New-Object System.Collections.Generic.List[string]
try {
    Invoke-Tool '--status'
    if ((Get-Snapshot) -cne $baseline) { throw '查看状态意外改变了设置。' }
    $results.Add('PASS: --status does not change settings')
    Assert-Brightness $brightness
    Invoke-Tool '--invalid' 2
    if ((Get-Snapshot) -cne $baseline) { throw '无效参数意外改变了设置。' }
    $results.Add('PASS: invalid arguments rejected without changes')
    Assert-Brightness $brightness
    Invoke-Tool '--toggle'
    $first = Get-Timeout
    $target = if ($initial.AC -eq 0) { 60 } else { 0 }
    if ($first.AC -ne $target -or $first.DC -ne $initial.DC) { throw '首次切换状态错误。' }
    if ($target -eq 60) { Assert-Brightness $brightness -Minimum }
    else { Assert-Brightness $brightness }
    $changed = Get-Snapshot
    $a = $baseline -split "`n"
    $b = $changed -split "`n"
    if ($a.Count -ne $b.Count) { throw '计划结构意外变化。' }
    $diff = @(for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -cne $b[$i]) { $i } })
    if ($diff.Count -lt 1) { throw '关屏设置没有变化。' }
    foreach ($lineNumber in $diff) {
        $guidMatches = [regex]::Matches(($b[0..$lineNumber] -join "`n"), '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}')
        $setting = $guidMatches[$guidMatches.Count - 1].Value
        if ($setting -notin @('3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e', 'aded5e82-b909-4619-9949-f5d71dac0bcb')) { throw '存在关屏时间和显示亮度以外的电源设置变化。' }
    }
    $results.Add('PASS: only AC display timeout and driver-managed display brightness values change')
    Invoke-Tool '--toggle'
    if ($initial.AC -in @(0, 60) -and (Get-Snapshot) -cne $baseline) { throw '往返切换后没有恢复原始计划。' }
    $results.Add('PASS: both toggle directions')
    Invoke-Tool '--minute'
    if ((Get-Timeout).AC -ne 60) { throw '--minute 失败。' }
    Assert-Brightness $brightness -Minimum
    if (-not (Test-Path -LiteralPath $backupPath)) { throw '没有保存原亮度。' }
    $backupHash = (Get-FileHash -LiteralPath $backupPath).Hash
    $minuteSnapshot = Get-Snapshot
    Invoke-Tool '--minute'
    if ((Get-Snapshot) -cne $minuteSnapshot) { throw '--minute 不满足幂等性。' }
    if ((Get-FileHash -LiteralPath $backupPath).Hash -ne $backupHash) { throw '重复设置覆盖了原亮度。' }
    foreach ($monitor in $brightness) {
        if ($monitor.Minimum -lt 100) { Set-Brightness $monitor ([byte]($monitor.Minimum + 1)) }
    }
    Invoke-Tool '--minute'
    Assert-Brightness $brightness -Minimum
    if ((Get-FileHash -LiteralPath $backupPath).Hash -ne $backupHash) { throw '手动调亮后重复设置覆盖了原亮度。' }
    Invoke-Tool '--never'
    if ((Get-Timeout).AC -ne 0) { throw '--never 失败。' }
    Assert-Brightness $brightness
    if (Test-Path -LiteralPath $backupPath) { throw '恢复后没有清理亮度记录。' }
    $results.Add('PASS: explicit modes and repeated setting')
    $results.Add('PASS: hardware minimum, original brightness restoration, and backup preservation after repeated/manual changes')
    [System.IO.File]::WriteAllText($backupPath, 'invalid test backup')
    Invoke-Tool '--minute' 3
    Assert-Brightness $brightness
    if ((Get-Timeout).AC -ne 60) { throw '亮度失败影响了关屏切换。' }
    if ([System.IO.File]::ReadAllText($backupPath) -ne 'invalid test backup') { throw '损坏的记录被覆盖。' }
    Remove-Item -LiteralPath $backupPath
    $results.Add('PASS: invalid brightness backup reports partial failure without changing brightness or blocking timeout switching')
}
finally {
    $restoreErrors = New-Object System.Collections.Generic.List[string]
    foreach ($monitor in $brightness) {
        try { Set-Brightness $monitor $monitor.Value } catch { $restoreErrors.Add($_.Exception.Message) }
    }
    $currentPlan = (& powercfg /getactivescheme) -join ''
    if ($currentPlan -notmatch [regex]::Escape($scheme)) { throw '当前计划被外部更改，已停止自动恢复。' }
    if ((Get-Snapshot) -cne $baseline) {
        & powercfg /setacvalueindex $scheme SUB_VIDEO VIDEOIDLE $initial.AC
        if ($LASTEXITCODE -ne 0) { throw '恢复初始关屏时间失败。' }
        & powercfg /setactive $scheme
        if ($LASTEXITCODE -ne 0) { throw '应用恢复后的计划失败。' }
    }
    Invoke-Tool '--status'
    if ($restoreErrors.Count -gt 0) { throw ($restoreErrors -join "`n") }
    Assert-Brightness $brightness
    if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath }
    if ((Get-Snapshot) -cne $baseline) { throw '最终电源计划与测试前不一致。' }
}
$results.Add('PASS: original full power plan and original screen brightness restored')
$results | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'verification.txt') -Encoding UTF8
$results
