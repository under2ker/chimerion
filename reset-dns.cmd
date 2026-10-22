@echo off
chcp 65001 >nul
:: Аварийный сброс DNS, который выставила DNEX. Запрашивает права администратора.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0reset-dns.ps1"
echo.
pause
