$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot 'ScreenTimeoutToggle.ico'
$bitmap = New-Object System.Drawing.Bitmap 64, 64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(31, 41, 59))
$screen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(101, 220, 176))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 232, 245)), 4
$graphics.FillRectangle($background, 0, 0, 64, 64)
$graphics.DrawRectangle($pen, 9, 11, 46, 32)
$graphics.DrawLine($pen, 32, 44, 32, 52)
$graphics.DrawLine($pen, 21, 53, 43, 53)
$graphics.FillEllipse($screen, 21, 19, 23, 17)
$graphics.DrawLine($pen, 32, 17, 32, 26)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.File]::Create($iconPath)
try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $background.Dispose(); $screen.Dispose(); $pen.Dispose() }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 "/win32icon:$iconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\关屏时间切换.exe" "$PSScriptRoot\ScreenTimeoutToggle.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path $PSScriptRoot '关屏时间切换.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $PSScriptRoot '关屏时间切换.exe'
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = '一键切换插电关屏时间：1 分钟 / 从不。'
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Hotkey = ''
$shortcut.Save()
Write-Output "已生成：$($shortcut.TargetPath)"
