; ============================================================================
; 芯跳 CoreBeat —— Inno Setup 安装脚本
; 前置：先运行 tools\publish.ps1 生成最新绿色版到 dist\CoreBeat
; 编译：ISCC.exe tools\installer.iss   （Inno Setup 6，%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe）
; 产出：dist\CoreBeat-Setup-x64.exe （可选安装目录 + 开始菜单/桌面快捷方式 + 卸载）
; 说明：WebView2 Evergreen 运行时由系统提供，不打包；未安装时会先弹官网提示。
; ============================================================================

#define MyAppName "芯跳 CoreBeat"
; 版本号可由 CI 通过 /DMyAppVer=0.6.1 覆盖；本地缺省用 0.6.1
#ifndef MyAppVer
  #define MyAppVer "0.6.1"
#endif
#define MyAppPublisher "CoreBeat"
#define MyAppExe "CoreBeat.exe"

[Setup]
AppId={{2E3B0A6E-9F64-4D66-8A6B-CoreBeat000001}
AppName={#MyAppName}
AppVersion={#MyAppVer}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CoreBeat
DefaultGroupName=芯跳 CoreBeat
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=..\dist
OutputBaseFilename=CoreBeat-Setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\src\CoreBeat\Assets\CoreBeat.ico
UninstallDisplayName={#MyAppName}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "..\dist\CoreBeat\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
