using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // The V/F curve editor, the way Afterburner draws it: voltage across, frequency up; the factory curve dotted, the
    // curve being made solid with its points. Drag a point up or down; with Shift the whole curve moves. The driver
    // never lets the curve fall, so what is drawn is the running maximum of the points (a point pushed low follows the
    // one before it: that is how a flat top, the usual undervolt, works).
    class VfGraph : Painted
    {
        public int[] Mv = new int[0], Base = new int[0], Target = new int[0];
        public bool[] Known = new bool[0]; // the factory frequency of the point is known (not hidden under a flat top)
        public int Selected = -1;
        // 23.11: the core voltage-demand offset in the editor, mV: the card runs every point at its voltage + this, so the curve
        // being made is drawn moved by it (left for a minus) against the factory one, which stays where it is
        public int VoltShift;
        public int VoltAt(int k) { return Mv[k] + VoltShift; }
        public event EventHandler Changed, SelectionChanged, ZoneChanged;
        public event EventHandler Changing; // just before the points change: the page keeps the step for Ctrl+Z (16.11)
        bool dragChanged;
        void RaiseChanging() { var h = Changing; if (h != null) h(this, EventArgs.Empty); }
        int drag = -1, dragY, hot = -1;
        int[] dragStart;
        // 16.11: a zone, a run of points picked by dragging across the graph with Ctrl or with the right button; its
        // corners can be rounded (Smooth) or it can be made a straight line between its two ends (Straighten)
        public int ZoneA = -1, ZoneB = -1;
        int zoneFrom = -1;
        public bool HasZone { get { return ZoneA >= 0 && ZoneB > ZoneA && ZoneB < Target.Length; } }

        public VfGraph() { Height = Theme.S(330); BackColor = Theme.Card; Cursor = Cursors.Cross; }

        RectangleF Plot { get { return new RectangleF(Theme.S(58), Theme.S(14), Width - Theme.S(76), Height - Theme.S(48)); } }
        int MinMv { get { return Mv.Length > 0 ? Mv[0] + Math.Min(0, VoltShift) - 10 : 400; } }
        int MaxMv { get { return Mv.Length > 0 ? Mv[Mv.Length - 1] + Math.Max(0, VoltShift) + 10 : 1250; } }
        int MaxMhz { get { return (int)(Math.Ceiling(Math.Max(Base.DefaultIfEmpty(3000).Max(), Shown().DefaultIfEmpty(3000).Max()) / 250.0) * 250 + 250); } }

        float X(int mv) { var p = Plot; return p.X + p.Width * (mv - MinMv) / (float)(MaxMv - MinMv); }
        float PX(int k) { return X(VoltAt(k)); } // where point k of the curve being made is drawn
        float Y(int mhz) { var p = Plot; return p.Bottom - p.Height * mhz / (float)MaxMhz; }
        int MhzAt(float y) { var p = Plot; return (int)Math.Round((p.Bottom - y) / p.Height * MaxMhz); }

        // what the card will run: the curve never falls
        public int[] Shown()
        {
            var r = new int[Target.Length]; int m = 0;
            for (int k = 0; k < Target.Length; k++) { m = Math.Max(m, Target[k]); r[k] = m; }
            return r;
        }

        int Nearest(Point e)
        {
            if (Mv.Length == 0 || !Plot.Contains(e)) return -1;
            var s = Shown(); int best = -1; float bd = float.MaxValue;
            for (int k = 0; k < Mv.Length; k++)
            {
                float dx = PX(k) - e.X, dy = Y(s[k]) - e.Y, d = dx * dx + dy * dy * 0.25f;
                if (d < bd) { bd = d; best = k; }
            }
            return best;
        }

        int IndexAtX(int x)
        {
            int best = -1; float bd = float.MaxValue;
            for (int k = 0; k < Mv.Length; k++) { float d = Math.Abs(PX(k) - x); if (d < bd) { bd = d; best = k; } }
            return best;
        }

        void RaiseZone() { var h = ZoneChanged; if (h != null) h(this, EventArgs.Empty); }
        public void ClearZone() { ZoneA = ZoneB = zoneFrom = -1; Invalidate(); RaiseZone(); }

        // a point of the zone moved to mhz: in the driver's 15 MHz steps from its factory frequency, within what a point may be
        void SetPoint(int k, double mhz)
        {
            int d = (int)Math.Round((mhz - Base[k]) / 15.0) * 15;
            Target[k] = Clamp(k, Base[k] + d);
        }

        // the corners of the zone rounded: each inner point goes towards the middle of its neighbours (1-2-1), a few passes
        // a press; the two end points stay, so the zone still meets the rest of the curve. Works on the curve as drawn.
        public void Smooth(int passes = 3)
        {
            if (!HasZone) return;
            RaiseChanging();
            var s = Shown().Select(v => (double)v).ToArray();
            for (int p = 0; p < passes; p++)
            {
                var n = (double[])s.Clone();
                for (int k = ZoneA + 1; k < ZoneB; k++) n[k] = (s[k - 1] + 2 * s[k] + s[k + 1]) / 4.0;
                s = n;
            }
            for (int k = ZoneA + 1; k < ZoneB; k++) SetPoint(k, s[k]);
            Invalidate();
            var h = Changed; if (h != null) h(this, EventArgs.Empty);
        }

        // a straight line from the first point of the zone to the last one (by voltage)
        public void Straighten()
        {
            if (!HasZone) return;
            RaiseChanging();
            var s = Shown();
            double a = s[ZoneA], b = s[ZoneB], span = Mv[ZoneB] - Mv[ZoneA];
            for (int k = ZoneA + 1; k < ZoneB; k++) SetPoint(k, span > 0 ? a + (b - a) * (Mv[k] - Mv[ZoneA]) / span : a);
            Invalidate();
            var h = Changed; if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && Plot.Contains(e.Location) && (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) != 0)))
            {
                zoneFrom = IndexAtX(e.X); ZoneA = ZoneB = zoneFrom;
                Invalidate(); RaiseZone();
                return;
            }
            if (!Enabled || e.Button != MouseButtons.Left) return;
            int k = Nearest(e.Location); if (k < 0) return;
            // 16.11: a click anywhere picks the nearest point; it moves only when taken by the point itself. Before, a press
            // far from the curve already dragged the nearest point, so picking one could shift it by accident.
            Selected = k;
            if (OnPoint(k, e.Location)) { drag = k; dragY = e.Y; dragStart = (int[])Target.Clone(); dragChanged = false; }
            var h = SelectionChanged; if (h != null) h(this, EventArgs.Empty);
            Invalidate();
        }

        // the mouse is on the point's knob (a little more than the knob, so it is easy to take)
        bool OnPoint(int k, Point e)
        {
            if (k < 0 || k >= Mv.Length) return false;
            float dx = PX(k) - e.X, dy = Y(Shown()[k]) - e.Y, r = Theme.S(13);
            return dx * dx + dy * dy <= r * r;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (zoneFrom >= 0)
            {
                int z = IndexAtX(e.X); if (z < 0) return;
                int a = Math.Min(zoneFrom, z), b = Math.Max(zoneFrom, z);
                if (a != ZoneA || b != ZoneB) { ZoneA = a; ZoneB = b; Invalidate(); RaiseZone(); }
                return;
            }
            if (drag < 0)
            {
                int k = Nearest(e.Location); if (k != hot) { hot = k; Invalidate(); }
                // the cursor says whether a press here would take the point or only pick it
                var want = Enabled && OnPoint(k, e.Location) ? Cursors.SizeNS : Cursors.Cross;
                if (Cursor != want) Cursor = want;
                return;
            }
            int dmhz = MhzAt(e.Y) - MhzAt(dragY);
            dmhz = (int)Math.Round(dmhz / 15.0) * 15; // the driver's steps are 15 MHz
            if (!dragChanged) { if (dmhz == 0) return; dragChanged = true; RaiseChanging(); } // one step for the whole drag, none for a click
            if ((ModifierKeys & Keys.Shift) != 0) for (int i = 0; i < Target.Length; i++) Target[i] = Clamp(i, dragStart[i] + dmhz);
            else Target[drag] = Clamp(drag, dragStart[drag] + dmhz);
            Invalidate();
            var h = Changed; if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); drag = -1;
            if (zoneFrom < 0) return;
            zoneFrom = -1;
            if (ZoneB <= ZoneA) ClearZone(); // a click without a drag takes the zone away
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        // a point stays within ±1000 MHz of its factory frequency (what NVML allows as a core offset)
        // a point whose factory frequency is hidden stays at or under the highest point that is known: its real frequency
        // could not be shown above that
        public int Clamp(int k, int mhz)
        {
            int v = Math.Max(Math.Max(0, Base[k] - 2000), Math.Min(Base[k] + 1000, mhz));
            if (k < Known.Length && !Known[k])
            {
                int top = 0; for (int i = 0; i < Target.Length; i++) if (Known[i]) top = Math.Max(top, Target[i]);
                v = Math.Min(v, top);
            }
            return v;
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            using (var grid = new Pen(Color.FromArgb(40, 255, 255, 255)))
            {
                for (int f = 0; f <= MaxMhz; f += 250)
                {
                    g.DrawLine(grid, p.X, Y(f), p.Right, Y(f));
                    if (f % 500 == 0) TextRenderer.DrawText(g, f.ToString(), Theme.Small, new Rectangle(0, (int)Y(f) - Theme.S(8), (int)p.X - Theme.S(6), Theme.S(16)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                for (int v = (MinMv + 49) / 50 * 50; v <= MaxMv; v += 50)
                {
                    g.DrawLine(grid, X(v), p.Y, X(v), p.Bottom);
                    if (v % 100 == 0) TextRenderer.DrawText(g, v.ToString(), Theme.Small, new Rectangle((int)X(v) - Theme.S(20), (int)p.Bottom + Theme.S(4), Theme.S(40), Theme.S(16)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }
            TextRenderer.DrawText(g, L.T("МГц"), Theme.Small, new Rectangle((int)p.X + Theme.S(6), (int)p.Y + Theme.S(2), Theme.S(60), Theme.S(14)), Theme.Muted, TextFormatFlags.Left);
            TextRenderer.DrawText(g, L.T("мВ"), Theme.Small, new Rectangle((int)p.Right - Theme.S(30), (int)p.Bottom + Theme.S(18), Theme.S(40), Theme.S(16)), Theme.Muted, TextFormatFlags.Right);
            if (Mv.Length < 2) return;

            // the zone: a band the height of the graph under the curves, its voltages on top
            if (ZoneA >= 0 && ZoneB >= ZoneA && ZoneB < Mv.Length)
            {
                float zx0 = PX(ZoneA), zx1 = PX(ZoneB);
                var zc = Enabled ? Theme.Accent : Theme.Muted;
                using (var zb = new SolidBrush(Color.FromArgb(34, zc))) g.FillRectangle(zb, zx0, p.Y, Math.Max(1f, zx1 - zx0), p.Height);
                using (var zp = new Pen(Color.FromArgb(170, zc), 1.2f)) { g.DrawLine(zp, zx0, p.Y, zx0, p.Bottom); g.DrawLine(zp, zx1, p.Y, zx1, p.Bottom); }
                if (ZoneB > ZoneA)
                {
                    var zt = VoltAt(ZoneA) + "–" + VoltAt(ZoneB) + " " + L.T("мВ");
                    var zs = TextRenderer.MeasureText(zt, Theme.Small);
                    int tx = (int)Math.Max(p.X + Theme.S(50), Math.Min(p.Right - zs.Width - Theme.S(4), (zx0 + zx1) / 2f - zs.Width / 2f));
                    TextRenderer.DrawText(g, zt, Theme.Small, new Rectangle(tx, (int)p.Y + Theme.S(2), zs.Width + Theme.S(4), Theme.S(16)), Theme.AccentFg, TextFormatFlags.Left);
                }
            }

            // the factory curve, dotted
            var fk = Enumerable.Range(0, Mv.Length).Where(k => k >= Known.Length || Known[k]).Select(k => new PointF(X(Mv[k]), Y(Base[k]))).ToArray();
            if (fk.Length > 1) using (var pen = new Pen(Theme.Muted, 1.2f) { DashStyle = DashStyle.Dot }) g.DrawLines(pen, fk);
            // the curve the card will run
            var s = Shown();
            var line = Enabled ? Theme.Accent : Theme.Muted;
            using (var pen = new Pen(line, Math.Max(1.5f, Theme.S(2))))
                g.DrawLines(pen, Enumerable.Range(0, Mv.Length).Select(k => new PointF(PX(k), Y(s[k]))).ToArray());
            using (var b = new SolidBrush(line))
                for (int k = 0; k < Mv.Length; k++)
                {
                    // 14.11: the point in hand or under the mouse is a glass knob like the sliders'; the rest stay small dots
                    if (k == Selected || k == hot) Theme.GlassKnob(g, PX(k), Y(s[k]), Theme.S(k == Selected ? 17 : 14), 1f, line, Theme.S(4.5f), !Enabled, 180f,
                        new PointF?[] { k > 0 ? new PointF(PX(k - 1), Y(s[k - 1])) : (PointF?)null, k < Mv.Length - 1 ? new PointF(PX(k + 1), Y(s[k + 1])) : (PointF?)null });
                    else { float r = Theme.S(2); g.FillEllipse(b, PX(k) - r, Y(s[k]) - r, 2 * r, 2 * r); }
                }
            int show = Selected >= 0 ? Selected : hot;
            if (show >= 0)
            {
                var t = VoltAt(show) + " " + L.T("мВ") + " · " + s[show] + " " + L.T("МГц");
                float x = PX(show), y = Y(s[show]);
                using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255)) { DashStyle = DashStyle.Dash }) { g.DrawLine(pen, x, p.Y, x, p.Bottom); g.DrawLine(pen, p.X, y, p.Right, y); }
                var sz = TextRenderer.MeasureText(t, Theme.Body);
                var box = new Rectangle((int)Math.Min(x + Theme.S(8), p.Right - sz.Width - Theme.S(8)), (int)Math.Max(p.Y, y - sz.Height - Theme.S(10)), sz.Width + Theme.S(8), sz.Height + Theme.S(4));
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Raised))) g.FillRectangle(b, box);
                TextRenderer.DrawText(g, t, Theme.Body, box, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // «Видеокарта»: the NVIDIA card's V/F curve and memory clock, like MSI Afterburner (13.11). Reads at once; writes on
    // «Применить», with a confirm the first time; presets; applied again at start if asked. An expert section.
    class GpuPage : Page
    {
        readonly Settings s;
        readonly VfGraph graph = new VfGraph();
        readonly Row head, pointRow, warnRow, presetRow, memRow, zoneRow;
        readonly FlatButton smoothBtn = new FlatButton("Сгладить"), lineBtn = new FlatButton("Выпрямить"), zoneOffBtn = new FlatButton("Снять");
        const string ZoneHint = "Протяните с Ctrl или правой кнопкой мыши";
        // 16.11: the steps of editing, for Ctrl+Z / Ctrl+Y and the two buttons: the points, the curve shift and the memory
        // slider as they were before each change. A drag, or a slider moved in one go, is one step.
        class Snap { public int[] Target, Dom; public int Shift, Mem, Volt; public bool Hold; public int[] DomV; }
        readonly List<Snap> back = new List<Snap>(), forward = new List<Snap>();
        Row histRow, shiftRow;
        string lastKind; DateTime lastStep;
        int memShown;

        Snap Take() { return new Snap { Target = (int[])graph.Target.Clone(), Shift = shiftShown, Mem = memShown, Dom = (int[])domShown.Clone(), Hold = holdShown, Volt = voltShown, DomV = (int[])dvShown.Clone() }; }

        // called before a change; the same kind of change again within a second (a slider being dragged) is the same step
        void Remember(string kind)
        {
            if (loading || curve == null) return;
            bool same = kind != null && kind == lastKind && (DateTime.Now - lastStep).TotalSeconds < 1;
            lastKind = kind; lastStep = DateTime.Now;
            if (same) return;
            back.Add(Take()); if (back.Count > 200) back.RemoveAt(0);
            forward.Clear();
            ShowHistory();
        }

        void Put(Snap p)
        {
            loading = true;
            graph.Target = (int[])p.Target.Clone();
            if (memory.Enabled) memory.Value = p.Mem;
            memShown = p.Mem;
            if (p.Dom != null) for (int k = 0; k < 3; k++) domS[k].Value = p.Dom[k];
            domShown = DomValues();
            if (holdOnCard != null) hold.Set(p.Hold);
            holdShown = p.Hold;
            if (voltOnCard != null) volt.Value = p.Volt;
            voltShown = p.Volt;
            if (p.DomV != null && dvOnCard != null) for (int k = 0; k < 3; k++) dvS[k].Value = p.DomV[k];
            dvShown = DvValues();
            graph.VoltShift = volt.Value;
            loading = false;
            lastKind = null;
            graph.Invalidate(); ShowMemory(); ShowShift(); Edited();
            // back at the first step: the editor shows what the steps started from, an opened preset (16.11: opening one
            // starts the steps anew) or what is on the card
            if (back.Count == 0)
            {
                var from = baseline != null ? GpuStore.Presets.FirstOrDefault(x => x.Name == baseline) : null;
                if (from != null && Holds(from)) { AtPreset(from); FillPresets(); }
                else if (baseline == null)
                {
                    apply.Enabled = false; undo.Enabled = false; bar.SetStatus(s.Expert ? "Как сейчас на видеокарте" : "Только просмотр: запись — экспертная функция");
                    if (picked != null) { picked = null; FillPresets(); } // the list shows the preset on the card again
                }
            }
        }

        // the editor holds exactly this preset
        bool Holds(GpuPreset p)
        {
            if (curve == null || p.Delta.Length != curve.Count) return false;
            for (int k = 0; k < curve.Count; k++) if (graph.Target[k] - graph.Base[k] != p.Delta[k]) return false;
            if (domOnCard != null && !DomValues().SequenceEqual(p.Domain)) return false;
            if (holdOnCard != null && hold.Checked != p.Hold) return false;
            if (voltOnCard != null && volt.Value != p.Volt) return false;
            if (dvOnCard != null && !DvValues().SequenceEqual(p.VoltDom)) return false;
            return !memory.Enabled || memory.Value == p.Memory;
        }

        // the editor holds preset p untouched (just opened, or stepped back to): what the footer says and whether there
        // is anything to apply
        void AtPreset(GpuPreset p)
        {
            picked = p.Name; pickedClean = true;
            if (IsOnCard(p))
            {
                // the preset the card already runs (another one was opened and this one picked again): nothing to apply,
                // and it is the active one, as after «Применить»
                apply.Enabled = false; undo.Enabled = false;
                if (GpuStore.Active != p.Name) { GpuStore.Active = p.Name; GpuStore.Save(); }
                if (s.Expert) bar.SetStatus("Пресет «" + p.Name + "» уже стоит на видеокарте");
            }
            else
            {
                apply.Enabled = s.Expert; undo.Enabled = true;
                if (s.Expert) bar.SetStatus("Пресет «" + p.Name + "» открыт, не применён");
            }
        }

        void StepBack() { if (back.Count == 0 || curve == null) return; forward.Add(Take()); var p = back[back.Count - 1]; back.RemoveAt(back.Count - 1); Put(p); ShowHistory(); }
        void StepForward() { if (forward.Count == 0 || curve == null) return; back.Add(Take()); var p = forward[forward.Count - 1]; forward.RemoveAt(forward.Count - 1); Put(p); ShowHistory(); }

        void ShowHistory()
        {
            if (histRow == null) return;
            histRow.SetSub("Ctrl+Z — " + L.T("шаг назад") + ", Ctrl+Y — " + L.T("шаг вперёд") + (back.Count > 0 ? " · " + L.T("шагов") + ": " + back.Count : ""));
        }

        // Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z from the main window while this page is shown
        public bool HandleKey(Keys keyData)
        {
            if (!Visible || curve == null || !graph.Enabled) return false;
            if (keyData == (Keys.Control | Keys.Z)) { StepBack(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { StepForward(); return true; }
            return false;
        }
        int memFactoryTop; // the card's own top memory clock, without the offset
        readonly Slider shift = new Slider(-1000, 1000, 0, "МГц") { ResetTo = 0, Width = Theme.S(330), WarpToKnob = true };
        readonly Slider memory = new Slider(-1000, 3000, 0, "МГц") { ResetTo = 0, Width = Theme.S(330), WarpToKnob = true };
        readonly FlatButton cut = new FlatButton("Срезать выше"), minus = new FlatButton("−15"), plus = new FlatButton("+15");
        readonly DropButton presetBtn = new DropButton();
        readonly FlatButton saveBtn = new FlatButton("Сохранить как…"), delBtn = new FlatButton("Удалить");
        readonly Toggle onStart = new Toggle();
        // 23.11: «Держать частоты под нагрузкой» (GpuDrs), a part of the preset like the memory clock; holdOnCard = what the
        // driver has (null: its profiles cannot be read, the row is hidden), holdShown = the value before the change in hand
        readonly Toggle hold = new Toggle();
        Row holdRow;
        bool? holdOnCard;
        bool holdShown;
        // 23.11: the core voltage-demand offset (GpuVolt), mV, a part of the preset; voltOnCard = what the driver has (null: no
        // such control, the row is hidden), voltShown = the value before the change in hand
        readonly Slider volt = new Slider(GpuVolt.MinMv, GpuVolt.MaxMv, 0, L.T("мВ")) { ResetTo = 0, Width = Theme.S(330), WarpToKnob = true };
        Row voltRow;
        int? voltOnCard;
        int voltShown;
        // footer: «Сбросить» = the factory curve on the card now; «Отменить» = the editor back to what is on the card
        readonly FlatButton apply = new FlatButton("Применить", true), reset = new FlatButton("Сбросить"), undo = new FlatButton("Отменить всё");
        readonly FooterBar bar;
        Gpu.Curve curve;
        int[] lastMem;
        string afterReload; // a status line to keep once the page has re-read the card
        // 16.11: a preset picked in the list only opens in the editor; it goes to the card with «Применить», like any
        // other change (it used to be written the moment it was picked). picked: the preset chosen in the list (what
        // «Сохранить» and «Удалить» mean); pickedClean: nothing was changed since it was opened, so applying now puts
        // this very preset on the card and it becomes the active one (the one applied at start)
        string picked;
        bool pickedClean;
        string baseline; // the preset the steps of «История правок» start from (null: what the card has)
        int shiftShown;
        bool loading;
        // 20.11: the page is in the menu for everyone where the mode can be switched; the curve and the presets (oc) are
        // an expert function, without it a line says where they are
        readonly System.Collections.Generic.List<Control> oc = new System.Collections.Generic.List<Control>();
        Row expertHint;
        // 22.11: the XBAR / SYS / Video curves (GpuDomains), read only
        readonly DomainGraph domGraph = new DomainGraph();
        Row domHead, domNow;
        // 22.11: the offsets of the XBAR / SYS / Video curves (GpuDomains.Tunable order); domOnCard = what the card has
        // (null: no domain curves on this card)
        readonly Slider[] domS = { new Slider(GpuDomains.MinOffsetMhz, GpuDomains.MaxOffsetMhz, 0, "МГц") { ResetTo = 0, Width = Theme.S(330), Step = 5, WarpToKnob = true }, new Slider(GpuDomains.MinOffsetMhz, GpuDomains.MaxOffsetMhz, 0, "МГц") { ResetTo = 0, Width = Theme.S(330), Step = 5, WarpToKnob = true }, new Slider(GpuDomains.MinOffsetMhz, GpuDomains.MaxOffsetMhz, 0, "МГц") { ResetTo = 0, Width = Theme.S(330), Step = 5, WarpToKnob = true } };
        readonly Row[] domRows = new Row[3];
        int[] domOnCard;
        int[] DomValues() { return domS.Select(x => x.Value).ToArray(); }
        // 23.11: the XBAR / SYS / Video voltage-demand offsets (GpuVolt.Tunable order), mV; dvOnCard = what the driver has (null: not
        // readable, the rows are hidden), dvShown = the values before the change in hand
        readonly Slider[] dvS = { new Slider(GpuVolt.MinMv, GpuVolt.MaxMv, 0, L.T("мВ")) { ResetTo = 0, Width = Theme.S(330), WarpToKnob = true }, new Slider(GpuVolt.MinMv, GpuVolt.MaxMv, 0, L.T("мВ")) { ResetTo = 0, Width = Theme.S(330), WarpToKnob = true }, new Slider(GpuVolt.MinMv, GpuVolt.MaxMv, 0, L.T("мВ")) { ResetTo = 0, Width = Theme.S(330), WarpToKnob = true } };
        readonly Row[] dvRows = new Row[3];
        int[] dvOnCard;
        int[] dvShown = new int[3];
        int[] DvValues() { return dvS.Select(x => x.Value).ToArray(); }
        // what the sliders showed before the change in hand (Changed comes after the value moved): the step Ctrl+Z goes back to
        int[] domShown = new int[3];
        readonly Timer domTimer = new Timer { Interval = 1000 };
        bool domBusy;

        public GpuPage(Settings s)
        {
            this.s = s;
            GpuStore.Load();
            if (GpuModeRow.Available) GpuModeRow.Add(this);
            // 23.11 (the user): the presets right after the graphics mode, above the overclocking; still an expert part (added to oc below)
            var presetBlock = new Control[] { Section("Пресеты"), presetRow = Row("Пресет", "", presetBtn, saveBtn, delBtn),
                Row("Применять при запуске", "Драйвер забывает кривую после перезагрузки: Red Tune вернёт выбранный пресет", onStart) };
            Section("Разгон");
            expertHint = Row("Разгон видеокарты", "Кривая частоты и пресеты — в экспертных функциях: Настройки → Основное");
            int ocFrom = Controls.Count;
            head = Row("Кривая частоты видеокарты", "Читаю…");
            warnRow = Row("MSI Afterburner запущен", "Он тоже применяет свою кривую: программы будут перезаписывать друг друга. Закройте его или уберите из автозагрузки");
            warnRow.WarnStyle = true; warnRow.Visible = false;
            Add(graph);
            pointRow = Row("Точка", "Нажмите на точку на графике. Перетаскивание — одна точка, с Shift — вся кривая", minus, plus, cut);
            // 16.11: a zone of the curve and what can be done with it
            zoneRow = Row("Зона", ZoneHint, zoneOffBtn, lineBtn, smoothBtn);
            // 23.11 (the user): no «История правок» row any more; the steps stay on Ctrl+Z / Ctrl+Y (HandleKey), ShowHistory
            // does nothing without the row
            graph.Changing += (o, e) => Remember(null);
            shiftRow = Row("Сдвиг всей кривой", "Двигает все точки вместе, форма кривой остаётся", shift);
            voltRow = Row("Сдвиг напряжения ядра", "Минус — меньше напряжения на тех же частотах (андервольт), плюс — больше", volt);
            voltRow.Visible = false;
            volt.Changed += (o, e) => { graph.VoltShift = volt.Value; graph.Invalidate(); ShowPoint(); ShowZone(); if (loading || curve == null) { voltShown = volt.Value; return; } Remember("volt"); voltShown = volt.Value; Edited(); };
            memRow = Row("Частота памяти", "Сдвиг частоты видеопамяти", memory);
            holdRow = Row("Держать частоты под нагрузкой", "Драйвер не опускает карту на ступень ниже посреди игры: память держит верхнюю частоту. В простое частоты всё равно падают", hold);
            holdRow.Visible = false;
            hold.CheckedChanged += (o, e) => { if (loading || curve == null) { holdShown = hold.Checked; return; } Remember("hold"); holdShown = hold.Checked; Edited(); };
            Section("Домены частоты");
            domHead = Row("Кривые XBAR, SYS и Video", "Читаю…");
            Add(domGraph);
            domNow = Row("Частоты сейчас", "Читаю…");
            var domTitles = new[] { "Сдвиг XBAR", "Сдвиг SYS", "Сдвиг Video" };
            var domSubs = new[] { "Шина между блоками чипа: вся кривая XBAR вверх или вниз", "Системный домен: вся кривая SYS вверх или вниз", "Видеоблок (кодирование и декодирование видео): вся кривая Video" };
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                domRows[k] = Row(domTitles[k], domSubs[k], domS[k]);
                domS[k].Changed += (o, e) => { if (loading || curve == null) { domShown[kk] = domS[kk].Value; return; } Remember("dom" + kk); domShown[kk] = domS[kk].Value; Edited(); };
            }
            domTimer.Tick += (o, e) => PollClocks();
            VisibleChanged += (o, e) => domTimer.Enabled = Visible && s.Expert;
            Disposed += (o, e) => domTimer.Dispose();
            var dvTitles = new[] { "Напряжение XBAR", "Напряжение SYS", "Напряжение Video" };
            var dvSubs = new[] { "Сдвиг запроса напряжения шины XBAR", "Сдвиг запроса напряжения домена SYS", "Сдвиг запроса напряжения видеоблока" };
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                dvRows[k] = Row(dvTitles[k], dvSubs[k], dvS[k]); dvRows[k].Visible = false;
                dvS[k].Changed += (o, e) => { if (loading || curve == null) { dvShown[kk] = dvS[kk].Value; return; } Remember("dv" + kk); dvShown[kk] = dvS[kk].Value; Edited(); };
            }
            Row("Что это", "Кривые без сдвигов, ядро — заводская. Сдвиги пишутся по «Применить» вместе с кривой ядра и сохраняются в пресет");
            foreach (Control c in Controls) if (Controls.GetChildIndex(c) >= ocFrom) oc.Add(c);
            oc.AddRange(presetBlock);

            graph.Changed += (o, e) => { Edited(); };
            graph.SelectionChanged += (o, e) => ShowPoint();
            graph.ZoneChanged += (o, e) => ShowZone();
            smoothBtn.Click += (o, e) => graph.Smooth();
            lineBtn.Click += (o, e) => graph.Straighten();
            zoneOffBtn.Click += (o, e) => graph.ClearZone();
            minus.Click += (o, e) => Nudge(-15); plus.Click += (o, e) => Nudge(15);
            cut.Click += (o, e) => Cut();
            shift.Changed += (o, e) =>
            {
                if (loading || curve == null) return;
                Remember("shift");
                int d = shift.Value - shiftShown; shiftShown = shift.Value;
                for (int k = 0; k < graph.Target.Length; k++) graph.Target[k] = graph.Clamp(k, graph.Target[k] + d);
                graph.Invalidate(); Edited(); ShowShift();
            };
            memory.Changed += (o, e) => { if (!loading) { Remember("memory"); memShown = memory.Value; } ShowMemory(); if (!loading) Edited(); };
            presetBtn.Changed += (o, e) => LoadPreset(presetBtn.Index);
            saveBtn.Click += (o, e) => SaveAs();
            delBtn.Click += (o, e) => Delete();
            onStart.Set(GpuStore.ApplyOnStart);
            onStart.CheckedChanged += (o, e) => { GpuStore.ApplyOnStart = onStart.Checked; GpuStore.Save(); };
            Footer = bar = new FooterBar(undo, reset, apply);
            apply.Click += (o, e) => Apply();
            reset.Click += (o, e) => Factory();
            undo.Click += (o, e) => { if (curve != null) Show(curve, lastMem); };
            undo.Enabled = false;
            VisibleChanged += (o, e) => { if (Visible) Reload(true); };
            Action onExpert = () => { ApplyExpert(); if (s.Expert && Visible) Reload(); };
            Hw.ExpertChanged += onExpert;
            Disposed += (o, e) => Hw.ExpertChanged -= onExpert;
            ApplyExpert();
        }

        // without the expert functions: the mode only (and the line saying where the rest is); the card is not read
        void ApplyExpert()
        {
            bool ex = s.Expert;
            domTimer.Enabled = ex && Visible;
            foreach (var c in oc) if (c != warnRow && c != holdRow && c != voltRow && Array.IndexOf(dvRows, c) < 0) c.Visible = ex;
            foreach (var r in dvRows) r.Visible = ex && dvOnCard != null;
            holdRow.Visible = ex && holdOnCard != null;
            voltRow.Visible = ex && voltOnCard != null;
            if (!ex) warnRow.Visible = false;
            ShowDomRows();
            expertHint.Visible = !ex;
            Footer = ex ? bar : null;
            Relayout();
        }

        void Reload(bool keepEdits = false)
        {
            if (!s.Expert) return; // 20.11: the curve is not shown: nothing to read
            warnRow.Visible = Process.GetProcessesByName("MSIAfterburner").Length > 0;
            Task.Run(() =>
            {
                var dc = GpuDomains.Read();
                try { BeginInvoke((Action)(() => ShowDomains(dc))); } catch { }
            });
            if (keepEdits && undo.Enabled) return; // something is changed and not applied: keep it on screen
            Task.Run(() =>
            {
                var c = Gpu.Read(); var mem = Gpu.ReadMemory(); var hd = GpuDrs.TryIsOn(); var vd = GpuVolt.Read(); var dvd = GpuVolt.ReadDomains();
                try { BeginInvoke((Action)(() => { holdOnCard = hd; voltOnCard = vd; dvOnCard = dvd; Show(c, mem); })); } catch { }
            });
        }

        void Show(Gpu.Curve c, int[] mem)
        {
            loading = true;
            curve = c; lastMem = mem; undo.Enabled = false;
            picked = null; pickedClean = false; baseline = null; // what the card has is on screen again
            if (c == null)
            {
                head.SetSub("Нет доступа к кривой: " + (Gpu.Error ?? "драйвер не ответил"));
                graph.Enabled = false; apply.Enabled = reset.Enabled = false;
                loading = false; Relayout(); return;
            }
            GpuStore.KeepStock(c);
            bool[] known;
            // 23.11: the driver reports each point at its voltage with the voltage-demand offset already in it (−50 mV read
            // 400–1190 instead of 450–1240): the factory voltages are those minus the offset on the card, and the graph adds the
            // offset in the editor (VoltShift) when it draws the curve being made
            int vOff = voltOnCard ?? 0;
            graph.Mv = vOff == 0 ? c.Mv : c.Mv.Select(x => x - vOff).ToArray(); graph.Base = GpuStore.Base(c, out known); graph.Known = known; graph.Target = (int[])c.Mhz.Clone();
            // a point pinned under a flat top keeps its own target (factory + its offset), so the flat part stays as set
            for (int k = 0; k < c.Count; k++) graph.Target[k] = graph.Base[k] + c.Delta[k];
            graph.Selected = -1; graph.ZoneA = graph.ZoneB = -1;
            back.Clear(); forward.Clear(); lastKind = null;
            shift.Value = 0; shiftShown = 0;
            if (mem != null) { memory.SetRange(Math.Max(mem[1], -1000), Math.Min(mem[2], 3000)); memory.Value = mem[0]; }
            else memory.Enabled = false;
            // the offset counts double, like Afterburner's: +1000 moved the top 12 001 → 12 501 MHz (13.11, RTX 5070 Laptop)
            memFactoryTop = c.MemTop > 0 && mem != null ? c.MemTop - mem[0] / 2 : 0;
            memShown = memory.Enabled ? memory.Value : 0;
            // 22.11: «Отменить всё» (and every re-read) puts the domain sliders back to what the card has too
            if (domOnCard != null) for (int k = 0; k < 3; k++) domS[k].Value = domOnCard[k];
            domShown = DomValues();
            holdRow.Visible = s.Expert && holdOnCard != null;
            if (holdOnCard != null) hold.Set(holdOnCard.Value);
            holdShown = hold.Checked;
            foreach (var r in dvRows) r.Visible = s.Expert && dvOnCard != null;
            if (dvOnCard != null) for (int k = 0; k < 3; k++) dvS[k].Value = dvOnCard[k];
            dvShown = DvValues();
            voltRow.Visible = s.Expert && voltOnCard != null;
            if (voltOnCard != null) volt.Value = voltOnCard.Value;
            voltShown = volt.Value;
            graph.VoltShift = volt.Value; // the curve drawn at the voltages the card will use
            ShowMemory(); ShowShift();
            head.SetSub(Hw.Gpus.FirstOrDefault(n => n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) + " · " + c.Count + " " + L.T("точек") + ", "
                + graph.Mv[0] + "–" + graph.Mv[c.Count - 1] + " " + L.T("мВ") + " · " + L.T("сейчас") + ": " + Gpu.Describe(c.Delta)
                + (mem != null && mem[0] != 0 ? ", " + L.T("память") + " " + (mem[0] > 0 ? "+" : "") + mem[0] + " " + L.T("МГц") : "")
                + (voltOnCard != null && voltOnCard != 0 ? ", " + L.T("напряжение") + " " + (voltOnCard > 0 ? "+" : "") + voltOnCard + " " + L.T("мВ") : "")
                + (holdOnCard == true ? ", " + L.T("держит частоты") : "")
                + (OnCard(c, mem) != null ? " · " + L.T("пресет") + " «" + OnCard(c, mem).Name + "»" : ""));
            graph.Enabled = s.Expert; apply.Enabled = false; reset.Enabled = s.Expert;
            var now = OnCard(c, mem);
            if (now != null && now.Name != GpuStore.Active) { GpuStore.Active = now.Name; GpuStore.Save(); }
            FillPresets();
            ShowPoint(); ShowZone(); ShowHistory();
            bar.SetStatus(afterReload ?? (s.Expert ? "Как сейчас на видеокарте" : "Только просмотр: запись — экспертная функция"));
            afterReload = null;
            loading = false;
            Relayout();
        }

        // 22.11: the clocks the domains run at now (GpuDomains.Measured), once a second while the page is shown
        void PollClocks()
        {
            if (domBusy || !s.Expert) return;
            domBusy = true;
            Task.Run(() =>
            {
                var keys = new[] { 0, 1, 2, 21, 4 };
                var v = keys.Select(k => GpuDomains.Measured(k)).ToArray();
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        domBusy = false;
                        var parts = new System.Collections.Generic.List<string>();
                        for (int k = 0; k < keys.Length; k++)
                            if (v[k] > 0) parts.Add((keys[k] == 0 ? L.T("Ядро") : keys[k] == 4 ? L.T("Память") : GpuDomains.NameOf(keys[k])) + " " + (v[k] / 1000) + " " + L.T("МГц"));
                        domNow.SetSub(parts.Count > 0 ? string.Join(" · ", parts) : L.T("драйвер не ответил"));
                    }));
                }
                catch { domBusy = false; }
            });
        }

        void ShowDomains(System.Collections.Generic.List<GpuDomains.Curve> dc)
        {
            domGraph.Set(dc);
            domGraph.Visible = dc != null && s.Expert;
            if (dc == null) { domOnCard = null; ShowDomRows(); domHead.SetSub(L.T("Нет доступа") + ": " + (GpuDomains.Error ?? L.T("драйвер не ответил"))); Relayout(); return; }
            var top = dc.Where(c => c.Key != 0).Select(c => c.Name + " " + L.T("до") + " " + c.Mhz.Max() + " " + L.T("МГц")).ToArray();
            domHead.SetSub(string.Join(" · ", top));
            domOnCard = dc == null ? null : GpuDomains.Offsets(dc);
            ShowDomRows();
            Relayout();
        }

        // the sliders show the card's offsets unless the editor holds changes not applied; hidden without domain curves
        void ShowDomRows()
        {
            bool has = domOnCard != null && s.Expert;
            for (int k = 0; k < 3; k++) domRows[k].Visible = has;
            if (domOnCard == null) return;
            if (!undo.Enabled)
            {
                loading = true;
                for (int k = 0; k < 3; k++) domS[k].Value = domOnCard[k];
                domShown = DomValues();
                loading = false;
            }
            if (curve != null && !loading && !SameAsCard()) Edited(); // when it matches, the status line (e.g. «Применено») stays
        }

        // the saved preset that is on the card now (offsets within 15 MHz, the driver's step), or null
        GpuPreset OnCard(Gpu.Curve c, int[] mem)
        {
            if (c == null) return null;
            return GpuStore.Presets.FirstOrDefault(p => p.Delta.Length == c.Count
                && Enumerable.Range(0, c.Count).All(k => Math.Abs(p.Delta[k] - c.Delta[k]) <= 15)
                && (mem == null || p.Memory == mem[0]) && (holdOnCard == null || p.Hold == holdOnCard.Value) && (voltOnCard == null || p.Volt == voltOnCard.Value) && (dvOnCard == null || p.VoltDom.SequenceEqual(dvOnCard)) && p.Delta.Any(x => x != 0));
        }

        // what the slider means: the top memory clock in games, as the driver and Home show it, and the data rate
        void ShowMemory()
        {
            if (memRow == null) return;
            if (memFactoryTop <= 0) { memRow.SetSub(L.T("Сдвиг частоты видеопамяти")); return; }
            int top = memFactoryTop + memory.Value / 2;
            memRow.SetSub(L.T("В играх до") + " " + top + " " + L.T("МГц") + " (≈" + (top * 2 / 1000.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo(L.En ? "en-US" : "ru-RU")) + " " + L.T("Гбит/с") + ")"
                + (memory.Value != 0 ? " · " + L.T("заводская") + " " + memFactoryTop : ""));
        }

        // 22.11 (the user: the slider said 0 while the card ran +408): the slider stands at the offset of the curve from the
        // factory one (its first point), and moving it moves every point by the difference, as before
        void ShowShift()
        {
            if (curve == null || graph.Target.Length == 0 || graph.Base.Length != graph.Target.Length) return;
            bool was = loading; loading = true;
            shift.Value = Math.Max(-1000, Math.Min(1000, graph.Target[0] - graph.Base[0])); shiftShown = shift.Value;
            loading = was;
        }

        void ShowPoint()
        {
            int k = graph.Selected;
            minus.Enabled = plus.Enabled = cut.Enabled = k >= 0 && graph.Enabled;
            if (k < 0) { pointRow.SetSub(L.T("Нажмите на точку на графике. Перетаскивание — одна точка, с Shift — вся кривая")); return; }
            var shown = graph.Shown();
            int d = graph.Target[k] - graph.Base[k];
            pointRow.SetSub(graph.VoltAt(k) + " " + L.T("мВ") + ": " + shown[k] + " " + L.T("МГц") + (graph.Known[k] ? " (" + L.T("заводская") + " " + graph.Base[k] + ", " + (d >= 0 ? "+" : "") + d + ")" : " (" + L.T("под срезом") + ")"));
        }

        void ShowZone()
        {
            if (zoneRow == null) return;
            bool has = graph.HasZone && graph.Enabled;
            smoothBtn.Enabled = lineBtn.Enabled = has; zoneOffBtn.Enabled = graph.ZoneA >= 0;
            if (!graph.HasZone) { zoneRow.SetSub(ZoneHint); return; }
            var shown = graph.Shown();
            zoneRow.SetSub(graph.VoltAt(graph.ZoneA) + "–" + graph.VoltAt(graph.ZoneB) + " " + L.T("мВ") + " · " + (graph.ZoneB - graph.ZoneA + 1) + " " + L.T("точек") + " · "
                + shown[graph.ZoneA] + "–" + shown[graph.ZoneB] + " " + L.T("МГц"));
        }

        void Nudge(int d)
        {
            int k = graph.Selected; if (k < 0) return;
            Remember(null);
            graph.Target[k] = graph.Clamp(k, graph.Target[k] + d);
            graph.Invalidate(); Edited();
        }

        // the usual undervolt: above the chosen point the frequency stays at its level, so the card never asks for more voltage
        void Cut()
        {
            int k = graph.Selected; if (k < 0) return;
            Remember(null);
            int top = graph.Shown()[k];
            for (int i = k + 1; i < graph.Target.Length; i++) graph.Target[i] = Math.Min(graph.Target[i], Math.Max(graph.Base[i] - 2000, top - 15));
            graph.Invalidate(); Edited();
        }

        void Edited()
        {
            ShowShift(); // the points moved some other way (drag, ±15, a zone): the slider follows the curve
            ShowPoint(); ShowZone(); ShowHistory();
            pickedClean = false; // LoadPreset sets it again after this
            // 16.11: back at exactly what the card has (a point moved and moved back, the card's own preset opened again):
            // there is nothing to apply
            bool same = SameAsCard();
            apply.Enabled = s.Expert && curve != null && !same;
            undo.Enabled = curve != null && !same;
            bar.SetStatus(same && s.Expert ? "Как сейчас на видеокарте" : "Изменено, не применено");
        }

        // the editor holds exactly the curve and the memory clock the card reported
        bool SameAsCard()
        {
            if (curve == null || graph.Target == null || graph.Target.Length != curve.Count) return false;
            for (int k = 0; k < curve.Count; k++) if (graph.Target[k] - graph.Base[k] != curve.Delta[k]) return false;
            if (domOnCard != null && !DomValues().SequenceEqual(domOnCard)) return false;
            if (holdOnCard != null && hold.Checked != holdOnCard.Value) return false;
            if (voltOnCard != null && volt.Value != voltOnCard.Value) return false;
            if (dvOnCard != null && !DvValues().SequenceEqual(dvOnCard)) return false;
            return !memory.Enabled || lastMem == null || memory.Value == lastMem[0];
        }

        // this preset is what the card runs now: its offsets within 15 MHz (the driver's step, as OnCard) and its memory clock
        bool IsOnCard(GpuPreset p)
        {
            if (curve == null || p.Delta.Length != curve.Count) return false;
            for (int k = 0; k < curve.Count; k++) if (Math.Abs(p.Delta[k] - curve.Delta[k]) > 15) return false;
            if (domOnCard != null && !p.Domain.SequenceEqual(domOnCard)) return false;
            if (holdOnCard != null && p.Hold != holdOnCard.Value) return false;
            if (voltOnCard != null && p.Volt != voltOnCard.Value) return false;
            if (dvOnCard != null && !p.VoltDom.SequenceEqual(dvOnCard)) return false;
            return lastMem == null || p.Memory == lastMem[0];
        }

        int[] Deltas()
        {
            var d = new int[graph.Target.Length];
            for (int k = 0; k < d.Length; k++) d[k] = Math.Max(-2000, Math.Min(1000, graph.Target[k] - graph.Base[k]));
            return d;
        }

        bool Confirmed()
        {
            if (s.GpuAsked) return true;
            if (!ConfirmDialog.Ask(FindForm(), "Разгон видеокарты", "Кривая и частота памяти пишутся в драйвер видеокарты, как в MSI Afterburner. Слишком высокие частоты дают артефакты, вылеты игр и сброс драйвера (экран мигнёт).\n\nПосле перезагрузки драйвер вернётся к заводской кривой.", "Применить", "Отмена")) return false;
            s.GpuAsked = true; Store.SaveSettings(s);
            return true;
        }

        void Apply() { Apply(null); }

        // done = the status line once it is on the card (else the header says what is there)
        void Apply(string done)
        {
            if (!s.Expert || curve == null || !Confirmed()) return;
            var d = Deltas(); int mem = memory.Enabled ? memory.Value : 0;
            var dom = domOnCard != null ? DomValues() : null;
            bool? holdWant = holdOnCard != null ? hold.Checked : (bool?)null;
            int? voltWant = voltOnCard != null ? volt.Value : (int?)null;
            int[] dvWant = dvOnCard != null ? DvValues() : null;
            // an opened preset, unchanged: this is the preset going to the card
            string name = pickedClean ? picked : null;
            apply.Enabled = false; bar.SetStatus("Применяю…");
            Task.Run(() =>
            {
                var e = Gpu.WriteAll(d, memory.Enabled ? mem : (int?)null);
                if (e == null && dom != null) e = GpuDomains.Apply(dom);
                if (e == null && holdWant != null) e = GpuDrs.Ensure(holdWant.Value);
                if (e == null && dvWant != null) e = GpuVolt.EnsureDomains(dvWant);
                if (e == null && voltWant != null) e = GpuVolt.Ensure(voltWant.Value); // last
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (e != null) { bar.SetStatus("Не удалось: " + e); apply.Enabled = true; return; }
                        if (name != null) { GpuStore.Active = name; GpuStore.Save(); }
                        afterReload = done ?? (name != null ? "Пресет «" + name + "» применён" : "Применено"); undo.Enabled = false; Reload();
                    }));
                }
                catch { }
            });
        }

        // back to the factory curve and memory clock on the card at once, like Afterburner's reset; presets stay
        void Factory()
        {
            if (!s.Expert || curve == null) return;
            if (!ConfirmDialog.Ask(FindForm(), "Сбросить видеокарту", "Вернуть заводскую кривую и частоту памяти? Сдвиги снимаются с видеокарты сразу, сохранённые пресеты остаются." +
                (GpuStore.ApplyOnStart ? "\n\nВключено «Применять при запуске»: после перезагрузки снова применится пресет «" + GpuStore.Active + "»." : ""), "Сбросить", "Отмена")) return;
            reset.Enabled = apply.Enabled = undo.Enabled = false; bar.SetStatus("Возвращаю заводскую…");
            var zero = new int[curve.Count];
            Task.Run(() =>
            {
                var e = Gpu.WriteAll(zero, memory.Enabled ? 0 : (int?)null);
                if (e == null && domOnCard != null) e = GpuDomains.Apply(new int[3]);
                if (e == null && holdOnCard == true) e = GpuDrs.Set(false); // 23.11: the driver's own power mode again
                if (e == null && dvOnCard != null && dvOnCard.Any(x => x != 0)) e = GpuVolt.EnsureDomains(new int[3]);
                if (e == null && voltOnCard != null && voltOnCard != 0) e = GpuVolt.Set(0); // 23.11: no voltage offset
                try { BeginInvoke((Action)(() => { afterReload = e != null ? "Не удалось: " + e : "Заводская кривая и частота памяти"; Reload(); })); } catch { }
            });
        }

        void FillPresets()
        {
            var names = GpuStore.Presets.Select(p => p.Name).ToArray();
            presetBtn.Set(names.Length > 0 ? names : new[] { "—" }, names.Length > 0 ? Array.IndexOf(names, picked ?? GpuStore.Active) : 0);
            presetRow.SetSub(names.Length == 0 ? L.T("Пресетов пока нет: настройте кривую и сохраните") : L.T("Только открывается · запись — «Применить»"));
            delBtn.Enabled = names.Length > 0;
        }

        void LoadPreset(int i)
        {
            if (i < 0 || i >= GpuStore.Presets.Count || curve == null) return;
            var p = GpuStore.Presets[i];
            if (p.Delta.Length != curve.Count) { Msg.Show(FindForm(), "Этот пресет снят с другой кривой (другая видеокарта или драйвер)", "Red Tune"); return; }
            // 16.11: opened, not applied: nothing goes to the card and the active preset (the one applied at start) stays
            // until «Применить». Picking a preset is not a step of «История правок»: the steps start anew from it, so
            // «Шаг назад» walks back through the changes made to this preset and stops at it
            back.Clear(); forward.Clear(); lastKind = null; baseline = p.Name;
            loading = true;
            for (int k = 0; k < curve.Count; k++) graph.Target[k] = graph.Base[k] + p.Delta[k];
            if (memory.Enabled) memory.Value = p.Memory;
            if (domOnCard != null) for (int k = 0; k < 3; k++) domS[k].Value = p.Domain[k];
            domShown = DomValues();
            if (holdOnCard != null) hold.Set(p.Hold);
            holdShown = hold.Checked;
            if (voltOnCard != null) volt.Value = p.Volt;
            voltShown = volt.Value;
            graph.VoltShift = volt.Value; // the curve drawn at the voltages the card will use
            if (dvOnCard != null) for (int k = 0; k < 3; k++) dvS[k].Value = p.VoltDom[k];
            dvShown = DvValues();
            shift.Value = 0; shiftShown = 0; memShown = memory.Enabled ? memory.Value : 0;
            loading = false;
            graph.Invalidate(); Edited(); ShowShift();
            AtPreset(p);
        }

        void SaveAs()
        {
            if (curve == null) return;
            // a preset of the factory curve is almost always a mistake (13.11: one came out empty): say what is about to be saved
            if (Deltas().All(x => x == 0) && (!memory.Enabled || memory.Value == 0) && DomValues().All(x => x == 0) && !(holdOnCard != null && hold.Checked) && !(voltOnCard != null && volt.Value != 0) && !(dvOnCard != null && DvValues().Any(x => x != 0))
                && !ConfirmDialog.Ask(FindForm(), "Пресет видеокарты", "В редакторе сейчас заводская кривая без сдвигов. Сохранить её как пресет?", "Сохранить", "Отмена")) return;
            var name = InputDialog.Ask(FindForm(), "Пресет видеокарты", "Название", picked ?? (GpuStore.Active.Length > 0 ? GpuStore.Active : "Мой андервольт"));
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            var p = GpuStore.Presets.FirstOrDefault(x => x.Name == name);
            if (p == null) GpuStore.Presets.Add(p = new GpuPreset { Name = name });
            p.Delta = Deltas(); p.Memory = memory.Enabled ? memory.Value : 0;
            p.Domain = domOnCard != null ? DomValues() : new int[3];
            p.Hold = holdOnCard != null && hold.Checked;
            p.Volt = voltOnCard != null ? volt.Value : 0;
            p.VoltDom = dvOnCard != null ? DvValues() : new int[3];
            GpuStore.Active = name; GpuStore.Save();
            picked = name; pickedClean = true; // the editor holds exactly this preset now
            FillPresets();
            bar.SetStatus("Пресет «" + name + "» сохранён");
        }

        void Delete()
        {
            // the one shown in the list: an opened preset, else the active one
            var p = GpuStore.Presets.FirstOrDefault(x => x.Name == (picked ?? GpuStore.Active));
            if (p == null) return;
            if (Msg.Show(FindForm(), "Удалить пресет «" + p.Name + "»?", "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            GpuStore.Presets.Remove(p);
            if (GpuStore.Active == p.Name) GpuStore.Active = GpuStore.Presets.Count > 0 ? GpuStore.Presets[0].Name : "";
            GpuStore.Save();
            picked = null; pickedClean = false;
            FillPresets();
        }
    }
}
