using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // 20.11: the frames of a page's glide, as ToggleAnim's: a pool timer every 15 ms posts one frame at a time to the
    // window. A WinForms Timer comes last of everything Windows hands out (after every paint), so the glide ran at about
    // 30 frames a second, a frame every 31 ms (measured with --motion-frames). Start / Stop / Enabled / Tick as a Timer's.
    class FrameClock : IDisposable
    {
        readonly Control owner;
        readonly System.Threading.Timer timer;
        volatile bool running;
        int posted; // a frame is waiting in the queue: no second one
        public event EventHandler Tick;

        public FrameClock(Control owner)
        {
            this.owner = owner;
            timer = new System.Threading.Timer(_ => Post(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        void Post()
        {
            if (!running || System.Threading.Interlocked.Exchange(ref posted, 1) == 1) return;
            try { owner.BeginInvoke((Action)(() => { posted = 0; var h = Tick; if (running && h != null) h(this, EventArgs.Empty); })); }
            catch { posted = 0; } // the window is gone or not made yet
        }

        public bool Enabled { get { return running; } }
        public void Start() { if (running) return; running = true; timer.Change(0, 15); }
        public void Stop() { running = false; timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); }
        public void Dispose() { running = false; timer.Dispose(); }
    }

    // a scrolling page: controls stacked top to bottom at the page width
    class Page : Panel
    {
        readonly List<KeyValuePair<Control, int>> items = new List<KeyValuePair<Control, int>>();

        public Page()
        {
            motion = new FrameClock(this); // first: setting AutoScroll lays the page out at once, and that reads it
            AutoScroll = true; BackColor = Theme.Window; Dock = DockStyle.Fill;
            motion.Tick += (s, e) => MotionTick();
            HandleCreated += (s, e) => Native.DarkScrollBars(Handle);
            ResizeDone += AfterLiveResize;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { if (!Backdrops.Paint(e.Graphics, this)) base.OnPaintBackground(e); }

        public T Add<T>(T c, int gapBefore = 8) where T : Control
        {
            items.Add(new KeyValuePair<Control, int>(c, gapBefore));
            Controls.Add(c);
            c.SizeChanged += (s, e) => { if (!inLayout) Relayout(); else again = true; };
            return c;
        }

        // 20.11: a control put into the stack in front of another (the hints of «Распределение» above its groups)
        public T InsertBefore<T>(Control before, T c, int gapBefore = 8) where T : Control
        {
            int i = items.FindIndex(kv => kv.Key == before);
            if (i < 0) return Add(c, gapBefore);
            items.Insert(i, new KeyValuePair<Control, int>(c, gapBefore));
            Controls.Add(c);
            c.SizeChanged += (s, e) => { if (!inLayout) Relayout(); else again = true; };
            return c;
        }

        // for pages that rebuild a list (Автозагрузка): takes the control out of the stack and frees it
        public void Remove(Control c)
        {
            items.RemoveAll(kv => kv.Key == c);
            spots.Remove(c);
            Controls.Remove(c);
            c.Dispose();
        }

        // 23.11 («Удаление программ» was slow): many rows shown or hidden at once are placed once, after all of them —
        // each change used to place the whole page anew (74 programs: 100–140 placings, 0.3–0.5 s per letter typed in the search)
        public void Batch(Action change)
        {
            bool was = inLayout; inLayout = true; SuspendLayout();
            try { change(); } finally { ResumeLayout(false); inLayout = was; }
            Relayout();
        }

        // 23.11: these controls, already on the page, take the places they hold among themselves in this order (a list sorted
        // anew without making its rows again)
        public void Reorder(IList<Control> order)
        {
            var mine = new HashSet<Control>(order);
            var at = new List<int>();
            for (int i = 0; i < items.Count; i++) if (mine.Contains(items[i].Key)) at.Add(i);
            var gap = at.ToDictionary(i => items[i].Key, i => items[i].Value);
            for (int k = 0; k < at.Count && k < order.Count; k++) items[at[k]] = new KeyValuePair<Control, int>(order[k], gap[order[k]]);
            Relayout();
        }

        public Row Row(string title, string sub = null, params Control[] right) { return Add(new Row(title, sub, right)); }
        public SectionLabel Section(string text) { return Add(new SectionLabel(text), 10); }

        // ---------- 19.11 (the user's wish): movement where things come and go ----------
        // Every page stacks its rows here, so the movement is made here once, for all of them. When a row is shown or
        // hidden (or changes its height), the rows under it glide to their new places in about a fifth of a second,
        // quick at first and slowing at the end. A row that comes is uncovered from its top edge down as the rows under
        // it make room (its window is cut to the part uncovered). A row that goes is already hidden by the page's code,
        // so its last picture is kept and shrinks away as the rows under it close in. 20.11: the picture is a window of its
        // own under the rows (GhostView), made shorter each frame, the mirror of a row coming; the page used to draw it
        // itself, repainting the whole rectangle of it with its background on every frame, and on the window's glass a row
        // going came out in jerks while one coming was smooth.
        // No movement: the first placing, a page that is not on screen, another width (the window resized), more than
        // four rows changing at once (a tab switched, a list rebuilt: that would be a stampede, not a movement), the mini
        // panel (its window is as tall as its rows), Windows' own «show animations» switched off, and the checks and
        // off-screen renders (Motion is set false there).
        public static bool Motion = MotionAllowed();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
        static bool MotionAllowed() { try { bool on; return !SystemParametersInfo(0x1042, 0, out on, 0) || on; } catch { return true; } } // SPI_GETCLIENTAREAANIMATION
        public const double MotionMs = 220;
        // a row's place: Y where it stands now and To where it goes (down from the page's top, whatever the scrolling),
        // From where the glide began; Was: it was shown at the last placing; Showing: it is being uncovered
        class Spot { public float Y, From; public int To; public bool Was, Showing, Placed; }
        class Ghost { public Bitmap Pic; public int X, Y, W, H; public GhostView View; }
        // the picture of a row that went: it paints its top part as tall as it is; the rows under it are drawn over it
        class GhostView : Control
        {
            public Bitmap Pic;
            public GhostView() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Pic == null) return;
                var g = e.Graphics; var r = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, Math.Min(Width, Pic.Width), Math.Min(Height, Pic.Height)));
                if (r.Width <= 0 || r.Height <= 0) return;
                g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(Pic, r, r, GraphicsUnit.Pixel);
            }
        }
        readonly Dictionary<Control, Spot> spots = new Dictionary<Control, Spot>();
        readonly List<Ghost> ghosts = new List<Ghost>();
        readonly FrameClock motion;
        DateTime motionStart; int placedWidth = -1; bool placedOnce;
        // A page that hides its rows one by one (a tab switched: each change places the rows anew) is seen by the number
        // of changes within a moment, not within one placing: more than four in 60 ms, and nothing glides.
        DateTime burstAt; int burst;
        internal bool Moving { get { return motion.Enabled; } }
        internal int GhostCount { get { return ghosts.Count; } }
        internal Bitmap GhostPic(int i) { return i < ghosts.Count ? ghosts[i].Pic : null; } // for the check

        static float Ease(double t) { t = t < 0 ? 0 : t > 1 ? 1 : t; double u = 1 - t; return (float)(1 - u * u * u); }

        void MotionTick()
        {
            double t = (DateTime.Now - motionStart).TotalMilliseconds / MotionMs;
            if (t >= 1 || !Visible || !IsHandleCreated) { MotionEnd(); return; }
            float e = Ease(t);
            bool was = inLayout; inLayout = true; SuspendLayout();
            foreach (var kv in items)
            {
                Spot sp; if (!spots.TryGetValue(kv.Key, out sp) || !kv.Key.Visible) continue;
                if (sp.To != sp.From) { sp.Y = sp.From + (sp.To - sp.From) * e; int y = AutoScrollPosition.Y + (int)Math.Round(sp.Y); if (kv.Key.Top != y) kv.Key.Top = y; }
                if (sp.Showing) Uncover(kv.Key, (int)Math.Round(kv.Key.Height * e));
            }
            // inside the suspended layout too: a child that changes its size makes the page lay out, and with the pictures
            // shrinking outside it the page placed all its rows anew for every picture on every frame (every 2.5 ms on
            // «Питание»: the toggle and the rows got no frames, measured 20.11)
            foreach (var g in ghosts) { int tall = Math.Max(0, (int)Math.Round(g.H * (1 - e))); if (g.View.Height != tall) g.View.Height = tall; }
            ResumeLayout(false); inLayout = was;
        }

        static void Uncover(Control c, int tall)
        {
            var old = c.Region;
            c.Region = tall >= c.Height ? null : new Region(new Rectangle(0, 0, c.Width, Math.Max(0, tall)));
            if (old != null) old.Dispose();
        }

        // everything in its place at once
        void MotionEnd()
        {
            motion.Stop();
            bool was = inLayout; inLayout = true; SuspendLayout();
            foreach (var kv in items)
            {
                Spot sp; if (!spots.TryGetValue(kv.Key, out sp)) continue;
                sp.Y = sp.From = sp.To;
                if (sp.Showing) { sp.Showing = false; Uncover(kv.Key, int.MaxValue); }
                if (kv.Key.Visible && sp.Placed) { int y = AutoScrollPosition.Y + sp.To; if (kv.Key.Top != y) kv.Key.Top = y; }
            }
            ResumeLayout(false); inLayout = was;
            DropGhosts();
        }

        // the picture of a row as a window of its own under the rows, painted at once; null: it could not be taken
        Ghost TakeGhost(Control c, int y)
        {
            try
            {
                var pic = new Bitmap(c.Width, c.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb); // the quick kind to draw
                c.DrawToBitmap(pic, new Rectangle(0, 0, pic.Width, pic.Height));
                var gh = new Ghost { Pic = pic, X = Theme.S(Side), Y = y, W = pic.Width, H = pic.Height };
                gh.View = new GhostView { Pic = pic, BackColor = BackColor, Bounds = new Rectangle(gh.X, AutoScrollPosition.Y + gh.Y, gh.W, gh.H) };
                Controls.Add(gh.View); gh.View.SendToBack(); // under the rows, which close in over it
                gh.View.Update(); // painted now
                return gh;
            }
            catch { return null; }
        }

        // 20.11: a row about to be hidden (Painted.SetVisibleCore, before Windows hides it): its picture is put under it
        // now. Taken after the hiding, there were tens of milliseconds between Windows wiping the row's place with the
        // page's colour and the picture painted over it, and on the laptop a frame of that empty place reached the
        // screen: the help of «Автотюн» went, came back and then shrank. Not for a burst of rows (a tab switched).
        readonly Dictionary<Control, Ghost> early_ = new Dictionary<Control, Ghost>();
        DateTime earlyAt; int earlyBurst;
        internal void BeforeHide(Control c)
        {
            Spot sp;
            if (!Motion || NoScroll || !placedOnce || !IsHandleCreated || !Visible || early_.ContainsKey(c) || c.Width <= 0 || c.Height <= 0) return;
            if (!spots.TryGetValue(c, out sp) || !sp.Was || !sp.Placed || !items.Any(kv => kv.Key == c)) return;
            var now = DateTime.Now;
            if ((now - earlyAt).TotalMilliseconds > 60) { earlyAt = now; earlyBurst = 0; }
            if (++earlyBurst > 4) return;
            var gh = TakeGhost(c, (int)Math.Round(sp.Y));
            if (gh != null) early_[c] = gh;
        }

        void DropEarly(bool hiddenOnly = false)
        {
            foreach (var c in early_.Keys.Where(c => !hiddenOnly || !c.Visible).ToList()) { var g = early_[c]; Controls.Remove(g.View); g.View.Dispose(); g.Pic.Dispose(); early_.Remove(c); }
        }

        void DropGhosts()
        {
            foreach (var g in ghosts) { Controls.Remove(g.View); g.View.Dispose(); g.Pic.Dispose(); }
            ghosts.Clear();
        }

        internal static Action<double> PaintProbe; // --motion-frames: how long each paint took (null: not measured)
        internal static Action<string> PlaceProbe; // --motion-frames: what each placing decided
        protected override void OnPaint(PaintEventArgs e)
        {
            long pp0 = System.Diagnostics.Stopwatch.GetTimestamp();
            base.OnPaint(e);
            if (PaintProbe != null) PaintProbe((System.Diagnostics.Stopwatch.GetTimestamp() - pp0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ResizeDone -= AfterLiveResize; motion.Dispose(); DropGhosts(); DropEarly(); }
            base.Dispose(disposing);
        }

        bool inLayout;

        // 23.11 («Удаление программ» was slow while the window was dragged wider or narrower: 66 rows and their buttons got
        // the new width on every step, 40 ms a step before painting, though 8–10 of them were in sight): while the main
        // window is being resized by hand, the rows out of sight keep their width; they get it once, when the drag ends.
        // MainForm says when (ResizeBegin / ResizeEnd); a maximize or a snap is one step and places everything at once.
        public static bool LiveResize;
        static event Action ResizeDone;
        public static void EndLiveResize() { LiveResize = false; var h = ResizeDone; if (h != null) h(); }
        void AfterLiveResize() { if (Visible && IsHandleCreated) Relayout(); }
        // 16.11, for the mini panel: a page that is as tall as its rows and never scrolls, and its margins (the gap at
        // the sides and over the first row, in the units of Theme.S)
        public bool NoScroll;
        public int Side = 24, TopGap = 20;

        // pages whose items size themselves from the width (the program cards) set them here
        protected virtual void BeforeLayout(int width) { }

        // 13.11: the cards are fitted to the width BEFORE WinForms works out the scroll bars. After the window came back from
        // maximized, the still-wide cards made it add a horizontal bar and scroll the page sideways; the cards then got
        // narrower, but the bar and the sideways offset stayed. Pages scroll only up and down.
        protected override void OnLayout(LayoutEventArgs e)
        {
            Relayout();
            base.OnLayout(e);
            // WinForms keeps a horizontal bar it once needed even when nothing sticks out any more (seen after leaving
            // maximized): then the scrolling is switched off and on, which makes it work the bars out afresh
            HideSideBar();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr h, int bar, bool show);

        // the window's own horizontal bar (WS_HSCROLL) off when nothing sticks out: WinForms' HorizontalScroll.Visible said
        // false while the window still showed the bar, so it is asked of Windows, not of WinForms
        void HideSideBar()
        {
            if (!IsHandleCreated || (GetWindowLong(Handle, -16) & 0x100000) == 0) return;
            if (Controls.Cast<Control>().Any(c => c.Visible && c.Right > ClientSize.Width + 1)) return;
            if (AutoScrollPosition.X != 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y);
            ShowScrollBar(Handle, 0, false); // SB_HORZ
        }


        // a row that changed its height while being placed (its controls moved under the text in a narrow window): the
        // rows under it are placed again, at most a couple of times
        bool again;

        public void Relayout()
        {
            if (inLayout) return;
            for (int pass = 0; pass < 3; pass++)
            {
                again = false;
                Place();
                if (!again) break;
            }
        }

        void Place()
        {
            inLayout = true;
            SuspendLayout();
            if (AutoScrollPosition.X != 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y); // no sideways offset
            // the width leaves room for the vertical scroll bar whenever the page is taller than the window, decided before
            // the bar shows: cards sized for the width without it pushed past it once it appeared, and WinForms added a
            // horizontal bar that scrolled nothing (13.11, «Настройки»)
            int tall = Theme.S(44);
            foreach (var kv in items) if (kv.Key.Visible) tall += Theme.S(kv.Value) + kv.Key.Height;
            // 16.11: the room for the bar is kept on every page, tall or not. It used to be taken only where the page did
            // not fit, so the cards of a short page were a scroll bar wider than those of a long one: going between
            // «Кривая», «Автотюн» and «Сторож» (or the tabs of «Очистка») changed the width of everything. tall is left
            // for reading: it says whether the bar will show.
            // NoScroll (the mini panel): the page never scrolls, so no room is kept and the rows reach both sides alike
            int view = Width - (NoScroll ? 0 : SystemInformation.VerticalScrollBarWidth);
            int w = Math.Min(view, ClientSize.Width) - Theme.S(2 * Side);
            LineUpButtons();
            BeforeLayout(Math.Max(Theme.S(200), w));
            // 19.11: the places are worked out from the page's top (cy), the scrolling is added when a row is put there;
            // with movement a row is put where it stands now and glides to its place (see Motion)
            int wide = Math.Max(Theme.S(200), w), changed = 0;
            foreach (var kv in items) { Spot sp0; if (spots.TryGetValue(kv.Key, out sp0) && sp0.Was != kv.Key.Visible) changed++; }
            var nowAt = DateTime.Now;
            if (changed > 0) { if ((nowAt - burstAt).TotalMilliseconds > 60) { burstAt = nowAt; burst = 0; } burst += changed; }
            bool stampede = burst > 4 && (nowAt - burstAt).TotalMilliseconds <= 60;
            var form = FindForm();
            bool glide = Motion && !NoScroll && placedOnce && placedWidth == wide && !stampede && IsHandleCreated && Visible && form != null && form.Visible && form.WindowState != FormWindowState.Minimized;
            if (PlaceProbe != null) PlaceProbe("place: glide " + glide + ", changed " + changed + ", burst " + burst + (stampede ? " (stampede)" : "") + ", width " + placedWidth + "->" + wide + ", moving " + motion.Enabled + ", ghosts " + ghosts.Count);
            if (!glide && (motion.Enabled || ghosts.Count > 0)) MotionEnd();
            bool start = false;
            int cy = Theme.S(TopGap);
            foreach (var kv in items)
            {
                Spot sp; bool fresh = !spots.TryGetValue(kv.Key, out sp);
                if (fresh) spots[kv.Key] = sp = new Spot { Was = kv.Key.Visible };
                if (!kv.Key.Visible)
                {
                    // it has just been hidden: its last picture stays where it stood and shrinks away
                    Ghost early;
                    if (sp.Was && glide && sp.Placed && early_.TryGetValue(kv.Key, out early)) { early_.Remove(kv.Key); ghosts.Add(early); start = true; }
                    else if (sp.Was && glide && sp.Placed && kv.Key.Width > 0 && kv.Key.Height > 0)
                    {
                        var gh = TakeGhost(kv.Key, (int)Math.Round(sp.Y));
                        if (gh != null) { ghosts.Add(gh); start = true; }
                    }
                    if (sp.Showing) { sp.Showing = false; Uncover(kv.Key, int.MaxValue); }
                    sp.Was = false; continue;
                }
                cy += kv.Value == 8 && cy == Theme.S(TopGap) ? 0 : Theme.S(kv.Value);
                if (!glide || fresh || !sp.Placed) { sp.Y = sp.From = sp.To = cy; if (sp.Showing) { sp.Showing = false; Uncover(kv.Key, int.MaxValue); } }
                else if (!sp.Was)
                {
                    // it has just been shown: at its place at once, uncovered from the top as the rows under it make room
                    sp.Y = sp.From = sp.To = cy; sp.Showing = true; Uncover(kv.Key, 0); start = true;
                }
                else if (sp.To != cy) { sp.To = cy; start = true; }
                sp.Was = true; sp.Placed = true;
                int rowY = AutoScrollPosition.Y + (int)Math.Round(sp.Y);
                if (LiveResize && kv.Key.Width != wide && (rowY + kv.Key.Height < -Theme.S(60) || rowY > ClientSize.Height + Theme.S(60)))
                    { if (kv.Key.Top != rowY) kv.Key.Top = rowY; } // out of sight: the width comes when the drag ends
                else kv.Key.Bounds = new Rectangle(Theme.S(Side), rowY, wide, kv.Key.Height);
                cy += kv.Key.Height;
            }
            if (start)
            {
                // a new glide (or one more change during a glide): everything goes on from where it stands now
                foreach (var sp in spots.Values) sp.From = sp.Y;
                motionStart = DateTime.Now; motion.Start();
            }
            DropEarly(true); // pictures taken for rows that went without a glide after all (a row still shown waits for its hiding)
            placedOnce = true; placedWidth = wide;
            int y = AutoScrollPosition.Y + cy;
            if (!NoScroll) AutoScrollMinSize = new Size(0, y - AutoScrollPosition.Y + Theme.S(24));
            ResumeLayout();
            inLayout = false;
        }

        // 13.11: the rows with one button on the right («Balance ▾», «Открыть «Электропитание»», «Daily ▾») get one width
        // per page, the widest of them, so the column of buttons lines up. Rows with several buttons keep their own
        // widths (made equal they would squeeze the text), and so do the square «…» buttons.
        void LineUpButtons()
        {
            var lone = new List<FlatButton>();
            foreach (var kv in items)
            {
                var row = kv.Key as Row;
                if (row == null) continue;
                var buttons = row.RightControls.OfType<FlatButton>().ToList();
                if (buttons.Count == 1 && !buttons[0].Square) lone.Add(buttons[0]);
            }
            if (lone.Count < 2) return;
            int wide = lone.Max(b => b.NaturalWidth);
            foreach (var b in lone) b.MinWidth = wide;
        }

        // 20.11: the slide is the page's own glide now (see Motion: the rows are uncovered as the ones under them make room,
        // or shrink away as they close in). The pane of its own (14.11) ran beside that glide, which came in 19.11: every
        // step of the pane moved the rows under it and the glide started over to chase them, so they went down, back up
        // and down again with stops of up to 157 ms (measured with --motion-frames). (14.11 to 20.11: rows slid out of the row above as a picture.)
        public void Slide(Control[] rows, bool show)
        {
            foreach (var r in rows) r.Visible = show;
            Relayout();
        }

        // a bar under the page that doesn't scroll away (the window shows it at the bottom); null = none
        public Control Footer;

        // while a drop-down menu is open the pages don't scroll: the menu would stay put and the page slide away under it
        public static int MenusOpen;

        // a drop-down centred under its button (above = it opens upwards, for buttons near the bottom of the window)
        // 13.11: never narrower than its button (wider when the items need it), and centred by its real size once open —
        // the size known before showing missed the check-mark column on some lists, which then sat 16 px to the right
        public static void ShowAt(ContextMenuStrip m, Control anchor, bool above = false)
        {
            // the items as wide as the menu, so the highlight under the mouse (drawn in the item) fills it: a menu made
            // wider than its items kept them narrow and the highlight stopped short (13.11). The menu sizes its items by
            // what they need, a set size is ignored, so the room goes into their right padding, measured before
            // MinimumSize (which would hide the gap)
            int extra = anchor.Width - m.PreferredSize.Width;
            if (extra > 0)
                foreach (ToolStripItem it in m.Items)
                    it.Padding = new Padding(it.Padding.Left, it.Padding.Top, it.Padding.Right + extra, it.Padding.Bottom);
            m.MinimumSize = new Size(anchor.Width, 0);
            var size = m.PreferredSize;
            int w = Math.Max(anchor.Width, size.Width);
            m.Opened += (s, e) =>
            {
                var at = anchor.PointToScreen(Point.Empty);
                int x = at.X + (anchor.Width - m.Width) / 2;
                var screen = Screen.FromControl(anchor).WorkingArea;
                x = Math.Max(screen.Left, Math.Min(screen.Right - m.Width, x)); // still on the screen
                if (Math.Abs(m.Left - x) > 1) m.Left = x;
            };
            m.Show(anchor, new Point((anchor.Width - w) / 2, above ? -size.Height - Theme.S(2) : anchor.Height + Theme.S(2)));
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body, ShowImageMargin = true };
            m.Opened += (s, e) => MenusOpen++;
            m.Closed += (s, e) => { MenusOpen = Math.Max(0, MenusOpen - 1); m.BeginInvoke((Action)m.Dispose); };
            return m;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (MenusOpen > 0) { var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; return; }
            base.OnMouseWheel(e);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); HideSideBar(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Relayout(); }
    }
}
