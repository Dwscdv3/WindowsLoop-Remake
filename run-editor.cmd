@echo off
rem 可以把 json 拖到这个 cmd 上，或 run-editor.cmd "路径\level.json" 直接打开
start "" "%~dp0src\WindowsLoop.LevelEditor\bin\Release\net10.0-windows\WindowsLoop.LevelEditor.exe" %*
