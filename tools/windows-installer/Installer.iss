[Setup]
AppId={{CBDA5672-56E1-492F-A1D2-86B2BA1C50C4}
AppName=FerrumPix Dev
AppVersion={#BuildVersion}
AppPublisher=FerrumPix local build
DefaultDirName={localappdata}\Programs\FerrumPix Dev
DefaultGroupName=FerrumPix Dev
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\FerrumPix_Icon.ico
SetupIconFile={#RepoRoot}\FerrumPix_Icon.ico
OutputDir={#RepoRoot}\artifacts\installer
OutputBaseFilename=FerrumPix-Dev-Setup
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FerrumPix Dev"; Filename: "{app}\FerrumPix.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FerrumPix_Icon.ico"; AppUserModelID: "FerrumPix.Dev"
Name: "{userdesktop}\FerrumPix Dev"; Filename: "{app}\FerrumPix.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FerrumPix_Icon.ico"; AppUserModelID: "FerrumPix.Dev"

[Registry]
Root: HKCU; Subkey: "Software\Classes\Applications\FerrumPix.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "FerrumPix Dev"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\FerrumPix.exe\DefaultIcon"; ValueType: string; ValueData: """{app}\FerrumPix_Icon.ico"",0"
Root: HKCU; Subkey: "Software\Classes\Applications\FerrumPix.exe\shell\open\command"; ValueType: string; ValueData: """{app}\FerrumPix.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\FerrumPixDev.Media"; ValueType: string; ValueData: "FerrumPix Dev Media"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\FerrumPixDev.Media"; ValueType: string; ValueName: "AppUserModelID"; ValueData: "FerrumPix.Dev"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.png\OpenWithProgids"; ValueType: string; ValueName: "FerrumPixDev.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\FerrumPixDev.Media\Application"; ValueType: string; ValueName: "ApplicationName"; ValueData: "FerrumPix Dev"
Root: HKCU; Subkey: "Software\Classes\FerrumPixDev.Media\Application"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\FerrumPix_Icon.ico"",0"
Root: HKCU; Subkey: "Software\Classes\FerrumPixDev.Media\DefaultIcon"; ValueType: string; ValueData: """{app}\FerrumPix_Icon.ico"",0"
Root: HKCU; Subkey: "Software\Classes\FerrumPixDev.Media\shell\open\command"; ValueType: string; ValueData: """{app}\FerrumPix.exe"" ""%1"""
Root: HKCU; Subkey: "Software\FerrumPixDev\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "FerrumPix Dev"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\FerrumPixDev\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "FerrumPix image and video viewer"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "FerrumPix Dev"; ValueData: "Software\FerrumPixDev\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\FerrumPix.exe"; ValueType: string; ValueData: "{app}\FerrumPix.exe"; Flags: uninsdeletekey
#include "Extensions.iss"
