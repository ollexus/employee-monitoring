@echo off
rem Развёртывание агента на рабочее место и включение автозапуска.
rem Использование: scripts\install-client.cmd <путь-к-опубликованному-клиенту> [сервер] [порт] [токен]
setlocal
if "%~1"=="" (
  echo Использование: install-client.cmd ^<путь-к-папке-клиента^> [сервер] [порт] [токен]
  exit /b 1
)

set "SOURCE=%~1"
set "SERVER=%~2"
set "PORT=%~3"
set "TOKEN=%~4"
set "TARGET=%LOCALAPPDATA%\EmployeeMonitoring"

if not exist "%SOURCE%\EmployeeMonitoring.Client.exe" (
  echo Не найден %SOURCE%\EmployeeMonitoring.Client.exe
  exit /b 1
)

echo Копирование агента в %TARGET% ...
if not exist "%TARGET%" mkdir "%TARGET%"
copy /Y "%SOURCE%\EmployeeMonitoring.Client.exe" "%TARGET%\" >nul || goto :error
copy /Y "%SOURCE%\*.dll" "%TARGET%\" >nul
if exist "%SOURCE\appsettings.json" copy /Y "%SOURCE%\appsettings.json" "%TARGET%\" >nul
if exist "%SOURCE\runtimeconfig.json" copy /Y "%SOURCE\runtimeconfig.json" "%TARGET%\" >nul

if not "%SERVER%"=="" (
  echo Обновление адреса сервера в конфигурации: %SERVER%:%PORT%
  powershell -NoProfile -Command ^
    "$p='%TARGET%\appsettings.json'; if(Test-Path $p){$j=Get-Content $p -Raw|ConvertFrom-Json; $j.ServerHost='%SERVER%'; if('%PORT%'){$j.ServerPort=%PORT%}; if('%TOKEN%'){$j.Token='%TOKEN%'}; $j|ConvertTo-Json|Set-Content $p -Encoding UTF8}"
)

echo Включение автозапуска (HKCU Run) ...
"%TARGET%\EmployeeMonitoring.Client.exe" --install
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v EmployeeMonitoringAgent
echo.
echo Готово. Агент запустится при следующем входе в систему.
exit /b 0

:error
echo.
echo Не удалось скопировать файлы агента.
exit /b 1
