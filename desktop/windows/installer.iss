; The installer of Verticals for Windows: what the .dmg is on a Mac. desktop/windows/bundle.py builds the app's folder and
; compiles this with Inno Setup 6, passing AppVersion, SourceDir (the folder), OutputDir and IconFile.
;
; It installs for the current user, without admin rights, into %LOCALAPPDATA%\Programs\Verticals, adds Verticals to the
; Start menu and registers verticals:// links. An update replaces the whole folder, as an update replaces the whole
; Verticals.app, after quitting a running copy as its Quit does, so the database stops cleanly. The data in
; %LOCALAPPDATA%\Verticals stays through updates and uninstalling, as ~/Library/Application Support/Verticals does.
; Where Windows 10 lacks WebView2, the installer fetches Microsoft's own installer for it.

#ifndef AppVersion
  #define AppVersion "0.2"
#endif
#ifndef SourceDir
  #error Run desktop/windows/bundle.py, which passes SourceDir
#endif

[Setup]
AppId={{C9C39C23-DBB7-48C1-A2C7-8ADB3555E4FA}
AppName=Verticals
AppVersion={#AppVersion}
AppVerName=Verticals {#AppVersion}
AppPublisher=Verticals
AppPublisherURL=https://github.com/konstantinopolskii/verticals
AppSupportURL=https://github.com/konstantinopolskii/verticals/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Verticals
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1903, the first with .NET Framework 4.8 built in.
MinVersion=10.0.18362
OutputDir={#OutputDir}
OutputBaseFilename=Verticals-Setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Verticals.exe
UninstallDisplayName=Verticals
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Verticals is quit with --quit below, never closed by the Restart Manager, which would stop its database abruptly.
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.GettingWebView2=Getting Microsoft Edge WebView2, which shows the board
ru.GettingWebView2=Загружаю Microsoft Edge WebView2: в нём показывается доска

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The whole app is replaced: files an older version had and this one has not are gone.
Type: filesandordirs; Name: "{app}\verticals"
Type: filesandordirs; Name: "{app}\desktop"
Type: filesandordirs; Name: "{app}\.agents"
Type: filesandordirs; Name: "{app}\python"
Type: filesandordirs; Name: "{app}\site"
Type: filesandordirs; Name: "{app}\pg"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The same AppUserModelID as Verticals.exe gives itself, so a pinned icon and the running window are one on the taskbar.
Name: "{autoprograms}\Verticals"; Filename: "{app}\Verticals.exe"; AppUserModelID: "Verticals.Desktop"
Name: "{autodesktop}\Verticals"; Filename: "{app}\Verticals.exe"; AppUserModelID: "Verticals.Desktop"; Tasks: desktopicon

[Registry]
; verticals://goal/<id> opens the task (the app writes the same at each start).
Root: HKCU; Subkey: "Software\Classes\verticals"; ValueType: string; ValueName: ""; ValueData: "URL:Verticals link"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\verticals"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\verticals\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Verticals.exe"",0"
Root: HKCU; Subkey: "Software\Classes\verticals\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Verticals.exe"" ""%1"""
; Open at Login is the person's choice in the tray menu; uninstalling takes it away with the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Verticals"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Verticals.exe"; Description: "{cm:LaunchProgram,Verticals}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Verticals.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitVerticals"

[Code]
var
  DownloadPage: TDownloadWizardPage;

// Microsoft's check: the runtime's version under EdgeUpdate, for the machine or for this user.
function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'))
    or (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(CustomMessage('GettingWebView2'), '', nil);
end;

// Without network the install goes on: the app itself says where to get WebView2.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and not WebView2Installed then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      except
        Log('WebView2 was not installed: ' + GetExceptionMessage);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

// A running Verticals quits as its Quit does (the database stops cleanly) before its files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Verticals.exe')) then
    Exec(ExpandConstant('{app}\Verticals.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
