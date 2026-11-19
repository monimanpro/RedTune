using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    class HomePage : Page
    {
        static readonly int[] Modes = { Lenovo.Quiet, Lenovo.Balance, Lenovo.Performance, Lenovo.Custom };
        // 13.11: «Свой» is a switch of its own, so in a narrow window it goes to the next line together with the preset
        // choice; it stands 4 px from the others and has their width, so on one line it still looks like one switch
        readonly Segmented mode = new Segmented("Тихий", "Баланс", "Макс");
        readonly Segmented custom = new Segmented("Свой");
        readonly Row modeRow;
        readonly DropButton presetDrop = new DropButton(); // the custom-mode presets, shown in the custom mode
        readonly StatCard cpu = new StatCard(), gpu = new StatCard(), ram = new StatCard();
        // 16.11: a card per drive under the memory card
        readonly Disks disks = new Disks();
        StatCard[] drives = new StatCard[0];
        readonly Sensors sensors = new Sensors();
        readonly Timer timer = new Timer { Interval = 1500 };
        bool reading;
        int shownMode = -1;
        // the CPU limit is re-read every 3 s for 12 s after a mode or preset change (the firmware applies it a moment later), else every 30 s
        int limitMode = -2;
        DateTime limitKick = DateTime.Now;

        readonly PowerService power;

        readonly Row appsRow;
        DateTime appsChecked = DateTime.MinValue;
        bool appsChecking;

        public HomePage(PowerService power, Action openApps, Settings settings = null, Action openComp = null)
        {
            this.power = power;
            // 13.11: on a model nobody tried yet, one quiet line that says so and leads to what works and what does not
            if (settings != null && !Hw.VerifiedModel && !settings.UnverifiedSeen)
            {
                var more = new FlatButton("Подробнее"); var hide = new FlatButton("Скрыть");
                var row = Row("Эта модель ещё не проверена", "Что здесь работает, а что не проверено — в «Компонентах»", more, hide);
                more.Click += (s, e) => { if (openComp != null) openComp(); };
                hide.Click += (s, e) => { settings.UnverifiedSeen = true; Store.SaveSettings(settings); row.Visible = false; Relayout(); };
            }
            // safe start (Safety): what was not applied and why, with the button that applies it now
            var applyNow = new FlatButton("Применить сейчас");
            var safeRow = Row("Безопасный запуск", "Кривая и лимиты не применены: " + Safety.Reason, applyNow);
            safeRow.WarnStyle = true; applyNow.BackColor = Theme.WarningBg;
            safeRow.Visible = Safety.SafeStart;
            applyNow.Click += (s, e) => { Safety.Resume(); safeRow.Visible = false; Relayout(); };
            // other programs that fight over the same settings: a warning that leads to Settings → «Другие программы»
            var fix = new FlatButton("Разобраться");
            appsRow = Row("Мешают другие программы", "", fix);
            appsRow.WarnStyle = true; fix.BackColor = Theme.WarningBg;
            appsRow.Visible = false; appsRow.Cursor = Cursors.Hand;
            appsRow.Click += (s, e) => openApps();
            fix.Click += (s, e) => openApps();
            modeRow = Row("Режим питания", "Fn+Q переключает по кругу", mode, custom, presetDrop);
            modeRow.SetGapBefore(custom, Theme.S(4));
            modeRow.KeepWithPrevious(presetDrop);
            custom.MinItemWidth = mode.ItemWidth;
            mode.SizeChanged += (s, e) => custom.MinItemWidth = mode.ItemWidth; // another language
            modeRow.Visible = Hw.LenovoPower; // power modes come from Lenovo's firmware interface
            presetDrop.Visible = false;
            presetDrop.Changed += (s, e) =>
            {
                var pr = power.Data.Presets[presetDrop.Index];
                modeRow.SetSub("Применяю «" + pr.Name + "»…");
                power.Apply(pr, ok => { shownMode = -1; limitKick = DateTime.Now; Tick(); if (!ok) modeRow.SetSub("Не удалось: " + power.LastError); });
            };
            power.Changed += () => { shownMode = -1; FillPresets(); };
            FillPresets();
            mode.Changed += (s, e) => { custom.Selected = -1; SetMode(Modes[mode.Selected]); };
            custom.Changed += (s, e) => { mode.Selected = -1; SetMode(Lenovo.Custom); };
            Add(cpu, 12); Add(gpu); Add(ram);
            drives = disks.List.Select(d => Add(new StatCard())).ToArray();
            cpu.Title = "Процессор"; gpu.Title = "Видеокарта"; ram.Title = "Память";
            // 13.11: the graphics card reads through NVIDIA's driver only. A laptop with Radeon graphics alone (most
            // thin AMD laptops) gets no card instead of a card of dashes; a Radeon discrete card says why it is empty.
            gpu.Visible = Nvidia || RadeonDiscrete != null;
            Fill();
            timer.Tick += (s, e) => Tick();
            VisibleChanged += (s, e) => { if (Visible) { Tick(); CheckApps(true); timer.Start(); } else timer.Stop(); };
            timer.Tick += (s, e) => CheckApps(false);
        }

        // every 15 s while the page is open (the task list query is slow), right away when it opens
        void CheckApps(bool now)
        {
            if (appsChecking || (!now && (DateTime.Now - appsChecked).TotalSeconds < 15)) return;
            appsChecking = true; appsChecked = DateTime.Now;
            Task.Run(() =>
            {
                var bad = new System.Collections.Generic.List<string>();
                foreach (var a in OtherApp.All())
                    try { if (!a.Relevant()) continue; a.Check(); if (a.Found && (a.Running || a.Autostart)) bad.Add(a.Name + (a.Running && a.Autostart ? " запущена и стартует с Windows" : a.Running ? " запущена" : " стартует с Windows")); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        appsChecking = false;
                        bool show = bad.Count > 0;
                        if (show) appsRow.SetSub(string.Join(" · ", bad) + " — спорят с Red Tune за те же настройки");
                        if (appsRow.Visible != show) { appsRow.Visible = show; Relayout(); }
                    }));
                }
                catch { appsChecking = false; }
            });
        }

        void SetMode(int m)
        {
            Task.Run(() =>
            {
                string err = null;
                // Notify: the firmware sends no event for a mode set by a program, so the mode's plan, curve and graphics
                // card preset are applied from here (16.11)
                try { Lenovo.SetPowerMode(m); Store.Log("режим питания -> " + m); power.Notify(); } catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)(() => { if (err != null) modeRow.SetSub("Не удалось: " + err); Tick(); }));
            });
        }

        void Tick()
        {
            if (reading || !IsHandleCreated) return;
            reading = true;
            Task.Run(() =>
            {
                int m = -1;
                try { sensors.Update(true); } catch { }
                try { disks.Update(); } catch { }
                try { m = Lenovo.GetPowerMode(); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        reading = false; ShowMode(m); Fill();
                        if (m != limitMode) { limitMode = m; limitKick = DateTime.Now; }
                        CpuLimits.Refresh((DateTime.Now - limitKick).TotalSeconds < 12 ? 3 : 30);
                    }));
                }
                catch { reading = false; }
            });
        }

        void ShowMode(int m)
        {
            if (m == shownMode) return;
            shownMode = m;
            int i = Array.IndexOf(Modes, m);
            mode.Selected = i >= 0 && i < 3 ? i : -1;
            custom.Selected = i == 3 ? 0 : -1;
            presetDrop.Visible = m == Lenovo.Custom;
            FillPresets();
            var p = power.Active;
            modeRow.SetSub(m == Lenovo.Extreme ? "Сейчас: Экстремальный · Fn+Q переключает по кругу" : m < 0 ? "Не удалось прочитать режим" : "Fn+Q переключает по кругу");
        }

        void FillPresets()
        {
            var names = power.Data.Presets.Select(x => x.Name).ToArray();
            presetDrop.Set(names, Array.IndexOf(names, power.Active != null ? power.Active.Name : ""));
        }

        static bool Nvidia { get { return Hw.Gpus.Any(n => n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0); } }
        // a Radeon RX / Pro card besides the processor's own graphics, or null
        static string RadeonDiscrete { get { return Hw.Gpus.FirstOrDefault(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"Radeon\s+(RX|Pro)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)); } }

        static string Size(ulong bytes) { return bytes >= 1e12 * 0.95 ? N(bytes / 1e12, "0.#") + " ТБ" : N(bytes / 1e9, "0") + " ГБ"; }
        static string Speed(double bps) { return double.IsNaN(bps) ? "—" : bps >= 1e9 ? N(bps / 1e9, "0.0") + " ГБ/с" : N(bps / 1e6, "0") + " МБ/с"; }
        static string N(double v, string fmt) { return double.IsNaN(v) ? "—" : v.ToString(fmt, CultureInfo.GetCultureInfo("ru-RU")); }
        static StatCard.Metric M(string name, string value, double fraction, bool warn = false) { return new StatCard.Metric { Name = name, Value = value, Fraction = fraction, Warn = warn }; }

        void Fill()
        {
            var c = sensors.Cpu;
            bool fans = Hw.Features; // fan speeds come from Lenovo's interface; elsewhere there is nothing to show
            cpu.Subtitle = c.Name + (c.Cores > 0 ? " · " + c.Cores + " ядер / " + c.Threads + " потока" : "");
            // 16.11: the load is a bar on the title's line (StatCard.Head), the rest as before
            var cpuLoad = M("Загрузка", N(c.LoadPct, "0") + " %", c.LoadPct / 100);
            var cpuClock = M("Частота", N(c.ClockMHz / 1000, "0.0") + " ГГц", c.ClockMHz / 5400);
            var cpuTemp = M(sensors.TempFromAcpi ? "Температура (ACPI)" : "Температура", c.TempC > 0 ? c.TempC + " °C" : "—", c.TempC / 100.0, c.TempC >= 85);
            var cpuPower = M("Мощность", N(c.PowerW, "0") + (double.IsNaN(c.LimitW) ? "" : " / " + N(c.LimitW, "0")) + " W", c.PowerW / (double.IsNaN(c.LimitW) ? 65 : c.LimitW));
            cpu.Head = cpuLoad;
            cpu.Metrics = new[] { cpuClock, cpuTemp, cpuPower }.ToList();
            if (fans) cpu.Metrics.Add(M("Вентилятор", c.FanRpm >= 0 ? c.FanRpm + " об/мин" : "—", c.FanRpm / 5500.0));
            cpu.Fit();

            var g = sensors.Gpu;
            if (!Nvidia)
            {
                // a Radeon discrete card: named, with why there are no numbers
                gpu.Subtitle = RadeonDiscrete ?? ""; gpu.Note = "показания Radeon пока не поддерживаются";
                gpu.Head = null; gpu.Metrics = new System.Collections.Generic.List<StatCard.Metric>(); gpu.Fit();
                goto Memory;
            }
            gpu.Subtitle = (g.Name.Length > 0 ? g.Name : "NVIDIA") + (g.VramTotal > 0 ? " · " + N(g.VramTotal / 1073741824.0, "0") + " ГБ" : "");
            gpu.Note = g.Ok ? "" : g.Note;
            // speeds on the left, temperature / fan / how full on the right; the load on the title's line
            var gLoad = M("Загрузка", g.Ok ? g.LoadPct + " %" : "—", g.Ok ? g.LoadPct / 100.0 : 0);
            var gClock = M("Частота ядра", g.Ok ? g.ClockMHz + " МГц" : "—", g.MaxClockMHz > 0 ? (double)g.ClockMHz / g.MaxClockMHz : 0);
            var gTemp = M("Температура", g.TempC > 0 ? g.TempC + " °C" : "—", g.TempC / 100.0, g.TempC >= 85);
            var gPower = M("Мощность", g.Ok && !double.IsNaN(g.PowerW) ? N(g.PowerW, "0") + (double.IsNaN(g.LimitW) ? "" : " / " + N(g.LimitW, "0")) + " W" : "—", g.LimitW > 0 ? g.PowerW / g.LimitW : 0);
            var gMem = M("Частота памяти", g.Ok ? g.MemClockMHz + " МГц" : "—", g.MaxMemClockMHz > 0 ? (double)g.MemClockMHz / g.MaxMemClockMHz : 0);
            var gVram = M("Видеопамять", g.VramTotal > 0 ? N(g.VramUsed / 1073741824.0, "0.0") + " / " + N(g.VramTotal / 1073741824.0, "0") + " ГБ" : "—", g.VramTotal > 0 ? (double)g.VramUsed / g.VramTotal : 0);
            gpu.Head = gLoad;
            gpu.Metrics = fans
                ? new[] { gClock, gTemp, gPower, M("Вентилятор", g.FanRpm >= 0 ? g.FanRpm + " об/мин" : "—", g.FanRpm / 5500.0), gMem, gVram }.ToList()
                : new[] { gClock, gTemp, gMem, gVram, gPower }.ToList();
            gpu.Fit();

            Memory:
            var r = sensors.Ram;
            double gb = 1073741824.0;
            // 16.11: the makers after the type, the modules themselves in their own cards below; the clock bar is full (it is
            // the clock the memory runs at; how it stands to the module's rating is in the module cards)
            ram.Subtitle = r.Type + (r.Slots.Count > 0 ? " · " + string.Join(" / ", r.Slots.Select(s => s.Maker.Length > 0 ? s.Maker : N(s.Bytes / gb, "0") + " ГБ")) : "");
            var list = new[]
            {
                M("Частота", r.SpeedMHz > 0 ? r.SpeedMHz + " МГц" : "—", r.SpeedMHz > 0 ? 1 : 0),
                M("Занято", N(r.Used / gb, "0.0") + " / " + N(r.Total / gb, "0") + " ГБ", r.Total > 0 ? (double)r.Used / r.Total : 0, r.Total > 0 && r.Used > r.Total * 0.9),
            }.ToList();
            ram.Metrics = list;
            // 16.11: the modules' own sensors, on the title's line over «Занято»: each module in the order of the names
            // in the subtitle. 85 °C is where a DDR5 module's normal range ends
            int ramH = ram.Height;
            if (r.Temps.Length > 0)
            {
                float hot = r.Temps.Max();
                ram.Head = M("Температура", string.Join(" / ", r.Temps.Select(t => N(Math.Round(t), "0"))) + " °C", hot / 85.0, hot >= 80);
            }
            else ram.Head = null;
            ram.Fit();
            if (ram.Height != ramH) Relayout(); // the line appeared (or went): the cards below move
            // the drives: the volumes' fill, temperature against the drive's own limit, read and write speed
            for (int i = 0; i < drives.Length && i < disks.List.Count; i++)
            {
                var d = disks.List[i]; var k = drives[i];
                double top = d.Nvme ? 3500e6 : 550e6; // the bars: a fast NVMe and SATA drive
                k.Title = d.Kind + (d.Letters.Length > 0 ? " " + d.Letters : "");
                k.Subtitle = string.Join(" · ", new[] { d.Model, Size(d.Size), d.Nvme ? "NVMe" : "", d.Wear >= 0 ? "износ " + d.Wear + " %" : "" }.Where(x => x.Length > 0));
                k.Metrics = new[]
                {
                    M("Занято", d.VolSize > 0 ? N(d.Used / gb, "0") + " / " + N(d.VolSize / gb, "0") + " ГБ" : "—", d.VolSize > 0 ? (double)d.Used / d.VolSize : 0, d.VolSize > 0 && d.Used > d.VolSize * 0.9),
                    M("Температура", d.TempC > 0 ? d.TempC + " °C" : "—", d.TempC > 0 ? d.TempC / (double)(d.TempMaxC > 0 ? d.TempMaxC : 80) : 0, d.TempMaxC > 0 && d.TempC >= d.TempMaxC - 5),
                    M("Чтение", Speed(d.ReadBps), d.ReadBps / top),
                    M("Запись", Speed(d.WriteBps), d.WriteBps / top),
                }.ToList();
                k.Fit();
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); sensors.Dispose(); disks.Dispose(); } base.Dispose(disposing); }
    }
}
