using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RedTune
{
    // 17.11: the mini panel as separate blocks, each a window of its own with Windows' own blur behind it.
    //
    // The panel before this (MiniPanel, still in the source) made its glass itself: it read the screen under it,
    // frosted and bent that picture and drew it under the blocks. However fast that was made, the glass came one
    // or two frames after what moved behind it, and that could be seen. Windows blurs what is behind a window
    // inside its own compositor, in step with the screen and at no cost to this program, but only as a whole
    // rectangle: it does not follow a window cut to a shape (tried 17.11: the blur fills the window's rectangle
    // whatever its Region is), only the standard rounded corners of Windows 11. So, as chosen:
    //   · every block is a small window with that blur and those corners; between the blocks is the screen itself;
    //   · no bending of the picture, no frost of our own, no round ends: the frost is Windows' and cannot be tuned.
    //
    // A window with this blur shows it wherever its own picture is not solid, so the picture must carry how solid
    // each pixel is, which ordinary drawing does not write. A block therefore draws nothing straight to the screen:
    //   · the glass is a layer of the card colour, part solid (Tint), with the card's sheen;
    //   · what the row and its controls draw is taken as a layer too: they are drawn into memory twice, over black
    //     and over white (IBackdrop.Solid); where the two agree it is solid, where they differ the difference is
    //     how much shows through;
    //   · the two layers make one picture with its transparency, and every paint message of the block's controls
    //     is answered by copying their piece of it (Backdrops.Whole). The layers are made again when a control
    //     asks to be redrawn (Invalidated).
    sealed class MiniBlock : Form, IBackdrop
    {
        [StructLayout(LayoutKind.Sequential)] struct ACCENT { public int State, Flags, Color, Anim; }
        [StructLayout(LayoutKind.Sequential)] struct WCA { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WCA d);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public readonly Row Row;
        public bool Wanted = true;  // part of the panel now (a row this computer or this mode has nothing for is not)
        public bool Glass;          // Windows' blur is on behind this window; false: a plain card
        public Action<IntPtr> Lost; // the window lost the keyboard, to this window (zero: to another program)
        public Action Escape;       // Esc was pressed

        int tint = 150;             // how solid the card colour is over the blur, of 255
        public int Tint { get { return tint; } set { if (tint != value) { tint = value; glass = null; dirty = true; if (IsHandleCreated) Invalidate(true); } } }

        // the picture of the block, with its transparency: 32 bits a pixel, colour already multiplied by how solid it is
        IntPtr dc, bmp, old, bits; int fw, fh; Bitmap pic;
        uint[] glass;
        bool dirty = true;
        Color? solid;

        public Bitmap Backdrop { get { return pic; } }
        public IntPtr BackdropDc { get { return dc; } }
        public Color? Solid { get { return solid; } }
        public bool Whole { get { return true; } }

        public MiniBlock(Row row)
        {
            Row = row;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; Font = Theme.Body; BackColor = Theme.Card;
            KeyPreview = true; Text = "Red Tune — мини-панель";
            ClientSize = new Size(Theme.S(412), row.Height);
            row.Location = Point.Empty;
            Controls.Add(row);
            Hook(row);
        }

        void Hook(Control c)
        {
            c.Invalidated += (o, e) => dirty = true;
            c.SizeChanged += (o, e) => dirty = true;
            foreach (Control k in c.Controls) Hook(k);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // a tool window (not in Alt+Tab, not on the taskbar) with the soft shadow of a menu
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x00020000; return cp; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                // the rounded corners of Windows 11 (the blur is cut by them) and a quiet line round the block
                int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
                var line = Theme.BorderStrong; int border = line.R | line.G << 8 | line.B << 16; DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
            accentSet = false; SetGlass(wantGlass);
        }

        // Windows' blur behind this window, on or off. Off: a plain solid card (the block's picture is then made solid
        // too). 19.11: asked for per opening, by the display the panel opens on (ColorMode.Advanced): on a display in
        // Windows' advanced colour mode the blur comes out wrong on the screen itself.
        bool wantGlass = true, accentSet;
        public void SetGlass(bool on)
        {
            if (accentSet && on == wantGlass) return;
            wantGlass = on;
            if (!IsHandleCreated) return;
            bool was = Glass;
            try
            {
                // 3: blur behind the window (the colour is not used by this kind, but must not be all zeros); 0: none
                var a = new ACCENT { State = on ? 3 : 0, Flags = 0, Color = unchecked((int)0x01000000) };
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENT)));
                try
                {
                    Marshal.StructureToPtr(a, p, false);
                    var d = new WCA { Attr = 19, Data = p, Size = Marshal.SizeOf(typeof(ACCENT)) };
                    bool ok = SetWindowCompositionAttribute(Handle, ref d) != 0;
                    Glass = on && ok;
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch { Glass = false; }
            accentSet = true;
            if (Glass != was) { glass = null; dirty = true; Invalidate(true); }
        }

        // the row as wide as the block, the block as tall as the row comes out (a row whose controls go under its
        // title is taller). Only while the block is shown: the controls of a hidden window count as hidden
        public void Fit(int w)
        {
            if (ClientSize.Width != w) ClientSize = new Size(w, ClientSize.Height);
            Row.Bounds = new Rectangle(0, 0, w, Row.Height);
            Row.PerformLayout();
            if (ClientSize.Height != Row.Height) ClientSize = new Size(w, Row.Height);
        }

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == System.Windows.Forms.Keys.Escape && Escape != null) Escape(); base.OnKeyDown(e); }

        protected override void OnPaintBackground(PaintEventArgs e) { if (solid.HasValue) e.Graphics.Clear(solid.Value); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014 && !solid.HasValue) { m.Result = (IntPtr)1; return; } // nothing is erased: the picture is the whole window
            if (Backdrops.Whole(this, ref m)) return;
            base.WndProc(ref m);
            // WM_ACTIVATE, going inactive: who takes the keyboard comes with the message
            if (m.Msg == 0x0006 && ((long)m.WParam & 0xFFFF) == 0 && Lost != null) Lost(m.LParam);
        }

        public void Fresh()
        {
            if (!dirty && dc != IntPtr.Zero && fw == ClientSize.Width && fh == ClientSize.Height) return;
            try { Build(); } catch (Exception ex) { Store.Log("мини-панель: блок не нарисовался: " + ex.Message); }
        }

        bool Frame(int w, int h)
        {
            if (dc != IntPtr.Zero && fw == w && fh == h) return true;
            Drop();
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            bmp = CreateDIBSection(IntPtr.Zero, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) { Drop(); return false; }
            dc = CreateCompatibleDC(IntPtr.Zero); old = SelectObject(dc, bmp);
            pic = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            fw = w; fh = h; glass = null;
            return true;
        }

        void Drop()
        {
            if (pic != null) { pic.Dispose(); pic = null; }
            if (dc != IntPtr.Zero) { if (old != IntPtr.Zero) SelectObject(dc, old); DeleteDC(dc); dc = IntPtr.Zero; old = IntPtr.Zero; }
            if (bmp != IntPtr.Zero) { DeleteObject(bmp); bmp = IntPtr.Zero; }
            bits = IntPtr.Zero; fw = fh = 0;
        }

        protected override void Dispose(bool disposing) { Drop(); base.Dispose(disposing); }

        static uint[] Pixels(Bitmap b, PixelFormat how)
        {
            int w = b.Width, h = b.Height;
            var px = new int[w * h];
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, how);
            try { for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w, w); }
            finally { b.UnlockBits(d); }
            var res = new uint[px.Length];
            Buffer.BlockCopy(px, 0, res, 0, px.Length * 4);
            return res;
        }

        // The light on a block. 17.11: it was the sheen of a card (Theme.GlassSheen: a glow inside the top edge, a
        // weaker one inside the bottom edge, both white). Asked for: less of it at the top, and at the bottom the
        // opposite, a shade. So: a faint glow and a thin line of light under the top edge, the bottom edge going dark.
        const int TopGlow = 22, TopLine = 18, BottomShade = 84;
        static void Sheen(Graphics g, int w, int h)
        {
            using (var b = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillRectangle(b, 0, 0, w, h); // a faint frost
            Glow(g, new RectangleF(w * 0.03f, -Theme.S(12), w * 0.94f, Theme.S(24)), Color.FromArgb(TopGlow, 255, 255, 255));
            Glow(g, new RectangleF(w * 0.04f, h - Theme.S(11), w * 0.92f, Theme.S(22)), Color.FromArgb(BottomShade, 0, 0, 0));
            float x0 = w * 0.06f, x1 = w * 0.94f;
            if (x1 - x0 > 8)
                using (var line = new LinearGradientBrush(new RectangleF(x0 - 1, 0, x1 - x0 + 2, 2), Color.White, Color.White, 0f))
                {
                    var c = Color.FromArgb(TopLine, 255, 255, 255); var none = Color.FromArgb(0, 255, 255, 255);
                    line.InterpolationColors = new ColorBlend { Colors = new[] { none, c, c, none }, Positions = new[] { 0f, 0.25f, 0.75f, 1f } };
                    using (var pen = new Pen(line)) g.DrawLine(pen, x0, 1.5f, x1, 1.5f);
                }
        }

        // light (or shade) that is strongest in the middle of an ellipse and gone at its rim
        static void Glow(Graphics g, RectangleF e, Color c)
        {
            if (e.Width < 4 || e.Height < 2) return;
            using (var ep = new GraphicsPath())
            {
                ep.AddEllipse(e);
                using (var b = new PathGradientBrush(ep) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } }) g.FillPath(b, ep);
            }
        }

        unsafe void Build()
        {
            int w = ClientSize.Width, h = ClientSize.Height, n = w * h;
            if (w <= 0 || h <= 0 || !Frame(w, h)) return;
            // the glass: the card colour, part solid, and the card's sheen over it. Without the blur: a solid card
            if (glass == null || glass.Length != n)
                using (var g0 = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(g0))
                    {
                        g.Clear(Color.FromArgb(Glass ? Math.Max(0, Math.Min(255, tint)) : 255, Theme.Card));
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        Sheen(g, w, h);
                    }
                    glass = Pixels(g0, PixelFormat.Format32bppPArgb);
                }
            // what the row and its controls draw, over black and over white
            uint[] k, v;
            var all = new Rectangle(0, 0, w, h);
            using (var black = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (var white = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                try { solid = Color.Black; DrawToBitmap(black, all); solid = Color.White; DrawToBitmap(white, all); }
                finally { solid = null; }
                k = Pixels(black, PixelFormat.Format32bppArgb); v = Pixels(white, PixelFormat.Format32bppArgb);
            }
            uint* o = (uint*)bits;
            fixed (uint* pg = glass, pk = k, pv = v)
                for (int i = 0; i < n; i++)
                {
                    uint gl = pg[i], kb = pk[i] & 0xFFFFFF, wb = pv[i] & 0xFFFFFF;
                    if (kb == 0 && wb == 0xFFFFFF) { o[i] = gl; continue; }   // nothing drawn here: the glass
                    if (kb == wb) { o[i] = kb | 0xFF000000u; continue; }      // solid
                    // d: how much shows through, per colour (0..256)
                    int d0 = (int)(wb & 255) - (int)(kb & 255), d1 = (int)((wb >> 8) & 255) - (int)((kb >> 8) & 255), d2 = (int)((wb >> 16) & 255) - (int)((kb >> 16) & 255);
                    if (d0 < 0) d0 = 0; if (d1 < 0) d1 = 0; if (d2 < 0) d2 = 0;
                    d0 += d0 >> 7; d1 += d1 >> 7; d2 += d2 >> 7;
                    uint b = (kb & 255) + (uint)(((gl & 255) * d0) >> 8), g = ((kb >> 8) & 255) + (uint)((((gl >> 8) & 255) * d1) >> 8), r = ((kb >> 16) & 255) + (uint)((((gl >> 16) & 255) * d2) >> 8);
                    uint through = (uint)(((255 - (gl >> 24)) * ((d0 + d1 + d2) / 3)) >> 8), a = 255 - Math.Min(255u, through);
                    if (b > 255) b = 255; if (g > 255) g = 255; if (r > 255) r = 255;
                    if (a < b) a = b; if (a < g) a = g; if (a < r) a = r;     // a colour is never more than how solid the pixel is
                    o[i] = b | g << 8 | r << 16 | a << 24;
                }
            dirty = false;
        }
    }

    // The panel: what is switched many times a day (the rows of MiniPanel, see there for what each does), as blocks.
    // A left click on the tray icon opens it by the icon; a click anywhere else closes it.
    class MiniBlocks
    {
        readonly Settings s;
        readonly PowerService power;
        readonly CurveService curve;
        readonly Action<int> setMode;
        readonly Action<string> applyCurve, applyGpu;
        readonly Func<string> gpuBusy, curveBusy; // the preset being written to the card / the processor right now, or null

        readonly Segmented mode = new Segmented("Тихий", "Баланс", "Макс", "Свой");
        readonly DropButton powerDrop = new DropButton(), curveDrop = new DropButton(), gpuDrop = new DropButton(), soundDrop = new DropButton();
        List<SoundDevice> sounds = new List<SoundDevice>(); // the output devices as the list of the block shows them
        readonly Toggle overlay = new Toggle();
        readonly FlatButton cleanBtn = new FlatButton("Очистить"), screensBtn = new FlatButton("Выключить");
        readonly MiniBlock modeB, powerB, curveB, gpuB, soundB, overlayB, cleanB, screensB;
        readonly List<MiniBlock> blocks = new List<MiniBlock>();
        readonly Timer refresh = new Timer { Interval = 900 }, watch = new Timer { Interval = 160 }, anim = new Timer { Interval = 10 };
        const string CleanSub = "Закрыть лишние программы";

        // 17.11: the blocks, for Settings → «Мини-панель», where each can be switched off: its key in Settings.MiniOff,
        // its title and line as on the block, and whether this computer has anything for it
        public class Kind { public string Key, Name, Sub; public Func<bool> Fits; }
        public static readonly Kind[] Kinds =
        {
            new Kind { Key = "mode", Name = "Режим питания", Fits = () => Hw.LenovoPower },
            new Kind { Key = "power", Name = "Пресет питания", Sub = "Включает режим «Свой»", Fits = () => Hw.LenovoPower },
            new Kind { Key = "curve", Name = "Кривая", Sub = "Пресет кривой", Fits = () => Hw.Curve },
            new Kind { Key = "gpu", Name = "Видеокарта", Sub = "Пресет видеокарты", Fits = () => Hw.Nvidia },
            new Kind { Key = "sound", Name = "Устройство вывода", Sub = "Куда играют все программы", Fits = () => true },
            new Kind { Key = "overlay", Name = "Оверлей", Sub = "FPS, температура и частота поверх игр", Fits = () => true },
            new Kind { Key = "clean", Name = "Очистка памяти", Sub = CleanSub, Fits = () => true },
            new Kind { Key = "screens", Name = "Выключить дисплеи", Sub = "Разбудит мышь или клавиша", Fits = () => true },
        };
        public static bool Shown(Settings s, string key) { return !(s.MiniOff ?? "").Split(',').Contains(key); }
        public static void SetShown(Settings s, string key, bool on)
        {
            var off = (s.MiniOff ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(k => k != key).ToList();
            if (!on) off.Add(key);
            s.MiniOff = string.Join(",", off);
            Store.SaveSettings(s);
        }
        bool On(string key) { return Shown(s, key); }
        static readonly int[] Modes = { Lenovo.Quiet, Lenovo.Balance, Lenovo.Performance, Lenovo.Custom };
        static readonly string[] Dots = { "•··", "·•·", "··•", "·•·" };
        const int AnimMs = 200;

        bool up, filling, wasBusy;
        int frame, tint = 150, slide, slideFrom;
        double shade = 1;
        DateTime hidden = DateTime.MinValue, expectUntil = DateTime.MinValue;
        Rectangle anchorArea; bool anchorBottom; Point home;
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();

        public bool Visible { get { return up; } }
        static int BlockW { get { return Theme.S(412); } }
        static int Gap { get { return Theme.S(8); } }

        MiniBlock Block(Row row)
        {
            var b = new MiniBlock(row);
            // 17.11: to another block of the panel is not leaving the panel. (The first build asked Windows which window
            // is in front; opened by RedTune.exe --mini, the panel is not in front at all, the blocks handed the
            // keyboard to each other while coming up, and the panel closed itself before it was seen.)
            b.Lost = to => { try { if (up && !blocks.Any(x => x.IsHandleCreated && x.Handle == to)) b.BeginInvoke((Action)Away); } catch { } };
            b.Escape = Hide;
            blocks.Add(b);
            var h = b.Handle; // made now: the panel's own calls (BeginInvoke) need a window, shown or not
            return b;
        }

        public MiniBlocks(Settings s, PowerService power, CurveService curve, Action<int> setMode, Action<string> applyCurve, Action<string> applyGpu, Func<string> gpuBusy, Func<string> curveBusy)
        {
            this.s = s; this.power = power; this.curve = curve; this.setMode = setMode; this.applyCurve = applyCurve; this.applyGpu = applyGpu;
            this.gpuBusy = gpuBusy; this.curveBusy = curveBusy;

            modeB = Block(new Row("Режим питания", null, mode));
            powerB = Block(new Row("Пресет питания", "Включает режим «Свой»", powerDrop));
            curveB = Block(new Row("Кривая", null, curveDrop));
            gpuB = Block(new Row("Видеокарта", null, gpuDrop));
            // 19.11 (the user's wish): where the sound goes — the device every program plays to, as Windows' own list by
            // the volume does it. The line under the title names the device's adapter (the headphones' model).
            soundB = Block(new Row("Устройство вывода", " ", soundDrop));
            overlayB = Block(new Row("Оверлей", "FPS, температура и частота поверх игр", overlay));
            cleanB = Block(new Row("Очистка памяти", CleanSub, cleanBtn));
            // 17.11: the screens go dark, the computer keeps running (the button of «Устройство» → «Экран»), on any computer
            screensB = Block(new Row("Выключить дисплеи", "Разбудит мышь или клавиша", screensBtn));

            mode.Changed += (o, e) =>
            {
                if (filling || mode.Selected < 0) return;
                setMode(Modes[mode.Selected]); Expect(900);
                // the presets belong to «Свой»; with one of the firmware's own modes picked their block is put away at once
                powerB.Wanted = Modes[mode.Selected] == Lenovo.Custom && On("power");
                Arrange();
                refresh.Stop(); refresh.Start();
            };
            powerDrop.Changed += (o, e) =>
            {
                if (filling || powerDrop.Index < 0 || powerDrop.Index >= power.Data.Presets.Count) return;
                Expect(900);
                power.Apply(power.Data.Presets[powerDrop.Index], ok => { if (up) Fill(); });
            };
            curveDrop.Changed += (o, e) => { if (!filling && curveDrop.Index >= 0 && curveDrop.Index < curveDrop.Items.Length) { applyCurve(curveDrop.Items[curveDrop.Index]); Expect(300); } };
            gpuDrop.Changed += (o, e) => { if (!filling && gpuDrop.Index >= 0 && gpuDrop.Index < gpuDrop.Items.Length) { applyGpu(gpuDrop.Items[gpuDrop.Index]); Expect(300); } };
            soundDrop.Changed += (o, e) =>
            {
                if (filling || soundDrop.Index < 0 || soundDrop.Index >= sounds.Count) return;
                var why = AudioFx.SetDefault(sounds[soundDrop.Index].Id);
                if (why != null) Store.Log("мини-панель: устройство вывода: " + why);
                Fill();
            };
            overlay.CheckedChanged += (o, e) => { if (filling) return; s.OverlayOn = overlay.Checked; Store.SaveSettings(s); Overlay.Apply(); };
            cleanBtn.Click += (o, e) => CleanPage.RunClean(cleanB, t => cleanB.Row.SetSub(t ?? CleanSub), busy => cleanBtn.Enabled = !busy);
            // the panel is put away first, and the screens go dark a moment later: by then the hand is off the mouse
            // (any move of it wakes them at once), and the panel is not there when they come back
            screensBtn.Click += (o, e) =>
            {
                hidden = DateTime.Now; Hide();
                var later = new Timer { Interval = 700 };
                later.Tick += (a, b) => { later.Stop(); later.Dispose(); try { Display.MonitorsOff(); } catch (Exception ex) { Store.Log("мини-панель: экраны: " + ex.Message); } };
                later.Start();
            };
            // the mode and the active presets a moment after a switch (the firmware and the processor take a moment)
            refresh.Tick += (o, e) => { refresh.Stop(); if (up) Fill(); };
            power.Changed += () => { try { if (up) modeB.BeginInvoke((Action)Fill); } catch { } };
            watch.Tick += (o, e) => Watch();
            anim.Tick += (o, e) => AnimTick();
        }

        // a write on its way (a mode brings its own graphics card preset and curve, and writing them takes a second or
        // two): the list shows the preset that is coming with a running dot and takes no clicks; see MiniPanel
        void Expect(int ms) { expectUntil = DateTime.Now.AddMilliseconds(ms); Watch(); }

        void Watch()
        {
            string g = null, c = null;
            try { g = gpuBusy(); c = curveBusy(); } catch { }
            bool expect = DateTime.Now < expectUntil, busy = g != null || c != null || expect;
            frame++;
            Loading(gpuB, gpuDrop, g, expect);
            Loading(curveB, curveDrop, c, expect);
            if (busy) wasBusy = true;
            else if (wasBusy) { wasBusy = false; Fill(); } // the writes are over: what is on now
        }

        void Loading(MiniBlock b, DropButton drop, string name, bool expect)
        {
            if (!b.Wanted) return;
            if (name != null) { drop.Enabled = false; drop.Text = name + "  " + Dots[frame / 2 % Dots.Length]; }
            else if (expect) drop.Enabled = false;
        }

        // what is on now, read afresh every time the panel opens
        void Fill()
        {
            filling = true;
            try
            {
                bool lenovo = Hw.LenovoPower;
                modeB.Wanted = lenovo && On("mode");
                powerB.Wanted = lenovo && power.Mode == Lenovo.Custom && On("power"); // the presets of «Свой» only in «Свой»
                if (lenovo)
                {
                    mode.Selected = Array.IndexOf(Modes, power.Mode);
                    var names = power.Data.Presets.Select(p => p.Name).ToArray();
                    powerDrop.Set(names.Length > 0 ? names : new[] { "—" }, power.Active != null ? Array.IndexOf(names, power.Active.Name) : -1);
                    powerDrop.Enabled = names.Length > 0;
                }
                var curves = curve.Presets.Select(p => p.Name).ToArray();
                curveB.Wanted = Hw.Curve && s.Expert && curves.Length > 0 && On("curve");
                if (curveB.Wanted) { curveDrop.Set(curves, Array.IndexOf(curves, curve.Active.Name)); curveDrop.Enabled = true; }
                string[] gpus = new string[0];
                if (Hw.Nvidia && s.Expert && On("gpu")) { try { GpuStore.Load(); gpus = GpuStore.Presets.Select(p => p.Name).ToArray(); } catch { } }
                gpuB.Wanted = gpus.Length > 0;
                if (gpuB.Wanted) { gpuDrop.Set(gpus, Array.IndexOf(gpus, GpuStore.Active)); gpuDrop.Enabled = true; }
                // the output devices: a block only when there is a choice (two or more plugged in and switched on).
                // The list says a device by its short name («Наушники»); two with the same short name get the full one
                sounds = On("sound") ? AudioFx.Devices().OrderBy(d => d.Full, StringComparer.CurrentCultureIgnoreCase).ToList() : new List<SoundDevice>();
                soundB.Wanted = sounds.Count > 1;
                if (soundB.Wanted)
                {
                    var names = sounds.Select(d => sounds.Count(x => x.Name == d.Name) > 1 ? d.Full : d.Name).ToArray();
                    int now = sounds.FindIndex(d => d.Default);
                    soundDrop.Set(names, now);
                    var sub = now >= 0 && !string.IsNullOrEmpty(sounds[now].Adapter) ? sounds[now].Adapter : " ";
                    if (soundB.Row.Sub != sub) soundB.Row.SetSub(sub);
                }
                overlay.Set(s.OverlayOn);
                overlayB.Wanted = On("overlay"); cleanB.Wanted = On("clean"); screensB.Wanted = On("screens");
            }
            catch (Exception ex) { Store.Log("мини-панель: " + ex.Message); }
            filling = false;
            // a write still going on (the panel reads its state while a mode's preset is on its way): the lists stay held
            try { bool ex = DateTime.Now < expectUntil; Loading(gpuB, gpuDrop, gpuBusy(), ex); Loading(curveB, curveDrop, curveBusy(), ex); } catch { }
            Arrange();
        }

        // the blocks that are part of the panel now, on the screen one under another, as far apart as from the taskbar
        void Arrange()
        {
            if (!up) return;
            int w = BlockW, gap = Gap;
            foreach (var b in blocks)
            {
                if (b.Wanted && !b.Visible)
                {
                    b.Tint = tint; b.Opacity = shade;
                    b.Location = new Point(home.X, home.Y);
                    b.Show();
                }
                else if (!b.Wanted && b.Visible)
                {
                    // the block that has the keyboard goes away: another takes it, or the panel would count as left
                    if (GetActiveWindow() == b.Handle) { var next = blocks.FirstOrDefault(x => x.Wanted && x != b); if (next != null) next.Activate(); }
                    b.Hide();
                }
            }
            // the buttons on the right of the blocks as wide as the widest of them (a page lines up its column the same way)
            var column = new FlatButton[] { powerDrop, curveDrop, gpuDrop, soundDrop, cleanBtn, screensBtn };
            int wide = 0;
            foreach (var c in column) wide = Math.Max(wide, c.NaturalWidth);
            foreach (var c in column) c.MinWidth = wide;
            foreach (var b in blocks) if (b.Wanted) b.Fit(w);
            // the four modes do not fit beside the row's title and go under it; there they run the width of the row
            if (modeB.Wanted)
            {
                int room = w - Theme.S(16) - Theme.S(14);
                mode.MinItemWidth = Math.Max(0, (room - 3 * Theme.S(4)) / 4);
                modeB.Fit(w);
            }
            Place();
        }

        void Place()
        {
            int gap = Gap, total = 0;
            foreach (var b in blocks) if (b.Wanted) total += b.Height + gap;
            // opened over a taskbar at the bottom, the panel keeps its bottom edge when a block comes or goes
            int y = (anchorBottom ? anchorArea.Bottom - total : home.Y) + slide;
            foreach (var b in blocks)
            {
                if (!b.Wanted) continue;
                var at = new Point(home.X, y);
                if (b.Location != at) b.Location = at;
                y += b.Height + gap;
            }
        }

        // how it comes up: from the side of the taskbar, a few pixels, while it turns from clear to solid
        void AnimTick()
        {
            if (!up) { anim.Stop(); return; }
            double t = animClock.Elapsed.TotalMilliseconds / AnimMs;
            if (t >= 1) { anim.Stop(); slide = 0; shade = 1; }
            else
            {
                double e = 1 - Math.Pow(1 - t, 3); // fast at first, settling at the end
                slide = (int)Math.Round(slideFrom * (1 - e));
                shade = Math.Min(0.99, t * 1.7);
            }
            foreach (var b in blocks) if (b.Wanted && b.Opacity != shade) b.Opacity = shade;
            Place();
        }

        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

        // a block lost the keyboard: to another block or to a question window of the panel's own it does not count
        void Away()
        {
            if (!up) return;
            IntPtr a = GetActiveWindow(); // of this program's own windows; zero: another program has the keyboard
            if (a != IntPtr.Zero && blocks.Any(b => b.IsHandleCreated && b.Handle == a)) return;
            if (blocks.Any(b => b.OwnedForms.Any(f => f.Visible))) return;
            hidden = DateTime.Now;
            Hide();
        }

        public void Hide()
        {
            if (!up) return;
            up = false;
            anim.Stop(); watch.Stop(); refresh.Stop();
            foreach (var b in blocks) if (b.Visible) b.Hide();
        }

        // how solid the card colour has to be for white print to stay readable: by the screen where the panel will
        // stand, read once, before the panel is there
        int Measure(Rectangle r)
        {
            try { using (var shot = Frost.Grab(r)) return DarkRenderer.TintFor(shot); }
            catch { return 150; }
        }

        // from the tray icon: opens by the mouse (the icon is under it), on the side of the screen where the taskbar is
        // true: it opened (the caller then puts the full window away: one of the two at a time)
        public bool Toggle()
        {
            if (up) { hidden = DateTime.Now; Hide(); return false; }
            if ((DateTime.Now - hidden).TotalMilliseconds < 350) return false; // the click on the icon that has just closed it
            cleanB.Row.SetSub(CleanSub);
            var p = Cursor.Position;
            var area = Screen.FromPoint(p).WorkingArea;
            int w = BlockW, gap = Gap, tall = Theme.S(520);
            bool top = p.Y < area.Top + area.Height / 2;
            int x = Math.Max(area.Left + gap, Math.Min(area.Right - gap - w, p.X - w / 2));
            anchorArea = area; anchorBottom = !top; home = new Point(x, area.Top + gap);
            tint = Measure(new Rectangle(x, top ? area.Top : area.Bottom - tall, w, tall));
            // 19.11: a display in Windows' advanced colour mode (HDR, automatic colour management) shows the blur behind
            // a window with its colours turned inside out (on the screen only: screenshots are right). There the blocks
            // are plain cards. Asked for at every opening: the mode is per display and can be switched at any time.
            bool plain = false;
            try { plain = ColorMode.Advanced(Screen.FromPoint(p).DeviceName); } catch { }
            foreach (var b in blocks) b.SetGlass(!plain);
            up = true; shade = 0; slide = 0;
            Fill(); // shows the blocks, unseen yet, and stands them in their places
            if (!blocks.Any(b => b.Wanted)) { up = false; return false; } // every block is switched off in Settings: there is no panel
            slideFrom = slide = (top ? -1 : 1) * Theme.S(14);
            Place();
            var first = blocks.FirstOrDefault(b => b.Wanted);
            if (first != null) first.Activate();
            animClock.Restart(); anim.Start(); watch.Start();
            return true;
        }
    }
}
