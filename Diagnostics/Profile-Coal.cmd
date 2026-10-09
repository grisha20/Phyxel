@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Profile-Coal.ps1"
exit /b %errorlevel%
