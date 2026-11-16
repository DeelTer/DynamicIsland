#define AppName "Island"
#define AppVersion "1.0.0"
#ifndef AppSource
  #define AppSource SourcePath + "\..\release\Island"
#endif
#ifndef WebView2Bootstrapper
  #define WebView2Bootstrapper SourcePath + "\..\assets\MicrosoftEdgeWebView2Setup.exe"
#endif

[Setup]
AppId={{50A627B5-07E0-4BA4-AD00-E4BCB04C6E61}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Island
DefaultDirName={localappdata}\Programs\Island
DefaultGroupName=Island
UninstallDisplayName=Island
UninstallDisplayIcon={app}\Island.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#SourcePath}\..\release
OutputBaseFilename=Island-Setup
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked
Name: "autostart"; Description: "Запускать вместе с Windows"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsWebView2Installed

[Icons]
Name: "{group}\Island"; Filename: "{app}\Island.exe"
Name: "{autodesktop}\Island"; Filename: "{app}\Island.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Island"; ValueData: """{app}\Island.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Подготовка компонента Microsoft Edge WebView2..."; Flags: waituntilterminated runhidden; Check: not IsWebView2Installed
Filename: "{app}\Island.exe"; Description: "Запустить Island"; Flags: postinstall nowait skipifsilent

[Code]
function IsWebView2Installed: Boolean;
var
  Version: string;
begin
  Result := False;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    if (Version <> '') and (Version <> '0.0.0.0') then begin Result := True; Exit; end;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    if (Version <> '') and (Version <> '0.0.0.0') then begin Result := True; Exit; end;
end;
