; Storiva installer (Inno Setup 6). Built by the GitHub Actions release job:
;   ISCC.exe /DAppVersion=0.4.0 /DSourceDir=<portable publish folder> /O<output folder> installer\Storiva.iss
; Installs the portable, self-contained Storiva.exe (no .NET runtime needed) for the current user, without
; administrator rights, into %LOCALAPPDATA%\Programs\Storiva. Adds a Start menu shortcut and an entry in
; Settings > Apps for uninstalling. Saved connections and settings (%LOCALAPPDATA%\Storiva) are kept on uninstall.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<folder with Storiva.exe>
#endif

[Setup]
AppId={{6A3C1F52-8E0B-4D2B-9C55-2F7A64B1D9E3}
AppName=Storiva
AppVersion={#AppVersion}
AppVerName=Storiva {#AppVersion}
AppPublisher=Storiva contributors
AppPublisherURL=https://github.com/StormLives/Storiva
AppSupportURL=https://github.com/StormLives/Storiva/issues
AppUpdatesURL=https://github.com/StormLives/Storiva/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Storiva
VersionInfoDescription=Storiva installer
DefaultDirName={autopf}\Storiva
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\Storiva.App\Assets\Brand\storiva.ico
UninstallDisplayIcon={app}\Storiva.exe
UninstallDisplayName=Storiva
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=Storiva-v{#AppVersion}-win-x64-setup
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Storiva.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Storiva"; Filename: "{app}\Storiva.exe"
Name: "{autodesktop}\Storiva"; Filename: "{app}\Storiva.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Storiva.exe"; Description: "{cm:LaunchProgram,Storiva}"; Flags: nowait postinstall skipifsilent
