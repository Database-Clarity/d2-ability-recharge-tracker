#ifndef AppVersion
  #error AppVersion must be supplied by the release build
#endif

[Setup]
AppId={{83D77947-D57A-42D8-A35C-6C8868FC3C01}
AppName=Destiny 2 Ability Recharge Tracker
AppVersion={#AppVersion}
DefaultDirName={autopf}\D2AbilityTracker
DefaultGroupName=Destiny 2 Ability Recharge Tracker
OutputDir={#OutputPath}
OutputBaseFilename=D2AbilityTracker-v{#AppVersion}-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\D2AbilityTracker.exe
CloseApplications=yes

[Files]
Source: "{#PublishPath}\D2AbilityTracker.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Destiny 2 Ability Recharge Tracker"; Filename: "{app}\D2AbilityTracker.exe"

[Run]
Filename: "{app}\D2AbilityTracker.exe"; Description: "Launch the ability tracker"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  DownloadPage: TDownloadWizardPage;

function HasDesktopRuntime: Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
      if (Pos('9.0.', Versions[I]) = 1) and (Pos('-', Versions[I]) = 0) then
      begin
        Result := True;
        Exit;
      end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Installing required .NET Desktop Runtime',
    'The runtime is downloaded from Microsoft only when missing.', nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasDesktopRuntime then
    Exit;

  if MsgBox('This app needs the Microsoft .NET 9 Desktop Runtime (x64).' + #13#10 +
    'Download and install it now? It will be shared by future tracker updates.',
    mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := 'The required runtime was not installed. Setup cannot continue.';
    Exit;
  end;

  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'desktop-runtime.exe', '{#RuntimeSHA256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if not Exec(ExpandConstant('{tmp}\desktop-runtime.exe'), '/passive /norestart',
        '', SW_SHOW, ewWaitUntilTerminated, ExitCode) then
        RaiseException('Could not start the Microsoft runtime installer.');
      if (ExitCode <> 0) and (ExitCode <> 3010) and (ExitCode <> 1641) then
        RaiseException(Format('Microsoft runtime installation failed (exit code %d).', [ExitCode]));
      NeedsRestart := (ExitCode = 3010) or (ExitCode = 1641);
      if not HasDesktopRuntime then
        RaiseException('The .NET Desktop Runtime is still unavailable after installation. Restart Windows if requested and retry setup.');
    except
      Result := GetExceptionMessage;
    end;
  finally
    DownloadPage.Hide;
  end;
end;
