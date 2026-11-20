using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RedTune.Ram
{
    // one reader at a time: the page and the after-reboot check at startup share it
    static class RamState
    {
        static readonly object busy = new object();
        public static RamData Data;
        public static Dictionary<string, string> Live;

        public static Tuple<bool, string> Refresh()
        {
            lock (busy)
            {
                Live = RamCore.ReadLive();
                if (!RamCore.Ready) RamCore.Init(Live);
                Data = RamCore.Load(Live);
                return RamCore.CheckPending(Data, Live);
            }
        }
    }

    // "Auto" with a switch, for the speed and FCLK rows
    class AutoSwitch : Painted
    {
        public readonly Toggle Toggle = new Toggle();
        public AutoSwitch()
        {
            // 16.11: as wide as the word needs — a fixed 86 left it some 30 px and it was drawn as "Au..."
            BackColor = Theme.Card;
            Size = new Size(TextRenderer.MeasureText("Auto", Theme.Body).Width + Theme.S(12) + Toggle.Width, Math.Max(Theme.S(24), Toggle.Height));
            Toggle.Location = new Point(Width - Toggle.Width, (Height - Toggle.Height) / 2);
            Controls.Add(Toggle);
        }
        protected override void OnBackColorChanged(EventArgs e) { Toggle.BackColor = BackColor; base.OnBackColorChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Theme.Text1(e.Graphics, "Auto", Theme.Body, new Rectangle(0, 0, Toggle.Left - Theme.S(8), Height), Enabled ? Theme.Text2 : Theme.Muted, TextFormatFlags.Right);
        }
        protected override void OnEnabledChanged(EventArgs e) { Toggle.Enabled = Enabled; Invalidate(); base.OnEnabledChanged(e); }
    }

    // the timings: name | now | in BIOS | new (edit box) | Auto | range
    class TimingTable : Painted
    {
        public static readonly Color BadBg = Color.FromArgb(0x47, 0x2c, 0x1a);
        public List<TimingRow> Rows = new List<TimingRow>();
        public TextBox[] Boxes = new TextBox[0];
        public bool[] Auto = new bool[0];
        public bool Known, Editable;
        public event EventHandler Changed;
        bool filling;
        int hotAuto = -1;

        int HeadH { get { return Theme.S(40); } }
        int RowH { get { return Theme.S(38); } }
        int ColLive { get { return Theme.S(150); } }
        int ColStored { get { return Theme.S(240); } }
        int ColNew { get { return Theme.S(330); } }
        int NewW { get { return Theme.S(86); } }
        int ColAuto { get { return Theme.S(440); } }
        int ColRange { get { return Theme.S(510); } }

        public TimingTable() { BackColor = Theme.Window; }

        public void SetData(List<TimingRow> rows, bool known, bool editable)
        {
            foreach (var b in Boxes) b.Dispose();
            Rows = rows; Known = known; Editable = editable;
            Boxes = new TextBox[rows.Count]; Auto = new bool[rows.Count];
            filling = true;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var b = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.Body, BackColor = Theme.Raised, ForeColor = Theme.Text, TextAlign = HorizontalAlignment.Center };
                int idx = i;
                b.TextChanged += (s, e) => { if (!filling) { Style(idx); Fire(); } };
                Boxes[i] = b;
                Controls.Add(b);
            }
            Height = HeadH + rows.Count * RowH + Theme.S(10);
            Reset();
            filling = false;
            PerformLayout();
        }

        // back to what the BIOS holds
        public void Reset()
        {
            filling = true;
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                Auto[i] = Known && !r.Manual;
                Boxes[i].Text = Known && !r.Manual ? (r.Live != null ? r.Live.ToString() : "") : r.Stored.ToString();
                Style(i);
            }
            filling = false;
            Invalidate();
        }

        public void SetRow(int i, int? value)
        {
            if (!RowEditable(i)) return;
            filling = true;
            Auto[i] = value == null;
            if (value != null) Boxes[i].Text = value.ToString();
            Style(i);
            filling = false;
            Invalidate();
        }

        bool RowEditable(int i) { return Editable && Rows[i].Editable; }

        void Fire() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }

        // null = Auto; error text when the box is wrong
        public int? Value(int i, out string error)
        {
            error = null;
            if (Auto[i]) return null;
            int v;
            var t = Boxes[i].Text.Trim();
            if (t.Length == 0) { error = Rows[i].Label + ": впишите число или включите Auto"; return null; }
            if (!int.TryParse(t, out v)) { error = Rows[i].Label + ": «" + t + "» не число"; return null; }
            var bad = RamCore.CheckValue(Rows[i], v);
            if (bad != null) { error = Rows[i].Label + " = " + v + ": " + bad; return null; }
            return v;
        }

        public bool Edited(int i)
        {
            var r = Rows[i];
            if (Auto[i]) return r.Manual;
            string err; int? v = Value(i, out err);
            return err == null && (!r.Manual || v != r.Stored);
        }

        void Style(int i)
        {
            var b = Boxes[i];
            bool ed = RowEditable(i);
            b.Enabled = ed && !Auto[i];
            b.ReadOnly = !ed;
            string err; Value(i, out err);
            Color bg = Theme.Raised, fg = Theme.Text;
            if (ed && Auto[i] && Rows[i].Manual) { bg = Theme.WarningBg; fg = Theme.Warning; } // was manual, goes to Auto
            else if (!ed || Auto[i]) { fg = Theme.Muted; }
            else if (err != null) { bg = BadBg; fg = Theme.Danger; }
            else if (Edited(i)) { bg = Theme.WarningBg; fg = Theme.Warning; }
            b.BackColor = bg; b.ForeColor = fg;
            Invalidate(new Rectangle(0, HeadH + i * RowH, Width, RowH));
        }

        Rectangle BoxRect(int i) { return new Rectangle(ColNew, HeadH + i * RowH + Theme.S(5), NewW, RowH - Theme.S(10)); }
        Rectangle AutoRect(int i) { int k = Theme.S(18); return new Rectangle(ColAuto + Theme.S(12), HeadH + i * RowH + (RowH - k) / 2, k, k); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            for (int i = 0; i < Boxes.Length; i++)
            {
                if (Boxes[i] == null) continue;
                var r = BoxRect(i);
                int h = Boxes[i].PreferredHeight;
                Boxes[i].Bounds = new Rectangle(r.X + Theme.S(6), r.Y + (r.Height - h) / 2, r.Width - Theme.S(12), h);
            }
        }

        int HitAuto(Point p) { for (int i = 0; i < Rows.Count; i++) if (RowEditable(i) && Rectangle.Inflate(AutoRect(i), Theme.S(10), Theme.S(8)).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitAuto(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hotAuto) { hotAuto = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hotAuto = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = HitAuto(e.Location);
            if (i >= 0)
            {
                Auto[i] = !Auto[i];
                // Auto switched off on an empty box: start from what runs now
                if (!Auto[i] && Boxes[i].Text.Trim().Length == 0 && Rows[i].Live != null) { filling = true; Boxes[i].Text = Rows[i].Live.ToString(); filling = false; }
                Style(i); Fire();
            }
            base.OnMouseClick(e);
        }

        public static void DrawBox(Graphics g, Rectangle r, bool on, bool hover, bool enabled)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (on)
            {
                Theme.Fill(g, r, Theme.S(4), !enabled ? Theme.Muted : hover ? Theme.AccentHover : Theme.Accent);
                using (var pen = new Pen(Color.White, Math.Max(1.6f, Theme.S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.71f), new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.31f) });
            }
            else using (var pen = new Pen(!enabled ? Theme.Border : hover ? Theme.Text : Theme.Muted, Math.Max(1.2f, Theme.S(1.5f))))
                using (var path = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(4))) g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, card, Theme.S(8), Theme.Card);
            Theme.Stroke(g, card, Theme.S(8), Theme.Border);
            var hf = TextFormatFlags.NoPadding;
            Theme.Text1(g, "Тайминг", Theme.Small, new Rectangle(Theme.S(16), 0, Theme.S(120), HeadH), Theme.Muted, hf);
            Theme.Text1(g, "Сейчас", Theme.Small, new Rectangle(ColLive, 0, Theme.S(80), HeadH), Theme.Muted, hf);
            Theme.Text1(g, "В BIOS", Theme.Small, new Rectangle(ColStored, 0, Theme.S(80), HeadH), Theme.Muted, hf);
            Theme.Text1(g, "Новое", Theme.Small, new Rectangle(ColNew, 0, NewW, HeadH), Theme.Muted, hf | TextFormatFlags.HorizontalCenter);
            Theme.Text1(g, "Auto", Theme.Small, new Rectangle(ColAuto, 0, Theme.S(60), HeadH), Theme.Muted, hf);
            Theme.Text1(g, "Диапазон", Theme.Small, new Rectangle(ColRange, 0, Theme.S(160), HeadH), Theme.Muted, hf);
            using (var pen = new Pen(Theme.Border))
            {
                g.DrawLine(pen, Theme.S(12), HeadH - 1, Width - Theme.S(12), HeadH - 1);
                for (int i = 0; i < Rows.Count; i++)
                {
                    var r = Rows[i];
                    int y = HeadH + i * RowH;
                    if (i > 0) g.DrawLine(pen, Theme.S(12), y, Width - Theme.S(12), y);
                    var cell = new Rectangle(0, y, 0, RowH);
                    Theme.Text1(g, r.Label, Theme.Body, new Rectangle(Theme.S(16), y, Theme.S(130), RowH), Theme.Text, hf);
                    Theme.Text1(g, r.Live != null ? r.Live.ToString() : "—", Theme.Body, new Rectangle(ColLive, y, Theme.S(80), RowH), Theme.Text2, hf);
                    string stored = !Known ? "?" : r.Manual ? r.Stored.ToString() : "Auto";
                    bool pending = Known && r.Manual && r.Live != null && r.Stored != r.Live;
                    if (pending)
                    {
                        int tw = TextRenderer.MeasureText(stored, Theme.Body).Width;
                        Theme.Fill(g, new RectangleF(ColStored - Theme.S(8), y + Theme.S(7), tw + Theme.S(16), RowH - Theme.S(14)), Theme.S(5), Theme.AccentBg);
                    }
                    Theme.Text1(g, stored, Theme.Body, new Rectangle(ColStored, y, Theme.S(80), RowH), pending ? Theme.AccentFg : Theme.Text2, hf);
                    if (i < Boxes.Length)
                    {
                        var br = BoxRect(i);
                        Theme.Fill(g, br, Theme.S(6), Boxes[i].BackColor);
                        if (!RowEditable(i) || Auto[i])
                        {
                            Boxes[i].Visible = false;
                            Theme.Text1(g, Auto[i] ? "Auto" : Boxes[i].Text, Theme.Body, br, Boxes[i].ForeColor, hf | TextFormatFlags.HorizontalCenter);
                        }
                        else Boxes[i].Visible = true;
                        if (i < Auto.Length) DrawBox(g, AutoRect(i), Auto[i], i == hotAuto, RowEditable(i));
                    }
                    string range = r.Min == null ? "—" : r.Min + "–" + r.Max + (r.Step > 1 ? ", шаг " + r.Step : "");
                    Theme.Text1(g, range, Theme.Small, new Rectangle(ColRange, y, Math.Max(0, Width - ColRange - Theme.S(12)), RowH), Theme.Muted, hf);
                }
            }
        }
    }

    class RamPage : Page
    {
        readonly Row liveRow, stateRow, speedRow, fclkRow, uclkRow, expRow;
        readonly Toggle expOn = new Toggle();
        readonly DropButton speedDrop = new DropButton(), fclkDrop = new DropButton();
        readonly AutoSwitch speedAuto = new AutoSwitch(), fclkAuto = new AutoSwitch();
        readonly Segmented uclkSeg = new Segmented("1:1", "1:2", "Auto");
        readonly TimingTable table = new TimingTable();
        readonly FlatButton resetBtn = new FlatButton("Сбросить  ▾"), profBtn = new FlatButton("Профили  ▾"),
                            applyBtn = new FlatButton("Применить", true), rebootBtn = new FlatButton("Применить и перезагрузить");
        readonly FooterBar bar;
        // the memory check (14.11)
        readonly Row testRow, testResult;
        readonly DropButton testTime = new DropButton();
        readonly FlatButton testBtn = new FlatButton("Запустить");
        readonly System.Windows.Forms.Timer testTimer = new System.Windows.Forms.Timer { Interval = 500 };
        static readonly int[] TestSeconds = { 20 * 60, 3600, 3 * 3600, 0 };
        // the latency measurement and its curve (16.11, Latency)
        readonly Row latRow;
        readonly FlatButton latBtn = new FlatButton("Измерить");
        readonly LatencyGraph latGraph = new LatencyGraph();
        const string LatHelp = "Задержка чтения в L1, L2, L3 и в самой памяти · около 15 с, компьютер пока лучше не трогать";
        RamData d;
        bool filling, loading;
        string note = "";

        public RamPage()
        {
            liveRow = Row("Сейчас работает", "читаю тайминги…");
            stateRow = Row("В BIOS", "—");
            // a desktop board has a Clear CMOS button or jumper; a laptop has to be opened
            Row("Осторожно", Hw.Desktop ? "Слишком жёсткие значения могут не дать компьютеру загрузиться. Тогда нужен сброс CMOS: кнопка или перемычка Clear CMOS на плате"
                : "Слишком жёсткие значения могут не дать ноутбуку загрузиться, а сброс BIOS здесь — только с разборкой").WarnStyle = true;
            expRow = Row("Экспериментальная запись", ExpSeen, expOn);
            expRow.Visible = false;
            expOn.CheckedChanged += (s, e) => { if (!filling) ToggleExperimental(); };
            Section("Проверка");
            testTime.Set(new[] { "20 минут", "1 час", "3 часа", "Без остановки" }, 1);
            testRow = Row("Проверка памяти", "Все потоки, вся свободная память · кривую ставьте в 0", testTime, testBtn);
            testResult = Row("Результат проверки", MemTest.StatusText());
            testBtn.Click += (s, e) => ToggleTest();
            testTimer.Tick += (s, e) => ShowTest();
            Action ended = () => { try { if (IsHandleCreated) BeginInvoke((Action)ShowTest); } catch { } };
            MemTest.Finished += ended;
            Disposed += (s, e) => { MemTest.Finished -= ended; testTimer.Dispose(); };
            ShowTest();
            latRow = Row("Латентность памяти", LatHelp, latBtn);
            Add(latGraph, 8);
            latBtn.Click += (s, e) => ToggleLatency();
            ShowLatency(null);
            Section("Частоты");
            speedRow = Row("Скорость памяти", "MT/s · Auto — скорость выбирает BIOS", speedDrop, speedAuto);
            fclkRow = Row("FCLK", Hw.Desktop ? "Частота Infinity Fabric, МГц" : "Частота Infinity Fabric, МГц · выше 2400 ноутбук перегревается", fclkDrop, fclkAuto);
            uclkRow = Row("UCLK", "Контроллер памяти: 1:1 — на частоте памяти, 1:2 — вдвое ниже", uclkSeg);
            Section("Тайминги");
            Row("Цвета в таблице", "жёлтым — ваша правка · синим — ждёт перезагрузки · красным — недопустимо");
            Add(table, 8);
            Footer = bar = new FooterBar(resetBtn, profBtn, applyBtn, rebootBtn);
            bar.SetStatus("Читаю тайминги…");

            speedDrop.Changed += (s, e) => Changed();
            fclkDrop.Changed += (s, e) => Changed();
            speedAuto.Toggle.CheckedChanged += (s, e) => Changed();
            fclkAuto.Toggle.CheckedChanged += (s, e) => Changed();
            uclkSeg.Changed += (s, e) => Changed();
            table.Changed += (s, e) => Changed();
            resetBtn.Click += (s, e) => ResetMenu();
            profBtn.Click += (s, e) => ProfileMenu();
            applyBtn.Click += (s, e) => Apply(false);
            rebootBtn.Click += (s, e) => Apply(true);
            SetEnabled(false);
            HandleCreated += (s, e) => { if (d == null) Reload(null); };
        }

        void SetEnabled(bool on)
        {
            foreach (Control c in new Control[] { speedDrop, fclkDrop, speedAuto, fclkAuto, uclkSeg, resetBtn, profBtn, applyBtn, rebootBtn }) c.Enabled = on;
        }

        // ---------- the memory check ----------
        void ToggleTest()
        {
            if (MemTest.Running) { MemTest.Stop(); return; }
            if (MemTest.Busy != null && MemTest.Busy()) { Note("Идёт автотюн: проверку памяти запускайте после него"); return; }
            var co = MemTest.CurveNow != null ? MemTest.CurveNow() : null;
            if (co != null && co.Any(v => v != 0) && !ConfirmDialog.Ask(FindForm(), "Проверка памяти",
                "Кривая сейчас не нулевая. Ошибку в проверке тогда может дать и процессор, а не память.\n\nДля чистого результата сначала примените кривую «Заводской (0)».", "Всё равно запустить", "Отмена")) return;
            string mem = d != null && d.SpeedLive != null ? d.SpeedLive + " MT/s" : "";
            var err = MemTest.Start(TestSeconds[Math.Max(0, Math.Min(TestSeconds.Length - 1, testTime.Index))], mem);
            if (err != null) Note(err);
            ShowTest();
        }

        void ShowTest()
        {
            bool run = MemTest.Running;
            testTimer.Enabled = run;
            string text = run ? "Остановить" : "Запустить";
            if (testBtn.Text != text) testBtn.Text = text;
            testTime.Enabled = !run;
            testResult.SetSub(MemTest.StatusText());
            bool bad = MemTest.ShownErrors > 0;
            if (testResult.WarnStyle != bad) { testResult.WarnStyle = bad; testResult.Invalidate(); }
        }

        // ---------- the latency measurement ----------
        // what the memory runs at, kept with the run: «5600 MT/s 40-40-40»
        string MemLabel()
        {
            if (d == null) return "";
            var t = new[] { "tCL", "tRCD", "tRP" }.Select(k => d.Rows.FirstOrDefault(r => r.Label == k)).Where(r => r != null && r.Live != null).Select(r => r.Live.ToString()).ToArray();
            return ((d.SpeedLive != null ? d.SpeedLive + " MT/s" : "") + (t.Length == 3 ? " " + string.Join("-", t) : "")).Trim();
        }

        void ToggleLatency()
        {
            if (Latency.Running) { Latency.Stop(); return; }
            if (MemTest.Running) { latRow.SetSub("Идёт проверка памяти: латентность измеряйте после неё"); return; }
            if (MemTest.Busy != null && MemTest.Busy()) { latRow.SetSub("Идёт автотюн: латентность измеряйте после него"); return; }
            if (RedTune.CoreCurve.Measuring) { latRow.SetSub("Идёт замер кривой ядра: латентность измеряйте после него"); return; }
            Action<Action> ui = a => { try { if (IsHandleCreated) BeginInvoke(a); } catch { } };
            var err = Latency.Start(MemLabel(),
                (run, done, all) =>
                {
                    // a copy: the worker goes on adding points to its own list
                    var copy = new Latency.Run { Time = run.Time, Mem = run.Mem, Points = run.Points.ToList() };
                    ui(() => { latGraph.Live = copy; latGraph.Invalidate(); latRow.SetSub("Измеряю: " + done + " из " + all + " · " + Latency.Size(copy.Points[copy.Points.Count - 1].Bytes)); });
                },
                (run, why) => ui(() => ShowLatency(why)));
            if (err != null) { latRow.SetSub(err); return; }
            latBtn.Text = "Остановить";
            latRow.SetSub("Измеряю…");
            latGraph.Live = new Latency.Run(); latGraph.Visible = true; latGraph.Invalidate();
        }

        // the last run and the one before it (the grey line: what the previous settings gave)
        void ShowLatency(string why)
        {
            var runs = Latency.Load();
            var now = runs.Count > 0 ? runs[runs.Count - 1] : null;
            // 21.11: the previous run of the same kind of pages (one on 4 KB pages reads ~20 ns slower and is no «было»)
            var before = now == null ? null : runs.Take(runs.Count - 1).LastOrDefault(r => r.Large == now.Large);
            latGraph.Live = null; latGraph.Now = now; latGraph.Before = before;
            if (latGraph.Visible != (now != null)) latGraph.Visible = now != null;
            latGraph.Invalidate();
            latBtn.Text = "Измерить";
            latRow.SetSub(why ?? (now == null ? LatHelp : Latency.Summary(now, before) + " · " + now.Time.ToString("dd.MM HH:mm") + (now.Mem.Length > 0 ? " · " + now.Mem : "")));
        }

        // ---------- reading ----------
        void Reload(string message)
        {
            if (loading) return;
            loading = true;
            Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tuple<bool, string> pending = null; Exception err = null;
                try { pending = RamState.Refresh(); } catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        loading = false;
                        Cursor = Cursors.Default;
                        if (err != null) { liveRow.SetSub("Не удалось прочитать: " + err.Message); bar.SetStatus("Тайминги не прочитались"); return; }
                        d = RamState.Data;
                        Fill();
                        if (message != null) Note(message);
                        if (pending != null)
                        {
                            Note(pending.Item1 ? pending.Item2 : "Проверка после перезагрузки: не всё включилось");
                            if (!pending.Item1) Msg.Show(FindForm(), pending.Item2, "Проверка после перезагрузки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }));
                }
                catch { loading = false; }
            });
        }

        void Note(string s) { note = s ?? ""; UpdateState(); }

        static string Mode(int? m) { return m == null ? "" : " (" + RamCore.UclkText(m) + ")"; }

        // 16.11: on a device that is not the verified one, writing is switched on here by its owner, after a warning; the
        // consent is kept for that very device (RamCore.SetConsent)
        const string ExpSeen = "Устройство не проверено: команды берутся у его BIOS, запись сверяется с сохранённым";
        const string ExpBlind = "Устройство не проверено: команды берутся у его BIOS, результат виден только после перезагрузки";
        void ToggleExperimental()
        {
            if (expOn.Checked)
            {
                string text = "Запись настроек памяти на этом устройстве не проверялась.\n\n" +
                    "• Номера команд и допустимые значения программа берёт у BIOS этого устройства.\n" +
                    (RamCore.LayoutKnown ? "• Формат настроек BIOS совпал с проверенным: после записи значения сверяются.\n"
                                         : "• Формат настроек BIOS неизвестен: до перезагрузки нельзя ни проверить, что записалось, ни увидеть, что там лежит.\n") +
                    "• Неверное значение может не дать компьютеру загрузиться. Тогда помогает только сброс настроек BIOS (CMOS).\n\n" +
                    "Разрешить запись на этом устройстве?";
                if (!ConfirmDialog.Ask(FindForm(), "Экспериментальная запись", text, "Разрешить запись", "Отмена")) { filling = true; expOn.Set(false); filling = false; return; }
                RamCore.SetConsent(true);
            }
            else RamCore.SetConsent(false);
            Reload(RamCore.CanWrite ? "Запись в экспериментальном режиме разрешена" : "Запись в экспериментальном режиме выключена");
        }

        void Fill()
        {
            filling = true;
            if (expRow.Visible != RamCore.Experimental) expRow.Visible = RamCore.Experimental;
            expRow.SetSub(RamCore.LayoutKnown ? ExpSeen : ExpBlind);
            expOn.Set(RamCore.Experimental && RamCore.CanWrite);
            var refiNs = d.SpeedLive != null && d.Refi != null ? Math.Round(d.Refi.Value / (d.SpeedLive.Value / 2000.0)).ToString() : "?";
            liveRow.SetSub(d.SpeedLive + " MT/s · " + d.MemType + " · FCLK " + (d.FclkLive != null ? d.FclkLive.ToString() : "?") + " · UCLK " + (d.UclkLive != null ? d.UclkLive + Mode(d.UclkLiveMode) : "?") +
                           " · GDM " + (d.Gdm == "Enabled" ? "вкл" : "выкл") + " · " + d.Cmd2T + " · tREFI " + d.Refi + " (" + refiNs + " нс)");

            var sl = d.SpeedList.Count > 0 ? d.SpeedList : new List<int> { d.SpeedLive ?? 0 };
            int sSel = d.SpeedManual ? d.SpeedStored : d.SpeedLive ?? 0;
            speedDrop.Set(sl.Select(v => v + " MT/s").ToArray(), Math.Max(0, sl.IndexOf(sSel)));
            speedAuto.Toggle.Set(!d.SpeedManual);

            var fl = d.FclkList.Count > 0 ? d.FclkList : new List<int> { d.FclkLive ?? 0 };
            // on Auto the choice waiting in the list is the value nearest to what runs now (it used to be the first one)
            int fSel = d.FclkManual && d.FclkRaw > 0 ? d.FclkRaw : fl.OrderBy(v => Math.Abs(v - (d.FclkLive ?? 0))).First();
            fclkDrop.Set(fl.Select(v => v + " МГц").ToArray(), Math.Max(0, fl.IndexOf(fSel)));
            fclkAuto.Toggle.Set(!d.FclkManual);

            uclkSeg.Selected = !d.UclkManual ? 2 : d.UclkRaw == 1 ? 0 : 1;
            table.SetData(d.Rows, d.Known, RamCore.CanWrite);
            filling = false;
            Relayout();
            Changed();
        }

        // ---------- the wish on the page ----------
        Want Collect(out List<string> errors)
        {
            errors = new List<string>();
            var w = new Want();
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var r = table.Rows[i];
                if (!r.Editable) continue;
                string err; int? v = table.Value(i, out err);
                if (err != null) { errors.Add(err); continue; }
                w.Timings[r.Key] = v;
            }
            var sl = d.SpeedList.Count > 0 ? d.SpeedList : new List<int> { d.SpeedLive ?? 0 };
            var fl = d.FclkList.Count > 0 ? d.FclkList : new List<int> { d.FclkLive ?? 0 };
            w.Speed = speedAuto.Toggle.Checked ? (int?)null : sl[Math.Max(0, speedDrop.Index)];
            w.Fclk = fclkAuto.Toggle.Checked ? (int?)null : fl[Math.Max(0, fclkDrop.Index)];
            w.Uclk = uclkSeg.Selected == 2 ? (int?)null : uclkSeg.Selected == 0 ? 1 : 0;
            if (!RamCore.HasSpeed(d)) w.Speed = d.SpeedManual ? d.SpeedStored : (int?)null;
            if (!RamCore.HasFclk(d)) w.Fclk = d.FclkManual ? d.FclkRaw : (int?)null;
            if (!RamCore.HasUclk) w.Uclk = d.UclkManual ? d.UclkRaw : (int?)null;
            return w;
        }

        // 16.11: on Auto the field shows what really runs now, off Auto the value chosen in the list. The FCLK the BIOS
        // picks itself need not be one of the manual values: after a start on the fallback settings it was 1733, and the
        // field showed the first manual value, 1800, as if that were running.
        static void ShowLive(DropButton drop, bool auto, string live)
        {
            string t = (auto && live != null ? live : drop.Index >= 0 && drop.Index < drop.Items.Length ? drop.Items[drop.Index] : "—") + "  ▾";
            if (drop.Text != t) drop.Text = t;
        }

        void Changed()
        {
            if (filling || d == null) return;
            bool cw = RamCore.CanWrite;
            bool hs = cw && RamCore.HasSpeed(d), hf = cw && RamCore.HasFclk(d), hu = cw && RamCore.HasUclk;
            speedAuto.Enabled = hs; speedDrop.Enabled = hs && !speedAuto.Toggle.Checked;
            fclkAuto.Enabled = hf; fclkDrop.Enabled = hf && !fclkAuto.Toggle.Checked;
            ShowLive(speedDrop, speedAuto.Toggle.Checked, d.SpeedLive != null ? d.SpeedLive + " MT/s" : null);
            ShowLive(fclkDrop, fclkAuto.Toggle.Checked, d.FclkLive != null ? d.FclkLive + " МГц" : null);
            uclkSeg.Enabled = hu;
            resetBtn.Enabled = cw; profBtn.Enabled = true;

            List<string> errors;
            var w = Collect(out errors);
            int edits = 0;
            if (errors.Count == 0)
            {
                try { edits = RamCore.Plan(d, w).Diff.Count; } catch { edits = 1; }
            }
            speedRow.WarnStyle = hs && (w.Speed != (d.SpeedManual ? d.SpeedStored : (int?)null));
            fclkRow.WarnStyle = hf && (w.Fclk != (d.FclkManual ? d.FclkRaw : (int?)null));
            uclkRow.WarnStyle = hu && (w.Uclk != (d.UclkManual ? d.UclkRaw : (int?)null));
            foreach (var r in new[] { speedRow, fclkRow, uclkRow }) { foreach (Control c in r.Controls) c.BackColor = r.WarnStyle ? Theme.WarningBg : Theme.Card; r.Invalidate(); }
            applyBtn.Enabled = cw && edits > 0 && errors.Count == 0;
            rebootBtn.Enabled = cw && errors.Count == 0;
            if (errors.Count > 0) bar.SetStatus("Исправьте: " + errors[0] + (errors.Count > 1 ? " (и ещё " + (errors.Count - 1) + ")" : ""));
            else if (edits > 0) bar.SetStatus("Изменений: " + edits);
            else UpdateState();
        }

        void UpdateState()
        {
            if (d == null) return;
            string state;
            if (!RamCore.CanWrite) state = "Только просмотр: " + RamCore.Why;
            else
            {
                int p = d.PendingCount;
                state = p > 0 ? "В BIOS лежат значения, которые ещё не работают (" + p + " шт.) — включатся после перезагрузки" : "В BIOS сохранено то же, что работает сейчас";
            }
            stateRow.SetSub(state);
            stateRow.WarnStyle = !RamCore.CanWrite || d.PendingCount > 0;
            stateRow.Invalidate();
            bar.SetStatus(note.Length > 0 ? note : RamCore.CanWrite ? "Включается после перезагрузки" : "Только просмотр");
        }

        // ---------- buttons ----------
        void ResetMenu()
        {
            var m = Page.Menu();
            m.Items.Add(new ToolStripMenuItem("Отменить правки", null, (s, e) => { Fill(); Note("Правки отменены"); }));
            m.Items.Add(new ToolStripMenuItem("Всё в Auto", null, (s, e) => AllAuto()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Перечитать из BIOS", null, (s, e) => Reload("Перечитано")));
            ShowAt(m, resetBtn, true);
        }

        void AllAuto()
        {
            filling = true;
            for (int i = 0; i < table.Rows.Count; i++) table.SetRow(i, null);
            if (RamCore.HasSpeed(d)) speedAuto.Toggle.Set(true);
            if (RamCore.HasFclk(d)) fclkAuto.Toggle.Set(true);
            if (RamCore.HasUclk) uclkSeg.Selected = 2;
            filling = false;
            Changed();
            note = "";
        }

        void ProfileMenu()
        {
            var m = Page.Menu();
            m.Items.Add(new ToolStripMenuItem("Сохранить профиль…", null, (s, e) => SaveProfile()));
            if (RamCore.CanWrite) m.Items.Add(new ToolStripMenuItem("Загрузить профиль…", null, (s, e) => LoadProfile()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Открыть папку профилей", null, (s, e) => { Directory.CreateDirectory(RamCore.ProfileDir); Process.Start("explorer.exe", "\"" + RamCore.ProfileDir + "\""); }));
            ShowAt(m, profBtn, true);
        }

        void SaveProfile()
        {
            List<string> errors;
            var w = Collect(out errors);
            if (errors.Count > 0) { Msg.Show(FindForm(), string.Join("\n", errors), "Исправьте значения", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Directory.CreateDirectory(RamCore.ProfileDir);
            using (var dlg = new SaveFileDialog { InitialDirectory = RamCore.ProfileDir, Filter = "Профиль памяти (*.json)|*.json", FileName = "profile_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".json" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try { RamCore.SaveProfile(dlg.FileName, w); Note("Профиль сохранён: " + Path.GetFileName(dlg.FileName)); }
                catch (Exception ex) { Msg.Show(FindForm(), "Не сохранился: " + ex.Message, "Red Tune"); }
            }
        }

        void LoadProfile()
        {
            Directory.CreateDirectory(RamCore.ProfileDir);
            using (var dlg = new OpenFileDialog { InitialDirectory = RamCore.ProfileDir, Filter = "Профиль памяти (*.json)|*.json" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    bool hs, hf, hu;
                    var w = RamCore.LoadProfile(dlg.FileName, out hs, out hf, out hu);
                    filling = true;
                    for (int i = 0; i < table.Rows.Count; i++) { int? v; if (w.Timings.TryGetValue(table.Rows[i].Key, out v)) table.SetRow(i, v); }
                    if (hs && RamCore.HasSpeed(d))
                    {
                        speedAuto.Toggle.Set(w.Speed == null);
                        if (w.Speed != null) { int i = d.SpeedList.IndexOf(w.Speed.Value); if (i >= 0) speedDrop.Set(speedDrop.Items, i); }
                    }
                    if (hf && RamCore.HasFclk(d))
                    {
                        fclkAuto.Toggle.Set(w.Fclk == null);
                        if (w.Fclk != null) { int i = d.FclkList.IndexOf(w.Fclk.Value); if (i >= 0) fclkDrop.Set(fclkDrop.Items, i); }
                    }
                    if (hu && RamCore.HasUclk) uclkSeg.Selected = w.Uclk == null ? 2 : w.Uclk == 1 ? 0 : 1;
                    filling = false;
                    Changed();
                    bar.SetStatus("Профиль «" + Path.GetFileNameWithoutExtension(dlg.FileName) + "» загружен в окно · в BIOS ещё не записан");
                }
                catch (Exception ex) { filling = false; Msg.Show(FindForm(), "Не удалось прочитать профиль: " + ex.Message, "Red Tune"); }
            }
        }

        void Apply(bool reboot)
        {
            if (!RamCore.CanWrite) return;
            List<string> errors;
            var w = Collect(out errors);
            if (errors.Count > 0) { Msg.Show(FindForm(), string.Join("\n", errors), "Исправьте значения", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            ApplyPlan plan;
            try { plan = RamCore.Plan(d, w); }
            catch (Exception ex) { Msg.Show(FindForm(), ex.Message, "Red Tune", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (plan.Empty)
            {
                if (reboot && ConfirmDialog.Ask(FindForm(), "Перезагрузка", "Новых значений нет. Просто перезагрузить компьютер?", "Перезагрузить", "Отмена")) RamCore.Reboot();
                else if (!reboot) Note("Менять нечего: всё совпадает с сохранённым в BIOS");
                return;
            }
            var text = "Записать в BIOS:\n\n" + string.Join("\n", plan.Diff) + "\n\n";
            if (plan.Tighter > 0) text += "Значений жёстче или выше текущих: " + plan.Tighter + (Hw.Desktop ? ". Если компьютер с ними не загрузится, понадобится сброс CMOS (кнопка или перемычка Clear CMOS на плате).\n\n"
                : ". Если ноутбук с ними не загрузится, понадобится сброс BIOS вручную (с разборкой).\n\n");
            if (plan.Reset) text += "Auto ставится через общий сброс настроек памяти; всё, что остаётся ручным, будет записано заново.\n\n";
            text += reboot ? "После записи компьютер перезагрузится через 15 секунд." : "Значения включатся после следующей перезагрузки.";
            if (!ConfirmDialog.Ask(FindForm(), "Подтверждение", text, reboot ? "Записать и перезагрузить" : "Записать в BIOS", "Отмена")) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                RamCore.Send(d, w, plan);
                if (reboot) { RamCore.Reboot(); Reload("Записано и проверено. Перезагрузка через 15 секунд (отмена: shutdown /a)"); }
                else Reload("Записано в BIOS и проверено · включится после перезагрузки");
            }
            catch (Exception ex)
            {
                RamCore.Log("ERROR: " + ex.Message);
                Msg.Show(FindForm(), ex.Message, "Ошибка записи", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Reload(null);
            }
            finally { Cursor = Cursors.Default; }
        }
    }
}
