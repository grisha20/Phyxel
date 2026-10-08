@echo off
if "%~1"=="" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Profile-Scene.ps1"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Profile-Scene.ps1" -ScenePath "%~1"
)
exit /b %errorlevel%
