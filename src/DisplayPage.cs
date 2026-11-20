using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // «Дисплей» (20.11, the user's wish): the screens in a section of their own. Moved here from «Устройство → Экран»:
    // the resolution, the refresh rate, the scale, HDR, Over Drive, one brightness for all plans, the displays off. New:
    // the brightness (the built-in screen through Windows, a monitor through DDC/CI), Win+P, the main monitor, and the
    // built-in screen's refresh rate following the power source. A tab per monitor shown; the rows above the tabs are
    // for all of them. Every value is read when the page shows; a failure shows in the row's grey line.
    class DisplayPage : Page
    {
        readonly Settings s;
        List<DisplayInfo> mons = new List<DisplayInfo>();
        DisplayInfo mon; // the monitor of the tab shown
        int tab;
        PageTabs tabs;
        readonly Row topoRow, briRow, resRow, hzRow, dpiRow, hdrRow, colorRow, profRow, mainRow, odRow, autoRow, autoRatesRow, syncRow;
        readonly DropButton profBtn = new DropButton();
        readonly Row acmRow; readonly Toggle acm = new Toggle();
        const string AcmSub = "Windows сама держит цвета точными (sRGB). Выключено — экран показывает цветовой режим выбранного профиля";
        readonly FlatButton profAdd = new FlatButton("Добавить…");
        List<string> profNames = new List<string>(); // the profile behind each item of profBtn; null = no profile
        readonly Segmented topo = new Segmented("Только ноутбук", "Повторять", "Расширить", "Только второй");
        readonly Slider bri = new Slider(0, 100, 50, "%") { Width = Theme.S(330), WarpToKnob = true };
        readonly DropButton resBtn = new DropButton(), hzBtn = new DropButton(), dpiBtn = new DropButton(), hzAc = new DropButton(), hzDc = new DropButton();
        readonly Toggle hdr = new Toggle(), od = new Toggle(), auto = new Toggle(), sync = new Toggle();
        readonly FlatButton mainBtn = new FlatButton("Сделать основным");
        List<DisplayMode> modes = new List<DisplayMode>();
        bool loading;
        static readonly int[] Topologies = { 1, 2, 4, 8 };
        const string BriSub = "Как клавиши яркости", BriSubDdc = "Через DDC/CI, как кнопки самого монитора";
        const string AutoSub = "Высокая частота от сети, низкая от батареи: экран меньше тратит, когда ноутбук не на зарядке";

        public DisplayPage(Settings settings)
        {
            s = settings;
            topoRow = Row("Режим экранов", "Как Win+P", topo);
            topo.Changed += (o, e) => { if (loading || topo.Selected < 0) return; int t = Topologies[topo.Selected]; Run(topoRow, "Как Win+P", () => Display.SetTopology(t), true); };

            // the rows of one monitor (the tabs go in front of the first of them when there are two or more)
            briRow = Row("Яркость", BriSub, bri);
            resRow = Row("Разрешение", null, resBtn);
            hzRow = Row("Частота обновления", null, hzBtn);
            dpiRow = Row("Масштаб", null, dpiBtn);
            // 20.11 (the user's wish): under the scale the profile, HDR, automatic colour management and the signal
            // 20.11: the colour profile Windows applies to this display (Lenovo ships measured ones for its panel)
            profRow = Row("Цветовой профиль", "—", profAdd, profBtn); // the list at the right edge, under the other lists (20.11)
            profBtn.Changed += (o, e) =>
            {
                if (loading || profBtn.Index < 0 || profBtn.Index >= profNames.Count) return;
                var m = mon; var name = profNames[profBtn.Index];
                Store.Log("цветовой профиль: " + m.Title + " → " + (name ?? "без профиля"));
                Run(profRow, null, () => { if (name == null) Display.ClearColorProfile(m); else Display.SetColorProfile(m, name); }, true);
            };
            profAdd.Click += (o, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Цветовые профили (*.icc; *.icm)|*.icc;*.icm", InitialDirectory = Display.ColorDir })
                {
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    var m = mon; var file = d.FileName;
                    Run(profRow, null, () => Display.InstallColorProfile(m, file), true);
                }
            };
            hdrRow = Row("HDR", null, hdr);
            // 20.11: Windows' automatic colour management: on, every profile of the panel gives the same accurate picture;
            // off, Lenovo's colour modes (DCI-P3, sRGB …) show
            acmRow = Row("Автоуправление цветом", AcmSub, acm);
            acm.CheckedChanged += (o, e) =>
            {
                if (loading) return;
                var m = mon; bool v = acm.Checked;
                ColorKeep.Remember(s, m, v); // 20.11 (the user's wish): turned off, it is kept off for this monitor
                Store.Log("автоуправление цветом: " + m.Title + " → " + (v ? "вкл" : "выкл"));
                Run(acmRow, AcmText(m), () => Display.SetAutoColor(m, v), true);
            };
            // 20.11: what goes down the cable: RGB or YCbCr and the bits per channel (a YCbCr signal read as RGB tints the picture)
            colorRow = Row("Цвет сигнала", "—");
            mainRow = Row("Основной монитор", "Панель задач с часами и новые окна переедут на этот экран", mainBtn); // a line from the start: the row is as tall as one with it
            odRow = Row("Over Drive", "Быстрее отклик матрицы", od);
            autoRow = Row("Частота от питания", AutoSub, auto);
            autoRatesRow = Row("От сети · от батареи", "Сменится сама при подключении и отключении зарядки", hzAc, hzDc);
            syncRow = Row("Одна яркость для всех схем питания", "Яркость экрана записывается во все схемы Windows при каждом изменении: при смене схемы экран не меняет яркость", sync);
            var off = new FlatButton("Выключить");
            Row("Выключить дисплеи", "Экраны гаснут, ноутбук работает и не засыпает. Мышь или клавиша включают их", off);
            // a moment later, as the mini panel does: by then the hand is off the mouse
            off.Click += (o, e) => { var later = new Timer { Interval = 600 }; later.Tick += (a, b) => { later.Stop(); later.Dispose(); Display.MonitorsOff(); }; later.Start(); };

            bri.Changed += (o, e) => { if (!loading) BriLater(); };
            briTimer.Tick += (o, e) => { briTimer.Stop(); BriWrite(); };
            resBtn.Changed += (o, e) => SetMode(resRow, true);
            hzBtn.Changed += (o, e) => SetMode(hzRow, false);
            dpiBtn.Changed += (o, e) => { if (loading || dpiBtn.Index < 0) return; var m = mon; var v = int.Parse(dpiBtn.Items[dpiBtn.Index].TrimEnd('%', ' ')); Run(dpiRow, null, () => Display.SetDpi(m, v)); };
            hdr.CheckedChanged += (o, e) => { if (loading) return; var m = mon; bool v = hdr.Checked; Run(hdrRow, null, () => Display.SetHdr(m, v)); };
            mainBtn.Click += (o, e) => { var m = mon; Run(mainRow, null, () => Display.SetPrimary(m), true); };
            od.CheckedChanged += (o, e) => { if (loading) return; bool v = od.Checked; Run(odRow, "Быстрее отклик матрицы", () => Lenovo.GameZoneSet("SetODStatus", v ? 1 : 0)); };
            auto.CheckedChanged += (o, e) => { if (loading) return; s.HzAuto = auto.Checked; Store.SaveSettings(s); ShowAuto(); DisplayAuto.ApplyLater(s, "переключатель"); };
            hzAc.Changed += (o, e) => { if (loading || hzAc.Index < 0) return; s.HzAc = Rate(hzAc); Store.SaveSettings(s); DisplayAuto.ApplyLater(s, "выбор частоты"); };
            hzDc.Changed += (o, e) => { if (loading || hzDc.Index < 0) return; s.HzDc = Rate(hzDc); Store.SaveSettings(s); DisplayAuto.ApplyLater(s, "выбор частоты"); };
            sync.Set(s.SyncBrightness);
            sync.CheckedChanged += (o, e) => { s.SyncBrightness = sync.Checked; Store.SaveSettings(s); BrightnessSync.Init(s); };

            VisibleChanged += (o, e) => { if (Visible) Reload(); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplays;
            Disposed += (o, e) => { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplays; briTimer.Dispose(); };
        }

        // the check and the off-screen render: that monitor's tab
        internal void ShowTab(int i) { if (i < 0 || i >= mons.Count) return; tab = i; if (tabs != null) tabs.Selected = i; ShowMonitor(); }

        static int Rate(DropButton b) { int v; return int.TryParse(b.Items[b.Index].Replace(" Hz", ""), out v) ? v : 0; }

        void OnDisplays(object sender, EventArgs e) { try { BeginInvoke((Action)(() => { if (Visible) Reload(); })); } catch { } }

        // the monitors anew (one may have come or gone), then the tab shown
        void Reload()
        {
            Task.Run(() =>
            {
                List<DisplayInfo> all; int connected, topology;
                try { all = Display.All(); connected = Display.Connected(); topology = Display.Topology(); } catch { all = new List<DisplayInfo>(); connected = 0; topology = 0; }
                try { BeginInvoke((Action)(() => ShowMonitors(all, connected, topology))); } catch { }
            });
        }

        void ShowMonitors(List<DisplayInfo> all, int connected, int topology)
        {
            loading = true;
            topoRow.Visible = connected >= 2;
            topo.Selected = Array.IndexOf(Topologies, topology);
            loading = false;
            string was = mon != null ? mon.Gdi : null;
            bool same = all.Select(m => m.Gdi + m.Name).SequenceEqual(mons.Select(m => m.Gdi + m.Name));
            mons = all;
            if (!same)
            {
                if (tabs != null) { Remove(tabs); tabs = null; }
                if (mons.Count >= 2)
                {
                    tabs = InsertBefore(briRow, new PageTabs(0, mons.Select(m => m.Title).ToArray()), 8);
                    tabs.Picked += i => { tab = i; tabs.Selected = i; ShowMonitor(); };
                }
                int keep = mons.FindIndex(m => m.Gdi == was);
                tab = keep >= 0 ? keep : 0;
                if (tabs != null) tabs.Selected = tab;
            }
            ShowMonitor();
        }

        void ShowMonitor()
        {
            mon = tab < mons.Count ? mons[tab] : null;
            bool any = mon != null, inside = any && mon.Internal;
            foreach (var r in new Control[] { briRow, resRow, hzRow, dpiRow, mainRow }) r.Visible = any;
            mainRow.Visible = any && mons.Count >= 2;
            bool lenovo = Hw.Device && !Hw.AsusFw;
            odRow.Visible = inside && lenovo; autoRow.Visible = inside; syncRow.Visible = inside; // acmRow stays as it is until the answer comes (hidden here, it went and came back after every profile pick, 20.11)
            ShowAuto();
            if (!any) { hdrRow.Visible = false; acmRow.Visible = false; Relayout(); return; }
            mainBtn.Enabled = !mon.Primary;
            mainRow.SetSub(mon.Primary ? "Этот: панель задач с часами и новые окна на нём" : "Панель задач с часами и новые окна переедут на этот экран");
            briRow.SetSub(mon.Internal ? BriSub : BriSubDdc);
            var m = mon;
            Read(() =>
            {
                var cur = Display.Current(m); var all = Display.Modes(m); int dpi; var dpis = Display.DpiOptions(m, out dpi);
                var h = Display.Hdr(m); var b = Display.Brightness(m); var col = Display.Color(m);
                var prof = Display.ColorProfiles(m); var installed = Display.InstalledProfiles(); var auto = Display.AutoColor(m);
                int odv = m.Internal && lenovo ? Lenovo.GameZoneGet("GetODStatus") : -1;
                return new object[] { cur, all, dpis, dpi, h, b, odv, col, prof, installed, auto };
            }, r =>
            {
                if (m != mon) return; // another tab was picked meanwhile
                var cur = (DisplayMode)r[0]; modes = (List<DisplayMode>)r[1];
                if (cur != null)
                {
                    var res = modes.Select(x => x.Width + " × " + x.Height).Distinct().ToList();
                    if (!res.Contains(cur.Width + " × " + cur.Height)) res.Insert(0, cur.Width + " × " + cur.Height);
                    resBtn.Set(res.ToArray(), res.IndexOf(cur.Width + " × " + cur.Height));
                    var hz = Rates(cur); int now = Now(cur); if (!hz.Contains(now)) { hz.Add(now); hz.Sort(); }
                    hzBtn.Set(hz.Select(x => x + " Hz").ToArray(), hz.IndexOf(now));
                    if (m.Internal) ShowAutoRates(hz);
                }
                var dpis = (int[])r[2]; dpiBtn.Set(dpis.Select(x => x + " %").ToArray(), Array.IndexOf(dpis, (int)r[3]));
                var h = (bool?)r[4]; hdrRow.Visible = h.HasValue; if (h.HasValue) hdr.Set(h.Value);
                var b = (int?)r[5]; bri.Enabled = b.HasValue; if (b.HasValue) bri.Value = b.Value;
                if (!b.HasValue) briRow.SetSub(m.Internal ? "Windows не отдаёт яркость этого экрана" : "Монитор не отвечает по DDC/CI: его можно включить в меню монитора");
                int odv = (int)r[6]; if (odv >= 0) od.Set(odv == 1);
                var col = (Tuple<string, int>)r[7]; colorRow.Visible = col != null;
                var auto = (bool?)r[10]; acmRow.Visible = auto.HasValue; if (auto.HasValue) { acm.Set(auto.Value); acmRow.SetSub(AcmText(m)); }
                ShowProfiles(m, (Display.Profiles)r[8], (List<string>)r[9]);
                if (col != null) colorRow.SetSub(col.Item1 + " · " + col.Item2 + " бит на канал" + (col.Item1.StartsWith("YCbCr") ? " · если цвета искажены (красный или розовый оттенок), поставьте формат RGB в панели видеокарты" : ""));
                Relayout();
            }, resRow);
            Relayout();
        }

        // 20.11: the rates at this size, a pair such as 59 and 60 as one: they are the same mode (the monitor's «60 Hz» runs at
        // 59.95 and Windows lists it under both numbers; picking 60 gave 59 again), shown and set as the higher one
        List<int> Rates(DisplayMode cur)
        {
            var all = modes.Where(x => x.Width == cur.Width && x.Height == cur.Height).Select(x => x.Hz).Distinct().OrderBy(x => x).ToList();
            return all.Where(h => !all.Contains(h + 1)).ToList();
        }

        string AcmText(DisplayInfo m) { return ColorKeep.Kept(s, m) ? "Выключено и запомнено для этого монитора: если Windows включит его сама, Red Tune выключит снова" : AcmSub; }

        // the list: no profile, then the installed monitor profiles that fit this display (Lenovo's panel profiles only on
        // the panel they were measured on), and the one in use even when it does not fit, marked so
        void ShowProfiles(DisplayInfo m, Display.Profiles p, List<string> installed)
        {
            profRow.Visible = p.Error == null;
            if (p.Error != null) return;
            var names = new List<string> { null };
            names.AddRange(installed.Where(x => !Display.ForOtherPanel(m, x)));
            if (!string.IsNullOrEmpty(p.Default) && !names.Contains(p.Default)) names.Add(p.Default);
            profNames = names;
            profBtn.Set(names.Select(x => x == null ? "Без профиля" : Display.ProfileTitle(x)).ToArray(), Math.Max(0, names.IndexOf(string.IsNullOrEmpty(p.Default) ? null : p.Default)));
            // short beside the two buttons; the explanation on a line of its own under the row
            int cm = Display.ColorMode(m);
            string mode = cm == 2 ? "Режим HDR" : p.Advanced ? "Автоуправление цветом Windows" : "Обычный режим цвета";
            if (!string.IsNullOrEmpty(p.Default) && Display.ForOtherPanel(m, p.Default))
            {
                profRow.SetSub("Профиль матрицы ноутбука");
                profRow.SetExtra("Этот профиль измерен на экране ноутбука: монитору он не подходит и искажает цвета. Лучше «Без профиля» или профиль самого монитора");
            }
            else
            {
                profRow.SetSub(mode);
                // under automatic colour management Windows maps everything to accurate sRGB through the profile: every profile of
                // the panel gives the same picture (seen 20.11: no pick changed anything); the colour modes need it off
                profRow.SetExtra(p.Advanced && cm != 2 ? "При автоуправлении все профили матрицы дают одну картинку. Для режима Lenovo (DCI-P3, sRGB…) выключите его ниже" : null);
            }
            Relayout();
        }

        // the rate the screen runs at, rounded from Windows' fraction (59.951 → 60); the listed number when that is not known
        static int Now(DisplayMode cur) { return cur.Exact > 0 ? (int)Math.Round(cur.Exact) : cur.Hz; }

        void ShowAuto()
        {
            loading = true; auto.Set(s.HzAuto); loading = false;
            autoRatesRow.Visible = autoRow.Visible && s.HzAuto;
        }

        // the two lists of rates: from mains the choice stored or the highest, on battery the choice stored or 60
        void ShowAutoRates(List<int> hz)
        {
            loading = true;
            var names = hz.Select(x => x + " Hz").ToArray();
            int ac = DisplayAuto.Pick(hz, s.HzAc, true), dc = DisplayAuto.Pick(hz, s.HzDc, false);
            hzAc.Set(names, hz.IndexOf(ac)); hzDc.Set(names, hz.IndexOf(dc));
            loading = false;
        }

        // ---------- brightness: written a moment after the slider stops (a monitor takes ~50 ms a command), and while
        // it is dragged no more often than that; one write at a time ----------
        readonly Timer briTimer = new Timer { Interval = 120 };
        bool briBusy, briAgain;
        void BriLater() { briTimer.Stop(); briTimer.Start(); }
        void BriWrite()
        {
            if (mon == null) return;
            if (briBusy) { briAgain = true; return; }
            briBusy = true;
            var m = mon; int v = bri.Value;
            Task.Run(() =>
            {
                string err = null;
                try { Display.SetBrightness(m, v); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        briBusy = false;
                        if (err != null) briRow.SetSub("Не удалось: " + err); else briRow.SetSub(m.Internal ? BriSub : BriSubDdc);
                        if (briAgain) { briAgain = false; BriWrite(); }
                    }));
                }
                catch { briBusy = false; }
            });
        }

        void SetMode(Row row, bool resolutionChanged)
        {
            if (loading || mon == null || resBtn.Index < 0) return;
            var r = resBtn.Items[resBtn.Index].Split('×').Select(x => int.Parse(x.Trim())).ToArray();
            int hz;
            if (resolutionChanged) hz = modes.Where(x => x.Width == r[0] && x.Height == r[1]).Select(x => x.Hz).DefaultIfEmpty(60).Max();
            else hz = int.Parse(hzBtn.Items[hzBtn.Index].Replace(" Hz", ""));
            var m = mon;
            Run(row, null, () => Display.SetMode(m, r[0], r[1], hz), true);
        }

        // a setter off the UI thread; an error goes to the row; again: read the monitors anew afterwards
        void Run(Row row, string sub, Action set, bool again = false)
        {
            Task.Run(() =>
            {
                string err = null;
                try { set(); } catch (Exception ex) { err = ex.Message; Store.Log("дисплей: " + row.Title + ": " + err); }
                try { BeginInvoke((Action)(() => { row.SetSub(err != null ? "Не удалось: " + err : sub); if (err != null || again) Reload(); })); } catch { }
            });
        }

        void Read<T>(Func<T> get, Action<T> show, Row row)
        {
            Task.Run(() =>
            {
                T v = default(T); string err = null;
                try { v = get(); } catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => { loading = true; if (err == null) show(v); else row.SetSub("Недоступно: " + err); loading = false; })); } catch { }
            });
        }
    }

    // 20.11 (the user's wish): a monitor whose automatic colour management the user turned off on «Дисплей» keeps it off.
    // Windows turns it on by itself when a monitor gets 10 bits (and again after a new cable, a driver update …); on
    // the Mi Monitor over DisplayPort that gave the picture a red cast (its EDID describes its colours wrongly). Known by
    // the monitor's EDID (maker and product), so it holds on every cable. Checked at the start, after sleep, on every
    // change of the displays and every 20 s (one read of Windows' state per monitor).
    static class ColorKeep
    {
        static System.Threading.Timer timer;
        static Settings settings;
        static int busy;

        static HashSet<string> Keys(Settings s) { return new HashSet<string>((s.AcmOff ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase); }
        public static bool Kept(Settings s, DisplayInfo m) { return m != null && Keys(s).Contains(m.Key); }

        public static void Remember(Settings s, DisplayInfo m, bool on)
        {
            var k = Keys(s);
            if (on) k.Remove(m.Key); else k.Add(m.Key);
            s.AcmOff = string.Join(",", k.OrderBy(x => x)); Store.SaveSettings(s);
        }

        public static void Start(Settings s)
        {
            settings = s;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (o, e) => Later("экраны изменились");
            timer = new System.Threading.Timer(_ => Check("проверка"), null, 3000, 20000);
        }

        public static void Later(string why) { Task.Run(() => { System.Threading.Thread.Sleep(1500); Check(why); }); } // after Windows has settled the new setup

        static void Check(string why)
        {
            var s = settings;
            if (s == null || string.IsNullOrEmpty(s.AcmOff) || System.Threading.Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var keys = Keys(s);
                foreach (var m in Display.All())
                {
                    if (!keys.Contains(m.Key) || Display.AutoColor(m) != true) continue;
                    try { Display.SetAutoColor(m, false); Store.Log("автоуправление цветом: " + m.Title + " снова выключено (" + why + ")"); }
                    catch (Exception ex) { Store.Log("автоуправление цветом: " + m.Title + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Store.Quiet("автоуправление цветом: проверка", ex); }
            finally { busy = 0; }
        }
    }

    // 20.11: the built-in screen's refresh rate follows the power source (Settings.HzAuto): the rate chosen for mains
    // (0: the highest) or for the battery (0: 60, or the lowest above it). At the start, after sleep and whenever the
    // charger comes or goes (MainForm), and when a choice changes on the page.
    static class DisplayAuto
    {
        public static int Pick(List<int> rates, int want, bool mains)
        {
            if (rates.Count == 0) return want;
            if (want > 0 && rates.Contains(want)) return want;
            if (want > 0) return rates.OrderBy(r => Math.Abs(r - want)).First();
            if (mains) return rates.Max();
            return rates.Where(r => r >= 60).DefaultIfEmpty(rates.Max()).Min();
        }

        public static void ApplyLater(Settings s, string why) { Task.Run(() => { try { Apply(s, why); } catch (Exception ex) { Store.Log("частота от питания: " + ex.Message); } }); }

        public static void Apply(Settings s, string why)
        {
            if (s == null || !s.HzAuto) return;
            var inside = Display.All().FirstOrDefault(m => m.Internal);
            if (inside == null) return; // the built-in screen is off (only the monitor)
            var cur = Display.Current(inside); if (cur == null) return;
            var rates = Display.Modes(inside).Where(m => m.Width == cur.Width && m.Height == cur.Height).Select(m => m.Hz).Distinct().ToList();
            bool mains = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
            int want = Pick(rates, mains ? s.HzAc : s.HzDc, mains);
            if (want == cur.Hz) return;
            Display.SetMode(inside, cur.Width, cur.Height, want);
            Store.Log("частота от питания (" + why + "): " + cur.Hz + " → " + want + " Hz, " + (mains ? "от сети" : "от батареи"));
        }
    }
}
