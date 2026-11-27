using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    class SettingsPage : Page
    {
        readonly Settings s;
        readonly List<Tuple<OtherApp, Row, FlatButton>> apps = new List<Tuple<OtherApp, Row, FlatButton>>();

        public SettingsPage(Settings s, Action languageChanged, Action<int> scaleChanged)
        {
            this.s = s;
            PageGroups.AddTabs(this, "settings");
            // 17.11: a list instead of the Русский / English switch: nine languages, each under its own name
            var lang = new DropButton();
            lang.Set(L.Names, Math.Max(0, Array.IndexOf(L.Codes, s.Language)));
            Row("Язык", null, lang);
            lang.Changed += (o, e) => { s.Language = L.Codes[lang.Index]; Store.SaveSettings(s); L.Set(s.Language); languageChanged(); };
            // 19.11 (the user's order): the scale right under the language — the two that are about how the program looks
            var scales = Enumerable.Range(0, 6).Select(i => 75 + i * 25).ToArray();
            var scale = new DropButton();
            scale.Set(scales.Select(v => v + " %").ToArray(), Math.Max(0, Array.IndexOf(scales, s.UiScale)));
            Row("Масштаб интерфейса", "Программа перезапустится", scale);
            scale.Changed += (o, e) => scaleChanged(scales[scale.Index]);

            var auto = new Toggle();
            auto.Set(Autostart.IsOn());
            var autoRow = Row("Запуск с Windows, свёрнутым в трей", "Через планировщик заданий, без запроса прав при входе", auto);
            auto.CheckedChanged += (o, e) =>
            {
                try { Autostart.Set(auto.Checked); autoRow.SetSub("Через планировщик заданий, без запроса прав при входе"); }
                catch (Exception ex) { auto.Set(!auto.Checked); autoRow.SetSub("Не удалось: " + ex.Message); }
            };
            var tray = new Toggle();
            tray.Set(s.CloseToTray);
            tray.CheckedChanged += (o, e) => { s.CloseToTray = tray.Checked; Store.SaveSettings(s); };
            Row("Закрытие окна сворачивает в трей", "Иначе крестик закрывает программу и автоприменение кривой перестаёт работать", tray);
            // 21.11: the only network access (Updater): a daily look at GitHub's latest release
            var upd = new Toggle();
            upd.Set(s.UpdateCheck);
            upd.CheckedChanged += (o, e) => { s.UpdateCheck = upd.Checked; Store.SaveSettings(s); };
            Row("Проверять обновления", "Раз в день Red Tune спрашивает GitHub, вышла ли новая версия. О компьютере ничего не отправляется; установить — в «О программе»", upd);
            // the sections that can make a PC unstable appear in the menu only with this on (Hw.Risky)
            var expert = new Toggle();
            expert.Set(s.Expert);
            expert.CheckedChanged += (o, e) =>
            {
                if (expert.Checked && !s.Expert && !ConfirmDialog.Ask(FindForm(), "Экспертные функции", "Кривая напряжения, тайминги памяти и прямые лимиты процессора меняют то, что задал производитель. Неверные значения приводят к сбоям, синим экранам или к тому, что ноутбук не загрузится.\n\nЕсли что-то пошло не так, зажмите Shift при запуске Red Tune — он ничего не применит.", "Включить", "Отмена"))
                { expert.Set(false); return; }
                s.Expert = expert.Checked; Store.SaveSettings(s);
                Hw.RaiseExpertChanged();
            };
            Row("Экспертные функции", "«Кривая» и «Память» в меню, разгон видеокарты, запись лимитов процессора AMD. Shift при запуске — безопасный запуск без них", expert);

            // 21.11: the data folder in one file and back (Backup)
            Section("Резервная копия");
            var save = new FlatButton("Сохранить…");
            var saveRow = Row("Сохранить настройки в файл", "Пресеты, кривые, вентиляторы, звук, оформление — всё в одном zip: перед переустановкой Windows или для другого компьютера", save);
            save.Click += (o, e) =>
            {
                using (var d = new SaveFileDialog { Title = L.T("Сохранить настройки в файл"), Filter = "Zip (*.zip)|*.zip", FileName = Backup.DefaultName, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
                {
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    try { Store.SaveSettings(s); int n = Backup.Export(d.FileName); saveRow.SetSub(L.T("Сохранено: ") + System.IO.Path.GetFileName(d.FileName) + " · " + n + L.T(" файлов")); }
                    catch (Exception ex) { saveRow.SetSub(L.T("Не сохранилось: ") + ex.Message); }
                }
            };
            var load = new FlatButton("Загрузить…");
            var loadRow = Row("Загрузить из файла", "Текущие настройки сначала сохранятся в папку backups, потом Red Tune перезапустится с загруженными", load);
            load.Click += (o, e) =>
            {
                using (var d = new OpenFileDialog { Title = L.T("Загрузить из файла"), Filter = "Zip (*.zip)|*.zip", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
                {
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    var m = Backup.ManifestOf(d.FileName);
                    if (m == null) { loadRow.SetSub("Это не резервная копия Red Tune"); return; }
                    bool same = Backup.SameMachine(m);
                    var text = same ? L.T("Текущие настройки заменятся копией от ") + (m.Length > 2 ? m[2] : "") + L.T(". Их прежний вид сохранится в папке backups. Red Tune перезапустится.")
                        : L.T("Копия сделана на другом компьютере: ") + (m.Length > 1 ? m[1] : "") + L.T(". Кривая, тайминги памяти и разгон видеокарты подобраны под то железо — они перенесены не будут, остальное заменится. Прежние настройки сохранятся в папке backups. Red Tune перезапустится.");
                    if (!ConfirmDialog.Ask(FindForm(), "Загрузить из файла", text, "Загрузить", "Отмена")) return;
                    try { Backup.Stage(d.FileName, same); }
                    catch (Exception ex) { loadRow.SetSub(L.T("Не загрузилось: ") + ex.Message); return; }
                    var main = FindForm() as MainForm; if (main != null) main.RestartApp();
                }
            };

            // 17.11: the mini panel (a left click on the tray icon): which of its blocks are shown
            Section("Мини-панель");
            Row("Что показывать", "Панель открывается кликом по значку в трее. Выключенные блоки в ней не показываются; без блоков она не открывается");
            foreach (var k in MiniBlocks.Kinds.Where(k => k.Fits())) // only what this computer has
            {
                var t = new Toggle(); t.Set(MiniBlocks.Shown(s, k.Key));
                var key = k.Key;
                t.CheckedChanged += (o, e) => MiniBlocks.SetShown(s, key, t.Checked);
                Row(k.Name, k.Sub, t);
            }

            // notifications: the general switches, and on their own tab which events show one
            Section("Уведомления");
            var osd = Add(new TabbedRows("Основное", "Внешний вид", "Что показывать"));
            osd.Resized += Relayout;
            var on = new Toggle(); on.Set(s.OsdOn);
            osd.Add(0, new Row("Показывать уведомления", "Режим питания, Fn-клавиши, Caps Lock и другое — по центру экрана на пару секунд", on));
            on.CheckedChanged += (o, e) => { s.OsdOn = on.Checked; Store.SaveSettings(s); };
            var top = new Toggle(); top.Set(s.OsdTopmost);
            osd.Add(0, new Row("Поверх полноэкранных приложений", "Иначе во время игры или видео на весь экран уведомления не показываются", top));
            top.CheckedChanged += (o, e) => { s.OsdTopmost = top.Checked; Store.SaveSettings(s); };
            // 16.11: which monitor: the main one (as it always was), the one with the window in front, or all of them
            var where = new DropButton();
            where.Set(new[] { "Основной монитор", "Где активное окно", "Все мониторы" }, s.OsdAllMonitors ? 2 : s.OsdFollow ? 1 : 0);
            osd.Add(0, new Row("Монитор", "На каком экране показывать уведомления", where));
            where.Changed += (o, e) => { s.OsdAllMonitors = where.Index == 2; s.OsdFollow = where.Index == 1; Store.SaveSettings(s); Osd.Show("power", "Так будет выглядеть уведомление", true); };
            var pos = new DropButton();
            pos.Set(Osd.PositionNames, Math.Max(0, Array.IndexOf(Osd.Positions, s.OsdPosition)));
            pos.Changed += (o, e) => { s.OsdPosition = Osd.Positions[pos.Index]; Store.SaveSettings(s); Osd.Show("power", "Так будет выглядеть уведомление", true); };
            var times = new[] { 1000, 2000, 3000, 5000 };
            var dur = new DropButton();
            dur.Set(times.Select(t => t / 1000 + (L.En ? " s" : " с")).ToArray(), Math.Max(0, Array.IndexOf(times, s.OsdMs)));
            dur.Changed += (o, e) => { s.OsdMs = times[dur.Index]; Store.SaveSettings(s); };
            var test = new FlatButton("Пример");
            test.Click += (o, e) => Osd.Show("power", "Режим питания: Свой", true);
            osd.Add(0, new Row("Положение и длительность", null, pos, dur, test));
            foreach (var k in Osd.Kinds.Where(Osd.Available)) // only what this laptop can show
            {
                var t = new Toggle(); t.Set(Osd.IsOn(k.Key));
                var key = k.Key;
                t.CheckedChanged += (o, e) => Osd.SetOn(key, t.Checked);
                osd.Add(2, new Row(k.Name, null, t));
            }

            // the glass look; each change shows an example half a second later (not on every step of a slider)
            var preview = new Timer { Interval = 500 };
            preview.Tick += (o, e) => { preview.Stop(); Osd.Show("power", "Так будет выглядеть уведомление", true); };
            Action changed = () => { Store.SaveSettings(s); preview.Stop(); preview.Start(); };
            var glass = new Toggle(); glass.Set(s.OsdGlass);
            osd.Add(1, new Row("Стекло", "Размывать то, что за уведомлением", glass));
            glass.CheckedChanged += (o, e) => { s.OsdGlass = glass.Checked; changed(); };
            var liveT = new Toggle(); liveT.Set(s.OsdLive);
            osd.Add(1, new Row("Живое стекло", "Стекло меняется вместе с тем, что под ним. Минус: уведомлений не видно на скриншотах и в записи экрана", liveT));
            liveT.CheckedChanged += (o, e) => { s.OsdLive = liveT.Checked; changed(); };
            var blur = new Segmented("Слабое", "Среднее", "Сильное") { Selected = s.OsdBlur };
            osd.Add(1, new Row("Размытие", "Насколько стекло матовое", blur));
            blur.Changed += (o, e) => { s.OsdBlur = Math.Max(0, blur.Selected); changed(); };
            var tint = new Slider(0, 100, s.OsdTint, "%") { Width = Theme.S(300) };
            osd.Add(1, new Row("Затемнение", "0 % — чистое стекло, 100 % — сплошной тёмный фон", tint));
            tint.Changed += (o, e) => { s.OsdTint = tint.Value; changed(); };
            var shine = new Toggle(); shine.Set(s.OsdShine);
            osd.Add(1, new Row("Блик и рамка", "Светлый край сверху, как у стекла", shine));
            shine.CheckedChanged += (o, e) => { s.OsdShine = shine.Checked; changed(); };
            var example = new FlatButton("Пример");
            example.Click += (o, e) => Osd.Show("power", "Режим питания: Свой", true);
            osd.Add(1, new Row("Посмотреть", "Уведомление покажется там, где выбрано на вкладке «Основное»", example));

            // 16.11: the overlay over games
            Section("Оверлей");
            // 27.11: two tabs like the notifications: how it looks, and which values it shows (Overlay.Items); a change
            // shows the overlay for a few seconds over whatever is on screen (Overlay.Preview)
            var ovlTabs = Add(new TabbedRows("Основное", "Что показывать"));
            ovlTabs.Resized += Relayout;
            var ovl = new Toggle(); ovl.Set(s.OverlayOn);
            ovlTabs.Add(0, new Row("Оверлей поверх игр", "FPS, температуры, загрузка и другие показатели в углу экрана", ovl));
            var ovlWhen = new DropButton(); ovlWhen.Set(Overlay.WhenNames, s.OverlayAlways ? 1 : 0);
            var ovlCorner = new DropButton(); ovlCorner.Set(Overlay.CornerNames, Math.Max(0, Array.IndexOf(Overlay.CornerValues, s.OverlayCorner)));
            ovlTabs.Add(0, new Row("Когда и где показывать", "Не виден в монопольном полном экране", ovlWhen, ovlCorner));
            var ovlSize = new DropButton(); ovlSize.Set(Overlay.SizeNames, s.OverlaySize);
            var ovlLayout = new DropButton(); ovlLayout.Set(Overlay.LayoutNames, s.OverlayColumn ? 1 : 0);
            ovlTabs.Add(0, new Row("Размер и вид", "Столбиком удобнее, когда показателей много", ovlSize, ovlLayout));
            ovlSize.Changed += (o, e) => { s.OverlaySize = Math.Max(0, ovlSize.Index); Store.SaveSettings(s); Overlay.Preview(); Overlay.Apply(); };
            ovlLayout.Changed += (o, e) => { s.OverlayColumn = ovlLayout.Index == 1; Store.SaveSettings(s); Overlay.Preview(); Overlay.Apply(); };
            ovl.CheckedChanged += (o, e) => { s.OverlayOn = ovl.Checked; Store.SaveSettings(s); Overlay.Preview(); Overlay.Apply(); };
            ovlWhen.Changed += (o, e) => { s.OverlayAlways = ovlWhen.Index == 1; Store.SaveSettings(s); Overlay.Apply(); };
            ovlCorner.Changed += (o, e) => { s.OverlayCorner = Overlay.CornerValues[Math.Max(0, Math.Min(Overlay.CornerValues.Length - 1, ovlCorner.Index))]; Store.SaveSettings(s); Overlay.Preview(); Overlay.Apply(); };
            foreach (var it in Overlay.Items.Where(x => x.Fits())) // only what this computer has
            {
                var t = new Toggle(); t.Set(Overlay.Has(s, it.Key));
                var key = it.Key;
                t.CheckedChanged += (o, e) => Overlay.SetItem(s, key, t.Checked);
                ovlTabs.Add(1, new Row(it.Name, it.Sub, t));
            }
            int ovlTab; // page pictures: RT_OVL_TAB=1 shows the second tab
            if (int.TryParse(Environment.GetEnvironmentVariable("RT_OVL_TAB") ?? "", out ovlTab)) ovlTabs.Show(ovlTab);

            appsSection = Section("Другие программы");
            Row("Программы, которые меняют те же настройки", "Если они запускаются вместе с Windows, они будут спорить с Red Tune");
            foreach (var a in OtherApp.All())
            {
                var btn = new FlatButton("Отключить автозапуск");
                var row = Row(a.Name, a.Why, btn);
                var app = a;
                btn.Click += (o, e) => Disable(app, row, btn);
                apps.Add(Tuple.Create(a, row, btn));
            }
            VisibleChanged += (o, e) => { if (Visible) CheckApps(); };
        }

        SectionLabel appsSection;

        // from the warning on the home page
        public void ShowApps()
        {
            if (appsSection == null) return;
            AutoScrollPosition = new System.Drawing.Point(0, Math.Max(0, appsSection.Top - AutoScrollPosition.Y - Theme.S(12)));
            CheckApps();
        }

        void CheckApps()
        {
            Task.Run(() =>
            {
                foreach (var t in apps) try { t.Item1.Check(); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        foreach (var t in apps)
                        {
                            var a = t.Item1;
                            var state = !a.Found ? "не найдено" : a.Autostart ? (a.Running ? "запущена, стартует с Windows" : "стартует с Windows") : a.Running ? "запущена, автозапуск выключен" : "автозапуск выключен";
                            t.Item2.SetSub(a.Why + " · " + state); // kept in Russian, translated when drawn: survives a language switch
                            t.Item3.Visible = a.Found && (a.Autostart || a.Running); t.Item3.Text = a.Autostart ? "Отключить автозапуск" : "Закрыть";
                            t.Item2.PerformLayout();
                        }
                    }));
                }
                catch { }
            });
        }

        void Disable(OtherApp a, Row row, FlatButton btn)
        {
            var text = (a.Autostart ? "Выключить автозапуск «" + a.Name + "» и закрыть её?" : "Закрыть «" + a.Name + "»?") + "\n\nЕё можно вернуть: запустить вручную и включить автозапуск в её настройках.";
            if (Msg.Show(FindForm(), text, "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            btn.Enabled = false;
            Task.Run(() =>
            {
                string err = null;
                try { a.Disable(); } catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => { btn.Enabled = true; if (err != null) row.SetSub("Не удалось: " + err); else CheckApps(); })); } catch { }
            });
        }
    }
}
