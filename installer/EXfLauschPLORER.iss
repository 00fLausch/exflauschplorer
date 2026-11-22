#define MyAppName "EXfLauschPLORER"
#define MyAppVersion "0.1.0"
[Setup]
#ifdef TESTBUILD
AppId={{D26F30A8-6988-4D15-A8FA-A0E4A7B22E11}
#else
AppId={{4E633C77-5881-4CDB-969F-1B6A60C71381}
#endif
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=EXfLauschPLORER
DefaultDirName={localappdata}\Programs\WindowsMobileExplorer
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=EXfLauschPLORER-Setup-0.1.0-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableFinishedPage=yes
UninstallDisplayName=EXfLauschPLORER

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{tmp}\EXfLauschPLORER\publish\win-x64"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\setup.ps1"; DestDir: "{tmp}\EXfLauschPLORER"; Flags: ignoreversion
Source: "Einrichtung.ps1"; DestDir: "{tmp}\EXfLauschPLORER"; Flags: ignoreversion
Source: "..\setup.ps1"; DestDir: "{app}\setup-tools"; Flags: ignoreversion
Source: "Einrichtung.ps1"; DestDir: "{app}\setup-tools"; Flags: ignoreversion
Source: "Entfernen.ps1"; DestDir: "{app}\setup-tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\EXfLauschPLORER\Einrichtung und Optionen"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\setup-tools\Einrichtung.ps1"" -ConfigureOnly"; WorkingDir: "{app}\setup-tools"
Name: "{autoprograms}\EXfLauschPLORER\Deinstallieren"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{tmp}\EXfLauschPLORER\Einrichtung.ps1"" -SetupDirectory ""{tmp}\EXfLauschPLORER"""; Flags: waituntilterminated; Check: not WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeUninstall(): Boolean;
var ResultCode: Integer;
begin
#ifdef TESTBUILD
  Result := True;
#else
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ExpandConstant('{app}\setup-tools\Entfernen.ps1') + '" -InstallRoot "' + ExpandConstant('{app}') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
#endif
end;
