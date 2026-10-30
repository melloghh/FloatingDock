; Inno Setup 6 script for FloatingDock.
;   ISCC /DAppVersion=1.6.4 /DSourceDir=..\..\artifacts\FloatingDock /O..\..\artifacts FloatingDock.iss
; Produces FloatingDock-Setup-x64.exe. Everything the app needs is inside the installer (self-contained .NET runtime + self-contained Windows App SDK):
; nothing is downloaded during installation, no admin rights are required (per-user by default).
#ifndef AppVersion
  #define AppVersion "1.6.4"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\FloatingDock"
#endif

[Setup]
; the AppId must never change: it is how updates / uninstall find the existing installation
AppId={{6F7B1E8A-3C1D-4D55-9A0B-4C6B1F0D2A11}
AppName=FloatingDock
AppVersion={#AppVersion}
AppVerName=FloatingDock {#AppVersion}
AppPublisher=Mello
AppPublisherURL=https://github.com/melloghh/FloatingDock
AppSupportURL=https://github.com/melloghh/FloatingDock/issues
AppUpdatesURL=https://github.com/melloghh/FloatingDock/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\FloatingDock
UsePreviousAppDir=yes
DefaultGroupName=FloatingDock
DisableProgramGroupPage=yes
OutputBaseFilename=FloatingDock-Setup-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayName=FloatingDock
UninstallDisplayIcon={app}\FloatingDock.exe
SetupIconFile=..\..\src\FloatingDock.App\Assets\AppIcon.ico
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "autostart"; Description: "Start FloatingDock when I sign in to Windows"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\FloatingDock"; Filename: "{app}\FloatingDock.exe"
Name: "{autoprograms}\FloatingDock - Restore Windows taskbar"; Filename: "{app}\FloatingDock.exe"; Parameters: "--restore-taskbar"; Comment: "Puts the Windows taskbar back if FloatingDock left it hidden"
Name: "{autodesktop}\FloatingDock"; Filename: "{app}\FloatingDock.exe"; Tasks: desktopicon

[Registry]
; remove the "start with Windows" entry the app itself writes (HKCU\...\Run\FloatingDock) when FloatingDock is uninstalled
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "FloatingDock"; ValueType: none; Flags: dontcreatekey uninsdeletevalue

[Run]
; "start with Windows" is a setting owned by the app; the installer only asks for it (the app writes the Run entry itself)
Filename: "{app}\FloatingDock.exe"; Parameters: "--enable-autostart"; Flags: nowait runasoriginaluser; Tasks: autostart
Filename: "{app}\FloatingDock.exe"; Description: "Start FloatingDock"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not WizardIsTaskSelected('autostart')

; User data in %LOCALAPPDATA%\FloatingDock (settings, pinned apps, icon packs, taskbar recovery record) is intentionally NOT touched by install, update or uninstall.

[Code]
// Abort if the app cannot stop or restore the recorded taskbar state.
// Never force-kill unrelated processes by executable name or remove the recovery tool after failed restoration.
function StopRunningDock: Boolean;
var
  Exe: String;
  Code: Integer;
begin
  Result := True;
  Exe := ExpandConstant('{app}\FloatingDock.exe');
  if not FileExists(Exe) then Exit;
  if (not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code)) or (Code <> 0) then
  begin
    Result := False;
    Exit;
  end;
  if CheckForMutexes('Local\FloatingDock.SingleInstance') then
  begin
    Result := False;
    Exit;
  end;
  Result := Exec(Exe, '--restore-taskbar --quiet', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Result then Result := Code = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopRunningDock then
    Result := 'FloatingDock could not stop or restore the Windows taskbar. Close it from the tray, use Restore Windows taskbar, then try again. Your data and installation have been kept.';
end;

function InitializeUninstall: Boolean;
begin
  Result := StopRunningDock;
  if not Result then
    MsgBox('FloatingDock could not stop or restore the Windows taskbar. Close it from the tray, use Restore Windows taskbar, then try again. Your data and installation have been kept.', mbError, MB_OK);
end;
