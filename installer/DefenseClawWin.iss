; DefenseClaw for Windows - per-user installer (Inno Setup 6).
;
; Compile (the release workflow does exactly this; see .github/workflows/ci.yml):
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<folder with the two published exes> /DOutputDir=<where Setup goes> installer\DefenseClawWin.iss
;
; What it does, in one place (the contract is docs/PARITY-FOUNDATIONS.md, "Releases, installer and updates"):
;   - installs for the current user only, never elevates: %LOCALAPPDATA%\Programs\DefenseClawWin
;     (NOT ...\Programs\DefenseClaw, which is Cisco's DefenseClaw 0.8.x Setup folder)
;   - Start menu shortcut "DefenseClaw for Windows", optional sign-in autostart (task, on by default) as the HKCU Run value
;     DefenseClawWin = "<dir>\DefenseClaw.App.exe" --minimized
;   - an Add/Remove Programs entry under this script's own AppId. It NEVER touches the Add/Remove Programs entry of Cisco's DefenseClaw Setup,
;     %USERPROFILE%\.defenseclaw, DefenseClaw itself, or %ProgramFiles%\DefenseClaw Companion (the administrator helper's copy)
;   - closes a running copy first; offers "Launch DefenseClaw for Windows" at the end
;   - the uninstaller removes the program files, the shortcut and the Run value, and ASKS (default: keep) before deleting the
;     app's own data folder %LOCALAPPDATA%\DefenseClaw.App
;
; The installer and the exes are NOT Authenticode-signed. Trust comes from the SHA-256 in checksums.txt and the GitHub build
; provenance attestation (see the release page and SECURITY.md).

#ifndef AppVersion
  #error AppVersion is not defined. Pass /DAppVersion=X.Y.Z (the release workflow takes it from the tag).
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

; "1.2.3-dev" -> "1.2.3" for the numeric-only VersionInfoVersion field.
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

#define AppName "DefenseClaw for Windows"
#define AppExe "DefenseClaw.App.exe"
#define HelperExe "DefenseClaw.GatewayAdmin.exe"
#define RunValueName "DefenseClawWin"
#define RepoUrl "https://github.com/Rmontalbano/defenseclaw_win"

[Setup]
; Fixed forever: this GUID is how an upgrade finds the previous install and how self-update finds the Add/Remove Programs key
; (HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{290C7D59-D43A-4CF8-BE38-60332DD563EC}_is1). The leading "{{" is
; Inno's escape for one "{".
AppId={{290C7D59-D43A-4CF8-BE38-60332DD563EC}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DefenseClaw for Windows (community)
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#NumericVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany=DefenseClaw for Windows (community)

PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\DefenseClawWin
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; A second Setup cannot run at the same time as this one.
SetupMutex=DefenseClawWinSetup
; Restart Manager closes (and does not restart) a running copy; [Code] below handles what it cannot (a tray-only window).
CloseApplications=yes
CloseApplicationsFilter={#AppExe},{#HelperExe}
RestartApplications=no

LicenseFile=..\LICENSE
SetupIconFile=..\DefenseClaw.App\Assets\DefenseClaw.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=DefenseClawWin-{#AppVersion}-Setup-x64
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] for your Windows account only. It does not need administrator rights and does not change DefenseClaw itself.%n%nThis installer and the app are NOT Authenticode-signed, so Windows SmartScreen may warn about an unknown publisher. Check the download against checksums.txt (SHA-256) or the GitHub build attestation on the release page before you run it.%n%nClose DefenseClaw for Windows first if it is running; Setup will offer to close it.

[Tasks]
; On by default (no "unchecked" flag). Unchecking it on an upgrade removes the sign-in value that was there.
Name: "autostart"; Description: "Start {#AppName} when I sign in (minimized to the tray)"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#HelperExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Directly in the Programs folder (no sub-folder); an existing shortcut of the same name is replaced.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName}"

[Registry]
; Only the DefenseClawWin value. Overwrites a dev value that points at a repo publish folder.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValueName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#RunValueName}"; Flags: deletevalue; Tasks: not autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; Leftovers of a self-update (DefenseClaw.App.exe.old, DefenseClaw.GatewayAdmin.exe.old).
Type: files; Name: "{app}\*.old"

[Code]
const
  AppProcessName = '{#AppExe}';

// Escapes a path for a WQL string literal (backslash and quote).
function WqlQuote(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '''', '\''', True);
end;

// The process ids of a DefenseClaw.App.exe that runs from THIS install folder. A dev copy started from a repo publish folder is
// not ours to close, so the match is on the full path.
function RunningAppPids(const Dir: String): array of Integer;
var
  Locator, Service, Procs: Variant;
  I, Count: Integer;
  Query: String;
begin
  SetArrayLength(Result, 0);
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Query := 'SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = ''' + WqlQuote(AddBackslash(Dir) + AppProcessName) + '''';
    Procs := Service.ExecQuery(Query);
    Count := Procs.Count;
    SetArrayLength(Result, Count);
    for I := 0 to Count - 1 do
      Result[I] := Procs.ItemIndex(I).ProcessId;
  except
    // WMI unavailable: Restart Manager (CloseApplications) is still in effect, and file replacement reports a locked exe.
    SetArrayLength(Result, 0);
  end;
end;

// Asks the running app to close (WM_CLOSE, not a kill), waits a few seconds, and if it is still there asks the person to exit it
// from the tray icon. Returns True when nothing of ours is running any more.
function CloseRunningApp(const Dir: String; const Why: String): Boolean;
var
  Pids: array of Integer;
  I, Waited, ResultCode: Integer;
begin
  Result := True;
  Pids := RunningAppPids(Dir);
  if GetArrayLength(Pids) = 0 then
    Exit;

  for I := 0 to GetArrayLength(Pids) - 1 do
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/PID ' + IntToStr(Pids[I]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  while True do
  begin
    Waited := 0;
    while (Waited < 8000) and (GetArrayLength(RunningAppPids(Dir)) > 0) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
    if GetArrayLength(RunningAppPids(Dir)) = 0 then
      Exit;
    if MsgBox('{#AppName} is still running.' + #13#10 + #13#10 +
              'Exit it (right-click its tray icon and choose Exit), then click Retry to ' + Why + ', or Cancel to stop.',
              mbConfirmation, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseRunningApp(WizardDirValue, 'continue installing') then
    Result := '{#AppName} is running from the install folder. Exit it and run Setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseRunningApp(ExpandConstant('{app}'), 'continue uninstalling');
end;

procedure CurrentUninstallStep(CurrentStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurrentStep = usUninstall then
  begin
    // Belt and braces next to uninsdeletevalue: the value is removed even if Setup was run with the autostart task unchecked.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValueName}');
  end
  else if CurrentStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\DefenseClaw.App');
    // A silent uninstall keeps the data. Interactively the default button is No (keep).
    if (not UninstallSilent) and DirExists(DataDir) then
    begin
      if MsgBox('Also delete the app''s own data folder?' + #13#10 + #13#10 + DataDir + #13#10 + #13#10 +
                'It holds this app''s settings, caches and logs. It does NOT hold DefenseClaw itself: your ~\.defenseclaw folder and the ' +
                'DefenseClaw installation are never touched by this uninstaller.' + #13#10 + #13#10 +
                'If you turned on "Run gateway as administrator", its copy in Program Files\DefenseClaw Companion is also left in place; ' +
                'turn that mode off in the app before uninstalling to remove it, or delete that folder by hand.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
