using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // 16.11: a small overlay over games: «144 FPS · CPU 68° 4,3 ГГц · GPU 61° 2310 МГц» in a corner of the screen.
    // One line on a dark plate, a window of its own that takes no clicks and no focus and stays on top (the way the
    // notifications are shown: a layered window drawn in real pixels on its own thread). It shows over windowed and
    // borderless games; a game in the old exclusive full-screen mode covers every window, this one too.
    // FPS comes from Fps (Windows' own events about frames, nothing is injected into the game), the rest from Sensors:
    // the processor's temperature and clock as on «Главная», the graphics card's from NVIDIA's driver. The card is asked
    // only while the overlay is on screen (asking keeps a sleeping discrete card awake), and by default the overlay is
    // on screen only over a program that covers the whole monitor.
    static class Overlay
    {
        static Settings settings;
        static SynchronizationContext ui;
        static OverlayForm form;
        static readonly object gate = new object();

        // the places in the order of the list; CornerValues: what is kept in the settings for each (0..3 the corners as
        // before, 4 the middle of the top edge, added 16.11)
        public static readonly string[] CornerNames = { "Слева сверху", "Сверху по центру", "Справа сверху", "Слева снизу", "Справа снизу" };
        public static readonly int[] CornerValues = { 0, 4, 1, 2, 3 };
        public static readonly string[] WhenNames = { "В полноэкранных программах", "Всегда" };
        public static readonly string[] SizeNames = { "Мелкий", "Средний", "Крупный", "Очень крупный", "Огромный" };
        static readonly float[] SizePx = { 12f, 15f, 19f, 24f, 30f }; // the text height at 100 % scaling

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

        public static void Init(Settings s)
        {
            settings = s;
            Application.ApplicationExit += (o, e) => { try { Fps.Stop(); } catch { } };
            Apply();
        }

        // the switch, the corner or «когда» changed
        public static void Apply()
        {
            if (settings == null) return;
            if (!settings.OverlayOn)
            {
                Fps.Stop();
                Post(f => f.Off());
                return;
            }
            lock (gate)
            {
                if (ui == null)
                {
                    var ready = new ManualResetEvent(false);
                    var t = new Thread(() =>
                    {
                        // real pixels, like the notifications: on a monitor of another scaling Windows would move and
                        // stretch a window of the program's own (system) DPI awareness
                        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
                        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                        ui = SynchronizationContext.Current;
                        form = new OverlayForm(settings);
                        ready.Set();
                        Application.Run();
                    }) { IsBackground = true, Name = "overlay" };
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    ready.WaitOne(3000);
                }
            }
            Task.Run(() => { var why = Fps.Start(); if (why != null) Store.Log("оверлей: FPS не считается: " + why); });
            Post(f => f.On());
        }

        static void Post(Action<OverlayForm> a)
        {
            var u = ui; var f = form;
            if (u == null || f == null) return;
            u.Post(_ => { try { a(f); } catch (Exception ex) { Store.Log("оверлей: " + ex.Message); } }, null);
        }

        class OverlayForm : Form
        {
            [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref Blend pblend, int flags);
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
            [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
            [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

            readonly Settings s;
            readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
            Sensors sensors;
            volatile bool reading;
            DateTime read = DateTime.MinValue;
            // the last readings, written by the worker
            volatile int cpuTemp = -1, gpuTemp = -1, gpuMhz = -1;
            double cpuMhz = double.NaN;
            Font font; float fontPx;
            string shownKey = "";

            public OverlayForm(Settings s)
            {
                this.s = s;
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000); Size = new Size(10, 10);
                timer.Tick += (o, e) => Tick();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000 | 0x8; return cp; } // tool window, no activate, click-through, layered, topmost
            }

            public void On() { shownKey = ""; timer.Start(); Tick(); }
            public void Off() { timer.Stop(); if (Visible) Hide(); }

            // the monitor to show on, or null: nothing to show over now
            Osd.Mon Target()
            {
                var mons = Osd.Monitors();
                var active = Osd.ActiveMonitor();
                var m = mons.FirstOrDefault(x => x.Handle == active) ?? mons.FirstOrDefault(x => x.Primary) ?? mons.FirstOrDefault();
                if (m == null) return null;
                return s.OverlayAlways || Osd.FullscreenOn(m) ? m : null;
            }

            void Tick()
            {
                if (!s.OverlayOn) { Off(); return; }
                Osd.Mon m = null;
                try { m = Target(); } catch { }
                if (m == null) { if (Visible) Hide(); return; }
                Poll();
                uint pid = 0;
                try { GetWindowThreadProcessId(GetForegroundWindow(), out pid); } catch { }
                Draw(m, FpsOf(pid));
            }

            // the frame rate of the program in front. A game draws from the process its window belongs to; a program
            // built on a browser engine (Chrome, Electron) draws from a helper process of the same name, so when the
            // window's own process draws nothing, its namesakes are looked at (their list is kept for 5 s)
            uint famPid; DateTime famAt = DateTime.MinValue; int[] famIds = new int[0];
            double FpsOf(uint pid)
            {
                double f = Fps.Of((int)pid);
                if (!double.IsNaN(f) || pid == 0) return f;
                try
                {
                    if (pid != famPid || (DateTime.Now - famAt).TotalSeconds > 5)
                    {
                        famPid = pid; famAt = DateTime.Now;
                        string name; using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName;
                        famIds = System.Diagnostics.Process.GetProcessesByName(name).Select(x => { int id = x.Id; x.Dispose(); return id; }).Where(id => id != pid).ToArray();
                    }
                    foreach (var id in famIds) { double v = Fps.Of(id); if (!double.IsNaN(v) && (double.IsNaN(f) || v > f)) f = v; }
                }
                catch { }
                return f;
            }

            // the sensors once a second, off this thread (the driver calls take a few milliseconds each)
            void Poll()
            {
                if (reading || (DateTime.Now - read).TotalMilliseconds < 950) return;
                reading = true; read = DateTime.Now;
                Task.Run(() =>
                {
                    try
                    {
                        if (sensors == null) sensors = new Sensors { SkipRam = true };
                        sensors.Update(true);
                        cpuTemp = sensors.Cpu.TempC; cpuMhz = sensors.Cpu.ClockMHz;
                        var g = sensors.Gpu;
                        gpuTemp = g.Ok && g.TempC > 0 ? (int)g.TempC : -1; gpuMhz = g.Ok ? (int)g.ClockMHz : -1;
                    }
                    catch { }
                    finally { reading = false; }
                });
            }

            static readonly Color Label = Color.FromArgb(0xff, 0x6e, 0x73), Value = Color.White, Unit = Color.FromArgb(0xc4, 0xc0, 0xbc), Hot = Color.FromArgb(0xff, 0xa2, 0x40);

            void Draw(Osd.Mon m, double fps)
            {
                var ru = CultureInfo.GetCultureInfo("ru-RU");
                bool en = L.En;
                // the pieces of the line: text and colour; an empty text is a wider gap between the groups
                var parts = new List<Tuple<string, Color>>();
                parts.Add(Tuple.Create(double.IsNaN(fps) ? "—" : Math.Round(fps).ToString("0"), Value)); parts.Add(Tuple.Create("FPS", Label));
                parts.Add(Tuple.Create("", Value));
                parts.Add(Tuple.Create("CPU", Label));
                parts.Add(Tuple.Create(cpuTemp > 0 ? cpuTemp + "°" : "—", cpuTemp >= 90 ? Hot : Value));
                if (!double.IsNaN(cpuMhz) && cpuMhz > 0) { parts.Add(Tuple.Create((cpuMhz / 1000).ToString("0.0", en ? CultureInfo.InvariantCulture : ru), Value)); parts.Add(Tuple.Create(en ? "GHz" : "ГГц", Unit)); }
                if (Hw.Nvidia)
                {
                    parts.Add(Tuple.Create("", Value));
                    parts.Add(Tuple.Create("GPU", Label));
                    parts.Add(Tuple.Create(gpuTemp > 0 ? gpuTemp + "°" : "—", gpuTemp >= 85 ? Hot : Value));
                    if (gpuMhz > 0) { parts.Add(Tuple.Create(gpuMhz.ToString(), Value)); parts.Add(Tuple.Create(en ? "MHz" : "МГц", Unit)); }
                }

                // 16.11: the size follows Windows' scaling of the monitor it is shown on (150 % on the laptop's own screen,
                // 100 % on the monitor beside it), like everything else on that screen. For an hour it followed the
                // screen's resolution instead, after the user found the line too big on the laptop's screen; he then
                // asked for the scaling back: the screen is small and dense, and its scaling is there for that reason.
                // It never follows the scale of Red Tune's own window (set to 150 % here, that came on top at first).
                float k = 1f;
                try { uint dx, dy; if (m.Handle != IntPtr.Zero && GetDpiForMonitor(m.Handle, 0, out dx, out dy) == 0 && dx >= 96) k = dx / 96f; } catch { }
                string key = string.Join("|", parts.Select(p => p.Item1)) + "|" + m.Bounds + "|" + s.OverlayCorner + "|" + s.OverlaySize + "|" + k + "|" + Osd.FullscreenOn(m);
                if (key == shownKey && Visible) { OnTop(); return; } // nothing changed: the picture stays
                shownKey = key;

                float px = SizePx[Math.Max(0, Math.Min(SizePx.Length - 1, s.OverlaySize))] * k;
                if (font == null || Math.Abs(fontPx - px) > 0.1f) { if (font != null) font.Dispose(); font = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel); fontPx = px; }
                int padX = (int)Math.Round(10 * k), padY = (int)Math.Round(5 * k), gap = (int)Math.Round(5 * k), group = (int)Math.Round(14 * k), margin = (int)Math.Round(12 * k);
                var fmt = StringFormat.GenericTypographic;
                var widths = new float[parts.Count];
                float total = 0; int h, textH;
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        widths[i] = parts[i].Item1.Length == 0 ? group : g.MeasureString(parts[i].Item1, font, 10000, fmt).Width;
                        total += widths[i] + (i > 0 && parts[i].Item1.Length > 0 && parts[i - 1].Item1.Length > 0 ? gap : 0);
                    }
                    // 16.11: the plate is half as tall again as the text with its margins needs (the user asked for a
                    // bigger plate around the same text); the text stands in the middle of it
                    textH = (int)Math.Ceiling(font.GetHeight(g));
                    h = (int)Math.Round((textH + 2 * padY) * 1.5f);
                }
                // 16.11: the plate is a pill, its ends are half circles; the text keeps clear of the round ends
                padX = Math.Max(padX, (int)Math.Round(h * 0.5f));
                int w = (int)Math.Ceiling(total) + 2 * padX;

                using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.Clear(Color.Transparent);
                        using (var path = new GraphicsPath())
                        {
                            float d = h - 1;
                            path.AddArc(0, 0, d, d, 90, 180); path.AddArc(w - 1 - d, 0, d, d, 270, 180);
                            path.CloseFigure();
                            using (var b = new SolidBrush(Color.FromArgb(165, 12, 12, 12))) g.FillPath(b, path);
                        }
                        float x = padX;
                        for (int i = 0; i < parts.Count; i++)
                        {
                            if (i > 0 && parts[i].Item1.Length > 0 && parts[i - 1].Item1.Length > 0) x += gap;
                            if (parts[i].Item1.Length > 0) using (var b = new SolidBrush(parts[i].Item2)) g.DrawString(parts[i].Item1, font, b, x, (h - textH) / 2f - textH * 0.05f, fmt); // a line of this font has more room under the letters than over them
                            x += widths[i];
                        }
                    }
                    // over a full-screen program the corner of the screen; over the desktop («Всегда») the corner of the work area, clear of the taskbar
                    var area = Osd.FullscreenOn(m) ? m.Bounds : m.Work;
                    // 16.11: the plate stands further from the edge of the screen by the distance its letters keep from
                    // its own top edge (asked for after the first game: the plate sat too close to the top). That distance:
                    // where the line of text starts, down to its baseline, back up by the height of a capital letter
                    // (0.70 of the size in Segoe UI). At the bottom places it is kept from the bottom edge the same way.
                    var fam = font.FontFamily;
                    float ascent = fam.GetCellAscent(font.Style) * fontPx / fam.GetEmHeight(font.Style);
                    int inner = Math.Max(0, (int)Math.Round((h - textH) / 2f - textH * 0.05f + ascent - 0.70f * fontPx));
                    // the gap to the edge of the screen equals the gap from the plate's top to its letters (the first try added
                    // the two: 39 px at the biggest size, a quarter too far down; this gives 27)
                    int edge = inner;
                    var at = s.OverlayCorner == 4 ? new Point(area.Left + (area.Width - w) / 2, area.Top + edge)
                        : new Point(s.OverlayCorner % 2 == 0 ? area.Left + margin : area.Right - margin - w, s.OverlayCorner < 2 ? area.Top + edge : area.Bottom - edge - h);
                    Push(bmp, at);
                }
                if (!Visible) Show();
                OnTop();
            }

            void Push(Bitmap pic, Point at)
            {
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hbmp = pic.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(mem, hbmp);
                    var size = pic.Size; var src = Point.Empty;
                    var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screen);
                    if (hbmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hbmp); }
                    DeleteDC(mem);
                }
            }

            // above the game's own window, which is topmost too in borderless mode
            void OnTop() { try { SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } catch { } } // HWND_TOPMOST; NOSIZE, NOMOVE, NOACTIVATE
        }
    }
}
