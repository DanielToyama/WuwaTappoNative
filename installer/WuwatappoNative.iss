#define MyAppName "Wuwatappo Native"
#ifndef MyVersion
  #define MyVersion "0.1.13"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\publish\app-" + MyVersion
#endif

[Setup]
AppId={{A7C3F6D2-5B1E-4E8A-9C2D-8F4B6A1D3E57}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppPublisher=Wuwatappo Native
DefaultDirName={localappdata}\Programs\Wuwatappo Native
UsePreviousAppDir=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
; 默认管理员模式；安装模式对话框可选择“为所有用户安装（推荐）”或“仅当前用户”标准权限安装。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; 旧版本是以 lowest 安装的；不能沿用旧安装模式，否则 admin 默认值不会生效。
UsePreviousPrivileges=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=WuwatappoNative-{#MyVersion}-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\WuwatappoNative.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
CloseApplicationsFilter=WuwatappoNative.exe
RestartApplications=no
VersionInfoVersion={#MyVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyVersion}
VersionInfoDescription={#MyAppName} 安装包

[Files]
Source: "{#MySourceDir}\WuwatappoNative.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\WuwatappoNative.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\WuwatappoNative.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\WuwatappoNative.exe"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""WuwatappoNative"" /F"; Flags: runhidden; RunOnceId: "DeleteAutoStartTask"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v WuwatappoNative /f"; Flags: runhidden; RunOnceId: "DeleteRunValue"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if CheckForMutexes('Local\WuwatappoNative.SingleInstance.v1') then
  begin
    if MsgBox('检测到 Wuwatappo Native 正在运行。安装前需要关闭它，是否现在关闭并继续？', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
    if (not Exec('taskkill.exe', '/IM WuwatappoNative.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    begin
      if IsAdmin then
      begin
        MsgBox('无法自动关闭 Wuwatappo Native，可能它正以管理员权限运行。请先从托盘退出程序，再重新运行安装包。', mbError, MB_OK);
        Result := False;
      end
      else if MsgBox('当前为标准权限，无法自动结束管理员模式运行的桌宠。请先手动退出桌宠后继续安装；是否仍继续？', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;
