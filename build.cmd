@echo off
cd /d "%~dp0"
echo Сборка DNEX...
taskkill /im DNEX.exe /f >nul 2>&1
dotnet build -c Release > build.log 2>&1
echo EXIT %ERRORLEVEL% >> build.log
if %ERRORLEVEL%==0 (start "" "bin\Release\net8.0-windows\DNEX.exe") else (type build.log ^| findstr /i "error" & pause)
