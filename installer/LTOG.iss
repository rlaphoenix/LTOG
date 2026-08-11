; ===========================================================================
;  LTOG - Inno Setup installer
;
;  Bundles everything needed to run the LTOG GUI on a fresh Windows 10/11 x64
;  machine:
;
;    * the WinUI 3 GUI (self-contained: .NET + Windows App SDK baked in) -> {app}\gui
;    * the LTFS engine (ltfs/mkltfs/unltfs/ltfsck) + all runtime DLLs -> {app}
;    * WinFsp 2.1 (the signed virtual-drive driver) via its MSI
;
;  The GUI needs no external runtime (the .NET and Windows App SDK runtimes are
;  baked in; the only system requirement is Windows 10 1809+, which provides the
;  UCRT). WinFsp is the sole prerequisite: an opt-in checkbox (checked by
;  default) on the Tasks page, installed on a dedicated "Setting up dependencies"
;  page - the last step before Finished - which shows a live log and progress
;  bar, and is skipped automatically if a suitable version is already present.
;
;  It also regenerates ltfs.conf with the real install path, because the file
;  staged in dist/ hard-codes the developer's build directory.
;
;  Compile with Inno Setup 6.3 or newer (ISCC LTOG.iss), or run
;  build-installer.ps1 which fetches the prerequisites and compiles for you.
; ===========================================================================

#define MyAppName       "LTOG"
#ifndef MyAppVersion
#define MyAppVersion    "1.0.0"
#endif
#define MyAppId         "{6E9D2B4A-1C3F-4E58-9A7D-2F5B8C1E4D0A}"
#define MyAppPublisher  "rlaphoenix"
#define MyAppURL        "https://github.com/rlaphoenix/LTOG"
#define MyAppExeName    "LTOG.exe"
; The GUI lives in a subfolder; LtfsEnv.Resolve() finds ltfs.exe/ltfs.conf by
; walking up to the GUI exe's parent, so this MUST mirror the dist/ layout
; (ltfs.exe at {app}, the GUI under {app}\gui).
#define GuiDir          "gui"

; Pinned prerequisite. build-installer.ps1 downloads this into redist\.
#define WinFspMsi       "winfsp-2.1.25156.msi"

