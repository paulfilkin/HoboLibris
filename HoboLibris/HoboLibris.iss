; HoboLibris installer (Inno Setup 6).
; Build the program first:
;   dotnet publish HoboLibris\HoboLibris.csproj -c Release -r win-x64 --self-contained
; then compile this script:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" HoboLibris.iss
; The installer is written to ..\Releases\.

#define AppName "HoboLibris"
#define AppVersion "1.0.0"
#define AppExe "HoboLibris.exe"
#define AppPublisher "multifarious"
#define AppURL "https://multifarious.filkin.com/"
; What the app was called before; an earlier installation is replaced.
#define OldName1 "KoboLibris"
#define OldName2 "Book Backup"

[Setup]
; Same id as the earlier "Book Backup" and "KoboLibris" installers and the first HoboLibris
; build, so this one replaces them.
AppId={{6F0C2A4E-3B7D-4C1A-9E55-8B2D7A1C4F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}support
AppUpdatesURL={#AppURL}
; Installed for the current user by default, so no administrator password is needed;
; the first page lets the person install for everyone instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=HoboLibris\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\Releases
OutputBaseFilename={#AppName}Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Leftovers of the app under its old names.
Type: files; Name: "{autoprograms}\{#OldName1}.lnk"
Type: files; Name: "{autodesktop}\{#OldName1}.lnk"
Type: filesandordirs; Name: "{autopf}\{#OldName1}"
Type: files; Name: "{autoprograms}\{#OldName2}.lnk"
Type: files; Name: "{autodesktop}\{#OldName2}.lnk"
Type: filesandordirs; Name: "{autopf}\{#OldName2}"
; The earlier single-file build of HoboLibris used a lower-case file name.
Type: files; Name: "{app}\hobolibris.exe"

[Files]
; Everything from the self-contained publish folder.
Source: "HoboLibris\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Your settings, sign-in and the eReader book-list backups (in %APPDATA%\HoboLibris) and your
; backed-up books are kept when HoboLibris is uninstalled.
