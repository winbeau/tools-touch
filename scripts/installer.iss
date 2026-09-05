#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif

[Setup]
AppId={{0C8D6389-B695-4E27-9838-C559FBE45D41}
AppName=Tools Touch
AppVersion={#AppVersion}
AppPublisher=winbeau
AppPublisherURL=https://github.com/winbeau/tools-touch
AppSupportURL=https://github.com/winbeau/tools-touch/issues
AppUpdatesURL=https://github.com/winbeau/tools-touch/releases
DefaultDirName={localappdata}\Programs\ToolsTouch
DefaultGroupName=Tools Touch
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=Local\ToolsTouch
SetupMutex=Local\ToolsTouchSetup
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\ToolsTouch.exe
OutputBaseFilename=ToolsTouch-Setup-{#AppVersion}-win-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Tools Touch Windows Installer

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Tools Touch"; Filename: "{app}\ToolsTouch.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Tools Touch"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tools Touch"; Filename: "{app}\ToolsTouch.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ToolsTouch.exe"; Description: "Launch Tools Touch"; Flags: nowait postinstall skipifsilent unchecked

; Application data lives outside {app}, in LocalAppData\ToolsTouch.
; Do not add a recursive UninstallDelete rule: user-created files must survive.
