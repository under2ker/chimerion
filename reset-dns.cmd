@echo off
chcp 65001 >nul
:: Аварийный сброс DNS на автоматический (DHCP) для всех адаптеров. Запрашивает права администратора.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-NetAdapter | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses -ErrorAction SilentlyContinue; Write-Host ('Сброшен: ' + $_.Name) }; Clear-DnsClientCache"
ipconfig /flushdns >nul
del "%APPDATA%\DNEX\active.json" 2>nul
del "%APPDATA%\DNEX\dns-backup.json" 2>nul
echo.
echo Готово: DNS всех адаптеров = автоматически.
pause
