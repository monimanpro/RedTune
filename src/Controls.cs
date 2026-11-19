using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // controls whose size depends on their text: re-measured when the language changes
    interface ILang { void Relang(); }

    // 16.11: a window with a picture behind its controls (the mini panel: each block is glass over the blurred screen).
    // A control inside it starts from its own piece of that picture instead of a flat colour, so the picture runs
    // unbroken under the rows, their buttons and switches. Backdrop: in the window's client coordinates; null: none now.
    // BackdropDc: the same picture selected into a GDI memory DC (zero: none), for a fast copy.
    // Solid: not null while the window is taking a picture of what its controls draw by themselves (17.11, the mini
    // panel's live glass): the controls then start from this plain colour instead of the picture.
    // Whole (17.11, the mini panel as blocks with Windows' blur): the picture already has everything in it that the
    // controls draw, with its transparency; a control then answers a paint message by copying its piece of the
    // picture and draws nothing itself (Backdrops.Whole). Fresh: the picture brought up to date before that.
    interface IBackdrop { Bitmap Backdrop { get; } IntPtr BackdropDc { get; } Color? Solid { get; } bool Whole { get; } void Fresh(); }

    static class Backdrops
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc; public int fErase, left, top, right, bottom, fRestore, fIncUpdate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);

        // A paint message of a control (or of the window itself) whose window keeps the whole picture: the control's
        // piece of that picture is copied to the screen as it is, transparency and all, and the control's own
        // drawing does not run. true: done. Not while the window is taking the picture of its controls (Solid).
        public static bool Whole(Control c, ref Message m)
        {
            if (m.Msg != 0x000F || m.WParam != IntPtr.Zero) return false; // WM_PAINT, not a print into somebody's DC
            var host = c as IBackdrop ?? Of(c);
            if (host == null || !host.Whole || host.Solid.HasValue) return false;
            try { host.Fresh(); } catch { }
            IntPtr src = host.BackdropDc;
            if (src == IntPtr.Zero) return false;
            var at = ReferenceEquals(c, host) ? Point.Empty : ((Control)host).PointToClient(c.PointToScreen(Point.Empty));
            PAINTSTRUCT ps;
            IntPtr dc = BeginPaint(c.Handle, out ps);
            try { BitBlt(dc, 0, 0, c.Width, c.Height, src, at.X, at.Y, 0x00CC0020); }
            finally { EndPaint(c.Handle, ref ps); }
            m.Result = IntPtr.Zero;
            return true;
        }

        public static IBackdrop Of(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent) { var b = p as IBackdrop; if (b != null) return b.Backdrop != null ? b : null; }
            return null;
        }

        // the piece of the picture c stands on, over all of c; false: no picture, the caller paints its own colour
        public static bool Paint(Graphics g, Control c)
        {
            var host = Of(c);
            if (host == null || c.Width <= 0 || c.Height <= 0) return false;
            try
            {
                var at = ((Control)host).PointToClient(c.PointToScreen(Point.Empty));
                var solid = host.Solid;
                if (solid.HasValue) { g.Clear(solid.Value); return true; }
                IntPtr src = host.BackdropDc;
                if (src != IntPtr.Zero)
                {
                    IntPtr dc = g.GetHdc();
                    try { if (BitBlt(dc, 0, 0, c.Width, c.Height, src, at.X, at.Y, 0x00CC0020)) return true; } // SRCCOPY
                    finally { g.ReleaseHdc(dc); }
                }
                // a plain copy of pixels (no blending, no filtering): with live glass this runs for every control on
                // every changed frame
                var cm = g.CompositingMode; var im = g.InterpolationMode;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(host.Backdrop, new Rectangle(0, 0, c.Width, c.Height), new Rectangle(at.X, at.Y, c.Width, c.Height), GraphicsUnit.Pixel);
                g.CompositingMode = cm; g.InterpolationMode = im;
                return true;
            }
            catch { return false; }
        }
    }

    // 19.11: a click anywhere outside a text field takes the caret out of it. The program's own controls are drawn by
    // hand and never take the keyboard, so a field that was being typed in (the number of a slider, a value of the
    // curve, a memory timing) stayed in editing whatever was clicked next: only a click into another field ended it.
    // Leaving the field ends the typing as it always did there: a slider takes its number as typed, then the click
    // goes on to what was clicked. One filter for the whole program (Program.Main), the dialogs included.
    class ClickAway : IMessageFilter
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetFocus();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);

        public bool PreFilterMessage(ref Message m)
        {
            // a mouse button going down (left, right, middle), inside a window or on its frame and scroll bars
            if (m.Msg != 0x201 && m.Msg != 0x204 && m.Msg != 0x207 && m.Msg != 0xA1 && m.Msg != 0xA4) return false;
            try
            {
                IntPtr f = GetFocus();
                if (f == IntPtr.Zero || f == m.HWnd) return false;
                var box = Control.FromHandle(f) as TextBoxBase;
                if (box == null || Control.FromHandle(m.HWnd) is TextBoxBase) return false; // another field takes the caret itself
                var form = box.FindForm();
                if (form == null) return false;
                form.ActiveControl = null;
                if (GetFocus() == f) SetFocus(form.Handle);
            }
            catch { }
            return false; // the click itself goes where it was going
        }
    }

    abstract class Painted : Control
    {
        protected bool OnGlass { get { return Backdrops.Of(this) != null; } }
        // the background: the window's picture when it has one, else the control's colour
        protected void Back(Graphics g) { if (!Backdrops.Paint(g, this)) g.Clear(BackColor); }
        // what a button fills itself with at rest: its background colour, or over glass a faint shade that lets it show
        protected Color Rest { get { return OnGlass ? Color.FromArgb(70, 0, 0, 0) : BackColor; } }

        protected override void WndProc(ref Message m) { if (Backdrops.Whole(this, ref m)) return; base.WndProc(ref m); }

        protected Painted()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
        }

        // a hidden control skips layout (WinForms defers it): a language switch resized the buttons of pages that were
        // not open, and their rows kept the old positions (12.11: «Custom» over the preset button). Lay out again when shown.
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) { PerformLayout(); Invalidate(); } }
        protected override void OnParentVisibleChanged(EventArgs e) { base.OnParentVisibleChanged(e); if (Visible) { PerformLayout(); Invalidate(); } }
    }

    class Toggle : Painted
    {
        bool on;
        public event EventHandler CheckedChanged;

        readonly ToggleAnim anim; // the knob slides, it does not jump (14.11)

        public Toggle() { Size = Theme.ToggleSize; Cursor = Cursors.Hand; BackColor = Theme.Card; anim = new ToggleAnim(this, on); }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; anim.Go(on); Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        // without the event: for loading saved values (and without the slide)
        public void Set(bool value) { on = value; anim.Snap(on); Invalidate(); }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Back(e.Graphics);
            Theme.DrawToggle(e.Graphics, new RectangleF(0, 0, Width, Height), anim.Pos);
            // switched off for now (Enabled = false): the same toggle, faded into the background
            if (!Enabled) using (var b = new SolidBrush(Color.FromArgb(150, BackColor))) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    class FlatButton : Painted, ILang
    {
        public void Relang() { OnTextChanged(EventArgs.Empty); }
        public bool Primary;
        bool hover;

        public FlatButton(string text, bool primary = false)
        {
            Primary = primary; Cursor = Cursors.Hand; Height = Theme.S(30); BackColor = Theme.Card; Text = text;
        }

        // one standard width for every button (12.11): wider only when the text needs it; a symbol («…») stays square-ish
        public static int StandardWidth { get { return Theme.S(132); } }

        protected virtual int TextWidth() { return TextRenderer.MeasureText(L.T(Text), Font).Width; }

        // 13.11: widths come from a short scale (132, 176, 220 … in steps of 44), not from each label, so buttons that
        // need a little more room than the standard one still match each other. The page can widen a button further
        // (MinWidth) to line it up with the other buttons in its column.
        public static int Step { get { return Theme.S(44); } }
        public bool Square { get { return L.T(Text ?? "").Trim().Length <= 2; } }
        public int NaturalWidth
        {
            get
            {
                if (Square) return Theme.S(44);
                int need = TextWidth() + Theme.S(26);
                if (need <= StandardWidth) return StandardWidth;
                return StandardWidth + (need - StandardWidth + Step - 1) / Step * Step;
            }
        }
        int minWidth;
        public int MinWidth { get { return minWidth; } set { if (minWidth == value) return; minWidth = value; Width = Math.Max(minWidth, NaturalWidth); } }

        protected override void OnTextChanged(EventArgs e)
        {
            Width = Math.Max(minWidth, NaturalWidth);
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Back(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (Primary && Enabled) Theme.Fill(g, r, Theme.S(6), hover ? Theme.AccentHover : Theme.Accent);
            else
            {
                Theme.Fill(g, r, Theme.S(6), hover && Enabled ? Theme.Raised : Rest);
                Theme.Stroke(g, r, Theme.S(6), Theme.BorderStrong);
            }
            var fg = !Enabled ? Theme.Muted : Primary ? Color.White : Theme.Text;
            var label = L.T(Text ?? "");
            if (!label.EndsWith("  ▾")) { Theme.Text1(g, Text, Font, new Rectangle(0, 0, Width, Height), fg, TextFormatFlags.HorizontalCenter); return; }

            // a drop-down (13.11): the label and a drawn arrow centered as one block. The «▾» glyph has empty space on its
            // right, so drawing the text as one string left the visible part a few pixels left of the middle.
            label = label.Substring(0, label.Length - 3);
            const TextFormatFlags how = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int aw = Theme.S(7), ah = Theme.S(4), gap = Theme.S(8);
            int tw = Math.Min(TextRenderer.MeasureText(g, label, Font, Size.Empty, how).Width, Width - aw - gap - Theme.S(12));
            int x = (Width - tw - gap - aw) / 2;
            TextRenderer.DrawText(g, label, Font, new Rectangle(x, 0, tw, Height), fg, how | TextFormatFlags.EndEllipsis);
            float ax = x + tw + gap, ay = (Height - ah) / 2f + 0.5f;
            using (var b = new SolidBrush(fg))
                g.FillPolygon(b, new[] { new PointF(ax, ay), new PointF(ax + aw, ay), new PointF(ax + aw / 2f, ay + ah) });
        }
    }

    // a row of choices, one selected (power modes, charge modes…)
    class Segmented : Painted, ILang
    {
        public void Relang() { SetItems(items); }
        string[] items;
        int selected = -1, hot = -1;
        Rectangle[] rects = new Rectangle[0];
        public event EventHandler Changed;

        public Segmented(params string[] items) { BackColor = Theme.Card; Height = Theme.S(30); SetItems(items); }

        // one switch split in two controls («Главная»: «Свой» apart, so it can go to the next line with the preset) keeps
        // its choices equally wide through this
        int minItem;
        public int ItemWidth { get; private set; }
        public int MinItemWidth { get { return minItem; } set { if (minItem == value) return; minItem = value; SetItems(items); } }

        public void SetItems(string[] items)
        {
            this.items = items;
            using (var g = CreateGraphics())
            {
                // all choices of one row equally wide (the widest text decides), at least a standard minimum
                int x = 0, w = Math.Max(Math.Max(Theme.S(64), minItem), items.Select(t => Theme.Measure(g, t, Theme.Body)).DefaultIfEmpty(0).Max() + Theme.S(22));
                ItemWidth = w;
                rects = items.Select(t => { var r = new Rectangle(x, 0, w, Height); x += w + Theme.S(4); return r; }).ToArray();
                Width = Math.Max(1, x - Theme.S(4));
            }
            Invalidate();
        }

        public int Selected
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Array.FindIndex(rects, r => r.Contains(e.Location));
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Array.FindIndex(rects, r => r.Contains(e.Location));
            if (i >= 0 && i != selected) { selected = i; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Back(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < items.Length; i++)
            {
                var r = new RectangleF(rects[i].X + 0.5f, 0.5f, rects[i].Width - 1.5f, Height - 1.5f);
                bool on = i == selected;
                if (!Enabled) // locked: the choice still shows, greyed
                {
                    Theme.Fill(g, r, Theme.S(6), on ? Theme.Raised : Rest);
                    Theme.Stroke(g, r, Theme.S(6), Theme.Border);
                    Theme.Text1(g, items[i], Theme.Body, rects[i], Theme.Muted, TextFormatFlags.HorizontalCenter);
                    continue;
                }
                Theme.Fill(g, r, Theme.S(6), on ? Theme.AccentBg : i == hot ? Theme.Raised : Rest);
                Theme.Stroke(g, r, Theme.S(6), on ? Theme.Accent : Theme.Border);
                Theme.Text1(g, items[i], Theme.Body, rects[i], on ? Theme.AccentFg : Theme.Text2, TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void OnEnabledChanged(EventArgs e) { hot = -1; Invalidate(); base.OnEnabledChanged(e); }
    }

    enum PillKind { Ok, Warn, Muted }

    class Pill : Painted, ILang
    {
        public void Relang() { if (!string.IsNullOrEmpty(Text)) Set(Text, kind); }
        PillKind kind = PillKind.Muted;
        public Pill() { BackColor = Theme.Card; Height = Theme.S(22); }

        public void Set(string text, PillKind k)
        {
            kind = k; Text = text;
            using (var g = CreateGraphics()) Width = Theme.Measure(g, text, Theme.Small) + Theme.S(18);
            Invalidate();
            if (Parent != null) Parent.PerformLayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bg = kind == PillKind.Ok ? Theme.SuccessBg : kind == PillKind.Warn ? Theme.WarningBg : Theme.Raised;
            var fg = kind == PillKind.Ok ? Theme.Success : kind == PillKind.Warn ? Theme.Warning : Theme.Text2;
            Theme.Fill(g, new RectangleF(0, 0, Width, Height), Theme.S(6), bg);
            Theme.Text1(g, Text, Theme.Small, new Rectangle(0, 0, Width, Height), fg, TextFormatFlags.HorizontalCenter);
        }
    }

    // a number to read, not to set, on the right of a row: drawn like the number of a slider (16.11: TDC and EDC)
    class ValueText : Painted
    {
        public ValueText() { BackColor = Theme.Card; Height = Theme.S(24); Width = Theme.S(60); }

        public void Set(string text)
        {
            if (Text == text) return;
            Text = text;
            int w = TextRenderer.MeasureText(text ?? "", Theme.Bold).Width + Theme.S(4);
            if (w != Width) { Width = w; if (Parent != null) Parent.PerformLayout(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Text ?? "", Theme.Bold, new Rectangle(0, 0, Width, Height), Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    // a setting: title and a gray line under it on the left, controls on the right
    class Row : Painted
    {
        public string Title, Sub;
        public bool WarnStyle;
        readonly List<Control> right = new List<Control>();

        public Row(string title, string sub = null, params Control[] controls)
        {
            Title = title; Sub = sub; BackColor = Theme.Window;
            Height = Theme.S(string.IsNullOrEmpty(sub) ? 48 : 60);
            foreach (var c in controls) AddRight(c);
        }

        public void AddRight(Control c)
        {
            c.BackColor = WarnStyle ? Theme.WarningBg : Theme.Card;
            right.Add(c); Controls.Add(c);
            c.SizeChanged += (s, e) => PerformLayout();
            PerformLayout();
        }

        public IEnumerable<Control> RightControls { get { return right; } }

        public void SetSub(string sub) { Sub = sub; Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        // optional third, dimmer line (e.g. where a component comes from); makes the row taller
        public string Extra;
        public void SetExtra(string extra) { Extra = extra; Height = BaseHeight; PerformLayout(); Invalidate(); }

        // 17.11: further in from both sides by this much, the text and the controls alike (the mini panel: its blocks
        // have round ends, and what stood at the usual distance from the side stood in the curve)
        public int Inset;

        // the height of the text part; a row whose controls don't fit beside the title is taller (Wrapped)
        int BaseHeight { get { return Theme.S(!string.IsNullOrEmpty(Extra) ? 84 : string.IsNullOrEmpty(Sub) ? 48 : 60); } }

        // 13.11: in a narrow window the controls on the right (the mode switch and the preset on «Главная») ran over the
        // title and out of the card on the left. When they don't leave the title room, they move to a line of their own
        // under the text, still on the right, and the row grows to hold them.
        bool wrapped;

        // 13.11: a control that stands closer to the one before it (the «Свой» part of the mode switch, 4 px like the
        // switch's own choices) and one that never goes to another line without the one before it (the preset choice
        // stays with «Свой»)
        readonly Dictionary<Control, int> gapBefore = new Dictionary<Control, int>();
        readonly HashSet<Control> keepWithPrevious = new HashSet<Control>();
        public void SetGapBefore(Control c, int gap) { gapBefore[c] = gap; PerformLayout(); }
        public void KeepWithPrevious(Control c) { keepWithPrevious.Add(c); PerformLayout(); }
        int Gap(Control c) { int g; return gapBefore.TryGetValue(c, out g) ? g : Theme.S(8); }

        readonly List<Control> top = new List<Control>(); // wrapped: the controls still beside the title

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // 18.11: arranged again when the row's width changed while it was being arranged. A row shown on a tab of the
            // power page still had the width it was made with: its controls went under the text, the row grew, the page
            // answered by giving the row its real width, and WinForms dropped that second layout because the first was
            // still running. The row stayed wide with the narrow arrangement: the buttons on the left under the text.
            for (int pass = 0; pass < 3; pass++) { int was = Width; Arrange(); if (Width == was) break; }
        }

        void Arrange()
        {
            top.Clear();
            var vis = right.Where(c => c.Visible).ToList();
            int need = vis.Sum(c => c.Width) + vis.Skip(1).Sum(c => Gap(c));
            // 19.11: the controls stay on the title's line while the window is made narrower: the title and the grey line
            // under it are cut with «…» instead (the user: nothing should jump down while the window is dragged smaller).
            // They go under the text only when they would not leave the title even this little room, where the other
            // choice is to run over it and out of the card. It used to keep up to 200 px for the title, so rows broke
            // in two long before they had to.
            int titleRoom = Math.Min(Theme.S(72), TextRenderer.MeasureText(L.T(Title ?? ""), Theme.Body).Width) + Theme.S(16 + 12) + Inset;
            wrapped = vis.Count > 0 && Width > 0 && need + titleRoom + (Theme.S(14) + Inset) > Width;
            if (wrapped)
            {
                // under the text, as many lines as needed: each line takes what fits in the row's width, right-aligned.
                // Controls kept together go as one piece.
                int room = Width - Theme.S(28) - 2 * Inset, lineGap = Theme.S(8);
                var units = new List<List<Control>>();
                foreach (var c in vis)
                    if (units.Count > 0 && keepWithPrevious.Contains(c)) units[units.Count - 1].Add(c); else units.Add(new List<Control> { c });
                // 13.11: the first pieces stay beside the title as long as they fit there; only the rest goes down, so a
                // switch with one extra choice drops just that part (one step), not the whole set
                top.Clear();
                int topUsed = 0, k = 0;
                for (; k < units.Count; k++)
                {
                    var u = units[k];
                    int uw = u.Sum(c => c.Width) + u.Skip(1).Sum(c => Gap(c)) + (top.Count > 0 ? Gap(u[0]) : 0);
                    if (topUsed + uw + titleRoom + (Theme.S(14) + Inset) > Width) break;
                    topUsed += uw; top.AddRange(u);
                }
                {
                    int x = Width - (Theme.S(14) + Inset);
                    for (int i = top.Count - 1; i >= 0; i--)
                    {
                        var c = top[i];
                        x -= c.Width;
                        c.Location = new Point(x, string.IsNullOrEmpty(Extra) ? (BaseHeight - c.Height) / 2 : Math.Max(Theme.S(8), (Theme.S(58) - c.Height) / 2));
                        x -= Gap(c);
                    }
                }
                var lines = new List<List<Control>> { new List<Control>() };
                int used = 0;
                foreach (var u in units.Skip(k))
                {
                    int uw = u.Sum(c => c.Width) + u.Skip(1).Sum(c => Gap(c));
                    var line = lines[lines.Count - 1];
                    if (line.Count > 0 && used + Gap(u[0]) + uw > room) { line = new List<Control>(); lines.Add(line); used = 0; }
                    used += (line.Count > 0 ? Gap(u[0]) : 0) + uw;
                    line.AddRange(u);
                }
                int y = BaseHeight - Theme.S(4);
                foreach (var line in lines)
                {
                    if (line.Count == 0) continue;
                    int lh = line.Max(c => c.Height);
                    int x = Width - (Theme.S(14) + Inset);
                    for (int i = line.Count - 1; i >= 0; i--)
                    {
                        var c = line[i];
                        x -= c.Width;
                        c.Location = new Point(Math.Max((Theme.S(14) + Inset), x), y + (lh - c.Height) / 2);
                        x -= Gap(c);
                    }
                    y += lh + lineGap;
                }
                int tall = y - lineGap + Theme.S(12);
                if (Height != tall) Height = tall;
            }
            else
            {
                if (Height != BaseHeight) Height = BaseHeight;
                int x = Width - (Theme.S(14) + Inset);
                for (int i = vis.Count - 1; i >= 0; i--)
                {
                    var c = vis[i];
                    x -= c.Width;
                    // with a third line the buttons sit level with the first two, so that line can run under them
                    int y = string.IsNullOrEmpty(Extra) ? (Height - c.Height) / 2 : Math.Max(Theme.S(8), (Theme.S(58) - c.Height) / 2);
                    c.Location = new Point(x, y);
                    x -= Gap(c);
                }
            }
            Invalidate();
        }

        int TextRight
        {
            get
            {
                var vis = (wrapped ? top : right).Where(c => c.Visible).ToList();
                return vis.Count > 0 ? vis.Min(c => c.Left) - Theme.S(12) : Width - (Theme.S(14) + Inset);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool glass = Backdrops.Paint(g, this); // the glass card and its rim are in the window's picture
            if (!glass) g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (!glass)
            {
                Theme.Fill(g, r, Theme.S(8), WarnStyle ? Theme.WarningBg : Theme.Card);
                Theme.Stroke(g, r, Theme.S(8), WarnStyle ? Color.FromArgb(0x6b, 0x55, 0x1f) : Theme.Border);
            }
            int x = Theme.S(16) + Inset, w = Math.Max(0, TextRight - x);
            // a row that does nothing right now (Enabled = false): grey text
            Color title = !Enabled ? Theme.Muted : WarnStyle ? Theme.Warning : Theme.Text, sub = !Enabled ? Theme.Muted : Theme.Text2;
            if (!string.IsNullOrEmpty(Extra))
            {
                Theme.Text1(g, Title, Theme.Body, new Rectangle(x, Theme.S(9), w, Theme.S(22)), title);
                Theme.Text1(g, Sub ?? "", Theme.Small, new Rectangle(x, Theme.S(33), w, Theme.S(20)), sub);
                Theme.Text1(g, Extra, Theme.Small, new Rectangle(x, Theme.S(56), Math.Max(0, Width - (Theme.S(14) + Inset) - x), Theme.S(20)), Theme.Muted);
            }
            else if (string.IsNullOrEmpty(Sub)) Theme.Text1(g, Title, Theme.Body, new Rectangle(x, 0, w, BaseHeight), title);
            else
            {
                int mid = BaseHeight / 2;
                Theme.Text1(g, Title, Theme.Body, new Rectangle(x, mid - Theme.S(21), w, Theme.S(22)), title);
                Theme.Text1(g, Sub, Theme.Small, new Rectangle(x, mid + Theme.S(1), w, Theme.S(20)), sub);
            }
        }
    }

    class SectionLabel : Painted
    {
        public SectionLabel(string text) { Text = text; Height = Theme.S(34); BackColor = Theme.Window; }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Theme.Text1(e.Graphics, Text, Theme.Title, new Rectangle(Theme.S(2), Theme.S(8), Width, Height - Theme.S(8)), Theme.Text);
        }
    }

    // big card on the home page: named values with bars in two columns, filled row by row (a null leaves its place empty).
    // 16.11: no load ring any more; the load (Head) is a bar in the right column on the title's line
    class StatCard : Painted
    {
        public class Metric { public string Name, Value; public double Fraction; public bool Warn; }

        public string Title = "", Subtitle = "", Note = "";
        public List<Metric> Metrics = new List<Metric>();
        public Metric Head; // drawn in the right column at the height of the title, or none

        public StatCard() { BackColor = Theme.Window; }

        int Top { get { return Head != null ? Theme.S(54) : Theme.S(48); } }

        public void Fit()
        {
            int rows = (Metrics.Count + 1) / 2;
            Height = Math.Max(Theme.S(64), Top + Theme.S(4) + rows * Theme.S(40) + Theme.S(6));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, r, Theme.S(10), Theme.Card);
            Theme.Stroke(g, r, Theme.S(10), Theme.Border);

            int x = Theme.S(22), w = Width - x - Theme.S(22);
            int colW = (w - Theme.S(24)) / 2;
            int tw = Theme.Measure(g, Title, Theme.Title);
            int textW = Head != null ? colW : w; // with a head bar the subtitle stays in the left column
            Theme.Text1(g, Title, Theme.Title, new Rectangle(x, Theme.S(12), tw + Theme.S(4), Theme.S(26)), Theme.Text, TextFormatFlags.NoPadding);
            Theme.Text1(g, Subtitle + (Note.Length > 0 ? " · " + Note : ""), Theme.Small, new Rectangle(x + tw + Theme.S(10), Theme.S(13), Math.Max(0, textW - tw - Theme.S(10)), Theme.S(26)),
                Note.Length > 0 ? Theme.Warning : Theme.Text2, TextFormatFlags.NoPadding);
            if (Head != null) Bar(g, Head, x + colW + Theme.S(24), Theme.S(14), colW);
            for (int i = 0; i < Metrics.Count; i++)
                if (Metrics[i] != null) Bar(g, Metrics[i], x + (i % 2) * (colW + Theme.S(24)), Top + (i / 2) * Theme.S(40), colW);
        }

        static void Bar(Graphics g, Metric m, int cx, int cy, int colW)
        {
            {
                int vw = Theme.Measure(g, m.Value, Theme.Bold) + Theme.S(4);
                Theme.Text1(g, m.Name, Theme.Small, new Rectangle(cx, cy, colW - vw - Theme.S(6), Theme.S(20)), Theme.Text2, TextFormatFlags.NoPadding);
                Theme.Text1(g, m.Value, Theme.Bold, new Rectangle(cx + colW - vw, cy, vw, Theme.S(20)), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.Right);
                var track = new RectangleF(cx, cy + Theme.S(24), colW, Theme.S(4));
                Theme.Fill(g, track, Theme.S(2), Theme.Border);
                double f = Math.Max(0, Math.Min(1, double.IsNaN(m.Fraction) ? 0 : m.Fraction));
                if (f > 0.005) Theme.FillGradient(g, new RectangleF(cx, track.Y, (float)(colW * f), track.Height), Theme.S(2), m.Warn, colW);
            }
        }
    }

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
            if (c >= 0 && e.Button == MouseButtons.Left) { drag = armed = c; SetValue(c, V(e.Y)); Focus(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag >= 0) SetValue(drag, V(e.Y));
            int h = drag >= 0 ? drag : Column(e.Location);
            int num = drag >= 0 ? -1 : NumAt(e.Location);
            Cursor = num >= 0 ? Cursors.Hand : h >= 0 && !readOnly ? Cursors.SizeNS : Cursors.Default;
            if (h != hot || num != hotNum) { hot = h; hotNum = num; Invalidate(); }
            if (armed >= 0 && Column(e.Location) != armed && drag < 0) armed = -1;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = -1; base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { armed = -1; hot = -1; hotNum = -1; Invalidate(); base.OnMouseLeave(e); }

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
    // a scrolling page: controls stacked top to bottom at the page width
    class Page : Panel
    {
        readonly List<KeyValuePair<Control, int>> items = new List<KeyValuePair<Control, int>>();

        public Page()
        {
            AutoScroll = true; BackColor = Theme.Window; Dock = DockStyle.Fill;
            motion.Tick += (s, e) => MotionTick();
            HandleCreated += (s, e) => Native.DarkScrollBars(Handle);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { if (!Backdrops.Paint(e.Graphics, this)) base.OnPaintBackground(e); }

        public T Add<T>(T c, int gapBefore = 8) where T : Control
        {
            items.Add(new KeyValuePair<Control, int>(c, gapBefore));
            Controls.Add(c);
            c.SizeChanged += (s, e) => { if (!inLayout) Relayout(); else again = true; };
            return c;
        }

        // for pages that rebuild a list (Автозагрузка): takes the control out of the stack and frees it
        public void Remove(Control c)
        {
            items.RemoveAll(kv => kv.Key == c);
            spots.Remove(c);
            Controls.Remove(c);
            c.Dispose();
        }

        public Row Row(string title, string sub = null, params Control[] right) { return Add(new Row(title, sub, right)); }
        public SectionLabel Section(string text) { return Add(new SectionLabel(text), 10); }

        // ---------- 19.11 (the user's wish): movement where things come and go ----------
        // Every page stacks its rows here, so the movement is made here once, for all of them. When a row is shown or
        // hidden (or changes its height), the rows under it glide to their new places in about a fifth of a second,
        // quick at first and slowing at the end. A row that comes is uncovered from its top edge down as the rows under
        // it make room (its window is cut to the part uncovered). A row that goes is already hidden by the page's code,
        // so its last picture is kept and drawn by the page itself, shrinking as the rows under it close in.
        // No movement: the first placing, a page that is not on screen, another width (the window resized), more than
        // four rows changing at once (a tab switched, a list rebuilt: that would be a stampede, not a movement), the mini
        // panel (its window is as tall as its rows), Windows' own «show animations» switched off, and the checks and
        // off-screen renders (Motion is set false there).
        public static bool Motion = MotionAllowed();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
        static bool MotionAllowed() { try { bool on; return !SystemParametersInfo(0x1042, 0, out on, 0) || on; } catch { return true; } } // SPI_GETCLIENTAREAANIMATION
        public const double MotionMs = 220;
        // a row's place: Y where it stands now and To where it goes (down from the page's top, whatever the scrolling),
        // From where the glide began; Was: it was shown at the last placing; Showing: it is being uncovered
        class Spot { public float Y, From; public int To; public bool Was, Showing, Placed; }
        class Ghost { public Bitmap Pic; public int X, Y, W, H; public float Left = 1; }
        readonly Dictionary<Control, Spot> spots = new Dictionary<Control, Spot>();
        readonly List<Ghost> ghosts = new List<Ghost>();
        readonly Timer motion = new Timer { Interval = 15 };
        DateTime motionStart; int placedWidth = -1; bool placedOnce;
        // A page that hides its rows one by one (a tab switched: each change places the rows anew) is seen by the number
        // of changes within a moment, not within one placing: more than four in 60 ms, and nothing glides.
        DateTime burstAt; int burst;
        internal bool Moving { get { return motion.Enabled; } }
        internal int GhostCount { get { return ghosts.Count; } }
        internal Bitmap GhostPic(int i) { return i < ghosts.Count ? ghosts[i].Pic : null; } // for the check

        static float Ease(double t) { t = t < 0 ? 0 : t > 1 ? 1 : t; double u = 1 - t; return (float)(1 - u * u * u); }

        void MotionTick()
        {
            double t = (DateTime.Now - motionStart).TotalMilliseconds / MotionMs;
            if (t >= 1 || !Visible || !IsHandleCreated) { MotionEnd(); return; }
            float e = Ease(t);
            bool was = inLayout; inLayout = true; SuspendLayout();
            foreach (var kv in items)
            {
                Spot sp; if (!spots.TryGetValue(kv.Key, out sp) || !kv.Key.Visible) continue;
                if (sp.To != sp.From) { sp.Y = sp.From + (sp.To - sp.From) * e; int y = AutoScrollPosition.Y + (int)Math.Round(sp.Y); if (kv.Key.Top != y) kv.Key.Top = y; }
                if (sp.Showing) Uncover(kv.Key, (int)Math.Round(kv.Key.Height * e));
            }
            ResumeLayout(false); inLayout = was;
            foreach (var g in ghosts) { g.Left = 1 - e; Invalidate(new Rectangle(g.X, AutoScrollPosition.Y + g.Y, g.W, g.H)); }
        }

        static void Uncover(Control c, int tall)
        {
            var old = c.Region;
            c.Region = tall >= c.Height ? null : new Region(new Rectangle(0, 0, c.Width, Math.Max(0, tall)));
            if (old != null) old.Dispose();
        }

        // everything in its place at once
        void MotionEnd()
        {
            motion.Stop();
            bool was = inLayout; inLayout = true; SuspendLayout();
            foreach (var kv in items)
            {
                Spot sp; if (!spots.TryGetValue(kv.Key, out sp)) continue;
                sp.Y = sp.From = sp.To;
                if (sp.Showing) { sp.Showing = false; Uncover(kv.Key, int.MaxValue); }
                if (kv.Key.Visible && sp.Placed) { int y = AutoScrollPosition.Y + sp.To; if (kv.Key.Top != y) kv.Key.Top = y; }
            }
            ResumeLayout(false); inLayout = was;
            DropGhosts();
        }

        void DropGhosts()
        {
            foreach (var g in ghosts) { Invalidate(new Rectangle(g.X, AutoScrollPosition.Y + g.Y, g.W, g.H)); g.Pic.Dispose(); }
            ghosts.Clear();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            foreach (var g in ghosts)
            {
                int tall = (int)Math.Round(g.H * g.Left); if (tall <= 0) continue;
                e.Graphics.DrawImage(g.Pic, new Rectangle(g.X, AutoScrollPosition.Y + g.Y, g.W, tall), new Rectangle(0, 0, g.W, tall), GraphicsUnit.Pixel);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { motion.Dispose(); foreach (var g in ghosts) g.Pic.Dispose(); ghosts.Clear(); }
            base.Dispose(disposing);
        }

        bool inLayout;
        // 16.11, for the mini panel: a page that is as tall as its rows and never scrolls, and its margins (the gap at
        // the sides and over the first row, in the units of Theme.S)
        public bool NoScroll;
        public int Side = 24, TopGap = 20;

        // pages whose items size themselves from the width (the program cards) set them here
        protected virtual void BeforeLayout(int width) { }

        // 13.11: the cards are fitted to the width BEFORE WinForms works out the scroll bars. After the window came back from
        // maximized, the still-wide cards made it add a horizontal bar and scroll the page sideways; the cards then got
        // narrower, but the bar and the sideways offset stayed. Pages scroll only up and down.
        protected override void OnLayout(LayoutEventArgs e)
        {
            Relayout();
            base.OnLayout(e);
            // WinForms keeps a horizontal bar it once needed even when nothing sticks out any more (seen after leaving
            // maximized): then the scrolling is switched off and on, which makes it work the bars out afresh
            HideSideBar();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr h, int bar, bool show);

        // the window's own horizontal bar (WS_HSCROLL) off when nothing sticks out: WinForms' HorizontalScroll.Visible said
        // false while the window still showed the bar, so it is asked of Windows, not of WinForms
        void HideSideBar()
        {
            if (!IsHandleCreated || (GetWindowLong(Handle, -16) & 0x100000) == 0) return;
            if (Controls.Cast<Control>().Any(c => c.Visible && c.Right > ClientSize.Width + 1)) return;
            if (AutoScrollPosition.X != 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y);
            ShowScrollBar(Handle, 0, false); // SB_HORZ
        }


        // a row that changed its height while being placed (its controls moved under the text in a narrow window): the
        // rows under it are placed again, at most a couple of times
        bool again;

        public void Relayout()
        {
            if (inLayout) return;
            for (int pass = 0; pass < 3; pass++)
            {
                again = false;
                Place();
                if (!again) break;
            }
        }

        void Place()
        {
            inLayout = true;
            SuspendLayout();
            if (AutoScrollPosition.X != 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y); // no sideways offset
            // the width leaves room for the vertical scroll bar whenever the page is taller than the window, decided before
            // the bar shows: cards sized for the width without it pushed past it once it appeared, and WinForms added a
            // horizontal bar that scrolled nothing (13.11, «Настройки»)
            int tall = Theme.S(44);
            foreach (var kv in items) if (kv.Key.Visible) tall += Theme.S(kv.Value) + kv.Key.Height;
            // 16.11: the room for the bar is kept on every page, tall or not. It used to be taken only where the page did
            // not fit, so the cards of a short page were a scroll bar wider than those of a long one: going between
            // «Кривая», «Автотюн» and «Сторож» (or the tabs of «Очистка») changed the width of everything. tall is left
            // for reading: it says whether the bar will show.
            // NoScroll (the mini panel): the page never scrolls, so no room is kept and the rows reach both sides alike
            int view = Width - (NoScroll ? 0 : SystemInformation.VerticalScrollBarWidth);
            int w = Math.Min(view, ClientSize.Width) - Theme.S(2 * Side);
            LineUpButtons();
            BeforeLayout(Math.Max(Theme.S(200), w));
            // 19.11: the places are worked out from the page's top (cy), the scrolling is added when a row is put there;
            // with movement a row is put where it stands now and glides to its place (see Motion)
            int wide = Math.Max(Theme.S(200), w), changed = 0;
            foreach (var kv in items) { Spot sp0; if (spots.TryGetValue(kv.Key, out sp0) && sp0.Was != kv.Key.Visible) changed++; }
            var nowAt = DateTime.Now;
            if (changed > 0) { if ((nowAt - burstAt).TotalMilliseconds > 60) { burstAt = nowAt; burst = 0; } burst += changed; }
            bool stampede = burst > 4 && (nowAt - burstAt).TotalMilliseconds <= 60;
            var form = FindForm();
            bool glide = Motion && !NoScroll && placedOnce && placedWidth == wide && !stampede && IsHandleCreated && Visible && form != null && form.Visible && form.WindowState != FormWindowState.Minimized;
            if (!glide && (motion.Enabled || ghosts.Count > 0)) MotionEnd();
            bool start = false;
            int cy = Theme.S(TopGap);
            foreach (var kv in items)
            {
                Spot sp; bool fresh = !spots.TryGetValue(kv.Key, out sp);
                if (fresh) spots[kv.Key] = sp = new Spot { Was = kv.Key.Visible };
                if (!kv.Key.Visible)
                {
                    // it has just been hidden: its last picture stays where it stood and shrinks away
                    if (sp.Was && glide && sp.Placed && kv.Key.Width > 0 && kv.Key.Height > 0)
                    {
                        try
                        {
                            var pic = new Bitmap(kv.Key.Width, kv.Key.Height);
                            kv.Key.DrawToBitmap(pic, new Rectangle(0, 0, pic.Width, pic.Height));
                            ghosts.Add(new Ghost { Pic = pic, X = Theme.S(Side), Y = (int)Math.Round(sp.Y), W = pic.Width, H = pic.Height });
                            start = true;
                        }
                        catch { }
                    }
                    if (sp.Showing) { sp.Showing = false; Uncover(kv.Key, int.MaxValue); }
                    sp.Was = false; continue;
                }
                cy += kv.Value == 8 && cy == Theme.S(TopGap) ? 0 : Theme.S(kv.Value);
                if (!glide || fresh || !sp.Placed) { sp.Y = sp.From = sp.To = cy; if (sp.Showing) { sp.Showing = false; Uncover(kv.Key, int.MaxValue); } }
                else if (!sp.Was)
                {
                    // it has just been shown: at its place at once, uncovered from the top as the rows under it make room
                    sp.Y = sp.From = sp.To = cy; sp.Showing = true; Uncover(kv.Key, 0); start = true;
                }
                else if (sp.To != cy) { sp.To = cy; start = true; }
                sp.Was = true; sp.Placed = true;
                kv.Key.Bounds = new Rectangle(Theme.S(Side), AutoScrollPosition.Y + (int)Math.Round(sp.Y), wide, kv.Key.Height);
                cy += kv.Key.Height;
            }
            if (start)
            {
                // a new glide (or one more change during a glide): everything goes on from where it stands now
                foreach (var sp in spots.Values) sp.From = sp.Y;
                motionStart = DateTime.Now; motion.Start();
            }
            placedOnce = true; placedWidth = wide;
            int y = AutoScrollPosition.Y + cy;
            if (!NoScroll) AutoScrollMinSize = new Size(0, y - AutoScrollPosition.Y + Theme.S(24));
            ResumeLayout();
            inLayout = false;
        }

        // 13.11: the rows with one button on the right («Balance ▾», «Открыть «Электропитание»», «Daily ▾») get one width
        // per page, the widest of them, so the column of buttons lines up. Rows with several buttons keep their own
        // widths (made equal they would squeeze the text), and so do the square «…» buttons.
        void LineUpButtons()
        {
            var lone = new List<FlatButton>();
            foreach (var kv in items)
            {
                var row = kv.Key as Row;
                if (row == null) continue;
                var buttons = row.RightControls.OfType<FlatButton>().ToList();
                if (buttons.Count == 1 && !buttons[0].Square) lone.Add(buttons[0]);
            }
            if (lone.Count < 2) return;
            int wide = lone.Max(b => b.NaturalWidth);
            foreach (var b in lone) b.MinWidth = wide;
        }

        // 14.11: rows that appear or go away (the advanced settings under their switch) slide: they come out from under
        // the row above, the rows below move down with them, and they go back the same way. Slow start, speed, soft stop.
        // The rows themselves do not move while it lasts: a picture of them does, in a pane whose height changes; the
        // real rows take its place when it is over. rows: next to each other on the page.
        class SlidePane : Control
        {
            public Bitmap Pic; public int Full;
            public SlidePane() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); BackColor = Theme.Window; }
            protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(BackColor); if (Pic != null) e.Graphics.DrawImageUnscaled(Pic, 0, Height - Full); }
            protected override void Dispose(bool disposing) { if (disposing && Pic != null) { Pic.Dispose(); Pic = null; } base.Dispose(disposing); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        Action finishSlide;

        public void Slide(Control[] rows, bool show)
        {
            if (finishSlide != null) finishSlide(); // one at a time: the last one ends at once
            rows = items.Select(kv => kv.Key).Where(c => rows.Contains(c)).ToArray();
            if (rows.Length == 0) return;
            if (!IsHandleCreated || !Visible || Width < 10) { foreach (var r in rows) r.Visible = show; Relayout(); return; }

            SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW off: none of the steps below shows
            SlidePane pane;
            try
            {
                if (show) { foreach (var r in rows) r.Visible = true; Relayout(); } // their places and sizes, to take the picture
                int top = rows.Min(r => r.Top), bottom = rows.Max(r => r.Bottom), left = rows.Min(r => r.Left), wide = rows.Max(r => r.Right) - left;
                int at = items.FindIndex(kv => kv.Key == rows[0]), gap = Theme.S(items[at].Value);
                var pic = new Bitmap(Math.Max(1, wide), Math.Max(1, bottom - top + gap));
                using (var g = Graphics.FromImage(pic))
                {
                    g.Clear(Theme.Window);
                    foreach (var r in rows)
                        using (var one = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height)))
                        {
                            r.DrawToBitmap(one, new Rectangle(0, 0, one.Width, one.Height));
                            g.DrawImageUnscaled(one, r.Left - left, r.Top - top + gap);
                        }
                }
                foreach (var r in rows) r.Visible = false;
                pane = new SlidePane { Pic = pic, Full = pic.Height, Height = show ? 0 : pic.Height };
                items.Insert(at, new KeyValuePair<Control, int>(pane, 0)); // its own gap is in the picture, so it closes to nothing
                Controls.Add(pane);
                Relayout();
            }
            finally { SendMessage(Handle, 0x000B, (IntPtr)1, IntPtr.Zero); }
            Invalidate(true); Update();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = new Timer { Interval = 15 };
            bool over = false;
            Action end = () =>
            {
                if (over) return;
                over = true; finishSlide = null;
                timer.Stop(); timer.Dispose();
                if (IsDisposed) return;
                SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    items.RemoveAll(kv => kv.Key == pane); Controls.Remove(pane); pane.Dispose();
                    foreach (var r in rows) r.Visible = show;
                    Relayout();
                }
                finally { SendMessage(Handle, 0x000B, (IntPtr)1, IntPtr.Zero); }
                Invalidate(true); Update();
            };
            finishSlide = end;
            timer.Tick += (s, e) =>
            {
                if (IsDisposed || pane.IsDisposed) { end(); return; }
                float t = Math.Min(1f, clock.ElapsedMilliseconds / 320f);
                float k = t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f;
                pane.Height = (int)Math.Round(pane.Full * (show ? k : 1f - k));
                Relayout(); pane.Invalidate(); Update();
                if (t >= 1f) end();
            };
            timer.Start();
        }

        // a bar under the page that doesn't scroll away (the window shows it at the bottom); null = none
        public Control Footer;

        // while a drop-down menu is open the pages don't scroll: the menu would stay put and the page slide away under it
        public static int MenusOpen;

        // a drop-down centred under its button (above = it opens upwards, for buttons near the bottom of the window)
        // 13.11: never narrower than its button (wider when the items need it), and centred by its real size once open —
        // the size known before showing missed the check-mark column on some lists, which then sat 16 px to the right
        public static void ShowAt(ContextMenuStrip m, Control anchor, bool above = false)
        {
            // the items as wide as the menu, so the highlight under the mouse (drawn in the item) fills it: a menu made
            // wider than its items kept them narrow and the highlight stopped short (13.11). The menu sizes its items by
            // what they need, a set size is ignored, so the room goes into their right padding, measured before
            // MinimumSize (which would hide the gap)
            int extra = anchor.Width - m.PreferredSize.Width;
            if (extra > 0)
                foreach (ToolStripItem it in m.Items)
                    it.Padding = new Padding(it.Padding.Left, it.Padding.Top, it.Padding.Right + extra, it.Padding.Bottom);
            m.MinimumSize = new Size(anchor.Width, 0);
            var size = m.PreferredSize;
            int w = Math.Max(anchor.Width, size.Width);
            m.Opened += (s, e) =>
            {
                var at = anchor.PointToScreen(Point.Empty);
                int x = at.X + (anchor.Width - m.Width) / 2;
                var screen = Screen.FromControl(anchor).WorkingArea;
                x = Math.Max(screen.Left, Math.Min(screen.Right - m.Width, x)); // still on the screen
                if (Math.Abs(m.Left - x) > 1) m.Left = x;
            };
            m.Show(anchor, new Point((anchor.Width - w) / 2, above ? -size.Height - Theme.S(2) : anchor.Height + Theme.S(2)));
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body, ShowImageMargin = true };
            m.Opened += (s, e) => MenusOpen++;
            m.Closed += (s, e) => { MenusOpen = Math.Max(0, MenusOpen - 1); m.BeginInvoke((Action)m.Dispose); };
            return m;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (MenusOpen > 0) { var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; return; }
            base.OnMouseWheel(e);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); HideSideBar(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Relayout(); }
    }

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

        void SetFrom(int x)
        {
            int v = min + (int)Math.Round((double)(x - Pad) / Math.Max(1, TrackW - 2 * Pad) * (max - min));
            if (Step > 1) v = min + (int)Math.Round((v - min) / (double)Step) * Step;
            v = Math.Max(min, Math.Min(max, v));
            if (v != value || unset) { value = v; unset = false; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
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
            if (e.X < TrackW) { drag = true; Grab(); SetFrom(e.X); }
            else if (e.Button == MouseButtons.Left) BeginEdit();
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) SetFrom(e.X);
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
            int v = Math.Max(min, Math.Min(max, value + (e.Delta > 0 ? Step : -Step)));
            if (v != value || unset) { Grab(); value = v; unset = false; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
            var he = e as HandledMouseEventArgs;
            if (he != null) he.Handled = true;
        }

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

    // FanControl's curve: 11 points = % of the max fan RPM at 0, 10 .. 100 °C, dragged up and down.
    // The dashed line is the fan's minimum: under it the app hands the fan back to the firmware.
    // The live marker shows the fan's temperature now and the RPM it really turns at.
    class TempFanCurve : Painted
    {
        public int[] Points = (int[])FanControl.DefaultCurve.Clone();
        public int MaxRpm = 5100, MinRpm = 1700, LiveTemp = -1, LiveRpm = -1;
        int drag = -1, hot = -1;
        public event EventHandler Changed;

        public TempFanCurve() { BackColor = Theme.Card; Height = Theme.S(240); }

        Rectangle Plot { get { return new Rectangle(Theme.S(46), Theme.S(12), Width - Theme.S(62), Height - Theme.S(64)); } }

        PointF At(int i, double pct) { var p = Plot; return new PointF(p.X + p.Width * i / 10f, (float)(p.Bottom - p.Height * pct / 100.0)); }

        int Hit(Point m)
        {
            for (int i = 0; i <= 10; i++) { var a = At(i, Points[i]); if (Math.Abs(a.X - m.X) < Theme.S(14) && Math.Abs(a.Y - m.Y) < Theme.S(14)) return i; }
            return -1;
        }

        string Rpm(double pct)
        {
            int r = (int)Math.Round(pct / 100.0 * MaxRpm);
            return r < MinRpm ? "прошивка" : r + " об/мин";
        }

        // 14.11: the curve never goes down as the temperature goes up. A point dragged up takes the points to its right
        // with it once it reaches their height; dragged down, it takes the points to its left. They are measured from
        // where they stood when the drag began (held), so going back lets them return to their places.
        int[] held;
        protected override void OnMouseDown(MouseEventArgs e) { drag = Hit(e.Location); held = (int[])Points.Clone(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = -1; base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag >= 0)
            {
                var p = Plot;
                int v = (int)Math.Round((p.Bottom - e.Y) * 100.0 / p.Height);
                v = Math.Max(0, Math.Min(100, v));
                if (v != Points[drag])
                {
                    Points[drag] = v;
                    if (held != null && held.Length == Points.Length)
                        for (int j = 0; j < Points.Length; j++)
                            if (j > drag) Points[j] = Math.Max(held[j], v);
                            else if (j < drag) Points[j] = Math.Min(held[j], v);
                    Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty);
                }
            }
            int ht = drag >= 0 ? drag : Hit(e.Location);
            Cursor = ht >= 0 ? Cursors.SizeNS : Cursors.Default;
            if (ht != hot) { hot = ht; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }

        // locked (Enabled = false, the custom fan curve is off): grey and not editable, the live marker stays
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); hot = drag = -1; Cursor = Cursors.Default; Invalidate(); }
        Color Line { get { return Enabled ? Theme.Accent : Theme.Muted; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            using (var pen = new Pen(Theme.Border))
            {
                for (int k = 0; k <= 100; k += 20)
                {
                    float y = p.Bottom - p.Height * k / 100f;
                    g.DrawLine(pen, p.X, y, p.Right, y);
                    Theme.Text1(g, k + "%", Theme.Small, new Rectangle(0, (int)y - Theme.S(10), p.X - Theme.S(8), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
                }
                for (int i = 0; i <= 10; i += 2)
                {
                    float x = p.X + p.Width * i / 10f;
                    Theme.Text1(g, i * 10 + " °C", Theme.Small, new Rectangle((int)x - Theme.S(30), p.Bottom + Theme.S(4), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }
            // the minimum RPM: below it the firmware drives the fan
            float minY = p.Bottom - p.Height * MinRpm / (float)MaxRpm;
            using (var pen = new Pen(Theme.Muted) { DashStyle = DashStyle.Dash })
                g.DrawLine(pen, p.X, minY, p.Right, minY);
            var pts = Enumerable.Range(0, 11).Select(i => At(i, Points[i])).ToArray();
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[10], new PointF(pts[10].X, p.Bottom)); path.AddLine(new PointF(pts[10].X, p.Bottom), new PointF(pts[0].X, p.Bottom)); path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(Enabled ? 40 : 22, Line))) g.FillPath(b, path);
            }
            using (var pen = new Pen(Line, Theme.S(2))) g.DrawLines(pen, pts);
            for (int i = 0; i <= 10; i++)
            {
                Theme.GlassKnob(g, pts[i].X, pts[i].Y, Theme.S(i == hot ? 17 : 14), 1f, i == hot ? Theme.AccentHover : Line, Theme.S(4.5f), !Enabled, 180f,
                    new PointF?[] { i > 0 ? pts[i - 1] : (PointF?)null, i < 10 ? pts[i + 1] : (PointF?)null }); // the line itself shows in the glass
            }
            // now: the temperature on the curve and the real RPM
            if (LiveTemp > 0)
            {
                float t = Math.Min(100, LiveTemp), x = p.X + p.Width * t / 100f;
                using (var pen = new Pen(Color.FromArgb(150, Theme.Warning)) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, x, p.Y, x, p.Bottom);
                if (LiveRpm >= 0)
                {
                    float y = p.Bottom - p.Height * Math.Min(1f, LiveRpm / (float)MaxRpm), r = Theme.S(5);
                    using (var b = new SolidBrush(Theme.Warning)) g.FillEllipse(b, x - r, y - r, 2 * r, 2 * r);
                }
            }
            string hint = Enabled ? "Тяните точки вверх и вниз. Ниже пунктира вентилятором управляет прошивка" : "Кривая не действует: вентиляторами управляет прошивка";
            if (hot >= 0) hint = hot * 10 + " °C → " + Points[hot] + "% · " + Rpm(Points[hot]);
            else if (LiveTemp > 0 && !Enabled) hint = "Сейчас " + LiveTemp + " °C" + (LiveRpm >= 0 ? " · крутится " + LiveRpm + " об/мин" : "") + " · кривая не действует, управляет прошивка";
            else if (LiveTemp > 0) hint = "Сейчас " + LiveTemp + " °C · по кривой " + Rpm(FanControl.Percent(Points, LiveTemp)) + (LiveRpm >= 0 ? " · крутится " + LiveRpm + " об/мин" : "");
            Theme.Text1(g, hint, Theme.Small, new Rectangle(p.X, p.Bottom + Theme.S(26), p.Width, Theme.S(22)), Theme.Text2);
        }
    }

    // a card with tabs on top, each tab with its own rows (like «Дополнительно» in the fan card);
    // Resized fires when switching changes the height, so the page can stack again
    class TabbedRows : Painted
    {
        readonly Segmented tabs;
        readonly List<Row>[] rows;
        public event Action Resized;

        public TabbedRows(params string[] names)
        {
            BackColor = Theme.Window;
            tabs = new Segmented(names) { Selected = 0, BackColor = Theme.Card };
            rows = names.Select(n => new List<Row>()).ToArray();
            Controls.Add(tabs);
            tabs.Changed += (s, e) => Switch();
            Fit();
        }

        int Sel { get { return Math.Max(0, tabs.Selected); } }

        public Row Add(int tab, Row r)
        {
            r.BackColor = Theme.Card; r.Visible = tab == Sel;
            rows[tab].Add(r); Controls.Add(r);
            // a row that moved its controls under the text (narrow window) grows: the card grows with it
            int last = r.Height;
            r.SizeChanged += (s, e) => { if (r.Height == last) return; last = r.Height; Fit(); PerformLayout(); };
            Fit();
            return r;
        }

        // for lists rebuilt on change (Настройки → «Как после загрузки»)
        public void Clear(int tab)
        {
            foreach (var r in rows[tab]) { Controls.Remove(r); r.Dispose(); }
            rows[tab].Clear();
        }

        // after Add/Clear: new height, rows placed again, the page stacks again
        public void Relist() { Switch(); }

        void Switch()
        {
            for (int i = 0; i < rows.Length; i++) foreach (var r in rows[i]) r.Visible = i == Sel;
            Fit(); PerformLayout();
            // a row places its controls only while visible
            foreach (var r in rows[Sel]) { r.PerformLayout(); r.Invalidate(true); }
            Invalidate();
            var h = Resized; if (h != null) h();
        }

        void Fit() { Height = Theme.S(12) + tabs.Height + Theme.S(12) + rows[Sel].Sum(r => r.Height + Theme.S(8)) + Theme.S(8); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (rows == null) return;
            tabs.Location = new Point(Theme.S(16), Theme.S(12));
            int y = tabs.Bottom + Theme.S(12);
            foreach (var r in rows[Sel]) { r.Bounds = new Rectangle(Theme.S(16), y, Width - Theme.S(32), r.Height); y += r.Height + Theme.S(8); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, r, Theme.S(10), Theme.Card);
            Theme.Stroke(g, r, Theme.S(10), Theme.Border);
        }
    }

    // the page's buttons at the bottom of the window, always in reach while the page scrolls
    class FooterBar : Painted
    {
        string status = "";
        readonly Control[] buttons;

        public FooterBar(params Control[] buttons)
        {
            this.buttons = buttons; BackColor = Theme.Card; Height = Theme.S(58); Dock = DockStyle.Bottom;
            foreach (var b in buttons) { b.BackColor = Theme.Card; Controls.Add(b); b.SizeChanged += (s, e) => PerformLayout(); }
        }

        public void SetStatus(string s) { status = s ?? ""; Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (buttons == null) return;
            int x = Width - Theme.S(24);
            for (int i = buttons.Length - 1; i >= 0; i--) { x -= buttons[i].Width; buttons[i].Location = new Point(x, (Height - buttons[i].Height) / 2); x -= Theme.S(8); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, 0, Width, 0);
            int right = buttons != null && buttons.Length > 0 ? buttons[0].Left - Theme.S(12) : Width;
            var box = new Rectangle(Theme.S(24), 0, Math.Max(0, right - Theme.S(24)), Height);
            // 16.11: next to four buttons the note has little room and was cut ("Включается п..."): when it does not fit
            // on one line it goes on two, in the small font
            if (status.Length == 0 || Theme.Measure(g, status, Theme.Body) + Theme.S(8) <= box.Width) { Theme.Text1(g, status, Theme.Body, box, Theme.Text2); return; }
            var wf = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl;
            int line = TextRenderer.MeasureText(g, "Ag", Theme.Small).Height;
            int h = Math.Min(line * 2, TextRenderer.MeasureText(g, L.T(status), Theme.Small, new Size(box.Width, Height), wf).Height);
            TextRenderer.DrawText(g, L.T(status), Theme.Small, new Rectangle(box.X, (Height - h) / 2, box.Width, h), Theme.Text2, wf);
        }
    }
}
namespace RedTune
{
    // two (or more) halves on top of related pages («Кривая» | «Автотюн»): a quick switch between them
    class PageTabs : Painted
    {
        readonly string[] items;
        public readonly string[] Notes;   // small text after an item (e.g. "идёт"), drawn in the warning color
        int selected;
        int hot = -1;
        public event Action<int> Picked;

        public PageTabs(int selected, params string[] items)
        {
            this.items = items; this.selected = selected; Notes = new string[items.Length];
            BackColor = Theme.Window; Height = Theme.S(46);
        }

        // 19.11: the cells. They were all equally wide, whatever stood in them: in a narrow window «Схемы питания» did not
        // fit its fifth of the row and ran over «Вентиляторы» and out of its own highlight. Now:
        //  - equal cells while the longest name fits one (a wide window looks as before);
        //  - else each cell as wide as its name needs, the room left over shared out equally;
        //  - and when even that is too much, the space around the names shrinks first, then the cells shrink in
        //    proportion and a name that still does not fit ends in «…».
        Rectangle[] cells = new Rectangle[0]; string cellsFor;

        // 19.11: the width of a text as it is drawn here (no padding). It used to be measured with the padding Windows
        // adds on both sides and drawn without it, so every name stood a few pixels left of the middle of its cell:
        // not seen while the cells were wide, plain once they fit the names closely.
        // Measured on a Graphics: TextRenderer.MeasureText without one ignores NoPadding and answers 12 px too wide
        // (measured 19.11: «Схемы питания» 159 against 147 with a Graphics and 145 px of ink), which is exactly the
        // shift that was to be removed.
        const TextFormatFlags Plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        static int TextWidth(Graphics g, string s, Font f) { return TextRenderer.MeasureText(g, s, f, Size.Empty, Plain).Width; }

        int NameWidth(Graphics g, int i)
        {
            int w = TextWidth(g, L.T(items[i]), Theme.Bold);
            if (!string.IsNullOrEmpty(Notes[i])) w += TextWidth(g, "● " + L.T(Notes[i]), Theme.Small) + Theme.S(8);
            return w;
        }

        Rectangle[] Cells()
        {
            string key = Width + "|" + Height + "|" + L.Code + "|" + string.Join("|", items) + "|" + string.Join("|", Notes);
            if (key == cellsFor) return cells;
            int n = items.Length; var need = new int[n]; var w = new double[n];
            using (var g = CreateGraphics()) for (int i = 0; i < n; i++) need[i] = NameWidth(g, i);
            int pad = Theme.S(36), minPad = Theme.S(22), sum = need.Sum(); // room around a name: in equal cells, and at the least
            if (n > 0 && need.Max() + pad <= Width / n) for (int i = 0; i < n; i++) w[i] = Width / (double)n;
            else if (sum + n * minPad <= Width) { double extra = (Width - sum) / (double)n; for (int i = 0; i < n; i++) w[i] = need[i] + extra; }
            else for (int i = 0; i < n; i++) w[i] = Width * (need[i] + minPad) / (double)Math.Max(1, sum + n * minPad);
            cells = new Rectangle[n]; double x = 0;
            for (int i = 0; i < n; i++) { int left = (int)Math.Round(x); x += w[i]; cells[i] = new Rectangle(left, 0, (i == n - 1 ? Width : (int)Math.Round(x)) - left, Height); }
            cellsFor = key;
            return cells;
        }

        Rectangle Cell(int i) { return Cells()[i]; }
        int Hit(Point p) { for (int i = 0; i < items.Length; i++) if (Cell(i).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            Cursor = h >= 0 && h != selected ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Hit(e.Location);
            if (i >= 0 && i != selected) { var h = Picked; if (h != null) h(i); }
            base.OnMouseClick(e);
        }

        public void SetNote(int i, string note) { Notes[i] = note; Invalidate(); }
        public int Selected { get { return selected; } set { selected = value; hot = -1; Invalidate(); } } // tabs inside one page («Очистка»)

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var all = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, all, Theme.S(8), Theme.Card);
            Theme.Stroke(g, all, Theme.S(8), Theme.Border);
            for (int i = 0; i < items.Length; i++)
            {
                var c = Cell(i);
                var r = new RectangleF(c.X + Theme.S(4) + 0.5f, Theme.S(4) + 0.5f, c.Width - Theme.S(8) - 1, Height - Theme.S(8) - 1);
                bool on = i == selected;
                if (on) { Theme.Fill(g, r, Theme.S(6), Theme.AccentBg); Theme.Stroke(g, r, Theme.S(6), Theme.Accent); }
                else if (i == hot) Theme.Fill(g, r, Theme.S(6), Theme.Raised);
                var text = L.T(items[i]);
                int tw = TextWidth(g, text, Theme.Bold);
                string note = string.IsNullOrEmpty(Notes[i]) ? null : "● " + L.T(Notes[i]);
                int nw = note != null ? TextWidth(g, note, Theme.Small) + Theme.S(8) : 0;
                // a name wider than its cell (only in a window too narrow for all the names) is cut with «…», never drawn over its neighbour
                int room = c.Width - Theme.S(12) - nw; if (tw > room) tw = Math.Max(Theme.S(10), room);
                int x = c.X + (c.Width - tw - nw) / 2;
                TextRenderer.DrawText(g, text, Theme.Bold, new Rectangle(x, 0, tw + Theme.S(2), Height), on ? Theme.AccentFg : Theme.Text2, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (note != null) TextRenderer.DrawText(g, note, Theme.Small, new Rectangle(x + tw + Theme.S(8), 0, nw, Height), Theme.Warning, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }
}