[Setup]
AppId={{#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#GuiDir}\{#MyAppExeName}
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=LTOG-{#MyAppVersion}-setup
SetupIconFile=..\dist\{#GuiDir}\Assets\icon.ico
; Maximum ratio: ultra preset + 64 MB dictionary, one solid stream. The payload
; has many near-identical WinAppSDK DLLs and per-locale .mui files, so the large
; dictionary + solid compression dedups aggressively across them.
; ISCC.exe is 32-bit and the ultra64 encoder needs ~700 MB, so the compression
; is offloaded to a separate process to avoid an out-of-memory abort.
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
; The GUI is an x64 self-contained build and WinFsp installs an x64 driver,
; so install only on native x64 Windows.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; Windows App SDK / WinUI 3 requires Windows 10 1809 (build 17763) or newer.
MinVersion=10.0.17763
; Driver + Program Files install need elevation.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Required dependency - checked by default. Leave it checked unless you manage
; WinFsp yourself; mounting a tape as a drive needs it. It is still skipped
; automatically at install time if a suitable version is already present.
Name: "deps_winfsp"; Description: "WinFsp virtual-drive driver  (required - mounting a tape as a drive needs it)"; GroupDescription: "Required dependency:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; --- Application payload: the entire dist/ tree (LTFS engine + GUI). ---
; ltfs.conf is excluded because it is regenerated post-install with the real
; install path; *.pdb debug symbols are not needed at runtime.
Source: "..\dist\*"; DestDir: "{app}"; Excludes: "ltfs.conf,*.pdb"; \
    Flags: recursesubdirs createallsubdirs ignoreversion

; --- Legal: license + third-party notices, shipped alongside the binaries so
; end users receive the required texts (LGPL/GPL/ICU/zlib/etc.). ---
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: recursesubdirs createallsubdirs

; --- Prerequisite: staged to {tmp} (auto-removed when Setup exits), and run by
; the "Setting up dependencies" page in [Code] after the files are copied. ---
Source: "redist\{#WinFspMsi}"; DestDir: "{tmp}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#GuiDir}\{#MyAppExeName}"; WorkingDir: "{app}\{#GuiDir}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#GuiDir}\{#MyAppExeName}"; WorkingDir: "{app}\{#GuiDir}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#GuiDir}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    WorkingDir: "{app}\{#GuiDir}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Regenerated at install time, so not tracked by the installer's file list.
Type: files; Name: "{app}\ltfs.conf"

[Code]
{ ---------------------- prerequisite detection ----------------------------- }

{ The bundled WinFsp version (must track {#WinFspMsi}). Used to decide whether an
  already-installed WinFsp is new enough for the winfsp-x64.dll shipped in dist/. }
#define WinFspMajor   2
#define WinFspMinor   1
#define WinFspBuild   25156

function WinFspNeedsInstall: Boolean;
var
  Dir: String;
  InstMS, InstLS, NeedMS, NeedLS: Cardinal;
begin
  { No WinFsp registered at all -> must install. (Its 32-bit MSI always writes
    under WOW6432Node, so HKLM32 reads it.) }
  if not (RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir)
          and (Dir <> '') and DirExists(Dir)) then
  begin
    Result := True;
    Exit;
  end;

  { WinFsp is present. The bundled winfsp-x64.dll talks to the installed
    winfsp.sys driver, and WinFsp keeps the driver backward-compatible with
    OLDER user-mode callers but not the reverse. So upgrade only when the
    installed driver is OLDER than the one we bundle; leave a same-or-newer
    driver untouched. GetVersionNumbers returns MS=(major<<16|minor),
    LS=(build<<16|revision). }
  NeedMS := ({#WinFspMajor} shl 16) or {#WinFspMinor};
  NeedLS := {#WinFspBuild} shl 16;
  if GetVersionNumbers(AddBackslash(Dir) + 'bin\winfsp-x64.sys', InstMS, InstLS) then
    Result := (InstMS < NeedMS) or ((InstMS = NeedMS) and (InstLS < NeedLS))
  else
    Result := True;   { registered but driver unreadable -> repair/upgrade }
end;

{ ---------------------- dependency setup page ----------------------------- }
{ The prerequisites are installed on a dedicated wizard page shown right after
  the files are copied and just before the Finished page, with a live log and a
  progress bar (like a typical installer). The actual work lives in
  EnsureDependencyWork so it can also run during a silent install, where wizard
  pages never appear. }

var
  DepPage:     TWizardPage;
  DepLog:      TNewMemo;
  DepProgress: TNewProgressBar;
  DepDone:     Boolean;

function Stamp: String;
begin
  Result := '[' + GetDateTimeString('hh:nn:ss', #0, #0) + ']  ';
end;

procedure DepLogLine(const S: String);
begin
  Log(S);                       { also recorded in the setup log when /LOG is used }
  if DepLog <> nil then
  begin
    DepLog.Lines.Add(S);
    WizardForm.Refresh;         { paint the new line before any blocking Exec }
  end;
end;

procedure SetDepProgress(P: Integer);
begin
  if DepProgress <> nil then
  begin
    DepProgress.Position := P;
    WizardForm.Refresh;
  end;
end;

{ The dist/ltfs.conf staged at build time hard-codes the developer's path. Write
  a fresh plugin registry pointing at this install. libltfs parses values by
  whitespace and keeps trailing characters, so the file MUST use LF line endings
  (a trailing CR would be appended to each plugin DLL path and break loading). }
procedure WriteLtfsConf;
var
  Base, Conf, LF, Target: String;
begin
  Base := ExpandConstant('{app}');
  StringChangeEx(Base, '\', '/', True);   { FUSE/libltfs want forward slashes }
  LF := #10;
  Conf :=
    '# Generated by the LTOG installer - plugin registry for this install' + LF +
    'plugin driver ltotape_win ' + Base + '/libdriver-ltotape-win.dll' + LF +
    'plugin driver file ' + Base + '/libdriver-file.dll' + LF +
    'plugin iosched unified ' + Base + '/libiosched-unified.dll' + LF +
    'plugin iosched fcfs ' + Base + '/libiosched-fcfs.dll' + LF +
    'plugin kmi flatfile ' + Base + '/libkmi-flatfile.dll' + LF +
    'plugin kmi simple ' + Base + '/libkmi-simple.dll' + LF +
    'default driver ltotape_win' + LF +
    'default iosched unified' + LF +
    'default kmi none' + LF;
  Target := ExpandConstant('{app}\ltfs.conf');
  if SaveStringToFile(Target, Conf, False) then
    DepLogLine('  wrote ' + Target)
  else
    DepLogLine('  ERROR: could not write ' + Target);
end;

{ Install the prerequisites and write ltfs.conf. Runs once (DepDone guard), from
  either the dependency page (interactive) or ssPostInstall (silent). }
procedure EnsureDependencyWork;
var
  Code: Integer;
  MsiLog: String;
begin
  if DepDone then Exit;
  DepDone := True;

  { 1/2 - WinFsp (installs when absent, upgrades a driver older than the bundled DLL). }
  DepLogLine(Stamp + 'WinFsp virtual-drive driver');
  if not WizardIsTaskSelected('deps_winfsp') then
    DepLogLine('  skipped - left unchecked')
  else if not WinFspNeedsInstall then
    DepLogLine('  a suitable version is already installed - nothing to do')
  else
  begin
    MsiLog := ExpandConstant('{tmp}\winfsp-install.log');
    DepLogLine('  installing {#WinFspMsi} ...');
    if not Exec('msiexec.exe',
                '/i "' + ExpandConstant('{tmp}\{#WinFspMsi}') + '" /qn /norestart /L*v "' + MsiLog + '"',
                '', SW_HIDE, ewWaitUntilTerminated, Code) then
      DepLogLine('  ERROR: could not start msiexec - mounting needs WinFsp (https://winfsp.dev)')
    else if (Code = 0) or (Code = 1638) or (Code = 3010) then
      DepLogLine('  done (exit ' + IntToStr(Code) + ') - verbose log: ' + MsiLog)
    else
      DepLogLine('  WARNING: msiexec returned ' + IntToStr(Code) + ' - mounting needs WinFsp (https://winfsp.dev)');
  end;
  SetDepProgress(60);

  { 2/2 - LTFS plugin configuration. }
  DepLogLine(Stamp + 'Writing LTFS plugin configuration (ltfs.conf)');
  WriteLtfsConf;
  SetDepProgress(100);
  DepLogLine(Stamp + 'Setup complete.');
end;

procedure InitializeWizard;
begin
  { A custom page inserted right after the Installing page, so it is the last
    step before Finished. }
  DepPage := CreateCustomPage(wpInstalling, 'Setting up dependencies',
    'LTOG is installing the components it needs to run.');

  DepProgress := TNewProgressBar.Create(DepPage);
  DepProgress.Parent := DepPage.Surface;
  DepProgress.Left := 0;
  DepProgress.Top := 0;
  DepProgress.Width := DepPage.SurfaceWidth;
  DepProgress.Height := ScaleY(16);
  DepProgress.Min := 0;
  DepProgress.Max := 100;

  DepLog := TNewMemo.Create(DepPage);
  DepLog.Parent := DepPage.Surface;
  DepLog.Left := 0;
  DepLog.Top := DepProgress.Top + DepProgress.Height + ScaleY(8);
  DepLog.Width := DepPage.SurfaceWidth;
  DepLog.Height := DepPage.SurfaceHeight - DepLog.Top;
  DepLog.ReadOnly := True;
  DepLog.ScrollBars := ssVertical;
  DepLog.WantReturns := False;
  DepLog.Font.Name := 'Consolas';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (DepPage <> nil) and (CurPageID = DepPage.ID) then
  begin
    { Lock the wizard while the prerequisites install, then let the user proceed. }
    WizardForm.BackButton.Enabled := False;
    WizardForm.NextButton.Enabled := False;
    WizardForm.CancelButton.Enabled := False;
    WizardForm.Refresh;
    EnsureDependencyWork;
    WizardForm.NextButton.Enabled := True;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Wizard pages never display during a silent install, so run the same work in
    the post-install step there. The DepDone guard prevents a double run. }
  if (CurStep = ssPostInstall) and WizardSilent then
    EnsureDependencyWork;
end;

{ ============ running-process guard + clean (re)install ==================== }
{ LTOG must not be running during install/uninstall - its files would be locked,
  and force-killing it mid-mount is unsafe. We also remove any previous install
  *first*, so an update, downgrade or same-version reinstall never leaves stale
  files behind (Inno otherwise only overwrites and never deletes obsolete ones). }

const
  LtogUninstKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1';

function LtogRunning: Boolean;
var
  Code, I: Integer;
  ExecOut: TExecOutput;
begin
  Result := False;
  if ExecAndCaptureOutput('tasklist.exe',
       '/FI "IMAGENAME eq {#MyAppExeName}" /NH /FO CSV', '',
       SW_HIDE, ewWaitUntilTerminated, Code, ExecOut) then
    for I := 0 to GetArrayLength(ExecOut.StdOut) - 1 do
      if Pos('{#MyAppExeName}', ExecOut.StdOut[I]) > 0 then
      begin
        Result := True;
        Exit;
      end;
end;

procedure GracefullyCloseLtog;
var Code: Integer;
begin
  { taskkill WITHOUT /F posts WM_CLOSE to the app's window - a graceful close,
    not a hard kill. (An elevated installer may signal the user's app.) }
  Exec('taskkill.exe', '/IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function WaitForLtogToExit(TimeoutMs: Integer): Boolean;
var Waited: Integer;
begin
  Waited := 0;
  while LtogRunning and (Waited < TimeoutMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not LtogRunning;
end;

{ True if LTOG ends up closed (or was never running); False if the user cancels.
  Silent runs attempt a graceful close without prompting. }
function EnsureLtogClosed(Silent: Boolean): Boolean;
var
  Choice: Integer;
  Msg: String;
begin
  Result := True;
  if not LtogRunning then Exit;

  if Silent then
  begin
    GracefullyCloseLtog;
    Result := WaitForLtogToExit(15000);
    Exit;
  end;

  Msg :=
    'LTOG is currently running and must be closed before Setup can continue.' + #13#10#13#10 +
    'If you have any tapes mounted, please UNMOUNT them in LTOG first. Closing LTOG ' +
    'while a tape is mounted can interrupt an in-progress write and leave the cartridge ' +
    'index unwritten.' + #13#10#13#10 +
    'You can close LTOG yourself and retry, or let Setup close it gracefully for you.';

  while LtogRunning do
  begin
    Choice := TaskDialogMsgBox('LTOG is running', Msg, mbConfirmation, MB_YESNOCANCEL, [
      'Close LTOG &gracefully for me', 'I''ll close it myself - &retry', 'Cancel'], 0);
    if Choice = IDYES then
    begin
      GracefullyCloseLtog;
      if not WaitForLtogToExit(15000) then
        MsgBox('LTOG is still running - it may be waiting for you to confirm closing or to '
          + 'finish unmounting a tape. Please finish in LTOG, then retry.', mbInformation, MB_OK);
    end
    else if Choice = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    { IDNO falls through and re-checks - the user closes it manually. }
  end;
end;

function ReadUninstValue(const ValueName: String; var Value: String): Boolean;
begin
  Result := RegQueryStringValue(HKLM,   LtogUninstKey, ValueName, Value)
         or RegQueryStringValue(HKLM32, LtogUninstKey, ValueName, Value)
         or RegQueryStringValue(HKCU,   LtogUninstKey, ValueName, Value);
end;

function PreviousInstallPresent: Boolean;
begin
  Result := RegKeyExists(HKLM,   LtogUninstKey)
         or RegKeyExists(HKLM32, LtogUninstKey)
         or RegKeyExists(HKCU,   LtogUninstKey);
end;

{ Dotted numeric version compare: 1 if A>B, -1 if A<B, 0 if equal. }
function CompareVersions(A, B: String): Integer;
var SA, SB: String;
begin
  Result := 0;
  while ((A <> '') or (B <> '')) and (Result = 0) do
  begin
    if Pos('.', A) > 0 then begin SA := Copy(A, 1, Pos('.', A) - 1); Delete(A, 1, Pos('.', A)); end
    else begin SA := A; A := ''; end;
    if Pos('.', B) > 0 then begin SB := Copy(B, 1, Pos('.', B) - 1); Delete(B, 1, Pos('.', B)); end
    else begin SB := B; B := ''; end;
    if StrToIntDef(SA, 0) > StrToIntDef(SB, 0) then Result := 1
    else if StrToIntDef(SA, 0) < StrToIntDef(SB, 0) then Result := -1;
  end;
end;

{ Warn before a downgrade so it can never happen silently/accidentally. }
function InitializeSetup: Boolean;
var InstalledVer: String;
begin
  Result := True;
  if ReadUninstValue('DisplayVersion', InstalledVer) and (InstalledVer <> '') then
    if CompareVersions(InstalledVer, '{#MyAppVersion}') > 0 then
      { SuppressibleMsgBox so a silent/unattended install does not block - it
        proceeds (IDYES) by default. }
      Result := SuppressibleMsgBox('A newer version of LTOG (' + InstalledVer + ') is already installed.'
        + #13#10#13#10 + 'This installer will DOWNGRADE it to {#MyAppVersion}. Continue?',
        mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  UninstStr: String;
  Code, Waited: Integer;
begin
  Result := '';

  { 1. LTOG must be closed (locked files + safe shutdown). }
  if not EnsureLtogClosed(WizardSilent) then
  begin
    Result := 'Setup cannot continue while LTOG is running.';
    Exit;
  end;

  { 2. Remove any previous install first -> a clean install with no stale files,
       for updates, downgrades and same-version reinstalls alike. }
  if PreviousInstallPresent and ReadUninstValue('UninstallString', UninstStr)
     and (UninstStr <> '') then
    if Exec(RemoveQuotes(UninstStr), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
            '', SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      { The uninstaller relaunches a copy of itself, so the Exec above returns
        early; wait until its registry key disappears (up to ~20s). }
      Waited := 0;
      while PreviousInstallPresent and (Waited < 20000) do
      begin
        Sleep(200);
        Waited := Waited + 200;
      end;
    end;
end;

function InitializeUninstall: Boolean;
begin
  Result := EnsureLtogClosed(UninstallSilent);
  if not Result then
    MsgBox('Uninstall cannot continue while LTOG is running.', mbInformation, MB_OK);
end;
