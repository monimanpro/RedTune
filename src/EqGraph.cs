using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // The equalizer: the curve over a frequency axis, a knob per band. A knob is dragged (frequency and gain), the
    // wheel over it changes its width, a right click picks the kind of filter or deletes it, a press of the wheel on a strip
    // puts it back (20.11), a double click on empty
    // space adds one. Behind the curve, what the device plays right now (the spectrum of the processed sound).
    class EqGraph : Painted
    {
        public SoundParams P = SoundParams.Start();
        public double Rate = 48000;
        public bool Dim;
        public event EventHandler Changed;
        const double MinF = 20, MaxF = 20000;
        // 19.11: the scale is ±18 dB, and ±24 while the curve goes beyond that (a preset fitted to Nahimic's measured
        // response raises the bass by 23 dB): otherwise the curve ran along the edge of the plot. Not changed while a
        // point is held, so the scale does not jump under the mouse.
        float Range = 18;
        void FitRange()
        {
            if (drag >= 0 || dragPart != Part.None) return;
            double top = 0;
            for (int i = 0; i <= 30; i++) top = Math.Max(top, Math.Abs(AudioFx.Response(P, MinF * Math.Pow(MaxF / MinF, i / 30.0), Rate)));
            foreach (var b in P.Bands) top = Math.Max(top, Math.Abs((b.HasGain ? b.Gain : 0) + P.Preamp));
            Range = top > 17.9 ? 24 : 18;
        }
        int hot = -1, drag = -1; bool armed;
        float[] spec; bool specOn;

        public static readonly string[] Kinds = { "", "Колокол", "Низкая полка", "Высокая полка", "Срез низких", "Срез высоких", "Режектор" };

        public EqGraph() { BackColor = Theme.Window; Height = Theme.S(390); } // 19.11: 300 -> 390, the user asked for the block 30 % taller

        // 19.11, the strips on the right of the plot (the user: the same bands as knobs, as mixing programs have them).
        // One strip per band, in the order of the bands' frequencies, so a strip stands where its point stands along the
        // plot: the kind of filter on top (a click opens the list), a fader for the gain, a dial for the frequency and
        // a dial for the width. They are the same values as the points: moving one moves the other, a band removed on
        // either side is gone on both, and after the last strip stands «+» while there are fewer than 12 bands.
        // The order is not changed while something is held, so a strip does not jump from under the mouse.
        // 20.11 (the user): the pre-gain («Общее усиление», before the equalizer) moved from a row of the page into the
        // picture, as the first strip left of the bands' ones: a fader of ±24 dB by half a dB, its value under it.
        enum Part { None, Kind, Fader, Freq, Wide, Add, Master }
        const int StripW = 27, Gutter = 24, MasterW = 33, MasterDb = 24;
        int[] order = new int[0];
        Part hotPart, dragPart; int dragBand = -1, dragY; float dragFrom;

        internal int[] Order()
        {
            if (order.Length != P.Bands.Count || (drag < 0 && dragPart == Part.None))
                order = Enumerable.Range(0, P.Bands.Count).OrderBy(i => P.Bands[i].Freq).ThenBy(i => i).ToArray();
            return order;
        }

        int Slots { get { return P.Bands.Count + (P.Bands.Count < SoundParams.MaxBands ? 1 : 0); } }
        // no strips in a window so narrow that the plot would have nothing left
        int PanelW { get { int w = Theme.S(MasterW + Gutter + Slots * StripW); return Width - Theme.S(46 + 14 + 12) - w < Theme.S(170) ? 0 : w; } }
        Rectangle Plot { get { int pw = PanelW; return new Rectangle(Theme.S(46), Theme.S(14), Width - Theme.S(46) - (pw > 0 ? pw + Theme.S(14 + 12) : Theme.S(18)), Height - Theme.S(72)); } }
        // 19.11: as tall as the plot, but standing in the middle of the card: the kind pictures as far from its top edge as
        // the lower dials from its bottom edge (level with the plot it stood high, the plot having its scale and the hint under it)
        internal Rectangle Panel { get { var r = Plot; return new Rectangle(r.Right + Theme.S(14), (Height - r.Height - Theme.S(2)) / 2, PanelW, r.Height); } }
        internal int StripX(int slot) { return Panel.X + Theme.S(MasterW + Gutter) + slot * Theme.S(StripW) + Theme.S(StripW) / 2; } // its middle
        internal int MasterX { get { return Panel.X + Theme.S(StripW) / 2; } }
        float MasterY(double db) { return (float)(FaderTop + (FaderBottom - FaderTop) * (0.5 - Math.Max(-MasterDb, Math.Min(MasterDb, db)) / (2.0 * MasterDb))); }
        internal int FaderTop { get { return Panel.Y + Theme.S(30); } }
        internal int FaderBottom { get { return Panel.Bottom - Theme.S(64); } }
        internal int FreqY { get { return Panel.Bottom - Theme.S(40); } }
        internal int WideY { get { return Panel.Bottom - Theme.S(12); } }
        float FaderY(double db) { return (float)(FaderTop + (FaderBottom - FaderTop) * (0.5 - Math.Max(-Range, Math.Min(Range, db)) / (2 * Range))); }

        // what of the strips is under the mouse; band: its index among the bands
        Part HitPanel(Point m, out int band)
        {
            band = -1;
            var p = Panel; if (p.Width <= 0 || m.X < p.X || m.X >= p.Right || m.Y < p.Y - Theme.S(4) || m.Y > p.Bottom + Theme.S(4)) return Part.None;
            if (m.X < p.X + Theme.S(StripW)) return Part.Master;
            if (m.X < p.X + Theme.S(MasterW + Gutter)) return Part.None;
            int slot = (m.X - p.X - Theme.S(MasterW + Gutter)) / Theme.S(StripW);
            var ord = Order();
            if (slot == ord.Length && ord.Length < SoundParams.MaxBands) return Part.Add;
            if (slot < 0 || slot >= ord.Length) return Part.None;
            band = ord[slot];
            if (m.Y < FaderTop - Theme.S(8)) return Part.Kind;
            if (m.Y <= FaderBottom + Theme.S(8)) return Part.Fader;
            if (Math.Abs(m.Y - FreqY) <= Theme.S(13)) return Part.Freq;
            return Part.Wide;
        }

        static double RoundHz(double f) { return f < 100 ? Math.Round(f) : f < 1000 ? Math.Round(f / 5) * 5 : f < 10000 ? Math.Round(f / 50) * 50 : Math.Round(f / 500) * 500; }

        // a new band from «+»: a bell with no gain in the widest gap between the bands there are
        void AddBand()
        {
            if (P.Bands.Count >= SoundParams.MaxBands) return;
            var fs = new List<double> { MinF }; fs.AddRange(P.Bands.Select(b => (double)b.Freq).OrderBy(f => f)); fs.Add(MaxF);
            int at = 0; double widest = 0;
            for (int i = 0; i + 1 < fs.Count; i++) { double gap = Math.Log(fs[i + 1] / fs[i]); if (gap > widest) { widest = gap; at = i; } }
            P.Bands.Add(new EqBand { Type = 1, Freq = (float)RoundHz(Math.Sqrt(fs[at] * fs[at + 1])), Gain = 0, Q = 1f });
            hot = P.Bands.Count - 1;
            Fire();
        }
        float X(double f) { var r = Plot; return (float)(r.X + r.Width * Math.Log(f / MinF) / Math.Log(MaxF / MinF)); }
        double FreqAt(float x) { var r = Plot; return MinF * Math.Pow(MaxF / MinF, Math.Max(0, Math.Min(1, (x - r.X) / (double)r.Width))); }
        float Y(double db) { var r = Plot; return (float)(r.Y + r.Height * (0.5 - Math.Max(-Range - 2, Math.Min(Range + 2, db)) / (2 * Range))); }
        double DbAt(float y) { var r = Plot; return (0.5 - (y - r.Y) / (double)r.Height) * 2 * Range; }
        internal PointF Knob(int i) { var b = P.Bands[i]; return new PointF(X(b.Freq), Y((b.HasGain ? b.Gain : 0) + P.Preamp)); } // on the line, which the pre-gain moves

        int Hit(Point m)
        {
            int best = -1; float bd = Theme.S(15) * Theme.S(15);
            for (int i = 0; i < P.Bands.Count; i++) { var k = Knob(i); float d = (k.X - m.X) * (k.X - m.X) + (k.Y - m.Y) * (k.Y - m.Y); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        // a change by the mouse (a drag, a dial, the wheel) is painted at once: Windows paints a window only when no other
        // message waits, and a fast mouse sends its moves without a pause — the picture stood still while it was dragged
        // and moved again only when the mouse stopped (20.11, the user: «двигаешь и даже не видишь, куда двигаешь»)
        void Fire() { Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); Update(); }

        internal static string Hz(double f) { return f >= 1000 ? (f / 1000).ToString(f >= 10000 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " кГц" : f.ToString("0", CultureInfo.InvariantCulture) + " Гц"; }
        static string Gain(double g) { return (g > 0.04 ? "+" : g < -0.04 ? "−" : "") + Math.Abs(g).ToString("0.0", CultureInfo.InvariantCulture) + " дБ"; }

        // the fader follows the mouse; a dial turns with the mouse going up and down from where it was taken
        void DragStrip(int y)
        {
            if (dragPart == Part.Master)
            {
                // from where it was taken, by the mouse's travel (20.11: the knob no longer jumps to the press)
                double pdb = Math.Round((dragFrom + (dragY - y) * 2.0 * MasterDb / Math.Max(1, FaderBottom - FaderTop)) * 2) / 2;
                if (Math.Abs(pdb) < 0.35) pdb = 0;
                P.Preamp = (float)Math.Max(-MasterDb, Math.Min(MasterDb, pdb));
                Fire(); return;
            }
            if (dragBand < 0 || dragBand >= P.Bands.Count) return;
            var b = P.Bands[dragBand];
            if (dragPart == Part.Fader)
            {
                if (!b.HasGain) return;
                double gdb = Math.Round((dragFrom + (dragY - y) * 2.0 * Range / Math.Max(1, FaderBottom - FaderTop)) * 10) / 10; // from where it was taken
                if (Math.Abs(gdb) < 0.35) gdb = 0;
                b.Gain = (float)Math.Max(-Range, Math.Min(Range, gdb));
            }
            else
            {
                double turn = (dragY - y) / (double)Theme.S(220); // the whole range in 220 units of travel
                if (dragPart == Part.Freq) b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(dragFrom * Math.Pow(MaxF / MinF, turn))));
                else b.Q = (float)Math.Max(0.2, Math.Min(12, Math.Round(dragFrom * Math.Pow(60, turn) * 100) / 100));
            }
            Fire();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            armed = true;
            if (e.Button == MouseButtons.Middle) { ResetAt(e.Location); Invalidate(); base.OnMouseDown(e); return; }
            int band = -1; var part = Dim ? Part.None : HitPanel(e.Location, out band);
            if (part != Part.None)
            {
                if (part == Part.Add) { if (e.Button == MouseButtons.Left) AddBand(); }
                else if (part == Part.Master)
                {
                    // the pointer onto the knob, the knob stays (20.11)
                    if (e.Button == MouseButtons.Left) { dragPart = part; dragBand = -1; dragFrom = P.Preamp; int ky = (int)Math.Round(MasterY(P.Preamp)); dragY = Theme.Warp(this, MasterX, ky) ? ky : e.Y; Invalidate(); }
                }
                else if (e.Button == MouseButtons.Right || part == Part.Kind) Menu(band, e.Location);
                else if (e.Button == MouseButtons.Left)
                {
                    dragPart = part; dragBand = band; dragY = e.Y; dragFrom = part == Part.Freq ? P.Bands[band].Freq : P.Bands[band].Q; hot = band;
                    // a fader: the pointer goes onto its knob and the knob stays where it is; the drag starts from there (20.11)
                    if (part == Part.Fader && P.Bands[band].HasGain)
                    {
                        dragFrom = P.Bands[band].Gain; int ky = (int)Math.Round(FaderY(dragFrom));
                        if (Theme.Warp(this, StripX(Array.IndexOf(Order(), band)), ky)) dragY = ky;
                    }
                    Invalidate();
                }
                base.OnMouseDown(e); return;
            }
            int h = Hit(e.Location);
            if (e.Button == MouseButtons.Left) drag = Dim ? -1 : h;
            else if (e.Button == MouseButtons.Right && !Dim) Menu(h, e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = -1; dragPart = Part.None; dragBand = -1; Invalidate(); base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragPart != Part.None) { DragStrip(e.Y); base.OnMouseMove(e); return; }
            if (drag >= 0 && drag < P.Bands.Count)
            {
                var b = P.Bands[drag];
                b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(FreqAt(e.X))));
                if (b.HasGain) { double gdb = Math.Round((DbAt(e.Y) - P.Preamp) * 10) / 10; if (Math.Abs(gdb) < 0.35) gdb = 0; b.Gain = (float)Math.Max(-Range, Math.Min(Range, gdb)); }
                Fire();
            }
            int band = -1; var part = drag >= 0 || Dim ? Part.None : HitPanel(e.Location, out band);
            int ht = drag >= 0 ? drag : Dim ? -1 : part != Part.None ? band : Hit(e.Location);
            Cursor = part == Part.Kind || part == Part.Add ? Cursors.Hand : part != Part.None ? Cursors.SizeNS : ht >= 0 ? Cursors.SizeAll : Cursors.Default;
            if (ht != hot || part != hotPart) { hot = ht; hotPart = part; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            armed = false;
            if (drag < 0 && dragPart == Part.None && (hot >= 0 || hotPart != Part.None)) { hot = -1; hotPart = Part.None; Invalidate(); }
            base.OnMouseLeave(e);
        }

        // the wheel works only over a point or a strip and after a click on the graph; otherwise the page scrolls.
        // Over a point or a width dial it changes the width, over a fader the gain by half a dB, over a frequency dial the frequency
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            bool master = armed && !Dim && hotPart == Part.Master;
            if (!master && (!armed || hot < 0 || hot >= P.Bands.Count || Dim || hotPart == Part.Kind || hotPart == Part.Add)) { base.OnMouseWheel(e); return; }
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            // 20.11: one notch = one step, also from a smooth wheel that sends a notch in parts (Theme.WheelNotches)
            int n = Theme.WheelNotches(ref wheelAcc, e.Delta);
            for (int i = 0; i < Math.Abs(n); i++)
            {
                bool up = n > 0;
                if (master) { P.Preamp = (float)Math.Max(-MasterDb, Math.Min(MasterDb, Math.Round((P.Preamp + (up ? 0.5 : -0.5)) * 2) / 2)); continue; }
                var b = P.Bands[hot];
                if (hotPart == Part.Fader) { if (b.HasGain) b.Gain = (float)Math.Max(-Range, Math.Min(Range, Math.Round((b.Gain + (up ? 0.5 : -0.5)) * 2) / 2)); }
                else if (hotPart == Part.Freq) b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(b.Freq * (up ? 1.06 : 1 / 1.06))));
                else b.Q = (float)Math.Max(0.2, Math.Min(12, Math.Round(b.Q * (up ? 1.15 : 1 / 1.15) * 100) / 100));
            }
            if (n != 0) Fire();
        }
        int wheelAcc;

        // a press of the wheel: «Общ.» back to no gain, a band's fader to zero, its width dial to 1 (20.11, the user's wish:
        // it was a double click, which went off by itself when a fader was clicked twice quickly)
        void ResetAt(Point at)
        {
            int band = -1; var part = Dim ? Part.None : HitPanel(at, out band);
            if (part == Part.Master) { P.Preamp = 0; Fire(); }
            else if (band >= 0 && band < P.Bands.Count)
            {
                if (part == Part.Fader && P.Bands[band].HasGain) { P.Bands[band].Gain = 0; Fire(); }
                else if (part == Part.Wide) { P.Bands[band].Q = 1f; Fire(); }
            }
        }

        // a double click on empty space adds a point (that one stays a double click: it adds, it does not reset)
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int band = -1; var part = Dim ? Part.None : HitPanel(e.Location, out band);
            if (e.Button == MouseButtons.Left && !Dim && part == Part.None && Hit(e.Location) < 0 && Plot.Contains(e.Location)) AddAt(e.Location);
            base.OnMouseDoubleClick(e);
        }

        void AddAt(Point at)
        {
            if (P.Bands.Count >= SoundParams.MaxBands) return;
            double gdb = Math.Round((DbAt(at.Y) - P.Preamp) * 2) / 2;
            P.Bands.Add(new EqBand { Type = 1, Freq = (float)Math.Round(FreqAt(at.X)), Gain = (float)Math.Max(-Range, Math.Min(Range, gdb)), Q = 1f });
            hot = P.Bands.Count - 1;
            Fire();
        }

        void Menu(int band, Point at)
        {
            var m = Page.Menu();
            if (band >= 0)
            {
                var b = P.Bands[band];
                for (int t = 1; t < Kinds.Length; t++)
                {
                    int type = t;
                    var it = new ToolStripMenuItem(Kinds[t]) { Checked = b.Type == t };
                    it.Click += (s, e) => { b.Type = type; if (!b.HasGain) b.Gain = 0; if (type >= 4 && b.Q > 3 && type != 6) b.Q = 0.7f; Fire(); };
                    m.Items.Add(it);
                }
                m.Items.Add(new ToolStripSeparator());
                var zero = new ToolStripMenuItem("Усиление в ноль"); zero.Click += (s, e) => { b.Gain = 0; Fire(); }; zero.Enabled = b.HasGain; m.Items.Add(zero);
                var del = new ToolStripMenuItem("Удалить точку"); del.Click += (s, e) => { P.Bands.Remove(b); hot = -1; Fire(); }; m.Items.Add(del);
            }
            else
            {
                var add = new ToolStripMenuItem("Добавить точку"); add.Enabled = P.Bands.Count < SoundParams.MaxBands && Plot.Contains(at); add.Click += (s, e) => AddAt(at); m.Items.Add(add);
                var flat = new ToolStripMenuItem("Все усиления в ноль"); flat.Click += (s, e) => { foreach (var b in P.Bands) b.Gain = 0; Fire(); }; m.Items.Add(flat);
            }
            m.Show(this, at);
        }

        // the spectrum of what plays (dB per bin of AudioFx.Spectrum), null = nothing to show; drawn rising at once and
        // falling slowly, per pixel column
        public void Feed(double[] db)
        {
            if (db == null) { if (specOn) { specOn = false; Invalidate(); } return; }
            var r = Plot; int n = Math.Max(1, r.Width);
            if (spec == null || spec.Length != n) { spec = new float[n]; for (int i = 0; i < n; i++) spec[i] = -120; }
            double binHz = Rate / AudioFx.SpectrumSize;
            for (int px = 0; px < n; px++)
            {
                double f0 = FreqAt(r.X + px - 0.5f), f1 = FreqAt(r.X + px + 0.5f);
                int b0 = (int)Math.Floor(f0 / binHz), b1 = (int)Math.Ceiling(f1 / binHz);
                double v;
                if (b1 - b0 <= 1)
                {
                    // low frequencies: fewer bins than pixels, a straight line between the two nearest
                    double pos = (f0 + f1) / 2 / binHz; int a = Math.Max(1, Math.Min(db.Length - 2, (int)Math.Floor(pos))); double t = Math.Max(0, Math.Min(1, pos - a));
                    v = db[a] + (db[a + 1] - db[a]) * t;
                }
                else { v = -200; for (int b = Math.Max(1, b0); b <= b1 && b < db.Length; b++) if (db[b] > v) v = db[b]; }
                // tilted by 3 dB per octave around 1 kHz, as analyzers show music: flat to the eye when it sounds balanced
                v += 3 * Math.Log((f0 + f1) / 2 / 1000, 2);
                float now = (float)v;
                spec[px] = now > spec[px] ? spec[px] + (now - spec[px]) * 0.6f : spec[px] + (now - spec[px]) * 0.12f;
            }
            specOn = true;
            // the whole picture every moment, as it always was: 20.11 the user tried the plot alone with its still parts
            // kept as pictures and the point felt late under the mouse, and wanted the equalizer back as it was (the
            // compressor keeps its cached curve)
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        void SpectrumFill(ILive cv, Rectangle r)
        {
            if (!specOn || spec == null || spec.Length != r.Width) return;
            var pts = new List<PointF> { new PointF(r.X, r.Bottom) };
            for (int px = 0; px < spec.Length; px += 2) pts.Add(new PointF(r.X + px, r.Bottom - r.Height * Math.Max(0f, Math.Min(1f, (spec[px] + 84f) / 84f))));
            pts.Add(new PointF(r.Right, r.Bottom));
            cv.FillPoly(Color.FromArgb(34, Theme.Text), pts.ToArray());
        }

        // the dashed curve of the band under the mouse, the whole curve with its fill, and the points
        void CurveAndKnobs(Graphics g, Rectangle r, Color line)
        {
            var clip = g.Clip; g.SetClip(r);
            int steps = Math.Max(40, r.Width / 2);
            if (hot >= 0 && hot < P.Bands.Count)
            {
                var one = new PointF[steps + 1];
                for (int i = 0; i <= steps; i++) { double f = MinF * Math.Pow(MaxF / MinF, i / (double)steps); one[i] = new PointF(X(f), Y(AudioFx.Response(P, f, Rate, hot) + P.Preamp)); }
                using (var pen = new Pen(Color.FromArgb(120, Theme.Text2), Theme.S(1.2f)) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, one);
            }
            var curve = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++) { double f = MinF * Math.Pow(MaxF / MinF, i / (double)steps); curve[i] = new PointF(X(f), Y(AudioFx.Response(P, f, Rate))); } // with the pre-gain: the whole line goes up or down by it (20.11)
            using (var path = new GraphicsPath())
            {
                path.AddLines(curve);
                path.AddLine(curve[steps], new PointF(curve[steps].X, Y(0))); path.AddLine(new PointF(curve[steps].X, Y(0)), new PointF(curve[0].X, Y(0))); path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(Dim ? 18 : 36, line))) g.FillPath(b, path);
            }
            using (var pen = new Pen(line, Theme.S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, curve);
            g.Clip = clip;
            for (int i = 0; i < P.Bands.Count; i++)
            {
                var k = Knob(i); bool h = i == hot;
                float size = Theme.S(h ? 19 : 16);
                int a = 0, b = curve.Length - 1;
                while (a + 1 < curve.Length && curve[a + 1].X < k.X - size) a++;
                while (b - 1 >= 0 && curve[b - 1].X > k.X + size) b--;
                var piece = new PointF[Math.Max(0, b - a + 1)]; if (piece.Length > 0) Array.Copy(curve, a, piece, 0, piece.Length);
                Theme.GlassKnob(g, k.X, k.Y, size, 1f, h ? Theme.AccentHover : line, Theme.S(4.5f), Dim, 180f, null, -1f, piece);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, card, Theme.S(8), Theme.Card);
            Theme.Stroke(g, card, Theme.S(8), Theme.Border);
            var r = Plot;
            if (r.Width < 20 || r.Height < 20) return;
            FitRange();
            Color line = Dim ? Theme.Muted : Theme.Accent;

            using (var pen = new Pen(Theme.Border))
            using (var zeroPen = new Pen(Theme.BorderStrong))
            {
                for (int db = -(int)Range; db <= Range; db += 6)
                {
                    float y = Y(db);
                    g.DrawLine(db == 0 ? zeroPen : pen, r.X, y, r.Right, y);
                    Theme.Text1(g, (db > 0 ? "+" : db < 0 ? "−" : "") + Math.Abs(db), Theme.Small, new Rectangle(0, (int)y - Theme.S(10), r.X - Theme.S(8), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
                }
                foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    float x = X(f);
                    g.DrawLine(pen, x, r.Y, x, r.Bottom);
                    Theme.Text1(g, f >= 1000 ? f / 1000 + "k" : f.ToString(), Theme.Small, new Rectangle((int)x - Theme.S(30), r.Bottom + Theme.S(4), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }

            // what plays now (-84 dB at the bottom of the plot, 0 dB at its top), then the curve and its points
            var clip = g.Clip; g.SetClip(r);
            SpectrumFill(new GdiLive(g), r);
            g.Clip = clip;
            CurveAndKnobs(g, r, line);

            DrawStrips(g, line);

            string hint;
            if (!Dim && (hotPart == Part.Master || dragPart == Part.Master))
                hint = "Общее усиление" + ": " + Gain(P.Preamp) + " · " + L.T("До эквалайзера: убавьте, если полосы подняты высоко и лимитер срабатывает постоянно") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (hot >= 0 && hot < P.Bands.Count)
            {
                var b = P.Bands[hot];
                hint = Kinds[Math.Max(1, Math.Min(6, b.Type))] + " · " + Hz(b.Freq) + (b.HasGain ? " · " + Gain(b.Gain) : "") + " · " + "ширина Q " + b.Q.ToString("0.##", CultureInfo.InvariantCulture) + " · " + "колесо меняет ширину, правая кнопка — вид фильтра";
            }
            else if (Dim) hint = "Эквалайзер выключен";
            else hint = "Тяните точки. Двойной щелчок по пустому месту добавляет точку, правая кнопка на точке — вид фильтра или удаление";
            Theme.Text1(g, hint, Theme.Small, new Rectangle(r.X, r.Bottom + Theme.S(28), Width - r.X - Theme.S(14), Theme.S(22)), Theme.Text2);
        }

        // the kind of a filter as a small picture of its curve, in a box w x h around (cx, cy)
        static void KindIcon(Graphics g, int type, float cx, float cy, float w, float h, Color c)
        {
            float[][] shape;
            switch (type)
            {
                case 2: shape = new[] { new[] { 0f, 0.15f }, new[] { 0.3f, 0.15f }, new[] { 0.7f, 0.85f }, new[] { 1f, 0.85f } }; break;                     // low shelf
                case 3: shape = new[] { new[] { 0f, 0.85f }, new[] { 0.3f, 0.85f }, new[] { 0.7f, 0.15f }, new[] { 1f, 0.15f } }; break;                     // high shelf
                case 4: shape = new[] { new[] { 0f, 1f }, new[] { 0.45f, 0.2f }, new[] { 1f, 0.2f } }; break;                                                   // lows cut
                case 5: shape = new[] { new[] { 0f, 0.2f }, new[] { 0.55f, 0.2f }, new[] { 1f, 1f } }; break;                                                   // highs cut
                case 6: shape = new[] { new[] { 0f, 0.2f }, new[] { 0.36f, 0.2f }, new[] { 0.5f, 1f }, new[] { 0.64f, 0.2f }, new[] { 1f, 0.2f } }; break;   // notch
                default: shape = new[] { new[] { 0f, 0.9f }, new[] { 0.25f, 0.85f }, new[] { 0.5f, 0.1f }, new[] { 0.75f, 0.85f }, new[] { 1f, 0.9f } }; break; // bell
            }
            var pts = shape.Select(p => new PointF(cx - w / 2 + p[0] * w, cy - h / 2 + p[1] * h)).ToArray();
            using (var pen = new Pen(c, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                if (type >= 4) g.DrawLines(pen, pts); else g.DrawCurve(pen, pts, 0.35f);
        }

        // a dial: a ring three quarters round, lit as far as the value goes (0..1), and a pointer
        internal static void Dial(Graphics g, float cx, float cy, float frac, Color lit, bool hover, bool dim, float radius = 8.5f)
        {
            float rad = Theme.S(radius), pw = Theme.S(radius > 10 ? 3.2f : 2.4f);
            frac = Math.Max(0f, Math.Min(1f, frac));
            var box = new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2);
            using (var back = new Pen(Theme.Border, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(back, box, 135, 270);
            if (frac > 0.005f) using (var on = new Pen(lit, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(on, box, 135, 270 * frac);
            double a = (135 + 270 * frac) * Math.PI / 180;
            float inner = rad - pw - Theme.S(1.5f);
            using (var b = new SolidBrush(hover ? Theme.Raised : Theme.Window)) g.FillEllipse(b, cx - inner, cy - inner, inner * 2, inner * 2);
            using (var pen = new Pen(dim ? Theme.Muted : hover ? Theme.Text : Theme.Text2, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx + (float)Math.Cos(a) * inner * 0.25f, cy + (float)Math.Sin(a) * inner * 0.25f, cx + (float)Math.Cos(a) * inner * 0.95f, cy + (float)Math.Sin(a) * inner * 0.95f);
        }

        // the pre-gain's strip: on top a flat line with arrows up and down (the whole curve moves), the fader, the value
        // under it, «дБ» in the row of the widths; a thin line parts it from the bands' strips
        void DrawMaster(Graphics g, Color line, int top, int bottom, int sw, float th)
        {
            var p = Panel; float cx = MasterX, mid = (top + bottom) / 2f;
            bool h = !Dim && (hotPart == Part.Master || dragPart == Part.Master);
            if (h) Theme.Fill(g, new RectangleF(cx - sw / 2f + 1, p.Y - Theme.S(4), sw - 2, p.Height + Theme.S(8)), Theme.S(6), Color.FromArgb(46, Theme.Text));
            var ic = Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2;
            using (var pen = new Pen(ic, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float iy = p.Y + Theme.S(9), w = Theme.S(15);
                g.DrawLine(pen, cx - w / 2, iy, cx - Theme.S(3.5f), iy); g.DrawLine(pen, cx + Theme.S(3.5f), iy, cx + w / 2, iy);
                float a = Theme.S(2.5f), up = iy - Theme.S(4.5f), dn = iy + Theme.S(4.5f);
                g.DrawLine(pen, cx, up, cx, dn);
                g.DrawLines(pen, new[] { new PointF(cx - a, up + a), new PointF(cx, up), new PointF(cx + a, up + a) });
                g.DrawLines(pen, new[] { new PointF(cx - a, dn - a), new PointF(cx, dn), new PointF(cx + a, dn - a) });
            }
            Theme.Tube(g, new RectangleF(cx - th / 2f, top, th, bottom - top));
            using (var tick = new Pen(Theme.BorderStrong)) g.DrawLine(tick, cx - Theme.S(6), mid, cx + Theme.S(6), mid);
            float y = MasterY(P.Preamp);
            if (Math.Abs(y - mid) > 1f) Theme.Fill(g, new RectangleF(cx - th / 2f, Math.Min(y, mid), th, Math.Abs(y - mid)), th / 2f, Dim ? Theme.Muted : line);
            Theme.GlassKnob(g, cx, y, Theme.S(h ? 16 : 14), Dim ? 0f : 1f, h ? Theme.AccentHover : line, th, Dim, y <= mid ? 90f : 270f, null, Dim ? 0f : Math.Abs(y - mid));
            string v = P.Preamp == 0 ? "0" : (P.Preamp > 0 ? "+" : "−") + Math.Abs(P.Preamp).ToString("0.#", CultureInfo.InvariantCulture);
            Theme.Text1(g, v, Theme.Small, new Rectangle((int)cx - Theme.S(20), FreqY - Theme.S(10), Theme.S(40), Theme.S(20)), Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, TextFormatFlags.HorizontalCenter);
            Theme.Text1(g, "дБ", Theme.Small, new Rectangle((int)cx - Theme.S(20), WideY - Theme.S(10), Theme.S(40), Theme.S(20)), Theme.Muted, TextFormatFlags.HorizontalCenter);
            using (var sep = new Pen(Theme.Border)) { float sx = p.X + Theme.S(StripW) + Theme.S(3); g.DrawLine(sep, sx, p.Y, sx, p.Bottom); }
        }

        void DrawStrips(Graphics g, Color line)
        {
            var p = Panel; if (p.Width <= 0) return;
            var ord = Order();
            int top = FaderTop, bottom = FaderBottom, sw = Theme.S(StripW);
            float th = Theme.S(5), mid = (top + bottom) / 2f;
            // what the two rows of dials are
            Theme.Text1(g, "Гц", Theme.Small, new Rectangle(p.X + Theme.S(MasterW) - Theme.S(4), FreqY - Theme.S(10), Theme.S(Gutter), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
            Theme.Text1(g, "Q", Theme.Small, new Rectangle(p.X + Theme.S(MasterW) - Theme.S(4), WideY - Theme.S(10), Theme.S(Gutter), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
            DrawMaster(g, line, top, bottom, sw, th);
            using (var tick = new Pen(Theme.BorderStrong))
                for (int k = 0; k < ord.Length; k++)
                {
                    var b = P.Bands[ord[k]]; bool h = ord[k] == hot; float cx = StripX(k);
                    // the strip of the band under the mouse (here or on the plot) stands out
                    if (h) Theme.Fill(g, new RectangleF(cx - sw / 2f + 1, p.Y - Theme.S(4), sw - 2, p.Height + Theme.S(8)), Theme.S(6), Color.FromArgb(Dim ? 20 : 46, Theme.Text));
                    KindIcon(g, b.Type, cx, p.Y + Theme.S(9), Theme.S(15), Theme.S(9), Dim ? Theme.Muted : h && hotPart == Part.Kind ? Theme.Text : Theme.Text2);
                    // the fader: a tube, a mark at no gain, the bar from that mark to the knob
                    Theme.Tube(g, new RectangleF(cx - th / 2f, top, th, bottom - top));
                    g.DrawLine(tick, cx - Theme.S(6), mid, cx + Theme.S(6), mid);
                    bool gain = b.HasGain && !Dim;
                    float y = b.HasGain ? FaderY(b.Gain) : mid;
                    if (b.HasGain && Math.Abs(y - mid) > 1f) Theme.Fill(g, new RectangleF(cx - th / 2f, Math.Min(y, mid), th, Math.Abs(y - mid)), th / 2f, Dim ? Theme.Muted : line);
                    Theme.GlassKnob(g, cx, y, Theme.S(h && (hotPart == Part.Fader || dragPart == Part.Fader) ? 16 : 14), gain ? 1f : 0f, h ? Theme.AccentHover : line, th, !gain, y <= mid ? 90f : 270f, null, gain ? Math.Abs(y - mid) : 0f);
                    Dial(g, cx, FreqY, (float)(Math.Log(b.Freq / MinF) / Math.Log(MaxF / MinF)), Dim ? Theme.Muted : h ? Theme.AccentHover : line, h && (hotPart == Part.Freq || dragPart == Part.Freq), Dim);
                    Dial(g, cx, WideY, (float)(Math.Log(Math.Max(0.2, b.Q) / 0.2) / Math.Log(60)), Dim ? Theme.Muted : h ? Theme.AccentHover : line, h && (hotPart == Part.Wide || dragPart == Part.Wide), Dim);
                }
            if (ord.Length < SoundParams.MaxBands)
            {
                // «+»: one more band
                float cx = StripX(ord.Length), s = Theme.S(18); bool h = hotPart == Part.Add && !Dim;
                var box = new RectangleF(cx - s / 2, mid - s / 2, s, s);
                if (h) Theme.Fill(g, box, Theme.S(5), Theme.Raised);
                Theme.Stroke(g, box, Theme.S(5), h ? Theme.BorderStrong : Theme.Border);
                using (var pen = new Pen(Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - Theme.S(4), mid, cx + Theme.S(4), mid); g.DrawLine(pen, cx, mid - Theme.S(4), cx, mid + Theme.S(4));
                }
            }
        }
    }
}
