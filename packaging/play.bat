@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" %*
exit /b %errorlevel%
