; Red Tune installer (Inno Setup 6). build.ps1 runs it after the program is built:
;   ISCC.exe /DAppVersion=0.4.0 installer\RedTune.iss   →   dist\RedTune-Setup-0.4.0.exe
; Installs bin\ into Program Files, a Start menu shortcut, the logon task (the same one Settings creates, through
; RedTune.exe --autostart on), and installs the PawnIO driver with its own signed installer when it is missing (a task,
; on by default; 21.11): the curve, the AMD limits and the processor temperature need it.
; The uninstaller removes the task and the files and asks whether to keep the settings in %APPDATA%\RedTune.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F3B2D51-8C4E-4A7B-9E2F-1D5C7A9B3E40}
AppName=Red Tune
AppVersion={#AppVersion}
AppVerName=Red Tune {#AppVersion}
AppPublisher=Red Tune
DefaultDirName={autopf}\Red Tune
DefaultGroupName=Red Tune
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=RedTune-Setup-{#AppVersion}
SetupIconFile=..\redtune.ico
UninstallDisplayIcon={app}\RedTune.exe
UninstallDisplayName=Red Tune
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutostartTask=Запускать Red Tune при входе в Windows (свёрнутым в трей)
en.AutostartTask=Start Red Tune at sign-in (minimized to the tray)
ru.DesktopTask=Ярлык на рабочем столе
en.DesktopTask=Desktop shortcut
ru.RunNow=Запустить Red Tune
en.RunNow=Start Red Tune
ru.NoPawnIO=Драйвер PawnIO не установился.%n%nБез него не работают «Кривая» и лимиты процессора AMD; остальное работает. PawnIO можно поставить позже: «Настройки → Компоненты».%n%nОткрыть страницу загрузки PawnIO?
en.NoPawnIO=The PawnIO driver was not installed.%n%nWithout it «Curve» and the AMD processor limits do not work; everything else does. PawnIO can be installed later: «Settings → Components».%n%nOpen the PawnIO download page?
ru.PawnIOTask=Установить драйвер PawnIO (кривая, лимиты и температура процессора AMD)
en.PawnIOTask=Install the PawnIO driver (the AMD processor's curve, limits and temperature)
ru.PawnIOInstalling=Устанавливаю драйвер PawnIO…
en.PawnIOInstalling=Installing the PawnIO driver…
ru.KeepData=Удалить и настройки Red Tune (пресеты, кривые, настройки звука, правила «Распределения», журнал) из %APPDATA%\RedTune?%n%n«Нет» — оставить их для следующей установки.
en.KeepData=Also delete the Red Tune settings (presets, curves, sound settings, «Distribution» rules, log) in %APPDATA%\RedTune?%n%n«No» keeps them for a later install.

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"
Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked
Name: "pawnio"; Description: "{cm:PawnIOTask}"; Check: not PawnIOInstalled

[Files]
; the program's files by name: bin\ is a working folder and may hold other things (old builds, logs), none of which ships
Source: "..\bin\RedTune.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\RedTuneStress.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\audio\RedTuneApo.dll"; DestDir: "{app}\audio"; Flags: ignoreversion
Source: "..\bin\ram\RyzenSMU.bin"; DestDir: "{app}\ram"; Flags: ignoreversion
Source: "..\bin\ram\AMDFamily17.bin"; DestDir: "{app}\ram"; Flags: ignoreversion
Source: "..\bin\ram\SmbusPIIX4.bin"; DestDir: "{app}\ram"; Flags: ignoreversion
Source: "..\bin\pawnio\PawnIO_setup.exe"; DestDir: "{app}\pawnio"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Red Tune"; Filename: "{app}\RedTune.exe"
Name: "{autodesktop}\Red Tune"; Filename: "{app}\RedTune.exe"; Tasks: desktopicon

[Run]
; the driver first, so the program started at the end finds it (its setup refuses to run over an installed PawnIO)
Filename: "{app}\pawnio\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "{cm:PawnIOInstalling}"; Flags: runhidden waituntilterminated; Tasks: pawnio; Check: not PawnIOInstalled
Filename: "{app}\RedTune.exe"; Parameters: "--autostart on"; Flags: runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\RedTune.exe"; Parameters: "--autostart off"; Flags: runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\RedTune.exe"; Description: "{cm:RunNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\RedTune.exe"; Parameters: "--autostart off"; RunOnceId: "RemoveTask"; Flags: runhidden waituntilterminated
; the sound effect off every device, its registration and its files (Windows Audio restarts if it was in use)
Filename: "{app}\RedTune.exe"; Parameters: "--audio-uninstall"; RunOnceId: "RemoveAudio"; Flags: runhidden waituntilterminated

[UninstallDelete]
; anything still left in the program folder (the settings in %APPDATA% are asked about separately)
Type: filesandordirs; Name: "{app}"

[Code]
const
  PawnIOKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  PawnIOPage = 'https://pawnio.eu/';

function PawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, PawnIOKey) or RegKeyExists(HKLM32, PawnIOKey);
end;

// a running copy is asked to close, then waited for (up to 10 s) until its single-instance mutex is gone: --quit only
// asks, and on 13.11 the uninstaller deleted files while the app was still exiting and left RedTune.exe behind
procedure QuitApp;
var
  Code, i: Integer;
begin
  if not FileExists(ExpandConstant('{app}\RedTune.exe')) then Exit;
  Exec(ExpandConstant('{app}\RedTune.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  i := 0;
  while CheckForMutexes('Local\RedTune.App') and (i < 50) do
  begin
    Sleep(200);
    i := i + 1;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitApp;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep <> ssPostInstall) or WizardSilent then Exit;
  // the driver's task was picked and it is still missing: its setup failed (an unpicked task is the user's choice)
  if WizardIsTaskSelected('pawnio') and not PawnIOInstalled then
    if MsgBox(CustomMessage('NoPawnIO'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', PawnIOPage, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then QuitApp; // before any file goes
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox(CustomMessage('KeepData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\RedTune'), True, True, True);
end;
