; Chronicle Windows installer (Inno Setup: https://jrsoftware.org/isinfo.php).
;
; Expects a published build already sitting in ..\publish (i.e. run
; ..\scripts\publish-windows.ps1 -OutputDir ..\publish first, or point PublishDir at
; wherever you ran it -- see the CI workflow, docker-publish.yml's Windows counterpart, for
; the exact sequence this runs under).
;
; Compile: iscc chronicle.iss (optionally /DAppVersion=1.2.3 -- see AppVersion below for what
; happens if it's omitted).
;
; What this installer does that publish-windows.ps1 + install-service.ps1 don't, on their own:
; packages both into one double-click .exe with a normal wizard, an Add/Remove Programs entry,
; a real uninstaller, and a port prompt -- someone installing Chronicle never needs to open
; PowerShell or know ports.json exists.

#ifndef AppVersion
  ; Falls back to a clearly-not-a-real-release placeholder rather than silently shipping a
  ; stale hardcoded number (the exact mistake Chronicle.API.csproj's own SetVersionFromGit
  ; target was added to stop happening for the app's own version -- see that file's comment).
  ; CI always passes the real one via /DAppVersion; a local dev compile without it should look
  ; obviously wrong, not quietly plausible.
  #define AppVersion "0.0.0-local-dev-build"
#endif

#define PublishDir "..\publish"

[Setup]
AppId={{8F5E7A1C-3B9D-4E6A-9C2F-1D4B6A8E9F3C}
AppName=Chronicle
AppVersion={#AppVersion}
AppPublisher=Chronicle Contributors
AppPublisherURL=https://github.com/thegoddamnbeckster/Chronicle
AppSupportURL=https://github.com/thegoddamnbeckster/Chronicle/issues
AppUpdatesURL=https://github.com/thegoddamnbeckster/Chronicle/releases
; {autopf} is Program Files (the 64-bit one on a 64-bit machine, given ArchitecturesInstallIn64BitMode
; below) -- standard Windows convention for where an installed app lives. install-service.ps1's
; own bare -InstallPath default matches this (see that script), and its own icacls step grants
; the service account write access here, since Program Files isn't writable by LocalService/
; NetworkService by default and Chronicle writes its database/logs/keys into this same folder.
DefaultDirName={autopf}\Chronicle
DefaultGroupName=Chronicle
; Installing/removing a Windows Service and a firewall rule both need elevation -- the wizard
; itself prompts for UAC once, rather than each PowerShell step prompting separately.
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=Chronicle-Setup-{#AppVersion}
SetupIconFile=..\src\Chronicle.API\chronicle.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
; Per-user report (2026-09-29, live install test): the small header-corner image (the only
; branding shown without these) reads as "a dot" at that size in every installer, Chronicle's
; own included -- that's inherent to how tiny that slot is, not a Chronicle-specific problem.
; The real fix is a proper large image (shown down the left side of every wizard page), which
; these two settings add. Generated from chronicle.ico by installer/generate-wizard-images.py
; (run it again if chronicle.ico ever changes) -- comma-separated pairs are the plain/high-DPI
; variant Inno picks between automatically based on the display's scaling.
WizardImageFile=WizardImage.bmp,WizardImage2x.bmp
WizardSmallImageFile=WizardSmallImage.bmp,WizardSmallImage2x.bmp
LicenseFile=..\LICENSE
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Everything publish-windows.ps1 produced -- the exe, wwwroot (the built web UI), native
; deps, appsettings.json. Recursing the whole publish dir means this installer never drifts
; from what that script actually outputs; nothing here needs updating when its output changes.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\install-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\uninstall-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{group}\Chronicle"; Filename: "{code:GetChronicleUrl}"; IconFilename: "{app}\Chronicle.API.exe"; Comment: "Open Chronicle in your browser"
Name: "{group}\Uninstall Chronicle"; Filename: "{uninstallexe}"

[Run]
; No component choice -- installing Chronicle means installing and starting the service, end
; of story. PortPage's value drives BOTH install-service.ps1's ports.json write and the port
; the "Open Chronicle" links below point at -- one prompt, no chance of the two drifting apart.
; {code:GetServiceAccountArgs} expands to "" (LocalService, the default) or
; "-ServiceUser ... -ServicePassword ..." when a network account was entered on NetworkAccountPage
; -- see that page's own comment for why this exists.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-service.ps1"" -InstallPath ""{app}"" -Port {code:GetChroniclePort} {code:GetServiceAccountArgs}"; \
    StatusMsg: "Registering the Chronicle service..."; \
    Flags: runhidden waituntilterminated
Filename: "{code:GetChronicleUrl}"; Description: "Open Chronicle"; Flags: postinstall shellexec skipifsilent

[UninstallRun]
; RunOnceId so this doesn't get skipped as a "duplicate" of anything else, and so Inno logs
; it distinctly if uninstall ever needs debugging.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-service.ps1"""; \
    RunOnceId: "UninstallChronicleService"; Flags: runhidden waituntilterminated

[UninstallDelete]
; install-service.ps1/uninstall-service.ps1 are copied in under [Files] above alongside the
; publish output (so [Run]/[UninstallRun] can find them at a known {app}-relative path) --
; explicitly cleaning up the folder they land in, since Inno's own uninstaller otherwise only
; removes files it itself installed via [Files], not a directory some of those files happen to
; sit in if it's not empty for some other reason (e.g. a user-created ports.json is
; deliberately left behind, matching uninstall-service.ps1's own "data and configuration files
; are not removed" promise -- see that script's own doc).
Type: dirifempty; Name: "{app}\scripts"

[Code]
var
  PortPage: TInputQueryWizardPage;
  NetworkAccountPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  { wpSelectDir: right after the user picks (or accepts the default) install folder. }
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Chronicle Port', 'Which port should Chronicle listen on?',
    'The default (7979) is right for almost every install; only change it if something else ' +
    'on this machine already uses that port.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '7979';

  { Root-caused live (2026-09-29): Chronicle's File Scanner plugin reads plain filesystem
    paths with no SMB handling of its own, so reaching a network share (a NAS, another
    machine's folder) depends entirely on which account the Windows Service runs as.
    LocalService (the default) is a virtual account with no real credential store, so it
    can't hold network share credentials -- the reliable fix is running the service as a real
    local account whose username+password match an account already set up on whichever
    machine hosts the share. Optional and blank by default: most installs don't need this. }
  NetworkAccountPage := CreateInputQueryPage(PortPage.ID,
    'Network Share Access', 'Does Chronicle need to reach shares on other machines?',
    'Leave this blank unless Chronicle will scan folders on other computers on your network ' +
    '(e.g. a NAS). If so, enter an account for the Chronicle service to run as. You must ' +
    'also create this same username and password on every other machine that hosts a share ' +
    'you want Chronicle to reach on the other machine(s) yourself.');
  NetworkAccountPage.Add('Username (leave blank to skip):', False);
  NetworkAccountPage.Add('Password:', True);
end;

{ Same question PortManager.cs's own CheckPort asks at Chronicle's actual startup (see that
  file's IsPortInUse), asked here instead so a taken port is caught during install rather than
  as a service that registers successfully and then silently fails to start. PowerShell, not a
  raw WinSock call, to stay consistent with how this installer already shells out for the
  service/firewall steps rather than adding a second way of talking to Windows. }
function IsPortInUse(Port: String): Boolean;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "if (Get-NetTCPConnection -LocalPort ' + Port +
    ' -State Listen -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    { Couldn't even run the check (e.g. powershell.exe missing) -- don't block install over a
      check that itself failed; install-service.ps1's own port-conflict handling downstream
      is the backstop either way. }
    Result := False;
    Exit;
  end;
  Result := (ResultCode = 1);
end;

{ PowerShell single-quoted string literals escape an embedded ' by doubling it -- without this,
  a password containing a literal quote character would break out of the string and either
  fail or (worse) run as unintended PowerShell. }
function PsQuote(S: String): String;
begin
  { StringChangeEx modifies its first argument by reference and returns the replacement count
    (an Integer), not a new string -- Result itself, assigned from S first, is what gets
    passed by reference and returned here. }
  Result := S;
  StringChangeEx(Result, '''', '''''', True);
end;

{ Creates the account on first use, or resets its password to match if it already exists (e.g.
  a second run of this installer, or the account was pre-created with a different password) --
  either way, this machine ends up with exactly the username/password the wizard page says it
  will, which is the whole point: it has to match what's set up on the other machines by hand. }
function CreateOrUpdateLocalAccount(Username, Password: String): Boolean;
var
  ResultCode: Integer;
  Cmd: String;
begin
  Cmd := '$u=''' + PsQuote(Username) + '''; $sec = ConvertTo-SecureString ''' + PsQuote(Password) +
    ''' -AsPlainText -Force; if (Get-LocalUser -Name $u -ErrorAction SilentlyContinue) ' +
    '{ Set-LocalUser -Name $u -Password $sec -PasswordNeverExpires $true } else ' +
    '{ New-LocalUser -Name $u -Password $sec -PasswordNeverExpires $true -AccountNeverExpires }';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "' + Cmd + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortNum: Integer;
  NetUsername, NetPassword: String;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    { StrToIntDef, not TryStrToInt -- Inno's Pascal Script is its own subset, not full Delphi
      RTL, and doesn't have TryStrToInt. -1 as the "didn't parse" sentinel is safe: it can
      never collide with a real port number, which the range check below rejects anyway. }
    PortNum := StrToIntDef(PortPage.Values[0], -1);
    if (PortNum < 1) or (PortNum > 65535) then
    begin
      MsgBox('Please enter a valid port number (1-65535).', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    WizardForm.Cursor := crHourglass;
    try
      if IsPortInUse(PortPage.Values[0]) then
      begin
        MsgBox('Port ' + PortPage.Values[0] + ' is already in use by something else on this ' +
          'machine. Please choose a different port.', mbError, MB_OK);
        Result := False;
      end;
    finally
      WizardForm.Cursor := crDefault;
    end;
  end
  else if CurPageID = NetworkAccountPage.ID then
  begin
    NetUsername := Trim(NetworkAccountPage.Values[0]);
    NetPassword := NetworkAccountPage.Values[1];
    if (NetUsername = '') and (NetPassword = '') then
      Exit;   { skipped -- LocalService stays the default, nothing to do }
    if (NetUsername = '') or (NetPassword = '') then
    begin
      MsgBox('Enter both a username and a password, or leave both blank to skip this.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
    WizardForm.Cursor := crHourglass;
    try
      if not CreateOrUpdateLocalAccount(NetUsername, NetPassword) then
      begin
        MsgBox('Could not create or update the local account ''' + NetUsername + '''. ' +
          'Check that the password meets this machine''s password policy, then try again.',
          mbError, MB_OK);
        Result := False;
      end;
    finally
      WizardForm.Cursor := crDefault;
    end;
  end;
end;

function GetChroniclePort(Param: String): String;
begin
  Result := PortPage.Values[0];
end;

function GetChronicleUrl(Param: String): String;
begin
  Result := 'http://localhost:' + GetChroniclePort('');
end;

function GetServiceAccountArgs(Param: String): String;
begin
  { Known limitation: a password containing a literal " would break this command line (it's
    quoted with " for both Inno's own [Run] Parameters syntax and PowerShell's argument
    parsing). CreateOrUpdateLocalAccount above doesn't have this problem (PsQuote handles '
    correctly for a PowerShell string literal) -- only this hand-off to install-service.ps1's
    own -ServicePassword argument does. Not worth the added complexity of a second escaping
    scheme for a character vanishingly few generated or chosen passwords will contain. }
  if Trim(NetworkAccountPage.Values[0]) = '' then
    Result := ''
  else
    Result := '-ServiceUser "' + NetworkAccountPage.Values[0] + '" -ServicePassword "' +
      NetworkAccountPage.Values[1] + '"';
end;
