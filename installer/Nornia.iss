#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
#ifndef AppRid
  #define AppRid "win-x64"
#endif
#ifndef AppArchitecture
  #define AppArchitecture "x64compatible"
#endif
#ifndef SourceDir
  #error SourceDir must point to the dotnet publish output.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef IconPath
  #error IconPath must point to the Nornia application icon.
#endif

[Setup]
AppId={{B9B0D0C7-8A9B-4F10-ABDE-619215C91031}
AppName=Nornia
AppVersion={#AppVersion}
AppVerName=Nornia {#AppVersion}
AppPublisher=Nornia
DefaultDirName={autopf}\Nornia
DefaultGroupName=Nornia
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#AppArchitecture}
ArchitecturesInstallIn64BitMode={#AppArchitecture}
OutputDir={#OutputDir}
OutputBaseFilename=Nornia-{#AppVersion}-{#AppRid}-Setup
SetupIconFile={#IconPath}
UninstallDisplayIcon={app}\Nornia.Desktop.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Nornia"; Filename: "{app}\Nornia.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Nornia"; Filename: "{app}\Nornia.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; 在 Windows 资源管理器的文件夹/文件右键菜单注册"Nornia"项(当前用户,PrivilegesRequired=lowest
; 时写 HKCU\Software\Classes,卸载时整体删除)。Directory 作用于文件夹;通配符 "*" 作用于所有文件。
[Registry]
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Nornia"; ValueType: string; ValueName: ""; ValueData: "使用 Nornia 打开"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Nornia"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Nornia.Desktop.exe,0"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Nornia"; ValueType: string; ValueName: "OnlyInContextMenu"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Nornia\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Nornia.Desktop.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\*\shell\Nornia"; ValueType: string; ValueName: ""; ValueData: "使用 Nornia 打开"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Nornia"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Nornia.Desktop.exe,0"
Root: HKCU; Subkey: "Software\Classes\*\shell\Nornia"; ValueType: string; ValueName: "OnlyInContextMenu"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\*\shell\Nornia\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Nornia.Desktop.exe"" ""%1"""

[Run]
Filename: "{app}\Nornia.Desktop.exe"; Description: "启动 Nornia"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
