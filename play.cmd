@echo off
setlocal
cd /d "%~dp0"

set "EXE=src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe"
set "LEVELS=src\WindowsLoop\assets\levels"

if not exist "%EXE%" (
    echo Not built yet - run build.cmd first
    pause
    exit /b 1
)

if "%~1"=="" (
    play main
)

rem single instance: kill the old process first, otherwise --level is ignored
taskkill /IM "WindowsLoop.exe" >nul 2>nul
timeout /t 1 /nobreak >nul
taskkill /F /IM "WindowsLoop.exe" >nul 2>nul

start "" "%EXE%" --level "%~1"
