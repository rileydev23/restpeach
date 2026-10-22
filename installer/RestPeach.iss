; Windows installer: RestPeach-Setup.exe (Inno Setup 6). Per user, no administrator: installs the same portable exe
; into %LOCALAPPDATA%\Programs\RestPeach, adds it to the Start menu (so Windows search finds it) and to
; Settings > Apps with its uninstaller. Settings in %APPDATA%\peachapps\RestPeach are kept on uninstall.
; Build after build.cmd:  iscc /DAppVersion=1.2.3 installer\RestPeach.iss   ->  dist\RestPeach-Setup.exe
; The app updates itself by running a newer setup with /SILENT; installed.txt tells it that it was installed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{2FFC1B1E-74A7-4CEA-B522-9365FCAE8955}
AppName=RestPeach
AppVersion={#AppVersion}
AppVerName=RestPeach {#AppVersion}
AppPublisher=RestPeach
AppPublisherURL=https://github.com/rileydev23/restpeach
AppSupportURL=https://github.com/rileydev23/restpeach/issues
AppUpdatesURL=https://github.com/rileydev23/restpeach/releases
DefaultDirName={localappdata}\Programs\RestPeach
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=RestPeach-Setup
SetupIconFile=..\src\RestPeach\Assets\app.ico
UninstallDisplayIcon={app}\RestPeach.exe
UninstallDisplayName=RestPeach
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running RestPeach before replacing it (the app also exits by itself when it updates).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\RestPeach.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installed.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\RestPeach"; Filename: "{app}\RestPeach.exe"
Name: "{userdesktop}\RestPeach"; Filename: "{app}\RestPeach.exe"; Tasks: desktopicon

[Run]
; Interactive install: a "Launch RestPeach" checkbox. Silent install (an update from the app): reopen it right away.
Filename: "{app}\RestPeach.exe"; Description: "{cm:LaunchProgram,RestPeach}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\RestPeach.exe"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; Left behind by an update that replaced the exe in place.
Type: files; Name: "{app}\RestPeach.exe.old"
Type: files; Name: "{app}\RestPeach.exe.new"
Type: dirifempty; Name: "{app}"
