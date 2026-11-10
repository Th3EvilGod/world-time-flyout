; World Time Flyout installer (Inno Setup 6). Built by CI:
;   ISCC /DAppVersion=0.2.1 /DVersionNumeric=0.2.1 /DSourceDir=<publish dir> /DOutputDir=<dir> WorldTimeFlyout.iss
; Asks "for me only" (no admin, %LOCALAPPDATA%\Programs) or "for all users"
; (Program Files, asks for admin once). /CURRENTUSER and /ALLUSERS choose on the
; command line. The in-app updater runs this same installer with /VERYSILENT and
; the mode it was installed with; it closes the running app, replaces the files
; and starts the app again as the signed-in user (never elevated).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef VersionNumeric
  #define VersionNumeric "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\WorldTimeFlyout"
#endif
#ifndef OutputDir
  #define OutputDir "..\installer-out"
#endif

#define AppName "World Time Flyout"
#define AppExe "WorldTimeFlyout.exe"
#define RepoUrl "https://github.com/Th3EvilGod/world-time-flyout"

[Setup]
; Never change AppId: it is how Windows and updates recognize this app.
AppId={{F4DB3BC9-6641-4980-8AAD-3BBDC55DBF72}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Th3EvilGod
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#VersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup
VersionInfoProductTextVersion={#AppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=WorldTimeFlyout-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\WorldTimeFlyout\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--show"; Comment: "Your cities next to the taskbar calendar"

[Run]
; Interactive install: offer to open it. Silent install (updates): start it in the background.
Filename: "{app}\{#AppExe}"; Parameters: "--show"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
const
  WM_APP_EXIT = $8003; { WM_APP + 3: the app's message window exits on it }
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F4DB3BC9-6641-4980-8AAD-3BBDC55DBF72}_is1';

function DirWritable(Dir: String): Boolean;
var
  F: String;
begin
  F := AddBackslash(Dir) + 'wtf-write-test.tmp';
  Result := SaveStringToFile(F, 'x', False);
  if Result then DeleteFile(F);
end;

{ A "just me" copy that was put in a folder only admins can write (e.g. by running
  0.2.0's installer as administrator) can't be updated silently without admin
  rights. Leave it running as it was instead of failing halfway; the panel tells
  the user to reinstall for all users. }
function InitializeSetup(): Boolean;
var
  Dir: String;
  ResultCode: Integer;
begin
  Result := True;
  if WizardSilent and (not IsAdmin) and (not IsAdminInstallMode) and
     RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', Dir) and
     DirExists(Dir) and (not DirWritable(Dir)) then
  begin
    Log('Installed for this user in a folder that needs admin rights: ' + Dir + '. Not updating.');
    Exec(AddBackslash(Dir) + '{#AppExe}', '--background', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    Result := False;
  end;
end;

{ Closes the running app so its files can be replaced: ask first, then make sure
  (versions before 0.2.0 don't know the request). }
procedure StopRunningApp();
var
  Wnd: HWND;
  I, ResultCode: Integer;
begin
  Wnd := FindWindowByClassName('WorldTimeFlyout.MessageWindow');
  if Wnd <> 0 then
  begin
    PostMessage(Wnd, WM_APP_EXIT, 0, 0);
    for I := 1 to 50 do
    begin
      Sleep(100);
      if FindWindowByClassName('WorldTimeFlyout.MessageWindow') = 0 then Break;
    end;
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { "Start with Windows" entry, if it points at this install }
    if RegQueryStringValue(HKCU, RunKey, 'WorldTimeFlyout', Value) then
    begin
      Log('Startup entry: ' + Value);
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0 then
      begin
        if RegDeleteValue(HKCU, RunKey, 'WorldTimeFlyout') then Log('Startup entry removed')
        else Log('Startup entry could not be removed');
      end;
    end
    else
      Log('No startup entry');
    if (not UninstallSilent) and
       (MsgBox('Also delete your cities and settings?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{localappdata}\WorldTimeFlyout'), True, True, True);
  end;
end;
