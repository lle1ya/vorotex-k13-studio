; VOROTEX K13 Studio — installer (Inno Setup 6). Built on Linux under Wine: see installer/README.md.
; Per-user install, no admin rights. Profiles and backups (%APPDATA%, %LOCALAPPDATA%\VorotexK13Studio) are never touched.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppTag
  #define AppTag "v" + AppVersion
#endif
#ifndef SourceExe
  #define SourceExe "..\out\publish\VorotexK13Studio.exe"
#endif

[Setup]
AppId={{6E1F3A7B-0C5D-4B8E-9A21-3D5C0B13A7F1}
AppName=VOROTEX K13 Studio
AppVersion={#AppVersion}
AppVerName=VOROTEX K13 Studio {#AppTag}
AppPublisher=lle1ya
AppPublisherURL=https://github.com/lle1ya/vorotex-k13-studio
AppSupportURL=https://github.com/lle1ya/vorotex-k13-studio/issues
AppUpdatesURL=https://github.com/lle1ya/vorotex-k13-studio/releases
DefaultDirName={localappdata}\Programs\VorotexK13Studio
DefaultGroupName=VOROTEX K13 Studio
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=VorotexK13Studio-{#AppTag}-setup
SetupIconFile=..\src\K13.Studio\Assets\app.ico
UninstallDisplayIcon={app}\VorotexK13Studio.exe
UninstallDisplayName=VOROTEX K13 Studio
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\VorotexK13Studio.Single
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=VOROTEX K13 Studio

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "VorotexK13Studio.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\VOROTEX K13 Studio"; Filename: "{app}\VorotexK13Studio.exe"
Name: "{autodesktop}\VOROTEX K13 Studio"; Filename: "{app}\VorotexK13Studio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VorotexK13Studio.exe"; Description: "Запустить VOROTEX K13 Studio"; Flags: nowait postinstall skipifsilent

[Code]
{ Autostart («Запускать вместе с Windows») is removed on uninstall only if it points to this installation. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var v: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VorotexK13Studio', v) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(v)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VorotexK13Studio');
end;
