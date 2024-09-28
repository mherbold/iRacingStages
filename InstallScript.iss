[Setup]
AppName=iRacingStages
AppVersion=1.3
AppCopyright=Created by Marvin Herbold
AppPublisher=Marvin Herbold
AppPublisherURL=https://herboldracing.com/iracing-tv
WizardStyle=modern
DefaultDirName={autopf}\iRacingStages
DefaultGroupName=iRacingStages
UninstallDisplayIcon={app}\iRacingStages.exe
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=iRacingStages
OutputDir=userdocs:iRacingStages
PrivilegesRequired=lowest
SetupIconFile=""

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}";

[Files]
Source: "C:\Users\marvi\Documents\GitHub\iRacingStages\bin\Release\net8.0-windows\publish\win-x86\*"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{userdocs}\iRacingStages"

[Icons]
Name: "{group}\iRacingStages"; Filename: "{app}\iRacingStages.exe"
Name: "{userdesktop}\iRacingStages"; Filename: "{app}\iRacingStages.exe"; Tasks: desktopicon
