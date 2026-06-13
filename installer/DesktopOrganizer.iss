; Установщик Desktop Organizer (раздел 13 ТЗ).
; Сборка: сначала publish, затем компиляция этого скрипта в Inno Setup 6.
;   dotnet publish src\DesktopOrganizer\DesktopOrganizer.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
;   iscc installer\DesktopOrganizer.iss
; Поддерживается тихая установка: DesktopOrganizerSetup.exe /VERYSILENT /NORESTART

#define MyAppName "Desktop Organizer"
#define MyAppVersion "0.1.0"
#define MyAppExeName "DesktopOrganizer.exe"

[Setup]
AppId={{7B7E2C51-8A1F-4D14-9C45-DESKTOPORG01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
; Установка без прав администратора в профиль пользователя (критерий 18.2)
PrivilegesRequired=lowest
DefaultDirName={localappdata}\DesktopOrganizer\App
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DesktopOrganizerSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ShowLanguageDialog=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать вместе с Windows"; GroupDescription: "Дополнительно:"

[Files]
Source: "..\src\DesktopOrganizer\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Останавливаем приложение перед удалением
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillApp"
; Возвращаем все скрытые ярлыки на рабочий стол, чтобы после удаления программы
; пользователь не потерял к ним доступ.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-hidden"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreHidden"

[Code]
// При полном удалении спрашиваем, удалять ли пользовательские данные (настройки/БД сохраняются по умолчанию — раздел 13 ТЗ).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\DesktopOrganizer');
    if DirExists(DataDir) then
      if MsgBox('Удалить также настройки и базу данных Desktop Organizer?', mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
