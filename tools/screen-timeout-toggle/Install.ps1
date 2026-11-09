$ErrorActionPreference = 'Stop'
$executablePath = Join-Path $PSScriptRoot '关屏时间切换.exe'
if (-not (Test-Path -LiteralPath $executablePath)) {
    & (Join-Path $PSScriptRoot 'Build.ps1')
    if (-not (Test-Path -LiteralPath $executablePath)) { throw '未找到可运行的关屏切换程序。' }
}
$desktopPath = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopPath '关屏时间切换.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = '1 分钟关屏并调至最低亮度 / 从不关屏并恢复原亮度。'
$shortcut.IconLocation = (Join-Path $PSScriptRoot 'ScreenTimeoutToggle.ico') + ',0'
$shortcut.Hotkey = ''
$shortcut.Save()
Write-Output "桌面快捷方式已就绪：$shortcutPath"
