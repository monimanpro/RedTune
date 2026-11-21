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
        // 21.11 (the user): the de-esser's band, shown on the plot and not edited there; DsDb: how deep it takes it down now
        public bool DsOn; public float DsLo = 6500, DsHi = 10000, DsDb;
        public event EventHandler Changed;
        const double MinF = 20, MaxF = 20000;
        // 19.11: the scale is ±18 dB, and ±24 while the curve goes beyond that (a preset fitted to Nahimic's measured
        // response raises the bass by 23 dB): otherwise the curve ran along the edge of the plot. Not changed while a
        // point is held, so the scale does not jump under the mouse.
        // 21.11 (the user, a quiet microphone with +20.5 dB of pre-gain): the curve went off the top of the plot at ±24,
        // so the scale grows on to ±36 and ±48 (pre-gain 24 + a band's 24). A band's own gain stays within ±24 (GainMax),
        // and its fader keeps a scale of at most ±24: only the picture of the whole curve grows.
        float Range = 18;
        const float GainMax = 24;
        float FaderRange { get { return Math.Min(Range, GainMax); } }
        void FitRange()
        {
            if (drag >= 0 || dragPart != Part.None) return;
            double top = 0;
            for (int i = 0; i <= 30; i++) top = Math.Max(top, Math.Abs(AudioFx.Response(P, MinF * Math.Pow(MaxF / MinF, i / 30.0), Rate)));
            foreach (var b in P.Bands) top = Math.Max(top, Math.Abs((b.HasGain ? b.Gain : 0) + P.Preamp));
            Range = top > 35.9 ? 48 : top > 23.9 ? 36 : top > 17.9 ? 24 : 18;
        }
        int hot = -1, drag = -1; bool armed;
        float[] spec; bool specOn;

        public static readonly string[] Kinds = { "", "Колокол", "Низкая полка", "Высокая полка", "Срез низких", "Срез высоких", "Режектор" };

        public EqGraph() { BackColor = Theme.Window; Height = Theme.S(390); } // 19.11: 300 -> 390, the user asked for the block 30 % taller

        // 19.11, the strips on the right of the plot (the user: the same bands as knobs, as mixing programs have them).
        // One strip per band: the kind of filter on top (a click opens the list), a fader for the gain, a dial for the
        // frequency and a dial for the width. They are the same values as the points: moving one moves the other.
        // 21.11 (the user): always all twelve bands and no «+»; the strips stand in the bands' own order (by frequency
        // when the settings were loaded, SoundParams.FillBands) and do not move when a point is dragged past another.
        // 20.11 (the user): the pre-gain («Общее усиление», before the equalizer) moved from a row of the page into the
        // picture, as the first strip left of the bands' ones: a fader of ±24 dB by half a dB, its value under it.
        enum Part { None, Kind, Fader, Freq, Wide, Slope, Master }
        const int StripW = 27, Gutter = 24, MasterW = 33, MasterDb = 24;
        int[] order = new int[0];
        Part hotPart, dragPart; int dragBand = -1, dragY; float dragFrom;

        internal int[] Order()
        {
            if (order.Length != P.Bands.Count) order = Enumerable.Range(0, P.Bands.Count).ToArray();
            return order;
        }

        int Slots { get { return P.Bands.Count; } }
        // no strips in a window so narrow that the plot would have nothing left
        int PanelW { get { int w = Theme.S(MasterW + Gutter + Slots * StripW); return Width - Theme.S(46 + 14 + 12) - w < Theme.S(170) ? 0 : w; } }
        Rectangle Plot { get { int pw = PanelW; return new Rectangle(Theme.S(46), Theme.S(14), Width - Theme.S(46) - (pw > 0 ? pw + Theme.S(14 + 12) : Theme.S(18)), Height - Theme.S(72)); } }
        // 19.11: as tall as the plot, but standing in the middle of the card: the kind pictures as far from its top edge as
        // the lower dials from its bottom edge (level with the plot it stood high, the plot having its scale and the hint under it)
        // 21.11: what a moment of the sound changes: the plot (the spectrum and the de-esser's depth are clipped to it). A
        // point on its edge is drawn whole and clipped here; its part outside did not change, so it stays as it was
        Rectangle LiveArea { get { return Rectangle.Inflate(Plot, 1, 1); } }
        public void InvalidateLive() { LiveInvalidate(LiveArea); }
        internal Rectangle Panel { get { var r = Plot; return new Rectangle(r.Right + Theme.S(14), (Height - r.Height - Theme.S(2)) / 2, PanelW, r.Height); } }
        internal int StripX(int slot) { return Panel.X + Theme.S(MasterW + Gutter) + slot * Theme.S(StripW) + Theme.S(StripW) / 2; } // its middle
        internal int MasterX { get { return Panel.X + Theme.S(StripW) / 2; } }
        float MasterY(double db) { return (float)(FaderTop + (FaderBottom - FaderTop) * (0.5 - Math.Max(-MasterDb, Math.Min(MasterDb, db)) / (2.0 * MasterDb))); }
        internal int FaderTop { get { return Panel.Y + Theme.S(30); } }
        internal int FaderBottom { get { return Panel.Bottom - Theme.S(92); } }
        internal int FreqY { get { return Panel.Bottom - Theme.S(68); } }
        internal int WideY { get { return Panel.Bottom - Theme.S(40); } }
        internal int SlopeRowY { get { return Panel.Bottom - Theme.S(12); } }
        // 21.11 (the user): a third dial under each band, its slope 1..4 for every kind: a cut 12..48 dB/oct, the others
        // from smooth to straight
        internal static string SlopeText(EqBand b) { return b.IsCut ? (12 * b.Slope).ToString(CultureInfo.InvariantCulture) + " дБ/окт" : L.T("крутизна ") + b.Slope + "/4"; }
        static string SlopeItem(EqBand b, int s) { return b.IsCut ? (12 * s).ToString(CultureInfo.InvariantCulture) + " дБ/окт" : s == 1 ? "1 · " + L.T("плавно") : s == EqBand.MaxSlope ? s + " · " + L.T("прямо") : s.ToString(); }
        float FaderY(double db) { return (float)(FaderTop + (FaderBottom - FaderTop) * (0.5 - Math.Max(-FaderRange, Math.Min(FaderRange, db)) / (2 * FaderRange))); }

        // what of the strips is under the mouse; band: its index among the bands
        Part HitPanel(Point m, out int band)
        {
            band = -1;
            var p = Panel; if (p.Width <= 0 || m.X < p.X || m.X >= p.Right || m.Y < p.Y - Theme.S(4) || m.Y > p.Bottom + Theme.S(4)) return Part.None;
            if (m.X < p.X + Theme.S(StripW)) return Part.Master;
            if (m.X < p.X + Theme.S(MasterW + Gutter)) return Part.None;
            int slot = (m.X - p.X - Theme.S(MasterW + Gutter)) / Theme.S(StripW);
            var ord = Order();
            if (slot < 0 || slot >= ord.Length) return Part.None;
            band = ord[slot];
            if (m.Y < FaderTop - Theme.S(8)) return Part.Kind;
            if (m.Y <= FaderBottom + Theme.S(8)) return Part.Fader;
            if (Math.Abs(m.Y - FreqY) <= Theme.S(13)) return Part.Freq;
            if (Math.Abs(m.Y - WideY) <= Theme.S(13)) return Part.Wide;
            return Part.Slope;
        }

        static double RoundHz(double f) { return f < 100 ? Math.Round(f) : f < 1000 ? Math.Round(f / 5) * 5 : f < 10000 ? Math.Round(f / 50) * 50 : Math.Round(f / 500) * 500; }

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
            if (dragPart == Part.Slope)
            {
                // four steps over 70 units of travel (21.11: over 110 it felt stuck between the steps)
                int s = (int)Math.Round(dragFrom + (dragY - y) * (EqBand.MaxSlope - 1.0) / Theme.S(70));
                s = Math.Max(1, Math.Min(EqBand.MaxSlope, s));
                if (s == b.Slope) return;
                b.Slope = s;
            }
            else if (dragPart == Part.Fader)
            {
                if (!b.HasGain) return;
                double gdb = Math.Round((dragFrom + (dragY - y) * 2.0 * FaderRange / Math.Max(1, FaderBottom - FaderTop)) * 10) / 10; // from where it was taken
                if (Math.Abs(gdb) < 0.35) gdb = 0;
                b.Gain = (float)Math.Max(-FaderRange, Math.Min(FaderRange, gdb)); // as far as the fader shows (its scale grows after)
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
                if (part == Part.Master)
                {
                    // the pointer onto the knob, the knob stays (20.11)
                    if (e.Button == MouseButtons.Left) { dragPart = part; dragBand = -1; dragFrom = P.Preamp; int ky = (int)Math.Round(MasterY(P.Preamp)); dragY = Theme.Warp(this, MasterX, ky) ? ky : e.Y; Invalidate(); }
                }
                else if (e.Button == MouseButtons.Right) Menu(band, e.Location);
                // 21.11 (the user): a left click on the kind picture only takes it, for the wheel; the list is on the right button
                else if (part == Part.Kind) { if (e.Button == MouseButtons.Left) { hot = band; hotPart = Part.Kind; Invalidate(); } }
                else if (e.Button == MouseButtons.Left)
                {
                    dragPart = part; dragBand = band; dragY = e.Y; dragFrom = part == Part.Freq ? P.Bands[band].Freq : part == Part.Slope ? P.Bands[band].Slope : P.Bands[band].Q; hot = band;
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
                if (b.HasGain) { double gdb = Math.Round((DbAt(e.Y) - P.Preamp) * 10) / 10; if (Math.Abs(gdb) < 0.35) gdb = 0; b.Gain = (float)Math.Max(-GainMax, Math.Min(GainMax, gdb)); }
                Fire();
            }
            int band = -1; var part = drag >= 0 || Dim ? Part.None : HitPanel(e.Location, out band);
            int ht = drag >= 0 ? drag : Dim ? -1 : part != Part.None ? band : Hit(e.Location);
            Cursor = part == Part.Kind ? Cursors.Hand : part != Part.None ? Cursors.SizeNS : ht >= 0 ? Cursors.SizeAll : Cursors.Default;
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
            if (!master && (!armed || hot < 0 || hot >= P.Bands.Count || Dim)) { base.OnMouseWheel(e); return; }
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            // 20.11: one notch = one step, also from a smooth wheel that sends a notch in parts (Theme.WheelNotches)
            int n = Theme.WheelNotches(ref wheelAcc, e.Delta);
            for (int i = 0; i < Math.Abs(n); i++)
            {
                bool up = n > 0;
                if (master) { P.Preamp = (float)Math.Max(-MasterDb, Math.Min(MasterDb, Math.Round((P.Preamp + (up ? 0.5 : -0.5)) * 2) / 2)); continue; }
                var b = P.Bands[hot];
                // 21.11 (the user): over the kind picture the wheel goes through the kinds, in the order of the list
                if (hotPart == Part.Kind) { SetKind(b, Math.Max(1, Math.Min(Kinds.Length - 1, b.Type + (up ? -1 : 1)))); continue; }
                if (hotPart == Part.Fader) { if (b.HasGain) b.Gain = (float)Math.Max(-GainMax, Math.Min(GainMax, Math.Round((b.Gain + (up ? 0.5 : -0.5)) * 2) / 2)); }
                else if (hotPart == Part.Slope) b.Slope = Math.Max(1, Math.Min(EqBand.MaxSlope, b.Slope + (up ? 1 : -1)));
                else if (hotPart == Part.Freq) b.Freq = (float)Math.Max(MinF, Math.Min(MaxF, RoundHz(b.Freq * (up ? 1.06 : 1 / 1.06))));
                else b.Q = (float)Math.Max(0.2, Math.Min(12, Math.Round(b.Q * (up ? 1.15 : 1 / 1.15) * 100) / 100));
            }
            if (n != 0) Fire();
        }
        int wheelAcc;

        // a press of the wheel: «Общ.» back to no gain, a band's fader to zero, its width, frequency and slope dials to where
        // they are at rest (SoundParams.EvenBands; 20.11, the user's wish:
        // it was a double click, which went off by itself when a fader was clicked twice quickly)
        void ResetAt(Point at)
        {
            int band = -1; var part = Dim ? Part.None : HitPanel(at, out band);
            if (part == Part.Master) { P.Preamp = 0; Fire(); }
            else if (band >= 0 && band < P.Bands.Count)
            {
                if (part == Part.Fader && P.Bands[band].HasGain) { P.Bands[band].Gain = 0; Fire(); }
                else if (part == Part.Slope) { P.Bands[band].Slope = 1; Fire(); }
                else if (part == Part.Kind) { SetKind(P.Bands[band], 1); Fire(); }   // 21.11: the kind back to a bell
                else if (part == Part.Wide) { P.Bands[band].Q = SoundParams.EvenBands()[band].Q; Fire(); }
                // 21.11 (the user): the frequency dial back to where this strip stands at rest (the even spread)
                else if (part == Part.Freq && band < SoundParams.MaxBands) { P.Bands[band].Freq = SoundParams.EvenBands()[band].Freq; Fire(); }
            }
        }

        // another kind of filter for a band (the list, the wheel over its picture, a press of the wheel)
        static void SetKind(EqBand b, int type)
        {
            b.Type = type; if (!b.HasGain) b.Gain = 0; if (type >= 4 && b.Q > 3 && type != 6) b.Q = 0.7f;
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
                    it.Click += (s, e) => { SetKind(b, type); Fire(); };
                    m.Items.Add(it);
                }
                m.Items.Add(new ToolStripSeparator());
                // 21.11: the slope, for every kind
                for (int s = 1; s <= EqBand.MaxSlope; s++)
                {
                    int sl = s;
                    var it = new ToolStripMenuItem(L.T("Крутизна ") + SlopeItem(b, s)) { Checked = b.Slope == s };
                    it.Click += (o, e) => { b.Slope = sl; Fire(); };
                    m.Items.Add(it);
                }
                m.Items.Add(new ToolStripSeparator());
                var zero = new ToolStripMenuItem("Усиление в ноль"); zero.Click += (s, e) => { b.Gain = 0; Fire(); }; zero.Enabled = b.HasGain; m.Items.Add(zero);
                // 21.11: the twelve points stay; «Сбросить точку» makes it a bell of no gain where it stands
                var reset = new ToolStripMenuItem("Сбросить точку"); reset.Click += (s, e) => { b.Type = 1; b.Gain = 0; b.Q = 1f; b.Slope = 1; Fire(); }; m.Items.Add(reset);
            }
            else
            {
                var flat = new ToolStripMenuItem("Все усиления в ноль"); flat.Click += (s, e) => { foreach (var b in P.Bands) b.Gain = 0; Fire(); }; m.Items.Add(flat);
                // 21.11: the twelve points at rest, spread evenly along the line
                var even = new ToolStripMenuItem("Сбросить все точки"); even.Click += (s, e) => { P.Bands = SoundParams.EvenBands(); hot = -1; Fire(); }; m.Items.Add(even);
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
            // the plot drawn whole every moment (no pictures kept: 20.11 the user tried the still parts kept as pictures
            // and the point felt late under the mouse). 21.11: only the plot, not the strips and the scale's words, which
            // a moment of the sound does not change; whatever the mouse changes still repaints the whole control at once
            InvalidateLive();
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
            // 21.11: a paint of the plot alone (a moment of the sound) skips the words around it and the strips
            var want = e.ClipRectangle;
            bool dbWords = want.X < r.X - Theme.S(4), hzWords = want.Bottom > r.Bottom + Theme.S(2);

            using (var pen = new Pen(Theme.Border))
            using (var zeroPen = new Pen(Theme.BorderStrong))
            {
                for (int db = -(int)Range; db <= Range; db += Range > 24 ? 12 : 6)
                {
                    float y = Y(db);
                    g.DrawLine(db == 0 ? zeroPen : pen, r.X, y, r.Right, y);
                    if (dbWords) Theme.Text1(g, (db > 0 ? "+" : db < 0 ? "−" : "") + Math.Abs(db), Theme.Small, new Rectangle(0, (int)y - Theme.S(10), r.X - Theme.S(8), Theme.S(20)), Theme.Muted, TextFormatFlags.Right);
                }
                foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    float x = X(f);
                    g.DrawLine(pen, x, r.Y, x, r.Bottom);
                    if (hzWords) Theme.Text1(g, f >= 1000 ? f / 1000 + "k" : f.ToString(), Theme.Small, new Rectangle((int)x - Theme.S(30), r.Bottom + Theme.S(4), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }

            // what plays now (-84 dB at the bottom of the plot, 0 dB at its top), then the curve and its points
            var clip = g.Clip; g.SetClip(r);
            SpectrumFill(new GdiLive(g), r);
            if (DsOn) DeEssBand(g, r);
            g.Clip = clip;
            CurveAndKnobs(g, r, line);
            if (want.Right > Panel.X) DrawStrips(g, line);

            if (!hzWords) return;
            string hint;
            if (!Dim && (hotPart == Part.Master || dragPart == Part.Master))
                hint = "Общее усиление" + ": " + Gain(P.Preamp) + " · " + L.T("До эквалайзера: убавьте, если полосы подняты высоко и лимитер срабатывает постоянно") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (hot >= 0 && hot < P.Bands.Count)
            {
                var b = P.Bands[hot];
                hint = Kinds[Math.Max(1, Math.Min(6, b.Type))] + " · " + Hz(b.Freq) + (b.HasGain ? " · " + Gain(b.Gain) : "") + " · " + SlopeText(b) + " · " + "ширина Q " + b.Q.ToString("0.##", CultureInfo.InvariantCulture) + " · " + "колесо меняет ширину, правая кнопка — вид фильтра";
            }
            else if (Dim) hint = "Эквалайзер выключен";
            else hint = "Тяните точки: влево-вправо частота, вверх-вниз усиление. Правая кнопка на точке — вид фильтра или сброс";
            Theme.Text1(g, hint, Theme.Small, new Rectangle(r.X, r.Bottom + Theme.S(28), Width - r.X - Theme.S(14), Theme.S(22)), Theme.Text2);
        }

        // the de-esser's band: a light column with dashed edges and its name; while it works, how deep it takes the band
        // down, from the 0 dB line
        void DeEssBand(Graphics g, Rectangle r)
        {
            float a = X(Math.Max(MinF, DsLo)), b = X(Math.Min(MaxF, DsHi));
            if (b - a < 2) return;
            using (var fill = new SolidBrush(Color.FromArgb(Dim ? 10 : 22, Theme.Text))) g.FillRectangle(fill, a, r.Y, b - a, r.Height);
            if (DsDb < -0.5f && !Dim)
            {
                float y0 = Y(0), y1 = Y(Math.Max(-Range, DsDb));
                using (var cut = new SolidBrush(Color.FromArgb(70, Theme.Accent))) g.FillRectangle(cut, a, y0, b - a, Math.Max(1, y1 - y0));
            }
            using (var pen = new Pen(Color.FromArgb(Dim ? 40 : 90, Theme.Text), Theme.S(1)) { DashStyle = DashStyle.Dash })
            { g.DrawLine(pen, a, r.Y, a, r.Bottom); g.DrawLine(pen, b, r.Y, b, r.Bottom); }
            Theme.Text1(g, L.T("де-эссер") + (DsDb < -0.5f ? " " + Math.Round(DsDb).ToString(CultureInfo.InvariantCulture) + " " + L.T("дБ") : ""), Theme.Small,
                new Rectangle((int)a + Theme.S(4), r.Y + Theme.S(2), Math.Max(Theme.S(40), (int)(b - a) + Theme.S(60)), Theme.S(18)), Dim ? Theme.Muted : Theme.Text2);
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
            // the slopes' row: a small steep line for a label
            using (var pen = new Pen(Theme.Muted, Theme.S(1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float lx = p.X + Theme.S(MasterW) + Theme.S(Gutter) - Theme.S(6), ly = SlopeRowY;
                g.DrawLines(pen, new[] { new PointF(lx - Theme.S(11), ly + Theme.S(5)), new PointF(lx - Theme.S(6), ly + Theme.S(5)), new PointF(lx - Theme.S(2), ly - Theme.S(5)), new PointF(lx + Theme.S(1), ly - Theme.S(5)) });
            }
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
                    Dial(g, cx, SlopeRowY, (b.Slope - 1f) / (EqBand.MaxSlope - 1f), Dim ? Theme.Muted : h ? Theme.AccentHover : line, h && (hotPart == Part.Slope || dragPart == Part.Slope), Dim);
                }
        }
    }
}
