@echo off
rem Папка данных берется ДО запроса прав и передается повышенной копии: если пароль вводит другой пользователь
rem (администратор), у повышенной копии своя папка APPDATA и чужие файлы. Путь идет через переменную, а не в кавычках
rem внутри команды: апостроф в пути больше не ломает запуск (аудит DNS-10)
chcp 65001 >nul
:: Аварийный сброс DNS, который выставила CHIMERION. Запрашивает права администратора.
net session >nul 2>&1 || (set "CHIMERION_SELF=%~f0" & powershell -NoProfile -Command "Start-Process -FilePath $env:CHIMERION_SELF -ArgumentList ([char]34 + (Join-Path $env:APPDATA 'DNEX') + [char]34) -Verb RunAs" & exit /b)
if not "%~1"=="" set "CHIMERION_DATA=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0reset-dns.ps1"
echo.
pause
