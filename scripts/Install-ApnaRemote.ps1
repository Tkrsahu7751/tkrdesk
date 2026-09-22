$dest = Join-Path $env:LOCALAPPDATA 'ApnaRemote\TwoPcLab'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item '.\dist\ApnaRemote-TwoPcLab\*' -Destination $dest -Recurse -Force

$wsh = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'Apna Remote.lnk'
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $dest 'ApnaRemote.exe'
$shortcut.WorkingDirectory = $dest
$shortcut.Description = 'Apna Remote - Smart Office Hardware & Remote Desktop'
$shortcut.Save()

Write-Host "SUCCESS: Installed to $dest"
Write-Host "SUCCESS: Created Desktop shortcut at $shortcutPath"
Write-Host "Executable file size: $((Get-Item (Join-Path $dest 'ApnaRemote.exe')).Length / 1MB) MB"
