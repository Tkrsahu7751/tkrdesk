@echo off
:: Run AS ADMINISTRATOR on the HOST PC (the one that clicks Enable receiving).
:: Allows inbound TCP 5720 from private network so the 2nd PC can connect.

net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo RIGHT-CLICK this file -^> Run as administrator
  echo Host PC pe chalao — viewer PC pe nahi.
  echo.
  pause
  exit /b 1
)

netsh advfirewall firewall delete rule name="Apna Remote LAN Lab TCP" >nul 2>&1
netsh advfirewall firewall add rule name="Apna Remote LAN Lab TCP" dir=in action=allow protocol=TCP localport=5720,8989,8990,3240 profile=private,domain

netsh advfirewall firewall delete rule name="Apna Remote LAN Hub UDP" >nul 2>&1
netsh advfirewall firewall add rule name="Apna Remote LAN Hub UDP" dir=in action=allow protocol=UDP localport=8991 profile=private,domain

echo.
echo OK: TCP (5720, 8989, 8990, 3240) and UDP (8991) allowed on private/domain firewall.
echo Ab HOST pe Apna Remote kholo (Remote Desktop / Smart Queue / Pen Drive / USB Tunnel).
echo.
pause
