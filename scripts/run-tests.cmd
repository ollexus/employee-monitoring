@echo off
rem Запуск автотестов (без внешних зависимостей).
setlocal
cd /d "%~dp0.."

dotnet run --project src\EmployeeMonitoring.Tests -c Release -- %*
set "CODE=%ERRORLEVEL%"

if not "%CODE%"=="0" (
  echo.
  echo Тесты завершились с ошибкой: код %CODE%
)

exit /b %CODE%
