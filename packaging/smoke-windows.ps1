param([Parameter(Mandatory=$true)][string]$Installer,
      [Parameter(Mandatory=$true)][string]$Portable,
      [string]$PreviousInstaller)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path $Installer).Path
$Portable = (Resolve-Path $Portable).Path
if ($PreviousInstaller) { $PreviousInstaller = (Resolve-Path $PreviousInstaller).Path }
$work = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -ItemType Directory $work | Out-Null
Expand-Archive -Path $Portable -DestinationPath $work
$bundle = Join-Path $work 'Allegiance-win-x64'
$config = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'AllegianceCore'
if (Test-Path $config) { throw "Smoke requires a clean CI user config: $config" }
New-Item -ItemType Directory $config | Out-Null
'{"Pilot":"R2 preserve me"}' | Set-Content (Join-Path $config 'player.json')
'{"R2":"preserve keymap"}' | Set-Content (Join-Path $config 'keymap.json')
$saved = @{}
Get-ChildItem $config -File | ForEach-Object { $saved[$_.Name] = (Get-FileHash $_.FullName).Hash }

function Run-Setup([string]$File, [string[]]$SetupArgs) {
    $process = Start-Process -FilePath $File -ArgumentList $SetupArgs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$File exited $($process.ExitCode)" }
}
function Check-Config {
    foreach ($name in $saved.Keys) {
        if ((Get-FileHash (Join-Path $config $name)).Hash -ne $saved[$name]) {
            throw "User data changed: $name"
        }
    }
}

try {
    # Test default directory/mode rather than overriding it with /DIR.
    foreach ($mode in @('CURRENTUSER', 'ALLUSERS')) {
        $install = if ($mode -eq 'CURRENTUSER') {
            Join-Path $env:LOCALAPPDATA 'Programs\AllegianceCore'
        } else { Join-Path $env:ProgramFiles 'AllegianceCore' }
        if (Test-Path $install) { throw "Smoke requires a clean install path: $install" }
        $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/$mode", '/TASKS=desktopicon')
        $firstInstaller = if ($PreviousInstaller) { $PreviousInstaller } else { $Installer }
        Run-Setup $firstInstaller $setupArgs
        $registryRoot = if ($mode -eq 'CURRENTUSER') { 'HKCU:' } else { 'HKLM:' }
        $registration = "$registryRoot\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E5427756-E0DF-4CF0-80F2-668C53EC578A}_is1"
        $before = (Get-ItemProperty $registration).DisplayVersion
        # Upgrade under the same AppId/path, checking all original payload bytes.
        Run-Setup $Installer $setupArgs
        $after = (Get-ItemProperty $registration).DisplayVersion
        if ($PreviousInstaller -and ([version]$after -le [version]$before)) {
            throw "Expected a newer installed version: $before -> $after"
        }
        if (@(Get-ChildItem $install -Filter 'unins*.exe').Count -ne 1) {
            throw 'Upgrade created a second uninstaller'
        }
        Get-ChildItem $bundle -File -Recurse | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($bundle, $_.FullName)
            if ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash (Join-Path $install $relative)).Hash) {
                throw "Installed payload differs from portable: $relative"
            }
        }
        $shell = New-Object -ComObject WScript.Shell
        $menuRoot = if ($mode -eq 'CURRENTUSER') { 'Programs' } else { 'CommonPrograms' }
        $desktopRoot = if ($mode -eq 'CURRENTUSER') { 'DesktopDirectory' } else { 'CommonDesktopDirectory' }
        $shortcuts = @(
            (Join-Path ([Environment]::GetFolderPath($menuRoot)) 'AllegianceCore.lnk'),
            (Join-Path ([Environment]::GetFolderPath($desktopRoot)) 'AllegianceCore.lnk'))
        foreach ($shortcut in $shortcuts) {
            if (!(Test-Path $shortcut)) { throw "Missing shortcut: $shortcut" }
            $link = $shell.CreateShortcut($shortcut)
            if ($link.TargetPath -ne (Join-Path $install 'Allegiance.exe') -or $link.Arguments -ne '') {
                throw 'Shortcut must launch intro with no arguments'
            }
        }
        Push-Location $work
        try {
            & (Join-Path $install 'Allegiance.exe') --check-assets
            if ($LASTEXITCODE -ne 0) { throw 'Installed asset check failed' }
        } finally { Pop-Location }
        Check-Config
        Run-Setup (Join-Path $install 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        if (Test-Path (Join-Path $install 'Allegiance.exe')) { throw 'Uninstall left the executable' }
        if (Test-Path $registration) { throw 'Uninstall left its registration' }
        foreach ($shortcut in $shortcuts) {
            if (Test-Path $shortcut) { throw "Uninstall left shortcut: $shortcut" }
        }
        Check-Config
        Write-Host "$mode install, upgrade $before -> $after, payload, shortcuts, assets, uninstall, retained config: PASS"
    }
} finally {
    Remove-Item -Recurse -Force $work
    Remove-Item -Recurse -Force $config
}
