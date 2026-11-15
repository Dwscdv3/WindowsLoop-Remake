@echo off
rem Standalone tool: edit tile defaults (solid / platform / water / climb / spring).
rem Saving writes src\WindowsLoop\assets\tiles.meta.json; run build.cmd afterwards
rem so the game and level editor pick up the new copies.
rem   run-tile-editor.cmd --selftest   headless data check
rem   run-tile-editor.cmd --uicheck    headless window/layout check
set "EXE=%~dp0src\WindowsLoop.TileEditor\bin\Release\net10.0-windows\WindowsLoop.TileEditor.exe"
if not exist "%EXE%" (
    echo Not built yet - run build.cmd first
    pause
    exit /b 1
)
if "%~1"=="--selftest" goto selftest
if "%~1"=="--uicheck" goto uicheck
start "" "%EXE%" %*
exit /b 0

:selftest
"%EXE%" --selftest
set "RC=%errorlevel%"
echo exit code: %RC%
echo --- selftest report ---
type "%TEMP%\windowsloop-tileeditor-selftest.txt"
exit /b %RC%

:uicheck
"%EXE%" --uicheck
set "RC=%errorlevel%"
echo exit code: %RC%
echo --- uicheck report ---
type "%TEMP%\windowsloop-tileeditor-uicheck.txt"
exit /b %RC%
