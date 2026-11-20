using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // Curve Optimizer values as vertical sliders, one per core, grouped by chiplet (CCD1, CCD2 when there are two; 13.11:
    // any Ryzen, 4 to 16+ cores, from Hw) with an exact value box under each. Drag a slider, or click it and use the wheel; edited ones turn light blue.
    class CoreGrid : Painted
    {
        const int VMin = Smu.Min, VMax = Smu.Max;
        static int N { get { return Hw.CurveCores; } }
        readonly TextBox[] boxes = new TextBox[N];
        int[] reference = new int[N];
        bool loading, readOnly;
        int drag = -1, armed = -1, hot = -1;
        int pressY, pressValue; bool relative; // the drag goes from the knob (see OnMouseDown)
        public event EventHandler Changed;

        // 14.11: the core's number over its column is a small button: it opens that core's voltage/clock curve
        public event Action<int> CoreClicked;
        int hotNum = -1;
        RectangleF NumRect(int i) { float w = Math.Min(ColW - Theme.S(6), Theme.S(30)); return new RectangleF(ColX(i) + (ColW - w) / 2f, Theme.S(22) + HeadGap, w, Theme.S(19)); }
        // 17.11: the chips with the core numbers stood right under «CCD1» / «CCD2», nearly touching the print; they and
        // everything under them stand this much lower now (the grid is taller by as much, the sliders as long as before)
        static int HeadGap { get { return Theme.S(6); } }
        int NumAt(Point p) { for (int i = 0; i < N; i++) if (NumRect(i).Contains(p)) return i; return -1; }

        // 14.11: the group modes. Instead of a value per core the curve can be one value per chiplet (mode 1) or one
        // value for every core (mode 2). Their sliders are extra columns that slide out on the left (indexes N, N+1 …);
        // the per-core columns then turn grey, cannot be moved and show what each core gets. Back in mode 0 the group
        // columns slide away and the cores get their own values back (coreKeep).
        int mode, lastGroup = 2;
        static int Chips { get { return Hw.CurveChips; } }
        readonly TextBox[] gBoxes = new TextBox[Hw.CurveChips];
        int[] coreKeep = new int[N], chipVals = new int[Hw.CurveChips];
        int allVal;
        bool chipSet, allSet;
        float open; // 0..1: how far the group columns are out
        float openFrom, openTo;
        readonly System.Diagnostics.Stopwatch openClock = new System.Diagnostics.Stopwatch();
        readonly Timer openTimer = new Timer { Interval = 15 };
        // 16.11: from «по чипам» to «общая» and back the columns used to change in place, with no movement at all (open
        // stayed 1). Now the old group's columns slide away and the new one's slide out: viewGroup is the group on
        // screen (it follows lastGroup when the old columns are gone), ghost holds the old columns' values while they
        // leave (their boxes already carry the new group's numbers).
        int viewGroup = 2;
        int[] ghost;
        bool swapPending;
        float openMs = 300f;
        int ease; // 0: in and out (a plain slide), 1: in (the old group leaving), 2: out (the new one arriving)
        const float SwapMs = 210f;
        int K { get { return viewGroup == 1 ? Chips : 1; } } // how many group columns

        TextBox NewBox(int idx)
        {
            var b = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Theme.Bold, TextAlign = HorizontalAlignment.Center, MaxLength = 4 };
            b.TextChanged += (s, e) =>
            {
                if (loading) return;
                if (idx >= N) FromGroup(); else Recolor(idx);
                var h = Changed; if (h != null) h(this, EventArgs.Empty);
            };
            b.MouseWheel += (s, e) => { if (!b.Focused) return; Step(idx, e.Delta > 0 ? 1 : -1); var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; };
            b.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != '-') e.Handled = true; };
            Controls.Add(b);
            return b;
        }

        public CoreGrid()
        {
            BackColor = Theme.Card;
            Height = Theme.S(340) + HeadGap;
            for (int i = 0; i < N; i++) boxes[i] = NewBox(i);
            for (int j = 0; j < gBoxes.Length; j++) { gBoxes[j] = NewBox(N + j); gBoxes[j].Visible = false; }
            openTimer.Tick += (s, e) =>
            {
                float t = Math.Min(1f, openClock.ElapsedMilliseconds / openMs);
                float k = ease == 1 ? t * t * t : ease == 2 ? 1f - (float)Math.Pow(1f - t, 3) : t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f;
                open = openFrom + (openTo - openFrom) * k;
                if (t >= 1f)
                {
                    open = openTo;
                    if (swapPending)
                    {
                        // the old group is gone: the new one comes out of the same edge
                        swapPending = false; ghost = null; viewGroup = lastGroup;
                        openFrom = 0f; openTo = 1f; ease = 2; openMs = SwapMs; openClock.Restart();
                    }
                    else { openTimer.Stop(); if (open <= 0f) ghost = null; }
                }
                PerformLayout(); Invalidate(); Update();
            };
        }

        protected override void Dispose(bool disposing) { if (disposing) { openTimer.Stop(); openTimer.Dispose(); } base.Dispose(disposing); }

        TextBox Box(int i) { return i >= N ? gBoxes[i - N] : boxes[i]; }

        bool TryGet(int i, out int v)
        {
            return int.TryParse(Box(i).Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) && v >= Smu.Min && v <= Smu.Max;
        }

        void SetValue(int i, int v)
        {
            if (readOnly || (i < N && mode != 0)) return;
            v = Math.Max(Smu.Min, Math.Min(Smu.Max, v));
            int old;
            if (TryGet(i, out old) && old == v) return;
            Box(i).Text = v.ToString(CultureInfo.InvariantCulture);
        }

        // ---------- the group modes ----------

        public int Mode { get { return mode; } }
        // the per-core set: what the columns show in mode 0, what is kept aside in a group mode
        public int[] CoreKeep { get { return mode == 0 ? Values : (int[])coreKeep.Clone(); } }
        public int[] ChipVals { get { return chipSet ? (int[])chipVals.Clone() : null; } }
        public int? AllVal { get { return allSet ? allVal : (int?)null; } }

        // the group sliders' values go to every core of their group (nothing while one of them is not a number)
        void FromGroup()
        {
            if (mode == 0) return;
            int k = mode == 1 ? Chips : 1;
            var gv = new int[k];
            for (int j = 0; j < k; j++) if (!TryGet(N + j, out gv[j])) { Invalidate(); return; }
            if (mode == 1) { chipVals = gv; chipSet = true; } else { allVal = gv[0]; allSet = true; }
            bool was = loading; loading = true;
            for (int i = 0; i < N; i++) boxes[i].Text = (mode == 1 ? gv[Hw.ChipOf(i)] : gv[0]).ToString(CultureInfo.InvariantCulture);
            loading = was;
            for (int i = 0; i < N; i++) Recolor(i);
        }

        void ShowGroup()
        {
            bool was = loading; loading = true;
            if (mode == 1) for (int j = 0; j < Chips; j++) gBoxes[j].Text = chipVals[j].ToString(CultureInfo.InvariantCulture);
            else if (mode == 2) gBoxes[0].Text = allVal.ToString(CultureInfo.InvariantCulture);
            loading = was;
            FromGroup();
        }

        void Slide(float to, bool animate)
        {
            openTimer.Stop(); swapPending = false;
            if (!animate || !IsHandleCreated || !Visible) { open = to; ghost = null; if (mode != 0) viewGroup = lastGroup; PerformLayout(); Invalidate(); return; }
            openFrom = open; openTo = to; openMs = 300f; ease = 0; openClock.Restart(); openTimer.Start();
        }

        // one group to the other: the columns on screen slide away first (the timer then brings the new ones out)
        void Swap()
        {
            openTimer.Stop(); swapPending = true;
            openFrom = open; openTo = 0f; ease = 1; openMs = Math.Max(60f, SwapMs * open); openClock.Restart(); openTimer.Start();
        }

        // the switch above the grid. A group mode taken for the first time starts from the mildest value of its cores
        // (the one nearest zero from below, the highest): a value every core of the group already runs at or beyond.
        public void SetMode(int m, bool animate = true)
        {
            if (m == 1 && Chips < 2) m = 2;
            if (m == mode) return;
            if (mode == 0) coreKeep = Values ?? (int[])reference.Clone();
            // another group's columns are on screen (also while they are still sliding away after «по ядрам»): they
            // leave with the values they show now, taken before the boxes get the new group's numbers
            bool swap = m != 0 && animate && IsHandleCreated && Visible && open > 0.01f && viewGroup != m;
            if (swap && ghost == null)
            {
                ghost = new int[K];
                for (int j = 0; j < ghost.Length; j++) if (!TryGet(N + j, out ghost[j])) ghost[j] = int.MinValue;
            }
            mode = m;
            if (m == 0)
            {
                loading = true;
                for (int i = 0; i < N; i++) boxes[i].Text = coreKeep[i].ToString(CultureInfo.InvariantCulture);
                loading = false;
            }
            else
            {
                lastGroup = m;
                if (m == 1 && !chipSet) { for (int c = 0; c < Chips; c++) chipVals[c] = Hw.ChipCores(c).Max(i => coreKeep[i]); chipSet = true; }
                if (m == 2 && !allSet) { allVal = coreKeep.Max(); allSet = true; }
                ShowGroup();
            }
            ApplyReadOnly();
            for (int i = 0; i < N; i++) Recolor(i);
            if (swap) { PerformLayout(); Swap(); }
            else
            {
                if (m != 0) { viewGroup = m; ghost = null; } // the same group again (a swap called off), or nothing on screen yet
                Slide(m == 0 ? 0f : 1f, animate);
            }
            var h = Changed; if (h != null) h(this, EventArgs.Empty);
        }

        // a preset: its mode and all it keeps, shown at once (no slide, no Changed)
        public void LoadState(CurvePreset p)
        {
            loading = true;
            reference = (int[])Hw.Fit(p.Values).Clone();
            coreKeep = (int[])Hw.Fit(p.Core ?? p.Values).Clone();
            chipSet = p.Chip != null && p.Chip.Length == Chips; chipVals = chipSet ? (int[])p.Chip.Clone() : new int[Chips];
            allSet = p.All.HasValue; allVal = p.All ?? 0;
            mode = p.Mode == 1 && chipSet && Chips > 1 ? 1 : p.Mode == 2 && allSet ? 2 : 0;
            if (mode != 0) lastGroup = viewGroup = mode;
            var v = Hw.Fit(p.Values);
            for (int i = 0; i < N; i++) boxes[i].Text = v[i].ToString(CultureInfo.InvariantCulture);
            if (mode == 1) for (int j = 0; j < Chips; j++) gBoxes[j].Text = chipVals[j].ToString(CultureInfo.InvariantCulture);
            else if (mode == 2) gBoxes[0].Text = allVal.ToString(CultureInfo.InvariantCulture);
            loading = false;
            ApplyReadOnly();
            for (int i = 0; i < N; i++) Recolor(i);
            Slide(mode == 0 ? 0f : 1f, false);
        }

        // 20.11: a step back or forward on the curve page: the state of a snapshot, the light blue still against the preset
        public void Restore(CurvePreset s, int[] against)
        {
            LoadState(s);
            reference = (int[])Hw.Fit(against).Clone();
            for (int i = 0; i < N; i++) Recolor(i);
        }

        void ApplyReadOnly() { foreach (var b in boxes) b.ReadOnly = readOnly || mode != 0; foreach (var b in gBoxes) b.ReadOnly = readOnly; }

        void Step(int i, int d) { int v; if (TryGet(i, out v)) SetValue(i, v + d); }

        // null when a cell is not a number in range
        public int[] Values
        {
            get { var v = new int[N]; for (int i = 0; i < N; i++) if (!TryGet(i, out v[i])) return null; return v; }
        }

        // values and the reference they are compared with (edited sliders turn light blue)
        public void Load(int[] values, int[] reference)
        {
            loading = true;
            values = Hw.Fit(values); this.reference = (int[])Hw.Fit(reference ?? values).Clone();
            for (int i = 0; i < N; i++) boxes[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
            loading = false;
            for (int i = 0; i < N; i++) Recolor(i);
        }

        public bool ReadOnly { set { readOnly = value; ApplyReadOnly(); } }

        void Recolor(int i)
        {
            int v;
            boxes[i].ForeColor = !TryGet(i, out v) ? Theme.Danger : mode != 0 ? Theme.Muted : v != reference[i] ? Theme.AccentFg : Theme.Text;
            Invalidate();
        }

        // ---------- geometry ----------

        int LabelW { get { return Theme.S(34); } }
        int Gap { get { return Theme.S(26); } }
        // columns as wide as fit, at most ~70 px (a 6-core CPU doesn't get giant sliders); the block is centred
        // the group columns take their room on the left as they come out (open): K columns and a gap
        float Avail { get { return Width - Theme.S(16) - LabelW; } }
        float ColW { get { return Math.Min(Theme.S(70), (Avail - Gap * (Hw.CurveChips - 1) - Gap * open) / (N + K * open)); } }
        float GroupW { get { return (K * ColW + Gap) * open; } }
        float GridLeft { get { float used = N * ColW + Gap * (Hw.CurveChips - 1) + GroupW; return Theme.S(8) + LabelW + Math.Max(0, (Avail - used) / 2f); } }
        // a group column (i ≥ N) comes out from behind the left edge of the block
        float ColX(int i) { return i >= N ? GridLeft + (i - N) * ColW - (1f - open) * K * ColW : GridLeft + GroupW + Hw.ChipOf(i) * Gap + i * ColW; }
        int TrackTop { get { return Theme.S(52) + HeadGap; } }
        int TrackBottom { get { return Height - Theme.S(52); } }
        float Y(int v) { v = Math.Max(VMin, Math.Min(VMax, v)); return TrackTop + (float)(VMax - v) / (VMax - VMin) * (TrackBottom - TrackTop); }
        int V(int y) { return (int)Math.Round(VMax - (double)(y - TrackTop) / (TrackBottom - TrackTop) * (VMax - VMin)); }

        int Column(Point p)
        {
            if (p.Y < TrackTop - Theme.S(14) || p.Y > TrackBottom + Theme.S(14)) return -1;
            if (mode != 0)
            {
                // only the group columns move; the cores are for looking
                if (ghost != null) return -1; // the old group is still leaving
                for (int j = 0; j < K; j++) if (p.X >= ColX(N + j) && p.X < ColX(N + j) + ColW) return N + j;
                return -1;
            }
            for (int i = 0; i < N; i++) if (p.X >= ColX(i) && p.X < ColX(i) + ColW) return i;
            return -1;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (boxes == null || boxes[N - 1] == null) return; // the base constructor lays out before the cells exist
            for (int i = 0; i < N; i++)
            {
                var b = boxes[i];
                int w = (int)ColW - Theme.S(8);
                b.Bounds = new Rectangle((int)ColX(i) + Theme.S(4), Height - Theme.S(34) + (Theme.S(28) - b.PreferredHeight) / 2, w, b.PreferredHeight);
            }
            for (int j = 0; j < gBoxes.Length; j++)
            {
                var b = gBoxes[j];
                if (b == null) continue; // the constructor is still making them
                bool show = mode != 0 && j < K && open > 0.97f && ghost == null; // in place: not while the columns are still sliding
                if (b.Visible != show) b.Visible = show;
                if (show) b.Bounds = new Rectangle((int)ColX(N + j) + Theme.S(4), Height - Theme.S(34) + (Theme.S(28) - b.PreferredHeight) / 2, (int)ColW - Theme.S(8), b.PreferredHeight);
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int num = NumAt(e.Location);
            if (num >= 0 && e.Button == MouseButtons.Left) { var h = CoreClicked; if (h != null) h(num); base.OnMouseDown(e); return; }
            int c = Column(e.Location);
            if (c >= 0 && e.Button == MouseButtons.Left)
            {
                drag = armed = c; Focus();
                // 20.11 (the user's wish, as the equalizer's faders): a press on the line leaves the value as it is and puts
                // the pointer onto the knob; the drag then moves the value by as much as the mouse moves from there.
                // A column without a number (a cell being typed) still takes the value under the press.
                int pv;
                if (!readOnly && TryGet(c, out pv))
                {
                    pressValue = pv; float ky = Y(pv);
                    pressY = Theme.Warp(this, ColX(c) + ColW / 2f, ky) ? (int)Math.Round(ky) : e.Y;
                    relative = true;
                }
                else { relative = false; SetValue(c, V(e.Y)); }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag >= 0)
            {
                if (relative) SetValue(drag, pressValue + (int)Math.Round((double)(pressY - e.Y) / Math.Max(1, TrackBottom - TrackTop) * (VMax - VMin)));
                else SetValue(drag, V(e.Y));
            }
            int h = drag >= 0 ? drag : Column(e.Location);
            int num = drag >= 0 ? -1 : NumAt(e.Location);
            Cursor = num >= 0 ? Cursors.Hand : h >= 0 && !readOnly ? Cursors.SizeNS : Cursors.Default;
            if (h != hot || num != hotNum) { hot = h; hotNum = num; Invalidate(); }
            if (armed >= 0 && Column(e.Location) != armed && drag < 0) armed = -1;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = -1; base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { armed = -1; hot = -1; hotNum = -1; Invalidate(); base.OnMouseLeave(e); }

        // part of --slider-warp-check: the curve grid, a press far from core 0's knob and a drag (WarpPointer is off there)
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        internal static void WarpCheck(Action<bool, string> line)
        {
            using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(900, 500) })
            {
                var g = new CoreGrid { Width = 860 };
                f.Controls.Add(g); f.Show(); Application.DoEvents();
                var v = new int[N]; v[0] = -10; g.Load(v, v);
                Func<int, int, IntPtr> at = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
                int x0 = (int)(g.ColX(0) + g.ColW / 2), yb = g.TrackBottom - 2, ky = (int)Math.Round(g.Y(-10));
                SendMessage(g.Handle, 0x201, (IntPtr)1, at(x0, yb));
                line(g.Values[0] == -10, "grid: a press at the bottom of core 0's line, far from its knob: the value stays " + g.Values[0]);
                double per = (g.TrackBottom - g.TrackTop) / (double)(VMax - VMin);
                SendMessage(g.Handle, 0x200, (IntPtr)1, at(x0, yb - (int)Math.Round(5 * per)));
                line(g.Values[0] == -5, "grid: 5 steps up from there: " + g.Values[0] + " (-5 expected), not the bottom of the range");
                SendMessage(g.Handle, 0x202, IntPtr.Zero, at(x0, yb));
                line(g.Values[1] == 0, "grid: the other cores untouched (" + g.Values[1] + ")");
            }
        }

        // the wheel moves a slider only after it was clicked; otherwise the page scrolls
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int c = Column(e.Location);
            if (armed < 0 || c != armed) { base.OnMouseWheel(e); return; }
            Step(c, e.Delta > 0 ? 1 : -1);
            var he = e as HandledMouseEventArgs;
            if (he != null) he.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cw = ColW;
            for (int grp = 0; grp < Hw.CurveChips; grp++)
            {
                var cores = Hw.ChipCores(grp);
                float gx = ColX(cores[0]), gw = cores.Length * cw;
                Theme.Text1(g, "CCD" + (grp + 1), Theme.Bold, new Rectangle((int)gx, 0, (int)gw, Theme.S(22)), Theme.Text2, TextFormatFlags.HorizontalCenter);
                for (int k = VMin; k <= VMax; k += 10)
                    using (var pen = new Pen(k == 0 ? Theme.BorderStrong : Color.FromArgb(0x35, 0x34, 0x31)) { DashStyle = k == 0 ? DashStyle.Solid : DashStyle.Dot })
                        g.DrawLine(pen, gx, Y(k), gx + gw, Y(k));
            }
            for (int k = VMax; k >= VMin; k -= 10)
                Theme.Text1(g, k > 0 ? "+" + k : k.ToString(), Theme.Small, new Rectangle((int)(GridLeft - LabelW - Theme.S(8)), (int)Y(k) - Theme.S(9), LabelW + Theme.S(2), Theme.S(18)), Theme.Muted, TextFormatFlags.Right);

            // the group columns, as far as they are out (cut at the left edge of the block they come from)
            if (open > 0.01f)
            {
                var st = g.Save();
                g.SetClip(new RectangleF(GridLeft, 0, K * cw * open + 1, Height), CombineMode.Intersect);
                float bx = ColX(N), bw = K * cw;
                Theme.Text1(g, viewGroup == 1 ? "Чипы" : "Все", Theme.Bold, new Rectangle((int)bx, 0, (int)bw, Theme.S(22)), Theme.Text2, TextFormatFlags.HorizontalCenter);
                for (int k = VMin; k <= VMax; k += 10)
                    using (var pen = new Pen(k == 0 ? Theme.BorderStrong : Color.FromArgb(0x35, 0x34, 0x31)) { DashStyle = k == 0 ? DashStyle.Solid : DashStyle.Dot })
                        g.DrawLine(pen, bx, Y(k), bx + bw, Y(k));
                for (int j = 0; j < K; j++)
                {
                    float cx = ColX(N + j) + cw / 2;
                    if (viewGroup == 1) Theme.Text1(g, (j + 1).ToString(), Theme.Small, new Rectangle((int)ColX(N + j), Theme.S(24) + HeadGap, (int)cw, Theme.S(18)), N + j == hot ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter);
                    Theme.Tube(g, new RectangleF(cx - Theme.S(3), TrackTop, Theme.S(6), TrackBottom - TrackTop));
                    int v;
                    if (ghost != null) { if (j >= ghost.Length || ghost[j] == int.MinValue) continue; v = ghost[j]; }
                    else if (!TryGet(N + j, out v)) continue;
                    float y0 = Y(0), y1 = Y(v);
                    var col = Theme.Heat(v == 0 ? 0f : Math.Abs(v) / (float)Math.Abs(v < 0 ? VMin : VMax));
                    Theme.FillGradientV(g, new RectangleF(cx - Theme.S(3), Math.Min(y0, y1), Theme.S(6), Math.Abs(y1 - y0)), Theme.S(3), y0, Y(v < 0 ? VMin : VMax), false);
                    float kn = Theme.KnobSize + (N + j == hot || N + j == drag ? Theme.S(2) : 0);
                    Theme.GlassKnob(g, cx, y1, kn, v == 0 ? 0f : 1f, col, Theme.S(6), false, v == 0 ? 180f : v < 0 ? 270f : 90f, null, Math.Abs(y1 - y0));
                    if (open > 0.97f) Theme.Fill(g, new RectangleF(ColX(N + j) + Theme.S(2) + 0.5f, Height - Theme.S(34) + 0.5f, cw - Theme.S(4) - 1, Theme.S(28) - 1), Theme.S(6), Theme.Raised);
                }
                g.Restore(st);
            }

            for (int i = 0; i < N; i++)
            {
                float cx = ColX(i) + cw / 2;
                // the number is a button (the core's own curve): a faint outline, lit under the mouse
                var nr = NumRect(i);
                if (i == hotNum) Theme.Fill(g, nr, Theme.S(5), Theme.Raised);
                Theme.Stroke(g, nr, Theme.S(5), i == hotNum ? Theme.BorderStrong : Theme.Border);
                Theme.Text1(g, (i - Hw.ChipCores(Hw.ChipOf(i))[0] + 1).ToString(), Theme.Small, Rectangle.Round(nr), i == hot || i == hotNum ? Theme.Text : Theme.Text2, TextFormatFlags.HorizontalCenter);
                var track = new RectangleF(cx - Theme.S(3), TrackTop, Theme.S(6), TrackBottom - TrackTop);
                Theme.Tube(g, track); // 14.11: the same glass tube and glass knob as the sliders
                int v;
                bool ok = TryGet(i, out v);
                if (!ok) continue;
                if (mode != 0)
                {
                    // a group mode: the cores are grey and only show what each of them gets
                    float gy0 = Y(0), gy1 = Y(v);
                    Theme.Fill(g, new RectangleF(cx - Theme.S(3), Math.Min(gy0, gy1), Theme.S(6), Math.Abs(gy1 - gy0)), Theme.S(3), Color.FromArgb(0x5c, 0x58, 0x56));
                    Theme.GlassKnob(g, cx, gy1, Theme.KnobSize, v == 0 ? 0f : 1f, Theme.Muted, Theme.S(6), true, v == 0 ? 180f : v < 0 ? 270f : 90f, null, Math.Abs(gy1 - gy0));
                    continue;
                }
                bool edited = v != reference[i];
                // 14.11: the bar in the gradient of its half of the scale (0 … −50 or 0 … +30): the further from zero, the
                // brighter its end and the light in the knob
                float y0 = Y(0), y1 = Y(v);
                var col = Theme.Heat(v == 0 ? 0f : Math.Abs(v) / (float)Math.Abs(v < 0 ? VMin : VMax));
                if (edited) col = Theme.Pale(col);
                Theme.FillGradientV(g, new RectangleF(cx - Theme.S(3), Math.Min(y0, y1), Theme.S(6), Math.Abs(y1 - y0)), Theme.S(3), y0, Y(v < 0 ? VMin : VMax), edited);
                // the bar comes into the knob from the zero line: from above for a negative value, from below for a positive
                // one; at zero the glass holds only the pale streak
                float k = Theme.KnobSize + (i == hot || i == drag ? Theme.S(2) : 0);
                Theme.GlassKnob(g, cx, y1, k, v == 0 ? 0f : 1f, col, Theme.S(6), false, v == 0 ? 180f : v < 0 ? 270f : 90f, null, Math.Abs(y1 - y0));
            }
            for (int i = 0; i < N; i++)
            {
                int v;
                var r = new RectangleF(ColX(i) + Theme.S(2) + 0.5f, Height - Theme.S(34) + 0.5f, ColW - Theme.S(4) - 1, Theme.S(28) - 1);
                Theme.Fill(g, r, Theme.S(6), Theme.Raised);
                bool bad = !TryGet(i, out v);
                if (bad || (mode == 0 && v != reference[i])) Theme.Stroke(g, r, Theme.S(6), bad ? Theme.Danger : Theme.Accent);
            }
        }
    }
}
