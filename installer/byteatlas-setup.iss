; ByteAtlas installer (Inno Setup 6). Built by packaging\build-release.ps1:
;   ISCC /DAppVersion=0.4.0 /DSourceDir=..\publish\win-x64 /O..\release installer\byteatlas-setup.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#define AppUrl "https://github.com/Taoism/ByteAtlas"

[Setup]
; keep this AppId: it lets new versions upgrade older ByteAtlas installs in place
AppId={{2EBCAE69-DB6E-4FDF-BCAE-FB3D2FA80E68}
AppName=ByteAtlas
AppVersion={#AppVersion}
AppVerName=ByteAtlas {#AppVersion}
AppPublisher=Taoism
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\ByteAtlas
DefaultGroupName=ByteAtlas
DisableProgramGroupPage=yes
AllowNoIcons=yes
; installs for the current user without an administrator prompt; the dialog offers all users
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\src\ByteAtlas\Assets\byteatlas.ico
UninstallDisplayIcon={app}\ByteAtlas.exe
UninstallDisplayName=ByteAtlas
LicenseFile=..\LICENSE
OutputBaseFilename=ByteAtlas-{#AppVersion}-setup-win-x64
Compression=lzma2/max
SolidCompression=yes
; close a running ByteAtlas before replacing files
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add byteatlas-cli to PATH (use it from any command prompt)"; GroupDescription: "Command line:"; Flags: unchecked
Name: "explorermenu"; Description: """Analyze with ByteAtlas"" on folders and drives"; GroupDescription: "Explorer right-click menu (current user):"; Flags: unchecked
Name: "explorersearch"; Description: """Search with ByteAtlas"" on folders and drives"; GroupDescription: "Explorer right-click menu (current user):"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\ByteAtlas"; Filename: "{app}\ByteAtlas.exe"; Comment: "Where did my disk space go?"
Name: "{group}\{cm:UninstallProgram,ByteAtlas}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ByteAtlas"; Filename: "{app}\ByteAtlas.exe"; Tasks: desktopicon

[Registry]
; the same keys as Options › System in the app, so its check boxes show what the installer did
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ByteAtlas"; ValueType: string; ValueName: ""; ValueData: "Analyze with ByteAtlas"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ByteAtlas"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ByteAtlas.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ByteAtlas\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ByteAtlas.exe"" ""%1"""; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\ByteAtlas"; ValueType: string; ValueName: ""; ValueData: "Analyze with ByteAtlas"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\ByteAtlas"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ByteAtlas.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\ByteAtlas\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ByteAtlas.exe"" ""%1"""; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ByteAtlasSearch"; ValueType: string; ValueName: ""; ValueData: "Search with ByteAtlas"; Flags: uninsdeletekey; Tasks: explorersearch
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ByteAtlasSearch"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ByteAtlas.exe"",0"; Tasks: explorersearch
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ByteAtlasSearch\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ByteAtlas.exe"" --search ""%1"""; Tasks: explorersearch
Root: HKCU; Subkey: "Software\Classes\Drive\shell\ByteAtlasSearch"; ValueType: string; ValueName: ""; ValueData: "Search with ByteAtlas"; Flags: uninsdeletekey; Tasks: explorersearch
Root: HKCU; Subkey: "Software\Classes\Drive\shell\ByteAtlasSearch"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ByteAtlas.exe"",0"; Tasks: explorersearch
Root: HKCU; Subkey: "Software\Classes\Drive\shell\ByteAtlasSearch\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ByteAtlas.exe"" --search ""%1"""; Tasks: explorersearch

[UninstallDelete]
; menu entries the app itself added (Options › System) point into {app}
Type: dirifempty; Name: "{app}"

[Run]
Filename: "{app}\ByteAtlas.exe"; Description: "{cm:LaunchProgram,ByteAtlas}"; Flags: nowait postinstall skipifsilent

[Code]
// PATH entry for byteatlas-cli: the user's PATH for a per-user install, the system PATH for all users
function EnvironmentKey(var Root: Integer): String;
begin
  if IsAdminInstallMode then
  begin
    Root := HKEY_LOCAL_MACHINE;
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  end
  else
  begin
    Root := HKEY_CURRENT_USER;
    Result := 'Environment';
  end;
end;

function PathContains(Paths, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(Dir: String);
var
  Root: Integer;
  Key, Paths: String;
begin
  Key := EnvironmentKey(Root);
  if not RegQueryStringValue(Root, Key, 'Path', Paths) then
    Paths := '';
  if PathContains(Paths, Dir) then
    exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then
    Paths := Paths + ';';
  RegWriteExpandStringValue(Root, Key, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Root, P: Integer;
  Key, Paths: String;
begin
  Key := EnvironmentKey(Root);
  if not RegQueryStringValue(Root, Key, 'Path', Paths) then
    exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then
    exit;
  Delete(Paths, P, Length(Dir) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  RegWriteExpandStringValue(Root, Key, 'Path', Paths);
end;

// the app can add its Explorer entries itself (Options › System); remove them with the program
procedure RemoveMenuKeys;
begin
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\Directory\shell\ByteAtlas');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\Drive\shell\ByteAtlas');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\Directory\shell\ByteAtlasSearch');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\Drive\shell\ByteAtlasSearch');
end;

// closes ByteAtlas instances started from {app} (not other copies): politely, then after 5 s for good
procedure CloseRunningCopies;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process ByteAtlas,byteatlas-cli -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | ForEach-Object { ' +
    '[void]$_.CloseMainWindow(); if (-not $_.WaitForExit(5000)) { $_.Kill(); $_.WaitForExit(3000) } }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function CopiesRunning: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if (Get-Process ByteAtlas,byteatlas-cli -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' }) { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 1);
end;

// silent upgrades: close the running copy first (interactive installs show Inno's "close applications" page)
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if WizardSilent and CopiesRunning then
    CloseRunningCopies;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if CopiesRunning then
  begin
    if not UninstallSilent then
      Result := MsgBox('ByteAtlas is running. Close it and uninstall?', mbConfirmation, MB_YESNO) = IDYES;
    if Result then
      CloseRunningCopies;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}'));
    RemoveMenuKeys;
  end;
end;
