using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
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
                // 20.11 (the user): one gap everywhere, as it is seen: from the inside of the frame's line to the outside of
                // the cell's line, and between two cells' lines. Measured at 150 % the first try gave 6 px at the frame and
                // 8 px between cells: the gap was counted from the frame's outer edge, and the half pixel of each line
                // around the cells widened the gap between them. Now the edges are worked out where they show: the frame's
                // line covers 0..1 at the left and top, W-1.5..W-0.5 and H-1.5..H-0.5 at the right and bottom; a cell's line
                // is drawn half a pixel inside its outer edge
                float gap = Theme.S(5);
                float left = i == 0 ? 1f + gap : c.X + gap / 2, right = i == items.Length - 1 ? Width - 1.5f - gap : c.Right - gap / 2;
                float top = 2f + gap, bottom = Height - 1.5f - gap; // the top: the glass shine under the line makes it 2 px
                var r = RectangleF.FromLTRB(left + 0.5f, top + 0.5f, right - 0.5f, bottom - 0.5f);
                bool on = i == selected;
                // 20.11 (the user): the red cell (and the hover under it) rounder, 10; the frame stays at 8
                if (on) { Theme.Fill(g, r, Theme.S(10), Theme.AccentBg); Theme.Stroke(g, r, Theme.S(10), Theme.Accent); }
                else if (i == hot) Theme.Fill(g, r, Theme.S(10), Theme.Raised);
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
