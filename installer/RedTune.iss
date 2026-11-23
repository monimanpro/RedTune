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
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "th"; MessagesFile: "compiler:Languages\Thai.isl"
; 23.11: not among the translations Inno Setup ships (or newer than the one on the build machine): kept here, from jrsoftware/issrc
Name: "zh"; MessagesFile: "lang\ChineseSimplified.isl"
Name: "vi"; MessagesFile: "lang\Vietnamese.isl"
Name: "id"; MessagesFile: "lang\Indonesian.isl"

[CustomMessages]
ru.AutostartTask=Запускать Red Tune при входе в Windows (свёрнутым в трей)
en.AutostartTask=Start Red Tune at sign-in (minimized to the tray)
es.AutostartTask=Iniciar Red Tune al iniciar sesión (minimizado en la bandeja)
pt.AutostartTask=Iniciar o Red Tune ao entrar no Windows (minimizado na bandeja)
ko.AutostartTask=Windows 로그인 시 Red Tune 시작 (트레이로 최소화)
th.AutostartTask=เริ่ม Red Tune เมื่อลงชื่อเข้าใช้ Windows (ย่อไว้ในถาด)
zh.AutostartTask=登录 Windows 时启动 Red Tune（最小化到托盘）
vi.AutostartTask=Khởi động Red Tune khi đăng nhập Windows (thu nhỏ vào khay)
id.AutostartTask=Jalankan Red Tune saat masuk Windows (diminimalkan ke baki)
ru.DesktopTask=Ярлык на рабочем столе
en.DesktopTask=Desktop shortcut
es.DesktopTask=Acceso directo en el escritorio
pt.DesktopTask=Atalho na área de trabalho
ko.DesktopTask=바탕 화면 바로 가기
th.DesktopTask=ทางลัดบนเดสก์ท็อป
zh.DesktopTask=桌面快捷方式
vi.DesktopTask=Lối tắt trên màn hình
id.DesktopTask=Pintasan di desktop
ru.RunNow=Запустить Red Tune
en.RunNow=Start Red Tune
es.RunNow=Iniciar Red Tune
pt.RunNow=Iniciar o Red Tune
ko.RunNow=Red Tune 시작
th.RunNow=เริ่ม Red Tune
zh.RunNow=启动 Red Tune
vi.RunNow=Khởi động Red Tune
id.RunNow=Jalankan Red Tune
ru.NoPawnIO=Драйвер PawnIO не установился.%n%nБез него не работают «Кривая» и лимиты процессора AMD; остальное работает. PawnIO можно поставить позже: «Настройки → Компоненты».%n%nОткрыть страницу загрузки PawnIO?
en.NoPawnIO=The PawnIO driver was not installed.%n%nWithout it «Curve» and the AMD processor limits do not work; everything else does. PawnIO can be installed later: «Settings → Components».%n%nOpen the PawnIO download page?
es.NoPawnIO=El controlador PawnIO no se instaló.%n%nSin él no funcionan «Curva» ni los límites del procesador AMD; todo lo demás sí. PawnIO se puede instalar más tarde: «Ajustes → Componentes».%n%n¿Abrir la página de descarga de PawnIO?
pt.NoPawnIO=O driver PawnIO não foi instalado.%n%nSem ele, «Curva» e os limites do processador AMD não funcionam; o resto funciona. O PawnIO pode ser instalado depois: «Configurações → Componentes».%n%nAbrir a página de download do PawnIO?
ko.NoPawnIO=PawnIO 드라이버가 설치되지 않았습니다.%n%n이 드라이버가 없으면 «커브»와 AMD 프로세서 한계가 작동하지 않으며, 나머지는 작동합니다. PawnIO는 나중에 «설정 → 구성 요소»에서 설치할 수 있습니다.%n%nPawnIO 다운로드 페이지를 열까요?
th.NoPawnIO=ไม่ได้ติดตั้งไดรเวอร์ PawnIO%n%nหากไม่มีไดรเวอร์นี้ «เคิร์ฟ» และขีดจำกัดของโปรเซสเซอร์ AMD จะใช้งานไม่ได้ ส่วนอื่นใช้งานได้ตามปกติ ติดตั้ง PawnIO ภายหลังได้ที่ «การตั้งค่า → ส่วนประกอบ»%n%nเปิดหน้าดาวน์โหลด PawnIO หรือไม่?
zh.NoPawnIO=PawnIO 驱动未能安装。%n%n没有它，«曲线»和 AMD 处理器限制将无法使用，其余功能正常。PawnIO 可以稍后在 «设置 → 组件» 中安装。%n%n是否打开 PawnIO 下载页面？
vi.NoPawnIO=Driver PawnIO chưa được cài đặt.%n%nKhông có nó, «Đường cong» và giới hạn của bộ xử lý AMD sẽ không hoạt động; mọi thứ khác vẫn hoạt động. Có thể cài PawnIO sau: «Cài đặt → Thành phần».%n%nMở trang tải PawnIO?
id.NoPawnIO=Driver PawnIO tidak terpasang.%n%nTanpanya, «Kurva» dan batas prosesor AMD tidak berfungsi; yang lain tetap berfungsi. PawnIO dapat dipasang nanti: «Pengaturan → Komponen».%n%nBuka halaman unduhan PawnIO?
ru.PawnIOTask=Установить драйвер PawnIO (кривая, лимиты и температура процессора AMD)
en.PawnIOTask=Install the PawnIO driver (the AMD processor's curve, limits and temperature)
es.PawnIOTask=Instalar el controlador PawnIO (curva, límites y temperatura del procesador AMD)
pt.PawnIOTask=Instalar o driver PawnIO (curva, limites e temperatura do processador AMD)
ko.PawnIOTask=PawnIO 드라이버 설치 (AMD 프로세서의 커브, 한계, 온도)
th.PawnIOTask=ติดตั้งไดรเวอร์ PawnIO (เคิร์ฟ ขีดจำกัด และอุณหภูมิของโปรเซสเซอร์ AMD)
zh.PawnIOTask=安装 PawnIO 驱动（AMD 处理器的曲线、限制和温度）
vi.PawnIOTask=Cài driver PawnIO (đường cong, giới hạn và nhiệt độ của bộ xử lý AMD)
id.PawnIOTask=Pasang driver PawnIO (kurva, batas, dan suhu prosesor AMD)
ru.PawnIOInstalling=Устанавливаю драйвер PawnIO…
en.PawnIOInstalling=Installing the PawnIO driver…
es.PawnIOInstalling=Instalando el controlador PawnIO…
pt.PawnIOInstalling=Instalando o driver PawnIO…
ko.PawnIOInstalling=PawnIO 드라이버 설치 중…
th.PawnIOInstalling=กำลังติดตั้งไดรเวอร์ PawnIO…
zh.PawnIOInstalling=正在安装 PawnIO 驱动…
vi.PawnIOInstalling=Đang cài driver PawnIO…
id.PawnIOInstalling=Memasang driver PawnIO…
ru.KeepData=Удалить и настройки Red Tune (пресеты, кривые, настройки звука, правила «Распределения», журнал) из %APPDATA%\RedTune?%n%n«Нет» — оставить их для следующей установки.
en.KeepData=Also delete the Red Tune settings (presets, curves, sound settings, «Distribution» rules, log) in %APPDATA%\RedTune?%n%n«No» keeps them for a later install.
es.KeepData=¿Eliminar también los ajustes de Red Tune (preajustes, curvas, ajustes de sonido, reglas de «Distribución», registro) en %APPDATA%\RedTune?%n%n«No» los conserva para una instalación posterior.
pt.KeepData=Excluir também as configurações do Red Tune (predefinições, curvas, configurações de som, regras de «Distribuição», registro) em %APPDATA%\RedTune?%n%n«Não» as mantém para uma instalação futura.
ko.KeepData=%APPDATA%\RedTune에 있는 Red Tune 설정(프리셋, 커브, 사운드 설정, «배분» 규칙, 기록)도 삭제할까요?%n%n«아니요»를 누르면 다음 설치를 위해 남겨 둡니다.
th.KeepData=ลบการตั้งค่าของ Red Tune (พรีเซ็ต เคิร์ฟ การตั้งค่าเสียง กฎ «การกระจาย» บันทึก) ใน %APPDATA%\RedTune ด้วยหรือไม่?%n%n«ไม่» จะเก็บไว้สำหรับการติดตั้งครั้งถัดไป
zh.KeepData=是否同时删除 %APPDATA%\RedTune 中的 Red Tune 设置（预设、曲线、声音设置、«分配» 规则、日志）？%n%n选择 «否» 将保留它们供以后安装使用。
vi.KeepData=Xóa cả cài đặt của Red Tune (preset, đường cong, cài đặt âm thanh, quy tắc «Phân bổ», nhật ký) trong %APPDATA%\RedTune?%n%n«Không» sẽ giữ lại cho lần cài đặt sau.
id.KeepData=Hapus juga pengaturan Red Tune (preset, kurva, pengaturan suara, aturan «Distribusi», jurnal) di %APPDATA%\RedTune?%n%n«Tidak» menyimpannya untuk pemasangan berikutnya.

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
; 21.11: an update started from «О программе» (/SILENT /rtupdate=1): the program closed itself for it and comes back
Filename: "{app}\RedTune.exe"; Flags: nowait; Check: IsUpdate

[UninstallRun]
Filename: "{app}\RedTune.exe"; Parameters: "--autostart off"; RunOnceId: "RemoveTask"; Flags: runhidden waituntilterminated
; the sound effect off every device, its registration and its files (Windows Audio restarts if it was in use)
Filename: "{app}\RedTune.exe"; Parameters: "--audio-uninstall"; RunOnceId: "RemoveAudio"; Flags: runhidden waituntilterminated
; the NVIDIA driver setting «prefer maximum performance» back to the default, if Red Tune turned it on
Filename: "{app}\RedTune.exe"; Parameters: "--gpu-uninstall"; RunOnceId: "RemoveGpuDrs"; Flags: runhidden waituntilterminated

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

function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:rtupdate|0}') = '1';
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
