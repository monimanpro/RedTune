using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // controls whose size depends on their text: re-measured when the language changes
    interface ILang { void Relang(); }

    // 16.11: a window with a picture behind its controls (the mini panel: each block is glass over the blurred screen).
    // A control inside it starts from its own piece of that picture instead of a flat colour, so the picture runs
    // unbroken under the rows, their buttons and switches. Backdrop: in the window's client coordinates; null: none now.
    // BackdropDc: the same picture selected into a GDI memory DC (zero: none), for a fast copy.
    // Solid: not null while the window is taking a picture of what its controls draw by themselves (17.11, the mini
    // panel's live glass): the controls then start from this plain colour instead of the picture.
    // Whole (17.11, the mini panel as blocks with Windows' blur): the picture already has everything in it that the
    // controls draw, with its transparency; a control then answers a paint message by copying its piece of the
    // picture and draws nothing itself (Backdrops.Whole). Fresh: the picture brought up to date before that.
    interface IBackdrop { Bitmap Backdrop { get; } IntPtr BackdropDc { get; } Color? Solid { get; } bool Whole { get; } void Fresh(); }

    static class Backdrops
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc; public int fErase, left, top, right, bottom, fRestore, fIncUpdate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);

        // A paint message of a control (or of the window itself) whose window keeps the whole picture: the control's
        // piece of that picture is copied to the screen as it is, transparency and all, and the control's own
        // drawing does not run. true: done. Not while the window is taking the picture of its controls (Solid).
        public static bool Whole(Control c, ref Message m)
        {
            if (m.Msg != 0x000F || m.WParam != IntPtr.Zero) return false; // WM_PAINT, not a print into somebody's DC
            var host = c as IBackdrop ?? Of(c);
            if (host == null || !host.Whole || host.Solid.HasValue) return false;
            try { host.Fresh(); } catch { }
            IntPtr src = host.BackdropDc;
            if (src == IntPtr.Zero) return false;
            var at = ReferenceEquals(c, host) ? Point.Empty : ((Control)host).PointToClient(c.PointToScreen(Point.Empty));
            PAINTSTRUCT ps;
            IntPtr dc = BeginPaint(c.Handle, out ps);
            try { BitBlt(dc, 0, 0, c.Width, c.Height, src, at.X, at.Y, 0x00CC0020); }
            finally { EndPaint(c.Handle, ref ps); }
            m.Result = IntPtr.Zero;
            return true;
        }

        public static IBackdrop Of(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent) { var b = p as IBackdrop; if (b != null) return b.Backdrop != null ? b : null; }
            return null;
        }

        // the piece of the picture c stands on, over all of c; false: no picture, the caller paints its own colour
        public static bool Paint(Graphics g, Control c)
        {
            var host = Of(c);
            if (host == null || c.Width <= 0 || c.Height <= 0) return false;
            try
            {
                var at = ((Control)host).PointToClient(c.PointToScreen(Point.Empty));
                var solid = host.Solid;
                if (solid.HasValue) { g.Clear(solid.Value); return true; }
                IntPtr src = host.BackdropDc;
                if (src != IntPtr.Zero)
                {
                    IntPtr dc = g.GetHdc();
                    try { if (BitBlt(dc, 0, 0, c.Width, c.Height, src, at.X, at.Y, 0x00CC0020)) return true; } // SRCCOPY
                    finally { g.ReleaseHdc(dc); }
                }
                // a plain copy of pixels (no blending, no filtering): with live glass this runs for every control on
                // every changed frame
                var cm = g.CompositingMode; var im = g.InterpolationMode;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(host.Backdrop, new Rectangle(0, 0, c.Width, c.Height), new Rectangle(at.X, at.Y, c.Width, c.Height), GraphicsUnit.Pixel);
                g.CompositingMode = cm; g.InterpolationMode = im;
                return true;
            }
            catch { return false; }
        }
    }

    // 19.11: a click anywhere outside a text field takes the caret out of it. The program's own controls are drawn by
    // hand and never take the keyboard, so a field that was being typed in (the number of a slider, a value of the
    // curve, a memory timing) stayed in editing whatever was clicked next: only a click into another field ended it.
    // Leaving the field ends the typing as it always did there: a slider takes its number as typed, then the click
    // goes on to what was clicked. One filter for the whole program (Program.Main), the dialogs included.
    class ClickAway : IMessageFilter
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetFocus();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);

        public bool PreFilterMessage(ref Message m)
        {
            // a mouse button going down (left, right, middle), inside a window or on its frame and scroll bars
            if (m.Msg != 0x201 && m.Msg != 0x204 && m.Msg != 0x207 && m.Msg != 0xA1 && m.Msg != 0xA4) return false;
            try
            {
                IntPtr f = GetFocus();
                if (f == IntPtr.Zero || f == m.HWnd) return false;
                var box = Control.FromHandle(f) as TextBoxBase;
                if (box == null || Control.FromHandle(m.HWnd) is TextBoxBase) return false; // another field takes the caret itself
                var form = box.FindForm();
                if (form == null) return false;
                form.ActiveControl = null;
                if (GetFocus() == f) SetFocus(form.Handle);
            }
            catch { }
            return false; // the click itself goes where it was going
        }
    }

    abstract class Painted : Control
    {
        protected bool OnGlass { get { return Backdrops.Of(this) != null; } }
        // the background: the window's picture when it has one, else the control's colour
        protected void Back(Graphics g) { if (!Backdrops.Paint(g, this)) g.Clear(BackColor); }
        // what a button fills itself with at rest: its background colour, or over glass a faint shade that lets it show
        protected Color Rest { get { return OnGlass ? Color.FromArgb(70, 0, 0, 0) : BackColor; } }

        // 20.11: the page takes the picture of a row before Windows hides it (Page.BeforeHide)
        protected override void SetVisibleCore(bool value)
        {
            if (!value && Visible) { var page = Parent as Page; if (page != null) page.BeforeHide(this); }
            base.SetVisibleCore(value);
        }

        protected override void WndProc(ref Message m)
        {
            if (Backdrops.Whole(this, ref m)) return;
            if (m.Msg != 0x000F || PaintProbe == null) { base.WndProc(ref m); return; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); base.WndProc(ref m);
            PaintProbe((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        internal static Action<double> PaintProbe; // --motion-frames: how long each paint took (null: not measured)

        // 21.11 (the user: the open sound page took ~38 % of a core): a moment of the sound repaints a control only while
        // some of it is in sight. Windows still paints a control scrolled out of the page's view when it is invalidated
        // (measured: 20 paints of 20), so the compressor below the window was drawn 30 times a second for nobody. When
        // it scrolls back in, Windows paints the uncovered part as usual, with what it shows by then.
        internal bool InView { get { var p = Parent; return p != null && Visible && p.ClientRectangle.IntersectsWith(Bounds); } }
        protected void LiveInvalidate(Rectangle r) { if (InView) Invalidate(r); }

        protected Painted()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
        }

        // a hidden control skips layout (WinForms defers it): a language switch resized the buttons of pages that were
        // not open, and their rows kept the old positions (12.11: «Custom» over the preset button). Lay out again when shown.
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) { PerformLayout(); Invalidate(); } }
        protected override void OnParentVisibleChanged(EventArgs e) { base.OnParentVisibleChanged(e); if (Visible) { PerformLayout(); Invalidate(); } }
    }
}
