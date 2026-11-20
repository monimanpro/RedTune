using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RedTune
{
    // --motion-frames <file>: how smoothly rows come and go on a page (20.11, the user saw jerks): a row under the ones
    // that appear is watched, every move of it timed. Two ways a page shows rows: the long help card of «Автотюн» /
    // «Сторож» (shown, the page's own glide uncovers it) and the advanced overclocking rows of «Питание» (Page.Slide).
    // Each is shown, then hidden again. A smooth run moves the watched row often (a frame every ~15 ms), only one way,
    // and ends where it belongs; «call» is how long the click itself held the window before the first frame.
    static class MotionFrames
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            Measure(sb, "help card (Автотюн)", false);
            Measure(sb, "slide (Питание, разгон)", true);
            MeasurePower(sb);
            if (Environment.GetEnvironmentVariable("RT_MF_TUNE") == "1") MeasureTune(sb);
            // what one frame of the uncovered help card cost before it kept its picture: the whole text laid out and drawn
            var card = new TextCard(TunePage.HelpText) { Width = Theme.S(940) };
            using (var bmp = new Bitmap(card.Width, card.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                var sw = Stopwatch.StartNew(); int n = 10;
                for (int i = 0; i < n; i++) TextRenderer.DrawText(g, L.T(TunePage.HelpText), Theme.Small, new Rectangle(Theme.S(16), Theme.S(14), card.Width - Theme.S(32), card.Height - Theme.S(28)), Theme.Text2, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                double draw = sw.Elapsed.TotalMilliseconds / n;
                sw.Restart();
                for (int i = 0; i < n; i++) g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, 300), new Rectangle(0, 0, bmp.Width, 300), GraphicsUnit.Pixel);
                sb.AppendLine("help text " + card.Width + "×" + card.Height + ": drawn anew " + draw.ToString("0.0") + " ms a frame (before), the kept picture shown " + (sw.Elapsed.TotalMilliseconds / n).ToString("0.0") + " ms (now)");
            }
            // a hidden row shrinks away as a picture the page draws on every frame (Page ghosts): what one frame of that costs
            // for the help card, drawn the plain way (32 bpp, blended) and the quick way (premultiplied, copied as it is)
            using (var target = new Bitmap(card.Width, card.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (var tg = Graphics.FromImage(target))
            using (var plain = new Bitmap(card.Width, card.Height))
            using (var quick = new Bitmap(card.Width, card.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                int n = 10; var all = new Rectangle(0, 0, card.Width, card.Height);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) tg.DrawImage(plain, all, all, GraphicsUnit.Pixel);
                double a1 = sw.Elapsed.TotalMilliseconds / n; sw.Restart();
                var cm = tg.CompositingMode; tg.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; tg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                for (int i = 0; i < n; i++) tg.DrawImage(quick, all, all, GraphicsUnit.Pixel);
                double a2 = sw.Elapsed.TotalMilliseconds / n; sw.Restart();
                for (int i = 0; i < n; i++) using (var b = new Bitmap(card.Width, card.Height)) { }
                sb.AppendLine("a shrinking picture of the help card, one frame: plain " + a1.ToString("0.0") + " ms, quick " + a2.ToString("0.0") + " ms");
            }
            card.Dispose();
            return sb.ToString();
        }

        class Quiet : Form { protected override bool ShowWithoutActivation { get { return true; } } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        // what the screen shows at a point of the window (the window must be on top: RT_MF_TUNE)
        static Color Pixel(Form f, Point at) { var s = f.PointToScreen(at); var dc = GetDC(IntPtr.Zero); try { uint c = GetPixel(dc, s.X, s.Y); return Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF)); } finally { ReleaseDC(IntPtr.Zero, dc); } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        static bool Near(Color a, Color b) { return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) <= 12; }

        static void Wait(int ms) { var until = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < until) Application.DoEvents(); } // no Sleep(1): it sleeps a whole 15.6 ms tick

        // the real «Питание» page: «Расширенные параметры разгона» switched on and off as a click does (the page reads the
        // laptop's BIOS overclocking mode; nothing is applied or saved: the editor only)
        static void MeasurePower(StringBuilder sb)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            using (var f = new Quiet { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = wa.Location, BackColor = Theme.Window, FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(Theme.S(1000), Theme.S(1400)) })
            {
                var s = Store.LoadSettings();
                var ps = new PowerService(); var pp = new PowerPage(ps, new CurveService(s), new FanControl(ps, s), s);
                f.Controls.Add(pp); f.Show(); SetWindowPos(f.Handle, (IntPtr)1, 0, 0, 0, 0, 0x0013); pp.ShowTab(0);
                Wait(2500); // the BIOS overclocking mode is read off the UI thread
                var oc = (Toggle)typeof(PowerPage).GetField("ocOn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pp);
                var ocRow = (Row)typeof(PowerPage).GetField("ocRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pp);
                sb.AppendLine("power page: the overclocking row " + (ocRow.Visible ? "shown" : "hidden (BIOS overclocking off: nothing to measure)") + ", switch " + oc.Checked);
                if (!ocRow.Visible) return;
                foreach (bool on in new[] { !oc.Checked, oc.Checked })
                {
                    var clock = Stopwatch.StartNew();
                    int places = 0;
                    Page.PlaceProbe = t => places++;
                    var states = new List<string>(); string last = null;
                    oc.Checked = on;
                    double call = clock.Elapsed.TotalMilliseconds;
                    var until = DateTime.Now.AddMilliseconds(600);
                    while (DateTime.Now < until) { Application.DoEvents(); var st = (pp.Moving ? "moving" : "still") + "/" + pp.GhostCount; if (st != last) { states.Add(clock.Elapsed.TotalMilliseconds.ToString("0") + ":" + st); last = st; } }
                    Page.PlaceProbe = null;
                    sb.AppendLine("  switch " + (on ? "on" : "off") + ": call " + call.ToString("0") + " ms, placings " + places + "; " + string.Join(" ", states));
                    Wait(400);
                }
            }
        }

        // RT_MF_TUNE=1: the real «Автотюн» page in a small window on top of everything for about two seconds (seen on the
        // screen!), its help opened and closed as the button does; a point inside the card's corner is read off the
        // screen all the time: «card» while the card or its picture is there, «page» where it is gone
        static void MeasureTune(StringBuilder sb)
        {
            var s = Store.LoadSettings();
            using (var f = new Quiet { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = Screen.PrimaryScreen.WorkingArea.Location, BackColor = Theme.Window, FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(Theme.S(800), Theme.S(680)), TopMost = true })
            {
                var tp = new TunePage(new Tuner(s), new CurveService(s), s, () => { }, () => { });
                f.Controls.Add(tp); f.Show(); tp.Relayout();
                Wait(400);
                var help = (Control)typeof(TunePage).GetField("help", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(tp);
                var head = (Row)typeof(TunePage).GetField("headRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(tp);
                var btn = head.RightControls.OfType<FlatButton>().First();
                Action click = () => { var c = (IntPtr)((btn.Height / 2 << 16) | (btn.Width / 2)); SendMessage(btn.Handle, 0x201, (IntPtr)1, c); SendMessage(btn.Handle, 0x202, IntPtr.Zero, c); }; // a press and a release, as the mouse sends
                foreach (bool show in new[] { true, false })
                {
                    var clock = Stopwatch.StartNew();
                    // three points down the left edge of the card, inside it: 8, 150 and 300 px under its top
                    var spots = new Point[3]; var lines = new List<string>[3]; var lastC = new string[3];
                    for (int k = 0; k < 3; k++) lines[k] = new List<string>();
                    Action look = () =>
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            if (spots[k] == Point.Empty) continue;
                            var px = Pixel(f, spots[k]);
                            string name = Near(px, Theme.Card) ? "card" : Near(px, Theme.Window) ? "page" : "other";
                            if (name != lastC[k]) { lines[k].Add(clock.Elapsed.TotalMilliseconds.ToString("0") + ":" + name); lastC[k] = name; }
                        }
                    };
                    Action aim = () => { int[] dy = { 8, 150, 300 }; for (int k = 0; k < 3; k++) spots[k] = f.PointToClient(tp.PointToScreen(new Point(help.Left + Theme.S(8), help.Top + Theme.S(dy[k])))); };
                    if (!show) { aim(); look(); }
                    click();
                    if (show) aim();
                    look();
                    var until = DateTime.Now.AddMilliseconds(450);
                    while (DateTime.Now < until) { Application.DoEvents(); look(); }
                    sb.AppendLine("Автотюн help " + (show ? "opened" : "closed") + " by a click (help " + (help.Visible ? "shown" : "hidden") + "):");
                    for (int k = 0; k < 3; k++) sb.AppendLine("    point " + (k + 1) + " " + spots[k] + ": " + string.Join(" ", lines[k]));
                    Wait(300);
                }
            }
        }

        static void Measure(StringBuilder sb, string title, bool slide)
        {
            // on the screen (Windows paints nothing of a window outside it, and a hidden row's shrinking picture is all paint),
            // at the bottom of the window order and never active: under every other window. RT_MF_OFF=1: outside the screen
            var wa = Screen.PrimaryScreen.WorkingArea;
            bool off = Environment.GetEnvironmentVariable("RT_MF_OFF") == "1";
            using (var f = new Quiet { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = off ? new Point(-4000, -4000) : wa.Location, BackColor = Theme.Window, // not layered (no Opacity): a layered window this big is composed whole on every frame, the program's is not
                FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(Theme.S(1000), Theme.S(1400)) })
            {
                var page = new Page();
                f.Controls.Add(page);
                for (int i = 0; i < 4; i++) page.Row("Строка " + (i + 1), "Подпись под строкой", new Toggle());
                var shown = new List<Control>();
                if (slide) for (int i = 0; i < 4; i++) { var r = page.Row("Разгон " + (i + 1), "Строка, которая выезжает", new Slider(0, 100, 50, "%") { Width = Theme.S(300) }); r.Visible = false; shown.Add(r); }
                else { var t = page.Add(new TextCard(TunePage.HelpText)); t.Visible = false; shown.Add(t); }
                var watched = page.Row("Строка под ними", "За ней следит замер", new Toggle());
                for (int i = 0; i < 6; i++) page.Row("Ещё строка " + (i + 1), null, new Toggle());
                f.Show(); SetWindowPos(f.Handle, (IntPtr)1, 0, 0, 0, 0, 0x0013); page.Relayout(); // HWND_BOTTOM; NOSIZE | NOMOVE | NOACTIVATE
                Wait(300); // the page's first placing hides rows one by one, which reads as a stampede for 60 ms: a click comes later
                foreach (bool show in new[] { true, false })
                {
                    int y0 = watched.Top;
                    var clock = Stopwatch.StartNew();
                    var moves = new List<KeyValuePair<double, int>>();
                    EventHandler moved = (s, e) => moves.Add(new KeyValuePair<double, int>(clock.Elapsed.TotalMilliseconds, watched.Top));
                    watched.LocationChanged += moved;
                    var paints = new List<double>();
                    Page.PaintProbe = ms => paints.Add(ms);
                    int places = 0; Page.PlaceProbe = t => places++;
                    var rowPaints = new List<double>();
                    Painted.PaintProbe = ms => rowPaints.Add(ms);
                    // the help card: a point just inside its top left corner, read from the window's surface all the time
                    var spot = f.PointToClient(page.PointToScreen(new Point(shown[0].Left + Theme.S(10), shown[0].Top + Theme.S(10))));
                    var colours = new List<string>(); string lastC = null;
                    Action look = () => { if (slide) return; var px = Pixel(f, spot); string name = Near(px, Theme.Card) ? "card" : Near(px, Theme.Window) ? "page" : "other(" + px.R + "," + px.G + "," + px.B + ")"; if (name != lastC) { colours.Add(clock.Elapsed.TotalMilliseconds.ToString("0") + ":" + name); lastC = name; } };
                    look();
                    if (slide) page.Slide(shown.ToArray(), show);
                    else { shown[0].Visible = show; look(); page.Relayout(); }
                    double call = clock.Elapsed.TotalMilliseconds;
                    look();
                    var until2 = DateTime.Now.AddMilliseconds(700);
                    while (DateTime.Now < until2) { Application.DoEvents(); look(); }
                    Page.PaintProbe = null; Painted.PaintProbe = null; Page.PlaceProbe = null;
                    watched.LocationChanged -= moved;
                    int y1 = watched.Top;
                    bool was = Page.Motion; Page.Motion = false; page.Relayout(); int want = watched.Top; Page.Motion = was; // where it belongs
                    int back = 0; for (int i = 1; i < moves.Count; i++) if (show ? moves[i].Value < moves[i - 1].Value : moves[i].Value > moves[i - 1].Value) back++;
                    double gap = 0; for (int i = 1; i < moves.Count; i++) gap = Math.Max(gap, moves[i].Key - moves[i - 1].Key);
                    double span = moves.Count > 1 ? moves[moves.Count - 1].Key - moves[0].Key : 0;
                    var path = string.Join(" ", moves.Take(40).Select(m => m.Key.ToString("0") + ":" + (m.Value - y0)));
                    sb.AppendLine(title + (show ? ", shown: " : ", hidden: ") + moves.Count + " moves in " + span.ToString("0") + " ms, largest gap " + gap.ToString("0") + " ms, the wrong way " + back
                        + ", call " + call.ToString("0") + " ms, page paints " + paints.Count + (paints.Count > 0 ? " (avg " + paints.Average().ToString("0.0") + ", max " + paints.Max().ToString("0.0") + ", total " + paints.Sum().ToString("0") + " ms)" : "")
                        + ", placings " + places + ", control paints " + rowPaints.Count + (rowPaints.Count > 0 ? " (total " + rowPaints.Sum().ToString("0") + " ms, max " + rowPaints.Max().ToString("0.0") + ")" : "")
                        + ", ends at " + (y1 - y0) + " (" + (want - y0) + " wanted)\r\n    " + path + (slide ? "" : "\r\n    at the card's corner: " + string.Join(" ", colours)));
                    Wait(300);
                }
            }
        }
    }
}
