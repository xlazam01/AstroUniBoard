; Inno Setup script for the AstroUniBoard ASCOM driver and the control & debug app.
; Build:  ISCC.exe AstroUniBoard.iss  [/DAppVersion=0.1.0]
;   after: dotnet build ..\AstroUniBoard.Ascom -c Release  and  dotnet build ..\AstroUniBoard.DebugApp -c Release
; The installer needs administrator rights (it registers a COM server for all users).

#define AppName "AstroUniBoard ASCOM Driver"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define ServerExe "AstroUniBoard.Ascom.Server.exe"
#define DebugExe "AstroUniBoard.DebugApp.exe"
#define ServerDir "..\AstroUniBoard.Ascom\bin\Release\net48"
#define DebugDir "..\AstroUniBoard.DebugApp\bin\Release\net48"

[Setup]
; Fixed GUID: identifies the application for upgrades and uninstall. Do not change.
AppId={{D8146B52-12B7-42E6-A036-5C759D254ECD}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MartinsOpenAstroTools
AppPublisherURL=https://github.com/xlazam01/AstroUniBoard
AppSupportURL=https://github.com/xlazam01/AstroUniBoard/issues
; The server is a 32-bit program (like most ASCOM local servers); it serves 32 and 64 bit clients.
DefaultDirName={commonpf32}\AstroUniBoard
DefaultGroupName=AstroUniBoard
PrivilegesRequired=admin
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=AstroUniBoard-ASCOM-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\..\..\LICENSE
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#ServerExe}
; a running driver (a client such as NINA is connected) is closed or a restart is offered
CloseApplications=yes

[Files]
Source: "{#ServerDir}\{#ServerExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ServerDir}\{#ServerExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ServerDir}\AstroUniBoard.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
; the setup dialog's "debug app" button looks for the app next to the server
Source: "{#DebugDir}\{#DebugExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DebugDir}\{#DebugExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Starting the server without arguments opens the setup dialog
Name: "{group}\AstroUniBoard setup"; Filename: "{app}\{#ServerExe}"
Name: "{group}\AstroUniBoard control & debug"; Filename: "{app}\{#DebugExe}"
Name: "{group}\Uninstall AstroUniBoard"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#ServerExe}"; Parameters: "/regserver /silent"; StatusMsg: "Registering the ASCOM driver..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{app}\{#ServerExe}"; Parameters: "/unregserver /silent"; RunOnceId: "UnregisterAstroUniBoard"; Flags: runhidden waituntilterminated

[Code]
// The drivers implement IFocuserV4, IRotatorV4 and IObservingConditionsV2, which need ASCOM Platform 7 or newer.
function InitializeSetup(): Boolean;
var
  V: String;
  Major: Integer;
begin
  Result := True;
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\ASCOM\Platform', 'Platform Version', V) then
    V := '';
  if V = '' then V := 'none found';
  Major := StrToIntDef(Copy(V, 1, Pos('.', V) - 1), 0);
  if Major < 7 then
  begin
    MsgBox('The AstroUniBoard driver needs ASCOM Platform 7 or newer.' + #13#10 +
           'Installed: ' + V + #13#10#13#10 +
           'Please install it from https://ascom-standards.org and run this setup again.',
           mbError, MB_OK);
    Result := False;
  end;
end;
