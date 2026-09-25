@echo off
rem Публикация сервера и агента в папку artifacts (без сторонних пакетов).
setlocal
cd /d "%~dp0.."

echo [1/2] Публикация сервера...
dotnet publish src\EmployeeMonitoring.Server -c Release -r win-x64 --self-contained false -o artifacts\server || goto :error

echo [2/2] Публикация агента...
dotnet publish src\EmployeeMonitoring.Client -c Release -r win-x64 --self-contained false -o artifacts\client || goto :error

echo.
echo Готово:
echo   сервер: artifacts\server\EmployeeMonitoring.Server.exe
echo   агент:  artifacts\client\EmployeeMonitoring.Client.exe
echo.
echo Перед распространением измените appsettings.json в обеих папках:
echo   сервер: Agent:Token, Agent:CaptureIntervalSeconds, Dashboard:Password
echo   агент:  ServerHost, Token
exit /b 0

:error
echo.
echo Сборка завершилась с ошибкой.
exit /b 1
