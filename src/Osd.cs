using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RedTune
{
    // On-screen notifications (like LLT's): a dark pill with an icon and a line, fades out, never takes the focus
    static class Osd
    {
        // OffGlyph: the icon for the off state when the font has one (Fn lock: the open lock, 13.11); without it the icon gets a slash
        public class Kind
        {
            public string Key, Name, Glyph, OffGlyph;
            public Kind(string k, string n, string g, string off = null) { Key = k; Name = n; Glyph = g; OffGlyph = off; }
        }

        public static readonly Kind[] Kinds =
        {
            new Kind("power", "Режим питания", ""), new Kind("caps", "Caps Lock", ""), new Kind("num", "Num Lock", ""),
            new Kind("fnlock", "Блокировка Fn", "", ""), new Kind("touchpad", "Блокировка тачпада", ""), new Kind("camera", "Камера", ""),
            new Kind("mic", "Микрофон", ""), new Kind("airplane", "Режим полёта", ""), new Kind("refresh", "Частота обновления", ""),
            new Kind("light", "Подсветка клавиатуры", ""), new Kind("ac", "Блок питания", ""), new Kind("curve", "Применение кривой", ""),
        };

        // 13.11: which notifications this laptop can have. Fn keys, the power modes, the touchpad, camera, microphone and
        // airplane keys, the refresh rate and the keyboard light come from Lenovo's firmware events; Caps / Num Lock,
        // the charger and the curve work on any laptop
        public static bool Available(Kind k)
        {
            switch (k.Key)
            {
                case "caps": case "num": case "ac": return true;
                case "curve": return Hw.Curve;
                default: return Hw.Device;
            }
        }

        public static readonly string[] Positions = { "bottom", "top", "center", "bottomleft", "bottomright", "topleft", "topright" };
        public static readonly string[] PositionNames = { "Внизу по центру", "Вверху по центру", "По центру", "Внизу слева", "Внизу справа", "Вверху слева", "Вверху справа" };

        static Settings settings;
        static SynchronizationContext ui;
        static readonly List<OsdForm> open = new List<OsdForm>();

        // Notifications live on their own UI thread: when the main window is busy (switching or scrolling a page),
        // the glass still redraws at full rate. Fonts are cloned for that thread (GDI+ objects are not shared between threads).
        static Font titleFont;
        public static void Init(Settings s)
        {
            settings = s;
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                // 16.11: this thread works in real pixels (per-monitor DPI aware), the rest of the program does not. With
                // the main window on a monitor of another scaling (the laptop's own screen at 150 % beside a 100 % main
                // monitor) Windows gave a notification window that monitor's scaling the moment it was shown: its place
                // on the main monitor was multiplied by 1.5 from the far monitor's corner and it landed beyond every
                // screen, 1.5 times too large — created, "visible", seen by nobody. Windows of a per-monitor aware
                // thread are never scaled or moved by the system.
                try { SetThreadDpiAwarenessContext(PerMonitor); } catch { } // Windows before 10 1607: no such trouble either
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                ui = SynchronizationContext.Current;
                titleFont = new Font(Theme.Title.FontFamily, Theme.Title.Size, Theme.Title.Style, Theme.Title.Unit);
                ready.Set();
                Application.Run();
            }) { IsBackground = true, Name = "osd" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.WaitOne(3000);
        }

        // --osd-png: the notification drawn into a file over a backdrop picture (cropped from its lower middle), nothing is shown
        // all = every kind stacked (a check of how the icons line up with the text)
        public static void RenderTo(string file, string backdropFile, bool all = false, bool glass = true)
        {
            settings = new Settings { OsdGlass = glass, OsdShine = true };
            titleFont = new Font(Theme.Title.FontFamily, Theme.Title.Size, Theme.Title.Style, Theme.Title.Unit);
            var samples = new[] { "Режим питания: Свой", "Caps Lock включён", "Num Lock выключен", "Блокировка Fn включена", "Тачпад включён", "Камера включена",
                "Микрофон включён", "Режим полёта", "Частота обновления: 165 Hz", "Подсветка: высокая", "Зарядка подключена", "Кривая «Тест» применена" };
            var rows = (all ? Kinds : Kinds.Where(k => k.Key == "power")).Select(k => Tuple.Create(k, samples[Array.IndexOf(Kinds, k)], false)).ToList();
            if (all)
                rows.AddRange(new[] { Tuple.Create("fnlock", "Блокировка Fn выключена"), Tuple.Create("touchpad", "Тачпад выключен"), Tuple.Create("camera", "Камера выключена"), Tuple.Create("mic", "Микрофон выключен") }
                    .Select(t => Tuple.Create(Kinds.First(k => k.Key == t.Item1), t.Item2, true)));
            var pics = new List<Bitmap>();
            var mons = Monitors(); var main = mons.FirstOrDefault(m => m.Primary) ?? mons[0];
            using (var back = new Bitmap(backdropFile))
                foreach (var row in rows)
                    using (var f = row.Item3 && row.Item1.OffGlyph != null ? new OsdForm(row.Item1.OffGlyph, row.Item2, main, "bottom", 2000) : new OsdForm(row.Item1.Glyph, row.Item2, main, "bottom", 2000, row.Item3))
                    {
                        int w = Math.Min(back.Width, f.Width), h = Math.Min(back.Height, f.Height);
                        using (var crop = back.Clone(new Rectangle((back.Width - w) / 2, Math.Max(0, back.Height * 2 / 3 - h / 2), w, h), back.PixelFormat))
                            pics.Add(f.Preview(crop));
                    }
            using (var sheet = new Bitmap(pics.Max(p => p.Width), pics.Sum(p => p.Height)))
            {
                using (var g = Graphics.FromImage(sheet)) { int y = 0; foreach (var p in pics) { g.DrawImageUnscaled(p, 0, y); y += p.Height; } }
                sheet.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            foreach (var p in pics) p.Dispose();
        }

        public static bool IsOn(string key) { return !(settings.OsdOff ?? "").Split(',').Contains(key); }

        public static void SetOn(string key, bool on)
        {
            var list = (settings.OsdOff ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(k => k != key).ToList();
            if (!on) list.Add(key);
            settings.OsdOff = string.Join(",", list);
            Store.SaveSettings(settings);
        }

        // from any thread; force shows even when switched off (the "example" button)
        // off: the thing was switched off, the icon gets a slash through it (touchpad, camera, microphone: 13.11)
        public static void Show(string key, string text, bool force = false, bool off = false)
        {
            if (settings == null || ui == null) return;
            if (!force && (!settings.OsdOn || !IsOn(key))) return;
            ui.Post(_ =>
            {
                // 16.11: the main monitor unless the settings say «where the active window is» or «all»
                var mons = Monitors(); var main = mons.FirstOrDefault(m => m.Primary) ?? mons[0];
                Mon[] screens;
                if (settings.OsdAllMonitors) screens = mons.ToArray();
                else if (settings.OsdFollow) { var am = ActiveMonitor(); screens = new[] { mons.FirstOrDefault(m => m.Handle == am) ?? main }; }
                else screens = new[] { main };
                // a game or video on the whole screen hides the notification only on its own monitor
                if (!force && !settings.OsdTopmost) screens = screens.Where(sc => !FullscreenOn(sc)).ToArray();
                if (screens.Length == 0) return;
                foreach (var f in open.ToList()) f.Close();
                open.Clear();
                var kind = Kinds.FirstOrDefault(k => k.Key == key);
                foreach (var sc in screens)
                {
                    var f = kind != null && off && kind.OffGlyph != null ? new OsdForm(kind.OffGlyph, L.T(text), sc, settings.OsdPosition, settings.OsdMs) : new OsdForm(kind != null ? kind.Glyph : "", L.T(text), sc, settings.OsdPosition, settings.OsdMs, off);
                    f.FormClosed += (s, e) => open.Remove(f);
                    open.Add(f);
                    f.Show();
                }
            }, null);
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        // ---------- the monitors, in real pixels (16.11) ----------
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        delegate bool MonCb(IntPtr mon, IntPtr dc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonCb cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point p, uint flags);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        static readonly IntPtr PerMonitor = new IntPtr(-4), SystemAware = new IntPtr(-2); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, _SYSTEM_AWARE

        // Scale: how much larger this monitor shows the program than the main one (its scaling over the main monitor's)
        public class Mon { public IntPtr Handle; public Rectangle Bounds, Work; public bool Primary; public float Scale = 1f; }

        // f run as a thread of the given DPI awareness, whatever this thread is
        static T As<T>(IntPtr ctx, Func<T> f)
        {
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(ctx); } catch { }
            try { return f(); }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
        }

        static Rectangle Rect(RECT r) { return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }

        internal static List<Mon> Monitors() // also the overlay (Overlay)
        {
            var list = new List<Mon>();
            try
            {
                var handles = new List<IntPtr>();
                MonCb cb = (m, dc, r, d) => { handles.Add(m); return true; };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
                foreach (var m in handles)
                {
                    var real = new MONITORINFO { Size = 40 }; var mine = new MONITORINFO { Size = 40 };
                    if (!As(PerMonitor, () => GetMonitorInfo(m, ref real))) continue;
                    bool known = As(SystemAware, () => GetMonitorInfo(m, ref mine));
                    int rw = real.Monitor.R - real.Monitor.L, lw = mine.Monitor.R - mine.Monitor.L;
                    list.Add(new Mon { Handle = m, Bounds = Rect(real.Monitor), Work = Rect(real.Work), Primary = (real.Flags & 1) != 0, Scale = known && lw > 0 && rw > 0 ? (float)rw / lw : 1f });
                }
            }
            catch { list.Clear(); }
            if (list.Count == 0) { var p = Screen.PrimaryScreen; list.Add(new Mon { Bounds = p.Bounds, Work = p.WorkingArea, Primary = true }); }
            return list;
        }

        // the monitor the user is working on: the one with the window in front, or with the mouse when nothing is in front
        // (the setting «Где активное окно»; by default the notifications stay on the main monitor)
        internal static IntPtr ActiveMonitor()
        {
            try
            {
                return As(PerMonitor, () =>
                {
                    var h = GetForegroundWindow();
                    if (h != IntPtr.Zero && h != GetShellWindow() && h != GetDesktopWindow() && !IsIconic(h)) return MonitorFromWindow(h, 2);
                    Point p; GetCursorPos(out p);
                    return MonitorFromPoint(p, 2);
                });
            }
            catch { return IntPtr.Zero; }
        }

        // a game or video covering the whole of that screen
        internal static bool FullscreenOn(Mon target)
        {
            try
            {
                return As(PerMonitor, () =>
                {
                    var h = GetForegroundWindow();
                    if (h == IntPtr.Zero || h == GetShellWindow() || h == GetDesktopWindow()) return false;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return false;
                    if (target.Handle != IntPtr.Zero && MonitorFromWindow(h, 2) != target.Handle) return false; // it is on another monitor (it used to hide the notification anyway)
                    var b = target.Bounds;
                    return r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom;
                });
            }
            catch { return false; }
        }

        // Live glass (like macOS liquid glass). The window asks Windows to leave it out of screen capture
        // (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+), so it can read the screen behind itself
        // ~20 times a second without seeing itself or an older notification: the glass follows page changes, scrolling,
        // video. The price: notifications are not in screenshots or screen recordings. Without that flag (older Windows)
        // the background is read once, as before.
        // Layers: a soft shadow (drawn once) · the screen behind, blurred, more vivid, slightly magnified (every frame) ·
        // frost, darkening, edge glow, sheen, rim, the icon and the text (drawn once). A layered window with per-pixel
        // alpha; the fade-out only changes the constant alpha.
        class OsdForm : Form
        {
            const int FrameMs = 28; // ~30 frames a second with the ~15.6 ms timer tick (a frame takes ~10 ms)
            readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
            readonly Bitmap image, shadow, overlay;
            readonly GraphicsPath pill;
            readonly Rectangle area;     // the picture: the size of the window in the program's own units
            readonly Rectangle realArea; // the window on the screen, in real pixels (the same size on the main monitor)
            Bitmap stretched;
            readonly int blur;
            readonly bool glass, wantLive;
            bool live;
            DateTime shown, lastFrame;
            byte alpha = 255;
            readonly int ms;

            [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref Blend pblend, int dwFlags);
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
            [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

            // above every normal window, without taking the focus (TopMost alone was not applied: the notification
            // ended up under the app in front, 12.11)
            void OnTop() { if (IsHandleCreated) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } // HWND_TOPMOST; NOSIZE, NOMOVE, NOACTIVATE
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
            [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

            // the icon with its faint shadow on a layer of its own; off = a slash through it with a clear gap on
            // each side, cut out of the icon so it reads the same over any background
            static Bitmap GlyphLayer(string glyph, Font f, RectangleF gr, StringFormat sf, int W, int H, bool off, RectangleF ink)
            {
                var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var accent = Color.FromArgb(252, Theme.AccentHover);
                PointF p1 = PointF.Empty, p2 = PointF.Empty;
                bool slash = off && ink.Width > 0 && ink.Height > 0;
                if (slash)
                {
                    float side = Math.Max(ink.Width, ink.Height) / 2f + Theme.S(1.5f), cx = (ink.Left + ink.Right) / 2f, cy = (ink.Top + ink.Bottom) / 2f;
                    p1 = new PointF(cx - side, cy - side); p2 = new PointF(cx + side, cy + side);
                }
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (var s = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.DrawString(glyph, f, s, new RectangleF(gr.X, gr.Y + 1, gr.Width, gr.Height), sf);
                    using (var a = new SolidBrush(accent)) g.DrawString(glyph, f, a, gr, sf);
                }
                if (!slash) return bmp;
                // the gap: whatever the wide line covers is taken out of the icon
                using (var mask = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(mask))
                    using (var pen = new Pen(Color.White, Theme.S(5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawLine(pen, p1, p2); }
                    var rect = new Rectangle(0, 0, W, H);
                    var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var md = mask.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        var bb = new byte[bd.Stride * H]; var mb = new byte[md.Stride * H];
                        Marshal.Copy(bd.Scan0, bb, 0, bb.Length); Marshal.Copy(md.Scan0, mb, 0, mb.Length);
                        for (int i = 3; i < bb.Length; i += 4) bb[i] = (byte)(bb[i] * (255 - mb[i]) / 255);
                        Marshal.Copy(bb, 0, bd.Scan0, bb.Length);
                    }
                    finally { bmp.UnlockBits(bd); mask.UnlockBits(md); }
                }
                // and the slash itself, in the colour and about the weight of the icon
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var sp = new Pen(Color.FromArgb(90, 0, 0, 0), Theme.S(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(sp, p1.X, p1.Y + 1, p2.X, p2.Y + 1);
                    using (var pen = new Pen(accent, Theme.S(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, p1, p2);
                }
                return bmp;
            }

            // the rows a string really covers when drawn the way the overlay draws it (top, bottom exclusive)
            static RectangleF Ink(string s, Font f, RectangleF r, StringFormat sf, int W, int H)
            {
                using (var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.DrawString(s, f, Brushes.White, r, sf);
                    }
                    int top = -1, bottom = -1, lft = int.MaxValue, rgt = -1;
                    var data = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        var row = new byte[data.Stride];
                        for (int y = 0; y < H; y++)
                        {
                            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                            bool ink = false;
                            for (int x = 3; x < W * 4; x += 4) if (row[x] > 96) { ink = true; lft = Math.Min(lft, x / 4); rgt = Math.Max(rgt, x / 4 + 1); }
                            if (!ink) continue;
                            if (top < 0) top = y;
                            bottom = y + 1;
                        }
                    }
                    finally { bmp.UnlockBits(data); }
                    return top < 0 ? RectangleF.Empty : RectangleF.FromLTRB(lft, top, rgt, bottom);
                }
            }

            float shadeTint, shadeShadow; // the tint of the overlay and the shadow under the pane, 0..1 (for Shade)

            // 16.11: the glass made to look real rather than drawn. Two things do it:
            // · refraction: near the edge the pane is a rounded bevel, a lens; what is behind is seen there displaced
            //   towards the middle and squeezed, a little differently for red and blue (Refract, every frame);
            // · the light on that bevel computed from its slope (Bevel, once): the reflection of a bright surrounding above
            //   and a dark one below, growing towards the steep edge (Fresnel), a long lamp overhead, a hot spot top left,
            //   a weaker one bottom right, and the light that went through and gathers inside the far edge.
            const int ShadeAlpha = 26, GlowTop = 30, GlowBottom = 18;
            const float LensK = 1.04f;                 // the whole pane magnifies slightly
            static float LightBand { get { return Theme.S(6.5f); } }   // the bevel as lit
            static float LensBand { get { return Theme.S(12f); } }     // the bevel as a lens
            static float LensShift { get { return Theme.S(7f); } }     // how far the very edge looks inwards
            float[] mapX, mapY, offX, offY, gain; byte[] srcBuf, dstBuf;
            bool thinEdge; PointF bodyFocus;

            // the colour of the glass itself: thinner towards the edge, where the pane is a bevel and lets more light through
            void Body(Graphics g, Color c, int alpha)
            {
                if (alpha <= 0) return;
                if (!thinEdge) { using (var b = new SolidBrush(Color.FromArgb(alpha, c))) g.FillPath(b, pill); return; }
                using (var pb = new PathGradientBrush(pill) { CenterColor = Color.FromArgb(alpha, c), SurroundColors = new[] { Color.FromArgb((int)(alpha * 0.42f), c) }, FocusScales = bodyFocus })
                    g.FillPath(pb, pill);
            }

            // where a point is in the pill: d = how far inside the edge (negative outside), (ox, oy) = the way out
            static void EdgeOf(RectangleF rect, float x, float y, out float d, out float ox, out float oy)
            {
                float rr = rect.Height / 2f, cy = rect.Y + rr, x0 = rect.X + rr, x1 = rect.Right - rr;
                float qx = Math.Max(x0, Math.Min(x1, x)), vx = x - qx, vy = y - cy;
                float len = (float)Math.Sqrt(vx * vx + vy * vy);
                if (len < 0.001f) { ox = 0; oy = -1; d = rr; return; }
                ox = vx / len; oy = vy / len; d = rr - len;
            }

            static float Spec(float nx, float ny, float nz, float lx, float ly, float lz, double power)
            {
                // Blinn: the half vector between the light and the eye (straight above the screen)
                float hx = lx, hy = ly, hz = lz + 1f, hl = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz);
                float d = (nx * hx + ny * hy + nz * hz) / hl;
                return d <= 0 ? 0 : (float)Math.Pow(d, power);
            }

            static Bitmap Bevel(int W, int H, RectangleF rect)
            {
                var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var buf = new byte[W * H * 4];
                float band = LightBand;
                var rnd = new Random(7);
                // the lights, as unit vectors from the surface (x right, y down, z to the eye)
                float n1 = (float)Math.Sqrt(0.5 * 0.5 + 0.75 * 0.75 + 0.45 * 0.45), n2 = (float)Math.Sqrt(0.55 * 0.55 + 0.7 * 0.7 + 0.45 * 0.45), n0 = (float)Math.Sqrt(0.7 * 0.7 + 0.72 * 0.72);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float d, ox, oy;
                        EdgeOf(rect, x + 0.5f, y + 0.5f, out d, out ox, out oy);
                        if (d <= -0.5f) continue;
                        float cov = Math.Min(1f, d + 0.5f), dd = Math.Max(0f, d);
                        float tt = Math.Max(0f, 1f - dd / band);
                        // the slope of the bevel: flat on the pane, nearly a wall at the edge
                        float nxy = (float)Math.Pow(tt, 1.2) * 0.93f, nz = (float)Math.Sqrt(1 - nxy * nxy), nx = ox * nxy, ny = oy * nxy;
                        // what the eye sees mirrored: up = the bright room, down = the dark desk
                        float up = -2f * nz * ny;
                        float fres = 0.04f + 0.96f * (float)Math.Pow(1 - nz, 3.5);
                        float white = fres * Math.Max(0f, Math.Min(1f, up * 1.6f)) * 3.2f;
                        float black = fres * Math.Max(0f, Math.Min(1f, -up * 1.4f)) * 2.0f;
                        // a long lamp overhead: a soft line along the top bevel, gone round the ends
                        white += Spec(nx, ny, nz, 0f, -0.7f / n0, 0.72f / n0, 260) * 0.85f;
                        // the key light top left, and a weaker one opposite
                        white += Spec(nx, ny, nz, -0.5f / n1, -0.75f / n1, 0.45f / n1, 120) * 1.0f;
                        white += Spec(nx, ny, nz, 0.55f / n2, 0.7f / n2, 0.45f / n2, 80) * 0.55f;
                        // the light that went through the glass, gathered inside the far edge (below and to the right)
                        float far = Math.Max(0f, ox * 0.5f + oy * 0.87f), bell = (dd - band * 0.95f) / (band * 0.36f);
                        white += (float)Math.Pow(far, 1.5) * (float)Math.Exp(-bell * bell) * 0.24f;
                        // the thin dark line where the edge turns away from the eye
                        if (dd < 1.1f) black += 0.22f * (1f - dd / 1.1f);
                        // fine grain: no two bits of a real surface are the same, and the soft gradients do not band
                        float a = white - black + (float)(rnd.NextDouble() - 0.5) * 0.012f;
                        int al = (int)Math.Round(255 * Math.Min(1f, Math.Abs(a)) * cov);
                        if (al <= 0) continue;
                        int i = (y * W + x) * 4;
                        byte v = a > 0 ? (byte)255 : (byte)0;
                        buf[i] = v; buf[i + 1] = v; buf[i + 2] = v; buf[i + 3] = (byte)al;
                    }
                var bd = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { for (int y = 0; y < H; y++) Marshal.Copy(buf, y * W * 4, bd.Scan0 + y * bd.Stride, W * 4); }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }

            // where each pixel of the pane looks at the screen behind: all of it magnified a little, the bevel much more
            void BuildLens(int W, int H, RectangleF rect)
            {
                mapX = new float[W * H]; mapY = new float[W * H]; offX = new float[W * H]; offY = new float[W * H]; gain = new float[W * H];
                float cx = W / 2f, cy = H / 2f, band = LensBand, shift = LensShift;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        float d, ox, oy;
                        EdgeOf(rect, x + 0.5f, y + 0.5f, out d, out ox, out oy);
                        gain[i] = 1f;
                        if (d <= -1.5f) { mapX[i] = x; mapY[i] = y; continue; }
                        mapX[i] = cx + (x + 0.5f - cx) / LensK - 0.5f; mapY[i] = cy + (y + 0.5f - cy) / LensK - 0.5f;
                        float tt = Math.Max(0f, 1f - Math.Max(0f, d) / band), s = shift * tt * tt;
                        offX[i] = -ox * s; offY[i] = -oy * s;
                        gain[i] = 1f + 0.5f * tt;
                    }
            }

            static int Tap(byte[] s, int W, int H, float fx, float fy, int ch)
            {
                if (fx < 0) fx = 0; else if (fx > W - 1.001f) fx = W - 1.001f;
                if (fy < 0) fy = 0; else if (fy > H - 1.001f) fy = H - 1.001f;
                int x0 = (int)fx, y0 = (int)fy; float tx = fx - x0, ty = fy - y0;
                int i = (y0 * W + x0) * 4 + ch, j = i + W * 4;
                float a = s[i] + (s[i + 4] - s[i]) * tx, b = s[j] + (s[j + 4] - s[j]) * tx;
                return (int)(a + (b - a) * ty + 0.5f);
            }

            // the screen behind as seen through the pane (the same size as the window)
            Bitmap Refract(Bitmap back)
            {
                int W = area.Width, H = area.Height;
                if (mapX == null || back.Width != W || back.Height != H) return (Bitmap)back.Clone();
                var res = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rc = new Rectangle(0, 0, W, H);
                int n = W * H * 4;
                if (srcBuf == null) { srcBuf = new byte[n]; dstBuf = new byte[n]; }
                var sd = back.LockBits(rc, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { for (int y = 0; y < H; y++) Marshal.Copy(sd.Scan0 + y * sd.Stride, srcBuf, y * W * 4, W * 4); }
                finally { back.UnlockBits(sd); }
                for (int i = 0, o = 0; i < W * H; i++, o += 4)
                {
                    float fx = mapX[i], fy = mapY[i], dx = offX[i], dy = offY[i];
                    if (dx == 0 && dy == 0)
                    {
                        dstBuf[o] = (byte)Tap(srcBuf, W, H, fx, fy, 0); dstBuf[o + 1] = (byte)Tap(srcBuf, W, H, fx, fy, 1); dstBuf[o + 2] = (byte)Tap(srcBuf, W, H, fx, fy, 2);
                    }
                    else
                    {
                        // glass bends blue more than red: a faint colour fringe at the very edge
                        float k = gain[i];
                        dstBuf[o] = (byte)Math.Min(255f, Tap(srcBuf, W, H, fx + dx * 1.16f, fy + dy * 1.16f, 0) * k);
                        dstBuf[o + 1] = (byte)Math.Min(255f, Tap(srcBuf, W, H, fx + dx, fy + dy, 1) * k);
                        dstBuf[o + 2] = (byte)Math.Min(255f, Tap(srcBuf, W, H, fx + dx * 0.84f, fy + dy * 0.84f, 2) * k);
                    }
                    dstBuf[o + 3] = 255;
                }
                var dd = res.LockBits(rc, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { for (int y = 0; y < H; y++) Marshal.Copy(dstBuf, y * W * 4, dd.Scan0 + y * dd.Stride, W * 4); }
                finally { res.UnlockBits(dd); }
                return res;
            }

            public OsdForm(string glyph, string text, Mon screen, string position, int ms, bool off = false)
            {
                this.ms = ms;
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
                var family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                int w = TextRenderer.MeasureText(text, titleFont).Width + Theme.S(80), h = Theme.S(52);
                // the window is larger than the pill by pad on every side: room for the soft shadow under it
                int pad = Theme.S(14), W = w + 2 * pad, H = h + 2 * pad;
                // 16.11: placed in real pixels (the notification thread is per-monitor aware). k = how much larger this
                // monitor draws the program than the main one does (1.5 on a 150 % screen next to a 100 % main one): the
                // window is that much larger there, as the rest of the program is
                float k = screen.Scale;
                Func<float, int> R = v => (int)Math.Round(v * k);
                var a = screen.Work; int m = R(Theme.S(48)), rw = R(w), rh = R(h), rpad = R(pad);
                int x = position.EndsWith("left") ? a.Left + m : position.EndsWith("right") ? a.Right - rw - m : a.Left + (a.Width - rw) / 2;
                int y = position.StartsWith("top") ? a.Top + m : position == "center" ? a.Top + (a.Height - rh) / 2 : a.Bottom - rh - m;
                realArea = new Rectangle(x - rpad, y - rpad, R(W), R(H));
                area = new Rectangle(0, 0, W, H);
                Size = realArea.Size; Location = realArea.Location;

                var st = settings ?? new Settings();
                glass = st.OsdGlass; blur = st.OsdBlur; wantLive = st.OsdLive;
                var rect = new RectangleF(pad + 0.5f, pad + 0.5f, w - 1.5f, h - 1.5f);
                float r = h / 2f;
                pill = Theme.Round(rect, r);
                image = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                if (glass && st.OsdShine) BuildLens(W, H, rect);
                thinEdge = glass && st.OsdShine;
                bodyFocus = new PointF(Math.Max(0.3f, 1f - 2f * LensBand / rect.Width), Math.Max(0.2f, 1f - 2f * LensBand / rect.Height));

                // the shadow, once
                shadow = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var sh = new Bitmap(W, H))
                {
                    using (var sg = Graphics.FromImage(sh))
                    using (var sp = Theme.Round(new RectangleF(rect.X + Theme.S(2), rect.Y + Theme.S(st.OsdShine ? 5 : 4), rect.Width - Theme.S(4), rect.Height - Theme.S(2)), r))
                    using (var b = new SolidBrush(Color.FromArgb(st.OsdShine ? 150 : 70, 0, 0, 0)))
                    { sg.SmoothingMode = SmoothingMode.AntiAlias; sg.FillPath(b, sp); }
                    using (var s1 = Scale(sh, Math.Max(2, W / 6), Math.Max(2, H / 6))) using (var s2 = Scale(s1, W, H))
                    using (var g = Graphics.FromImage(shadow)) { g.Clear(Color.Transparent); g.DrawImageUnscaled(s2, 0, 0); }
                }

                // everything on top of the glass, once
                overlay = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(overlay))
                using (var glyphFont = new Font(family, 14f * Theme.Body.Size / 9.75f))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // frost (a faint white veil) and the darkening from the settings
                    using (var b = new SolidBrush(Color.FromArgb(glass ? 28 : 0, 255, 255, 255))) g.FillPath(b, pill);
                    int tint = (int)Math.Round(255 * (glass ? st.OsdTint : Math.Max(15, st.OsdTint)) / 100.0);
                    shadeTint = tint / 255f; shadeShadow = (st.OsdShine ? 120 : 70) / 255f;
                    Body(g, Color.FromArgb(26, 25, 24), tint);
                    if (st.OsdShine)
                    {
                        // the same glass as the cards of the app (13.11): the light comes from above and below the pane,
                        // so the top and bottom edges shine and the sides stay dim
                        var clip = g.Save();
                        g.SetClip(pill, CombineMode.Intersect);
                        // glows just inside the top and bottom edges, fading towards the sides
                        foreach (var e in new[]
                        {
                            Tuple.Create(new RectangleF(rect.X + rect.Width * 0.07f, rect.Y - Theme.S(11), rect.Width * 0.86f, Theme.S(22)), GlowTop),
                            Tuple.Create(new RectangleF(rect.X + rect.Width * 0.12f, rect.Bottom - Theme.S(8), rect.Width * 0.76f, Theme.S(16)), GlowBottom),
                        })
                            using (var ep = new GraphicsPath())
                            {
                                ep.AddEllipse(e.Item1);
                                using (var glow = new PathGradientBrush(ep) { CenterColor = Color.FromArgb(e.Item2, 255, 255, 255), SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) } })
                                    g.FillPath(glow, ep);
                            }
                        // a soft sheen on the upper half
                        using (var sheen = new LinearGradientBrush(new RectangleF(0, rect.Y, W, rect.Height), Color.White, Color.White, LinearGradientMode.Vertical))
                        {
                            sheen.InterpolationColors = new ColorBlend
                            {
                                Colors = new[] { Color.FromArgb(22, 255, 255, 255), Color.FromArgb(6, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                                Positions = new[] { 0f, 0.35f, 0.55f, 1f },
                            };
                            g.FillPath(sheen, pill);
                        }
                        // 16.11: the glass is a thick piece. Absorbed light: a little darker just inside the edge, most under
                        // the top one, clear in the middle
                        float band = Theme.S(13);
                        using (var ip = Theme.Round(RectangleF.Inflate(rect, 1, 1), r + 1))
                        using (var ib = new PathGradientBrush(ip)
                        {
                            CenterPoint = new PointF(rect.X + rect.Width / 2f, rect.Y + rect.Height * 0.6f), CenterColor = Color.FromArgb(0, 0, 0, 0),
                            SurroundColors = new[] { Color.FromArgb(ShadeAlpha, 0, 0, 0) },
                            FocusScales = new PointF(Math.Max(0.3f, 1f - 2f * band / rect.Width), Math.Max(0.2f, 1f - 2f * band / rect.Height)),
                        })
                            g.FillPath(ib, ip);
                        g.Restore(clip);
                        // the edge is not drawn with lines any more: the light on the rounded bevel is computed per pixel
                        using (var bevel = Bevel(W, H, rect)) g.DrawImageUnscaled(bevel, 0, 0);
                    }
                    // a hairline outside, so the glass still separates from a light background
                    using (var outer = Theme.Round(RectangleF.Inflate(rect, 0.6f, 0.6f), r + 0.6f))
                    using (var pen = new Pen(Color.FromArgb(st.OsdShine ? 46 : 160, 0, 0, 0), 1f)) g.DrawPath(pen, outer);

                    // the icon and the text: white with a faint shadow, readable over anything
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var center = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
                    var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None };
                    var gr = new RectangleF(pad + Theme.S(16), pad, Theme.S(30), h);
                    var tr = new RectangleF(pad + Theme.S(54), pad, w - Theme.S(60), h);
                    // centre the icon on the line of text by what is actually drawn (13.11): each icon sits
                    // differently in its em box, so the ink of the icon is put on the middle between the top of a
                    // capital and the baseline, the same for every kind of notification
                    var gi = Ink(glyph, glyphFont, gr, center, W, H);
                    var ti = Ink("H", titleFont, tr, left, W, H);
                    float dy = gi.Height > 0 && ti.Height > 0 ? (ti.Top + ti.Bottom) / 2f - (gi.Top + gi.Bottom) / 2f : 0;
                    gr.Y += dy; gi.Offset(0, dy);
                    using (var layer = GlyphLayer(glyph, glyphFont, gr, center, W, H, off, gi)) g.DrawImageUnscaled(layer, 0, 0);
                    // the text white over a faint shadow (the icon, in the accent, is on its layer above)
                    using (var s = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                        g.DrawString(text, titleFont, s, new RectangleF(tr.X, tr.Y + 1, tr.Width, tr.Height), left);
                    using (var b = new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
                        g.DrawString(text, titleFont, b, tr, left);
                }

                timer.Tick += (s, e) =>
                {
                    var t = (DateTime.Now - shown).TotalMilliseconds;
                    if (t > ms + 300) { timer.Stop(); Close(); return; }
                    alpha = t > ms ? (byte)(255 * (1 - (t - ms) / 300)) : (byte)255;
                    if (live && (DateTime.Now - lastFrame).TotalMilliseconds >= FrameMs) Frame(); // the glass follows the screen
                    else Draw();
                };
            }

            // one frame: shadow, the screen behind as glass, the overlay
            void Frame()
            {
                lastFrame = DateTime.Now;
                using (var g = Graphics.FromImage(image))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImageUnscaled(shadow, 0, 0);
                    if (glass)
                        using (var back = Frosted(realArea, area.Size, blur))
                            if (back != null)
                                using (var seen = Refract(back))
                                using (var tb = new TextureBrush(seen, WrapMode.Clamp))
                                {
                                    if (mapX == null)
                                    {
                                        float cx = area.Width / 2f, cy = area.Height / 2f, k = LensK; // slightly magnified, like through a lens
                                        var mtx = new Matrix(); mtx.Translate(cx, cy); mtx.Scale(k, k); mtx.Translate(-cx, -cy);
                                        tb.Transform = mtx;
                                    }
                                    g.FillPath(tb, pill);
                                    Shade(g, back);
                                }
                    // without the glass the pane is a clear tinted plate: the screen behind is only measured (once,
                    // before the window is up), so the plate is still dense enough over a white page
                    if (!glass)
                        using (var back = Frosted(realArea, area.Size, 0))
                            if (back != null) Shade(g, back);
                    g.DrawImageUnscaled(overlay, 0, 0);
                }
                Draw();
            }

            // 13.11: over a bright screen (a white page) the clear glass left white text hard to read. The glass is made
            // denser by how bright the screen behind is: nothing over a dark one, a smoked glass over a white one. It
            // goes under the overlay, whose frost and tint (shadeTint, from the settings) are counted in.
            void Shade(Graphics g, Bitmap back)
            {
                try
                {
                    const float dark = 25f, target = 82f;
                    float l = Frost.Bright(back);
                    if (glass) l += (255 - l) * 28f / 255f;      // the frost veil of the overlay
                    else l *= 1 - shadeShadow;                   // no picture behind: the shadow under the plate shows through it
                    l = l * (1 - shadeTint) + dark * shadeTint;  // its tint
                    if (l <= target) return;
                    int a = Math.Min(220, (int)Math.Round(255 * (l - target) / (l - dark)));
                    Body(g, Color.FromArgb(26, 25, 24), a);
                }
                catch { }
            }

            // the screen behind, blurred: scaled down (1/8, 1/14 or 1/24 by strength) and back up with bilinear filtering,
            // then a little more saturation and brightness, like light passing through glass
            // real = the place on the screen in real pixels, size = the size the picture is wanted in (16.11)
            static Bitmap Frosted(Rectangle real, Size size, int strength)
            {
                try
                {
                    using (var shot = Frost.GrabReal(real, size)) return FrostedOf(shot, strength);
                }
                catch { return null; } // e.g. the secure desktop: then only the tint
            }

            static Bitmap FrostedOf(Bitmap shot, int strength) { return Frost.Of(shot, strength); }

            // a picture of the notification over the given backdrop, composed like a frame (a check: the live window
            // is left out of screenshots, so it cannot be looked at any other way)
            public Bitmap Preview(Bitmap behind)
            {
                var res = new Bitmap(area.Width, area.Height);
                using (var shot = new Bitmap(area.Width, area.Height))
                {
                    using (var sg = Graphics.FromImage(shot)) sg.DrawImage(behind, new Rectangle(0, 0, shot.Width, shot.Height));
                    using (var g = Graphics.FromImage(res))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImageUnscaled(shot, 0, 0);
                        g.DrawImageUnscaled(shadow, 0, 0);
                        if (glass)
                            using (var back = FrostedOf(shot, blur))
                            using (var seen = Refract(back))
                            using (var tb = new TextureBrush(seen, WrapMode.Clamp))
                            { g.FillPath(tb, pill); Shade(g, back); }
                        if (!glass)
                            using (var back = FrostedOf(shot, 0)) Shade(g, back);
                        g.DrawImageUnscaled(overlay, 0, 0);
                    }
                }
                return res;
            }

            static Bitmap Scale(Bitmap src, int w, int h) { return Frost.Scale(src, w, h); }

            // the current image with the current fade alpha
            void Draw()
            {
                if (!IsHandleCreated) return;
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    // on a monitor with another scaling the picture, drawn in the program's own units, is stretched to the
                    // real size of the window (as Windows itself did before, 16.11)
                    var pic = image;
                    if (realArea.Size != image.Size)
                    {
                        if (stretched == null) stretched = new Bitmap(realArea.Width, realArea.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(stretched))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.Clear(Color.Transparent);
                            g.DrawImage(image, new Rectangle(0, 0, stretched.Width, stretched.Height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
                        }
                        pic = stretched;
                    }
                    bmp = pic.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(mem, bmp);
                    var size = pic.Size; var src = Point.Empty; var dst = realArea.Location;
                    var blend = new Blend { Op = 0, Flags = 0, Alpha = alpha, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screen);
                    if (bmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(bmp); }
                    DeleteDC(mem);
                }
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000 | 0x8; return cp; } // tool window, no activate, click-through, layered, topmost
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // left out of screen capture: then it can read the screen behind itself every frame
                // switched off in the settings: visible in screenshots, the background is read once before showing
                try { live = glass && wantLive && SetWindowDisplayAffinity(Handle, 0x11); } catch { live = false; } // WDA_EXCLUDEFROMCAPTURE
                OnTop();
                Frame();
            }

            protected override void OnShown(EventArgs e) { base.OnShown(e); OnTop(); Draw(); shown = DateTime.Now; timer.Start(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { timer.Dispose(); image.Dispose(); shadow.Dispose(); overlay.Dispose(); pill.Dispose(); if (stretched != null) stretched.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
