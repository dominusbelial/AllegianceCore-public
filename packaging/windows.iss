; R2: the same staged payload as the portable ZIP. Never delete user config.
#ifndef BundleDir
  #error BundleDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{E5427756-E0DF-4CF0-80F2-668C53EC578A}
AppName=AllegianceCore
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=AllegianceCore contributors
AppPublisherURL=https://github.com/dominusbelial/AllegianceCore-public
DefaultDirName={autopf}\AllegianceCore
DefaultGroupName=AllegianceCore
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#LicensePage}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Allegiance.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AllegianceCore"; Filename: "{app}\Allegiance.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\AllegianceCore"; Filename: "{app}\Allegiance.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Allegiance.exe"; Description: "Launch AllegianceCore"; Flags: nowait postinstall skipifsilent runasoriginaluser
