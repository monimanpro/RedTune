using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RedTune
{
    // 17.11: thick glass for rectangular blocks (the mini panel), after the notifications' pane (Osd: Bevel, Refract).
    // A block is a slab with a rounded bevel round its edge:
    //   · the face is flat and only lightly frosted: what is behind is seen through it where it is, soft. (For a
    //     while the face was a little convex and showed everything larger and pushed outwards, like a magnifying
    //     glass; taken out 17.11 as asked: the glass bends the picture at its edges only.)
    //   · the bevel bends the same picture much more: near the edge what is behind is seen from further in and
    //     squeezed, a little differently for red and blue, and brighter. (The first go showed the screen itself
    //     there, sharp and turned over, next to a matt face: a strip of small upside-down print in every block, two
    //     kinds of glass in one. Now the whole block is one glass, as matt at the edge as in the middle.)
    //   · the colour of the glass is thinner on the bevel, and a little light is lost just inside it;
    //   · the light on the bevel is computed from its slope (the notifications' formula): the bright room mirrored
    //     above, the dark desk below, a long lamp overhead, a hot spot top left, a weaker one opposite, the light
    //     that went through gathered inside the far edge;
    //   · a soft shadow falls on the screen around the block.
    // All that does not change from frame to frame is worked out once per size of block (Stamp, kept) and laid out
    // over the window (the maps below); a frame is then one pass over the pixels in whole numbers (Compose), which
    // is cheaper than the GDI+ fills it replaced.
    unsafe class GlassPanes
    {
        // how far outside a block its shadow reaches (the window is cut that far out)
        public static int Margin { get { return Theme.S(3.5f) + 1; } }

        const float ThinEdge = 0.5f;   // the colour of the glass at the very edge, of what it is in the middle
        const float ShadeMax = 0.10f;  // the light lost just inside the edge
        const float ShadowMax = 0.34f; // the shadow right at the block
        // 17.11: how thick the rounded edge of the glass is, of what it was at first (the lit bevel, the lens in it, the
        // thinner colour and the shade just inside it all go by this). At 1 the blocks looked like fat cushions
        const float Edge = 0.15f;
        // the lens in the edge has a width of its own: the lit bevel was made thin (Edge), and a lens as thin as that
        // bends nothing that can be seen
        const float LensEdge = 0.3f;
        const float BevelBand = 12f;    // how wide the bevel is as a lens, at LensEdge 1
        const float BevelBend = 1.3f;   // the bevel looks this far in at the very edge, of its own width

        public readonly int W, H;
        readonly byte[] cov, thin, lensW;  // how much of the pixel is glass; how dense its colour; how much of it is the lens
        readonly int[] lensX, lensY;       // where the lens looks from this pixel, in 1/256 of a pixel
        readonly uint[] light;             // the light, the shade and the shadow: premultiplied, laid over everything

        class Stamp { public int w, h, m, sw, sh; public byte[] cov, thin, lensW; public int[] lensX, lensY; public uint[] light; }
        static readonly Dictionary<long, Stamp> stamps = new Dictionary<long, Stamp>();
        static float stampScale, stampRad;

        public GlassPanes(int w, int h, IList<Rectangle> blocks, float rad)
        {
            W = w; H = h;
            int n = Math.Max(1, w * h);
            cov = new byte[n]; thin = new byte[n]; lensW = new byte[n]; lensX = new int[n]; lensY = new int[n]; light = new uint[n];
            if (stampScale != Theme.Scale || stampRad != rad) { stamps.Clear(); stampScale = Theme.Scale; stampRad = rad; }
            foreach (var b in blocks)
            {
                if (b.Width < 4 || b.Height < 4) continue;
                long key = ((long)b.Width << 32) | (uint)b.Height;
                Stamp s;
                if (!stamps.TryGetValue(key, out s)) { if (stamps.Count > 24) stamps.Clear(); stamps[key] = s = Make(b.Width, b.Height, rad); }
                for (int y = 0; y < s.sh; y++)
                {
                    int py = b.Y - s.m + y; if (py < 0 || py >= h) continue;
                    for (int x = 0; x < s.sw; x++)
                    {
                        int px = b.X - s.m + x; if (px < 0 || px >= w) continue;
                        int i = y * s.sw + x, o = py * w + px;
                        if (s.cov[i] != 0) { cov[o] = s.cov[i]; thin[o] = s.thin[i]; lensW[o] = s.lensW[i]; lensX[o] = s.lensX[i]; lensY[o] = s.lensY[i]; light[o] = s.light[i]; }
                        else if (cov[o] == 0 && s.light[i] > light[o]) light[o] = s.light[i]; // between two blocks: the darker of their shadows
                    }
                }
            }
        }

        // where a point is in a rounded rectangle (0, 0, w, h): d = how far inside the edge (negative outside),
        // (ox, oy) = the way out
        static void EdgeOf(float w, float h, float rr, float x, float y, out float d, out float ox, out float oy)
        {
            float x0 = rr, x1 = w - rr, y0 = rr, y1 = h - rr;
            float qx = x < x0 ? x0 : x > x1 ? x1 : x, qy = y < y0 ? y0 : y > y1 ? y1 : y;
            float vx = x - qx, vy = y - qy, len = (float)Math.Sqrt(vx * vx + vy * vy);
            if (len > 0.001f) { ox = vx / len; oy = vy / len; d = rr - len; return; }
            // deep inside: the nearest side
            float l = x, r = w - x, t = y, b = h - y, m = Math.Min(Math.Min(l, r), Math.Min(t, b));
            d = m; ox = 0; oy = 0;
            if (m == t) oy = -1; else if (m == b) oy = 1; else if (m == l) ox = -1; else ox = 1;
        }

        static float Spec(float nx, float ny, float nz, float lx, float ly, float lz, double power)
        {
            // Blinn: the half vector between the light and the eye (straight above the screen)
            float hx = lx, hy = ly, hz = lz + 1f, hl = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz);
            float d = (nx * hx + ny * hy + nz * hz) / hl;
            return d <= 0 ? 0 : (float)Math.Pow(d, power);
        }

        // the light on the bevel at a point dd inside the edge: above zero white, below zero black (Osd.Bevel's numbers)
        static float Light(float dd, float ox, float oy, float band, Random rnd)
        {
            float n1 = (float)Math.Sqrt(0.5 * 0.5 + 0.75 * 0.75 + 0.45 * 0.45), n2 = (float)Math.Sqrt(0.55 * 0.55 + 0.7 * 0.7 + 0.45 * 0.45), n0 = (float)Math.Sqrt(0.7 * 0.7 + 0.72 * 0.72);
            float tt = Math.Max(0f, 1f - dd / band);
            // the slope of the bevel: flat on the face, nearly a wall at the edge
            float nxy = (float)Math.Pow(tt, 1.2) * 0.93f, nz = (float)Math.Sqrt(1 - nxy * nxy), nx = ox * nxy, ny = oy * nxy;
            // what the eye sees mirrored: up = the bright room, down = the dark desk
            float up = -2f * nz * ny;
            float fres = 0.04f + 0.96f * (float)Math.Pow(1 - nz, 3.5);
            float white = fres * Math.Max(0f, Math.Min(1f, up * 1.6f)) * 3.2f;
            float black = fres * Math.Max(0f, Math.Min(1f, -up * 1.4f)) * 2.0f;
            white += Spec(nx, ny, nz, 0f, -0.7f / n0, 0.72f / n0, 260) * 0.55f;                 // a long lamp overhead (softer than on the pill: six wide blocks, six long lines)
            white += Spec(nx, ny, nz, -0.5f / n1, -0.75f / n1, 0.45f / n1, 120) * 1.0f;          // the key light top left
            white += Spec(nx, ny, nz, 0.55f / n2, 0.7f / n2, 0.45f / n2, 80) * 0.55f;            // a weaker one opposite
            // the light that went through the glass, gathered inside the far edge (below and to the right)
            float away = Math.Max(0f, ox * 0.5f + oy * 0.87f), bell = (dd - band * 0.95f) / (band * 0.36f);
            white += (float)Math.Pow(away, 1.5) * (float)Math.Exp(-bell * bell) * 0.16f;
            if (dd < 1.1f) black += 0.22f * (1f - dd / 1.1f);                                    // the edge turning away from the eye
            return white - black + (float)(rnd.NextDouble() - 0.5) * 0.012f;                     // grain: the soft gradients do not band
        }

        static float Smooth(float t) { t = t < 0 ? 0 : t > 1 ? 1 : t; return t * t * (3 - 2 * t); }

        static Stamp Make(int w, int h, float rad)
        {
            int m = Margin, sw = w + 2 * m, sh = h + 2 * m, n = sw * sh;
            var s = new Stamp { w = w, h = h, m = m, sw = sw, sh = sh, cov = new byte[n], thin = new byte[n], lensW = new byte[n], lensX = new int[n], lensY = new int[n], light = new uint[n] };
            float rr = Math.Min(rad, Math.Min(w, h) / 2f);
            float ek = Theme.Scale * Edge; // not Theme.S: that rounds to whole pixels, too coarse for a thin edge
            float band = 6.5f * ek, lens = Math.Min(BevelBand * Theme.Scale * LensEdge, rr), shift = lens * BevelBend, thinBand = 11f * ek, shadeBand = 13f * ek;
            float flat = Math.Max(Math.Max(lens, band * 2f), Math.Max(thinBand, shadeBand)), reach = m - 0.5f;
            // the frost and the sheen of a pane, drawn the usual way
            var sheen = new int[n];
            using (var bmp = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TranslateTransform(m, m);
                    Theme.GlassSheen(g, new RectangleF(0, 0, w, h), rr);
                }
                var bd = bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try { for (int y = 0; y < sh; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, sheen, y * sw, sw); }
                finally { bmp.UnlockBits(bd); }
            }
            var rnd = new Random(7);
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int i = y * sw + x;
                    float d, ox, oy;
                    EdgeOf(w, h, rr, x - m + 0.5f, y - m + 0.5f, out d, out ox, out oy);
                    if (d <= -0.5f)
                    {
                        // the shadow on the screen around the block, deeper under it than over it
                        float dist = -d - 0.5f;
                        if (dist >= reach) continue;
                        float k = 1f - dist / reach;
                        float sa = ShadowMax * k * k * (0.72f + 0.28f * oy);
                        s.light[i] = (uint)Math.Max(0, Math.Min(255, (int)(255 * sa + 0.5f))) << 24;
                        continue;
                    }
                    float c = Math.Min(1f, d + 0.5f), dd = Math.Max(0f, d);
                    s.cov[i] = (byte)Math.Max(1, (int)(255 * c + 0.5f));
                    uint under = unchecked((uint)sheen[i]);
                    // where this pixel looks: on the face straight through, on the bevel towards the middle, along the
                    // way out of the block
                    float lookX = 0, lookY = 0;
                    float tl = Math.Max(0f, 1f - dd / lens);
                    if (tl > 0)
                    {
                        float by = shift * tl * tl;
                        lookX -= ox * by; lookY -= oy * by;
                        s.lensW[i] = (byte)(255 * Smooth(tl) + 0.5f);
                    }
                    s.lensX[i] = (int)Math.Round(lookX * 256); s.lensY[i] = (int)Math.Round(lookY * 256);
                    if (dd >= flat) { s.thin[i] = 255; s.light[i] = under; continue; } // the face: no bevel light
                    s.thin[i] = (byte)(255 * (ThinEdge + (1f - ThinEdge) * Smooth(dd / thinBand)) + 0.5f);
                    float a = Light(dd, ox, oy, band, rnd) - ShadeMax * (1f - Smooth(dd / shadeBand));
                    uint al = (uint)Math.Round(255 * Math.Min(1f, Math.Abs(a)) * c);
                    uint over = al == 0 ? 0 : a > 0 ? (al << 24 | al << 16 | al << 8 | al) : al << 24;
                    // the bevel's light over the sheen
                    uint ia = 256 - (al + (al >> 7));
                    uint rb = (((under & 0xFF00FF) * ia) >> 8) & 0xFF00FF, ag = (((under >> 8) & 0xFF00FF) * ia) & 0xFF00FF00;
                    s.light[i] = over + (rb | ag);
                }
            return s;
        }

        internal static PixelFormat Fmt(Bitmap b)
        {
            // opaque pictures: the bytes are the same in all three, and asking for the picture's own format copies nothing
            var f = b.PixelFormat;
            return f == PixelFormat.Format32bppArgb || f == PixelFormat.Format32bppPArgb || f == PixelFormat.Format32bppRgb ? f : PixelFormat.Format32bppArgb;
        }

        // one colour of the picture at a point given in 1/256 of a pixel, the four nearest pixels mixed by distance
        static uint Tap(uint* p, int stride, int maxX, int maxY, int fx, int fy, int shift)
        {
            if (fx < 0) fx = 0; else if (fx > maxX) fx = maxX;
            if (fy < 0) fy = 0; else if (fy > maxY) fy = maxY;
            uint tx = (uint)(fx & 255), ty = (uint)(fy & 255);
            uint* q = p + (fy >> 8) * stride + (fx >> 8);
            uint a = (q[0] >> shift) & 255, b = (q[1] >> shift) & 255, c = (q[stride] >> shift) & 255, d = (q[stride + 1] >> shift) & 255;
            uint top = a * (256 - tx) + b * tx, bot = c * (256 - tx) + d * tx;
            return (top * (256 - ty) + bot * ty) >> 16;
        }

        // A frame. raw: the screen as read; blur: the same picture blurred, the same size; (ox, oy): where the window's
        // corner is in them; tint: how dense the card colour is on the flat face (of 255); dst: W × H pixels of 32 bits,
        // top row first. What the pictures do not cover is left as it was.
        public void Compose(Bitmap raw, Bitmap blur, int ox, int oy, int tint, Color card, IntPtr dst)
        {
            int rw = raw.Width, rh = raw.Height;
            if (dst == IntPtr.Zero || rw < 2 || rh < 2 || blur.Width != rw || blur.Height != rh) return;
            var rc = new Rectangle(0, 0, rw, rh);
            var rd = raw.LockBits(rc, ImageLockMode.ReadOnly, Fmt(raw));
            try
            {
                var bd = blur.LockBits(rc, ImageLockMode.ReadOnly, Fmt(blur));
                try
                {
                    int rs = rd.Stride / 4, bs = bd.Stride / 4;
                    uint* rp = (uint*)rd.Scan0, bp = (uint*)bd.Scan0, o = (uint*)dst;
                    uint cardRB = (uint)(card.R << 16 | card.B), cardG = card.G, tn = (uint)Math.Max(0, Math.Min(255, tint));
                    int x0 = Math.Max(0, -ox), x1 = Math.Min(W, rw - ox), maxX = (rw - 2) << 8, maxY = (rh - 2) << 8;
                    fixed (byte* pc = cov, pt = thin, pw = lensW)
                    fixed (int* lx = lensX, ly = lensY)
                    fixed (uint* pl = light)
                        for (int y = 0; y < H; y++)
                        {
                            int sy = y + oy;
                            if (sy < 0 || sy >= rh) continue;
                            uint* rr = rp + sy * rs + ox, br = bp + sy * bs + ox;
                            int i = y * W + x0;
                            for (int x = x0; x < x1; x++, i++)
                            {
                                uint px, c = pc[i];
                                if (c == 0) px = rr[x];
                                else
                                {
                                    // the face: the frosted screen, seen from nearer the middle (the picture is soft,
                                    // the nearest pixel will do)
                                    int dx = lx[i], dy = ly[i];
                                    if ((dx | dy) == 0) px = br[x];
                                    else
                                    {
                                        int qx = x + ox + ((dx + 128) >> 8), qy = sy + ((dy + 128) >> 8);
                                        if (qx < 0) qx = 0; else if (qx >= rw) qx = rw - 1;
                                        if (qy < 0) qy = 0; else if (qy >= rh) qy = rh - 1;
                                        px = bp[qy * bs + qx];
                                    }
                                    uint lw = pw[i];
                                    if (lw != 0)
                                    {
                                        // the bevel: glass bends blue more than red, and gathers light
                                        int fx = (x + ox) << 8, fy = sy << 8, far = 256 + (int)(lw * 18 >> 8), near = 256 - (int)(lw * 18 >> 8);
                                        uint b = Tap(bp, bs, maxX, maxY, fx + dx * far / 256, fy + dy * far / 256, 0);
                                        uint g = (px >> 8) & 255;
                                        uint r = Tap(bp, bs, maxX, maxY, fx + dx * near / 256, fy + dy * near / 256, 16);
                                        uint gain = 256 + (lw * 70 >> 8);
                                        b = b * gain >> 8; if (b > 255) b = 255;
                                        g = g * gain >> 8; if (g > 255) g = 255;
                                        r = r * gain >> 8; if (r > 255) r = 255;
                                        px = r << 16 | g << 8 | b;
                                    }
                                    // the colour of the glass (red and blue together: they sit a byte apart)
                                    uint ta = tn * pt[i] >> 8; ta += ta >> 7;
                                    uint rb = (((px & 0xFF00FF) * (256 - ta) + cardRB * ta) >> 8) & 0xFF00FF, gg = (((px >> 8) & 0xFF) * (256 - ta) + cardG * ta) >> 8;
                                    px = rb | gg << 8;
                                    if (c < 255)
                                    {
                                        // the edge of the block: part glass, part the screen
                                        uint cv = c + (c >> 7), q = rr[x];
                                        rb = (((q & 0xFF00FF) * (256 - cv) + (px & 0xFF00FF) * cv) >> 8) & 0xFF00FF;
                                        gg = (((q >> 8) & 0xFF) * (256 - cv) + ((px >> 8) & 0xFF) * cv) >> 8;
                                        px = rb | gg << 8;
                                    }
                                }
                                uint l = pl[i];
                                if (l != 0)
                                {
                                    uint ia = 256 - ((l >> 24) + (l >> 31));
                                    uint rb = (((px & 0xFF00FF) * ia) >> 8) & 0xFF00FF, gg = (((px >> 8) & 0xFF) * ia) >> 8;
                                    px = (l & 0xFFFFFF) + (rb | gg << 8);
                                }
                                o[i] = px | 0xFF000000u;
                            }
                        }
                }
                finally { blur.UnlockBits(bd); }
            }
            finally { raw.UnlockBits(rd); }
        }
    }
}
