; ---------------------------------------------------------------------------
; WPR Windows installer (Inno Setup 6).
;
; Driven by .github/workflows/release.yml, which passes the version and the
; already-published payload directory on the ISCC command line:
;
;   ISCC /DAppVersion=0.0.18 /DPayloadDir=<publish dir> /DOutputDir=<out> WPR.iss
;
; The defaults below let you compile it locally against build-desktop.ps1 output:
;
;   .\build-desktop.ps1 -SelfContained          (-> Artifacts\desktop\Release-selfcontained)
;   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Packaging\windows\WPR.iss
; ---------------------------------------------------------------------------

#define AppName "WPR"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\..\Artifacts\desktop\Release-selfcontained"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\Artifacts\installer"
#endif

[Setup]
; Never change AppId. Windows uses it to match an upgrade to an existing
; install and to locate the uninstaller; a new value orphans every prior install.
AppId={{7F3A6C21-5D48-4E9B-B0A7-2C61E8D4F930}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Bubbleshum
AppPublisherURL=https://github.com/Bubbleshum/WPR
AppSupportURL=https://github.com/Bubbleshum/WPR/issues
AppUpdatesURL=https://github.com/Bubbleshum/WPR/releases

; PrivilegesRequired=lowest installs per-user under %LocalAppData%\Programs\WPR,
; so there is no UAC prompt. WPR keeps its game data in %LocalAppData%\WPR
; regardless, so a machine-wide install buys nothing here.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; The published payload is self-contained win-x64.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

OutputDir={#OutputDir}
OutputBaseFilename=WPR-Setup-{#AppVersion}
SetupIconFile=..\..\Src\UI\WPR.UI\Assets\avalonia-logo.ico
UninstallDisplayIcon={app}\WPR.UI.Desktop.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\WPR.UI.Desktop.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WPR.UI.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WPR.UI.Desktop.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Deliberately empty. Installed games, patched DLLs and save data live in
; %LocalAppData%\WPR\AppData\<ProductId>, outside {app}, and must survive an
; uninstall/reinstall cycle. Do not add a cleanup entry for that folder.
