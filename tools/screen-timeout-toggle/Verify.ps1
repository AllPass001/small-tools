$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '关屏时间切换.exe'
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
$baseline | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'initial-power-plan.txt') -Encoding UTF8
$results = New-Object System.Collections.Generic.List[string]
try {
    Invoke-Tool '--status'
    if ((Get-Snapshot) -cne $baseline) { throw '查看状态意外改变了设置。' }
    $results.Add('PASS: --status does not change settings')
    Invoke-Tool '--invalid' 2
    if ((Get-Snapshot) -cne $baseline) { throw '无效参数意外改变了设置。' }
    $results.Add('PASS: invalid arguments rejected without changes')
    Invoke-Tool '--toggle'
    $first = Get-Timeout
    $target = if ($initial.AC -eq 0) { 60 } else { 0 }
    if ($first.AC -ne $target -or $first.DC -ne $initial.DC) { throw '首次切换状态错误。' }
    $changed = Get-Snapshot
    $a = $baseline -split "`n"
    $b = $changed -split "`n"
    if ($a.Count -ne $b.Count) { throw '计划结构意外变化。' }
    $diff = @(for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -cne $b[$i]) { $i } })
    if ($diff.Count -ne 1 -or $b[$diff[0]] -notmatch ('0x{0:x8}' -f $target)) { throw '存在预期以外的电源设置变化。' }
    $results.Add('PASS: toggle changes exactly one AC timeout value in the full power plan')
    Invoke-Tool '--toggle'
    if ($initial.AC -in @(0, 60) -and (Get-Snapshot) -cne $baseline) { throw '往返切换后没有恢复原始计划。' }
    $results.Add('PASS: both toggle directions')
    Invoke-Tool '--minute'
    if ((Get-Timeout).AC -ne 60) { throw '--minute 失败。' }
    $minuteSnapshot = Get-Snapshot
    Invoke-Tool '--minute'
    if ((Get-Snapshot) -cne $minuteSnapshot) { throw '--minute 不满足幂等性。' }
    Invoke-Tool '--never'
    if ((Get-Timeout).AC -ne 0) { throw '--never 失败。' }
    $results.Add('PASS: explicit modes and repeated setting')
}
finally {
    $currentPlan = (& powercfg /getactivescheme) -join ''
    if ($currentPlan -notmatch [regex]::Escape($scheme)) { throw '当前计划被外部更改，已停止自动恢复。' }
    if ((Get-Snapshot) -cne $baseline) {
        & powercfg /setacvalueindex $scheme SUB_VIDEO VIDEOIDLE $initial.AC
        if ($LASTEXITCODE -ne 0) { throw '恢复初始关屏时间失败。' }
        & powercfg /setactive $scheme
        if ($LASTEXITCODE -ne 0) { throw '应用恢复后的计划失败。' }
    }
    Invoke-Tool '--status'
    if ((Get-Snapshot) -cne $baseline) { throw '最终电源计划与测试前不一致。' }
}
$results.Add('PASS: original full power plan restored')
$results | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'verification.txt') -Encoding UTF8
$results
