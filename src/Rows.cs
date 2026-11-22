using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
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

        // 23.11: a row made with an empty sub is one line tall (48); a sub set later has to make it two lines tall (60) and
        // move the rows under it, or the sub is drawn over the bottom edge (the GPU page's «Пресет» row did that)
        public void SetSub(string sub)
        {
            bool grow = string.IsNullOrEmpty(Sub) != string.IsNullOrEmpty(sub);
            Sub = sub; Invalidate();
            if (!grow) return;
            PerformLayout();
            for (Control c = Parent; c != null; c = c.Parent) { var pg = c as Page; if (pg != null) { pg.Relayout(); break; } }
        }

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
            // 20.11: a row that is a block of the mini panel: the window is the card (Windows rounds it and draws its rim).
            // The row's own card showed inside it, with the page colour in its corners and another radius: two cards
            // of two shapes, one in the other (the user's screenshot)
            bool inBlock = Parent is IBackdrop;
            if (!glass) g.Clear(inBlock ? Parent.BackColor : BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (!glass && !inBlock)
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
}
