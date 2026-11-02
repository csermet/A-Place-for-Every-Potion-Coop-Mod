@echo off
rem PotionCoop: allow the game to accept co-op connections from the local network only.
rem Put this file in the game folder (next to "A Place for Every Potion.exe"),
rem then right click > "Run as administrator".

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Please run this file with right click ^> "Run as administrator".
  pause
  exit /b 1
)

set "EXE=%~dp0A Place for Every Potion.exe"
if not exist "%EXE%" (
  echo This file must be in the game folder: "A Place for Every Potion.exe" was not found next to it.
  pause
  exit /b 1
)

netsh advfirewall firewall delete rule name="PotionCoop TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="PotionCoop UDP" >nul 2>&1
netsh advfirewall firewall add rule name="PotionCoop TCP" dir=in action=allow program="%EXE%" protocol=TCP localport=47777 remoteip=localsubnet profile=any >nul
netsh advfirewall firewall add rule name="PotionCoop UDP" dir=in action=allow program="%EXE%" protocol=UDP localport=47778 remoteip=localsubnet profile=any >nul

echo.
echo Done: firewall rules for PotionCoop added (connections from the local network only).
pause
