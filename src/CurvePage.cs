using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    class CurvePage : Page
    {
        readonly CurveService curve;
        readonly Row presetRow, liveRow, historyRow;
        readonly FlatButton presetBtn = new FlatButton("…"), saveBtn = new FlatButton("Сохранить"), applyBtn = new FlatButton("Применить", true), resetBtn = new FlatButton("Сбросить");
        readonly FlatButton readBtn = new FlatButton("Прочитать"), historyBtn = new FlatButton("Открыть"), allCurvesBtn = new FlatButton("Все ядра");
        readonly Pill livePill = new Pill();
        readonly GridCard card;
        readonly Toggle onStart = new Toggle(), onAc = new Toggle(), onBattery = new Toggle(), onResume = new Toggle();
        CurvePreset selected;
        readonly PageTabs tabs = new PageTabs(0, "Кривая", "Автотюн", "Сторож");
        readonly Tuner tuner;
        // 14.11: how the curve is set: a value per core, one per chiplet, or one for all (CoreGrid's modes)
        static readonly int[] Modes = Hw.CurveChips > 1 ? new[] { 0, 1, 2 } : new[] { 0, 2 };
        readonly Segmented modeSeg = Hw.CurveChips > 1 ? new Segmented("По ядрам", "По чипам", "Общее") : new Segmented("По ядрам", "Общее");
        readonly Row modeRow;

        public CurvePage(CurveService curve, Tuner tuner, Action openTune, Action openWatch)
        {
            this.curve = curve; this.tuner = tuner;
            Add(tabs, 0);
            tabs.Picked += i => { if (i == 2) openWatch(); else openTune(); };
            presetRow = Row("Пресет кривой", "", presetBtn);
            modeRow = Row("Смещение", "", modeSeg);
            card = Add(new GridCard(resetBtn, saveBtn, applyBtn));
            modeSeg.Changed += (o, e) => { if (modeSeg.Selected >= 0) card.Grid.SetMode(Modes[modeSeg.Selected]); };
            liveRow = Row("Сейчас на процессоре", "", livePill, readBtn);
            // 14.11: every core's voltage/clock curve on one graph, and a measurement of all of them in a row
            Row("Кривые ядер", "Напряжение на разных частотах: одно ядро — номер над ползунком, все сразу — здесь", allCurvesBtn);
            allCurvesBtn.Click += (o, e) =>
            {
                using (var f = new AllCoresForm(() => curve.Live != null ? (int[])curve.Live.Clone() : null))
                    f.ShowDialog(FindForm());
            };
            Section("Применять автоматически");
            Row("При запуске Windows", "Нужен автозапуск программы (вкладка «Настройки»)", onStart);
            // a desktop has neither a charger nor a battery
            var acRow = Row("При подключении зарядки", null, onAc);
            var batRow = Row("При работе от батареи", null, onBattery);
            if (Hw.Desktop) acRow.Visible = batRow.Visible = false;
            Row("После выхода из сна", null, onResume);
            historyRow = Row("История изменений", "Каждое применение записывается, любой прошлый набор можно вернуть", historyBtn);
            tuner.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)ShowTune); };
            ShowTune();

            var s = curve.Settings;
            onStart.Set(s.ApplyOnStart); onAc.Set(s.ApplyOnAc); onBattery.Set(s.ApplyOnBattery); onResume.Set(s.ApplyOnResume);
            EventHandler save = (o, e) =>
            {
                s.ApplyOnStart = onStart.Checked; s.ApplyOnAc = onAc.Checked; s.ApplyOnBattery = onBattery.Checked; s.ApplyOnResume = onResume.Checked;
                Store.SaveSettings(s);
            };
            foreach (var t in new[] { onStart, onAc, onBattery, onResume }) t.CheckedChanged += save;

            presetBtn.Click += (o, e) => ShowPresetMenu();
            saveBtn.Click += (o, e) => SavePreset();
            applyBtn.Click += (o, e) => Apply();
            resetBtn.Click += (o, e) => Select(selected); // back to the saved preset
            readBtn.Click += (o, e) => { livePill.Set("читаю…", PillKind.Muted); curve.Refresh(); };
            historyBtn.Click += (o, e) => ShowHistory();
            card.Grid.Changed += (o, e) => UpdateButtons();
            // a core's number opens its measured voltage/clock curve (14.11)
            card.Grid.CoreClicked += core =>
            {
                // 16.11: after the measurement at the core's value the window measures it at 0 too: it sets the core to 0
                // through the same Apply as the page (no history entry) and puts the value back
                using (var f = new CoreCurveForm(core, () => curve.Live != null && core < curve.Live.Length ? curve.Live[core] : (int?)null,
                    () => curve.Live != null ? (int[])curve.Live.Clone() : null,
                    (v, what, done) => curve.Apply(v, what, "замер ядра", done, false)))
                    f.ShowDialog(FindForm());
            };
            curve.StateChanged += ShowLive;

            Select(curve.Active);
            VisibleChanged += (o, e) => { if (Visible) OnShown(); };
        }

        // a running or paused auto-tune shows on its tab
        void ShowTune() { tabs.SetNote(1, tuner.Running ? "идёт" : tuner.HasRun ? "пауза" : null); }

        void OnShown()
        {
            ShowTune();
            // a power mode may have switched the active curve meanwhile; follow it unless there are unsaved edits
            var v = card.Grid.Values;
            if (selected != curve.Active && !Dirty(v)) Select(curve.Active);
            livePill.Set("читаю…", PillKind.Muted);
            curve.Refresh();
        }

        void Select(CurvePreset p)
        {
            selected = p;
            card.Grid.LoadState(p);
            card.Grid.ReadOnly = false;
            presetBtn.Text = p.Name + "  ▾";
            UpdateButtons();
        }

        void UpdateButtons()
        {
            var v = card.Grid.Values;
            bool dirty = Dirty(v);
            int gm = card.Grid.Mode;
            modeSeg.Selected = Array.IndexOf(Modes, gm);
            modeRow.SetSub(gm == 0 ? "У каждого ядра своё значение" : gm == 1 ? "Одно значение на чип: ползунки слева, поядерные не действуют" : "Одно значение на все ядра: ползунок слева, поядерные не действуют");
            saveBtn.Enabled = v != null && dirty;
            resetBtn.Enabled = dirty;
            applyBtn.Enabled = CanApply(v);
            bool active = selected.Name == curve.Settings.ActivePreset;
            presetRow.SetSub((active ? "Активный: применяется автоматически" : "Выбран, но не активный: «Применить» сделает его активным") +
                (dirty ? " · есть несохранённые изменения" : ""));
            card.Status = v == null ? "Значения от " + Smu.Min + " до " + Smu.Max : gm != 0 ? "Тяните ползунок слева или введите число" : dirty ? "Голубые ползунки изменены" : "Тяните ползунок или введите число";
            card.Invalidate();
            ShowLive();
        }

        void ShowLive()
        {
            if (curve.LastError != null) { livePill.Set("ошибка", PillKind.Warn); liveRow.SetSub(curve.LastError); return; }
            if (curve.Live == null) return;
            var v = card.Grid.Values ?? selected.Values;
            var diff = Enumerable.Range(0, Math.Min(curve.Live.Length, v.Length)).Where(i => curve.Live[i] != v[i]).ToList();
            if (diff.Count == 0) { livePill.Set("совпадает", PillKind.Ok); liveRow.SetSub("Процессор работает на этих значениях"); }
            else
            {
                livePill.Set("отличается", PillKind.Warn);
                liveRow.SetSub(string.Join(", ", diff.Take(4).Select(i => Hw.CoreName(i) + ": " + curve.Live[i])) + (diff.Count > 4 ? " и ещё " + (diff.Count - 4) : ""));
            }
            applyBtn.Enabled = CanApply(card.Grid.Values);
        }

        // the grid differs from the selected preset: in what goes to the processor, in the mode, or in the per-core set
        // kept aside under a group mode
        bool Dirty(int[] v)
        {
            if (v == null) return true;
            var g = card.Grid;
            if (!v.SequenceEqual(Hw.Fit(selected.Values)) || g.Mode != selected.Mode) return true;
            return g.Mode != 0 && !g.CoreKeep.SequenceEqual(Hw.Fit(selected.Core ?? selected.Values));
        }

        // the grid into a preset: the values for the processor and how they were set
        void Keep(CurvePreset p, int[] v)
        {
            var g = card.Grid;
            p.Values = v; p.Mode = g.Mode; p.Chip = g.ChipVals; p.All = g.AllVal; p.Core = g.Mode == 0 ? null : g.CoreKeep;
        }

        // gray when applying would change nothing: these values are already on the CPU and the preset is the active one
        bool CanApply(int[] v)
        {
            if (v == null || curve.Busy) return false;
            return curve.Live == null || !v.SequenceEqual(curve.Live) || selected.Name != curve.Settings.ActivePreset;
        }

        void Apply()
        {
            var v = card.Grid.Values;
            if (v == null) return;
            bool dirty = Dirty(v);
            if (dirty && !selected.BuiltIn)
            {
                var r = Msg.Show(FindForm(), "Сохранить изменения в пресет «" + selected.Name + "» перед применением?", "Red Tune", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) { Keep(selected, v); curve.SavePresets(); }
            }
            if (!dirty || !selected.BuiltIn) { curve.Settings.ActivePreset = selected.Name; Store.SaveSettings(curve.Settings); }
            applyBtn.Enabled = false; livePill.Set("применяю…", PillKind.Muted);
            curve.Apply(v, selected.Name + (dirty && selected.BuiltIn ? " (изменён)" : ""), "вручную", ok => { Select(selected); card.Grid.Load(v, selected.Values); UpdateButtons(); });
        }

        void SavePreset()
        {
            var v = card.Grid.Values;
            if (v == null) return;
            if (selected.BuiltIn) { NewPreset(v); return; }
            Keep(selected, v);
            curve.SavePresets();
            Select(selected);
        }

        void NewPreset(int[] values)
        {
            var name = InputDialog.Ask(FindForm(), "Новый пресет", "Название", L.T("Мой пресет"));
            if (name == null) return;
            if (name == Store.FactoryName || curve.Presets.Any(p => p.Name == name)) { Msg.Show(FindForm(), "Такое название уже есть", "Red Tune"); return; }
            var p = new CurvePreset { Name = name };
            Keep(p, (int[])values.Clone());
            curve.Presets.Insert(curve.Presets.Count - 1, p);
            curve.SavePresets();
            Select(p);
        }

        void ShowPresetMenu()
        {
            var m = Page.Menu();
            foreach (var p in curve.Presets)
            {
                var item = new ToolStripMenuItem(p.Name + (p.Name == curve.Settings.ActivePreset ? "   · активный" : "")) { Checked = p == selected };
                var pp = p;
                item.Click += (s, e) => Select(pp);
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Новый пресет из этих значений…", null, (s, e) => { var v = card.Grid.Values; if (v != null) NewPreset(v); });
            var rename = m.Items.Add("Переименовать…", null, (s, e) => Rename());
            var del = m.Items.Add("Удалить", null, (s, e) => Delete());
            rename.Enabled = del.Enabled = !selected.BuiltIn;
            ShowAt(m, presetBtn);
        }

        void Rename()
        {
            var name = InputDialog.Ask(FindForm(), "Переименовать пресет", "Название", selected.Name);
            if (name == null || name == selected.Name) return;
            if (name == Store.FactoryName || curve.Presets.Any(p => p.Name == name)) { Msg.Show(FindForm(), "Такое название уже есть", "Red Tune"); return; }
            if (curve.Settings.ActivePreset == selected.Name) { curve.Settings.ActivePreset = name; Store.SaveSettings(curve.Settings); }
            selected.Name = name;
            curve.SavePresets();
            Select(selected);
        }

        void Delete()
        {
            if (Msg.Show(FindForm(), "Удалить пресет «" + selected.Name + "»? Его значения останутся в истории изменений.", "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            curve.Presets.Remove(selected);
            if (curve.Settings.ActivePreset == selected.Name) { curve.Settings.ActivePreset = ""; Store.SaveSettings(curve.Settings); }
            curve.SavePresets();
            Select(curve.Active);
        }

        void ShowHistory()
        {
            using (var f = new HistoryForm())
                if (f.ShowDialog(FindForm()) == DialogResult.OK && f.Picked != null)
                {
                    card.Grid.SetMode(0, false); // a set from the history is a value per core
                    card.Grid.Load(f.Picked.Values, selected.Values);
                    UpdateButtons();
                }
        }

        // the grid (a slider per core) with the save/apply buttons under it
        class GridCard : Painted
        {
            public readonly CoreGrid Grid = new CoreGrid();
            readonly FlatButton reset, save, apply;
            public string Status = "";

            public GridCard(FlatButton reset, FlatButton save, FlatButton apply)
            {
                this.reset = reset; this.save = save; this.apply = apply; BackColor = Theme.Window;
                Controls.Add(Grid); Controls.Add(reset); Controls.Add(save); Controls.Add(apply);
                Height = Theme.S(16) + Grid.Height + Theme.S(56);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e); if (save == null) return;
                Grid.Bounds = new Rectangle(Theme.S(8), Theme.S(10), Width - Theme.S(16), Grid.Height);
                apply.Location = new Point(Width - Theme.S(16) - apply.Width, Grid.Bottom + Theme.S(12));
                save.Location = new Point(apply.Left - Theme.S(8) - save.Width, apply.Top);
                reset.Location = new Point(save.Left - Theme.S(8) - reset.Width, apply.Top);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                Theme.Fill(g, r, Theme.S(10), Theme.Card);
                Theme.Stroke(g, r, Theme.S(10), Theme.Border);
                Theme.Text1(g, Status, Theme.Small, new Rectangle(Theme.S(16), Grid.Bottom + Theme.S(12), Math.Max(0, reset.Left - Theme.S(28)), save.Height), Theme.Text2);
            }
        }
    }
}
