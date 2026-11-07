@echo off
if not exist "%APPDATA%\AllegianceCore" mkdir "%APPDATA%\AllegianceCore"
if not exist "%APPDATA%\AllegianceCore\player.json" (
  > "%APPDATA%\AllegianceCore\player.json" echo {"Server":"","Port":6073,"Pilot":"pilot","Lobby":"http://127.0.0.1:5000"}
)
rem Call the native executable directly: cmd-to-PowerShell forwarding can reinterpret URL metacharacters.
"%~dp0Allegiance.exe" %*
exit /b %errorlevel%
