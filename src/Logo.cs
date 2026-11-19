using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace RedTune
{
    // Robot and a guy in sunglasses, shoulder to shoulder, arms crossed, smirking (the user's picture, 2026-11-11).
    // Kept as a white mask (logo.png, alpha = line) so it can be drawn in any color on any background.
    static class Logo
    {
        static Bitmap mask;

        static Bitmap Mask
        {
            get
            {
                if (mask != null) return mask;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RedTune.logo.png"))
                    mask = s != null ? new Bitmap(s) : new Bitmap(1, 1);
                return mask;
            }
        }

        // fill is unused: the mask has no fill, the background shows through
        public static void Draw(Graphics g, RectangleF r, Color line, Color fill)
        {
            var m = Mask;
            float k = Math.Min(r.Width / m.Width, r.Height / m.Height);
            var dst = new RectangleF(r.X + (r.Width - m.Width * k) / 2, r.Y + (r.Height - m.Height * k) / 2, m.Width * k, m.Height * k);
            var cm = new ColorMatrix(new[]
            {
                new[] { 0f, 0, 0, 0, 0 }, new[] { 0f, 0, 0, 0, 0 }, new[] { 0f, 0, 0, 0, 0 },
                new[] { 0f, 0, 0, line.A / 255f, 0 },
                new[] { line.R / 255f, line.G / 255f, line.B / 255f, 0, 1 },
            });
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                var mode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(m, Rectangle.Round(dst), 0, 0, m.Width, m.Height, GraphicsUnit.Pixel, ia);
                g.InterpolationMode = mode;
            }
        }

        // The app mark (12.11, the user's reference): a chip with three pins per side and three sliders inside.
        // Vector, in fractions of the box, so it is crisp from 16 px up. The comic logo above stays in «О программе».
        public static void DrawChip(Graphics g, RectangleF box, Color line)
        {
            float s = Math.Min(box.Width, box.Height), x0 = box.X + (box.Width - s) / 2, y0 = box.Y + (box.Height - s) / 2;
            Func<float, float> X = f => x0 + f * s, Y = f => y0 + f * s;
            var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            float stroke = Math.Max(1f, s * 0.058f), thin = Math.Max(1f, s * 0.04f);
            using (var pen = new Pen(line, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                // the body
                using (var body = Theme.Round(new RectangleF(X(0.25f), Y(0.25f), s * 0.5f, s * 0.5f), s * 0.11f)) g.DrawPath(pen, body);
                // three pins on every side
                foreach (var p in new[] { 0.385f, 0.5f, 0.615f })
                {
                    g.DrawLine(pen, X(p), Y(0.1f), X(p), Y(0.25f));
                    g.DrawLine(pen, X(p), Y(0.75f), X(p), Y(0.9f));
                    g.DrawLine(pen, X(0.1f), Y(p), X(0.25f), Y(p));
                    g.DrawLine(pen, X(0.75f), Y(p), X(0.9f), Y(p));
                }
            }
            // three sliders: knob left, right, left
            using (var pen = new Pen(line, thin) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var knob = new SolidBrush(line))
                foreach (var sl in new[] { new PointF(0.39f, 0.45f), new PointF(0.5f, 0.6f), new PointF(0.61f, 0.45f) })
                {
                    g.DrawLine(pen, X(0.34f), Y(sl.X), X(0.66f), Y(sl.X));
                    float r = s * 0.058f;
                    g.FillEllipse(knob, X(sl.Y) - r, Y(sl.X) - r, 2 * r, 2 * r);
                }
            g.SmoothingMode = mode;
        }

        // app icon: the chip in white on a dark rounded square, PNG entries (Windows Vista+ reads them)
        public static void WriteIcon(string path)
        {
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
            var images = new List<byte[]>();
            foreach (var s in sizes)
                using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        // a red square, lighter at the top (Red Tune, 13.11; it was dark while the app was Legion Tune)
                        using (var square = Theme.Round(new RectangleF(0, 0, s, s), s * 0.22f))
                        using (var bg = new LinearGradientBrush(new RectangleF(0, 0, s, s), Color.FromArgb(0xee, 0x4a, 0x50), Color.FromArgb(0xb4, 0x1f, 0x29), LinearGradientMode.Vertical))
                            g.FillPath(bg, square);
                        float pad = s * (s <= 24 ? 0.0f : 0.06f); // small sizes: the chip fills the square
                        DrawChip(g, new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad), Color.White);
                    }
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
