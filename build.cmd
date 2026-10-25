@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Не убиваем программу: при активном подключении жёсткое завершение оставит DNS переключённым.
tasklist /fi "imagename eq CHIMERION.exe" 2>nul | find /i "CHIMERION.exe" >nul
if %ERRORLEVEL%==0 (
  echo CHIMERION сейчас запущен. Закройте его окно штатно.
  echo Если подключение активно, выберите "Нет - выйти, оставив DNS как есть".
  pause
  exit /b 1
)
echo Сборка CHIMERION...
dotnet build -c Release > build.log 2>&1
echo EXIT %ERRORLEVEL% >> build.log
if %ERRORLEVEL%==0 (start "" "bin\Release\net8.0-windows\CHIMERION.exe") else (type build.log ^| findstr /i "error" & pause)
