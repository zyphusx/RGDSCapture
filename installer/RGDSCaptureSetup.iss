; RGDSCapture Inno Setup script
;
; Builds RGDSCaptureSetup.exe from the published output of the app.
;
; Build steps:
;   1. dotnet publish -c Release -r win-x64 --self-contained false -o bin\publish
;   2. "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\RGDSCaptureSetup.iss
;      (or open this file in the Inno Setup Compiler and press Build)
;
; Output lands in installer\Output\RGDSCaptureSetup.exe
;
; Override the version at compile time without editing this file:
;   ISCC installer\RGDSCaptureSetup.iss /DMyAppVersion=3.2.0

#ifndef MyAppVersion
  #define MyAppVersion "3.2.0"
#endif

#define MyAppName "RGDSCapture"
#define MyAppPublisher "zyphusx"
#define MyAppURL "https://github.com/zyphusx/RGDSCapture"
#define MyAppExeName "RGDSCapture.exe"
#define PublishDir "..\bin\publish"

[Setup]
; Fixed AppId (GUID) — keep this stable across releases so upgrades install
; over the previous version instead of side-by-side.
AppId={{8F3B2C1E-6B7A-4E9C-9D2A-4F6E7C1A9B20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=RGDSCaptureSetup
SetupIconFile=..\Resources\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableWelcomePage=no
; App uses SSH.NET/DPAPI-encrypted credentials and writes recordings under
; the user profile — no admin rights needed to run or install per-user.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything from the publish output (exe, managed DLLs, FFmpeg native DLLs
; and executables, Themes\*.xaml, README/LICENSE/THIRDPARTYLICENSES).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Recordings/screenshots/settings live under the user profile (see AppPaths),
; not {app}, so a normal uninstall only needs to remove the install folder
; itself — nothing extra to clean up here today.
