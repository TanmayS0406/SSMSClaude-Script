; Inno Setup script for the SSMS Claude Chat setup .exe. Build-Installer.ps1 compiles it with ISCC:
;   ISCC.exe /DAppVersion=1.4.0 /DVsixPath=<path to .vsix> installer\SsmsClaudeChat.iss
;
; SSMS 21+ has no "Manage Extensions" dialog and double-clicking a .vsix opens the wrong installer, so setup
; runs the VSIXInstaller.exe that ships with SSMS. Command line extras:
;   /SSMSPATH="D:\...\Common7\IDE"   SSMS installed somewhere non-standard
;   /VERYSILENT                      unattended install (fails if SSMS is running)

#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif
#ifndef VsixPath
  #define VsixPath "..\src\SsmsChatExtension\bin\Release\SsmsChatExtension.vsix"
#endif
; Must equal the <Identity Id="..."> in source.extension.vsixmanifest.
#define ExtensionId "SsmsChatExtension.8CAA5A9A-15AF-420A-8646-04F08447673D"

[Setup]
AppId={{1667CCB9-A526-499F-A238-F869B4ED0EB2}
AppName=SSMS Claude Chat
AppVersion={#AppVersion}
AppVerName=SSMS Claude Chat {#AppVersion}
AppPublisher=Tanmay Seth
AppPublisherURL=https://github.com/TanmayS0406/SSMSClaude-Script
AppSupportURL=https://github.com/TanmayS0406/SSMSClaude-Script/issues
VersionInfoVersion={#AppVersion}
; The extension itself goes into SSMS; this folder only holds the uninstaller.
DefaultDirName={localappdata}\Programs\SSMS Claude Chat
DisableDirPage=yes
DisableProgramGroupPage=yes
; Per-user install, like VSIXInstaller's default: no UAC prompt.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=SsmsClaudeChat-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=SSMS Claude Chat
CloseApplications=no
SetupLogging=yes

[Files]
Source: "{#VsixPath}"; DestName: "SsmsChatExtension.vsix"; Flags: dontcopy

[Run]
Filename: "{code:GetSsmsExe}"; Description: "Start SQL Server Management Studio"; Flags: postinstall nowait skipifsilent

[Messages]
FinishedLabel=SSMS Claude Chat is installed. In SQL Server Management Studio, open View > SSMS Claude Chat.%n%nThe chat runs through the Claude Code CLI. If it is not installed yet, get it from https://claude.com/claude-code, then click "Sign in with Claude" in the chat window.

[Code]
var
  VsixInstaller: String;

function FindVsixInstaller(): String;
var
  Dir: String;
  Version: Integer;
begin
  Result := '';
  Dir := ExpandConstant('{param:SSMSPATH|}');
  if Dir <> '' then
  begin
    if FileExists(AddBackslash(Dir) + 'VSIXInstaller.exe') then
      Result := AddBackslash(Dir) + 'VSIXInstaller.exe';
    exit;
  end;
  for Version := 30 downto 21 do
  begin
    Dir := ExpandConstant('{commonpf64}') + '\Microsoft SQL Server Management Studio ' + IntToStr(Version) + '\Release\Common7\IDE\';
    if FileExists(Dir + 'VSIXInstaller.exe') then
    begin
      Result := Dir + 'VSIXInstaller.exe';
      exit;
    end;
  end;
end;

function GetSsmsExe(Param: String): String;
begin
  Result := ExtractFilePath(VsixInstaller) + 'Ssms.exe';
end;

function IsSsmsRunning(): Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Ssms.exe''');
    Result := Processes.Count > 0;
  except
  end;
end;

function WaitForSsmsClosed(): Boolean;
begin
  Result := True;
  while IsSsmsRunning() do
    if SuppressibleMsgBox('SQL Server Management Studio is running. Close it, then click Retry.',
                          mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
end;

function InitializeSetup(): Boolean;
begin
  VsixInstaller := FindVsixInstaller();
  Result := VsixInstaller <> '';
  if not Result then
    SuppressibleMsgBox('Could not find SQL Server Management Studio 21 or 22.' + #13#10 + #13#10 +
                       'Install SSMS first. If it is in a non-standard folder, run setup with' + #13#10 +
                       '/SSMSPATH="C:\path\to\SSMS\Release\Common7\IDE"', mbCriticalError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Page: TOutputProgressWizardPage;
  Vsix: String;
  Code: Integer;
begin
  Result := '';
  if not WaitForSsmsClosed() then
  begin
    Result := 'SQL Server Management Studio must be closed to install the extension.';
    exit;
  end;

  ExtractTemporaryFile('SsmsChatExtension.vsix');
  Vsix := ExpandConstant('{tmp}\SsmsChatExtension.vsix');
  Log('VSIXInstaller: ' + VsixInstaller);

  Page := CreateOutputProgressPage('Installing', 'Adding SSMS Claude Chat to SQL Server Management Studio.');
  Page.Show;
  try
    // Remove any earlier copy first, so reinstalling the same version (or going back one) also works.
    Page.SetText('Removing any previous version...', '');
    Exec(VsixInstaller, '/quiet /uninstall:{#ExtensionId}', '', SW_HIDE, ewWaitUntilTerminated, Code);

    Page.SetText('Installing the extension...', '');
    if not Exec(VsixInstaller, '/quiet "' + Vsix + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Result := Format('VSIXInstaller failed (exit code %d). Details are in the newest VSIXInstaller_*.log in %%TEMP%%.', [Code]);
  finally
    Page.Hide;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitForSsmsClosed();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    VsixInstaller := FindVsixInstaller();
    if VsixInstaller <> '' then
      Exec(VsixInstaller, '/quiet /uninstall:{#ExtensionId}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;
