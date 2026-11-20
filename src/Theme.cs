using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RedTune
{
    static class Theme
    {
        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        // 20.11: whole wheel notches in what the mouse sent (120 = one notch). A smooth wheel or a touchpad sends a notch in
        // several small parts; the parts are kept in acc until they make a notch. A turn the other way starts afresh.
        // 20.11 (the user): a press on a fader's column does not move its knob, the mouse pointer goes onto the knob
        // instead, ready to drag it. False in the checks, which must not move the real pointer. Returns whether it moved.
        public static bool WarpPointer = true;
        public static bool Warp(Control c, float x, float y)
        {
            if (!WarpPointer) return false;
            try { Cursor.Position = c.PointToScreen(new Point((int)Math.Round(x), (int)Math.Round(y))); return true; } catch { return false; }
        }

        public static int WheelNotches(ref int acc, int delta)
        {
            if (acc != 0 && Math.Sign(acc) != Math.Sign(delta)) acc = 0;
            acc += delta;
            int n = acc / 120; acc -= n * 120;
            return n;
        }

        // 13.11 (Red Tune): near-neutral dark greys with a trace of warmth, off-white text and a red accent, cards drawn
        // as glass. The red is a little softer than a pure signal red: it fills toggles, sliders and whole buttons, and
        // at full saturation that much of it glares on a dark page. Red being the brand, "wrong" is orange (Danger).
        public static readonly Color Window = Color.FromArgb(0x1d, 0x1c, 0x1c);
        public static readonly Color Nav = Color.FromArgb(0x17, 0x16, 0x16);
        public static readonly Color Card = Color.FromArgb(0x29, 0x28, 0x28);
        public static readonly Color Raised = Color.FromArgb(0x38, 0x36, 0x36);
        public static readonly Color Border = Color.FromArgb(0x3f, 0x3c, 0x3c);
        public static readonly Color BorderStrong = Color.FromArgb(0x5c, 0x57, 0x57);
        public static readonly Color Text = Color.FromArgb(0xfa, 0xf8, 0xf6);
        public static readonly Color Text2 = Color.FromArgb(0xbf, 0xba, 0xb7);
        public static readonly Color Muted = Color.FromArgb(0x86, 0x80, 0x7e);
        public static readonly Color Accent = Color.FromArgb(0xe0, 0x3a, 0x40);
        public static readonly Color AccentHover = Color.FromArgb(0xec, 0x55, 0x5a);
        public static readonly Color AccentBg = Color.FromArgb(0x4a, 0x21, 0x25);
        public static readonly Color AccentFg = Color.FromArgb(0xf7, 0xb0, 0xb2);
        public static readonly Color Success = Color.FromArgb(0x6f, 0xc2, 0x95);
        public static readonly Color SuccessBg = Color.FromArgb(0x1f, 0x35, 0x28);
        public static readonly Color Warning = Color.FromArgb(0xe6, 0xbc, 0x62);
        public static readonly Color WarningBg = Color.FromArgb(0x3a, 0x31, 0x1a);
        public static readonly Color Danger = Color.FromArgb(0xff, 0x8a, 0x4c);

        public static Font Body, Small, Bold, Title, Big, Mono;

        // zoom: the app's own scale in percent; fonts in points already follow the Windows DPI, sizes in pixels get both
        public static void Init(int zoom)
        {
            float z = zoom / 100f;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f * z;
            Body = new Font("Segoe UI", 9.75f * z);
            Small = new Font("Segoe UI", 8.25f * z);
            Bold = new Font("Segoe UI Semibold", 9.75f * z);
            Title = new Font("Segoe UI Semibold", 11f * z);
            Big = new Font("Segoe UI Semibold", 14f * z);
            Mono = new Font("Consolas", 9f * z);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- glass ----------
        // Every card goes through Fill(Card) + Stroke(Border), so the glass look lives here: the light comes from above and
        // below the pane, so its top and bottom edges shine and the sides stay dim; a soft glow sits just inside those two
        // edges and fades towards the sides. The fill itself stays flat: buttons and toggles on a card are separate
        // windows painted in the plain card colour, and a gradient under them would show as patches.

        static bool Same(Color a, Color b) { return a.ToArgb() == b.ToArgb(); }
        static bool Pane(RectangleF r) { return r.Height >= S(44); }                 // a card, not a button or a pill
        static float Rad(RectangleF r, float rad) { return Pane(r) ? rad * 1.5f : rad; } // cards get rounder corners
        static Color W(int alpha) { return Color.FromArgb(alpha, 255, 255, 255); }

        // 14.11: the filled part of the bars, sliders and rings runs from a deep wine red where it starts to a bright coral at
        // its end, so the end of every bar is its brightest point. The warning colour gets the same treatment in amber.
        public static readonly Color GradFrom = Color.FromArgb(0x8e, 0x1f, 0x2a), GradTo = Color.FromArgb(0xff, 0x6b, 0x5e);
        public static readonly Color WarnFrom = Color.FromArgb(0xa8, 0x6a, 0x1e), WarnTo = Color.FromArgb(0xf6, 0xd0, 0x7a);
        public static Color GradAt(float t, bool warn = false)
        {
            Color a = warn ? WarnFrom : GradFrom, b = warn ? WarnTo : GradTo;
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // 14.11 (3): the gradient belongs to the whole scale, not to the filled part: the colour says how high the value is.
        // A low value is a short dark-red bar, the closer to the top of the scale the brighter its end. Most of the change
        // happens in the upper part (the same curve for bars, sliders and rings).
        public static float Level(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            float[] p = { 0f, 0.35f, 0.75f, 1f }, f = { 0f, 0.18f, 0.55f, 1f };
            for (int i = 1; i < p.Length; i++) if (t <= p[i]) return f[i - 1] + (f[i] - f[i - 1]) * (t - p[i - 1]) / (p[i] - p[i - 1]);
            return 1f;
        }
        public static Color Heat(float t, bool warn = false) { return GradAt(Level(t), warn); }

        static Color Mix(Color a, Color b, float t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }

        // 14.11: the knob of a slider is a piece of thick glass, a round dish pressed inwards. What makes it read so:
        //  · it darkens what is under it, more at the top (the upper rim shades the hollow) and less at the bottom;
        //  · the bar goes on inside it, thinner, a touch lower and brighter, and stops just past the middle (bent by the
        //    glass), with its light spread over the left half;
        //  · a bright thin rim, strongest where the light comes from (top left) and where it leaves (bottom right),
        //    and a crescent of light inside the lower right edge;
        //  · a soft shadow under it, so it sits on the card.
        // lit: how much of the bar's light is in the knob, 0..1. At 0 (a switch that is off, a slider at its start) the
        // glass is not empty: a short pale streak lies in its middle (14.11). As the light comes, the streak grows to the
        // left edge, where the bar enters, and takes its colour.
        // from: the side the bar comes into the knob from, in degrees (180 = from the left, as in a slider; 90 = from
        // below, 270 = from above, for the upright bars of «Кривая»).
        // through: a knob on a line of a graph (14.11). The streak from the left was a fake there: the line comes to the
        // point at its own angle and goes on at another. With through the glass shows the line itself, the piece from the
        // point before to this one and on to the point after, thinner and brighter, as seen through thick glass; null for
        // an end of the line that has no neighbour.
        // reach: how long the bar outside really is, from the knob's middle back to where it starts (the per-core bars of
        // «Кривая», 14.11). Without it a core one step from zero showed a full streak of light coming from the edge of the
        // glass, with no bar outside to bring it. With it the streak in the glass is as long as the bar: it grows out of
        // the middle towards the zero line as the knob leaves it, while the pale dash of «zero» fades.
        // under: a knob that lies on a plot where a curve runs its own way (the equalizer, 19.11: a band's knob marks the
        // band's frequency and gain, and the curve, being the sum of all bands, may pass through it, beside it or far
        // from it). The glass then shows what is really under it: the piece of that curve inside its circle, thinner
        // and brighter, with its light around it; and nothing at all when the curve passes the knob by. No streak is made up.
        public static void GlassKnob(Graphics g, float cx, float cy, float d, float lit, Color light, float barH, bool dim, float from = 180f, PointF?[] through = null, float reach = -1f, PointF[] under = null)
        {
            float r = d / 2f, px = Scale;
            var circle = new RectangleF(cx - r, cy - r, d, d);
            using (var sp = new GraphicsPath())
            {
                float sr = r + 3f * px;
                sp.AddEllipse(cx - sr, cy - sr + 1.8f * px, sr * 2, sr * 2);
                using (var sb = new PathGradientBrush(sp) { CenterColor = Color.FromArgb(130, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) }, FocusScales = new PointF(0.7f, 0.7f) })
                    g.FillPath(sb, sp);
            }
            using (var body = new LinearGradientBrush(new RectangleF(circle.X, circle.Y - 1, d, d + 2), Mix(Card, Color.Black, 0.3f), Mix(Card, Color.White, 0.13f), 90f))
                g.FillEllipse(body, circle);
            var st = g.Save();
            using (var cp = new GraphicsPath())
            {
                cp.AddEllipse(circle);
                g.SetClip(cp, CombineMode.Intersect);
                lit = Math.Max(0, Math.Min(1, lit));
                if (through != null)
                {
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(cx - r * 1.25f, cy - r * 1.25f, r * 2.5f, r * 2.5f);
                        using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(dim ? 60 : 120, Mix(light, Color.White, 0.12f)), SurroundColors = new[] { Color.FromArgb(0, light) } })
                            g.FillPath(pb, gp);
                    }
                    using (var pen = new Pen(Mix(light, Color.White, dim ? 0.2f : 0.34f), Math.Max(1.2f, barH * 0.62f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        var c = new PointF(cx, cy + 0.5f * px);
                        var pts = new System.Collections.Generic.List<PointF>();
                        if (through.Length > 0 && through[0].HasValue) pts.Add(Toward(c, through[0].Value, d));
                        pts.Add(c);
                        if (through.Length > 1 && through[1].HasValue) pts.Add(Toward(c, through[1].Value, d));
                        if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                    }
                }
                if (under != null)
                {
                    // where the curve comes nearest to the knob's middle, and how near
                    float best = float.MaxValue; var at = new PointF(cx, cy);
                    for (int i = 0; i + 1 < under.Length; i++)
                    {
                        float ax = under[i].X, ay = under[i].Y, bx = under[i + 1].X - ax, by = under[i + 1].Y - ay, len2 = bx * bx + by * by;
                        float t = len2 > 1e-6f ? Math.Max(0f, Math.Min(1f, ((cx - ax) * bx + (cy - ay) * by) / len2)) : 0f;
                        float qx = ax + bx * t, qy = ay + by * t, dist = (float)Math.Sqrt((qx - cx) * (qx - cx) + (qy - cy) * (qy - cy));
                        if (dist < best) { best = dist; at = new PointF(qx, qy); }
                    }
                    if (best < r && under.Length > 1)
                    {
                        // the light is where the line is, and weaker as the line leaves the glass
                        float near = Math.Max(0f, Math.Min(1f, 1.4f * (1f - best / r)));
                        using (var gp = new GraphicsPath())
                        {
                            gp.AddEllipse(at.X - r * 1.25f, at.Y - r * 1.25f, r * 2.5f, r * 2.5f);
                            using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)((dim ? 60 : 120) * near), Mix(light, Color.White, 0.12f)), SurroundColors = new[] { Color.FromArgb(0, light) } })
                                g.FillPath(pb, gp);
                        }
                        using (var pen = new Pen(Mix(light, Color.White, dim ? 0.2f : 0.34f), Math.Max(1.2f, barH * 0.62f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            var pts = new PointF[under.Length];
                            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(under[i].X, under[i].Y + 0.5f * px);
                            g.DrawLines(pen, pts);
                        }
                    }
                }
                if (reach >= 0f && through == null)
                {
                    lit = Math.Min(lit, Math.Max(0f, Math.Min(1f, reach / (r + barH * 0.62f))));
                    // the dash of «zero», level, fading as the bar grows
                    float h0 = barH * 0.62f, half0 = r * 0.42f;
                    var pale0 = Mix(Card, Color.White, dim ? 0.22f : 0.42f);
                    using (var p = Round(new RectangleF(cx - half0, cy + 0.7f * px - h0 / 2f, half0 * 2, h0), h0 / 2f))
                    using (var b = new SolidBrush(Color.FromArgb((int)(255 * (1f - lit)), pale0)))
                        g.FillPath(b, p);
                }
                var turn = g.Save();
                if (from != 180f) { g.TranslateTransform(cx, cy); g.RotateTransform(from - 180f); g.TranslateTransform(-cx, -cy); }
                if (lit > 0.01f && through == null && under == null)
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(cx - r * 1.7f, cy - r * 0.95f, r * 2.5f, r * 1.9f);
                        using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(150 * lit), Mix(light, Color.White, 0.12f)), SurroundColors = new[] { Color.FromArgb(0, light) } })
                            g.FillPath(pb, gp);
                    }
                if (through == null && reach >= 0f)
                {
                    // the bar as far as it goes: from the middle back along itself, never longer than it is outside
                    float h = barH * 0.62f, back = Math.Min(r + h, reach);
                    if (back > 0.5f)
                        using (var p = Round(new RectangleF(cx - back, cy - h / 2f, back + Math.Max(h / 2f, r * 0.2f * lit), h), h / 2f))
                        using (var b = new SolidBrush(Mix(light, Color.White, 0.34f)))
                            g.FillPath(b, p);
                }
                else if (through == null && under == null)
                {
                    float h = barH * 0.62f, y = cy + 0.7f * px - h / 2f, half = r * 0.42f;
                    float xs = (cx - half) + ((cx - r - h) - (cx - half)) * lit, xe = (cx + half) + ((cx + r * 0.2f) - (cx + half)) * lit;
                    var pale = Mix(Card, Color.White, dim ? 0.22f : 0.42f);
                    using (var p = Round(new RectangleF(xs, y, xe - xs, h), h / 2f))
                    using (var b = new SolidBrush(Mix(pale, Mix(light, Color.White, 0.34f), lit)))
                        g.FillPath(b, p);
                }
                g.Restore(turn);
                using (var ip = new GraphicsPath())
                {
                    ip.AddEllipse(RectangleF.Inflate(circle, 1, 1));
                    using (var ib = new PathGradientBrush(ip) { CenterPoint = new PointF(cx, cy + r * 0.5f), CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(105, 0, 0, 0) }, FocusScales = new PointF(0.3f, 0.3f) })
                        g.FillPath(ib, ip);
                }
            }
            g.Restore(st);
            float ir = r - 2.3f * px;
            using (var pen = new Pen(W(dim ? 26 : 50), 1.3f * px) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, cx - ir, cy - ir, ir * 2, ir * 2, 8, 112);
            using (var pen = new Pen(W(dim ? 55 : 125), 1.3f * px) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, cx - ir, cy - ir, ir * 2, ir * 2, 28, 68);
            using (var rb = new LinearGradientBrush(RectangleF.Inflate(circle, 2, 2), Color.White, Color.White, 45f))
            {
                int k = dim ? 2 : 1;
                rb.InterpolationColors = new ColorBlend { Colors = new[] { W(235 / k), W(105 / k), W(80 / k), W(190 / k) }, Positions = new[] { 0f, 0.42f, 0.62f, 1f } };
                using (var pen = new Pen(rb, 1.35f * px)) g.DrawEllipse(pen, circle);
            }
        }

        // 16.11: a button of the same glass as the slider knobs (the core switches under «Кривые всех ядер»): a soft shadow
        // under it, a body darker at the top, an inner shade, the bright rim strongest top left and bottom right. lit 0..1:
        // the light inside, coming from below, with a bright dash near the lower edge; unlit, the dash is short and pale.
        public static void GlassButton(Graphics g, RectangleF r, float rad, float lit, Color light)
        {
            float px = Scale;
            lit = Math.Max(0, Math.Min(1, lit));
            var sh = new RectangleF(r.X - 2f * px, r.Y - 0.5f * px, r.Width + 4f * px, r.Height + 4.5f * px);
            using (var sp = Round(sh, rad + 2f * px))
            using (var sb = new PathGradientBrush(sp) { CenterColor = Color.FromArgb(120, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) }, FocusScales = new PointF(0.8f, 0.6f) })
                g.FillPath(sb, sp);
            using (var path = Round(r, rad))
            {
                using (var body = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Mix(Card, Color.Black, 0.3f), Mix(Card, Color.White, 0.13f), 90f))
                    g.FillPath(body, path);
                var st = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                float cx = r.X + r.Width / 2f;
                if (lit > 0.01f)
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(cx - r.Width * 0.75f, r.Y + r.Height * 0.15f, r.Width * 1.5f, r.Height * 1.5f);
                        using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(190 * lit), Mix(light, Color.White, 0.1f)), SurroundColors = new[] { Color.FromArgb(0, light) } })
                            g.FillPath(pb, gp);
                    }
                // the dash: short and pale when dark, as wide as the light and in its colour when lit
                float dh = 2.4f * px, half = r.Width * (0.16f + 0.2f * lit), dy = r.Bottom - 5.5f * px;
                var pale = Mix(Card, Color.White, 0.3f);
                using (var dp = Round(new RectangleF(cx - half, dy - dh / 2f, half * 2, dh), dh / 2f))
                using (var db = new SolidBrush(Mix(pale, Mix(light, Color.White, 0.4f), lit)))
                    g.FillPath(db, dp);
                using (var ip = Round(RectangleF.Inflate(r, 1, 1), rad + 1))
                using (var ib = new PathGradientBrush(ip) { CenterPoint = new PointF(cx, r.Y + r.Height * 0.7f), CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(95, 0, 0, 0) }, FocusScales = new PointF(0.75f, 0.4f) })
                    g.FillPath(ib, ip);
                g.Restore(st);
                using (var rb = new LinearGradientBrush(RectangleF.Inflate(r, 2, 2), Color.White, Color.White, 45f))
                {
                    float k = 2f - lit; // the rim of a dark button is half as bright
                    rb.InterpolationColors = new ColorBlend { Colors = new[] { W((int)(235 / k)), W((int)(105 / k)), W((int)(80 / k)), W((int)(190 / k)) }, Positions = new[] { 0f, 0.42f, 0.62f, 1f } };
                    using (var pen = new Pen(rb, 1.35f * px)) g.DrawPath(pen, path);
                }
            }
        }

        // 16.11: a round lamp button, after the lit push buttons of studio gear (the core switches under «Кривые всех ядер»):
        // a dark socket sunk into the panel, in it a glass lens seen as rings (the rim, an inner ring, a crescent of
        // reflected light lower left), and the core in the middle. Dark: the core is black, the rings are grey glass.
        // lit 0..1: the core burns nearly white in the light's colour, the rings and the crescent take the colour, the lens
        // fills with it and a faint halo falls on the panel around the socket.
        // press 0..1: the button pushed in: the lens with everything in it goes deeper into the socket (drawn smaller)
        // and into its shade (darker), the socket itself stays.
        public static void LensButton(Graphics g, float cx, float cy, float d, float lit, Color light, float press = 0f)
        {
            float r = d / 2f, px = Scale;
            lit = Math.Max(0, Math.Min(1, lit)); press = Math.Max(0, Math.Min(1, press));
            Func<float, RectangleF> circle = rad => new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2);
            Func<Color, int, Color> a = (c, alpha) => Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c);
            if (lit > 0.01f)
                using (var hp = new GraphicsPath())
                {
                    hp.AddEllipse(circle(r * 1.5f));
                    using (var hb = new PathGradientBrush(hp) { CenterColor = a(light, (int)(70 * lit * (1f - 0.4f * press))), SurroundColors = new[] { a(light, 0) }, FocusScales = new PointF(0.55f, 0.55f) })
                        g.FillPath(hb, hp);
                }
            // the socket: a black hole whose lower edge catches a little light, the upper one is in shade
            using (var sb = new SolidBrush(Mix(Mix(Card, Color.Black, 0.78f), Mix(light, Color.Black, 0.72f), lit)))
                g.FillEllipse(sb, circle(r));
            using (var rim = new LinearGradientBrush(RectangleF.Inflate(circle(r), 1, 1), Color.FromArgb(150, 0, 0, 0), W(60), 90f))
            using (var pen = new Pen(rim, 1.2f * px))
                g.DrawEllipse(pen, circle(r));
            // the lens; pushed in, it is smaller and shaded
            var pushed = g.Save();
            float deep = 1f - 0.14f * press;
            g.TranslateTransform(cx, cy); g.ScaleTransform(deep, deep); g.TranslateTransform(-cx, -cy);
            float lr = r * 0.74f;
            using (var lp = new GraphicsPath())
            {
                lp.AddEllipse(circle(lr));
                var off0 = Mix(Card, Color.White, 0.10f); var off1 = Mix(Card, Color.Black, 0.35f);
                using (var lb = new PathGradientBrush(lp)
                {
                    CenterPoint = new PointF(cx - lr * 0.15f, cy + lr * 0.2f),
                    CenterColor = Mix(off0, light, lit),
                    SurroundColors = new[] { Mix(off1, Mix(light, Color.Black, 0.5f), lit) },
                })
                    g.FillPath(lb, lp);
            }
            var ringCol = Mix(Color.FromArgb(200, 200, 200), Mix(light, Color.White, 0.3f), lit);
            using (var pen = new Pen(a(ringCol, (int)(110 + 120 * lit)), 1.15f * px)) g.DrawEllipse(pen, circle(lr));
            using (var pen = new Pen(a(ringCol, (int)(55 + 110 * lit)), 1f * px)) g.DrawEllipse(pen, circle(r * 0.60f));
            // the crescent of reflected light inside the glass, lower left; a thin one opposite it, upper right
            using (var pen = new Pen(a(Mix(Color.White, Mix(light, Color.White, 0.7f), lit), (int)(150 + 80 * lit)), r * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, circle(r * 0.60f), 95, 110);
            using (var pen = new Pen(a(Mix(Color.White, Mix(light, Color.White, 0.7f), lit), (int)(60 + 90 * lit)), r * 0.06f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, circle(r * 0.66f), 295, 70);
            // the core, a little off the middle like a lamp seen through thick glass
            float kr = r * 0.40f, kx = cx + r * 0.04f, ky = cy + r * 0.05f;
            using (var kp = new GraphicsPath())
            {
                kp.AddEllipse(kx - kr, ky - kr, kr * 2, kr * 2);
                var dark = Mix(Card, Color.Black, 0.82f);
                using (var kb = new PathGradientBrush(kp)
                {
                    CenterColor = Mix(Mix(dark, Color.White, 0.04f), Mix(light, Color.White, 0.8f), lit),
                    SurroundColors = new[] { Mix(dark, Mix(light, Color.White, 0.12f), lit) },
                    FocusScales = new PointF(0.55f, 0.55f),
                })
                    g.FillPath(kb, kp);
            }
            if (lit > 0.01f)
                using (var gp = new GraphicsPath())
                {
                    // the light spilling from the core over the rings
                    gp.AddEllipse(circle(lr));
                    using (var gb = new PathGradientBrush(gp) { CenterColor = a(light, (int)(110 * lit)), SurroundColors = new[] { a(light, 0) } })
                        g.FillPath(gb, gp);
                }
            if (press > 0.01f)
                using (var shade = new SolidBrush(Color.FromArgb((int)(95 * press), 0, 0, 0)))
                    g.FillEllipse(shade, RectangleF.Inflate(circle(lr), px, px));
            g.Restore(pushed);
        }

        // the point a knob's width from c on the way to p (far enough to leave the glass, whatever the real distance)
        static PointF Toward(PointF c, PointF p, float far)
        {
            float dx = p.X - c.X, dy = p.Y - c.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            return len < 0.01f ? c : new PointF(c.X + dx / len * far, c.Y + dy / len * far);
        }

        // an upright bar in the gradient («Кривая», 14.11): the colour belongs to the scale between yFrom (the zero line,
        // deep red) and yTo (the end of the scale, bright), so a core far from zero ends bright. pale: the lighter set
        // for a value that was changed and not applied yet.
        public static Color Pale(Color c) { return Mix(c, AccentFg, 0.5f); }
        public static void FillGradientV(Graphics g, RectangleF r, float rad, float yFrom, float yTo, bool pale)
        {
            if (r.Width < 0.5f || r.Height < 0.5f || Math.Abs(yTo - yFrom) < 1f) return;
            Color a = GradAt(0), b = GradAt(1);
            if (pale) { a = Pale(a); b = Pale(b); }
            using (var p = Round(r, rad))
            using (var br = new LinearGradientBrush(new PointF(r.X, yFrom), new PointF(r.X, yTo), a, b))
            {
                br.WrapMode = WrapMode.TileFlipXY; // nothing of the other end's colour past the ends
                br.Blend = new Blend { Factors = new[] { 0f, 0.18f, 0.55f, 1f }, Positions = new[] { 0f, 0.35f, 0.75f, 1f } };
                g.FillPath(br, p);
            }
        }

        // the empty part of a slider: a glass tube, dark inside with a thin light wall
        public static void Tube(Graphics g, RectangleF r)
        {
            using (var p = Round(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillPath(b, p);
                using (var lb = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), W(96), W(52), 90f))
                using (var pen = new Pen(lb, Math.Max(1f, 0.8f * Scale))) g.DrawPath(pen, p);
            }
        }

        // a filled bar in the gradient, rounded like Fill; a soft light at its end. full: the width of the whole scale
        // (the track), so the colour of the end is the colour of that point of the scale
        public static void FillGradient(Graphics g, RectangleF r, float rad, bool warn = false, float full = 0)
        {
            if (r.Width < 0.5f || r.Height < 0.5f) return;
            float span = Math.Max(r.Width, full > 0 ? full : r.Width);
            using (var p = Round(r, Rad(r, rad)))
            using (var b = new LinearGradientBrush(new RectangleF(r.X - 0.5f, r.Y, span + 1f, r.Height), GradAt(0, warn), GradAt(1, warn), 0f))
            {
                // the first third stays near the deep colour, most of the change is towards the end
                b.Blend = new Blend { Factors = new[] { 0f, 0.18f, 0.55f, 1f }, Positions = new[] { 0f, 0.35f, 0.75f, 1f } };
                g.FillPath(b, p);
            }
            // a glow around the end, a little wider than the bar is tall
            float gw = Math.Min(r.Width, r.Height * 4f), gh = r.Height * 3f;
            if (gw > r.Height)
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(r.Right - gw * 0.75f, r.Y + r.Height / 2f - gh / 2f, gw, gh);
                    var tip = Heat(r.Width / span, warn);
                    using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(70, tip), SurroundColors = new[] { Color.FromArgb(0, tip) } })
                        g.FillPath(pb, gp);
                }
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (var p = Round(r, Rad(r, rad)))
            {
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
                if (Same(c, Card) && Pane(r)) Sheen(g, p, r);
            }
        }

        // a see-through pane (the drop-downs, 13.11): the card colour at the given opacity over whatever is already
        // drawn (the blurred screen behind), a faint white frost, and the same sheen as a card
        public static void FillGlass(Graphics g, RectangleF r, float rad, int alpha)
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, Card))) g.FillRectangle(b, r);
            using (var b = new SolidBrush(W(10))) g.FillRectangle(b, r);
            using (var p = Round(r, Rad(r, rad))) if (Pane(r)) Sheen(g, p, r);
        }

        // 17.11: the light of a see-through pane alone, without the card colour (the mini panel's blocks put the colour
        // on themselves, thinner towards the edge): the faint white frost and the sheen. rad: the corner as drawn
        public static void GlassSheen(Graphics g, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            {
                using (var b = new SolidBrush(W(10))) g.FillPath(b, p);
                Sheen(g, p, r);
            }
        }

        // the glow inside the top and bottom edges: an ellipse of light centred on the edge, clipped to the pane
        static void Sheen(Graphics g, GraphicsPath pane, RectangleF r)
        {
            var st = g.Save();
            try
            {
                g.SetClip(pane, CombineMode.Intersect);
                // kept within 12 px of the edge: tabs and buttons on a card start below that
                // 17.11, after the mini panel's blocks: less light at the top (the glow was 58, the line 46), and at the
                // bottom, where a weaker glow was, the opposite: the edge goes into shade
                Glow(g, new RectangleF(r.X + r.Width * 0.03f, r.Y - S(12), r.Width * 0.94f, S(24)), 22);
                Glow(g, new RectangleF(r.X + r.Width * 0.04f, r.Bottom - S(11), r.Width * 0.92f, S(22)), 84, true);
                // a thin second line of light right under the top edge, gone before the corners
                float x0 = r.X + r.Width * 0.06f, x1 = r.Right - r.Width * 0.06f;
                if (x1 - x0 > 8)
                    using (var b = new LinearGradientBrush(new RectangleF(x0 - 1, r.Y, x1 - x0 + 2, 2), Color.White, Color.White, 0f))
                    {
                        b.InterpolationColors = new ColorBlend { Colors = new[] { W(0), W(18), W(18), W(0) }, Positions = new[] { 0f, 0.25f, 0.75f, 1f } };
                        using (var pen = new Pen(b)) g.DrawLine(pen, x0, r.Y + 1.5f, x1, r.Y + 1.5f);
                    }
            }
            finally { g.Restore(st); }
        }

        // light that is strongest in the middle of an ellipse and gone at its rim; dark: shade instead of light
        static void Glow(Graphics g, RectangleF e, int alpha, bool dark = false)
        {
            if (e.Width < 4 || e.Height < 2) return;
            using (var ep = new GraphicsPath())
            {
                ep.AddEllipse(e);
                Color mid = dark ? Color.FromArgb(alpha, 0, 0, 0) : W(alpha), rim = dark ? Color.FromArgb(0, 0, 0, 0) : W(0);
                using (var b = new PathGradientBrush(ep) { CenterColor = mid, SurroundColors = new[] { rim } }) g.FillPath(b, ep);
            }
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c)
        {
            using (var p = Round(r, Rad(r, rad)))
            {
                bool strong = Same(c, BorderStrong);
                if (strong || Same(c, Border)) { Rim(g, p, r, strong); return; }
                using (var pen = new Pen(c)) g.DrawPath(pen, p);
            }
        }

        // the edge of the glass: bright on top, a little less at the bottom, dim down the sides
        static void Rim(Graphics g, GraphicsPath p, RectangleF r, bool strong)
        {
            if (r.Height < 3) { using (var pen = new Pen(strong ? BorderStrong : Border)) g.DrawPath(pen, p); return; }
            float fade = Math.Min(0.45f, S(14) / r.Height); // the shine leaves the sides within a few pixels of the corner
            int top = strong ? 124 : 150, mid = strong ? 44 : 20, bottom = strong ? 84 : 100;
            using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.White, Color.White, 90f))
            {
                b.InterpolationColors = new ColorBlend { Colors = new[] { W(top), W(mid), W(mid), W(bottom) }, Positions = new[] { 0f, fade, 1f - fade, 1f } };
                using (var pen = new Pen(b)) g.DrawPath(pen, p);
            }
        }

        // one size for every glass knob (sliders and switches, 14.11)
        public static int KnobSize { get { return S(20); } }
        public static Size ToggleSize { get { return new Size(S(42), S(26)); } }

        // 14.11: a switch is made like a slider: a short glass tube and the same glass knob. Off: the tube is empty and the
        // knob on the left is clear. On: the tube is full of red light, which goes on into the knob on the right.
        public static void DrawToggle(Graphics g, RectangleF r, bool on) { DrawToggle(g, r, on ? 1f : 0f); }

        // t: where the knob is on its way, 0 = off (left) .. 1 = on (right); in between while it moves (ToggleAnim). The
        // red light fills the tube behind the knob and comes up as it goes.
        public static void DrawToggle(Graphics g, RectangleF r, float t)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            t = Math.Max(0, Math.Min(1, t));
            float d = KnobSize, th = S(13), cy = r.Y + r.Height / 2f;
            float left = r.X + S(3) + d / 2f, right = r.Right - S(3) - d / 2f; // the knob's two places
            float kx = left + (right - left) * t;
            var pill = new RectangleF(left - th / 2f, cy - th / 2f, right - left + th, th);
            Tube(g, pill);
            if (t > 0.01f)
            {
                var lit = RectangleF.Inflate(pill, -1f, -1f);
                int a = (int)(255 * Math.Min(1f, t * 1.6f));
                var part = new RectangleF(lit.X, lit.Y, Math.Min(lit.Width, kx - lit.X + lit.Height / 2f), lit.Height);
                using (var p = Round(part, part.Height / 2f))
                using (var b = new LinearGradientBrush(RectangleF.Inflate(lit, 1, 1), Color.FromArgb(a, Heat(0.45f)), Color.FromArgb(a, Heat(1f)), 0f))
                    g.FillPath(b, p);
            }
            GlassKnob(g, kx, cy, d, t, Heat(0.86f), S(7), false);
        }

        public static void Text1(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0)
        {
            TextRenderer.DrawText(g, L.T(s), f, r, c, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | extra);
        }

        public static int Measure(Graphics g, string s, Font f)
        {
            return TextRenderer.MeasureText(g, L.T(s), f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        }

        // load ring: track, arc from the top clockwise, value and caption in the middle
        public static void DrawRing(Graphics g, RectangleF r, float fraction, string value, string caption)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = S(7);
            var a = new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w);
            // 14.11 (5), after a rod and a hollow that looked better in the head than on the screen: flat and clean. A thin
            // quiet track; over it a wider arc in the gradient of the whole circle (dark red at the top, brighter the
            // further round), cut straight at both ends; and at its end a fine light tick across it, a little longer than
            // the arc is wide, like the needle of a gauge, with a soft glow. The eye finds the value by the tick.
            using (var pen = new Pen(Border, Math.Max(2f, 2.4f * Scale))) g.DrawEllipse(pen, a);
            fraction = Math.Max(0, Math.Min(1, fraction));
            if (fraction > 0.003f)
            {
                float sweep = 360 * fraction;
                float cx = a.X + a.Width / 2f, cy0 = a.Y + a.Height / 2f, rr = a.Width / 2f;
                using (var pen = new Pen(Color.Black, w) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                using (var path = new GraphicsPath())
                {
                    path.AddArc(a, -90, sweep);
                    path.Widen(pen);
                    var box = Rectangle.Inflate(Rectangle.Ceiling(path.GetBounds()), 2, 2);
                    using (var pic = Conic(box, cx, cy0, sweep))
                    using (var tb = new TextureBrush(pic, WrapMode.Clamp))
                    {
                        tb.TranslateTransform(box.X, box.Y);
                        g.FillPath(tb, path);
                    }
                }
                double end = (-90 + sweep) * Math.PI / 180;
                float ux = (float)Math.Cos(end), uy = (float)Math.Sin(end), ex = cx + rr * ux, ey = cy0 + rr * uy;
                float gr = w * 1.5f;
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(ex - gr, ey - gr, gr * 2, gr * 2);
                    using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(80, Heat(fraction)), SurroundColors = new[] { Color.FromArgb(0, Heat(fraction)) } })
                        g.FillPath(pb, gp);
                }
                float half = w / 2f + 2.2f * Scale;
                using (var pen = new Pen(Mix(Heat(fraction), Color.White, 0.78f), Math.Max(1.5f, 1.5f * Scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, ex - ux * half, ey - uy * half, ex + ux * half, ey + uy * half);
            }
            // the value and the caption as one block centred in the ring: from the top of the digits to the baseline of the
            // caption (its descenders, «з», «у», hang below and don't count for the eye). Measured on 13.11: the block sat
            // 7–9 px low; the rows below put it on the centre, with the caption 2 px closer to the value.
            float cy = r.Y + r.Height / 2f;
            // TextRenderer also centres the text 2 px left of the middle (measured): that goes back
            int tx = (int)Math.Round(r.X + 1.6f * Scale), tw = (int)Math.Round(r.Width);
            var vr = new Rectangle(tx, (int)Math.Round(cy - 19.4f * Scale), tw, S(24));
            TextRenderer.DrawText(g, value, Big, vr, Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var cr = new Rectangle(tx, (int)Math.Round(cy + 1.1f * Scale), tw, S(16));
            TextRenderer.DrawText(g, L.T(caption), Small, cr, Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // the ring's colours as a picture over the box: each pixel coloured by its angle from the top, clockwise. Pixels just
        // before the top (the round start) count as the start; the colour is that point of the whole circle.
        static Bitmap Conic(Rectangle box, float cx, float cy, float sweep)
        {
            var bmp = new Bitmap(Math.Max(1, box.Width), Math.Max(1, box.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                var row = new int[bmp.Width];
                float gap = (360f - sweep) / 2f;
                for (int y = 0; y < bmp.Height; y++)
                {
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        double deg = Math.Atan2(box.Y + y + 0.5 - cy, box.X + x + 0.5 - cx) * 180 / Math.PI + 90; // 0 = top, clockwise
                        if (deg < 0) deg += 360;
                        if (deg > sweep + gap) deg -= 360; // just before the top: the start
                        row[x] = Heat((float)(deg / 360)).ToArgb();
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }
    }

    // 14.11: the move of a switch's knob. It starts slowly, speeds up and comes to a soft stop (ease-in-out, cubic),
    // about a quarter of a second for the whole way; a switch flipped back half-way turns round from where it is.
    class ToggleAnim : IDisposable
    {
        readonly Control owner;
        readonly Func<Rectangle> area; // the part of the owner to redraw; null = all of it
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        // 20.11: the frames come from a pool timer as posted messages, each painted at once. A WinForms Timer is the
        // last thing Windows hands out (after every paint), so beside the sound page's live pictures the slide got half
        // its frames with gaps of 50-60 ms and looked slower than on the other pages (check: --toggle-frames).
        readonly System.Threading.Timer timer;
        volatile bool running;
        int posted; // a frame is waiting in the queue: no second one (a busy window must not pile them up)
        float from, to, pos;
        bool target;
        const float Ms = 260f;

        public ToggleAnim(Control owner, bool on, Func<Rectangle> area = null)
        {
            this.owner = owner; this.area = area; target = on; pos = to = on ? 1f : 0f;
            timer = new System.Threading.Timer(_ => Post(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        void Post()
        {
            if (!running || System.Threading.Interlocked.Exchange(ref posted, 1) == 1) return;
            try { owner.BeginInvoke((Action)(() => { posted = 0; if (running) Step(); })); }
            catch { posted = 0; } // the window is gone or not made yet
        }

        void Start() { running = true; timer.Change(0, 15); }
        void Stop() { running = false; timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); }

        public float Pos { get { return pos; } }

        // no move: a value that is loaded, not switched by hand
        public void Snap(bool on) { Stop(); target = on; pos = to = on ? 1f : 0f; }

        public void Go(bool on)
        {
            if (on == target) return;
            target = on; from = pos; to = on ? 1f : 0f;
            if (!owner.IsHandleCreated || !owner.Visible) { pos = to; return; }
            clock.Restart(); Start();
        }

        void Step()
        {
            if (owner.IsDisposed) { Stop(); return; }
            float t = Math.Min(1f, clock.ElapsedMilliseconds / (Ms * Math.Max(0.35f, Math.Abs(to - from))));
            float e = t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f;
            pos = from + (to - from) * e;
            if (t >= 1f) { pos = to; Stop(); }
            if (area != null) owner.Invalidate(area()); else owner.Invalidate();
            owner.Update(); // now, not when the queue is empty
        }

        public void Dispose() { Stop(); timer.Dispose(); }
    }

    // 13.11: a hint beside a control (the names of the folded side menu), made like the drop-downs: Windows 11's own
    // rounded corners, the blurred screen behind under the card glass, the same rim, the text in the middle. It never
    // takes the focus or the mouse (the mouse goes through to the menu under it).
    class GlassHint : Form
    {
        static GlassHint one;
        string text = "";
        Bitmap pic;
        int tint = 255;

        GlassHint()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card; Font = Theme.Body;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8; return p; } // NOACTIVATE, TOOLWINDOW, TRANSPARENT, TOPMOST
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int pref = 2; DwmSetWindowAttribute(Handle, 33, ref pref, 4);                          // rounded corners
                int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, 4); // no system border: the rim is ours
            }
            catch { }
        }

        const TextFormatFlags How = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        // item: the control's part the hint belongs to, in screen coordinates; the hint stands to its right, level with it
        public static void Beside(Rectangle item, string text)
        {
            if (one == null || one.IsDisposed) one = new GlassHint();
            var h = one;
            int tw = TextRenderer.MeasureText(text, Theme.Body, Size.Empty, How).Width;
            var b = new Rectangle(item.Right + Theme.S(10), item.Y + (item.Height - Theme.S(32)) / 2, tw + Theme.S(32), Theme.S(32));
            if (h.Visible && h.Bounds == b && h.text == text) return;
            h.text = text;
            // the glass: the screen where the hint will stand, taken before it shows there (the last hint stood a row higher
            // or lower and does not reach into this one)
            if (h.pic != null) h.pic.Dispose();
            h.pic = Frost.Screen(b, 0);
            h.tint = h.pic == null ? 255 : DarkRenderer.TintFor(h.pic);
            h.Bounds = b;
            if (!h.Visible) h.Show();
            h.Invalidate(); h.Update();
        }

        public static void Off() { if (one != null && !one.IsDisposed && one.Visible) one.Hide(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float rad = DarkRenderer.PaneRad(g);
            var all = new RectangleF(0, 0, Width, Height);
            if (pic != null) { g.DrawImageUnscaled(pic, 0, 0); Theme.FillGlass(g, all, rad, tint); }
            else Theme.Fill(g, all, rad, Theme.Card);
            Theme.Stroke(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), rad, Theme.BorderStrong);
            // the text exactly in the middle: measured without padding, the same room left and right
            int tw = TextRenderer.MeasureText(g, text, Theme.Body, Size.Empty, How).Width;
            // up and down by the eye's middle (capital top to baseline): the line box puts it 2 px low (measured 13.11)
            int up = (int)Math.Round(1.3f * Theme.Scale);
            TextRenderer.DrawText(g, text, Theme.Body, new Rectangle((Width - tw) / 2, -up, tw + 1, Height), Theme.Text, How);
        }

        protected override void Dispose(bool disposing) { if (disposing && pic != null) { pic.Dispose(); pic = null; } base.Dispose(disposing); }
    }

    // The screen behind a pane, blurred: what the glass of the notifications and of the drop-downs shows through.
    static class Frost
    {
        // the given rectangle of the screen; null when it cannot be read (e.g. the secure desktop)
        public static Bitmap Screen(Rectangle r, int strength)
        {
            try { using (var shot = Grab(r)) return Of(shot, strength); }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        static readonly IntPtr PerMonitor = new IntPtr(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        // 16.11: a picture of the screen under r, r being in the coordinates this program is given. The program is DPI aware
        // for the main monitor only; on a monitor with another scaling (the laptop's own screen at 150 % next to an external
        // one at 100 %) Windows stretches its windows and hands it made-up coordinates, while the screen is still read in
        // real pixels: the glass of the drop-downs, hints and notifications showed some other place, and unscaled. Here the
        // rectangle is turned into the real pixels of its monitor, read as a per-monitor aware thread and brought back to
        // the size asked for. On the main monitor (and on Windows without this API) it is read as before.
        public static Bitmap Grab(Rectangle r)
        {
            Rectangle phys;
            if (Physical(r, out phys)) return GrabReal(phys, r.Size);
            var shot = new Bitmap(r.Width, r.Height);
            try { using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(r.Location, Point.Empty, r.Size); return shot; }
            catch { shot.Dispose(); throw; }
        }

        // the screen under phys, a rectangle in real pixels, as a picture of the given size
        public static Bitmap GrabReal(Rectangle phys, Size size)
        {
            var shot = new Bitmap(size.Width, size.Height);
            var r = new Rectangle(Point.Empty, size);
            try
            {
                IntPtr old = IntPtr.Zero;
                try { old = SetThreadDpiAwarenessContext(PerMonitor); } catch { }
                try
                {
                    using (var big = new Bitmap(phys.Width, phys.Height))
                    {
                        using (var g = Graphics.FromImage(big)) g.CopyFromScreen(phys.Location, Point.Empty, phys.Size);
                        using (var g = Graphics.FromImage(shot))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(big, new Rectangle(0, 0, r.Width, r.Height), new Rectangle(0, 0, big.Width, big.Height), GraphicsUnit.Pixel);
                        }
                    }
                }
                finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
                return shot;
            }
            catch { shot.Dispose(); throw; }
        }

        // 17.11: the screen under phys, a rectangle in real pixels, as it is, not brought to another size (the mini panel's
        // live glass compares frame with frame first, and scaling every frame for that cost 26 ms on a 150 % screen)
        public static Bitmap GrabRealRaw(Rectangle phys)
        {
            var big = new Bitmap(phys.Width, phys.Height);
            try
            {
                IntPtr old = IntPtr.Zero;
                try { old = SetThreadDpiAwarenessContext(PerMonitor); } catch { }
                try { using (var g = Graphics.FromImage(big)) g.CopyFromScreen(phys.Location, Point.Empty, phys.Size); }
                finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
                return big;
            }
            catch { big.Dispose(); throw; }
        }

        // 17.11: the screen under phys (real pixels) copied into a memory DC of the caller's, no picture made in between
        // (the mini panel's live glass on a monitor of another scaling reads a frame up to 60 times a second)
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        public static bool BlitReal(IntPtr dc, Rectangle phys)
        {
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(PerMonitor); } catch { }
            try
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return false;
                try { return BitBlt(dc, 0, 0, phys.Width, phys.Height, screen, phys.X, phys.Y, 0x00CC0020); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }
            finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
        }

        // r (in the coordinates this program is given) in the real pixels of its monitor
        public static Rectangle Real(Rectangle r) { Rectangle phys; Physical(r, out phys); return phys; }

        // the real pixels of r; false when they are the same as r (nothing to convert)
        static bool Physical(Rectangle r, out Rectangle phys)
        {
            phys = r;
            try
            {
                var rc = new RECT { L = r.Left, T = r.Top, R = r.Right, B = r.Bottom };
                IntPtr mon = MonitorFromRect(ref rc, 2); // the nearest monitor
                var mine = new MONITORINFO { Size = 40 };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mine)) return false;
                var real = new MONITORINFO { Size = 40 };
                IntPtr old = SetThreadDpiAwarenessContext(PerMonitor);
                if (old == IntPtr.Zero) return false;
                try { if (!GetMonitorInfo(mon, ref real)) return false; }
                finally { SetThreadDpiAwarenessContext(old); }
                int lw = mine.Monitor.R - mine.Monitor.L, pw = real.Monitor.R - real.Monitor.L;
                if (lw <= 0 || pw <= 0 || pw == lw) return false;
                double k = (double)pw / lw;
                phys = new Rectangle(real.Monitor.L + (int)Math.Round((r.X - mine.Monitor.L) * k), real.Monitor.T + (int)Math.Round((r.Y - mine.Monitor.T) * k),
                    Math.Max(1, (int)Math.Round(r.Width * k)), Math.Max(1, (int)Math.Round(r.Height * k)));
                return true;
            }
            catch { return false; } // Windows before 10 1607: no thread DPI contexts
        }

        // scaled down (1/8, 1/14 or 1/24 by strength) and back up with bilinear filtering, then a little more
        // saturation and brightness, like light passing through glass
        public static Bitmap Of(Bitmap shot, int strength)
        {
            int W = shot.Width, H = shot.Height;
            int w1 = Math.Max(2, W / 4), h1 = Math.Max(2, H / 4), k = strength <= 0 ? 8 : strength == 1 ? 14 : 24, w2 = Math.Max(2, W / k), h2 = Math.Max(2, H / k);
            using (var a = Scale(shot, w1, h1)) using (var b = Scale(a, w2, h2)) using (var c = Scale(b, w1, h1)) using (var d = Scale(c, W, H))
                return Vivid(d, 1.35f, 1.06f);
        }

        // how bright the picture is where it is bright: the 90th percentile of its luminance (0..255), so a white
        // window under a part of the pane counts. The glass over it is made denser by this, to keep white text readable.
        public static float Bright(Bitmap pic)
        {
            const int n = 24;
            var lum = new System.Collections.Generic.List<float>(n * n);
            using (var small = Scale(pic, n, n))
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++) { var c = small.GetPixel(x, y); lum.Add(0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B); }
            lum.Sort();
            return lum[(int)(lum.Count * 0.9f)];
        }

        // saturation s, brightness b (ColorMatrix with the Rec. 709 luma weights)
        static Bitmap Vivid(Bitmap src, float s, float b)
        {
            float lr = 0.2126f * (1 - s), lg = 0.7152f * (1 - s), lb = 0.0722f * (1 - s);
            var cm = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { (lr + s) * b, lr * b, lr * b, 0, 0 },
                new[] { lg * b, (lg + s) * b, lg * b, 0, 0 },
                new[] { lb * b, lb * b, (lb + s) * b, 0, 0 },
                new[] { 0f, 0, 0, 1, 0 },
                new[] { 0.01f, 0.01f, 0.01f, 0, 1 },
            });
            var dst = new Bitmap(src.Width, src.Height);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }

        public static Bitmap Scale(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY); // no dark seams at the edges
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Raised; } }
        public override Color MenuItemBorder { get { return Theme.Raised; } }
        public override Color MenuBorder { get { return Theme.BorderStrong; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color CheckBackground { get { return Theme.Card; } }
        public override Color CheckSelectedBackground { get { return Theme.Raised; } }
        public override Color CheckPressedBackground { get { return Theme.Raised; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Raised; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Raised; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Raised; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Raised; } }
    }

    // Every drop-down of the app (Page.Menu, DropButton lists, the tray menu, submenus) is drawn here. 13.11: rounded
    // and made of the same glass as the cards. The rounding is Windows 11's own for the popup window
    // (DWMWA_WINDOW_CORNER_PREFERENCE: smooth corners, not a jagged Region), its system border is switched off and the
    // glass rim is drawn on the same curve; the highlighted item is a rounded plate set in from the edges.
    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        static readonly System.Collections.Generic.HashSet<IntPtr> rounded = new System.Collections.Generic.HashSet<IntPtr>();

        // 13.11 (2): the drop-downs are see-through glass. A popup of WinForms cannot be translucent by itself, so the
        // screen under it is photographed just before it shows (Opening: the bounds are set, the window is not up yet),
        // blurred like the notifications, and painted as its background under a translucent tint. The picture is a
        // little larger than the menu, so a menu that grows while opening still has glass under it.
        class Shot { public Bitmap Pic; public Rectangle Rect; public int Tint; }
        static readonly System.Collections.Generic.Dictionary<ToolStripDropDown, Shot> shots = new System.Collections.Generic.Dictionary<ToolStripDropDown, Shot>();
        static readonly System.Collections.Generic.HashSet<ToolStripDropDown> hooked = new System.Collections.Generic.HashSet<ToolStripDropDown>();
        const int GlassTint = 96;  // the least opacity of the card colour over the blurred screen (of 255): over a dark background
        const int GlassBlur = 0;   // the lightest of the three blurs: what is behind stays recognisable, like through frosted glass

        static void Hook(ToolStripDropDown d)
        {
            if (d == null || !hooked.Add(d)) return;
            d.Opening += (s, e) => { Capture(d); try { d.Opacity = 0; } catch { } }; // shows from nothing (Appear)
            d.Opened += (s, e) => Appear(d);
            d.Closed += (s, e) => Drop(d);
            d.Disposed += (s, e) => { Drop(d); hooked.Remove(d); };
        }

        // 14.11: every drop-down comes in instead of popping up: it fades in while it slides the last few pixels into
        // place, away from where it was opened (down from a button, up from the tray or from a button near the bottom);
        // a submenu only fades. Quick at first, a soft stop, about a sixth of a second. The window stays a hair under
        // full opacity afterwards: going to exactly 1 changes the window's style and makes it blink.
        const double Shown = 0.997;
        static void Appear(ToolStripDropDown d)
        {
            try
            {
                bool sub = d.OwnerItem != null && d.OwnerItem.Owner is ToolStripDropDown;
                int from = sub ? 0 : (d.Top + d.Height / 2 < Cursor.Position.Y ? 1 : -1) * Theme.S(10); // where it starts, off its place
                int left0 = d.Left, top0 = d.Top;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var timer = new Timer { Interval = 12 };
                Action stop = () => { timer.Stop(); timer.Dispose(); };
                timer.Tick += (s, e) =>
                {
                    try
                    {
                        if (d.IsDisposed || !d.Visible) { stop(); return; }
                        float t = Math.Min(1f, clock.ElapsedMilliseconds / 170f), k = 1f - (float)Math.Pow(1f - t, 3);
                        // another handler may have moved the menu sideways meanwhile (Page.ShowAt centres it): only the
                        // height is ours
                        if (from != 0) d.Top = top0 + (int)Math.Round(from * (1f - k));
                        d.Opacity = Shown * k;
                        d.Invalidate(); // the glass behind is drawn by the menu's place on the screen
                        if (t >= 1f) stop();
                    }
                    catch { stop(); try { d.Opacity = Shown; } catch { } }
                };
                if (from != 0) d.Top = top0 + from;
                timer.Start();
            }
            catch { try { d.Opacity = Shown; } catch { } }
        }

        // a drop-down drawn over the given picture as if it had opened there, with one item highlighted (a check:
        // an open menu closes as soon as another window is touched, so it is hard to look at otherwise)
        public static void RenderTo(string file, string backdropFile)
        {
            using (var m = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body, ShowImageMargin = true })
            using (var back = new Bitmap(backdropFile))
            {
                m.Items.Add(new ToolStripMenuItem("01 Balance") { Checked = true });
                m.Items.Add("02 High Low"); m.Items.Add("03 High Medium"); m.Items.Add("04 High Max");
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Новый пресет из этих настроек..."); m.Items.Add("Переименовать...");
                m.Items.Add(new ToolStripMenuItem("Удалить") { Enabled = false });
                m.CreateControl(); m.PerformLayout();
                int pad = Theme.S(48);
                var r = new Rectangle(-pad, -pad, m.Width + 2 * pad, m.Height + 2 * pad);
                using (var scene = new Bitmap(r.Width, r.Height))
                {
                    using (var g = Graphics.FromImage(scene))
                        g.DrawImage(back, new Rectangle(0, 0, r.Width, r.Height), new Rectangle((back.Width - r.Width) / 2, (back.Height - r.Height) / 2, r.Width, r.Height), GraphicsUnit.Pixel);
                    var frost = Frost.Of(scene, GlassBlur);
                    shots[m] = new Shot { Pic = frost, Rect = r, Tint = TintFor(frost) };
                    m.Items[2].Select();
                    using (var pic = new Bitmap(m.Width, m.Height))
                    {
                        m.DrawToBitmap(pic, new Rectangle(0, 0, m.Width, m.Height));
                        using (var g = Graphics.FromImage(scene)) g.DrawImageUnscaled(pic, pad, pad);
                    }
                    scene.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
                Drop(m);
            }
        }

        // How dense the glass has to be for white text to stay readable: as clear as GlassTint over a dark background,
        // denser the brighter the screen behind is (a menu over a white page ends up a dark smoked glass). Measured
        // on the bright end of the picture (the 90th percentile), so a white window under half the menu counts.
        public static int TintFor(Bitmap pic)
        {
            try { return TintOf(Frost.Bright(pic)); }
            catch { return 190; }
        }

        // the same from the brightness itself (the mini panel's live glass measures it on its own small picture)
        public static int TintOf(float bright)
        {
            float card = 0.2126f * Theme.Card.R + 0.7152f * Theme.Card.G + 0.0722f * Theme.Card.B, target = 74f;
            if (bright <= target) return GlassTint;
            return Math.Max(GlassTint, Math.Min(232, (int)Math.Round(255 * (bright - target) / (bright - card))));
        }

        static void Drop(ToolStripDropDown d)
        {
            Shot sh;
            if (!shots.TryGetValue(d, out sh)) return;
            shots.Remove(d);
            if (sh.Pic != null) sh.Pic.Dispose();
        }

        static void Capture(ToolStripDropDown d)
        {
            Drop(d);
            // submenus open later, each with its own picture (taken with this menu already on the screen)
            foreach (ToolStripItem it in d.Items) { var di = it as ToolStripDropDownItem; if (di != null && di.HasDropDownItems) Hook(di.DropDown); }
            if (d.Width < 4 || d.Height < 4) return;
            var r = Rectangle.Inflate(d.Bounds, Theme.S(48), Theme.S(48));
            var pic = Frost.Screen(r, GlassBlur);
            if (pic != null) shots[d] = new Shot { Pic = pic, Rect = r, Tint = TintFor(pic) };
        }

        static void Round(ToolStripDropDown d)
        {
            if (!d.IsHandleCreated || rounded.Contains(d.Handle)) return;
            rounded.Add(d.Handle);
            d.HandleDestroyed += (s, e) => rounded.Remove(((Control)s).Handle);
            try
            {
                int pref = 2; DwmSetWindowAttribute(d.Handle, 33, ref pref, 4);           // DWMWCP_ROUND (Windows 11; older: ignored)
                int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(d.Handle, 34, ref none, 4); // DWMWA_BORDER_COLOR: none, the rim is ours
            }
            catch { } // no dwmapi: square corners, the rest still draws
        }

        // the corner radius Windows uses for ROUND: 8 px at 96 dpi; the glass helpers make panes 1.5× rounder, hence the division
        public static float PaneRad(Graphics g) { return 8f * g.DpiX / 96f / 1.5f; }

        protected override void Initialize(ToolStrip ts)
        {
            base.Initialize(ts);
            var d = ts as ToolStripDropDown;
            if (d == null) return;
            d.Padding = new Padding(Theme.S(4), Theme.S(6), Theme.S(4), Theme.S(6)); // items clear of the rounded corners
            d.HandleCreated += (s, e) => Round(d);
            Round(d);
            Hook(d);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var d = e.ToolStrip as ToolStripDropDown;
            if (d == null) { base.OnRenderToolStripBackground(e); return; }
            Round(d); // submenus share this renderer without Initialize: they get their corners on the first paint
            Hook(d); // for the next time it opens
            var g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var all = new RectangleF(0, 0, d.Width, d.Height);
            Shot sh;
            if (shots.TryGetValue(d, out sh) && sh.Pic != null)
            {
                g.DrawImageUnscaled(sh.Pic, sh.Rect.X - d.Left, sh.Rect.Y - d.Top); // the blurred screen behind
                Theme.FillGlass(g, all, PaneRad(g), sh.Tint);
            }
            else Theme.Fill(g, all, PaneRad(g), Theme.Card); // no picture (e.g. the secure desktop): the opaque card with its sheen
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (!(e.ToolStrip is ToolStripDropDown)) { base.OnRenderToolStripBorder(e); return; }
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Stroke(g, new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f), PaneRad(g), Theme.BorderStrong);
        }

        // the column for check marks stays the glass, not a flat band
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!(e.Item.Owner is ToolStripDropDown)) { base.OnRenderMenuItemBackground(e); return; }
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(Theme.S(2), 0.5f, e.Item.Width - Theme.S(4), e.Item.Height - 1f);
            Theme.Fill(g, r, Theme.S(5), Color.FromArgb(34, 255, 255, 255)); // a veil of light: the glass shows through
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (!(e.Item.Owner is ToolStripDropDown)) { base.OnRenderSeparator(e); return; }
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255))) e.Graphics.DrawLine(pen, Theme.S(10), y, e.Item.Width - Theme.S(10), y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted; e.Text = L.T(e.Text);
            // 13.11: in a drop-down the lines stay aligned on the left, and that block of text is centred in the list by its
            // longest line (the lists are centred under their buttons and as wide as them); never over the check marks
            var owner = e.Item.Owner as ToolStripDropDown;
            if (owner != null && !(e.Item is ToolStripSeparator))
            {
                // measured and drawn the same way (no padding), and placed by the list, not by the item: items of one list
                // are not always equally wide, and a line placed inside its own item started a few pixels off the others
                const TextFormatFlags how = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                int longest = owner.Items.Cast<ToolStripItem>().Where(i => i.Available && !(i is ToolStripSeparator))
                    .Select(i => TextRenderer.MeasureText(L.T(i.Text ?? ""), e.TextFont, Size.Empty, how).Width).DefaultIfEmpty(0).Max();
                var r = e.TextRectangle;
                int x = Math.Max(r.X, (owner.Width - longest) / 2 - e.Item.Bounds.X);
                e.TextRectangle = new Rectangle(x, r.Y, Math.Max(longest + 2, e.Item.Width - x), r.Height);
                e.TextFormat = (e.TextFormat & ~(TextFormatFlags.HorizontalCenter | TextFormatFlags.Right)) | TextFormatFlags.Left | how;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Text, Theme.S(1.6f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                e.Graphics.DrawLines(pen, new[]
                {
                    new PointF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.52f),
                    new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.72f),
                    new PointF(r.X + r.Width * 0.78f, r.Y + r.Height * 0.30f),
                });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text2;
            base.OnRenderArrow(e);
        }
    }
}
