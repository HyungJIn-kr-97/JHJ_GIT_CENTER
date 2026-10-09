@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Platform x64
endlocal
