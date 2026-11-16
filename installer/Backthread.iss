#ifndef PublishDir
  #error Pass /DPublishDir with the absolute path to the self-contained win-x64 publish directory.
#endif
#ifndef BuildVersion
  #define BuildVersion "1.0.0"
#endif

[Setup]
AppId={{2D042D09-27C6-4037-80EE-B3FB75E645A3}
AppName=回线 Backthread
AppVersion={#BuildVersion}
AppPublisher=Backthread
VersionInfoVersion={#BuildVersion}
VersionInfoDescription=回线 Backthread 安装程序
DefaultDirName={localappdata}\Programs\Backthread
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
UninstallDisplayIcon={app}\Backthread.exe
SetupIconFile={#PublishDir}\Assets\Backthread.ico
SetupMutex=Local\Backthread.Setup.Gate
CloseApplications=yes
CloseApplicationsFilter=Backthread.exe
RestartApplications=no
RestartIfNeededByRun=no
OutputBaseFilename=Backthread-{#BuildVersion}-win-x64-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "zhcn"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[CustomMessages]
english.RunBackthread=Launch Backthread
english.CloseBackthread=Backthread is running. Close its main window, then click Retry to continue uninstalling.
zhcn.RunBackthread=启动回线
zhcn.CloseBackthread=回线仍在运行。请关闭回线主窗口，再点击「重试」继续卸载。

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\回线 Backthread"; Filename: "{app}\Backthread.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\Backthread.exe"; WorkingDir: "{app}"; Description: "{cm:RunBackthread}"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser

[Code]
function InitializeUninstall(): Boolean;
begin
  { Restart Manager handles setup/upgrade, but Inno does not use it for uninstall. }
  Result := False;
  while CheckForMutexes('Local\Backthread.Relaunch.Gate') do
  begin
    if SuppressibleMsgBox(CustomMessage('CloseBackthread'), mbConfirmation, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
      Exit;
  end;
  Result := True;
end;
