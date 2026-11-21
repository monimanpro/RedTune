using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RedTune
{
    class MainForm : Form
    {
        readonly Settings settings;
        readonly CurveService curve;
        readonly PowerService power;
        FanControl fans;
        readonly KeyService keys = new KeyService();
        readonly Tuner tuner;
        readonly Watchdog watch;
        readonly Dist.Engine dist = new Dist.Engine();
        NavBar nav;
        NavBar.Item[] allNav; // every section; the menu shows the ones NavFits lets through
        readonly Panel host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
        readonly Panel footerHost = new Panel { Dock = DockStyle.Bottom, Height = 0, BackColor = Theme.Card };
        readonly Dictionary<string, Page> pages = new Dictionary<string, Page>();
        readonly NotifyIcon tray = new NotifyIcon();
        PowerLineStatus lastLine = SystemInformation.PowerStatus.PowerLineStatus;
        bool reallyExit;

        public MainForm(Settings settings)
        {
            this.settings = settings;
            curve = new CurveService(settings);
            power = new PowerService();
            Osd.Init(settings);
            Overlay.Init(settings);
            PowerModeWatch.Start(); // 18.11: a change of Windows' power mode made by something else goes to the log
            BrightnessSync.Init(settings); // 18.11: one screen brightness for all power plans, when the user switched it on
            StartDistribution();
            tuner = new Tuner(settings);
            tuner.ActiveValues = () => curve.Active.Values;
            watch = new Watchdog(settings, curve, () => tuner.Running || CoreCurve.Measuring);
            watch.Notify = Warn;
            tuner.Finished = v =>
            {
                var name = "Автотюн " + DateTime.Now.ToString("dd.MM HH:mm");
                curve.Presets.Insert(0, new CurvePreset { Name = name, Values = v });
                curve.SavePresets();
                TunePage.LastResultName = name;
                Warn("Автотюн закончен: пресет «" + name + "» сохранён во вкладке «Кривая»");
            };
            Text = "Red Tune";
            Icon = Program.AppIcon;
            BackColor = Theme.Window; ForeColor = Theme.Text; Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(1000), (int)(wa.Width * 0.95)), Math.Min(Theme.S(720), (int)(wa.Height * 0.95)));
            MinimumSize = new Size(Math.Min(Theme.S(760), wa.Width), Math.Min(Theme.S(520), wa.Height));
            StartPosition = FormStartPosition.CenterScreen;

            allNav = MenuItems();
            nav = new NavBar(allNav.Where(it => NavFits(it.Key)).ToArray()) { Compact = settings.NavCompact }; // only the sections this computer can run (Hw) and, for the risky ones, the expert switch allows
            nav.Selected += ShowPage;
            nav.CompactChanged += SaveNavCompact;
            PageGroups.Show = ShowPage;
            Resize += (s, e) => FitNav();
            Controls.Add(host); Controls.Add(nav);
            FitNav(); // a window that starts narrow (a small screen)
            Hw.ExpertChanged += RebuildNav;
            host.Controls.Add(footerHost);
            BuildTray();
            // safe start: Shift / --safe / a crash soon after the last start's applying → nothing is applied this time (Safety)
            Safety.Begin(Environment.GetCommandLineArgs(), tuner.HasRun && !tuner.State.Paused);
            Safety.Resumed += ApplyAfterSafeStart;
            ShowPage("home");

            if (Hw.Curve) // the curve goes to the SMU of a Ryzen only
                curve.EnsureFirstPreset(() =>
                {
                    if (!Safety.SafeStart && settings.ApplyOnStart && !tuner.HasRun && curve.Presets.Any(p => !p.BuiltIn)) curve.ApplyActive("запуск", ok => { if (!ok) Warn("Кривая не применилась при запуске: " + curve.LastError); });
                });
            SystemEvents.PowerModeChanged += OnPowerMode;
            if (Hw.LenovoPower) power.Start(); // modes and presets come from Lenovo's GameZone
            fans = new FanControl(power, settings);
            if (Hw.Fans) fans.Start(); // the app's own fan curves need the firmware to take RPM
            AmdPower.ApplySaved(settings, "запуск"); // AMD laptops without Lenovo: the saved limits
            if (Hw.AsusFw && !Safety.SafeStart) Asus.ApplySaved(settings); // ASUS: the charge limit and the mode picked here (the firmware does not keep them)
            if (Hw.PageFits("gpu")) { GpuStore.Load(); GpuStore.ApplySaved(settings, "запуск"); } // the NVIDIA curve (the driver forgets it on reboot)
            ColorKeep.Start(settings); // 20.11: automatic colour management stays off where the user turned it off
            if (!Safety.SafeStart) DisplayAuto.ApplyLater(settings, "запуск"); // 20.11: the built-in screen's rate for the power source
            AudioFx.Init(); // 18.11: the sound settings into the file the effect reads, for the devices it is attached to
            keys.Start();
            CoreCurve.RestoreIfLeft(); // a core-curve measurement cut short left the plan's clock cap lowered
            // the flight recorder (14.11): what the cores were doing before a restart that was not a clean one, then on again
            var blackBox = BlackBox.CheckLast();
            // a memory check cut short by a restart says more than the recorder: under it every core is busy
            var memCut = Ram.MemTest.CheckLast();
            if (memCut != null) Warn(memCut);
            else if (blackBox != null) Warn("Перезагрузка без завершения работы. Перед ней работали: " + blackBox);
            // the watchdog takes the cores that were busy, unless an auto-tune was under way (then the restart is the tune's)
            if (blackBox != null && memCut == null && !tuner.HasRun) watch.ResetSeen(BlackBox.LastCores, blackBox);
            BlackBox.Wanted = () => tuner.HasRun || CoreCurve.Measuring || settings.BlackBoxMode == 1 || (settings.BlackBoxMode == 0 && settings.WatchOn);
            BlackBox.Start();
            // 21.11: a new version on GitHub (once a day, if Settings allow): a note from the tray; installed from «О программе»
            Updater.StartDaily(settings, i => { try { BeginInvoke((Action)(() => { tray.BalloonTipTitle = "Red Tune"; tray.BalloonTipText = L.T("Доступна версия ") + i.Version + L.T(" — обновить можно в «О программе»"); tray.BalloonTipIcon = ToolTipIcon.Info; tray.ShowBalloonTip(8000); })); } catch { } });
            Ram.MemTest.Busy = () => tuner.Running;
            Ram.MemTest.CurveNow = () => curve.Active != null ? curve.Active.Values : null;
            Application.ApplicationExit += (s, e) => { Ram.MemTest.Abort(); BlackBox.Stop(); };
            // an auto-tune that was running before a reset or a reboot goes on by itself (not when Shift / --safe asked for quiet)
            if (Hw.Curve && tuner.HasRun && !tuner.State.Paused && !Safety.Manual) { tuner.Resume(true); TuneOnStart = true; }
            if (Safety.SafeStart) Warn("Безопасный запуск: кривая и лимиты не применены (" + Safety.Reason + ")");
            SystemEvents.SessionEnding += (s, e) => { Safety.Stable(); Ram.MemTest.Abort(); BlackBox.Stop(); }; // a sign-out or shutdown in the first minutes is not a crash; and it is a clean end for the recorder
            CheckRamAfterReboot();
            System.Threading.Tasks.Task.Run(() => Lenovo.ApplyPendingIgpu());
            power.CurveWanted += OnCurveWanted;
            power.GpuWanted += OnGpuWanted;
            PowerService.FnQCustom = () => settings.FnQCustom;
            power.ModeChanged += m =>
            {
                if (PowerService.LltRunning) return; // LLT shows its own
                var name = m == Lenovo.Quiet ? "Тихий" : m == Lenovo.Balance ? "Баланс" : m == Lenovo.Performance ? "Макс" : m == Lenovo.Extreme ? "Экстремальный" : "Свой";
                var p = power.Active;
                Osd.Show("power", "Режим питания: " + L.T(name) + (m == Lenovo.Custom && p != null ? " · " + p.Name : ""));
            };
        }

        // every section of the menu, in its order (the off-screen render draws the same menu beside a page: RT_FRAME)
        internal static NavBar.Item[] MenuItems()
        {
            return new[]
            {
                new NavBar.Item("home", "", "Главная"), new NavBar.Item("power", "", "Питание"), new NavBar.Item("curve", "", "Кривая"), new NavBar.Item("gpu", "", "Видеокарта"), new NavBar.Item("ram", "", "Память"),
                // 19.11: the order the user asked for after the cleaning: sound, the device, the distribution, startup, the components, the settings
                // 20.11: «Дисплей» a section of its own after «Звук»; «Очистка» moved under «Звук» (the user's wish); «Устройство» and «Компоненты» are tabs of «Настройки» now, «Распределение» and «Автозагрузка» tabs of «Программы» (PageGroups)
                new NavBar.Item("sound", "\ue767", "Звук"), new NavBar.Item("display", "\ue7f4", "Дисплей"), new NavBar.Item("clean", "\ue74d", "Очистка"), new NavBar.Item("apps", "", "Программы"), new NavBar.Item("settings", "", "Настройки"),
                new NavBar.Item(NavBar.ToggleKey, "", "Свернуть меню", true), new NavBar.Item("about", "", "О программе", true),
            };
        }

        // 20.11: «Видеокарта» is there without the expert functions too where its first row, the graphics mode, can be switched
        bool NavFits(string key) { return key == NavBar.ToggleKey || Hw.PageFits(key) && (settings.Expert || !Hw.Risky(key) || key == "gpu" && GpuModeRow.Available); }

        // 19.11: in a narrow window the menu folds to its icons by itself, as the item «Свернуть меню» does, and gives its
        // width to the page; made wider again, the window gets the menu the user chose (settings.NavCompact). Folding
        // at one width and unfolding a little above it, so the menu does not flicker while the edge is dragged across.
        // The item still works in a narrow window (the menu can be opened over the choice of the moment), but what is
        // clicked there is not kept: the kept choice is the one for a window with room.
        bool narrowNav;

        void FitNav()
        {
            if (nav == null || WindowState == FormWindowState.Minimized) return;
            int w = ClientSize.Width;
            bool narrow = narrowNav ? w < Theme.S(930) : w < Theme.S(900);
            if (narrow == narrowNav) return;
            narrowNav = narrow;
            nav.Compact = narrow || settings.NavCompact;
        }

        void SaveNavCompact(bool compact) { if (narrowNav) return; settings.NavCompact = compact; Store.SaveSettings(settings); }

        // the expert switch changed: the menu again, the open page stays unless it went away
        void RebuildNav()
        {
            var cur = nav.Current;
            var n = new NavBar(allNav.Where(it => NavFits(it.Key)).ToArray()) { Compact = settings.NavCompact || narrowNav };
            n.Selected += ShowPage; n.CompactChanged += SaveNavCompact; n.Current = cur;
            Controls.Remove(nav); nav.Dispose(); nav = n; Controls.Add(nav);
            if (!NavFits(PageGroups.Parent(cur))) ShowPage("home");
        }

        // language switch: re-measure the controls sized by their text and redraw everything
        void Relang()
        {
            Action<Control> walk = null;
            walk = c => { var l = c as ILang; if (l != null) l.Relang(); foreach (Control ch in c.Controls) walk(ch); };
            walk(this);
            // then every row places its re-measured buttons again (hidden pages do it when shown, see Painted)
            Action<Control> lay = null;
            lay = c => { foreach (Control ch in c.Controls) lay(ch); c.PerformLayout(); };
            lay(this);
            foreach (var pg in pages.Values) pg.Relayout();
            Invalidate(true);
        }

        void SetScale(int zoom)
        {
            if (zoom == settings.UiScale) return;
            settings.UiScale = zoom; Store.SaveSettings(settings);
            RestartApp();
        }

        // a new copy waits for this one to end (--after), then starts as usual
        public void RestartApp()
        {
            System.Diagnostics.Process.Start(Application.ExecutablePath, "--after " + System.Diagnostics.Process.GetCurrentProcess().Id);
            ExitApp();
        }

        // Ctrl+Z / Ctrl+Y on «Видеокарта»: a step back or forward in the curve's edits (16.11)
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Page gp;
            if (pages.TryGetValue("gpu", out gp) && gp.Visible && ((GpuPage)gp).HandleKey(keyData)) return true;
            // the same on «Звук»: a step back or forward in the sound settings (19.11)
            if (pages.TryGetValue("sound", out gp) && gp.Visible && ((SoundPage)gp).HandleKey(keyData)) return true;
            // and on «Питание» (20.11): the values of the preset being edited
            if (pages.TryGetValue("power", out gp) && gp.Visible && gp is PowerPage && ((PowerPage)gp).HandleKey(keyData)) return true;
            // and on «Кривая» (20.11): the grid's values and how they are set
            if (pages.TryGetValue("curve", out gp) && gp.Visible && gp is CurvePage && ((CurvePage)gp).HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowPage(string key)
        {
            if (key == "apps") key = PageGroups.Keys("apps")[0]; // «Программы» opens on its first tab
            Page p;
            if (!pages.TryGetValue(key, out p))
            {
                switch (key)
                {
                    case "home": p = new HomePage(power, () => { ShowPage("settings"); var sp = pages["settings"] as SettingsPage; if (sp != null) sp.ShowApps(); }, settings, () => ShowPage("comp")); break;
                    case "curve": p = new CurvePage(curve, tuner, () => ShowPage("tune"), () => ShowPage("watch")); break;
                    case "watch": p = new WatchPage(watch, curve, settings, i => ShowPage(i == 0 ? "curve" : "tune")); break;
                    case "settings": p = new SettingsPage(settings, Relang, SetScale); break;
                    case "comp": p = new ComponentsPage(settings); break;
                    case "about": p = new AboutPage(settings); break;
                    case "power": p = Hw.LenovoPower ? (Page)new PowerPage(power, curve, fans, settings) : new AmdPowerPage(settings); break; // without Lenovo: the AMD limits
                    case "device": p = Hw.AsusFw ? (Page)new AsusDevicePage(settings) : new DevicePage(settings); break; // each firmware has its own page
                    case "sound": p = new SoundPage(settings); break;
                    case "display": p = new DisplayPage(settings); break;
                    case "gpu": p = new GpuPage(settings); break;
                    case "startup": p = new StartupPage(); break;
                    case "clean": p = new CleanPage(settings); break;
                    case "dist": p = new Dist.DistPage(dist); break;
                    case "ram": p = new Ram.RamPage(); break;
                    default: p = new TunePage(tuner, curve, settings, () => ShowPage("curve"), () => ShowPage("watch")); break;
                }
                p.Visible = false;
                pages[key] = p;
                host.Controls.Add(p);
            }
            foreach (var other in pages.Values) if (other != p) other.Visible = false;
            footerHost.Controls.Clear();
            if (p.Footer != null) { footerHost.Controls.Add(p.Footer); footerHost.Height = p.Footer.Height; } else footerHost.Height = 0;
            footerHost.SendToBack(); // docks first, the page fills what is left
            p.Visible = true;
            nav.Current = PageGroups.Parent(key); // the auto-tune and the watchdog live under «Кривая», the device and the components under «Настройки», …
        }

        // ---------- automatic curve ----------

        void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) AmdPower.ApplySaved(settings, "после сна"); // AMD laptops without Lenovo: the limits again
            if (e.Mode == PowerModes.Resume && Hw.PageFits("gpu")) GpuStore.ApplySaved(settings, "после сна");
            if (e.Mode == PowerModes.Resume) { DisplayAuto.ApplyLater(settings, "после сна"); ColorKeep.Later("после сна"); } // 20.11: the screen's rate for the power source
            if (e.Mode == PowerModes.Resume && settings.ApplyOnResume) Later(8000, "после сна");
            else if (e.Mode == PowerModes.StatusChange)
            {
                var line = SystemInformation.PowerStatus.PowerLineStatus;
                if (line == lastLine) return;
                lastLine = line;
                if (!PowerService.LltRunning) Osd.Show("ac", line == PowerLineStatus.Online ? "Зарядка подключена" : "Зарядка отключена");
                DisplayAuto.ApplyLater(settings, line == PowerLineStatus.Online ? "зарядка" : "батарея");
                if (line == PowerLineStatus.Online && settings.ApplyOnAc) Later(3000, "зарядка");
                else if (line == PowerLineStatus.Offline && settings.ApplyOnBattery) Later(3000, "батарея");
            }
        }

        void Later(int ms, string source)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) =>
            {
                t.Dispose();
                if (!curve.Presets.Any(p => !p.BuiltIn) || tuner.Running || Safety.SafeStart || !Hw.Curve) return; // the auto-tune sets its own values; safe start applies nothing
                curve.ApplyActive(source, ok => { if (!ok) Warn("Кривая не применилась (" + source + "): " + curve.LastError); else Osd.Show("curve", "Кривая «" + curve.Active.Name + "» применена"); });
            };
            t.Start();
        }

        // a power mode or preset with its own «Видеокарта» preset: it goes to the graphics card (13.11). Like the page itself it
        // writes only with «Экспертные функции» on, and nothing on a safe start.
        string gpuApplying;
        void OnGpuWanted(string name)
        {
            if (!Hw.Nvidia || !settings.Expert || Safety.SafeStart || gpuApplying == name) return;
            GpuStore.Load();
            var p = GpuStore.Presets.FirstOrDefault(x => x.Name == name);
            if (p == null) { Store.Log("режим питания: нет пресета видеокарты «" + name + "»"); return; }
            gpuApplying = name;
            System.Threading.Tasks.Task.Run(() =>
            {
                var c = Gpu.Read();
                string e = c == null ? (Gpu.Error ?? "кривая не прочиталась") : p.Delta.Length != c.Count ? "пресет снят с другой кривой"
                    : c.Delta.SequenceEqual(p.Delta) && (Gpu.ReadMemory() ?? new[] { p.Memory })[0] == p.Memory ? "" // already on the card
                    : Gpu.WriteAll(p.Delta, p.Memory);
                Store.Log("режим питания: пресет видеокарты «" + name + "» " + (e == null ? "применён" : e == "" ? "уже стоит" : e));
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        gpuApplying = null;
                        // written, or found on the card already: either way it is the preset the card runs now
                        if ((e == null || e == "") && GpuStore.Active != name) { GpuStore.Active = name; GpuStore.Save(); }
                        else if (e != "") Warn("Пресет видеокарты «" + name + "» не применился: " + e);
                    }));
                }
                catch { gpuApplying = null; } // no window to post to: the next request for this preset must still go through
            });
        }

        // a power mode or preset with its own curve: that curve becomes the active one and goes to the CPU
        string curveApplying;
        void OnCurveWanted(string name) { ApplyCurveNamed(name, "режим питания"); }

        // source: who asked, for the history of the curve («режим питания», «мини-панель»)
        void ApplyCurveNamed(string name, string source)
        {
            var p = curve.Presets.FirstOrDefault(x => x.Name == name);
            if (p == null) { Store.Log(source + ": нет пресета кривой «" + name + "»"); return; }
            if (tuner.Running || Safety.SafeStart) return; // the auto-tune sets its own values; safe start applies nothing
            if (curveApplying == name) return; // the same request twice (preset + the mode event it causes)
            if (name == settings.ActivePreset && curve.Live != null && curve.Live.SequenceEqual(p.Values)) return;
            settings.ActivePreset = name; Store.SaveSettings(settings);
            curveApplying = name;
            curve.ApplyActive(source, ok =>
            {
                curveApplying = null;
                if (ok) Osd.Show("curve", "Кривая «" + name + "» применена"); else Warn("Кривая «" + name + "» не применилась: " + curve.LastError);
            });
        }

        // «Применить сейчас» after a safe start: what the start would have applied
        void ApplyAfterSafeStart()
        {
            if (Hw.Curve && settings.ApplyOnStart && !tuner.Running && curve.Presets.Any(p => !p.BuiltIn))
                curve.ApplyActive("после безопасного запуска", ok => { if (!ok) Warn("Кривая не применилась: " + curve.LastError); else Osd.Show("curve", "Кривая «" + curve.Active.Name + "» применена"); });
            AmdPower.ApplySaved(settings, "после безопасного запуска");
            if (Hw.PageFits("gpu")) GpuStore.ApplySaved(settings, "после безопасного запуска");
            if (Hw.LenovoPower && power.Mode == Lenovo.Custom && power.Active != null) power.Apply(power.Active);
        }

        void Warn(string text)
        {
            tray.BalloonTipTitle = "Red Tune"; tray.BalloonTipText = L.T(text); tray.BalloonTipIcon = ToolTipIcon.Warning;
            tray.ShowBalloonTip(5000);
        }

        // ---------- tray ----------

        void BuildTray()
        {
            tray.Icon = Program.AppIcon; tray.Text = "Red Tune"; tray.Visible = true;
            var m = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body };
            m.Items.Add("Открыть", null, (s, e) => ShowFromTray());
            // 18.11: only «Открыть» and «Выход» (the user's wish); the curve is applied from its page and the mini panel
            m.Items.Add("Выход", null, (s, e) => ExitApp());
            tray.ContextMenuStrip = m;
            // 16.11: a left click opens the mini panel by the icon (it used to open the whole window: that is now a
            // double click or this menu)
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMini(); };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (mini != null) mini.Hide(); ShowFromTray(); } };
        }

        // memory settings written before a reboot: did they all take effect? (the page shows the same when opened first)
        void CheckRamAfterReboot()
        {
            Ram.RamCore.ImportOld();
            if (!Ram.RamCore.HasPending) return;
            var t = new Timer { Interval = 20000 };
            t.Tick += (s, e) =>
            {
                t.Dispose();
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    Tuple<bool, string> r = null;
                    try { if (Ram.RamCore.HasPending) r = Ram.RamState.Refresh(); } catch { }
                    if (r != null) try { BeginInvoke((Action)(() => Warn(r.Item2))); } catch { }
                });
            };
            t.Start();
        }

        // programs on their chips (was the separate Distribution app; its rules now live in %APPDATA%\RedTune\Distribution)
        void StartDistribution()
        {
            Dist.Store.MoveOld();
            try
            {
                var rules = Dist.Store.LoadRules(dist.BackGroup);
                if (!System.IO.File.Exists(Dist.Store.RulesPath)) Dist.Store.SaveRules(rules);
                dist.Apply(rules, Dist.Store.LoadSettings());
                dist.Start();
                Dist.Store.Log("запуск в Red Tune: " + (dist.TwoChips ? "2 чипа" : "один чип") + ", CCD1 0x" + dist.MaskCCD1.ToString("X") + ", CCD2 0x" + dist.MaskCCD2.ToString("X"));
            }
            catch (Exception ex) { Dist.Store.Log("не запустилось: " + ex.Message); }
        }

        MiniBlocks mini;
        // from the tray icon and from RedTune.exe --mini. 16.11: one of the two windows at a time: the mini panel opening
        // sends the full window to the tray, the full window opening closes the panel (a click on the icon with the
        // full window open used to put the panel beside it)
        public void ToggleMini() { if (Mini.Toggle() && Visible) Hide(); }
        // 17.11: the panel as separate blocks with Windows' own blur (MiniBlocks). The one before it, with glass of its
        // own making (MiniPanel: round blocks, frost and bending, but a frame or two behind the screen), was removed
        // from the source on 20.11 (it is in the git history)
        MiniBlocks Mini { get { return mini ?? (mini = new MiniBlocks(settings, power, curve, SetModeFromMini, n => ApplyCurveNamed(n, "мини-панель"), OnGpuWanted, () => gpuApplying, () => curveApplying)); } }

        // a mode picked in the mini panel: as the buttons of «Главная» do it (the firmware sends no event for a mode set
        // by a program, so the mode's plan, curve and graphics card preset are applied through Notify)
        void SetModeFromMini(int m)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Lenovo.SetPowerMode(m); Store.Log("режим питания -> " + m + " (мини-панель)"); power.Notify(); }
                catch (Exception ex) { Store.Log("мини-панель: режим питания: " + ex.Message); }
            });
        }

        // 18.11: a tune goes on after a reset: the window opens on the auto-tune tab, so that what it is doing is seen at once
        // (an ordinary start in the tray stays hidden)
        public bool TuneOnStart;
        public void ShowTune() { ShowPage("tune"); ShowFromTray(); WindowState = FormWindowState.Maximized; } // on the whole screen: the progress is read from across the room

        public void ShowFromTray()
        {
            if (mini != null && mini.Visible) mini.Hide();
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void ExitApp()
        {
            reallyExit = true;
            Safety.Stable(); // a normal exit is not a crash
            SystemEvents.PowerModeChanged -= OnPowerMode;
            tray.Visible = false;
            if (tuner.Running) tuner.Pause();
            keys.Dispose(); power.Dispose(); BrightnessSync.Stop(); PowerModeWatch.Stop();
            if (fans != null) fans.Dispose(); // the firmware drives the fans again
            try { dist.Stop(); dist.ReleaseAll(); } catch { }
            Close();
            Application.Exit();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && settings.CloseToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            if (!reallyExit) { reallyExit = true; SystemEvents.PowerModeChanged -= OnPowerMode; tray.Visible = false; if (fans != null) fans.Dispose(); Application.Exit(); }
            base.OnFormClosing(e);
        }
    }

    // left menu: logo and name on top, pages, "О программе" at the bottom
    class NavBar : Painted
    {
        public class Item
        {
            public string Key, Glyph, Text; public bool Bottom;
            public Item(string key, string glyph, string text, bool bottom = false) { Key = key; Glyph = glyph; Text = text; Bottom = bottom; }
        }

        readonly Item[] items;
        readonly Font glyphFont;
        Rectangle[] rects = new Rectangle[0];
        string current = "";
        int hot = -1;
        public event Action<string> Selected;

        // 13.11: the menu folds to its icons (the item at the bottom, «Свернуть меню»); the names then show on hover.
        // The choice is kept in the settings (NavCompact).
        public const string ToggleKey = "*fold";
        public event Action<bool> CompactChanged;
        bool compact;
        public bool Compact
        {
            get { return compact; }
            set { if (compact == value) return; compact = value; Width = Theme.S(value ? 64 : 196); GlassHint.Off(); PerformLayout(); Invalidate(); }
        }

        public NavBar(Item[] items)
        {
            this.items = items;
            Dock = DockStyle.Left; Width = Theme.S(196); BackColor = Theme.Nav;
            var family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            glyphFont = new Font(family, 11f * Theme.Body.Size / 9.75f);
        }

        string Label(Item it) { return it.Key == ToggleKey ? (compact ? "Развернуть меню" : "Свернуть меню") : it.Text; }
        string Glyph(Item it) { return it.Key == ToggleKey ? (compact ? "" : "") : it.Glyph; }

        // the folded menu: the item's name beside it while the mouse is on it
        void ShowTip(int i)
        {
            if (!compact || i < 0) { GlassHint.Off(); return; }
            var form = FindForm();
            if (form != null && !hooked) { hooked = true; form.Deactivate += (s, e) => GlassHint.Off(); }
            GlassHint.Beside(RectangleToScreen(rects[i]), L.T(Label(items[i])));
        }
        bool hooked;

        protected override void Dispose(bool disposing) { if (disposing) GlassHint.Off(); base.Dispose(disposing); }

        public string Current { get { return current; } set { current = value; Invalidate(); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (items == null) return; // the base constructor lays out before the items are set
            int h = Theme.S(38), y = Theme.S(70), yb = Height - Theme.S(12) - h;
            rects = new Rectangle[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Bottom) { rects[i] = new Rectangle(Theme.S(10), yb, Width - Theme.S(20), h); yb -= h + Theme.S(2); }
                else { rects[i] = new Rectangle(Theme.S(10), y, Width - Theme.S(20), h); y += h + Theme.S(2); }
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = Array.FindIndex(rects, r => r.Contains(e.Location));
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            if (i != hot) { hot = i; Invalidate(); ShowTip(i); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); GlassHint.Off(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Array.FindIndex(rects, r => r.Contains(e.Location));
            if (i >= 0 && items[i].Key == ToggleKey)
            {
                Compact = !compact; hot = -1;
                var c = CompactChanged; if (c != null) c(compact);
            }
            else if (i >= 0) { var h = Selected; if (h != null) h(items[i].Key); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int k = Theme.S(34);
            Logo.DrawChip(g, new RectangleF(Theme.S(16), Theme.S(18), k, k), Theme.Text); // the comic stays in «О программе»
            // the name: «Red» in the accent, «Tune» in white
            int nx = Theme.S(16) + k + Theme.S(10);
            var nameFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int redW = TextRenderer.MeasureText(g, "Red ", Theme.Title, new Size(int.MaxValue, k), nameFlags).Width;
            if (!compact)
            {
                TextRenderer.DrawText(g, "Red", Theme.Title, new Rectangle(nx, Theme.S(18), Width, k), Theme.AccentHover, nameFlags);
                TextRenderer.DrawText(g, "Tune", Theme.Title, new Rectangle(nx + redW, Theme.S(18), Width, k), Theme.Text, nameFlags);
            }
            for (int i = 0; i < items.Length && i < rects.Length; i++)
            {
                var r = rects[i];
                bool on = items[i].Key == current;
                if (on || i == hot) Theme.Fill(g, r, Theme.S(6), on ? Theme.Raised : Color.FromArgb(0x24, 0x23, 0x21));
                if (on) Theme.Fill(g, new RectangleF(r.X, r.Y + Theme.S(10), Theme.S(3), r.Height - Theme.S(20)), Theme.S(1.5f), Theme.Accent);
                var fg = on ? Theme.Text : Theme.Text2;
                // the icon font sits its glyphs high in the line: the icon's middle should meet the middle of the label from
                // the top of its capital to the baseline (where the eye puts a line of text). Measured 13.11: 2 px above it as
                // drawn, so it goes 2 px down (a first try at 4 px put it 3 px below, «Настройки» looked low)
                int nudge = (int)Math.Round(1.6f * Theme.Scale);
                // 16.11: «Память», «Распределение» and «Устройство» have icons of their own, drawn here (the icon font has no
                // memory module, and its grid and its «device» said little); the rest are glyphs of the font
                if (!DrawOwn(g, items[i].Key, new RectangleF(r.X + Theme.S(12), r.Y, Theme.S(22), r.Height), fg))
                TextRenderer.DrawText(g, Glyph(items[i]), glyphFont, new Rectangle(r.X + Theme.S(12), r.Y + nudge, Theme.S(22), r.Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                if (!compact) Theme.Text1(g, Label(items[i]), Theme.Body, new Rectangle(r.X + Theme.S(44), r.Y, r.Width - Theme.S(48), r.Height), fg);
            }
        }

        // The icons drawn by hand, in the thin outline of the icon font: a 20 × 20 grid in the middle of the box, lines
        // of about 1.2 px at 100 %. false: this section has a glyph of the font.
        //   ram     a memory module: the board, three chips on it, the row of contacts under it with the key notch
        //   dist    one block on the left feeding two on the right: programs sent to one chiplet or the other
        //   device  a laptop: the screen and the base under it
        //   gpu     a graphics card seen from the side: the bracket, the body with heatsink fins and a fan, the slot connector
        static bool DrawOwn(Graphics g, string key, RectangleF box, Color c)
        {
            if (key != "ram" && key != "dist" && key != "device" && key != "gpu") return false;
            float u = Theme.S(18) / 20f, ox = box.X + (box.Width - 20 * u) / 2f, oy = box.Y + (box.Height - 20 * u) / 2f;
            Func<float, float> X = v => ox + v * u, Y = v => oy + v * u;
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var pen = new Pen(c, Math.Max(1f, 1.2f * Theme.Scale)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var fill = new SolidBrush(c))
            {
                Action<float, float, float, float, float> box2 = (x0, y0, x1, y1, rad) =>
                {
                    float d = 2 * rad * u;
                    using (var p = new GraphicsPath())
                    {
                        if (d < 0.5f) p.AddRectangle(RectangleF.FromLTRB(X(x0), Y(y0), X(x1), Y(y1)));
                        else
                        {
                            p.AddArc(X(x0), Y(y0), d, d, 180, 90); p.AddArc(X(x1) - d, Y(y0), d, d, 270, 90);
                            p.AddArc(X(x1) - d, Y(y1) - d, d, d, 0, 90); p.AddArc(X(x0), Y(y1) - d, d, d, 90, 90);
                            p.CloseFigure();
                        }
                        g.DrawPath(pen, p);
                    }
                };
                if (key == "ram")
                {
                    // 16.11, second try (the first had the contacts hanging under the board like hairs): the whole module
                    // in one outline with a straight bottom edge, the contacts inside it along that edge with the key
                    // notch in the middle, and the chips as outlines above them (the user's picture of a module)
                    // the size and the line weight of the icons beside it (the first drawing of this one was a size too
                    // big and heavy next to «Видеокарта»)
                    using (var thin = new Pen(c, Math.Max(1f, 0.9f * Theme.Scale)) { LineJoin = LineJoin.Round })
                    {
                        using (var board = new GraphicsPath())
                        {
                            float d = 2 * 1.1f * u, x0 = X(2f), y0 = Y(5.5f), x1 = X(18f), y1 = Y(14.5f);
                            board.AddArc(x0, y0, d, d, 180, 90); board.AddArc(x1 - d, y0, d, d, 270, 90);
                            board.AddArc(x1 - d, y1 - d, d, d, 0, 90); board.AddArc(x0, y1 - d, d, d, 90, 90);
                            board.CloseFigure();
                            g.DrawPath(thin, board);
                        }
                        foreach (var x in new[] { 3.9f, 8.4f, 12.9f })                    // the chips
                            g.DrawRectangle(thin, X(x), Y(7.2f), 3.2f * u, 2.8f * u);
                        foreach (var x in new[] { 4.2f, 6f, 7.8f, 12.2f, 14f, 15.8f })    // the contacts, up from the bottom edge
                            g.DrawLine(thin, X(x), Y(12.3f), X(x), Y(14.5f));
                        g.DrawArc(thin, X(9.2f), Y(13.2f), 1.6f * u, 2.6f * u, 180, 180); // the key notch
                    }
                }
                else if (key == "gpu")
                {
                    using (var thin = new Pen(c, Math.Max(1f, 0.9f * Theme.Scale)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(thin, X(2f), Y(4f), X(2f), Y(16.5f));                                  // the bracket
                        using (var body = new GraphicsPath())
                        {
                            float d = 2 * 1.1f * u, x0 = X(3.6f), y0 = Y(5.2f), x1 = X(18.6f), y1 = Y(13.4f);
                            body.AddArc(x0, y0, d, d, 180, 90); body.AddArc(x1 - d, y0, d, d, 270, 90);
                            body.AddArc(x1 - d, y1 - d, d, d, 0, 90); body.AddArc(x0, y1 - d, d, d, 90, 90);
                            body.CloseFigure();
                            g.DrawPath(thin, body);
                        }
                        // one fan and the fins of the heatsink beside it (two fans with their hubs looked like a pair of eyes)
                        g.DrawEllipse(thin, X(14.3f - 2.7f), Y(9.3f - 2.7f), 5.4f * u, 5.4f * u);
                        g.FillEllipse(fill, X(14.3f - 0.75f), Y(9.3f - 0.75f), 1.5f * u, 1.5f * u);
                        foreach (var fy in new[] { 7.5f, 9.3f, 11.1f }) g.DrawLine(thin, X(5.6f), Y(fy), X(9.4f), Y(fy));
                        g.DrawLines(thin, new[] { new PointF(X(5f), Y(13.4f)), new PointF(X(5f), Y(15.6f)), new PointF(X(12.5f), Y(15.6f)), new PointF(X(12.5f), Y(13.4f)) }); // the slot connector
                    }
                }
                else if (key == "dist")
                {
                    box2(1.5f, 7.5f, 6.5f, 12.5f, 1f);                                    // where the programs come from
                    box2(13.5f, 2f, 18.5f, 7f, 1f); box2(13.5f, 13f, 18.5f, 18f, 1f);     // the two chiplets
                    g.DrawLines(pen, new[] { new PointF(X(6.5f), Y(10f)), new PointF(X(10f), Y(10f)), new PointF(X(10f), Y(4.5f)), new PointF(X(13.5f), Y(4.5f)) });
                    g.DrawLines(pen, new[] { new PointF(X(10f), Y(10f)), new PointF(X(10f), Y(15.5f)), new PointF(X(13.5f), Y(15.5f)) });
                }
                else
                {
                    box2(4f, 4f, 16f, 12.5f, 1.2f);                                       // the screen
                    g.DrawLine(pen, X(1.5f), Y(15.5f), X(18.5f), Y(15.5f));               // the base
                    g.DrawLine(pen, X(8.5f), Y(15.5f), X(11.5f), Y(15.5f));
                }
            }
            g.Restore(st);
            return true;
        }
    }
}
