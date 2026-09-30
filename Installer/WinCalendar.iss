#define AppName "WinCalendar"
#define AppVersion "1.0.0.6"
#define PublishDir "..\bin\Release\net8.0-windows10.0.22621.0\win-x64\publish"

[Setup]
AppId={{B3520AF1-EFDD-4BAA-9EF6-41D783CBCB83}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\InstallerOutput
OutputBaseFilename=WinCalendar-Installer
SetupIconFile=..\Assets\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\WinCalendar.exe
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\WinCalendar.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WinCalendar.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Run]
Filename: "{app}\WinCalendar.exe"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM WinCalendar.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
