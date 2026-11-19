using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RedTune
{
    // 16.11: the mini panel: what is switched many times a day, in one small window at the tray icon, without opening
    // the whole program. A left click on the tray icon opens it by the icon; a click anywhere else closes it; a double
    // click on the icon (or the icon's menu) opens the full window. 17.11: the panel had a block of its own for
    // that («Red Tune / Открыть»); taken out, the icon does it.
    //   Режим питания      Тихий / Баланс / Макс / Свой, as on «Главная» (Lenovo's modes)
    //   Пресет питания     the presets of the custom mode; picking one switches to «Свой» and writes it
    //   Кривая             the Curve Optimizer presets; the one picked becomes the active one and goes to the processor
    //   Видеокарта         the graphics card presets; the one picked goes to the card
    //   Оверлей            the overlay over games on and off
    //   Очистка памяти     the cleaning of «Очистка» (was «Как после загрузки»): asks with the list of what it would close, as there
    // Every row does what its page does, through the same code; rows this computer has nothing for are not shown
    // (no Lenovo interface, no NVIDIA card, «Экспертные функции» off for the curve and the card).
    class MiniPanel : Form, IBackdrop
    {
        readonly Settings s;
        readonly PowerService power;
        readonly CurveService curve;
        readonly Action<int> setMode;
        readonly Action<string> applyCurve, applyGpu;
        readonly Func<string> gpuBusy, curveBusy; // the preset being written to the card / the processor right now, or null

        readonly Page page = new Page();
        readonly Segmented mode = new Segmented("Тихий", "Баланс", "Макс", "Свой");
        readonly DropButton powerDrop = new DropButton(), curveDrop = new DropButton(), gpuDrop = new DropButton();
        readonly Toggle overlay = new Toggle();
        readonly FlatButton cleanBtn = new FlatButton("Очистить");
        readonly Row modeRow, powerRow, curveRow, gpuRow, cleanRow;
        readonly Timer refresh = new Timer { Interval = 900 };
        const string CleanSub = "Закрыть лишние программы"; // the page's own line does not fit this narrow row
        static readonly int[] Modes = { Lenovo.Quiet, Lenovo.Balance, Lenovo.Performance, Lenovo.Custom };
        bool filling;
        DateTime hidden = DateTime.MinValue;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MiniPanel(Settings s, PowerService power, CurveService curve, Action<int> setMode, Action<string> applyCurve, Action<string> applyGpu, Func<string> gpuBusy, Func<string> curveBusy)
        {
            this.gpuBusy = gpuBusy; this.curveBusy = curveBusy;
            this.s = s; this.power = power; this.curve = curve; this.setMode = setMode; this.applyCurve = applyCurve; this.applyGpu = applyGpu;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; Font = Theme.Body; BackColor = Theme.Window;
            Width = Theme.S(440); KeyPreview = true; Text = "Red Tune — мини-панель";
            // as tall as its rows, never scrolls: the rows run the whole width with the same gap on both sides
            page.NoScroll = true; page.AutoScroll = false; page.Side = 14; page.TopGap = 14;
            Controls.Add(page);

            modeRow = page.Row("Режим питания", null, mode);
            powerRow = page.Row("Пресет питания", "Включает режим «Свой»", powerDrop);
            curveRow = page.Row("Кривая", null, curveDrop);
            gpuRow = page.Row("Видеокарта", null, gpuDrop);
            page.Row("Оверлей", "FPS, температура и частота поверх игр", overlay);
            cleanRow = page.Row("Очистка памяти", CleanSub, cleanBtn);
            // the blocks have round ends (the small ones are round all the way): everything stands further in
            foreach (Control c in page.Controls) { var row = c as Row; if (row != null) row.Inset = Theme.S(RowInset); }

            mode.Changed += (o, e) =>
            {
                if (filling || mode.Selected < 0) return;
                setMode(Modes[mode.Selected]); Expect(900);
                // 16.11: the presets belong to «Свой»; with one of the firmware's own modes picked their row is put away
                // at once (and comes back with «Свой»), as on «Главная»
                powerRow.Visible = Modes[mode.Selected] == Lenovo.Custom;
                FitHeight();
                refresh.Stop(); refresh.Start();
            };
            powerDrop.Changed += (o, e) =>
            {
                if (filling || powerDrop.Index < 0 || powerDrop.Index >= power.Data.Presets.Count) return;
                Expect(900);
                power.Apply(power.Data.Presets[powerDrop.Index], ok => { if (Visible) Fill(); });
            };
            curveDrop.Changed += (o, e) => { if (!filling && curveDrop.Index >= 0 && curveDrop.Index < curveDrop.Items.Length) { applyCurve(curveDrop.Items[curveDrop.Index]); Expect(300); } };
            gpuDrop.Changed += (o, e) => { if (!filling && gpuDrop.Index >= 0 && gpuDrop.Index < gpuDrop.Items.Length) { applyGpu(gpuDrop.Items[gpuDrop.Index]); Expect(300); } };
            overlay.CheckedChanged += (o, e) => { if (filling) return; s.OverlayOn = overlay.Checked; Store.SaveSettings(s); Overlay.Apply(); };
            cleanBtn.Click += (o, e) => CleanPage.RunClean(this, t => cleanRow.SetSub(t ?? CleanSub), busy => cleanBtn.Enabled = !busy);
            // the mode and the active presets a moment after a switch (the firmware and the processor take a moment)
            refresh.Tick += (o, e) => { refresh.Stop(); if (Visible) Fill(); };
            power.Changed += () => { try { if (IsHandleCreated && Visible) BeginInvoke((Action)Fill); } catch { } };
            Hook(page);
            watch.Tick += (o, e) => Watch();
            VisibleChanged += (o, e) => { if (Visible) watch.Start(); else { watch.Stop(); anim.Stop(); liveTimer.Stop(); Fine(false); LiveStats(); } };
            liveTimer.Tick += (o, e) => LiveTick();
            anim.Tick += (o, e) => AnimTick();
        }

        // 16.11: a mode of the firmware brings its own graphics card preset and curve, and writing them takes a second
        // or two (the card is read first, then written; the curve goes through a helper program). Until then the
        // panel showed the old preset as if nothing was going on. Now, while one is being written, its list shows the
        // preset that is on its way with a running dot and takes no clicks; when the write is over the panel reads
        // what is on again. Right after a click that will start a write (a mode, a preset) the lists are held for a
        // moment already: the write itself begins a few tenths of a second later.
        readonly Timer watch = new Timer { Interval = 160 };
        DateTime expectUntil = DateTime.MinValue;
        bool wasBusy; int frame;
        static readonly string[] Dots = { "•··", "·•·", "··•", "·•·" };

        void Expect(int ms) { expectUntil = DateTime.Now.AddMilliseconds(ms); Watch(); }

        void Watch()
        {
            string g = null, c = null;
            try { g = gpuBusy(); c = curveBusy(); } catch { }
            bool expect = DateTime.Now < expectUntil, busy = g != null || c != null || expect;
            frame++;
            Loading(gpuRow, gpuDrop, g, expect);
            Loading(curveRow, curveDrop, c, expect);
            if (busy) wasBusy = true;
            else if (wasBusy) { wasBusy = false; Fill(); } // the writes are over: what is on now
        }

        void Loading(Row row, DropButton drop, string name, bool expect)
        {
            if (!row.Visible) return;
            if (name != null) { drop.Enabled = false; drop.Text = name + "  " + Dots[frame / 2 % Dots.Length]; }
            else if (expect) drop.Enabled = false;
        }

        // a tool window: not in Alt+Tab, not on the taskbar
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // the window is cut to its blocks (Region): no corners of Windows' own, no system border
            try { int square = 1; DwmSetWindowAttribute(Handle, 33, ref square, 4); int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, 4); } catch { }
        }

        // a click anywhere else closes it; its own question windows (the cleaning asks before closing programs) do not
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (OwnedForms.Any(f => f.Visible)) return;
            hidden = DateTime.Now; Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == System.Windows.Forms.Keys.Escape) Hide(); base.OnKeyDown(e); }

        // what is on now, read afresh every time the panel opens
        void Fill()
        {
            filling = true;
            try
            {
                bool lenovo = Hw.LenovoPower;
                modeRow.Visible = lenovo;
                powerRow.Visible = lenovo && power.Mode == Lenovo.Custom; // the presets of «Свой» only in «Свой»
                if (lenovo)
                {
                    mode.Selected = Array.IndexOf(Modes, power.Mode);
                    var names = power.Data.Presets.Select(p => p.Name).ToArray();
                    powerDrop.Set(names.Length > 0 ? names : new[] { "—" }, power.Active != null ? Array.IndexOf(names, power.Active.Name) : -1);
                    powerDrop.Enabled = names.Length > 0;
                }
                var curves = curve.Presets.Select(p => p.Name).ToArray();
                curveRow.Visible = Hw.Curve && s.Expert && curves.Length > 0;
                if (curveRow.Visible) { curveDrop.Set(curves, Array.IndexOf(curves, curve.Active.Name)); curveDrop.Enabled = true; }
                string[] gpus = new string[0];
                if (Hw.Nvidia && s.Expert) { try { GpuStore.Load(); gpus = GpuStore.Presets.Select(p => p.Name).ToArray(); } catch { } }
                gpuRow.Visible = gpus.Length > 0;
                if (gpuRow.Visible) { gpuDrop.Set(gpus, Array.IndexOf(gpus, GpuStore.Active)); gpuDrop.Enabled = true; }
                overlay.Set(s.OverlayOn);
            }
            catch (Exception ex) { Store.Log("мини-панель: " + ex.Message); }
            filling = false;
            // a write still going on (the panel reads its state while a mode's preset is on its way): the lists stay held
            try { bool ex = DateTime.Now < expectUntil; Loading(gpuRow, gpuDrop, gpuBusy(), ex); Loading(curveRow, curveDrop, curveBusy(), ex); } catch { }
            FitHeight();
        }

        // as tall as its rows
        void FitHeight()
        {
            Height = Theme.S(900); // room for every row while they are placed
            page.Relayout();
            // 16.11: the four modes do not fit beside the row's title here and go under it; there they are stretched to
            // the width of the row (from where the title starts to the usual gap on the right) instead of standing in
            // its right-hand part at their own width
            if (modeRow.Visible && modeRow.Width > 0)
            {
                int room = modeRow.Width - Theme.S(16) - Theme.S(14) - 2 * Theme.S(RowInset);
                mode.MinItemWidth = Math.Max(0, (room - 3 * Theme.S(4)) / 4);
                page.Relayout();
            }
            int bottom = 0;
            foreach (Control c in page.Controls) if (c.Visible) bottom = Math.Max(bottom, c.Bottom);
            Height = bottom + Theme.S(14) + 2;
            // opened over a taskbar at the bottom, it keeps its bottom edge when a row comes or goes
            if (anchorBottom) { home.Y = anchorArea.Bottom - Theme.S(8) - Blocks().Bottom; Put(); }
            page.Relayout();
            // live glass reads the screen under the panel only: a panel that has grown or moved reads it again at once
            if (live && Visible && shotRaw != null && (shotRaw.Size != Size || shotAt != home)) Snap();
            Glass();
        }

        // the blocks together, in the window's coordinates: the window itself is wider and taller than they are
        Rectangle Blocks()
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (Control c in page.Controls)
            {
                if (!c.Visible || !(c is Row)) continue;
                var at = PointToClient(c.PointToScreen(Point.Empty));
                l = Math.Min(l, at.X); t = Math.Min(t, at.Y); r = Math.Max(r, at.X + c.Width); b = Math.Max(b, at.Y + c.Height);
            }
            return l == int.MaxValue ? ClientRectangle : Rectangle.FromLTRB(l, t, r, b);
        }

        // 17.11, how it comes up: from the side of the taskbar, a few pixels, while it turns from clear to solid.
        // home: where it stands when it has arrived; slide: how far from there it is now
        Point home; int slide, slideFrom;
        const int AnimMs = 200;
        readonly Timer anim = new Timer { Interval = 10 };
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();

        void Put() { Location = new Point(home.X, home.Y + slide); }

        // For checking the look while building it: with live glass the panel is left out of screen capture, so no
        // screenshot shows it. When the file mini-shot.request lies in the data folder, the panel, once it has come
        // up, saves its own picture (as it painted itself) to mini-shot.png there and removes the request.
        void DumpIfAsked()
        {
            try
            {
                string ask = System.IO.Path.Combine(Store.DataDir, "mini-shot.request");
                if (!System.IO.File.Exists(ask)) return;
                System.IO.File.Delete(ask);
                wantFrame = live; // and the first frame of the live glass, as it is put on the screen (mini-frame.png)
                using (var b = new Bitmap(Width, Height))
                {
                    DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
                    b.Save(System.IO.Path.Combine(Store.DataDir, "mini-shot.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Store.Log("мини-панель: снимок записан, окно " + Bounds + ", блоки " + Blocks());
            }
            catch (Exception ex) { Store.Log("мини-панель: снимок: " + ex.Message); }
        }

        void AnimTick()
        {
            if (!Visible) { anim.Stop(); return; }
            double t = animClock.Elapsed.TotalMilliseconds / AnimMs;
            if (t >= 1)
            {
                anim.Stop(); slide = 0; Put(); Opacity = 1;
                if (live) { frames = redraws = 0; sameTicks = redrawTicks = 0; tGrab = tBlur = tPic = tPaint = 0; liveFrom = DateTime.Now; lastFrameAt = 0; waitMs = 15; still = 0; Fine(true); liveTimer.Start(); }
                DumpIfAsked();
                return;
            }
            double e = 1 - Math.Pow(1 - t, 3); // fast at first, settling at the end
            slide = (int)Math.Round(slideFrom * (1 - e)); Put();
            Opacity = Math.Min(0.99, t * 1.7);
        }

        // ---------- no back, the blocks only, each a pane of glass (16.11) ----------
        // The panel had a dark back with the blocks on it. Now the window is cut to the blocks themselves (Region): what
        // is between and around them is the screen behind, seen and clicked through. A block is glass, made like the
        // drop-downs: the screen under it blurred, the card colour over it as dense as that screen is bright, the
        // card's sheen and rim. The screen is read once, when the panel opens (before it shows), over the room the
        // panel can take; the picture of all blocks is drawn from it again whenever a row comes or goes. The window
        // is cut one pixel outside each block and that pixel shows the screen as read, so the round corners end in
        // a smooth edge, not in the steps a cut-out window has.
        Bitmap shotRaw, shotBlur, backdrop; // shotBlur: the read screen blurred, at a quarter of its size; stretched under a block it is the glass's blur
        Point shotAt;  // where on the screen the read picture starts
        int tint = 255;
        public Bitmap Backdrop { get { return backdrop; } }

        // 16.11, live glass: the glass follows what moves under the panel (a video, a game), as the notifications' glass
        // does, and under the same switch (Настройки → Уведомления → «Живое стекло»). The window is left out of screen
        // capture (so the screen behind it can be read while it is on screen: the price is that the panel is not in
        // screenshots or recordings) and the screen under it is read 15 times a second. A frame equal to the last one
        // costs the read and a compare; only a changed one is blurred, drawn and painted. What it took is written to
        // the log when the panel closes.
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] static extern int memcmp(IntPtr a, IntPtr b, UIntPtr n);
        bool live;
        Bitmap lastReal; // live glass on a monitor of another scaling: the last frame in the screen's real pixels
        readonly Timer liveTimer = new Timer { Interval = 15 };

        // 17.11: the glass ran behind what moved under it: 15 frames a second, each a good 30 ms late. Now:
        //   · the screen is read straight into memory of this panel's own (two pieces, the frame before and this one)
        //     instead of a new GDI+ picture each time, and the two are compared where they lie;
        //   · the frost is done by hand (Frosted);
        //   · the pace is not fixed: while the screen changes, the next frame comes as soon as a frame's own cost
        //     allows (1.6 times its time apart, so the panel takes at most some 60 % of one processor core), up to 60 a
        //     second; while nothing changes the screen is looked at 30 times a second, after a second and a half of that 20 times.
        // What cannot be had this way is no delay at all: a frame is read after it was shown, so the glass is always
        // one to two frames behind the screen.
        sealed class Tap : IDisposable
        {
            public IntPtr Dc, Bmp, Old, Bits; public Bitmap Pic; public int W, H;
            public static Tap Make(int w, int h)
            {
                var t = new Tap { W = w, H = h };
                var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                t.Bmp = CreateDIBSection(IntPtr.Zero, ref info, 0, out t.Bits, IntPtr.Zero, 0);
                if (t.Bmp == IntPtr.Zero || t.Bits == IntPtr.Zero) { t.Dispose(); return null; }
                t.Dc = CreateCompatibleDC(IntPtr.Zero); t.Old = SelectObject(t.Dc, t.Bmp);
                t.Pic = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, t.Bits);
                return t;
            }
            public void Dispose()
            {
                if (Pic != null) { Pic.Dispose(); Pic = null; }
                if (Dc != IntPtr.Zero) { if (Old != IntPtr.Zero) SelectObject(Dc, Old); DeleteDC(Dc); Dc = IntPtr.Zero; }
                if (Bmp != IntPtr.Zero) { DeleteObject(Bmp); Bmp = IntPtr.Zero; }
                Bits = IntPtr.Zero;
            }
        }
        Tap tapNow, tapNext;
        Tap realNow, realNext; bool realSeen; // the same for a monitor of another scaling: frames in its real pixels

        static unsafe Bitmap StretchFrom(Tap src, int w, int h)
        {
            var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try { StretchTo((byte*)src.Bits, src.W * 4, src.W, src.H, (byte*)dd.Scan0, dd.Stride, w, h); }
            finally { dst.UnlockBits(dd); }
            return dst;
        }

        // 17.11: the print, the buttons and the switch took 7.5 ms of a frame: with every new picture of the glass each
        // control drew itself again (rounded shapes, gradients, text), though nothing about it had changed. Now what
        // the controls draw is kept apart from the glass, as a layer of its own:
        //   · the whole panel is drawn twice into memory, once over black and once over white (Solid). Where the two
        //     pictures agree the controls drew something solid; where they differ, the difference is how much of the
        //     glass shows through there (per colour, so the fine edges of print are kept as drawn);
        //   · a frame is then the glass with this layer over it (Frame), copied straight onto the panel and its
        //     controls (Present); nobody draws itself;
        //   · the layer is taken again only after a control asked to be redrawn (the mouse over a button, a switch
        //     moving, a list changing: Invalidated) or the rows changed.
        // A control that redraws itself in between does it the old way, over the glass alone; the next tick of the
        // live glass shows the layered picture again.
        Color? solid;
        public Color? Solid { get { return solid; } }
        public bool Whole { get { return false; } } // this panel's controls draw themselves over its picture
        public void Fresh() { }
        bool inkDirty = true, framed, wantFrame;
        uint[] inkK, inkD; // what the controls draw (as over black) and how much of the glass shows through, per colour
        Tap shown;         // the glass with the controls over it
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Control, Point>> spots = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Control, Point>>();

        void Hook(Control c) { c.Invalidated += (o, e) => inkDirty = true; foreach (Control k in c.Controls) Hook(k); }

        // every control that is on show and where it stands in the window, parents first
        void Spot(Control c)
        {
            if (!c.Visible) return;
            spots.Add(new System.Collections.Generic.KeyValuePair<Control, Point>(c, PointToClient(c.PointToScreen(Point.Empty))));
            foreach (Control k in c.Controls) Spot(k);
        }

        unsafe void Ink()
        {
            int w = ClientSize.Width, h = ClientSize.Height, n = w * h;
            if (w <= 0 || h <= 0) return;
            if (inkK == null || inkK.Length != n) { inkK = new uint[n]; inkD = new uint[n]; }
            var all = new Rectangle(0, 0, w, h);
            using (var black = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var white = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                try { solid = Color.Black; DrawToBitmap(black, all); solid = Color.White; DrawToBitmap(white, all); }
                finally { solid = null; }
                var bd = black.LockBits(all, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var wd = white.LockBits(all, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    fixed (uint* pk = inkK, pd = inkD)
                        for (int y = 0; y < h; y++)
                        {
                            uint* b = (uint*)((byte*)bd.Scan0 + (long)y * bd.Stride), v = (uint*)((byte*)wd.Scan0 + (long)y * wd.Stride);
                            uint* k = pk + y * w, d = pd + y * w;
                            for (int x = 0; x < w; x++)
                            {
                                uint kb = b[x] & 0xFFFFFF, wb = v[x] & 0xFFFFFF;
                                if (kb == 0 && wb == 0xFFFFFF) { k[x] = 0; d[x] = 0xFFFFFF; continue; } // nothing drawn here
                                int d0 = (int)(wb & 255) - (int)(kb & 255), d1 = (int)((wb >> 8) & 255) - (int)((kb >> 8) & 255), d2 = (int)((wb >> 16) & 255) - (int)((kb >> 16) & 255);
                                if (d0 < 0) d0 = 0; if (d1 < 0) d1 = 0; if (d2 < 0) d2 = 0;
                                k[x] = kb; d[x] = (uint)(d0 | d1 << 8 | d2 << 16);
                            }
                        }
                }
                finally { black.UnlockBits(bd); white.UnlockBits(wd); }
            }
            inkDirty = false;
        }

        unsafe bool Frame()
        {
            int w = ClientSize.Width, h = ClientSize.Height, n = w * h;
            if (backBits == IntPtr.Zero || dibW != w || dibH != h || inkK == null || inkK.Length != n) return false;
            if (shown == null || shown.W != w || shown.H != h) { if (shown != null) shown.Dispose(); shown = Tap.Make(w, h); framed = false; }
            if (shown == null) return false;
            uint* glass = (uint*)backBits, o = (uint*)shown.Bits;
            fixed (uint* pk = inkK, pd = inkD)
                for (int i = 0; i < n; i++)
                {
                    uint d = pd[i];
                    if (d == 0xFFFFFF) { o[i] = glass[i]; continue; }
                    uint k = pk[i];
                    if (d == 0) { o[i] = k | 0xFF000000u; continue; }
                    uint p = glass[i], d0 = d & 255, d1 = (d >> 8) & 255, d2 = (d >> 16) & 255;
                    uint b = (k & 255) + (((p & 255) * (d0 + (d0 >> 7))) >> 8), g = ((k >> 8) & 255) + ((((p >> 8) & 255) * (d1 + (d1 >> 7))) >> 8), r = ((k >> 16) & 255) + ((((p >> 16) & 255) * (d2 + (d2 >> 7))) >> 8);
                    if (b > 255) b = 255; if (g > 255) g = 255; if (r > 255) r = 255;
                    o[i] = b | g << 8 | r << 16 | 0xFF000000u;
                }
            framed = true;
            return true;
        }

        // the frame straight onto the panel and each of its controls (a control's own piece of it)
        void Present()
        {
            foreach (var s in spots)
            {
                var c = s.Key;
                if (!c.Visible || !c.IsHandleCreated) continue;
                IntPtr dc = GetDC(c.Handle);
                if (dc == IntPtr.Zero) continue;
                try { BitBlt(dc, 0, 0, c.Width, c.Height, shown.Dc, s.Value.X, s.Value.Y, 0x00CC0020); }
                finally { ReleaseDC(c.Handle, dc); }
            }
        }

        // the screen has not changed, but a control has: the layered picture again, so the whole panel stays drawn one way
        void Again()
        {
            if (!inkDirty || !framed) return;
            try { Ink(); if (Frame()) Present(); } catch (Exception ex) { Store.Log("мини-панель: слой надписей: " + ex.Message); }
        }
        long lastFrameAt; double waitMs; int still; bool fine;
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        void DropTaps()
        {
            if (shotRaw != null && !shotOwned) DropShot(); // it lives in one of them
            if (tapNow != null) { tapNow.Dispose(); tapNow = null; }
            if (tapNext != null) { tapNext.Dispose(); tapNext = null; }
            if (realNow != null) { realNow.Dispose(); realNow = null; }
            if (realNext != null) { realNext.Dispose(); realNext = null; }
            realSeen = false;
        }

        // the timer of Windows ticks every 15.6 ms unless asked for finer: asked while the live glass runs
        void Fine(bool on)
        {
            if (on == fine) return;
            try { if (on) timeBeginPeriod(1); else timeEndPeriod(1); fine = on; } catch { }
        }
        int frames, redraws; long sameTicks, redrawTicks; DateTime liveFrom;
        long tGrab, tBlur, tPic, tPaint; // where a redrawn frame's time goes, for the log

        // the screen behind: over the room the panel can take (read once), or just under the panel (live: read every frame)
        void Snap()
        {
            try
            {
                int tall = Theme.S(900);
                var r = live ? new Rectangle(home, Size) : new Rectangle(home.X, anchorBottom ? home.Y + Height - tall : home.Y, Width, tall);
                Take(Frost.Grab(r), r.Location, false);
            }
            catch (Exception ex) { Store.Log("мини-панель: экран не прочитался: " + ex.Message); }
        }

        // 17.11: how matt the glass is: the screen is made this many times smaller and brought back. It was 14, the
        // frost of the drop-downs; through that nothing could be told apart, so the bending of the glass (GlassPanes)
        // could not be seen either. Now shapes and colours stay, small print does not (at 4 print behind the panel
        // could nearly be read and got in the way of the panel's own; 6 was a little too matt)
        const int Frosting = 5;

        // The picture as seen through matt glass, the same size; bright: how bright it is at its bright end (what the
        // density of the glass goes by). 17.11: written by hand. It was four GDI+ scalings and took 9–11 ms of a 30 ms
        // frame; this is the same picture in about a quarter of that:
        //   · every 4 × 4 pixels averaged into one (a quarter of the size);
        //   · that small picture softened once more (each pixel with its neighbours, 1-2-1 across and down), a little
        //     more colour and light put into it (Frost.Vivid's numbers), its brightness measured;
        //   · stretched back to the full size (Stretch).
        internal static unsafe Bitmap Frosted(Bitmap shot, out float bright)
        {
            int w = shot.Width, h = shot.Height, qw = Math.Max(2, (w + 3) / 4), qh = Math.Max(2, (h + 3) / 4);
            var q = new uint[qw * qh]; var t = new uint[qw * qh];
            var sum = new int[qw * 3];
            var sd = shot.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, GlassPanes.Fmt(shot));
            try
            {
                fixed (int* ps = sum) fixed (uint* pq = q)
                    for (int y = 0; y < h; y++)
                    {
                        uint* row = (uint*)((byte*)sd.Scan0 + (long)y * sd.Stride);
                        for (int x = 0; x < w; x++)
                        {
                            uint c = row[x]; int* a = ps + (x >> 2) * 3;
                            a[0] += (int)(c & 255); a[1] += (int)((c >> 8) & 255); a[2] += (int)((c >> 16) & 255);
                        }
                        if ((y & 3) == 3 || y == h - 1)
                        {
                            int rows = (y & 3) + 1; uint* o = pq + (y >> 2) * qw;
                            for (int cx = 0; cx < qw; cx++)
                            {
                                int n = rows * Math.Min(4, w - cx * 4); int* a = ps + cx * 3;
                                if (n > 0) o[cx] = (uint)(a[0] / n) | (uint)(a[1] / n) << 8 | (uint)(a[2] / n) << 16;
                                a[0] = a[1] = a[2] = 0;
                            }
                        }
                    }
            }
            finally { shot.UnlockBits(sd); }
            if ((h + 3) / 4 < qh) Array.Copy(q, 0, q, qw, qw); // a picture under 5 pixels tall: the second row as the first
            const uint RB = 0xFF00FF;
            fixed (uint* pq = q, pt = t)
            {
                // softened across, then down (red and blue together: they sit a byte apart)
                for (int y = 0; y < qh; y++)
                {
                    uint* s = pq + y * qw, o = pt + y * qw;
                    for (int x = 0; x < qw; x++)
                    {
                        uint a = s[x > 0 ? x - 1 : 0], b = s[x], c = s[x < qw - 1 ? x + 1 : x];
                        o[x] = ((((a & RB) + 2 * (b & RB) + (c & RB)) >> 2) & RB) | ((((a >> 8) & 255) + 2 * ((b >> 8) & 255) + ((c >> 8) & 255)) >> 2) << 8;
                    }
                }
                // the brightness: over a grid of 24 × 24 cells, as Frost.Bright does on its small picture
                const int cells = 24;
                var lumSum = new long[cells * cells]; var lumN = new int[cells * cells];
                for (int y = 0; y < qh; y++)
                {
                    uint* up = pt + (y > 0 ? y - 1 : 0) * qw, mid = pt + y * qw, dn = pt + (y < qh - 1 ? y + 1 : y) * qw, o = pq + y * qw;
                    int cy = y * cells / qh;
                    for (int x = 0; x < qw; x++)
                    {
                        uint a = up[x], b = mid[x], c = dn[x];
                        uint rb = (((a & RB) + 2 * (b & RB) + (c & RB)) >> 2) & RB, gg = (((a >> 8) & 255) + 2 * ((b >> 8) & 255) + ((c >> 8) & 255)) >> 2;
                        // a little more colour (1.35) and light (1.06)
                        int bl = (int)(rb & 255), gr = (int)gg, rd = (int)(rb >> 16), lum = 54 * rd + 183 * gr + 19 * bl; // × 256
                        int nb = ((bl * 346 * 256 - lum * 90) >> 8) * 271 >> 16, ng = ((gr * 346 * 256 - lum * 90) >> 8) * 271 >> 16, nr = ((rd * 346 * 256 - lum * 90) >> 8) * 271 >> 16;
                        if (nb < 0) nb = 0; else if (nb > 255) nb = 255;
                        if (ng < 0) ng = 0; else if (ng > 255) ng = 255;
                        if (nr < 0) nr = 0; else if (nr > 255) nr = 255;
                        o[x] = (uint)nb | (uint)ng << 8 | (uint)nr << 16 | 0xFF000000u;
                        int cell = cy * cells + x * cells / qw;
                        lumSum[cell] += 54 * nr + 183 * ng + 19 * nb; lumN[cell]++;
                    }
                }
                var lums = new System.Collections.Generic.List<float>(cells * cells);
                for (int i = 0; i < lumSum.Length; i++) if (lumN[i] > 0) lums.Add(lumSum[i] / (256f * lumN[i]));
                lums.Sort();
                bright = lums.Count > 0 ? lums[(int)(lums.Count * 0.9f)] : 0;
                var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var dd = dst.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { StretchTo((byte*)pq, qw * 4, qw, qh, (byte*)dd.Scan0, dd.Stride, w, h); }
                finally { dst.UnlockBits(dd); }
                return dst;
            }
        }

        bool shotOwned = true; // shotRaw is this panel's to dispose of (not so for a frame that lives in a Tap)

        void DropShot()
        {
            if (shotRaw != null && shotOwned) shotRaw.Dispose();
            shotRaw = null; shotOwned = true;
        }

        void Take(Bitmap shot, Point at, bool smooth, bool owned = true)
        {
            float bright;
            Bitmap blur = Frosted(shot, out bright);
            int t = DarkRenderer.TintOf(bright);
            // live: the density of the glass follows the screen a few frames behind, so it does not pump with a moving picture
            tint = smooth && shotRaw != null ? (tint * 3 + t + 2) / 4 : t;
            if (shotRaw != shot) DropShot();
            if (shotBlur != null) shotBlur.Dispose();
            shotRaw = shot; shotOwned = owned; shotBlur = blur; shotAt = at;
        }

        // The quarter-size blurred picture stretched to the full size, by hand: GDI+ takes 19 ms for this stretch
        // whichever way it is asked (measured 17.11), a plain loop over the pixels a few. Each pixel is the four
        // nearest ones of the small picture mixed by distance (bilinear), in whole numbers out of 256.
        static unsafe Bitmap Stretch(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            int sw = src.Width, sh = src.Height;
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try { StretchTo((byte*)sd.Scan0, sd.Stride, sw, sh, (byte*)dd.Scan0, dd.Stride, w, h); }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        // sw × sh pixels at s0 (rows sStride bytes apart) to w × h pixels at d0; sw and sh at least 2
        static unsafe void StretchTo(byte* s0, int sStride, int sw, int sh, byte* d0, int dStride, int w, int h)
        {
            var xi = new int[w]; var xw = new uint[w];
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) * sw / w - 0.5f; if (fx < 0) fx = 0;
                int i = (int)fx; if (i > sw - 2) { i = sw - 2; fx = sw - 1; }
                xi[x] = i; xw[x] = (uint)Math.Min(256, (int)((fx - i) * 256));
            }
            fixed (int* pxi = xi) fixed (uint* pxw = xw)
                for (int y = 0; y < h; y++)
                {
                    float fy = (y + 0.5f) * sh / h - 0.5f; if (fy < 0) fy = 0;
                    int j = (int)fy; if (j > sh - 2) { j = sh - 2; fy = sh - 1; }
                    uint wy = (uint)Math.Min(256, (int)((fy - j) * 256)), vy = 256 - wy;
                    uint* r0 = (uint*)(s0 + (long)j * sStride), r1 = (uint*)(s0 + (long)(j + 1) * sStride);
                    uint* o = (uint*)(d0 + (long)y * dStride);
                    for (int x = 0; x < w; x++)
                    {
                        int i = pxi[x]; uint wx = pxw[x], vx = 256 - wx;
                        uint a = r0[i], b = r0[i + 1], c = r1[i], d = r1[i + 1];
                        // red and blue together (they sit a byte apart), green alone
                        uint rbT = (((a & 0xFF00FF) * vx + (b & 0xFF00FF) * wx) >> 8) & 0xFF00FF, rbB = (((c & 0xFF00FF) * vx + (d & 0xFF00FF) * wx) >> 8) & 0xFF00FF;
                        uint gT = (((a >> 8) & 0xFF) * vx + ((b >> 8) & 0xFF) * wx) >> 8, gB = (((c >> 8) & 0xFF) * vx + ((d >> 8) & 0xFF) * wx) >> 8;
                        uint rb = ((rbT * vy + rbB * wy) >> 8) & 0xFF00FF, gg = ((gT * vy + gB * wy) >> 8) & 0xFF;
                        o[x] = 0xFF000000u | rb | (gg << 8);
                    }
                }
        }

        // plain: no image attributes at all. With them (even only the edge mode) GDI+ takes its slow road, which is
        // nothing on a small picture and 19 ms on the full-size one
        static Bitmap Small(Bitmap src, int w, int h, InterpolationMode how = InterpolationMode.HighQualityBilinear, System.Drawing.Imaging.ImageAttributes look = null, bool plain = false)
        {
            var b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = how; g.PixelOffsetMode = PixelOffsetMode.Half;
                if (plain) g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
                else if (look != null) g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, look);
                else using (var at = new System.Drawing.Imaging.ImageAttributes()) { at.SetWrapMode(WrapMode.TileFlipXY); g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, at); }
            }
            return b;
        }

        // a little more colour and light, like light through glass (Frost.Vivid's numbers). Applied to the small blurred
        // picture: on the stretch under each block it sent GDI+ down its slow road (21 ms a frame)
        static System.Drawing.Imaging.ImageAttributes Vivid()
        {
            const float sat = 1.35f, lit = 1.06f;
            float r = (1 - sat) * 0.2126f, gg = (1 - sat) * 0.7152f, b = (1 - sat) * 0.0722f;
            var m = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { (r + sat) * lit, r * lit, r * lit, 0f, 0f },
                new[] { gg * lit, (gg + sat) * lit, gg * lit, 0f, 0f },
                new[] { b * lit, b * lit, (b + sat) * lit, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
            var at = new System.Drawing.Imaging.ImageAttributes();
            at.SetColorMatrix(m); at.SetWrapMode(WrapMode.TileFlipXY);
            return at;
        }

        void Glass() { Map(); Picture(); Cut(); inkDirty = true; Invalidate(true); }

        // 17.11: the blocks are thick glass now (GlassPanes): a lit bevel round each, the screen bent in it, a shadow
        // under it. Where the blocks are is laid out here, whenever a row comes or goes
        GlassPanes panes;

        void Map()
        {
            var blocks = new System.Collections.Generic.List<Rectangle>();
            foreach (Control c in page.Controls)
            {
                if (!c.Visible || !(c is Row)) continue;
                blocks.Add(new Rectangle(PointToClient(c.PointToScreen(Point.Empty)), c.Size));
            }
            spots.Clear(); Spot(page);
            try { panes = new GlassPanes(ClientSize.Width, ClientSize.Height, blocks, CardRad); }
            catch (Exception ex) { panes = null; Store.Log("мини-панель: стекло не построилось: " + ex.Message); }
        }

        // the picture behind the controls: the screen as read between the blocks, a slab of glass under each block.
        // Made straight into the memory of a GDI bitmap that sits in a memory DC: the controls copy their piece of it
        // with BitBlt, several times faster than GDI+ draws a piece of a Bitmap (painting all controls took 14 ms a
        // frame), and nothing is copied in between
        void Picture()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (shotRaw == null || shotBlur == null || w <= 0 || h <= 0) return;
            if (panes == null || panes.W != w || panes.H != h) Map();
            if (panes == null || !Dib(w, h)) return;
            // by where the panel stands when it has arrived: while it is on its way up, the picture is already that one
            try { panes.Compose(shotRaw, shotBlur, home.X - shotAt.X, home.Y - shotAt.Y, tint, Theme.Card, backBits); }
            catch (Exception ex) { Store.Log("мини-панель: кадр стекла: " + ex.Message); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        IntPtr backDc, backBmp, backOld, backBits; int dibW, dibH;
        public IntPtr BackdropDc { get { return backDc; } }

        // the bitmap the picture is made in, as large as the window (top row first, 32 bits a pixel)
        bool Dib(int w, int h)
        {
            if (backDc != IntPtr.Zero && dibW == w && dibH == h) return true;
            DropDc();
            try
            {
                var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                backBmp = CreateDIBSection(IntPtr.Zero, ref info, 0, out backBits, IntPtr.Zero, 0);
                if (backBmp == IntPtr.Zero || backBits == IntPtr.Zero) { DropDc(); return false; }
                backDc = CreateCompatibleDC(IntPtr.Zero); backOld = SelectObject(backDc, backBmp);
                // the same memory as a Bitmap, for whoever draws it with GDI+ (and as the sign that there is a picture)
                backdrop = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, backBits);
                dibW = w; dibH = h;
                return true;
            }
            catch { DropDc(); return false; }
        }

        void DropDc()
        {
            if (backdrop != null) { backdrop.Dispose(); backdrop = null; } // it looks into the bitmap's memory: first
            if (backDc != IntPtr.Zero) { if (backOld != IntPtr.Zero) SelectObject(backDc, backOld); DeleteDC(backDc); backDc = IntPtr.Zero; backOld = IntPtr.Zero; }
            if (backBmp != IntPtr.Zero) { DeleteObject(backBmp); backBmp = IntPtr.Zero; }
            backBits = IntPtr.Zero; dibW = dibH = 0;
        }

        protected override void Dispose(bool disposing) { Fine(false); DropTaps(); if (shown != null) { shown.Dispose(); shown = null; } DropDc(); base.Dispose(disposing); }

        // A block's corner. 17.11: was 12, the corner of a card in the full window, then 20; now half the height of
        // the smallest block, so the small ones (one line of text) are round all the way and the taller ones have
        // the same corner
        const int RowInset = 8;
        float CardRad
        {
            get
            {
                int least = int.MaxValue;
                foreach (Control c in page.Controls) if (c.Visible && c is Row) least = Math.Min(least, c.Height);
                return least == int.MaxValue ? Theme.S(24) : least / 2f;
            }
        }

        // For checking the glass alone, without the running program: the blocks of the panel (their sizes as on the
        // screen at 150 %) drawn over a picture, saved as a picture (RedTune.exe --glass-png out.png backdrop.png)
        internal static void GlassPng(string outPath, string backPath)
        {
            using (var loaded = new Bitmap(backPath))
            using (var raw = new Bitmap(loaded.Width, loaded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(raw)) g.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
                float bright;
                using (var blur = Frosted(raw, out bright))
                {
                    int side = Theme.S(14), gap = Theme.S(8), y = side;
                    var blocks = new System.Collections.Generic.List<Rectangle>();
                    foreach (int h in new[] { 84, 60, 48, 48, 60, 60 }) { blocks.Add(new Rectangle(side, y, raw.Width - 2 * side, Theme.S(h))); y += Theme.S(h) + gap; }
                    var panes = new GlassPanes(raw.Width, raw.Height, blocks, Theme.S(48) / 2f);
                    var px = new int[raw.Width * raw.Height];
                    var pin = GCHandle.Alloc(px, GCHandleType.Pinned);
                    try
                    {
                        var all = new Rectangle(0, 0, raw.Width, raw.Height);
                        var rd = raw.LockBits(all, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        try { for (int row = 0; row < raw.Height; row++) Marshal.Copy(rd.Scan0 + row * rd.Stride, px, row * raw.Width, raw.Width); } finally { raw.UnlockBits(rd); }
                        panes.Compose(raw, blur, 0, 0, DarkRenderer.TintOf(bright), Theme.Card, pin.AddrOfPinnedObject());
                        using (var res = new Bitmap(raw.Width, raw.Height, raw.Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject())) res.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    finally { pin.Free(); }
                }
            }
        }

        // the window cut to its blocks and the shadow round each
        void Cut()
        {
            int e = GlassPanes.Margin; // as far out as a block's shadow goes
            var cut = new Region(); cut.MakeEmpty();
            foreach (Control c in page.Controls)
            {
                if (!c.Visible || !(c is Row)) continue;
                var at = PointToClient(c.PointToScreen(Point.Empty));
                using (var path = Theme.Round(new RectangleF(at.X - e, at.Y - e, c.Width + 2 * e, c.Height + 2 * e), CardRad + e)) cut.Union(path);
            }
            var old = Region; Region = cut; if (old != null) old.Dispose();
        }

        void LiveTick()
        {
            if (!Visible || !live || Opacity < 1) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if ((t0 - lastFrameAt) * tick + 3 < waitMs) return; // not yet (3 ms: the timer is not exact)
            lastFrameAt = t0;
            Bitmap now; bool owned = true;
            var phys = Frost.Real(Bounds);
            if (phys == Bounds)
            {
                // the main monitor: the program's pixels are the screen's
                try
                {
                    if (tapNext == null || tapNext.W != Width || tapNext.H != Height) { DropTaps(); tapNow = Tap.Make(Width, Height); tapNext = Tap.Make(Width, Height); }
                    if (tapNow == null || tapNext == null) { DropTaps(); return; }
                    IntPtr screen = GetDC(IntPtr.Zero);
                    try { if (!BitBlt(tapNext.Dc, 0, 0, Width, Height, screen, Left, Top, 0x00CC0020)) return; }
                    finally { ReleaseDC(IntPtr.Zero, screen); }
                }
                catch { return; }
                frames++;
                if (shotRaw == tapNow.Pic && shotAt == Location && memcmp(tapNext.Bits, tapNow.Bits, (UIntPtr)(uint)(Width * Height * 4)) == 0)
                {
                    Again();
                    sameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (++still > 8) waitMs = still > 45 ? 50 : 33;
                    return;
                }
                var swap = tapNow; tapNow = tapNext; tapNext = swap;
                now = tapNow.Pic; owned = false;
            }
            else
            {
                // a monitor of another scaling: the screen is read in its real pixels and compared so; only a changed
                // frame is brought to the panel's own size (17.11: read into memory of the panel's own here too)
                try
                {
                    if (realNext == null || realNext.W != phys.Width || realNext.H != phys.Height)
                    {
                        if (realNow != null) realNow.Dispose(); if (realNext != null) realNext.Dispose();
                        realNow = Tap.Make(phys.Width, phys.Height); realNext = Tap.Make(phys.Width, phys.Height); realSeen = false;
                    }
                    if (realNow == null || realNext == null) return;
                    if (!Frost.BlitReal(realNext.Dc, phys)) return;
                }
                catch { return; }
                frames++;
                if (realSeen && shotAt == Location && memcmp(realNext.Bits, realNow.Bits, (UIntPtr)(uint)(phys.Width * phys.Height * 4)) == 0)
                {
                    Again();
                    sameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (++still > 8) waitMs = still > 45 ? 50 : 33;
                    return;
                }
                var turn = realNow; realNow = realNext; realNext = turn; realSeen = true;
                now = StretchFrom(realNow, Width, Height);
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Take(now, Location, true, owned);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            Picture();
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool layered = false;
            try { if (inkDirty || inkK == null) Ink(); layered = Frame(); if (layered) Present(); }
            catch (Exception ex) { Store.Log("мини-панель: слой надписей: " + ex.Message); }
            if (!layered) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x80 | 0x100); // the old way: invalidate, all children, now
            if (wantFrame && layered)
            {
                wantFrame = false;
                try { shown.Pic.Save(System.IO.Path.Combine(Store.DataDir, "mini-frame.png"), System.Drawing.Imaging.ImageFormat.Png); } catch { }
            }
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            redraws++; redrawTicks += t4 - t0; tGrab += t1 - t0; tBlur += t2 - t1; tPic += t3 - t2; tPaint += t4 - t3;
            // the screen is changing: the next frame as soon as this one's cost allows
            still = 0; waitMs = Math.Max(15, 1.6 * (t4 - t0) * tick);
        }

        static bool Same(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return false;
            var r = new Rectangle(Point.Empty, a.Size);
            var da = a.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var db = b.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { return memcmp(da.Scan0, db.Scan0, (UIntPtr)(uint)(Math.Abs(da.Stride) * a.Height)) == 0; }
                finally { b.UnlockBits(db); }
            }
            finally { a.UnlockBits(da); }
        }

        void LiveStats()
        {
            if (frames == 0) return;
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency, secs = (DateTime.Now - liveFrom).TotalSeconds;
            Store.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "мини-панель: живое стекло {0:0.0} с: кадров {1}, из них с перерисовкой {2}; без изменений {3:0.0} мс на кадр, с перерисовкой {4:0.0} мс",
                secs, frames, redraws, frames > redraws ? sameTicks * ms / (frames - redraws) : 0, redraws > 0 ? redrawTicks * ms / redraws : 0)
                + (redraws > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, " (чтение {0:0.0}, размытие {1:0.0}, картинка {2:0.0}, отрисовка {3:0.0})", tGrab * ms / redraws, tBlur * ms / redraws, tPic * ms / redraws, tPaint * ms / redraws) : ""));
            frames = redraws = 0; sameTicks = redrawTicks = 0; tGrab = tBlur = tPic = tPaint = 0;
        }

        Rectangle anchorArea; bool anchorBottom;

        // from the tray icon: opens by the mouse (the icon is under it), on the side of the screen where the taskbar is
        // true: it opened (the caller then puts the full window away: one of the two at a time)
        public bool Toggle()
        {
            if (Visible) { Hide(); return false; }
            if ((DateTime.Now - hidden).TotalMilliseconds < 350) return false; // the click on the icon that has just closed it
            cleanRow.SetSub(CleanSub);
            var p = Cursor.Position;
            var area = Screen.FromPoint(p).WorkingArea;
            // shown first, unseen: the rows of a window that is not shown count as hidden and are not placed, so the
            // height could not be found before (the first build opened 32 px tall)
            anchorBottom = false; anchorArea = area;
            DropTaps(); DropShot(); framed = false; inkDirty = true;
            if (lastReal != null) { lastReal.Dispose(); lastReal = null; }
            // live glass with the notifications' switch; where Windows cannot leave the window out of capture, a still one
            live = s.OsdLive;
            try { live = SetWindowDisplayAffinity(Handle, live ? 0x11u : 0u) && live; } catch { live = false; }
            anim.Stop(); slide = 0;
            Opacity = 0; Location = home = new Point(area.Left, area.Top); Height = Theme.S(900);
            Show();
            Fill();
            // 17.11: the blocks stand as far from the taskbar (and from the side of the screen) as from each other. The
            // window is larger than its blocks, so it is placed by them, not by its own edge
            int gap = Theme.S(8);
            bool top = p.Y < area.Top + area.Height / 2;
            var b = Blocks();
            int x = Math.Max(area.Left + gap - b.Left, Math.Min(area.Right - gap - b.Right, p.X - Width / 2));
            home = new Point(x, top ? area.Top + gap - b.Top : area.Bottom - gap - b.Bottom);
            anchorBottom = !top;
            Put();
            Snap(); Glass(); // the screen behind, read while the panel is still unseen
            Update();
            // it comes from the taskbar; the live glass starts when it has arrived
            slideFrom = slide = (top ? -1 : 1) * Theme.S(14); Put();
            Activate();
            animClock.Restart(); anim.Start();
            return true;
        }
    }
}
