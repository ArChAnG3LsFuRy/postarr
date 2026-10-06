; Inno Setup installer for Postarr — installs the app, registers a Windows service,
; opens the firewall port, and adds a Start-Menu shortcut to the web UI.
; NOTE: AppId is deliberately unchanged from the Curatarr releases so this upgrades an existing
; install in place (same folder, same settings/database) rather than appearing as a second app.
; Executables, service, data folder and database are all Postarr-named; the installer migrates
; the legacy Curatarr service, shortcuts, firewall rule and leftover binaries on upgrade.

#define AppName    "Postarr"
; Both can be overridden on the command line, which is how the GitHub release build uses this script:
;   ISCC /DAppVersion=1.8.0 /DSourceRoot=<checkout folder> deploy\Postarr.iss
#ifndef AppVersion
  #define AppVersion "1.7.5"
#endif
#ifndef SourceRoot
  #define SourceRoot "C:\CuratarrC"
#endif
#define AppExe     "Postarr.exe"
#define TrayExe    "PostarrTray.exe"
#define AppPort    "5286"
#define SvcName    "Postarr"
#define LegacySvc  "Curatarr"

[Setup]
SourceDir={#SourceRoot}
AppId={{A82FBEB9-2EB0-4BFC-9510-C4648A39CAE9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Postarr
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
SetupIconFile=src\Postarr\wwwroot\favicon.ico
UninstallDisplayIcon={app}\{#TrayExe}
OutputDir=dist
OutputBaseFilename=Postarr-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "dist\Postarr\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; The tray is a self-contained WinForms app: it needs PostarrTray.dll, System.Windows.Forms.dll and
; its deps/runtimeconfig alongside the exe. Copying ONLY PostarrTray.exe (the native launcher stub)
; left it unable to find its own assembly, so it crashed instantly on every launch — no tray icon.
; Merge the whole tray publish into {app}; shared .NET runtime DLLs overwrite with identical versions.
Source: "dist\Tray\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "LICENSE";                     DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.md";      DestDir: "{app}"; Flags: ignoreversion
Source: "deploy\service-install.bat";   DestDir: "{app}"; Flags: ignoreversion
Source: "deploy\service-uninstall.bat"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; System-tray helper: launches at login for every user, plus a Start-Menu entry.
Name: "{commonstartup}\Postarr Tray"; Filename: "{app}\{#TrayExe}"
Name: "{group}\Postarr Tray";         Filename: "{app}\{#TrayExe}"
Name: "{group}\Postarr (Web UI)";     Filename: "http://localhost:{#AppPort}"
Name: "{group}\Uninstall Postarr";    Filename: "{uninstallexe}"
Name: "{autodesktop}\Postarr";        Filename: "{app}\{#TrayExe}"; Tasks: desktopicon

[InstallDelete]
; Remove the old Curatarr-named shortcuts so an upgrade doesn't leave both sets behind.
Type: files; Name: "{commonstartup}\Curatarr Tray.lnk"
Type: files; Name: "{group}\Curatarr Tray.lnk"
Type: files; Name: "{group}\Curatarr (Web UI).lnk"
Type: files; Name: "{group}\Uninstall Curatarr.lnk"
Type: files; Name: "{autodesktop}\Curatarr.lnk"
; …and the pre-rename binaries, so an upgraded install doesn't keep both exes side by side.
Type: files; Name: "{app}\Curatarr.exe"
Type: files; Name: "{app}\Curatarr.dll"
Type: files; Name: "{app}\Curatarr.pdb"
Type: files; Name: "{app}\CuratarrTray.exe"
Type: files; Name: "{app}\Curatarr.deps.json"
Type: files; Name: "{app}\Curatarr.runtimeconfig.json"
Type: files; Name: "{app}\Curatarr.staticwebassets.runtime.json"

[Code]
const
  SvcName   = '{#SvcName}';
  LegacySvc = '{#LegacySvc}';
  Port      = '{#AppPort}';

procedure RunHidden(const Params: String);
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

procedure StopService;
begin
  // Stop BOTH names: upgrading from Curatarr must retire the old service, otherwise it keeps
  // running against the same files and port alongside the new one.
  RunHidden('stop ' + SvcName);
  RunHidden('stop ' + LegacySvc);
  Sleep(2500);
end;

procedure InstallService;
var exe, bin: String;
begin
  exe := ExpandConstant('{app}\{#AppExe}');
  RunHidden('delete ' + SvcName);
  RunHidden('delete ' + LegacySvc);   // remove the pre-rename service for good
  // binPath must quote the exe path (Program Files has a space); sc.exe wants \" inside.
  bin := 'create ' + SvcName + ' binPath= "\"' + exe + '\" --urls http://0.0.0.0:' + Port +
         '" start= auto DisplayName= "Postarr"';
  RunHidden(bin);
  RunHidden('description ' + SvcName + ' "Postarr - Plex artwork manager"');
  RunHidden('failure ' + SvcName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  RunHidden('start ' + SvcName);
end;

procedure AddFirewallRule;
var rc: Integer;
begin
  // Drop the pre-rename rule first so upgrades don't leave two rules on the same port.
  Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="Curatarr"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  // ...and our own earlier rule, which allowed every network profile.
  Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="Postarr"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  // Every network profile, but only from the local subnet: Windows labels many home networks "Public",
  // so the private/domain-only rule in 1.7.0 blocked other PCs on the LAN. LocalSubnet still keeps the
  // port closed to anything beyond the network the PC is on, and Postarr's login protects it from there.
  Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="Postarr" dir=in action=allow protocol=TCP profile=any remoteip=localsubnet localport=' + Port,
    '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

procedure KillTray;
var rc: Integer;
begin
  // Close any running tray helper so its exe isn't locked during (re)install.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im PostarrTray.exe /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

// Stop the service and tray before files are (over)written on upgrade, so they aren't locked.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopService;
  KillTray;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallService;
    AddFirewallRule;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/im PostarrTray.exe /f', '', SW_HIDE, ewWaitUntilTerminated, rc);
    RunHidden('stop ' + SvcName);
    RunHidden('stop ' + LegacySvc);
    Sleep(2000);
    RunHidden('delete ' + SvcName);
    RunHidden('delete ' + LegacySvc);
    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="Postarr"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="Curatarr"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;

[Run]
; Start the tray helper immediately, in the user's (non-elevated) session.
Filename: "{app}\PostarrTray.exe"; Flags: nowait runasoriginaluser skipifsilent
Filename: "http://localhost:{#AppPort}"; Description: "Open Postarr in your browser"; Flags: postinstall shellexec nowait skipifsilent
