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

[Registry]
; Автозапуск через HKCU\Run — ТОТ ЖE механизм, которым управляет переключатель в трее
; (StartupService), чтобы UI всегда показывал корректное состояние. Удаляется при деинсталляции.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DesktopOrganizer"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RestoreHadErrors: Boolean;

// usUninstall вызывается ДО удаления файлов: exe и БД ещё на месте — останавливаем
// приложение и возвращаем скрытые ярлыки на рабочий стол, фиксируя результат.
// usPostUninstall — предлагаем удалить данные ТОЛЬКО если восстановление прошло без ошибок
// (иначе можно потерять recovery-список оставшихся скрытых файлов).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  DataDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#MyAppExeName} /F', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RestoreHadErrors := True;
    if Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--restore-hidden', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RestoreHadErrors := (ResultCode <> 0);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\DesktopOrganizer');
    if DirExists(DataDir) then
    begin
      if RestoreHadErrors then
        MsgBox('Некоторые ярлыки не удалось вернуть на рабочий стол.' + #13#10 +
               'Папка данных оставлена, чтобы не потерять список для восстановления.' + #13#10 +
               'Снимите атрибут «скрытый» с нужных файлов вручную или переустановите программу.',
               mbError, MB_OK)
      else if MsgBox('Удалить также настройки и базу данных Desktop Organizer?',
                     mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
