@echo off
setlocal
cd /d "%~dp0"

echo.
echo === Build WindowsLoop ===
echo.

echo [1/3] pack tiles
where python >nul 2>nul
if errorlevel 1 (
    echo   [skip] python not found - keep existing tiles.png
) else (
    python tools\build_tileset.py
    if errorlevel 1 (
        echo.
        echo   [FAILED] tile packing failed.
        echo   If it says the file is locked, close the level editor / game and retry.
        goto fail
    )
)

echo.
echo [2/3] compile game / level editor / tile editor
dotnet build src\WindowsLoop\WindowsLoop.csproj -c Release -m:1 --nologo
if errorlevel 1 goto fail

dotnet build src\WindowsLoop.LevelEditor\WindowsLoop.LevelEditor.csproj -c Release -m:1 --nologo
if errorlevel 1 goto fail

dotnet build src\WindowsLoop.TileEditor\WindowsLoop.TileEditor.csproj -c Release -m:1 --nologo
if errorlevel 1 goto fail

echo.
echo [3/3] verify shared tile defaults
rem Each csproj copies tiles.meta.json into its own output dir via xcopy.
rem Game and level editor read those copies at runtime, so after editing defaults
rem in the tile editor you must run build.cmd again.
set "META=src\WindowsLoop\assets\tiles.meta.json"
set "GAME_META=src\WindowsLoop\bin\Release\net10.0-windows\assets\tiles.meta.json"
set "EDIT_META=src\WindowsLoop.LevelEditor\bin\Release\net10.0-windows\assets\tiles.meta.json"
if not exist "%GAME_META%" goto nometa
if not exist "%EDIT_META%" goto nometa
fc /B "%META%" "%GAME_META%" >nul
if errorlevel 1 goto metadiff
fc /B "%META%" "%EDIT_META%" >nul
if errorlevel 1 goto metadiff
echo   game + level editor both got identical assets\tiles.meta.json
goto done

:nometa
echo   [WARN] tiles.meta.json missing in an output dir - defaults fall back to the source copy.
goto done

:metadiff
echo   [WARN] output copies differ from the source - rebuild once more or check file locks.

:done
echo.
echo === Done ===
echo Levels copied to: bin\Release\net10.0-windows\assets\levels
echo Run: play.cmd [level]        Edit levels: run-editor.cmd
echo Edit tile defaults: run-tile-editor.cmd   (then run build.cmd again)
echo.
if not "%~1"=="--no-pause" pause
exit /b 0

:fail
echo.
echo === FAILED - see errors above ===
pause
exit /b 1
