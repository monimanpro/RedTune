using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // horizontal slider with the value on the right ("50 W")
    class Slider : Painted
    {
        int min, max, value;
        readonly string unit;
        bool drag;
        public event EventHandler Changed;
        // the user takes hold of the slider (a press on the track, a typed value, a wheel step), just before the value
        // changes: for sliders that move their neighbours and want to know where those stood (14.11)
        public event EventHandler Grabbed;
        void Grab() { var h = Grabbed; if (h != null) h(this, EventArgs.Empty); }

        // Unset: the slider shows «не менять» and a hollow grey knob; any move by the user clears it (13.11: the AMD
        // limits a processor did not report must not be written just because a slider happened to stand there)
        public bool Unset { get { return unset; } set { if (unset == value) return; unset = value; Invalidate(); } }
        bool unset;

        // 18.11: Step — the track and the wheel move the value in steps of this size (a typed value stays as typed);
        // Caption — a text in place of «value unit» (the top of the clock cap on the power page reads «нет»)
        public int Step = 1;
        public Func<int, string> Caption;
        // 20.11 (the user's wish, as the equalizer's faders): a press on the track leaves the knob where it is and puts the
        // pointer onto it; the drag then moves the value by as much as the mouse moves from there (not to the pixel under
        // it: on a long range such as the clock cap a pixel is tens of MHz, and the knob would jump at the first move)
        public bool WarpToKnob;
        int pressX, pressValue; bool relative;

        public Slider(int min, int max, int value, string unit)
        {
            this.min = min; this.max = max; this.unit = unit; this.value = Math.Max(min, Math.Min(max, value));
            Size = new Size(Theme.S(300), Theme.S(28)); BackColor = Theme.Card; Cursor = Cursors.Hand;
        }

        public int Value
        {
            get { return value; }
            set { value = Math.Max(min, Math.Min(max, value)); if (this.value == value) return; this.value = value; Invalidate(); }
        }

        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        public void SetRange(int min, int max) { this.min = min; this.max = Math.Max(min + 1, max); value = Math.Max(this.min, Math.Min(this.max, value)); Invalidate(); }

        int TrackW { get { return Width - Theme.S(70); } }
        static int Pad { get { return Theme.S(13); } } // room for half the glass knob and its shadow at both ends

        // --slider-warp-check: a press far from the knob and a drag, driven by the mouse messages Windows sends (the real
        // pointer is not moved: WarpPointer off, so the drag goes from the press)
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        internal static string WarpCheck()
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, t) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + t);
            Theme.WarpPointer = false;
            using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(600, 200) })
            {
                var s = new Slider(1000, 20000, 15000, "МГц") { Width = 470, WarpToKnob = true };
                int changes = 0; s.Changed += (o, e) => changes++;
                f.Controls.Add(s); f.Show(); Application.DoEvents();
                Func<int, int, IntPtr> at = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
                int y0 = s.Height / 2, k = s.KnobX, far = Pad + 5;
                SendMessage(s.Handle, 0x201, (IntPtr)1, at(far, y0));
                line(s.Value == 15000 && changes == 0, "a press at the left end, far from the knob: the value stays " + s.Value);
                SendMessage(s.Handle, 0x200, (IntPtr)1, at(far + 40, y0));
                int moved = s.Value; double px = (s.TrackW - 2 * Pad) / 19000.0;
                line(Math.Abs(moved - 15000 - 40 / px) <= 60, "40 px to the right from there: " + moved + " (15000 + " + (40 / px).ToString("0") + " expected), not the left end");
                SendMessage(s.Handle, 0x200, (IntPtr)1, at(far + 4000, y0));
                line(s.Value == 20000, "far to the right: the top of the range (" + s.Value + ")");
                SendMessage(s.Handle, 0x202, IntPtr.Zero, at(far + 4000, y0));
                s.WarpToKnob = false; s.Value = 15000;
                SendMessage(s.Handle, 0x201, (IntPtr)1, at(far, y0)); SendMessage(s.Handle, 0x202, IntPtr.Zero, at(far, y0));
                line(s.Value < 1300, "without it (other pages) the knob still jumps to the press: " + s.Value);
            }
            CoreGrid.WarpCheck(line);
            Theme.WarpPointer = true;
            return sb.ToString();
        }

        int KnobX { get { float f = max > min ? (float)(value - min) / (max - min) : 0; return (int)Math.Round(Pad + (TrackW - 2 * Pad) * f); } }

        void SetFrom(int x) { SetTo(min + (int)Math.Round((double)(x - Pad) / Math.Max(1, TrackW - 2 * Pad) * (max - min))); }

        // relative: the value at the press plus as much as the mouse went since
        void DragTo(int x) { SetTo(pressValue + (int)Math.Round((double)(x - pressX) / Math.Max(1, TrackW - 2 * Pad) * (max - min))); }

        void SetTo(int v)
        {
            if (Step > 1) v = min + (int)Math.Round((v - min) / (double)Step) * Step;
            v = Math.Max(min, Math.Min(max, v));
            // 20.11: the knob paints itself before the page reacts — a heavy handler (the sound page's pictures) left it waiting
            // for an empty queue, so it lagged behind the mouse
            if (v != value || unset) { value = v; unset = false; Invalidate(); Update(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
        }

        // the wheel steps the value only after a click on this slider and while the mouse stays on it;
        // otherwise it goes on to the page, which scrolls (2026-11-11: scrolling the page changed the limits under the cursor)
        bool armed;

        // 13.11: the number on the right can be typed: a click on it turns it into a field, Enter (or a click elsewhere)
        // sets the value within the slider's range, Esc leaves it as it was
        TextBox box;
        bool overValue;
        Rectangle ValueArea { get { return new Rectangle(TrackW, 0, Width - TrackW, Height); } }

        void BeginEdit()
        {
            if (box != null || !Enabled) return;
            int unitW = TextRenderer.MeasureText(" " + L.T(unit), Theme.Bold).Width;
            box = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Theme.Bold,
                TextAlign = HorizontalAlignment.Right, Text = unset ? "" : value.ToString(),
            };
            int w = Math.Max(Theme.S(40), ValueArea.Width - unitW - Theme.S(4));
            box.SetBounds(Width - unitW - w, (Height - box.PreferredHeight) / 2, w, box.PreferredHeight);
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
            };
            box.LostFocus += (s, e) => EndEdit(true);
            Controls.Add(box);
            box.Focus(); box.SelectAll();
            Invalidate();
        }

        void EndEdit(bool keep)
        {
            if (box == null) return;
            var t = box.Text.Trim().Replace(" ", "").Replace("+", "").Replace("−", "-");
            var b = box; box = null;
            Controls.Remove(b); b.Dispose();
            int v;
            if (keep && int.TryParse(t, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                v = Math.Max(min, Math.Min(max, v));
                if (v != value || unset) { Grab(); value = v; unset = false; var h = Changed; if (h != null) h(this, EventArgs.Empty); }
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            armed = true;
            if (e.X < TrackW)
            {
                drag = true; Grab();
                relative = WarpToKnob && !unset && e.Button == MouseButtons.Left;
                if (relative) { int kx = KnobX; pressValue = value; pressX = Theme.Warp(this, kx, Height / 2f) ? kx : e.X; }
                else SetFrom(e.X);
            }
            else if (e.Button == MouseButtons.Left) BeginEdit();
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) { if (relative) DragTo(e.X); else SetFrom(e.X); }
            bool over = Enabled && ValueArea.Contains(e.Location);
            if (over != overValue) { overValue = over; Invalidate(); }
            // 19.11: the pointer is set from where the mouse is on every move, not only when it crosses into the number
            // or out of it. A slider the mouse had left through its number kept the text pointer, and showed it over its
            // knob the next time (the «hand» came back only after another pass over the number).
            var want = over ? Cursors.IBeam : Enabled ? Cursors.Hand : Cursors.Default;
            if (Cursor != want) Cursor = want;
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e)
        {
            armed = false;
            if (overValue) { overValue = false; Invalidate(); }
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            base.OnMouseLeave(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!armed) { base.OnMouseWheel(e); return; }
            int n = Theme.WheelNotches(ref wheelAcc, e.Delta); // 20.11: one notch = one step, also from a smooth wheel
            int v = Math.Max(min, Math.Min(max, value + n * Step));
            if (n != 0 && (v != value || unset)) { Grab(); value = v; unset = false; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
            var he = e as HandledMouseEventArgs;
            if (he != null) he.Handled = true;
        }
        int wheelAcc;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x0 = Pad, x1 = TrackW - Pad, cy = Height / 2f, th = Theme.S(5);
            float f = max > min ? (float)(value - min) / (max - min) : 0;
            // 14.11: a glass tube with the bar inside it and a knob of thick glass (Theme.GlassKnob)
            Theme.Tube(g, new RectangleF(x0, cy - th / 2f, x1 - x0, th));
            bool live = !unset && Enabled;
            var fill = live ? Theme.Heat(f) : Theme.Muted; // grey: not going to be written, or locked
            var bar = new RectangleF(x0, cy - th / 2f, (x1 - x0) * f, th);
            if (live) Theme.FillGradient(g, bar, th / 2f, false, x1 - x0);
            else if (!unset) Theme.Fill(g, bar, th / 2f, fill);
            Theme.GlassKnob(g, x0 + (x1 - x0) * f, cy, Theme.KnobSize, !unset && f > 0.001f ? 1f : 0f, fill, th, !live);
            // the value can be typed: a faint field behind it under the mouse, the field itself while typing
            if (overValue || box != null)
                Theme.Fill(g, new RectangleF(TrackW + Theme.S(6), Theme.S(2), Width - TrackW - Theme.S(6), Height - Theme.S(4)), Theme.S(5), Theme.Raised);
            if (box != null) Theme.Text1(g, unit, Theme.Bold, new Rectangle(TrackW, 0, Width - TrackW, Height), Theme.Text2, TextFormatFlags.Right);
            else if (unset) Theme.Text1(g, "не менять", Theme.Body, new Rectangle(TrackW - Theme.S(20), 0, Width - TrackW + Theme.S(20), Height), Theme.Muted, TextFormatFlags.Right);
            else Theme.Text1(g, (Caption != null ? Caption(value) : null) ?? value + " " + unit, Theme.Bold, new Rectangle(TrackW, 0, Width - TrackW, Height), Enabled ? Theme.Text : Theme.Text2, TextFormatFlags.Right);
        }
    }

    // a button that opens a list of choices ("100 W ▾")
    class DropButton : FlatButton
    {
        public string[] Items = new string[0];
        public int Index = -1;
        public event EventHandler Changed;

        public DropButton() : base("—") { Click += (s, e) => Open(); }

        public void Set(string[] items, int index)
        {
            Items = items; Index = index;
            Text = (index >= 0 && index < items.Length ? items[index] : "—") + "  ▾";
        }

        // a choice as the user makes it in the menu (the checks pick this way too)
        public void Pick(int index) { Set(Items, index); var h = Changed; if (h != null) h(this, EventArgs.Empty); }

        // as wide as the longest choice, so the button does not jump when another one is picked
        protected override int TextWidth()
        {
            int w = base.TextWidth();
            foreach (var it in Items) w = Math.Max(w, TextRenderer.MeasureText(L.T(it) + "  ▾", Font).Width);
            return w;
        }

        void Open()
        {
            if (Items.Length == 0) return;
            var m = Page.Menu();
            for (int i = 0; i < Items.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Items[i]) { Checked = i == Index };
                it.Click += (s, e) => Pick(idx);
                m.Items.Add(it);
            }
            Page.ShowAt(m, this);
        }
    }
}
