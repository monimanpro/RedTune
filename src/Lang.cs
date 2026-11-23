using System;
using System.Collections.Generic;
using System.Linq;

namespace RedTune
{
    // The UI is written in Russian; English is applied when text is drawn (Theme.Text1, menus, dialogs), so switching
    // the language needs no restart. Whole strings first, then phrase fragments for composed strings ("Свой: пресет «X»").
    //
    // 17.11: more languages. Russian is the text as written, English is the tables in this file and Lang2.cs, and every
    // other language is a file lang\<code>.txt built into the exe: a line per English pair, found by the id of its
    // Russian text (Id), so the files do not repeat the Russian. A pair such a file has no line for is shown in English.
    // The files are made by tools\lang (extract.js lists the pairs, build.js writes the files).
    static partial class L
    {
        // 19.11: the order of the list in the settings (the user's): English, Spanish, Portuguese, Russian, then the rest
        public static readonly string[] Codes = { "en", "es", "pt", "ru", "zh", "ko", "th", "vi", "id" };
        public static readonly string[] Names = { "English", "Español", "Português (Brasil)", "Русский", "简体中文", "한국어", "ไทย", "Tiếng Việt", "Bahasa Indonesia" };

        public static string Code = "ru";
        // «not Russian»: the texts go through the tables, numbers and units are written the international way
        public static bool En { get { return Code != "ru"; } }

        static Dictionary<string, string> local;               // the chosen language's whole strings by the Russian text; null for ru and en
        static KeyValuePair<string, string>[] localFragments;  // its fragments in Fragments' order, English where it has none
        static string laptop = "laptop", pc = "PC";            // its word for a laptop and what a desktop PC is called instead

        public static void Set(string code)
        {
            if (Array.IndexOf(Codes, code) < 0) code = "ru";
            Dictionary<string, string> whole = null; KeyValuePair<string, string>[] frags = null; string lap = "laptop", desk = "PC";
            if (code != "ru" && code != "en")
            {
                var byId = Load(code, ref lap, ref desk);
                string t;
                whole = new Dictionary<string, string>();
                foreach (var ru in Whole.Keys) if (byId.TryGetValue("w" + Id(ru), out t)) whole[ru] = t;
                frags = Fragments.Select(kv => new KeyValuePair<string, string>(kv.Key, byId.TryGetValue("f" + Id(kv.Key), out t) ? t : kv.Value)).ToArray();
            }
            local = whole; localFragments = frags; laptop = lap; pc = desk; Code = code;
        }

