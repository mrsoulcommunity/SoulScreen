; SoulScreen installer. Builds on top of tools/publish.ps1's self-contained output in
; dist/SoulScreen - run that first. Installs per-user (no admin, no UAC prompt) into
; {localappdata}\Programs\SoulScreen, which matters beyond convenience: the in-app updater
; (UpdateService.cs) mirrors a downloaded release over whatever folder the running exe sits
; in, with no elevation step, so an admin-owned Program Files install would make every future
; "Check for updates" fail silently on a permissions error.
;
; Usage:
;   pwsh tools/publish.ps1 -SelfContained
;   ISCC tools/installer.iss

#define AppVersion GetEnv("SOULSCREEN_VERSION")
#if AppVersion == ""
  #define AppVersion "1.3.0"
#endif

[Setup]
AppId={{8F4C6B2A-6C3E-4A1E-9B5D-2B7E9F1C4D6A}
AppName=SoulScreen
AppVersion={#AppVersion}
AppPublisher=SoulScreen
AppPublisherURL=https://github.com/mrsoulcommunity/SoulScreen
AppUpdatesURL=https://github.com/mrsoulcommunity/SoulScreen/releases
DefaultDirName={localappdata}\Programs\SoulScreen
DefaultGroupName=SoulScreen
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=SoulScreen-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\SoulScreen.App\Assets\SoulScreen.ico
UninstallDisplayIcon={app}\SoulScreen.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\dist\SoulScreen\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SoulScreen"; Filename: "{app}\SoulScreen.App.exe"
Name: "{group}\Uninstall SoulScreen"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SoulScreen"; Filename: "{app}\SoulScreen.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SoulScreen.App.exe"; Description: "Launch SoulScreen now"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; Logs and the update work folder are written after install; the uninstaller only removes
; what it laid down unless asked, so this cleans up what the running app added alongside it.
Type: filesandordirs; Name: "{app}"
