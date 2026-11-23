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
        readonly Toggle onStart = new Toggle();
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
            // 23.11 (the user): one switch instead of start / charger / battery / sleep; the curve is checked and written only
            // when the CPU lost it (CurveService.EnsureActive)
            Row("Возвращать выбранную кривую", "При запуске Windows, после сна и смене питания: если кривая слетела, Red Tune вернёт её. Нужен автозапуск", onStart);
            historyRow = Row("История изменений", "Каждое применение записывается, любой прошлый набор можно вернуть", historyBtn);
            tuner.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)ShowTune); };
            ShowTune();

            var s = curve.Settings;
            onStart.Set(s.ApplyOnStart);
            onStart.CheckedChanged += (o, e) => { s.ApplyOnStart = onStart.Checked; Store.SaveSettings(s); };

            presetBtn.Click += (o, e) => ShowPresetMenu();
            saveBtn.Click += (o, e) => SavePreset();
            applyBtn.Click += (o, e) => Apply();
            // back to the saved preset; 20.11: as a step of its own, Ctrl+Z brings the edits back
            resetBtn.Click += (o, e) => ResetEdits();
            readBtn.Click += (o, e) => { livePill.Set("читаю…", PillKind.Muted); curve.Refresh(); };
            historyBtn.Click += (o, e) => ShowHistory();
            card.Grid.Changed += (o, e) => { Record(); UpdateButtons(); };
            gestureEnd.Tick += (o, e) => { if (Control.MouseButtons != MouseButtons.None) return; gestureEnd.Stop(); gesture = false; }; // a run of changes ends
            Disposed += (o, e) => gestureEnd.Dispose();
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
            Baseline();
            UpdateButtons();
        }

        // ---------- 20.11 (the user's wish): Ctrl+Z / Ctrl+Y, as «Питание», «Звук» and «Видеокарта» have ----------
        // A step is the grid's state: the per-core values, the mode, the chip and common values. A run of changes with
        // less than half a second between them, or made while a mouse button is held (a slider dragged), is one step;
        // «Сбросить» and a set from the history are steps too. The steps belong to the preset shown: another preset picked
        // starts afresh; saving and applying keep them. Nothing is applied by a step: «Применить» does that.
        readonly System.Collections.Generic.List<CurvePreset> back = new System.Collections.Generic.List<CurvePreset>(), forward = new System.Collections.Generic.List<CurvePreset>();
        CurvePreset seen; string seenName; bool gesture;
        readonly Timer gestureEnd = new Timer { Interval = 500 };

        void ResetEdits() { gesture = false; card.Grid.LoadState(selected); card.Grid.ReadOnly = false; Record(); UpdateButtons(); }

        // the grid as a preset (null while a cell is not a number)
        CurvePreset State()
        {
            var v = card.Grid.Values;
            if (v == null) return null;
            var p = new CurvePreset { Name = selected.Name };
            Keep(p, (int[])v.Clone());
            return p;
        }

        static bool Same(CurvePreset a, CurvePreset b)
        {
            Func<int[], int[], bool> eq = (x, y) => x == null ? y == null : y != null && x.SequenceEqual(y);
            return a.Mode == b.Mode && a.All == b.All && eq(a.Values, b.Values) && eq(a.Chip, b.Chip) && eq(a.Core, b.Core);
        }

        // a preset loaded into the grid: another one starts the steps afresh, the same one (saved, applied) keeps them
        void Baseline()
        {
            if (seenName != selected.Name) { back.Clear(); forward.Clear(); }
            gesture = false; gestureEnd.Stop();
            seen = State(); seenName = selected.Name;
        }

        // a change in the grid: the state before it is a step back (once per run of changes)
        void Record()
        {
            var now = State();
            if (now == null) return; // a cell being typed, not a number yet
            if (seen == null) { seen = now; return; }
            if (Same(now, seen)) return;
            if (!gesture) { back.Add(seen); if (back.Count > 200) back.RemoveAt(0); forward.Clear(); gesture = true; }
            seen = now;
            gestureEnd.Stop(); gestureEnd.Start();
        }

        bool Step(System.Collections.Generic.List<CurvePreset> from, System.Collections.Generic.List<CurvePreset> to)
        {
            var now = State();
            if (from.Count == 0 || now == null) return false;
            to.Add(now);
            var snap = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
            card.Grid.Restore(snap, selected.Values);
            card.Grid.ReadOnly = false;
            seen = State(); gesture = false; gestureEnd.Stop();
            UpdateButtons();
            return true;
        }

        // Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z from the main window while this page is shown. The grid's own cells hand them to
        // the page too (each typed number is in the steps); any other text field keeps its own.
        public bool HandleKey(Keys keyData)
        {
            bool undo = keyData == (Keys.Control | Keys.Z), redo = keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z);
            if (!Visible || selected == null || (!undo && !redo)) return false;
            var form = FindForm();
            var tb = form != null ? form.ActiveControl as TextBoxBase : null;
            if (tb != null && tb.Parent != card.Grid) return false;
            if (Control.MouseButtons != MouseButtons.None) return true; // something is being dragged: not under its hand
            if (undo) Step(back, forward); else Step(forward, back);
            return true;
        }

        // --curve-undo-check: the steps back and forward in the grid, nothing applied or saved
        internal string UndoCheck()
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            var z = Keys.Control | Keys.Z; var y = Keys.Control | Keys.Y;
            var g = card.Grid;
            g.SetMode(0, false); g.LoadState(selected); Baseline(); back.Clear(); forward.Clear();
            var v0 = (int[])g.Values.Clone();
            line(HandleKey(z) && g.Values.SequenceEqual(v0), "nothing changed yet: Ctrl+Z is taken and changes nothing");
            var v1 = (int[])v0.Clone(); v1[0] = v0[0] > -20 ? v0[0] - 5 : v0[0] + 5; v1[1] = v0[1] > -20 ? v0[1] - 3 : v0[1] + 3;
            g.Load(new[] { v1[0] }.Concat(v0.Skip(1)).ToArray(), selected.Values); Record();
            g.Load(v1, selected.Values); Record();
            line(back.Count == 1, "two cores moved in one run: one step back (" + back.Count + ")");
            gesture = false; gestureEnd.Stop();
            int m2 = Hw.CurveChips > 1 ? 1 : 2;
            g.SetMode(m2, false);
            line(back.Count == 2 && g.Mode == m2, "after a pause the mode switched: a second step (" + back.Count + ")");
            HandleKey(z);
            line(g.Mode == 0 && g.Values.SequenceEqual(v1) && back.Count == 1 && forward.Count == 1, "Ctrl+Z: the mode is back, the cores stay moved (" + g.Values[0] + ", " + g.Values[1] + ")");
            HandleKey(z);
            line(g.Values.SequenceEqual(v0) && back.Count == 0 && forward.Count == 2 && !Dirty(g.Values), "Ctrl+Z again: the preset's values, nothing unsaved");
            line(!saveBtn.Enabled, "«Сохранить» is grey again");
            HandleKey(z);
            line(g.Values.SequenceEqual(v0) && forward.Count == 2, "a third Ctrl+Z: nothing more to take back");
            HandleKey(y); HandleKey(Keys.Control | Keys.Shift | Keys.Z);
            line(g.Mode == m2 && forward.Count == 0 && back.Count == 2 && saveBtn.Enabled, "Ctrl+Y and Ctrl+Shift+Z: both steps again, «Сохранить» lit");
            HandleKey(z); gesture = false;
            ResetEdits();
            line(g.Values.SequenceEqual(v0) && g.Mode == 0 && back.Count == 2 && forward.Count == 0, "«Сбросить» after an edit is a step of its own (" + back.Count + ")");
            HandleKey(z);
            line(g.Values.SequenceEqual(v1), "Ctrl+Z after «Сбросить»: the edit is back (" + g.Values[0] + ", " + g.Values[1] + ")");
            HandleKey(z);
            line(g.Values.SequenceEqual(v0) && !Dirty(g.Values), "and once more: the preset as it was");
            return sb.ToString();
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
            var shown = selected.Name;
            curve.Apply(v, selected.Name + (dirty && selected.BuiltIn ? " (изменён)" : ""), "вручную", ok =>
            {
                Select(selected); card.Grid.Load(v, selected.Values); seen = State(); UpdateButtons();
                // 23.11 (the user): «Применение кривой» in the notifications covers this button too, as it does the mini panel and
                // the power modes; a failure is on the page itself (the pill)
                if (ok) Osd.Show("curve", "Кривая «" + shown + "» применена");
            });
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
                    gesture = false; // a step of its own
                    card.Grid.SetMode(0, false); // a set from the history is a value per core
                    card.Grid.Load(f.Picked.Values, selected.Values);
                    Record();
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