        // for --lang-check: «es: 886 of 886 whole, 596 of 596 fragments», then the Russian texts a file has no line for
        public static string Coverage()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var code in Codes.Where(c => c != "ru" && c != "en")) // the two languages that live in the code have no file
            {
                string lap = "", desk = "";
                var byId = Load(code, ref lap, ref desk);
                var noWhole = Whole.Keys.Where(ru => !byId.ContainsKey("w" + Id(ru))).ToList();
                var noFrag = Fragments.Where(kv => !byId.ContainsKey("f" + Id(kv.Key))).Select(kv => kv.Key).ToList();
                sb.AppendLine(code + ": " + (Whole.Count - noWhole.Count) + " of " + Whole.Count + " whole, " + (Fragments.Length - noFrag.Count) + " of " + Fragments.Length + " fragments, laptop «" + lap + "» → «" + desk + "», lines in the file " + byId.Count);
                foreach (var ru in noWhole.Concat(noFrag).Take(40)) sb.AppendLine("    — " + ru.Replace("\n", "\\n"));
            }
            return sb.ToString();
        }

        // FNV-1a over the UTF-16 code units, 8 hex digits: the same as in tools\lang\extract.js
        static string Id(string s)
        {
            uint h = 0x811C9DC5;
            unchecked { foreach (char c in s) { h ^= c; h *= 0x01000193; } }
            return h.ToString("x8");
        }

        // lang\<code>.txt: «w|f <tab> id <tab> text» (w = a whole string, f = a fragment; \n, \t and \\ escaped),
        // «@laptop <tab> word <tab> word for a desktop», «#» comments. Empty when the file is not in this build
        static Dictionary<string, string> Load(string code, ref string lap, ref string desk)
        {
            var d = new Dictionary<string, string>();
            try
            {
                using (var st = typeof(L).Assembly.GetManifestResourceStream("RedTune.lang." + code + ".txt"))
                {
                    if (st == null) return d;
                    using (var rd = new System.IO.StreamReader(st, System.Text.Encoding.UTF8))
                    {
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            var p = line.Split('\t');
                            if (p[0] == "@laptop" && p.Length >= 3) { lap = p[1]; desk = p[2]; continue; }
                            if (p.Length < 3 || (p[0] != "w" && p[0] != "f")) continue;
                            d[p[0] + p[1]] = p[2].Replace("\\\\", "\u0001").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\u0001", "\\");
                        }
                    }
                }
            }
            catch (Exception ex) { Store.Log("язык " + code + ": " + ex.Message); }
            return d;
        }

        // 16.11: on a desktop PC every text says «компьютер» where it says «ноутбук» (both words take the same endings).
        // The translation is found by the original text first. Only the lower-case «laptop» is swapped: «RTX 5070
        // Laptop» is a card's name; the other languages name their two words in their file.
        // 19.11, a check before a release: with RT_LANG_MISS=<file> every text that still has Russian letters in it after
        // the tables (in a language other than Russian) is written to that file, each once — what a page shows
        // untranslated is found by drawing the page (--page-png … --lang en).
        static readonly string missFile = Environment.GetEnvironmentVariable("RT_LANG_MISS");
        static readonly HashSet<string> missed = new HashSet<string>();
        static void Miss(string from, string got)
        {
            if (!got.Any(c => c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё')) return;
            lock (missed) { if (!missed.Add(from)) return; try { System.IO.File.AppendAllText(missFile, from.Replace("\n", "\\n") + "\t=>\t" + got.Replace("\n", "\\n") + "\r\n", System.Text.Encoding.UTF8); } catch { } }
        }

        public static string T(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var r = Tr(s);
            if (missFile != null && En) Miss(s, r);
            if (!Hw.Desktop) return r;
            if (r.IndexOf("оутбук", StringComparison.Ordinal) >= 0) r = r.Replace("ноутбук", "компьютер").Replace("Ноутбук", "Компьютер");
            if (En && laptop.Length > 0 && r.IndexOf(laptop, StringComparison.Ordinal) >= 0) r = r.Replace(laptop, pc);
            return r;
        }

        static string Tr(string s)
        {
            if (!En || string.IsNullOrEmpty(s) || !s.Any(c => c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё')) return s;
            string r;
            if (local != null && local.TryGetValue(s, out r)) return r;
            if (Whole.TryGetValue(s, out r)) return r;
            if (s.EndsWith("  ▾")) return Tr(s.Substring(0, s.Length - 3)) + "  ▾"; // drop-down buttons
            foreach (var kv in localFragments ?? Fragments) if (s.Contains(kv.Key)) s = s.Replace(kv.Key, kv.Value);
            return s;
        }

        static readonly Dictionary<string, string> Whole = new Dictionary<string, string>
        {
            // menu and pages
            { "Главная", "Home" }, { "Питание", "Power" }, { "Кривая", "Curve" }, { "Автотюн", "Auto-tune" }, { "Устройство", "Device" },
            { "Компоненты", "Components" }, { "Настройки", "Settings" }, { "О программе", "About" },
            // home
            { "Режим питания", "Power mode" }, { "Fn+Q переключает по кругу", "Fn+Q cycles the modes" }, { "Свой режим: выберите пресет справа · Fn+Q переключает по кругу", "Custom mode: pick a preset on the right · Fn+Q cycles the modes" }, { "Тихий", "Quiet" }, { "Баланс", "Balance" },
            { "Макс", "Performance" }, { "Свой", "Custom" }, { "Экстремальный", "Extreme" }, { "Процессор", "Processor" }, { "Видеокарта", "Graphics" },
            { "Память", "Memory" }, { "загрузка", "load" }, { "Загрузка", "Load" }, { "занято", "used" }, { "Частота", "Clock" }, { "Температура", "Temperature" },
            { "Мощность", "Power" }, { "Вентилятор", "Fan" }, { "Частота ядра", "Core clock" }, { "Видеопамять", "VRAM" }, { "Частота памяти", "Memory clock" },
            { "Занято", "Used" }, { "Чтение", "Read" }, { "Запись", "Write" }, { "Не удалось прочитать режим", "Couldn't read the mode" }, { "видеокарта спит", "GPU is asleep" },
            { "нет данных NVIDIA", "no NVIDIA data" }, { "драйвер NVIDIA не найден", "NVIDIA driver not found" }, { "старый драйвер NVIDIA", "old NVIDIA driver" },
            // curve
            { "UXTU тоже управляет кривой", "UXTU also controls the curve" },
            { "Он применяет свой пресет при запуске, зарядке и после сна и перезапишет эти значения", "It applies its preset at startup, on charger and after sleep and will overwrite these values" },
            { "Отключить UXTU", "Disable UXTU" }, { "Пресет кривой", "Curve preset" }, { "Сохранить", "Save" }, { "Отменить изменения", "Undo the changes" }, { "Как в режиме «Тихий»", "As in the Quiet mode" }, { "Как в режиме «Баланс»", "As in the Balance mode" }, { "Как в режиме «Макс»", "As in the Performance mode" }, { "Сбросить", "Reset" }, { "Применить", "Apply" },
            { "Сейчас на процессоре", "On the CPU now" }, { "Прочитать", "Read" }, { "Применять автоматически", "Apply automatically" },
            { "При запуске Windows", "At Windows startup" }, { "Нужен автозапуск программы (вкладка «Настройки»)", "Needs the app to start with Windows (Settings)" },
            { "При подключении зарядки", "When the charger is plugged in" }, { "При работе от батареи", "On battery" }, { "После выхода из сна", "After sleep" },
            { "История изменений", "Change history" }, { "Каждое применение записывается, любой прошлый набор можно вернуть", "Every apply is recorded, any past set can be restored" },
            { "Открыть", "Open" }, { "совпадает", "matches" }, { "отличается", "differs" }, { "ошибка", "error" }, { "читаю…", "reading…" }, { "применяю…", "applying…" },
            { "Процессор работает на этих значениях", "The CPU runs on these values" }, { "Активный: применяется автоматически", "Active: applied automatically" },
            { "Выбран, но не активный: «Применить» сделает его активным", "Selected but not active: Apply makes it active" },
            { "Синие ячейки изменены", "Blue cells are changed" }, { "Тяните ползунок или введите число. После щелчка колесо мыши меняет значение на 1", "Drag a slider or type a number. After a click the mouse wheel steps it by 1" }, { "Голубые ползунки изменены", "Light blue sliders are changed" }, { "Тяните ползунок или введите число", "Drag a slider or type a number" }, { "Щёлкните ячейку — колесо мыши будет менять её на 1", "Click a cell, then the mouse wheel steps it by 1" },
            { "Заводской (0)", "Factory (0)" }, { "Дневной", "Daily" }, { "Новый пресет из этих значений…", "New preset from these values…" },
            { "Переименовать…", "Rename…" }, { "Удалить", "Delete" }, { "Новый пресет", "New preset" }, { "Название", "Name" }, { "Мой пресет", "My preset" },
            { "Переименовать пресет", "Rename preset" }, { "Такое название уже есть", "That name is taken" }, { "Готово", "Done" }, { "Отмена", "Cancel" },
            { "История изменений кривой", "Curve change history" }, { "Пока пусто", "Nothing yet" }, { "Здесь появится каждое применение кривой", "Every curve apply shows up here" },
            { "В редактор", "To editor" },
            // power
            { "Lenovo Legion Toolkit тоже управляет питанием", "Lenovo Legion Toolkit also controls power" },
            { "Он применяет свои пресеты при смене режима и перезапишет эти настройки", "It applies its presets on mode changes and will overwrite these settings" },
            { "Отключить LLT", "Disable LLT" }, { "Пресет своего режима", "Custom mode preset" }, { "Изменения", "Changes" }, { "Сохранено", "Saved" },
            { "Есть несохранённые изменения", "Unsaved changes" }, { "Применяю…", "Applying…" },
            { "Активный: включается в режиме «Свой» (Fn+Q или главная)", "Active: used in the Custom mode (Fn+Q or Home)" },
            { "Долгосрочное ограничение мощности", "Long-term power limit" }, { "Непрерывная мощность процессора", "Sustained CPU power" },
            { "Краткосрочное ограничение мощности", "Short-term power limit" }, { "Пиковая мощность на короткое время", "Peak power for a short time" },
            { "Предельная пиковая мощность", "Peak power limit" }, { "Максимум на мгновенных нагрузках", "Maximum on instant loads" },
            { "Долгосрочное ограничение при нагрузке ЦП+ГП", "Long-term limit under CPU+GPU load" }, { "Мощность процессора, когда работает и видеокарта", "CPU power while the GPU works too" },
            { "Ограничение температуры ЦП", "CPU temperature limit" }, { "Выше — процессор сбрасывает частоту", "Above it the CPU lowers its clock" },
            { "Добавка к видеокарте по загрузке процессора", "Extra GPU power by CPU load" }, { "Настраиваемый TGP", "Configurable TGP" },
            { "Мощность видеокарты сверх базовой", "GPU power above the base" }, { "Ограничение температуры ГП", "GPU temperature limit" },
            { "Выше — видеокарта сбрасывает частоту", "Above it the GPU lowers its clock" }, { "Общая целевая мощность процессора от сети", "Total CPU power target on AC" },
            { "Порог, с которого мощность перераспределяется на видеокарту", "Threshold where power moves to the GPU" }, { "Dynamic Boost ГП на ЦП", "Dynamic Boost GPU to CPU" },
            { "Сколько видеокарта может забрать у процессора", "How much the GPU may take from the CPU" }, { "Вентиляторы", "Fans" },
            { "По умолчанию", "Default" }, { "По самому горячему из ЦП и ГП", "Follows the hotter of CPU and GPU" }, { "Максимальная скорость", "Full speed" }, { "Все вентиляторы на полную, включается кнопкой «Применить» выше. Долго так держать — изнашивает вентиляторы", "All fans at full speed, switched on with Apply above. Wears the fans if kept on" },
            { "Все вентиляторы на полную. Долго так держать — изнашивает вентиляторы", "All fans at full speed. Wears the fans if kept on" },
            { "Скорость по самому горячему из ЦП и ГП. Наведите на точку, чтобы увидеть пороги", "Speed follows the hotter of CPU and GPU. Hover a point for its thresholds" },
            { "Тяните точки вверх и вниз", "Drag the points up and down" }, { "Тяните точки вверх и вниз, наведите на точку — покажет пороги", "Drag the points up and down, hover one for its thresholds" }, { "Схема питания Windows", "Windows power plan" }, { "Схема для этого пресета", "Plan for this preset" },
            { "Включается вместе с пресетом", "Switched on with the preset" }, { "Схемы Windows", "Windows plans" }, { "Создать или настроить схемы — в панели управления Windows", "Create or edit plans in the Windows Control Panel" }, { "Создать схему", "Create plan" },
            { "Открыть «Электропитание»", "Open Power Options" }, { "Создать копию сбалансированной схемы или открыть панель управления", "Copy the balanced plan or open the Control Panel" },
            { "Схемы для других режимов", "Plans for the other modes" }, { "Не менять", "Don't change" }, { "Новый пресет из этих настроек…", "New preset from these settings…" },
            { "Новая схема питания", "New power plan" },
            // device
            { "Батарея", "Battery" }, { "Режим зарядки", "Charging mode" }, { "Консервация", "Conservation" }, { "Обычный", "Normal" }, { "Быстрый", "Rapid" },
            { "Консервация держит заряд около 80 % и бережёт батарею", "Conservation keeps the charge near 80 % to spare the battery" },
            { "Ночная зарядка", "Night charge" }, { "80 % на ночь, 100 % к утру", "80 % overnight, 100 % by morning" },
            { "Включение при открытии крышки", "Power on when the lid opens" }, { "Включение при подключении зарядки", "Power on when the charger is plugged in" },
            { "Включение при зарядке через USB-C", "Power on with USB-C charging" }, { "Питание USB", "USB power" }, { "Выкл", "Off" }, { "Во сне", "In sleep" }, { "Всегда", "Always" },
            { "Заряжать устройства по USB, когда ноутбук спит или выключен", "Charge USB devices while the laptop sleeps or is off" },
            { "Экран", "Display" }, { "Разрешение", "Resolution" }, { "Частота обновления", "Refresh rate" }, { "Масштаб", "Scale" }, { "Встроенный экран", "Built-in display" },
            { "Быстрее отклик матрицы", "Faster panel response" }, { "Выключить дисплеи", "Turn off displays" }, { "Выключить", "Turn off" },
            { "Экраны гаснут, ноутбук работает и не засыпает. Мышь или клавиша включают их", "Screens go dark, the laptop keeps running. Mouse or a key wakes them" },
            { "Клавиатура и ввод", "Keyboard and input" }, { "Подсветка клавиатуры", "Keyboard backlight" }, { "Fn+Пробел", "Fn+Space" },
            { "Блокировка тачпада", "Touchpad lock" }, { "Выключить сенсорную панель", "Turn the touchpad off" }, { "Блокировка Fn", "Fn lock" },
            { "F1–F12 работают без Fn", "F1–F12 work without Fn" }, { "Блокировка клавиши Windows", "Windows key lock" }, { "Только на встроенной клавиатуре", "Built-in keyboard only" },
            { "Микрофон", "Microphone" }, { "Все микрофоны вкл. или выкл.", "All microphones on or off" },
            // components, settings, about
            { "найден", "found" }, { "не найден", "not found" }, { "Указать папку", "Choose folder" }, { "Страница CoreCycler", "CoreCycler page" },
            { "Скоро", "Coming" }, { "Prime95, .NET 8 и проверка всего для автотюна", "Prime95, .NET 8 and checks for auto-tune" }, { "Этап 4", "Stage 4" },
            { "Запуск с Windows, свёрнутым в трей", "Start with Windows, in the tray" }, { "Через планировщик заданий, без запроса прав при входе", "Through the Task Scheduler, no UAC prompt at logon" },
            { "Закрытие окна сворачивает в трей", "Closing the window hides it to the tray" },
            { "Иначе крестик закрывает программу и автоприменение кривой перестаёт работать", "Otherwise the close button quits and the automatic curve stops" },
            { "Папка с настройками", "Settings folder" }, { "Инициатор", "Initiator" }, { "Исполнитель", "Executor" },
            { "Кожаный — идеи, тесты, сотни перезагрузок и терпение", "The human — ideas, tests, hundreds of reboots and patience" },
            { "Claude — код, кривые и 400+ раундов Prime95", "Claude — code, curves and 400+ rounds of Prime95" },
            { "Ни одно ядро не пострадало. Ну, почти.", "No core was harmed. Well, almost." },
            { "Выход", "Exit" }, { "Применить кривую", "Apply curve" },
            // settings, notifications, other programs
            { "Язык", "Language" }, { "Масштаб интерфейса", "Interface scale" }, { "Программа перезапустится", "The app restarts" }, { "Уведомления", "Notifications" },
            { "Показывать уведомления", "Show notifications" },
            { "Режим питания, Fn-клавиши, Caps Lock и другое — по центру экрана на пару секунд", "Power mode, Fn keys, Caps Lock and more — on screen for a moment" },
            { "Поверх полноэкранных приложений", "Over full-screen apps" },
            { "Иначе во время игры или видео на весь экран уведомления не показываются", "Otherwise nothing shows during a full-screen game or video" },
            { "1 с", "1 s" }, { "2 с", "2 s" }, { "3 с", "3 s" }, { "5 с", "5 s" },
            { "На всех мониторах", "On all monitors" }, { "Положение и длительность", "Position and duration" }, { "Пример", "Preview" },
            { "Внизу по центру", "Bottom center" }, { "Вверху по центру", "Top center" }, { "По центру", "Center" }, { "Внизу слева", "Bottom left" },
            { "Внизу справа", "Bottom right" }, { "Вверху слева", "Top left" }, { "Вверху справа", "Top right" },
            { "Так будет выглядеть уведомление", "This is how a notification looks" },
            { "Камера", "Camera" }, { "Режим полёта", "Airplane mode" }, { "Блок питания", "Power adapter" }, { "Применение кривой", "Curve applied" },
            { "Caps Lock включён", "Caps Lock on" }, { "Caps Lock выключен", "Caps Lock off" }, { "Num Lock включён", "Num Lock on" }, { "Num Lock выключен", "Num Lock off" },
            { "Камера включена", "Camera on" }, { "Камера выключена", "Camera off" }, { "Блокировка Fn включена", "Fn lock on" }, { "Блокировка Fn выключена", "Fn lock off" },
            { "Тачпад выключен", "Touchpad off" }, { "Тачпад включён", "Touchpad on" }, { "Подсветка выключена", "Backlight off" },
            { "Микрофон выключен", "Microphone off" }, { "Микрофон включён", "Microphone on" }, { "Зарядка подключена", "Charger connected" }, { "Зарядка отключена", "Charger disconnected" },
            { "Другие программы", "Other programs" }, { "Программы, которые меняют те же настройки", "Programs that change the same settings" },
            { "Если они запускаются вместе с Windows, они будут спорить с Red Tune", "If they start with Windows they fight Red Tune" },
            { "Отключить автозапуск", "Disable autostart" }, { "не найдено", "not found" }, { "запущена, стартует с Windows", "running, starts with Windows" }, { "стартует с Windows", "starts with Windows" }, { "запущена, автозапуск выключен", "running, autostart off" }, { "автозапуск выключен", "autostart off" },
            { "Режимы питания, лимиты, вентиляторы, батарея, Fn-клавиши", "Power modes, limits, fans, battery, Fn keys" },
            { "Кривая (пресет при запуске, зарядке и после сна)", "Curve (preset at startup, on charger and after sleep)" },
            { "Служба Fn-клавиш Lenovo: перехватывает их у программы", "Lenovo Fn key service: takes the keys from the app" },
            { "Службы Lenovo, меняют режимы и батарею", "Lenovo services, change modes and the battery" },
            { "Фирменная программа Lenovo для игровых ноутбуков", "Lenovo's own app for gaming laptops" },
            { "Закрыть Lenovo Legion Toolkit и выключить его автозапуск?\n\nПитание, вентиляторы и батарею дальше ведёт Red Tune. LLT можно вернуть, запустив его вручную.",
              "Close Lenovo Legion Toolkit and disable its autostart?\n\nRed Tune takes over power, fans and the battery. LLT can be started by hand again." },
            { "Закрыть UXTU и выключить его автозапуск?\n\nКривую дальше применяет Red Tune. UXTU можно вернуть: задача «UXTU» в планировщике заданий.",
              "Close UXTU and disable its autostart?\n\nRed Tune applies the curve from now on. UXTU can come back: the \"UXTU\" task in Task Scheduler." },
        };

        // longest first so "Не удалось прочитать" wins over "Не удалось"
        static KeyValuePair<string, string>[] Fragments = new Dictionary<string, string>
        {
            { "Свой: пресет «", "Custom: preset «" }, { "» · Fn+Q переключает по кругу", "» · Fn+Q cycles the modes" }, { "Сейчас: Экстремальный", "Now: Extreme" },
            { "Fn+Q переключает по кругу", "Fn+Q cycles the modes" }, { "Свой режим: выберите пресет справа · Fn+Q переключает по кругу", "Custom mode: pick a preset on the right · Fn+Q cycles the modes" }, { "Применено: режим «Свой», пресет «", "Applied: Custom mode, preset «" },
            { "Применить кривую «", "Apply curve «" }, { "Кривая «", "Curve «" }, { "» применена", "» applied" }, { "   · активный", "   · active" },
            { "Не удалось: ", "Failed: " }, { "Применяю «", "Applying «" }, { "Недоступно: ", "Unavailable: " }, { "Ошибка: ", "Error: " }, { "Не применилась: ", "Not applied: " },
            { " · есть несохранённые изменения", " · unsaved changes" }, { "Значения от ", "Values from " }, { " до ", " to " }, { " и ещё ", " and " },
            { " ядер / ", " cores / " }, { " потока", " threads" }, { " ГБ", " GB" }, { "износ ", "wear " }, { " МБ/с", " MB/s" }, { " ГБ/с", " GB/s" }, { " ТБ", " TB" }, { "Диск ", "Drive " }, { " ГГц", " GHz" }, { " МГц", " MHz" }, { " об/мин", " rpm" },
            { "Слот ", "Slot " }, { "канал ", "channel " }, { "Точка ", "Point " }, { " · ЦП от ", " · CPU from " }, { " · ГП от ", " · GPU from " }, { "Версия ", "Version " },
            { " (изменён)", " (edited)" }, { "Кривая не применилась при запуске: ", "The curve was not applied at startup: " }, { "Кривая не применилась (", "The curve was not applied (" },
            { "Сохранить изменения в пресет «", "Save the changes into preset «" }, { "» перед применением?", "» before applying?" },
            { "Удалить пресет «", "Delete preset «" }, { "»? Его значения останутся в истории изменений.", "»? Its values stay in the change history." },
            { ": Свой", ": Custom" }, { ": Тихий", ": Quiet" }, { ": Баланс", ": Balance" }, { ": Макс", ": Performance" }, { ": Экстремальный", ": Extreme" },
            { "Режим питания: ", "Power mode: " }, { "Частота обновления: ", "Refresh rate: " }, { "Подсветка: ", "Backlight: " },
            { "Выключить автозапуск «", "Disable the autostart of «" }, { "» и закрыть её?\n\nЕё можно вернуть: запустить вручную и включить автозапуск в её настройках.", "» and close it?\n\nIt can come back: start it by hand and turn its autostart on." },
            { " · не найдено", " · not found" }, { " · запущена, стартует с Windows", " · running, starts with Windows" }, { " · стартует с Windows", " · starts with Windows" },
            { " · запущена, автозапуск выключен", " · running, autostart off" }, { " · автозапуск выключен", " · autostart off" },
            { "вручную", "manual" }, { "запуск", "startup" }, { "зарядка", "charger" }, { "батарея", "battery" }, { "после сна", "after sleep" },
        }.OrderByDescending(kv => kv.Key.Length).ToArray();

        public static void Add(Dictionary<string, string> more) { foreach (var kv in more) Whole[kv.Key] = kv.Value; }
    }
}
