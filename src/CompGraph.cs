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
    // In and out levels and how much the compressor and the limiter take off, live
    class LevelMeter : Painted
    {
        bool alive; float inDb = -90, outDb = -90, gr, lim;

        public LevelMeter() { BackColor = Theme.Window; Height = Theme.S(84); }

        static float Db(float lin) { return lin <= 0.00001f ? -100f : 20f * (float)Math.Log10(lin); }

        public void Set(bool on, float inPeak, float outPeak, float grDb, float limDb)
        {
            float i = Db(inPeak), o = Db(outPeak);
            if (on == alive && Math.Abs(i - inDb) < 0.2f && Math.Abs(o - outDb) < 0.2f && Math.Abs(grDb - gr) < 0.1f && Math.Abs(limDb - lim) < 0.1f) return;
            alive = on; inDb = i; outDb = o; gr = grDb; lim = limDb;
            LiveInvalidate(ClientRectangle); // 21.11: not while scrolled out of sight
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, r, Theme.S(8), Theme.Card);
            Theme.Stroke(g, r, Theme.S(8), Theme.Border);
            int x = Theme.S(16), label = Theme.S(96), val = Theme.S(84), rowH = Theme.S(22), y = Theme.S(9);
            int bx = x + label, bw = Width - bx - val - Theme.S(16);
            Bar(g, "На входе", y, bx, bw, rowH, (inDb + 60) / 60f, alive ? Num(inDb) : "—", false);
            Bar(g, "На выходе", y + rowH, bx, bw, rowH, (outDb + 60) / 60f, alive ? Num(outDb) : "—", false);
            float take = -(gr + lim);
            Bar(g, "Сжатие", y + 2 * rowH, bx, bw, rowH, take / 24f, alive && take > 0.05f ? "−" + take.ToString("0.0", CultureInfo.InvariantCulture) + " дБ" : alive ? "0 дБ" : "—", true);
        }

        static string Num(float db) { return db < -90 ? "тишина" : (db < 0 ? "−" : "") + Math.Abs(db).ToString("0.0", CultureInfo.InvariantCulture) + " дБ"; }

        void Bar(Graphics g, string name, int y, int bx, int bw, int h, float f, string value, bool warn)
        {
            f = Math.Max(0, Math.Min(1, f));
            Theme.Text1(g, name, Theme.Small, new Rectangle(Theme.S(16), y, bx - Theme.S(16), h), Theme.Text2);
            float th = Theme.S(5), cy = y + h / 2f;
            Theme.Tube(g, new RectangleF(bx, cy - th / 2f, bw, th));
            if (f > 0.002f) Theme.FillGradient(g, new RectangleF(bx, cy - th / 2f, bw * f, th), th / 2f, warn, bw);
            Theme.Text1(g, value, Theme.Small, new Rectangle(bx + bw + Theme.S(8), y, Width - bx - bw - Theme.S(24), h), alive ? Theme.Text : Theme.Muted, TextFormatFlags.Right);
        }
    }

    // The compressor drawn (19.11, the user's wish: a picture with the settings in it, as the equalizer has).
    // Left: what comes out for what goes in, both in dB — the curve the threshold, the ratio, the knee and the gain after
    // make, with three points on it: the threshold (dragged left and right), the ratio (the right end, up and down) and
    // the gain after (the left part, up and down). The dot is the sound right now.
    // Beside it: dials for the attack, the release and the knee, one under another (they have no rows on the page).
    // Then, to the right edge: the last ten seconds on the same dB scale — the level coming in, the level going out, and
    // from the top down how much the compressor takes off; the dashed line is the threshold and can be dragged too.
    // The points write into the same settings as the sliders under the picture; the page keeps the two in step.
    // The limiter, when it is on, is in the same picture (19.11, the user's wish): its ceiling is a line across the curve
    // (the curve goes flat under it) and across the last seconds, where what the limiter takes off hangs under what the
    // compressor takes off, in its own colour. The line can be dragged, also while the compressor is off.
    class CompGraph : Painted
    {
        public SoundParams P = SoundParams.Start();
        public bool Dim;
        // 21.11 (the user: the second compressor «exactly the same, with the pictures», in bands too): everything of the
        // main one but the limiter's ceiling and the listening to parts apart (S/M). The page gives it a SoundParams
        // whose parts and splits are the second compressor's (SoundPage.c2View).
        public bool Second;
        // 19.11: the compressor in bands. Band: the one of SoundParams.Parts the points, the dials and the last seconds
        // show (the page's choice); the curves of the other bands at work are drawn thin behind it, each in its colour.
        public int Band;
        CompPart Q { get { return P.Parts[Math.Max(0, Math.Min(2, Band))]; } }
        internal static readonly Color[] PartColor = { Theme.Accent, Color.FromArgb(0x6f, 0xc2, 0x95), Color.FromArgb(0x6a, 0xa9, 0xf2) }; // the bass, the middle, the top
        static Color Lighter(Color c) { return Color.FromArgb(c.R + (255 - c.R) / 4, c.G + (255 - c.G) / 4, c.B + (255 - c.B) / 4); }
        public event EventHandler Changed;
        const double Lo = -60, Hi = 0;
        // the dials' column: as wide as the longest name («Восстановление»), with the same gap to the curve on its left and to
        // the last seconds on its right; 300 samples = ten seconds at the page's 30 per second
        const int PanelUnits = 124, BandUnits = 120, Gap = 26, Hist = 300;
        // 19.11: a text in the middle of a small button or over a dial is drawn with no side room of Windows' own
        // (Mid): that room is wider on the right, and the text stood about two pixels left of the middle
        const TextFormatFlags Mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
        // A single digit or letter in a small button (1 2 3, S, M) stands in the middle by its ink, not by the box the
        // font keeps for it: the box has uneven room at the sides and more room above a digit than under it, so at
        // 150 % the digits sat a pixel and a half low (measured on the rendered picture). The shift from the box to the ink is found once per glyph, font and scale by drawing the
        // glyph the very way it is drawn here into a small picture and looking where its pixels are.
        static readonly Dictionary<string, Point> glyphOff = new Dictionary<string, Point>();
        static void Glyph(Graphics g, string text, Font f, Rectangle r, Color c)
        {
            string key = text + "|" + f.Name + "|" + f.SizeInPoints + "|" + (int)f.Style + "|" + g.DpiY;
            Point off;
            if (!glyphOff.TryGetValue(key, out off))
            {
                off = Point.Empty;
                try
                {
                    var box = TextRenderer.MeasureText(g, text, f, new Size(1000, 100), TextFormatFlags.NoPadding);
                    int pad = 6, bw = box.Width + 2 * pad, bh = box.Height + 2 * pad;
                    using (var bmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        bmp.SetResolution(g.DpiX, g.DpiY);
                        using (var bg = Graphics.FromImage(bmp))
                        {
                            bg.Clear(Color.Black);
                            TextRenderer.DrawText(bg, text, f, new Rectangle(pad, pad, box.Width, box.Height), Color.White, Color.Black, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                        }
                        int x0 = bw, x1 = -1, y0 = bh, y1 = -1;
                        for (int y = 0; y < bh; y++)
                            for (int x = 0; x < bw; x++)
                            {
                                var px = bmp.GetPixel(x, y);
                                if (px.R + px.G + px.B > 300) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                            }
                        if (x1 >= 0) off = new Point((int)Math.Round(pad + box.Width / 2f - (x0 + x1 + 1) / 2f), (int)Math.Round(pad + box.Height / 2f - (y0 + y1 + 1) / 2f));
                    }
                }
                catch { }
                glyphOff[key] = off;
            }
            r.Offset(off);
            TextRenderer.DrawText(g, text, f, r, c, Mid | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        internal enum Part { None, Thr, Ratio, Makeup, Line, Attack, Release, Knee, Ceil, Split1, Split2, Zone, ViewTime, ViewFreq, Count, Pill, DialThr, DialRatio, DialMakeup, Solo, Mute, DialSplit1, DialSplit2, CeilFader }
        Part hot, drag; bool armed, demo; int dragY; double dragFrom; float grabDx, grabDy;

        // 19.11 (the user's wish: the bands seen, not only named). The place of the last seconds holds one of two monitors,
        // switched by the two words under it at the right. ByFreq: the bands across the frequencies, as the equalizer has
        // its axis — each band at work a column in its colour with its name on top, in it the level the band reads now
        // (from the bottom), what is taken off it (from the top) and its threshold (the dashed line, dragged up and down);
        // between the columns the splits (dragged left and right). A click on a column makes that band the one the points,
        // the dials and the rows show. A split that is not at work stays as a faint dashed line where the page keeps it,
        // so the picture always shows where the bands would part.
        public bool ByFreq;
        public float Ghost1 = 250, Ghost2 = 4000;
        public event EventHandler BandPicked, ViewPicked;
        const double MinF = 20, MaxF = 20000;
        int zoneHot = -1; // the part of the settings whose column is under the mouse
        readonly float[] bLv = { -140, -140, -140 }, bGr = new float[3]; // per band, live: the level it reads and what is taken off it

        // 19.11 (the user's wish): listening to the bands. Under the name of each column of the monitor by frequencies two
        // small buttons: S (solo: only the bands with S are heard) and M (mute: this band is not heard). SoloMask and
        // MuteMask hold a bit per part of the settings; Silent is what the effect gets — the parts that do not go out.
        // It is for listening while setting up, not a setting: the page takes it off when it is left.
        public int SoloMask, MuteMask;
        public event EventHandler ListenChanged;
        internal static readonly Color SoloC = Color.FromArgb(0xf2, 0xc2, 0x4e), MuteC = Color.FromArgb(0xe8, 0x6a, 0x3a);
        public int Silent
        {
            get
            {
                if (!P.Comp || P.PartCount < 2) return 0;
                int act = 0; foreach (int i in P.ActiveParts) act |= 1 << i;
                int solo = SoloMask & act;
                return (MuteMask | (solo != 0 ? ~solo : 0)) & act;
            }
        }
        public void ClearListen() { if (SoloMask == 0 && MuteMask == 0) return; SoloMask = MuteMask = 0; Invalidate(); }
        void Listen(Part part, int band)
        {
            if (band < 0 || band > 2) return;
            if (part == Part.Solo) SoloMask ^= 1 << band; else MuteMask ^= 1 << band;
            hot = part; Invalidate();
            var h = ListenChanged; if (h != null) h(this, EventArgs.Empty);
        }
        // the S (mute = false) or the M button of a band's column; empty where there is none (one band, another monitor,
        // a column too narrow for the two)
        internal Rectangle ListenRect(int part, bool mute)
        {
            if (Second) return Rectangle.Empty;   // (the second compressor: no listening apart)
            var hh = History; if (!ByFreq || hh.Width <= 0 || P.PartCount < 2) return Rectangle.Empty;
            foreach (var z in Zones())
            {
                if (z.Part != part) continue;
                float x0 = FX(z.From), x1 = FX(z.To); int w = Theme.S(24), h = Theme.S(20), gap = Theme.S(4);
                if (x1 - x0 < 2 * w + gap + Theme.S(8)) return Rectangle.Empty;
                int cx = (int)((x0 + x1) / 2), y = hh.Y + Theme.S(31);
                return mute ? new Rectangle(cx + gap / 2, y, w, h) : new Rectangle(cx - gap / 2 - w, y, w, h);
            }
            return Rectangle.Empty;
        }

        internal struct Zone { public int Part; public double From, To; }
        internal Zone[] Zones()
        {
            int n = P.PartCount;
            if (n == 1) return new[] { new Zone { Part = 0, From = MinF, To = MaxF } };
            if (n == 2) return new[] { new Zone { Part = 0, From = MinF, To = P.Split }, new Zone { Part = 2, From = P.Split, To = MaxF } };
            return new[] { new Zone { Part = 0, From = MinF, To = P.Split }, new Zone { Part = 1, From = P.Split, To = P.Split2 }, new Zone { Part = 2, From = P.Split2, To = MaxF } };
        }
        internal static readonly string[] ZoneNames = { "Низ", "Середина", "Верх" };
        internal float FX(double hz) { var h = History; return (float)(h.X + h.Width * Math.Log(Clamp(hz, MinF, MaxF) / MinF) / Math.Log(MaxF / MinF)); }
        double HzAtX(float x) { var h = History; return MinF * Math.Pow(MaxF / MinF, Clamp((x - h.X) / (double)Math.Max(1, h.Width), 0, 1)); }
        // the grip of a split (0 the lower, 1 the upper): on its line, in the lower part of the plot, clear of the names on top
        internal PointF SplitAt(int k) { var h = History; return new PointF(FX(k == 0 ? P.Split : P.Split2), h.Y + h.Height * 0.76f); }
        int ZoneAt(float x) { double f = HzAtX(x); foreach (var z in Zones()) if (f < z.To) return z.Part; return 2; }

        // the two words that switch the monitor, at the right end of the line under it
        string viewKey; int viewW0, viewW1;
        internal Rectangle ViewRect(bool freq)
        {
            var h = History; if (h.Width <= 0) return Rectangle.Empty;
            string a = L.T("Во времени"), b = L.T("По частотам");
            if (viewKey != a + "|" + b) { viewKey = a + "|" + b; viewW0 = TextRenderer.MeasureText(a, Theme.Small).Width + Theme.S(12); viewW1 = TextRenderer.MeasureText(b, Theme.Small).Width + Theme.S(12); }
            int y = Curve.Bottom + Theme.S(3), hh = Theme.S(20);
            return freq ? new Rectangle(h.Right - viewW1, y, viewW1, hh) : new Rectangle(h.Right - viewW1 - Theme.S(4) - viewW0, y, viewW0, hh);
        }
        public void SetView(bool freq) { if (ByFreq == freq) return; ByFreq = freq; hot = Part.None; Invalidate(); }
        readonly float[] hIn = new float[Hist], hOut = new float[Hist], hLim = new float[Hist];
        // 20.11 (the user): what the compressor took off, kept per part of the settings (the bass or the whole sound, the
        // middle, the top), so the band picked shows its own last ten seconds at once; there was one line for whichever
        // band was shown, and a band picked only took over its colour
        readonly float[][] hGrB = { new float[Hist], new float[Hist], new float[Hist] };
        int hCount; bool live; float nowIn = -100, nowGr, nowLim, nowLv = -140; // nowLv: the level the shown band reads, for the dot on the curve

        public CompGraph() { BackColor = Theme.Window; Height = Theme.S(400); anim.Tick += (o, e) => AnimTick(); Disposed += (o, e) => anim.Dispose(); } // 300 -> 350: three dials with a name above and a value under each need the height

        // 19.11 (the user's order: first how many bands and which one, then its settings): the bands' column stands first,
        // the curve after it, with the room for the numbers of its scale between them
        int Side { get { return Math.Max(Theme.S(60), Height - Theme.S(72)); } }
        const int BandsLeft = 24, CurveAfterBands = 54; // as far from the card's edge as from the numbers of the curve's scale
        internal Rectangle Curve { get { int side = Side; return new Rectangle(HasBands ? Theme.S(BandsLeft + BandUnits + CurveAfterBands) : Theme.S(46), Theme.S(14), side, side); } }
        // 19.11 (the user's order): the curve, then the dials one under another, then the last seconds to the right edge.
        // In a narrow window the last seconds go first, then the dials.
        // 19.11 (the user's wish): six dials — the threshold, the ratio and the gain after came from their rows under the
        // picture and stand in a column of their own, left of the attack, the release and the knee. Where the two columns
        // do not fit there is the one of before (and the page shows the three rows again); narrower still, none.
        const int DialUnits = 104, DialGap = 8;
        int DialCols
        {
            get
            {
                int free = Width - Curve.Right - Theme.S(Gap) - Theme.S(12);
                return free >= Theme.S(2 * DialUnits + DialGap) ? 2 : free >= Theme.S(PanelUnits) ? 1 : 0;
            }
        }
        bool HasPanel { get { return DialCols > 0; } }
        public bool HasSixDials { get { return DialCols == 2; } }
        internal Rectangle Panel { get { var c = Curve; int n = DialCols; return n > 0 ? new Rectangle(c.Right + Theme.S(Gap), c.Y, n == 2 ? Theme.S(2 * DialUnits + DialGap) : Theme.S(PanelUnits), c.Height) : Rectangle.Empty; } }
        // the cell of a dial: 0 the attack, 1 the release, 2 the knee (the right column, or the only one), 3 the threshold,
        // 4 the ratio, 5 the gain after (the left column, only with two)
        // 19.11, later (the user's wish): under the six, two more — the splits between the bands, 6 the lower (the left
        // column) and 7 the upper (the right one). With two columns the dials stand in four rows (the picture is 400
        // tall for that, it was 350). A split that is not at work has its dial grey and still: with one band both,
        // with two the upper one.
        internal Rectangle DialCell(int k)
        {
            var pn = Panel;
            if (DialCols < 2) { int h3 = pn.Height / 3; return k < 3 ? new Rectangle(pn.X, pn.Y + h3 * k, pn.Width, h3) : Rectangle.Empty; }
            int rowH = pn.Height / 4, row = k >= 6 ? 3 : k % 3, w = Theme.S(DialUnits);
            bool rightCol = k < 3 || k == 7;
            return new Rectangle(rightCol ? pn.Right - w : pn.X, pn.Y + rowH * row, w, rowH);
        }
        int DialCount { get { return DialCols == 2 ? 8 : 3; } }
        // 21.11 (the user): a dial the character does not listen to is grey and not taken: the optical one lets go in its own
        // two stages (no release), the tube one has its own wide knee
        bool DialOn(int k) { return k == 6 ? P.PartCount >= 2 : k == 7 ? P.PartCount >= 3 : k == 1 ? P.CompChar != 2 : k == 2 ? P.CompChar != 3 : true; }
        internal Rectangle History
        {
            get
            {
                var c = Curve; if (!HasPanel) return Rectangle.Empty;
                int x = Panel.Right + Theme.S(Gap), w = Width - Theme.S(18) - Theme.S(CeilUnits) - x; // the ceiling's fader at the right end
                return w >= Theme.S(150) ? new Rectangle(x, c.Y, w, c.Height) : Rectangle.Empty;
            }
        }
        // 19.11 (the user's order: the bands, the curve, the dials, the monitor). The bands: how many (1, 2, 3) and under
        // that the one being set — a plate per band with its colour; a band that is not at work keeps its place, grey,
        // and lights up when the number takes it in. They were two rows under the picture; in a window too narrow for
        // the column beside the curve and the dials the page shows those rows instead (HasBands).
        // 20.11 (the user's wish): the limiter's ceiling as a fader right of the monitor, 0 dB on top, −12 at the bottom, by
        // half a dB; a flat line with an arrow up to it on top, the value under it. Only where the monitor has its room.
        const int CeilUnits = 44, CeilStripW = 30;
        // with no room for the monitor it still stands at the right end when it fits there itself (the line over the curve
        // is then the other way to the ceiling)
        internal Rectangle CeilStrip
        {
            get
            {
                var c = Curve; if (!HasPanel || Second) return Rectangle.Empty;
                int x = Width - Theme.S(18) - Theme.S(CeilStripW);
                return History.Width > 0 || x - Panel.Right >= Theme.S(CeilUnits - CeilStripW) ? new Rectangle(x, c.Y, Theme.S(CeilStripW), c.Height) : Rectangle.Empty;
            }
        }
        internal int CeilTop { get { return CeilStrip.Y + Theme.S(26); } }
        internal int CeilBottom { get { return CeilStrip.Bottom - Theme.S(6); } }
        internal float CeilY(double db) { return (float)(CeilTop + (CeilBottom - CeilTop) * Clamp(-db / 12.0, 0, 1)); }
        public bool HasMonitor { get { return History.Width > 0; } } // room for the monitor, where the splits are dragged
        public bool HasBands { get { return Width >= Theme.S(BandsLeft + BandUnits + CurveAfterBands) + Side + Theme.S(Gap) + Theme.S(PanelUnits) + Theme.S(12); } }
        internal Rectangle BandsCol { get { return HasBands ? new Rectangle(Theme.S(BandsLeft), Theme.S(14), Theme.S(BandUnits), Side) : Rectangle.Empty; } }
        // 19.11 (the user: it stood high, put it in order). One block in the middle of the plot's height: a title, 6 to its
        // numbers, 22 to the next title, 6 to its plates, the plates 8 apart.
        // 19.11, later (the user: a grey «Середина» reads as something switched off): only the bands at work have a plate,
        // so the block is as tall as their number and stays in the middle. A plate comes and goes with a short movement:
        // plate[i] runs 0..1 (how much of plate i is there — its height and its ink), the plates under it and the whole
        // block move with it. What is under the mouse is always judged by where things will stand (live = false).
        const int BandsTitle2 = 78;
        readonly float[] plate = { 1, 0, 0 }; SoundParams plateP;
        readonly Timer anim = new Timer { Interval = 15 };
        float PlateOf(int part, bool live) { return live ? plate[part] : P.ActiveParts.Contains(part) ? 1 : 0; }
        // Where the block stands. With another number of plates its middle place is another, and the numbers would move
        // from under the mouse that has just clicked one (20 units a plate: the next click would miss). So the block
        // stays where it is while the mouse is over the column and glides to the middle when the mouse has left.
        // What is drawn and what is clicked use the same place.
        float topNow = float.NaN; bool inCol;
        float TopWanted { get { var b = BandsCol; return b.Y + (b.Height - Theme.S(96) - Theme.S(40) * P.PartCount) / 2f; } }
        float BandsTopF { get { return float.IsNaN(topNow) ? TopWanted : topNow; } }
        void SetInCol(bool v) { if (inCol == v) return; inCol = v; Invalidate(); }
        internal void Settle() { plateP = null; inCol = false; SyncPlates(); Invalidate(); } // everything in its place at once (the check)
        internal Rectangle CountRect(int i) { var b = BandsCol; int gap = Theme.S(4), w = (b.Width - 2 * gap) / 3; return new Rectangle(b.X + i * (w + gap), (int)Math.Round(BandsTopF) + Theme.S(26), w, Theme.S(30)); }
        RectangleF PlateRect(int part, bool live)
        {
            var b = BandsCol; float y = BandsTopF + Theme.S(BandsTitle2 + 26);
            for (int j = 0; j < part; j++) y += Theme.S(40) * PlateOf(j, live);
            return new RectangleF(b.X, y, b.Width, Theme.S(32) * PlateOf(part, live));
        }
        internal Rectangle BandRect(int part) { return Rectangle.Round(PlateRect(part, false)); }
        internal float PlateShown(int part) { return plate[part]; } // for the check: how much of a plate is drawn now

        // the plates follow the bands at work: at once for other settings (a device, a preset) and in the off-screen
        // render, with the movement when the number of bands changes in the settings shown
        void SyncPlates()
        {
            var act = P.ActiveParts; bool snap = demo || !ReferenceEquals(P, plateP) || !IsHandleCreated, moving = false; plateP = P;
            for (int i = 0; i < 3; i++) { float t = act.Contains(i) ? 1 : 0; if (snap) plate[i] = t; else if (plate[i] != t) moving = true; }
            if (snap || float.IsNaN(topNow)) topNow = TopWanted; else if (!inCol && Math.Abs(topNow - TopWanted) > 0.01f) moving = true;
            if (moving && !anim.Enabled) anim.Start();
        }
        void AnimTick()
        {
            var act = P.ActiveParts; bool moving = false;
            for (int i = 0; i < 3; i++)
            {
                float t = act.Contains(i) ? 1 : 0;
                plate[i] += (t - plate[i]) * 0.22f; // quick at first, slowing at the end; about a quarter of a second
                if (Math.Abs(t - plate[i]) < 0.012f) plate[i] = t; else moving = true;
            }
            float want = TopWanted; if (float.IsNaN(topNow)) topNow = want;
            if (!inCol && topNow != want) { topNow += (want - topNow) * 0.22f; if (Math.Abs(want - topNow) < 0.4f) topNow = want; else moving = true; }
            if (!moving) anim.Stop();
            Invalidate();
        }
        int idxHot = -1; // which of the three numbers or plates is under the mouse

        // the number of bands from the picture: the splits go on where the page keeps them
        void SetCount(int n)
        {
            if (n == P.PartCount) return;
            P.Split = n >= 2 ? Ghost1 : 0; P.Split2 = n >= 3 ? Ghost2 : 0;
            SoloMask = MuteMask = 0; // other bands: nothing of them is listened to apart yet
            var act = P.ActiveParts; if (!act.Contains(Band)) Band = act[0];
            Fire();
        }
        // the middle of a dial: 0 the attack, 1 the release, 2 the knee
        internal Point DialAt(int k) { var r = DialCell(k); return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }
        static readonly Part[] Dials = { Part.Attack, Part.Release, Part.Knee, Part.DialThr, Part.DialRatio, Part.DialMakeup, Part.DialSplit1, Part.DialSplit2 };

        static double Clamp(double v, double a, double b) { return v < a ? a : v > b ? b : v; }
        internal float X(double db) { var c = Curve; return (float)(c.X + c.Width * (Clamp(db, Lo, Hi) - Lo) / (Hi - Lo)); }
        internal float Y(double db) { var c = Curve; return (float)(c.Bottom - c.Height * (Clamp(db, Lo, Hi) - Lo) / (Hi - Lo)); }
        double DbAtX(float x) { var c = Curve; return Lo + (x - c.X) / (double)c.Width * (Hi - Lo); }
        double DbAtY(float y) { var c = Curve; return Lo + (c.Bottom - y) / (double)c.Height * (Hi - Lo); }

        // what the compressor takes off at a level, dB (<= 0): the same formula as the effect's (Want in rtdsp.h)
        internal double Gr(double x) { return GrOf(Q, x); }
        internal float GrAgo(int part, int k) { return hGrB[part][Hist - 1 - k]; } // the check: what part took off k moments ago
        // 21.11: as the character draws it (rtdsp.h WantC): FET a hard knee, optical a soft one, vari-mu a slope that eases in
        double GrOf(CompPart q, double x) { return GrOf(q, x, P != null ? P.CompChar : 0); }
        static double GrOf(CompPart q, double x, int chr)
        {
            double over = x - q.Thr, slope = 1.0 / Math.Max(1, q.Ratio) - 1, knee = q.Knee;
            if (chr == 1) knee = Math.Min(knee, 1.5); else if (chr == 2) knee = Math.Max(knee, 10);
            else if (chr == 3)
            {
                double o = over + 6; if (o <= 0) return 0;
                double t = o / 24, S = t < 1 ? t * t * t - t * t * t * t / 2 : 0.5 + (t - 1);
                return slope * 24 * S;
            }
            if (2 * over < -knee) return 0;
            if (knee > 0 && 2 * over <= knee) { double t = over + knee * 0.5; return slope * t * t / (2 * knee); }
            return slope * over;
        }
        double Out(double x) { return x + Gr(x) + Q.Makeup; }
        // where the point of the gain after stands: on the part the compressor does not touch, halfway to the threshold
        double MakeupX { get { return Math.Max(Lo + 6, (Lo + Q.Thr) / 2); } }

        internal PointF KnobAt(Part p)
        {
            double x = p == Part.Thr ? Q.Thr : p == Part.Ratio ? Hi : MakeupX;
            return new PointF(X(x), Y(Out(x)));
        }

        Part Hit(Point m)
        {
            Part best = Part.None; float bd = Theme.S(15) * Theme.S(15); zoneHot = -1; idxHot = -1;
            if (!Dim) foreach (var p in new[] { Part.Thr, Part.Ratio, Part.Makeup }) { var k = KnobAt(p); float d = (k.X - m.X) * (k.X - m.X) + (k.Y - m.Y) * (k.Y - m.Y); if (d < bd) { bd = d; best = p; } }
            if (best != Part.None) return best;
            // the two words under the monitor
            if (ViewRect(false).Contains(m)) return Part.ViewTime;
            if (ViewRect(true).Contains(m)) return Part.ViewFreq;
            { var cs = CeilStrip; if (cs.Width > 0 && Rectangle.Inflate(cs, 0, Theme.S(4)).Contains(m)) return P.Limit ? Part.CeilFader : Part.None; }
            // the bands' column: a number, or the plate of a band at work
            if (!Dim && BandsCol.Contains(m))
            {
                for (int i = 0; i < 3; i++) if (CountRect(i).Contains(m)) { idxHot = i; return Part.Count; }
                for (int i = 0; i < 3; i++) if (BandRect(i).Contains(m) && P.ActiveParts.Contains(i)) { idxHot = i; return Part.Pill; }
                return Part.None;
            }
            // the two lines: the limiter's ceiling (over the curve and over the last seconds; with the compressor off too)
            // and the threshold (over the last seconds); where they lie close, the nearer one
            int near = Theme.S(6);
            var h = Rectangle.Inflate(History, 0, near); var c = Rectangle.Inflate(Curve, 0, near);
            bool inH = h.Width > 0 && h.Contains(m), bands = ByFreq && inH && !Dim;
            // the bands: a split first (its grip, or its line), then the lines, then the column itself
            if (bands)
            {
                int n = P.PartCount;
                for (int k = 0; k < n - 1; k++)
                {
                    var s = SplitAt(k); float dx = m.X - s.X, dy = m.Y - s.Y;
                    if (Math.Abs(dx) <= Theme.S(5) || dx * dx + dy * dy < Theme.S(13) * Theme.S(13)) return k == 0 ? Part.Split1 : Part.Split2;
                }
                zoneHot = ZoneAt(m.X);
                if (ListenRect(zoneHot, false).Contains(m)) return Part.Solo;
                if (ListenRect(zoneHot, true).Contains(m)) return Part.Mute;
            }
            // by frequencies each column has its own threshold line: the one of the column under the mouse
            float dc = Math.Abs(m.Y - Y(P.Ceil)), dt = Math.Abs(m.Y - Y(bands ? P.Parts[zoneHot].Thr : Q.Thr));
            bool onCeil = P.Limit && dc <= near && (inH || c.Contains(m));
            bool onLine = !Dim && dt <= near && inH;
            if (onCeil && (!onLine || dc <= dt)) return Part.Ceil;
            if (onLine) return Part.Line;
            if (bands) return Part.Zone;
            if (Dim) return Part.None;
            var pn = Panel;
            if (pn.Width > 0 && pn.Contains(m)) { for (int k = 0; k < DialCount; k++) if (DialCell(k).Contains(m)) return DialOn(k) ? Dials[k] : Part.None; }
            return Part.None;
        }

        // a change by the mouse (a drag, a dial, the wheel) is painted at once: Windows paints a window only when no other
        // message waits, and a fast mouse sends its moves without a pause — the picture stood still while it was dragged
        // and moved again only when the mouse stopped (20.11, the user: «двигаешь и даже не видишь, куда двигаешь»)
        void Fire() { Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); Update(); }

        static float Tenth(double v) { return (float)(Math.Round(v * 10) / 10); }

        void DragTo(Point m)
        {
            float mx = m.X + grabDx, my = m.Y + grabDy;
            double turn = (dragY - m.Y) / (double)Theme.S(220); // a dial: its whole range in 220 units of travel
            switch (drag)
            {
                case Part.Thr: Q.Thr = (float)Math.Round(Clamp(DbAtX(mx), -60, 0)); break;
                case Part.Line: Q.Thr = (float)Math.Round(Clamp(DbAtY(my), -60, 0)); break;
                case Part.Makeup: { double x = MakeupX; Q.Makeup = (float)Math.Round(Clamp(DbAtY(my) - x - Gr(x), -12, 24)); break; }
                case Part.Ratio:
                {
                    // the right end of the curve follows the mouse: the ratio that puts the output there. With the
                    // threshold at the very top there is no such stretch, the point then turns as a dial does.
                    double span = Hi - Q.Thr;
                    if (span >= 6) { double o = DbAtY(my) - Q.Makeup; Q.Ratio = Tenth(o >= Hi - 0.01 ? 1 : o <= Q.Thr + span / 20 ? 20 : Clamp(span / (o - Q.Thr), 1, 20)); }
                    else Q.Ratio = Tenth(Clamp(dragFrom * Math.Pow(20, -turn), 1, 20));
                    break;
                }
                case Part.Attack: Q.Attack = (float)Math.Round(Clamp(dragFrom * Math.Pow(200, turn), 1, 200)); break;
                case Part.Release: Q.Release = (float)(Math.Round(Clamp(dragFrom * Math.Pow(200, turn), 10, 2000) / 10) * 10); break;
                case Part.Knee: Q.Knee = (float)Math.Round(Clamp(dragFrom + 24 * turn, 0, 24)); break;
                case Part.DialThr: Q.Thr = (float)Math.Round(Clamp(dragFrom + 60 * turn, -60, 0)); break;
                case Part.DialRatio: Q.Ratio = Tenth(Clamp(dragFrom * Math.Pow(20, turn), 1, 20)); break;
                case Part.DialMakeup: Q.Makeup = (float)Math.Round(Clamp(dragFrom + 36 * turn, -12, 24)); break;
                // the splits as dials: the whole range of each (80..600 Hz, 1.2..12 kHz) in the same travel, evenly by ear
                case Part.DialSplit1: P.Split = (float)(Math.Round(Clamp(dragFrom * Math.Pow(600 / 80.0, turn), 80, 600) / 10) * 10); break;
                case Part.DialSplit2: P.Split2 = (float)(Math.Round(Clamp(dragFrom * Math.Pow(10, turn), 1200, 12000) / 100) * 100); break;
                case Part.Ceil: P.Ceil = (float)(Math.Round(Clamp(DbAtY(my), -12, 0) * 2) / 2); break; // by half a dB
                case Part.CeilFader: P.Ceil = (float)(Math.Round(Clamp(dragFrom + (dragY - m.Y) * 12.0 / Math.Max(1, CeilBottom - CeilTop), -12, 0) * 2) / 2); break; // from where it was taken
                // the splits: within what the page's sliders allow, by their steps
                case Part.Split1: P.Split = (float)(Math.Round(Clamp(HzAtX(mx), 80, 600) / 10) * 10); break;
                case Part.Split2: P.Split2 = (float)(Math.Round(Clamp(HzAtX(mx), 1200, 12000) / 100) * 100); break;
            }
            Fire();
        }

        // a column clicked: that band is the one shown from now on (the page moves its rows after it)
        void Pick(int part)
        {
            if (part < 0 || part == Band || !P.ActiveParts.Contains(part)) return;
            Band = part; Invalidate();
            var h = BandPicked; if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            armed = true; SetInCol(BandsCol.Contains(e.Location));
            if (e.Button == MouseButtons.Middle) { ResetAt(e.Location); Invalidate(); base.OnMouseDown(e); return; }
            var part = Hit(e.Location);
            if (e.Button == MouseButtons.Left && (part == Part.Solo || part == Part.Mute)) { Listen(part, zoneHot); base.OnMouseDown(e); return; }
            if (e.Button == MouseButtons.Left && (part == Part.Count || part == Part.Pill))
            {
                // 20.11 (the user): the monitor stays as it is — over time or by frequencies — when the number of bands or
                // the band being set is picked (it used to turn to the bands)
                hot = part;
                if (part == Part.Count) SetCount(idxHot + 1); else Pick(idxHot);
                Invalidate(); base.OnMouseDown(e); return;
            }
            if (e.Button == MouseButtons.Left && (part == Part.ViewTime || part == Part.ViewFreq))
            {
                SetView(part == Part.ViewFreq);
                var h = ViewPicked; if (h != null) h(this, EventArgs.Empty);
                base.OnMouseDown(e); return;
            }
            // by frequencies: a press on a column or on its threshold line takes that band first; the line is then dragged at once
            if (e.Button == MouseButtons.Left && ByFreq && (part == Part.Zone || part == Part.Line)) Pick(zoneHot);
            if (e.Button == MouseButtons.Left && part == Part.Zone) { hot = part; Invalidate(); base.OnMouseDown(e); return; }
            if (e.Button == MouseButtons.Left && part != Part.None)
            {
                drag = hot = part; dragY = e.Y; grabDx = grabDy = 0;
                dragFrom = part == Part.Attack ? Q.Attack : part == Part.Release ? Q.Release : part == Part.Knee ? Q.Knee : part == Part.DialThr ? Q.Thr : part == Part.DialMakeup ? Q.Makeup : part == Part.DialSplit1 ? P.Split : part == Part.DialSplit2 ? P.Split2 : Q.Ratio;
                // a split's dial taken: the monitor turns to the bands, where the split is seen moving
                if ((part == Part.DialSplit1 || part == Part.DialSplit2) && !ByFreq) { SetView(true); drag = hot = part; var hv = ViewPicked; if (hv != null) hv(this, EventArgs.Empty); }
                // the point stays under the mouse where it was taken, it does not jump to the pointer
                if (part == Part.Thr || part == Part.Ratio || part == Part.Makeup) { var k = KnobAt(part); grabDx = k.X - e.X; grabDy = k.Y - e.Y; }
                else if (part == Part.Line) grabDy = Y(Q.Thr) - e.Y;
                else if (part == Part.Ceil) grabDy = Y(P.Ceil) - e.Y;
                else if (part == Part.CeilFader)
                {
                    // the pointer onto the knob, the knob stays (20.11, as the equalizer's faders)
                    dragFrom = P.Ceil; var cs = CeilStrip; int ky = (int)Math.Round(CeilY(P.Ceil));
                    if (Theme.Warp(this, cs.X + cs.Width / 2f, ky)) dragY = ky;
                }
                else if (part == Part.Split1 || part == Part.Split2) grabDx = SplitAt(part == Part.Split1 ? 0 : 1).X - e.X;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { drag = Part.None; Invalidate(); base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            SetInCol(BandsCol.Contains(e.Location));
            if (drag != Part.None) { DragTo(e.Location); base.OnMouseMove(e); return; }
            int zoneWas = zoneHot, idxWas = idxHot;
            var part = Hit(e.Location);
            bool click = part == Part.ViewTime || part == Part.ViewFreq || part == Part.Solo || part == Part.Mute || (part == Part.Zone && zoneHot != Band) || (part == Part.Count && idxHot != P.PartCount - 1) || (part == Part.Pill && idxHot != Band);
            Cursor = part == Part.Thr || part == Part.Split1 || part == Part.Split2 ? Cursors.SizeWE : click ? Cursors.Hand : part == Part.Zone || part == Part.Count || part == Part.Pill ? Cursors.Default : part != Part.None ? Cursors.SizeNS : Cursors.Default;
            if (part != hot || zoneHot != zoneWas || idxHot != idxWas) { hot = part; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            armed = false; SetInCol(false);
            if (drag == Part.None && (hot != Part.None || zoneHot >= 0)) { hot = Part.None; zoneHot = -1; idxHot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        // the wheel works over a point or a dial and after a click on the picture; otherwise the page scrolls.
        // Over the threshold point it changes the knee, elsewhere the value under the mouse
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!armed || hot == Part.None || hot == Part.Zone || hot == Part.ViewTime || hot == Part.ViewFreq || hot == Part.Count || hot == Part.Pill || hot == Part.Solo || hot == Part.Mute || (Dim && hot != Part.Ceil && hot != Part.CeilFader)) { base.OnMouseWheel(e); return; }
            if (hot == Part.Line && ByFreq && zoneHot != Band) { base.OnMouseWheel(e); return; } // the line of a band not taken yet
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            // 20.11 (the user: a notch moved a dial by three): one notch (120) = one step of the value, whatever the mouse
            // sends — a smooth wheel sends a notch in several small parts, each of them used to be a whole step
            int notches = Theme.WheelNotches(ref wheelAcc, e.Delta);
            for (int i = 0; i < Math.Abs(notches); i++) WheelStep(notches > 0 ? 1 : -1);
            if (notches != 0) Fire();
        }
        int wheelAcc;

        // one step of the value under the mouse (20.11: by one step of its scale; the attack, the release and the ratio
        // went by 15 and 10 %, three milliseconds a notch at 20 ms)
        void WheelStep(int d)
        {
            switch (hot)
            {
                case Part.Split1: case Part.DialSplit1: P.Split = (float)Clamp(Math.Round(P.Split / 10) * 10 + d * 10, 80, 600); break;
                case Part.Split2: case Part.DialSplit2: P.Split2 = (float)Clamp(Math.Round(P.Split2 / 100) * 100 + d * 100, 1200, 12000); break;
                case Part.Ceil: case Part.CeilFader: P.Ceil = Tenth(Clamp(P.Ceil + d * 0.5, -12, 0)); break;
                case Part.Thr: if (P.CompChar != 3) Q.Knee = (float)Clamp(Math.Round(Q.Knee) + d, 0, 24); break;   // (the tube character has its own knee)
                case Part.Knee: Q.Knee = (float)Clamp(Math.Round(Q.Knee) + d, 0, 24); break;
                case Part.DialThr: Q.Thr = (float)Clamp(Math.Round(Q.Thr) + d, -60, 0); break;
                case Part.DialMakeup: Q.Makeup = (float)Clamp(Math.Round(Q.Makeup) + d, -12, 24); break;
                case Part.DialRatio: Q.Ratio = Tenth(Clamp(Q.Ratio + d * 0.1, 1, 20)); break; // a dial: up = more
                case Part.Line: Q.Thr = (float)Clamp(Math.Round(Q.Thr) + d, -60, 0); break;
                case Part.Makeup: Q.Makeup = (float)Clamp(Math.Round(Q.Makeup) + d, -12, 24); break;
                case Part.Ratio: Q.Ratio = Tenth(Clamp(Q.Ratio - d * 0.1, 1, 20)); break; // up = less squeezing, as the point goes up
                case Part.Attack: Q.Attack = (float)Clamp(Math.Round(Q.Attack) + d, 1, 200); break;
                case Part.Release: Q.Release = (float)Clamp(Math.Round(Q.Release / 10) * 10 + d * 10, 10, 2000); break;
            }
        }

        // a press of the wheel puts the value under the mouse back to where a new device starts (20.11, the user's wish:
        // it was a double click, which went off by itself when a knob was clicked twice quickly)
        void ResetAt(Point at)
        {
            var part = Hit(at);
            if (part == Part.Solo || part == Part.Mute) return;
            if (part != Part.None && part != Part.Zone && part != Part.ViewTime && part != Part.ViewFreq && part != Part.Count && part != Part.Pill)
            {
                drag = Part.None; var s = SoundParams.Start();
                if (part == Part.Makeup || part == Part.DialMakeup) Q.Makeup = 0;
                else if (part == Part.DialThr) Q.Thr = s.Thr;
                else if (part == Part.DialRatio) Q.Ratio = s.Ratio;
                else if (part == Part.DialSplit1) P.Split = 250; // where the page's sliders start
                else if (part == Part.DialSplit2) P.Split2 = 4000;
                else if (part == Part.Attack) Q.Attack = s.Attack;
                else if (part == Part.Release) Q.Release = s.Release;
                else if (part == Part.Knee) Q.Knee = s.Knee;
                else if (part == Part.CeilFader) P.Ceil = s.Ceil;
                else return;
                Fire();
            }
        }

        static float Db(float lin) { return lin <= 0.00001f ? -100f : 20f * (float)Math.Log10(lin); }

        // one more moment of the sound (30 a second from the page): the peaks in and out, linear, and what the compressor
        // and the limiter took off, dB
        public void Feed(bool on, float inPeak, float outPeak, float grDb, float limDb, float levelDb)
        {
            if (demo) return;
            if (!on) { if (live) { live = false; hCount = 0; nowIn = -100; nowLv = -140; nowGr = nowLim = 0; Invalidate(); } return; }
            Push(Db(inPeak), Db(outPeak), grDb, limDb, bGr); nowLv = levelDb; // bGr: all the bands, from FeedBands just before
            live = true;
            // drawn live every moment (20.11, the user: no caches). 21.11: only what the sound moves — the dot on the
            // curve and the monitor — not the dials, the words and the bands' buttons; the mouse still repaints it whole
            var c = Curve; LiveInvalidate(Rectangle.FromLTRB(c.X - Theme.S(5), c.Y - Theme.S(5), c.Right + Theme.S(5), c.Bottom + Theme.S(4))); // the dot: 7 across
            // the dials lie between the two: one paint of both would take their bounding box (WinForms paints the box of
            // what is invalid), dials and all; so the curve is painted first, on its own
            var hist = History; if (hist.Width > 0) { if (InView) Update(); LiveInvalidate(Rectangle.Inflate(hist, 2, 2)); }
        }

        // the same moment per band, for the monitor by frequencies (before Feed, which draws): the level each band reads
        // and what is taken off it, dB; null = nothing. The levels rise at once and fall slower, so the columns do not flicker
        public void FeedBands(float[] gr, float[] lv)
        {
            if (demo) return;
            for (int i = 0; i < 3; i++)
            {
                float l = lv == null ? -140 : Math.Max(-140, lv[i]);
                bLv[i] = l > bLv[i] ? bLv[i] + (l - bLv[i]) * 0.7f : bLv[i] + (l - bLv[i]) * 0.25f;
                bGr[i] = gr == null ? 0 : gr[i];
            }
        }

        void Push(float i, float o, float gr, float lim, float[] grs)
        {
            Array.Copy(hIn, 1, hIn, 0, Hist - 1); Array.Copy(hOut, 1, hOut, 0, Hist - 1); Array.Copy(hLim, 1, hLim, 0, Hist - 1);
            hIn[Hist - 1] = i; hOut[Hist - 1] = o; hLim[Hist - 1] = lim; if (hCount < Hist) hCount++;
            for (int b = 0; b < 3; b++) { Array.Copy(hGrB[b], 1, hGrB[b], 0, Hist - 1); hGrB[b][Hist - 1] = grs[b]; }
            nowIn = i; nowGr = gr; nowLim = lim;
        }

        // for the off-screen render: a made-up sound that goes over the threshold now and then
        public void Demo()
        {
            demo = true; hCount = 0;
            for (int k = 0; k < Hist; k++)
            {
                float i = (float)(-27 + 13 * Math.Sin(k / 17.0) + 7 * Math.Sin(k / 3.1) + 4 * Math.Sin(k / 1.3)); if (i > -1) i = -1;
                float gr = Dim ? 0 : (float)Gr(i), o = i + gr + (Dim ? 0 : Q.Makeup);
                float lim = P.Limit && o > P.Ceil ? P.Ceil - o : 0;
                var grs = new float[3]; for (int b = 0; b < 3; b++) grs[b] = Dim ? 0 : (float)GrOf(P.Parts[b], i);
                Push(i, Math.Min(0, o + lim), gr, lim, grs); nowLv = i;
            }
            float[] lv = { -11, -21, -30 };
            for (int b = 0; b < 3; b++) { bLv[b] = lv[b]; bGr[b] = Dim ? 0 : (float)GrOf(P.Parts[b], lv[b]); }
            live = true; Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        // the monitor by frequencies (see ByFreq): the columns of the bands at work, the splits between them
        // the last ten seconds as lines: what comes in, what goes out, what the compressor and the limiter take off, the
        // threshold (all of it moves, so it is drawn through ILive: GDI+ or the graphics card)
        void HistTime(ILive cv, Rectangle hist, Color line, Color lit, Color limC, bool lim)
        {
            if (live && hCount > 1)
            {
                int n = hCount; float dx = hist.Width / (float)(Hist - 1); var hGr = hGrB[Math.Max(0, Math.Min(2, Band))]; // the band shown
                var pin = new List<PointF>(); var pout = new PointF[n]; var pgr = new List<PointF>(); var pall = new List<PointF>();
                pin.Add(new PointF(hist.Right - (n - 1) * dx, hist.Bottom)); pgr.Add(new PointF(hist.Right - (n - 1) * dx, hist.Y)); pall.Add(pgr[0]);
                for (int k = 0; k < n; k++)
                {
                    int at = Hist - n + k; float x = hist.Right - (n - 1 - k) * dx;
                    pin.Add(new PointF(x, Y(hIn[at]))); pout[k] = new PointF(x, Y(hOut[at]));
                    pgr.Add(new PointF(x, hist.Y + hist.Height * (float)(Clamp(-hGr[at], 0, Hi - Lo) / (Hi - Lo))));
                    pall.Add(new PointF(x, hist.Y + hist.Height * (float)(Clamp(-hGr[at] - hLim[at], 0, Hi - Lo) / (Hi - Lo))));
                }
                pin.Add(new PointF(hist.Right, hist.Bottom)); pgr.Add(new PointF(hist.Right, hist.Y)); pall.Add(new PointF(hist.Right, hist.Y));
                cv.FillPoly(Color.FromArgb(Dim ? 20 : 40, Theme.Text), pin.ToArray());
                cv.Lines(Color.FromArgb(Dim ? 90 : 210, Theme.Text), Theme.S(1.4f), pout);
                // from the top down: what the compressor takes off, and under it what the limiter adds to that
                if (lim)
                {
                    cv.FillPoly(Color.FromArgb(80, limC), pall.ToArray());
                    cv.Lines(limC, Theme.S(1.4f), pall.Skip(1).Take(n).ToArray());
                }
                if (!Dim)
                {
                    if (lim) cv.FillPoly(Theme.Card, pgr.ToArray()); // the compressor's part is not tinted by the limiter's colour
                    cv.FillPoly(Color.FromArgb(70, line), pgr.ToArray());
                    cv.Lines(line, Theme.S(1.4f), pgr.Skip(1).Take(n).ToArray());
                }
            }
            bool lh = hot == Part.Line || drag == Part.Line;
            cv.Line(Dim ? Theme.Muted : lh ? lit : line, Theme.S(lh ? 1.8f : 1.2f), hist.X, Y(Q.Thr), hist.Right, Y(Q.Thr), true);
        }

        // the monitor by frequencies; stage: 1 what stands under the levels (the bands' ground, the decade lines), 2 the
        // levels and what is taken off (they move: through cv), 4 what lies over them (thresholds, names, S and M, the
        // splits). A paint by the program does all three at once; the graphics card gets 1 and 4 as pictures.
        void PaintBands(Graphics g, Rectangle hist, Color limC, bool lim, int stage, ILive cv)
        {
            int n = P.PartCount; float pad = Theme.S(5);
            bool under = (stage & 1) != 0, moving = (stage & 2) != 0, top = (stage & 4) != 0;
            // the frequency scale: a line per decade, the numbers along the bottom edge
            if (under) using (var pen = new Pen(Theme.Border)) foreach (var f in new[] { 100, 1000, 10000 }) g.DrawLine(pen, FX(f), hist.Y, FX(f), hist.Bottom);
            foreach (var z in Zones())
            {
                float x0 = FX(z.From), x1 = FX(z.To); var q = P.Parts[z.Part];
                bool sel = n == 1 || z.Part == Band, over = !Dim && zoneHot == z.Part && (hot == Part.Zone || hot == Part.Line) && drag == Part.None;
                Color col = Dim ? Theme.Muted : n == 1 ? Theme.Accent : PartColor[z.Part];
                if (under) using (var b = new SolidBrush(Color.FromArgb(Dim ? 8 : sel ? 34 : over ? 24 : 12, col))) g.FillRectangle(b, x0, hist.Y, x1 - x0, hist.Height);
                float bx = x0 + pad, bw = Math.Max(Theme.S(2), x1 - x0 - 2 * pad);
                // from the bottom: the level the band reads now
                if (moving && live && bLv[z.Part] > Lo)
                {
                    float y = Y(bLv[z.Part]);
                    cv.FillRect(Color.FromArgb(Dim ? 20 : sel ? 60 : 38, Theme.Text), bx, y, bw, hist.Bottom - y);
                    cv.Line(Color.FromArgb(Dim ? 90 : sel ? 220 : 140, Theme.Text), Theme.S(1.4f), bx, y, bx + bw, y);
                }
                // from the top: what is taken off the band, and under it what the limiter adds to that (as in the last seconds)
                if (moving && live)
                {
                    double gr = Dim ? 0 : -bGr[z.Part], all = gr + (lim ? -nowLim : 0);
                    float hg = hist.Height * (float)(Clamp(gr, 0, Hi - Lo) / (Hi - Lo)), ha = hist.Height * (float)(Clamp(all, 0, Hi - Lo) / (Hi - Lo));
                    if (lim && ha - hg > 0.5f)
                    {
                        cv.FillRect(Color.FromArgb(80, limC), bx, hist.Y + hg, bw, ha - hg);
                        cv.Line(limC, Theme.S(1.4f), bx, hist.Y + ha, bx + bw, hist.Y + ha);
                    }
                    if (hg > 0.5f)
                    {
                        cv.FillRect(Color.FromArgb(sel ? 90 : 60, col), bx, hist.Y, bw, hg);
                        cv.Line(col, Theme.S(1.4f), bx, hist.Y + hg, bx + bw, hist.Y + hg);
                    }
                }
                // its threshold (with the moving parts: dragging it must not make the pictures of the card anew)
                if (moving)
                {
                    bool lh = sel && (hot == Part.Line || drag == Part.Line) && (drag == Part.Line || zoneHot == z.Part);
                    // (lighter than the band's colour: the colour itself was lost over the grey of the level)
                    cv.Line(Dim ? Theme.Muted : lh ? Color.White : Lighter(Lighter(col)), Theme.S(lh ? 2f : sel ? 1.6f : 1.3f), x0, Y(q.Thr), x1, Y(q.Thr), true);
                }
                if (!top) continue;
                // a band that does not go out now (its M, or another band's S): its column under a veil
                bool silent = (Silent >> z.Part & 1) != 0;
                if (silent) using (var b = new SolidBrush(Color.FromArgb(165, Theme.Card))) g.FillRectangle(b, x0, hist.Y, x1 - x0, hist.Height);
                // the band shown is framed; its name on top, with its colour beside it
                if (sel && n > 1 && !Dim) using (var pen = new Pen(Color.FromArgb(200, col), Theme.S(1.4f))) g.DrawRectangle(pen, x0 + 0.5f, hist.Y + 0.5f, x1 - x0 - 1f, hist.Height - 1f);
                string name = n == 1 ? "Весь звук" : ZoneNames[z.Part];
                int tw = TextRenderer.MeasureText(g, L.T(name), Theme.Small, new Size(1000, 100), TextFormatFlags.NoPadding).Width, dot = Theme.S(8), dgap = Theme.S(6), padX = Theme.S(9);
                Color ink = Dim || silent ? Theme.Muted : sel ? Theme.Text : over ? Theme.Text : Theme.Text2;
                int ty = hist.Y + Theme.S(6), th = Theme.S(20);
                if (!Dim)
                    foreach (bool mute in new[] { false, true })
                    {
                        var lr = ListenRect(z.Part, mute); if (lr.Width == 0) continue;
                        bool on = ((mute ? MuteMask : SoloMask) >> z.Part & 1) != 0, bh = hot == (mute ? Part.Mute : Part.Solo) && zoneHot == z.Part;
                        var lf = new RectangleF(lr.X + 0.5f, lr.Y + 0.5f, lr.Width - 1f, lr.Height - 1f); Color lc = mute ? MuteC : SoloC;
                        Theme.Fill(g, lf, Theme.S(5), on ? lc : bh ? Theme.Raised : Color.FromArgb(215, Theme.Card));
                        Theme.Stroke(g, lf, Theme.S(5), on ? lc : bh ? Theme.BorderStrong : Theme.Border);
                        Glyph(g, mute ? "M" : "S", Theme.Small, lr, on ? Color.FromArgb(0x1c, 0x14, 0x0a) : bh ? Theme.Text : Theme.Text2);
                    }
                if (dot + dgap + tw + 2 * padX + Theme.S(4) <= x1 - x0)
                {
                    float whole = dot + dgap + tw, sx = (x0 + x1) / 2 - whole / 2f;
                    // on a plate of its own: what is taken off hangs from the top and its edge would run through the name
                    Theme.Fill(g, new RectangleF(sx - padX, ty, whole + 2 * padX, th), th / 2f, Color.FromArgb(205, Theme.Card));
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, sx, ty + (th - dot) / 2f, dot, dot);
                    Theme.Text1(g, name, Theme.Small, new Rectangle((int)Math.Round(sx + dot + dgap), ty, tw + Theme.S(3), th), ink, TextFormatFlags.NoPadding);
                }
                else Theme.Text1(g, name, Theme.Small, new Rectangle((int)x0, ty, (int)(x1 - x0), th), ink, Mid);
            }
            if (!top) return;
            // the numbers of the scale
            foreach (var f in hist.Width > Theme.S(520) ? new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 } : new[] { 100, 1000, 10000 })
                Theme.Text1(g, f >= 1000 ? f / 1000 + "k" : f.ToString(), Theme.Small, new Rectangle((int)FX(f) - Theme.S(30), hist.Bottom - Theme.S(19), Theme.S(60), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
            // a split that is not at work: where it would stand
            using (var dots = new Pen(Color.FromArgb(Dim ? 60 : 150, Theme.Text2), Theme.S(1.2f)) { DashStyle = DashStyle.Dash })
            {
                if (n < 2) g.DrawLine(dots, FX(Ghost1), hist.Y, FX(Ghost1), hist.Bottom);
                if (n < 3) g.DrawLine(dots, FX(Ghost2), hist.Y, FX(Ghost2), hist.Bottom);
            }
            // the splits at work: a line with a grip and its frequency beside the grip
            for (int k = 0; k < n - 1; k++)
            {
                var s = SplitAt(k); var pk = k == 0 ? Part.Split1 : Part.Split2; bool h = !Dim && (hot == pk || drag == pk);
                using (var pen = new Pen(Color.FromArgb(Dim ? 70 : h ? 255 : 150, Theme.Text), Theme.S(h ? 1.8f : 1.2f))) g.DrawLine(pen, s.X, hist.Y, s.X, hist.Bottom);
                float size = Theme.S(h ? 19 : 16);
                Theme.GlassKnob(g, s.X, s.Y, size, 1f, Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, Theme.S(4.5f), Dim, 180f, null, -1f, new[] { new PointF(s.X, s.Y - size), new PointF(s.X, s.Y + size) });
                string hz = EqGraph.Hz(k == 0 ? P.Split : P.Split2); int lw = Theme.S(76);
                bool left = s.X + Theme.S(12) + lw > hist.Right;
                Theme.Text1(g, hz, Theme.Small, new Rectangle(left ? (int)s.X - Theme.S(12) - lw : (int)s.X + Theme.S(12), (int)s.Y - Theme.S(10), lw, Theme.S(20)), Dim ? Theme.Muted : h ? Theme.Text : Theme.Text2, left ? TextFormatFlags.Right : 0);
            }
        }

        static string DbText(double v) { int n = (int)Math.Round(v); return (n < 0 ? "−" : "") + Math.Abs(n) + " дБ"; }
        static string Plus(double v) { int n = (int)Math.Round(v); return (n > 0 ? "+" : n < 0 ? "−" : "") + Math.Abs(n) + " дБ"; }
        string RatioText { get { return Q.Ratio.ToString("0.#", CultureInfo.InvariantCulture) + " : 1"; } }
        // the limiter's ceiling fader (see CeilStrip): grey and still while the limiter is off
        void CeilFader(Graphics g, Color limC, bool lim, bool ch)
        {
            var cs = CeilStrip; if (cs.Width <= 0) return;
            float cx = cs.X + cs.Width / 2f, th = Theme.S(5), top = CeilTop, bottom = CeilBottom;
            if (ch && lim) Theme.Fill(g, new RectangleF(cs.X + 1, cs.Y - Theme.S(4), cs.Width - 2, cs.Height + Theme.S(8)), Theme.S(6), Color.FromArgb(46, Theme.Text));
            var ic = lim ? limC : Theme.Muted;
            using (var pen = new Pen(ic, Theme.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float iy = cs.Y + Theme.S(6), w = Theme.S(15), a = Theme.S(2.5f);
                g.DrawLine(pen, cx - w / 2, iy, cx + w / 2, iy);
                g.DrawLine(pen, cx, iy + Theme.S(3), cx, iy + Theme.S(11));
                g.DrawLines(pen, new[] { new PointF(cx - a, iy + Theme.S(3) + a), new PointF(cx, iy + Theme.S(3)), new PointF(cx + a, iy + Theme.S(3) + a) });
            }
            Theme.Tube(g, new RectangleF(cx - th / 2f, top, th, bottom - top));
            float y = CeilY(P.Ceil);
            if (y - top > 1f) Theme.Fill(g, new RectangleF(cx - th / 2f, top, th, y - top), th / 2f, lim ? limC : Theme.Muted);
            Theme.GlassKnob(g, cx, y, Theme.S(ch && lim ? 16 : 14), lim ? 1f : 0f, lim ? limC : Theme.Muted, th, !lim, 270f, null, lim ? y - top : 0f); // lit towards the top: what is cut off
            string v = P.Ceil > -0.05f ? "0" : "−" + (-P.Ceil).ToString("0.#", CultureInfo.InvariantCulture);
            Theme.Text1(g, v, Theme.Small, new Rectangle((int)cx - Theme.S(24), Curve.Bottom + Theme.S(3), Theme.S(48), Theme.S(20)), lim ? (ch ? Theme.Text : Theme.Text2) : Theme.Muted, TextFormatFlags.HorizontalCenter);
        }

        string CeilText { get { return (P.Ceil > -0.05f ? "0" : "−" + (-P.Ceil).ToString("0.0", CultureInfo.InvariantCulture)) + " дБ"; } }

        // the transfer curve with its fill, the other bands, the threshold, the ceiling and the three points; the dot of the
        // sound now only with withDot
        void CurveBlock(Graphics g, Rectangle c, bool withDot)
        {
            Color line = Dim ? Theme.Muted : P.PartCount == 1 ? Theme.Accent : PartColor[Math.Max(0, Math.Min(2, Band))];
            Color lit = Dim ? Theme.Muted : Lighter(line);
            Color limC = Theme.Warning; bool lim = P.Limit, ch = hot == Part.Ceil || drag == Part.Ceil || hot == Part.CeilFader || drag == Part.CeilFader;
            float cy = Y(P.Ceil);
            var clip = g.Clip;
            int steps = Math.Max(40, c.Width / 2);
            var curve = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++) { double x = Lo + (Hi - Lo) * i / steps; curve[i] = new PointF(X(x), Y(Out(x))); }
            // with the limiter on the curve goes flat at the ceiling; what the compressor alone gives stays as a dashed line above it
            var real = curve;
            if (lim) { real = new PointF[curve.Length]; for (int i = 0; i < curve.Length; i++) real[i] = new PointF(curve[i].X, Math.Max(curve[i].Y, cy)); }
            g.SetClip(c);
            using (var path = new GraphicsPath())
            {
                path.AddLines(real);
                path.AddLine(real[steps], new PointF(c.Right, c.Bottom)); path.AddLine(new PointF(c.Right, c.Bottom), new PointF(c.X, c.Bottom)); path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(Dim ? 14 : 30, line))) g.FillPath(b, path);
            }
            using (var dash = new Pen(Color.FromArgb(Dim ? 60 : 110, Theme.Text2)) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, X(Q.Thr), c.Y, X(Q.Thr), c.Bottom);
            // the other bands at work, thin, each in its colour
            foreach (int other in P.ActiveParts)
            {
                if (other == Band) continue;
                var q = P.Parts[other]; var pts = new PointF[steps + 1];
                for (int i = 0; i <= steps; i++) { double x = Lo + (Hi - Lo) * i / steps; double o = x + GrOf(q, x) + q.Makeup; pts[i] = new PointF(X(x), lim ? Math.Max(Y(o), cy) : Y(o)); }
                using (var pen = new Pen(Color.FromArgb(Dim ? 70 : 170, Dim ? Theme.Muted : PartColor[other]), Theme.S(1.4f)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
            }
            if (lim) using (var pen = new Pen(Color.FromArgb(Dim ? 70 : 120, line), Theme.S(1.4f)) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round }) g.DrawLines(pen, curve);
            using (var pen = new Pen(line, Theme.S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, real);
            g.Clip = clip;
            // the ceiling itself (it may lie on the very top edge of the plot, so it is drawn unclipped)
            if (lim) using (var pen = new Pen(ch ? Color.FromArgb(0xf6, 0xd0, 0x7a) : limC, Theme.S(ch ? 2.2f : 1.5f))) g.DrawLine(pen, c.X, cy, c.Right, cy);
            if (withDot) { g.SetClip(c); NowDot(new GdiLive(g), c); g.Clip = clip; }

            foreach (var p in new[] { Part.Makeup, Part.Ratio, Part.Thr })
            {
                var k = KnobAt(p); bool h = p == hot || p == drag;
                float size = Theme.S(h ? 19 : 16);
                int a = 0, b2 = curve.Length - 1;
                while (a + 1 < curve.Length && curve[a + 1].X < k.X - size) a++;
                while (b2 - 1 >= 0 && curve[b2 - 1].X > k.X + size) b2--;
                var piece = new PointF[Math.Max(0, b2 - a + 1)]; if (piece.Length > 0) Array.Copy(curve, a, piece, 0, piece.Length);
                Theme.GlassKnob(g, k.X, k.Y, size, 1f, h ? lit : line, Theme.S(4.5f), Dim, 180f, null, -1f, piece);
            }
        }

        // the sound now: from where it would be without the compressor down to where it is
        void NowDot(ILive cv, Rectangle c)
        {
            if (!live || Dim || nowLv <= Lo) return;
            float x = X(nowLv), y0 = Y(nowLv + Q.Makeup), y1 = Y(nowLv + nowGr + Q.Makeup + nowLim);
            if (y1 - y0 > 1.5f) cv.Line(Color.FromArgb(170, Theme.Text), Theme.S(1.4f), x, y0, x, y1);
            float d = Theme.S(7);
            cv.Ellipse(Theme.Text, x - d / 2, y1 - d / 2, d, d);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, card, Theme.S(8), Theme.Card);
            Theme.Stroke(g, card, Theme.S(8), Theme.Border);
            SyncPlates();
            var c = Curve; var hist = History; var pn = Panel;
            if (c.Width < 20 || Width < c.Right + Theme.S(10)) return;
            // one band: the program's red; in bands: the colour of the band shown
            Color line = Dim ? Theme.Muted : P.PartCount == 1 ? Theme.Accent : PartColor[Math.Max(0, Math.Min(2, Band))];
            Color lit = Dim ? Theme.Muted : Lighter(line);
            Color limC = Theme.Warning; bool lim = P.Limit, ch = hot == Part.Ceil || drag == Part.Ceil || hot == Part.CeilFader || drag == Part.CeilFader;
            float cy = Y(P.Ceil);
            // a paint of a part of the picture (Windows uncovering it) skips what lies outside its clip
            var want = e.ClipRectangle;
            bool doCurve = want.IntersectsWith(Rectangle.Inflate(c, Theme.S(14), Theme.S(14))), doHist = hist.Width > 0 && want.IntersectsWith(Rectangle.Inflate(hist, 2, 2));
            var clip = g.Clip;

            // the scale: the same dB for the levels in (across) and out (up), a line every 12
            using (var pen = new Pen(Theme.Border))
            using (var strong = new Pen(Theme.BorderStrong))
            {
                for (int db = (int)Lo; db <= Hi; db += 12)
                {
                    float y = Y(db), x = X(db);
                    string t = db == 0 ? "0" : "−" + (-db);
                    g.DrawLine(db == 0 ? strong : pen, c.X, y, c.Right, y);
                    g.DrawLine(db == 0 ? strong : pen, x, c.Y, x, c.Bottom);
                    // 21.11: the words only when the paint reaches them (a moment of the sound repaints the curve and the monitor)
                    var wl = new Rectangle(0, (int)y - Theme.S(10), c.X - Theme.S(8), Theme.S(20)); if (want.IntersectsWith(wl)) Theme.Text1(g, t, Theme.Small, wl, Theme.Muted, TextFormatFlags.Right);
                    var wb = new Rectangle((int)x - Theme.S(30), c.Bottom + Theme.S(4), Theme.S(60), Theme.S(18)); if (want.IntersectsWith(wb)) Theme.Text1(g, t, Theme.Small, wb, Theme.Muted, TextFormatFlags.HorizontalCenter);
                    if (hist.Width > 0) g.DrawLine(db == 0 ? strong : pen, hist.X, y, hist.Right, y);
                }
                if (hist.Width > 0) { g.DrawLine(pen, hist.X, hist.Y, hist.X, hist.Bottom); g.DrawLine(pen, hist.Right, hist.Y, hist.Right, hist.Bottom); }
                // out = in: where the curve would run with no compressor
                using (var dash = new Pen(Theme.BorderStrong) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, c.X, c.Bottom, c.Right, c.Y);
            }
            CeilFader(g, limC, lim, ch);

            // the curve
            if (doCurve) CurveBlock(g, c, true);

            // the last ten seconds
            if (hist.Width > 0)
            {
                if (doHist)
                {
                g.SetClip(hist);
                var gl = new GdiLive(g);
                if (ByFreq) PaintBands(g, hist, limC, lim, 7, gl);
                else HistTime(gl, hist, line, lit, limC, lim);
                g.Clip = clip;
                if (lim) using (var pen = new Pen(ch ? Color.FromArgb(0xf6, 0xd0, 0x7a) : limC, Theme.S(ch ? 2.2f : 1.5f))) g.DrawLine(pen, hist.X, cy, hist.Right, cy);
                }
                // which monitor: the two words at the right end of the line under it
                var vt = ViewRect(false);
                if (want.IntersectsWith(new Rectangle(hist.X - Theme.S(4), c.Bottom + 2, Width - hist.X + Theme.S(4), Theme.S(28))))
                {
                foreach (bool fr in new[] { false, true })
                {
                    var vr = ViewRect(fr); bool on = ByFreq == fr, vh = hot == (fr ? Part.ViewFreq : Part.ViewTime);
                    if (on || vh) Theme.Fill(g, vr, Theme.S(5), Color.FromArgb(on ? 46 : 22, Theme.Text));
                    Theme.Text1(g, fr ? "По частотам" : "Во времени", Theme.Small, vr, on ? Theme.Text : vh ? Theme.Text2 : Theme.Muted, Mid);
                }
                // what is what, under the plot
                int lx = hist.X, ly = c.Bottom + Theme.S(4), sw = Theme.S(10);
                var legend = new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>("На входе", Color.FromArgb(110, Theme.Text)), new KeyValuePair<string, Color>("На выходе", Theme.Text), new KeyValuePair<string, Color>("Сжатие", line), new KeyValuePair<string, Color>("Порог", line) };
                if (ByFreq) legend.RemoveAt(1); // the columns show what comes into each band, not what leaves
                if (lim) legend.Add(new KeyValuePair<string, Color>("Лимитер", limC));
                foreach (var it in legend)
                {
                    string name = L.T(it.Key);
                    int w = TextRenderer.MeasureText(g, name, Theme.Small, new Size(1000, 100), TextFormatFlags.NoPadding).Width + Theme.S(12); // Text1 draws with its own margins
                    if (lx + sw + Theme.S(6) + w > vt.X - Theme.S(8)) break;
                    if (it.Key == "На входе" || it.Key == "Сжатие" || it.Key == "Лимитер") Theme.Fill(g, new RectangleF(lx, ly + Theme.S(5), sw, Theme.S(8)), Theme.S(2), it.Value);
                    else using (var pen = new Pen(it.Value, Theme.S(1.6f)) { DashStyle = it.Key == "Порог" ? DashStyle.Dash : DashStyle.Solid }) g.DrawLine(pen, lx, ly + Theme.S(9), lx + sw, ly + Theme.S(9));
                    Theme.Text1(g, it.Key, Theme.Small, new Rectangle(lx + sw + Theme.S(6), ly, w, Theme.S(18)), Theme.Muted);
                    lx += sw + Theme.S(6) + w + Theme.S(8);
                }
                }
            }

            // the dials
            if (pn.Width > 0)
            {
                var parts = Dials;
                // a split that is not at work shows where the page keeps it, grey
                float sp1 = P.Split > 0 ? P.Split : Ghost1, sp2 = P.Split2 > 0 ? P.Split2 : Ghost2;
                string[] names = { "Атака", "Восстановление", "Колено", "Порог", "Степень сжатия", "Усиление после", "Граница низа", "Граница верха" };
                string[] values = { (int)Math.Round(Q.Attack) + " мс", (int)Math.Round(Q.Release) + " мс", (int)Math.Round(Q.Knee) + " дБ", DbText(Q.Thr), RatioText, Plus(Q.Makeup), EqGraph.Hz(sp1), EqGraph.Hz(sp2) };
                if (P.CompChar == 2) values[1] = L.T("своё");      // the optical character's own two-stage release
                if (P.CompChar == 3) values[2] = L.T("широкое");   // the tube character's own wide knee
                float[] fr = { (float)(Math.Log(Math.Max(1, Q.Attack)) / Math.Log(200)), (float)(Math.Log(Math.Max(10, Q.Release) / 10) / Math.Log(200)), Q.Knee / 24f,
                    (Q.Thr + 60) / 60f, (float)(Math.Log(Math.Max(1, Q.Ratio)) / Math.Log(20)), (Q.Makeup + 12) / 36f,
                    (float)(Math.Log(Math.Max(80, sp1) / 80) / Math.Log(600 / 80.0)), (float)(Math.Log(Math.Max(1200, sp2) / 1200) / Math.Log(10)) };
                // in four rows a dial's name and value keep closer to it, so the room is between the dials, not inside one
                // (with the name 42 over the middle and the value 21 under it, one dial's value touched the next one's name)
                bool four = DialCols == 2; int nameUp = Theme.S(four ? 38 : 42), valueDown = Theme.S(four ? 19 : 21); float dialR = four ? 16f : 17f;
                for (int k = 0; k < DialCount; k++)
                {
                    bool off = Dim || !DialOn(k), h = !off && (hot == parts[k] || drag == parts[k]); var at = DialAt(k); var cell = DialCell(k);
                    if (!want.IntersectsWith(new Rectangle(cell.X, at.Y - nameUp - Theme.S(2), cell.Width, nameUp + valueDown + Theme.S(26)))) continue;
                    // all eight in one colour while they work (the user: the splits too, grey only when not at work)
                    Color dc = h ? lit : line;
                    // 19.11 (the user's order): the name on top, the dial under it, the value under the dial, all on one middle
                    if (h) Theme.Fill(g, new RectangleF(cell.X, at.Y - nameUp - Theme.S(1), cell.Width, nameUp + valueDown + Theme.S(22)), Theme.S(6), Color.FromArgb(40, Theme.Text));
                    Theme.Text1(g, names[k], Theme.Small, new Rectangle(cell.X, at.Y - nameUp, cell.Width, Theme.S(20)), off ? Theme.Muted : Theme.Text2, Mid | TextFormatFlags.EndEllipsis);
                    EqGraph.Dial(g, at.X, at.Y, fr[k], off ? Theme.Muted : dc, h, off, dialR);
                    Theme.Text1(g, values[k], Theme.Small, new Rectangle(cell.X, at.Y + valueDown, cell.Width, Theme.S(20)), off ? Theme.Muted : Theme.Text, Mid);
                }
            }

            // the bands: how many, and the one being set
            var bc = BandsCol;
            if (bc.Width > 0 && want.IntersectsWith(bc))
            {
                int n = P.PartCount; var act = P.ActiveParts;
                int bandsTop = (int)Math.Round(BandsTopF);
                Theme.Text1(g, "Полосы", Theme.Small, new Rectangle(bc.X, bandsTop, bc.Width, Theme.S(20)), Dim ? Theme.Muted : Theme.Text2, Mid);
                for (int i = 0; i < 3; i++)
                {
                    var r = CountRect(i); var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                    bool on = i == n - 1, h = !Dim && hot == Part.Count && idxHot == i;
                    Theme.Fill(g, rf, Theme.S(6), Dim ? (on ? Theme.Raised : Color.FromArgb(14, Theme.Text)) : on ? Theme.AccentBg : h ? Theme.Raised : Color.FromArgb(14, Theme.Text));
                    Theme.Stroke(g, rf, Theme.S(6), on && !Dim ? Theme.Accent : Theme.Border);
                    Glyph(g, (i + 1).ToString(), Theme.Body, r, Dim ? Theme.Muted : on ? Theme.AccentFg : Theme.Text2);
                }
                Theme.Text1(g, "Настраиваемая", Theme.Small, new Rectangle(bc.X, bandsTop + Theme.S(BandsTitle2), bc.Width, Theme.S(20)), Dim ? Theme.Muted : Theme.Text2, Mid);
                for (int i = 0; i < 3; i++)
                {
                    float a = plate[i]; if (a < 0.03f) continue; // only the bands at work, and the one coming or going
                    var pr = PlateRect(i, true); var rf = new RectangleF(pr.X + 0.5f, pr.Y + 0.5f, pr.Width - 1f, Math.Max(1f, pr.Height - 1f));
                    bool at = act.Contains(i), on = at && (n == 1 || i == Band), h = !Dim && hot == Part.Pill && idxHot == i;
                    Color col = Dim ? Theme.Muted : PartColor[i];
                    Func<Color, Color> fade = c0 => Color.FromArgb((int)(c0.A * a), c0);
                    float rad = Math.Min(Theme.S(6), rf.Height / 2f);
                    Theme.Fill(g, rf, rad, fade(on && !Dim ? Color.FromArgb(58, col) : h ? Theme.Raised : Color.FromArgb(14, Theme.Text)));
                    Theme.Stroke(g, rf, rad, fade(on && !Dim ? col : Theme.Border));
                    float dot = Theme.S(8) * a;
                    using (var b = new SolidBrush(fade(col))) g.FillEllipse(b, pr.X + Theme.S(15) - dot / 2f, pr.Y + (pr.Height - dot) / 2f, dot, dot);
                    if (a > 0.5f) // the name comes in over the second half, from the colour of the card
                    {
                        Color ink = Dim ? Theme.Muted : on || h ? Theme.Text : Theme.Text2, from = Theme.Card; float t = (a - 0.5f) * 2f;
                        ink = Color.FromArgb((int)(from.R + (ink.R - from.R) * t), (int)(from.G + (ink.G - from.G) * t), (int)(from.B + (ink.B - from.B) * t));
                        Theme.Text1(g, n == 1 && i == 0 ? "Весь звук" : ZoneNames[i], Theme.Small, new Rectangle((int)pr.X + Theme.S(23), (int)Math.Round(pr.Y), (int)pr.Width - Theme.S(27), (int)Math.Round(pr.Height)), ink);
                        // listened to apart: the letter at the plate's end (seen also while the other monitor is shown)
                        bool quiet = (Silent >> i & 1) != 0, solo = !quiet && P.Comp && n > 1 && (SoloMask >> i & 1) != 0;
                        if ((quiet || solo) && a > 0.9f) Glyph(g, quiet ? "M" : "S", Theme.Small, new Rectangle((int)pr.Right - Theme.S(26), (int)Math.Round(pr.Y), Theme.S(22), (int)Math.Round(pr.Height)), quiet ? MuteC : SoloC);
                    }
                }
            }

            var shown = drag != Part.None ? drag : hot;
            string hint;
            if (shown == Part.Ceil) hint = "Потолок: " + CeilText + " · " + "тянется вверх-вниз";
            else if (shown == Part.CeilFader) hint = "Потолок: " + CeilText + " · " + L.T("Не выпускает звук выше потолка, сколько бы ни поднял эквалайзер: без хрипа и перегруза") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (Dim) hint = "Компрессор выключен";
            else if (shown == Part.Thr || shown == Part.Line) hint = "Порог: " + DbText(Q.Thr) + " · " + "колено: " + DbText(Q.Knee) + (shown == Part.Thr ? " · " + "колесо меняет колено" : "");
            else if (shown == Part.Ratio) hint = "Степень сжатия: " + RatioText + " · " + "тянется вверх-вниз";
            else if (shown == Part.Makeup) hint = "Усиление после: " + Plus(Q.Makeup) + " · " + "тянется вверх-вниз";
            else if (shown == Part.Attack) hint = "Атака: " + (int)Math.Round(Q.Attack) + " мс" + " · " + L.T("Как быстро компрессор берётся за громкий звук") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Release) hint = "Восстановление: " + (int)Math.Round(Q.Release) + " мс" + " · " + L.T("Как быстро он отпускает, когда стало тише") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Knee) hint = "Колено: " + DbText(Q.Knee) + " · " + L.T("Плавность перехода у порога: 0 — резкий") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialThr) hint = "Порог: " + DbText(Q.Thr) + " · " + L.T("Громкость, с которой начинается сжатие") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialRatio) hint = "Степень сжатия: " + RatioText + " · " + L.T("Во сколько раз убавляется то, что выше порога") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialMakeup) hint = "Усиление после: " + Plus(Q.Makeup) + " · " + L.T("Возвращает громкость, которую забрало сжатие") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Solo) hint = "Соло: слышны только полосы с «S». Это для прослушивания — снимется само, когда вы уйдёте со страницы";
            else if (shown == Part.Mute) hint = "Без звука: эта полоса не слышна, остальные играют. Это для прослушивания — снимется само, когда вы уйдёте со страницы";
            else if (shown == Part.DialSplit1) hint = "Граница низа: " + EqGraph.Hz(P.Split) + " · " + L.T("Всё, что ниже, — полоса «Низ»") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.DialSplit2) hint = "Граница верха: " + EqGraph.Hz(P.Split2) + " · " + L.T("Всё, что выше, — полоса «Верх»") + " · " + "тяните вверх-вниз или крутите колесо";
            else if (shown == Part.Count) hint = "Каждая полоса сжимается порознь и со своими настройками";
            else if (shown == Part.Pill) hint = idxHot == Band || P.PartCount == 1 ? "Точки, ручки и ползунки относятся к этой полосе" : "Щёлкните, чтобы настраивать эту полосу";
            else if (shown == Part.Split1) hint = "Граница низа: " + EqGraph.Hz(P.Split) + " · " + "тянется влево-вправо";
            else if (shown == Part.Split2) hint = "Граница верха: " + EqGraph.Hz(P.Split2) + " · " + "тянется влево-вправо";
            else if (shown == Part.Zone && P.PartCount == 1) hint = "Одна полоса на весь звук. Разделить его на две или три можно в «Полосах сжатия» ниже";
            else if (shown == Part.Zone && zoneHot != Band) hint = "Щёлкните, чтобы настраивать эту полосу";
            else if (shown == Part.Zone || (ByFreq && shown == Part.ViewFreq)) hint = "Щёлкните по полосе, чтобы настраивать её. Границы полос тянутся влево-вправо, пунктир порога — вверх-вниз";
            else if (Silent != 0) hint = "Сейчас слышны не все полосы: включено прослушивание (S или M на мониторе «По частотам»)";
            else hint = "Точки тянутся мышью: порог — влево-вправо, степень сжатия (справа) и усиление после (слева) — вверх-вниз. Колесо на точке порога меняет колено";
            int hintX = bc.Width > 0 ? bc.X : c.X;
            var hintR = new Rectangle(hintX, c.Bottom + Theme.S(28), Width - hintX - Theme.S(14), Theme.S(22));
            if (want.IntersectsWith(hintR)) Theme.Text1(g, hint, Theme.Small, hintR, Theme.Text2, TextFormatFlags.EndEllipsis);
        }
    }
}
