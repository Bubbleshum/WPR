; ---------------------------------------------------------------------------
; WPR Windows installer (Inno Setup 6).
;
; Driven by .github/workflows/release.yml, which passes the version and the
; already-published payload directory on the ISCC command line:
;
;   ISCC /DAppVersion=0.1.0 /DPayloadDir=<publish dir> /DOutputDir=<out> WPR.iss
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
SetupIconFile=..\..\Src\Platforms\WPR.Platform.Windows\Assets\wpr.ico
UninstallDisplayIcon={app}\WPR.Platform.Windows.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The desktop head's assembly name changed WPR.UI.Desktop -> WPR.Platform.Windows on
; 2026-08-29. AppId is unchanged, so an upgrade lands in the same {app} and the [Icons]
; entries below are rewritten to the new exe — but the OLD payload is not overwritten by
; name and would otherwise sit there forever, leaving two launchable exes in the folder
; (the stale one still runs, against stale assemblies). Sweep it.
; Safe to delete unconditionally: these names are produced by nothing in the current build.
; Do NOT extend this to %LocalAppData%\WPR — see the [UninstallDelete] note below.
Type: files; Name: "{app}\WPR.UI.Desktop.exe"
Type: files; Name: "{app}\WPR.UI.Desktop.dll"
Type: files; Name: "{app}\WPR.UI.Desktop.pdb"
Type: files; Name: "{app}\WPR.UI.Desktop.deps.json"
Type: files; Name: "{app}\WPR.UI.Desktop.runtimeconfig.json"
Type: files; Name: "{app}\WPR.UI.Desktop.xml"

; Shim assemblies dissolved into their neighbours. These MUST be swept for a reason worse than
; tidiness: a leftover DLL still RESOLVES. Game IL patched before the dissolution carries an
; AssemblyRef naming the old assembly; if the stale file is present that ref binds successfully
; to a second copy of e.g. WPR.WindowsCompability.Application, while the host binds the new one
; in WPR.Framework.Silverlight. Same type FullName, two assemblies, two Application.Current
; singletons — so ApplicationLaunch's `Application.Current.ProductId = …` is never seen by the
; game's copy and ComputeCurrentProductFolder returns empty. That is a silent wrong-folder
; failure instead of the loud FileNotFoundException the version bump is supposed to force.
; (Windows has no auto-repatch on version mismatch — that exists only on Android — so an
; upgrading user who just clicks Run would hit exactly this.)
;   WPR.XnaCompability      dissolved at ApplicationPatcher.Version 16
;   WPR.StandardCompability dissolved at 17
;   WPR.WindowsCompability  dissolved at 18
Type: files; Name: "{app}\WPR.XnaCompability.dll"
Type: files; Name: "{app}\WPR.XnaCompability.pdb"
Type: files; Name: "{app}\WPR.StandardCompability.dll"
Type: files; Name: "{app}\WPR.StandardCompability.pdb"
Type: files; Name: "{app}\WPR.WindowsCompability.dll"
Type: files; Name: "{app}\WPR.WindowsCompability.pdb"
; Microsoft.Xna.Framework.GamerServices (dissolved at 19) is the most important sweep of the set.
; The others were WPR-internal names; this one carried a REAL WP7 identity that games reference by
; simple name. A leftover copy therefore satisfies an old game's AssemblyRef perfectly, and the
; game silently runs against a stale GamerServices — separate SignedInGamer, separate achievement
; DB context — while the host uses the one in WPR.Framework.Xna.
Type: files; Name: "{app}\Microsoft.Xna.Framework.GamerServices.dll"
Type: files; Name: "{app}\Microsoft.Xna.Framework.GamerServices.pdb"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\WPR.Platform.Windows.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WPR.Platform.Windows.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WPR.Platform.Windows.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Deliberately empty. Installed games, patched DLLs and save data live in
; %LocalAppData%\WPR\AppData\<ProductId>, outside {app}, and must survive an
; uninstall/reinstall cycle. Do not add a cleanup entry for that folder.
