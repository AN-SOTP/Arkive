#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PayloadDirectory
  #error PayloadDirectory must be supplied by Build-Installer.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied by Build-Installer.ps1
#endif

[Setup]
AppId={{5E7960AB-8D82-46F8-8BDD-4AC968058478}
AppName=Arkive
AppVersion={#AppVersion}
AppPublisher=Arkive contributors
AppPublisherURL=https://github.com/AN-SOTP/Arkive
DefaultDirName={localappdata}\Programs\Arkive
DefaultGroupName=Arkive
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=Arkive-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\Arkive.App\Assets\Arkive.ico
UninstallDisplayIcon={app}\Arkive.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
AppMutex=Arkive.Application.Update
LicenseFile=..\LICENSE.txt

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "fileassociation"; Description: "Add Arkive to Open with for .arkive mod files"

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Arkive"; Filename: "{app}\Arkive.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Arkive"; Filename: "{app}\Arkive.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\Arkive.Mod"; ValueType: string; ValueData: "Arkive mod"; Flags: uninsdeletekey; Tasks: fileassociation
Root: HKCU; Subkey: "Software\Classes\Arkive.Mod\DefaultIcon"; ValueType: string; ValueData: """{app}\Arkive.App.exe"",0"; Tasks: fileassociation
Root: HKCU; Subkey: "Software\Classes\Arkive.Mod\shell\open\command"; ValueType: string; ValueData: """{app}\Arkive.App.exe"" ""%1"""; Tasks: fileassociation
Root: HKCU; Subkey: "Software\Classes\.arkive\OpenWithProgids"; ValueName: "Arkive.Mod"; ValueType: none; Flags: uninsdeletevalue; Tasks: fileassociation

[Run]
Filename: "{app}\Arkive.App.exe"; Description: "Launch Arkive"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := not CheckForMutexes('Arkive.Application.Update');
  if not Result then
    MsgBox('Close Arkive after its current operation finishes, then run this installer again. Your mod data will be kept.', mbInformation, MB_OK);
end;
