@echo off
setlocal
title Apna Remote — lab install

set "SRC=%~dp0"
set "DEST=%USERPROFILE%\AppData\Local\ApnaRemote\TwoPcLab"

echo.
echo Apna Remote LAN lab
echo Yeh full Windows Setup nahi hai — portable app copy + shortcut.
echo.

if not exist "%SRC%ApnaRemote.exe" (
  echo ERROR: ApnaRemote.exe is folder me nahi mili.
  echo Pehle zip extract karo, phir is Install-Lab.bat ko usi folder se chalao.
  pause
  exit /b 1
)

mkdir "%DEST%" 2>nul
copy /Y "%SRC%ApnaRemote.exe" "%DEST%\ApnaRemote.exe" >nul
if exist "%SRC%TWO-PC-LAB.md" copy /Y "%SRC%TWO-PC-LAB.md" "%DEST%\TWO-PC-LAB.md" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Unblock-File -LiteralPath '%DEST%\ApnaRemote.exe' -ErrorAction SilentlyContinue; ^
   $ws = New-Object -ComObject WScript.Shell; ^
   $desk = [Environment]::GetFolderPath('Desktop'); ^
   $sc = $ws.CreateShortcut((Join-Path $desk 'Apna Remote Lab.lnk')); ^
   $sc.TargetPath = '%DEST%\ApnaRemote.exe'; ^
   $sc.WorkingDirectory = '%DEST%'; ^
   $sc.Description = 'Apna Remote same-WiFi LAN lab'; ^
   $sc.Save(); ^
   Write-Host 'Desktop shortcut ready.'"

echo.
echo Installed to:
echo   %DEST%
echo Desktop pe shortcut: Apna Remote Lab
echo.
echo Ab shortcut double-click karo.
echo Agar Windows SmartScreen aaye: More info -^> Run anyway
echo.
pause
