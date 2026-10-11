@echo off
rem Release in one go: asks whether to bump the version, then commit - build - push - GitHub Release - prune.
rem The release name is v{VersionPrefix}.{today yyyyMMdd}. Every outward step asks before it runs.
rem Usage: double-click, or run Release.bat from a console.
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1"
set RC=%errorlevel%

echo.
pause
exit /b %RC%
