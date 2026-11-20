using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
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

        // without the event: for loading saved values, no slide. 20.11: the same value leaves the toggle alone, so a page that
        // reads its values back right after a click (the plan block of «Питание»: «Запретить парковку ядер») no longer cuts off
        // the slide the click started
        public void Set(bool value) { if (value == on) return; on = value; anim.Snap(on); Invalidate(); }

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
}
