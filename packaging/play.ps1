$ErrorActionPreference = 'Stop'
$configDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'AllegianceCore'
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$configPath = Join-Path $configDir 'player.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    '{"Server":"","Port":6073,"Pilot":"pilot","Lobby":"http://127.0.0.1:5000"}' | Set-Content -Encoding UTF8 -LiteralPath $configPath
    Write-Host "Edit $configPath to remember your server and pilot. Empty Server opens the lobby."
}
Set-Location -LiteralPath $PSScriptRoot
& (Join-Path $PSScriptRoot 'Allegiance.exe') @args
exit $LASTEXITCODE
