@echo off
rem PotionCoop: allow the game to accept co-op connections from the home network only.
rem Run with right click > "Run as administrator".

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Bu dosyayi SAG TIK ^> "Yonetici olarak calistir" ile acmalisin.
  pause
  exit /b 1
)

set "EXE=%~dp0A Place for Every Potion.exe"
if not exist "%EXE%" (
  echo Bu dosya oyun klasorunde olmali: "A Place for Every Potion.exe" bulunamadi.
  pause
  exit /b 1
)

netsh advfirewall firewall delete rule name="PotionCoop TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="PotionCoop UDP" >nul 2>&1
netsh advfirewall firewall add rule name="PotionCoop TCP" dir=in action=allow program="%EXE%" protocol=TCP localport=47777 remoteip=localsubnet profile=any >nul
netsh advfirewall firewall add rule name="PotionCoop UDP" dir=in action=allow program="%EXE%" protocol=UDP localport=47778 remoteip=localsubnet profile=any >nul

echo.
echo Tamam: PotionCoop icin guvenlik duvari izinleri eklendi (yalnizca ev agindan gelen baglantilar).
pause
