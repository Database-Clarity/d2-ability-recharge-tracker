#ifndef AppVersion
  #error AppVersion must be supplied by the release build
#endif

#ifndef CoreRuntimeUrl
  #error CoreRuntimeUrl must be supplied by the release build
#endif

#ifndef CoreRuntimeSHA256
  #error CoreRuntimeSHA256 must be supplied by the release build
#endif

#ifndef DesktopRuntimeUrl
  #error DesktopRuntimeUrl must be supplied by the release build
#endif

#ifndef DesktopRuntimeSHA256
  #error DesktopRuntimeSHA256 must be supplied by the release build
#endif

[Setup]
AppId={{83D77947-D57A-42D8-A35C-6C8868FC3C01}
AppName=Destiny 2 Ability Recharge Tracker
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\D2AbilityTracker
UsePreviousAppDir=no
UsePreviousGroup=no
DefaultGroupName=Destiny 2 Ability Recharge Tracker
OutputDir={#OutputPath}
OutputBaseFilename=D2AbilityTracker-v{#AppVersion}-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\D2AbilityTracker.exe
CloseApplications=yes

[Files]
Source: "{#PublishPath}\D2AbilityTracker.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "Launch Tracker.vbs"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Destiny 2 Ability Recharge Tracker"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\Launch Tracker.vbs"""; IconFilename: "{app}\D2AbilityTracker.exe"

[Run]
Filename: "{sys}\wscript.exe"; Parameters: """{app}\Launch Tracker.vbs"""; Description: "Launch the ability tracker"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: RuntimeAvailable

[Code]
var
  DownloadPage: TDownloadWizardPage;
  RuntimeWarning: String;

function HasDesktopRuntimeAt(RootKey: Integer; const Subkey: String): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, Subkey, Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
      if (Pos('9.0.', Versions[I]) = 1) and (Pos('-', Versions[I]) = 0) then
      begin
        Result := True;
        Exit;
      end;
end;

function HasDesktopRuntime: Boolean;
const
  RuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
begin
  Result := HasDesktopRuntimeAt(HKLM64, RuntimeKey) or HasDesktopRuntimeAt(HKCU, RuntimeKey);
end;

function HasLocalDesktopRuntime: Boolean;
var
  Root: String;
begin
  Root := ExpandConstant('{app}\.dotnet');
  Result := FileExists(Root + '\dotnet.exe') and
    DirExists(Root + '\shared\Microsoft.WindowsDesktop.App') and
    DirExists(Root + '\shared\Microsoft.NETCore.App');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading required .NET Desktop Runtime',
    'The runtime stays in your local app folder; no administrator access or restart is needed.', nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  RuntimeRoot: String;
  StagingRoot: String;
begin
  Result := '';
  NeedsRestart := False;
  RuntimeWarning := '';
  if HasDesktopRuntime or HasLocalDesktopRuntime then
    Exit;

  if MsgBox('This app needs the Microsoft .NET 9 Desktop Runtime (x64).' + #13#10 +
    'Download it now? It will be installed in your local tracker folder and shared by future updates.',
    mbConfirmation, MB_YESNO) <> IDYES then
  begin
    RuntimeWarning := 'The tracker was installed, but .NET Desktop Runtime was not downloaded.';
    Exit;
  end;

  RuntimeRoot := ExpandConstant('{app}\.dotnet');
  StagingRoot := ExpandConstant('{app}\.dotnet-staging');
  DownloadPage.Clear;
  DownloadPage.Add('{#CoreRuntimeUrl}', 'core-runtime.zip', '{#CoreRuntimeSHA256}');
  DownloadPage.Add('{#DesktopRuntimeUrl}', 'desktop-runtime.zip', '{#DesktopRuntimeSHA256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if not ForceDirectories(StagingRoot) then
        RaiseException('Could not create the local runtime staging folder.');
      if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference = ''Stop''; Expand-Archive -LiteralPath ''' +
        ExpandConstant('{tmp}\core-runtime.zip') + ''' -DestinationPath ''' + StagingRoot + ''' -Force; ' +
        'Expand-Archive -LiteralPath ''' +
        ExpandConstant('{tmp}\desktop-runtime.zip') + ''' -DestinationPath ''' + StagingRoot + ''' -Force"',
        '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
        RaiseException(Format('Could not extract the local .NET Desktop Runtime (exit code %d).', [ExitCode]));

      if not FileExists(StagingRoot + '\dotnet.exe') or
        not DirExists(StagingRoot + '\shared\Microsoft.WindowsDesktop.App') or
        not DirExists(StagingRoot + '\shared\Microsoft.NETCore.App') then
        RaiseException('The downloaded archive does not contain the expected .NET Desktop Runtime files.');

      DelTree(RuntimeRoot, True, True, True);
      if not RenameFile(StagingRoot, RuntimeRoot) then
        RaiseException('Could not place the runtime in the local tracker folder.');
    except
      RuntimeWarning := 'The tracker will finish installing, but its local .NET runtime is unavailable: ' +
        GetExceptionMessage + #13#10 + 'Rerun setup while connected to the internet to try again.';
      DelTree(StagingRoot, True, True, True);
    end;
  finally
    DownloadPage.Hide;
  end;

end;

function RuntimeAvailable: Boolean;
begin
  Result := HasDesktopRuntime or HasLocalDesktopRuntime;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and (RuntimeWarning <> '') then
    MsgBox('Setup completed. The .NET 9 Desktop Runtime is not installed yet, so the tracker cannot start.' +
      #13#10 + 'Rerun this setup when the download is available.', mbInformation, MB_OK);
end;
