using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace RedTune
{
    // --page-png <curve|amdpower|home|gpu> <out.png>: a page drawn into a picture off screen, for checking how it looks on another
    // processor (with RT_HW_SIM) without starting a second copy of the app. Reads only: the curve grid gets sample values,
    // the power page asks the SMU what it accepts (AmdPower.Check).
    static class PagePng
    {
        // --plan-ui-check <file>: the plan block of the power page on a copy of the active plan made for the check: changes
        // wait, «Записать в схему» writes them, «Отменить» drops them. The copy is deleted; no plan of the user is written.
        public static void PlanUiCheck(string file, Settings s)
        {
            string text; Guid id = WinPower.MakeTestCopy(); Guid active = WinPower.Active();
            try
            {
                var ps = new PowerService();
                using (var pp = new PowerPage(ps, new CurveService(s), new FanControl(ps, s), s)) text = pp.TestCfg(id);
            }
            catch (Exception ex) { text = "FAILED: " + ex + "\r\n"; }
            finally { }
            text += "the copy deleted: " + (WinPower.DeleteTestCopy(id) ? "yes" : "NO") + "\r\nthe active plan: " + (WinPower.Active() == active ? "the same" : "CHANGED") + "\r\n";
            System.IO.File.WriteAllText(file, text, new System.Text.UTF8Encoding(true));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

        // --motion-check <file>: the movement of the pages' rows (Page, «movement where things come and go») on a page of
        // six rows in a window of its own, off the screen: a row hidden and shown glides, many at once do not. Pictures
        // of the window in the middle of each movement go beside the file (taken with PrintWindow, as Windows draws it).
        public static void MotionCheck(string file)
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            Action<int> pump = ms => { var until = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } };
            Page.Motion = true;
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(Theme.S(760), Theme.S(520)), BackColor = Theme.Window })
            {
                var page = new Page(); f.Controls.Add(page);
                var block = page.Add(new Panel { Height = Theme.S(30), BackColor = Theme.Card }); // something that can grow, over the rows
                var rows = new System.Collections.Generic.List<Row>();
                for (int i = 1; i <= 6; i++) rows.Add(page.Row("Строка " + i, "Проверка движения, строка " + i, new Toggle()));
                f.Show(); pump(300);
                Action<string> shot = name =>
                {
                    f.Update(); page.Update(); foreach (Control c in page.Controls) c.Update(); // what waits to be drawn is drawn first
                    using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); try { PrintWindow(f.Handle, dc, 3); } finally { g.ReleaseHdc(dc); } }
                        bmp.Save(System.IO.Path.ChangeExtension(file, null) + "-" + name + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                };
                int[] top0 = rows.Select(r => r.Top).ToArray(); int step = top0[1] - top0[0];
                line(!page.Moving && step > rows[0].Height, "six rows placed at once, " + step + " px apart; nothing moves");

                // a row hidden: the rows under it glide up, its last picture shrinks away
                rows[2].Visible = false; page.Relayout();
                int a0 = rows[3].Top; bool moving0 = page.Moving; int ghosts0 = page.GhostCount;
                var pic = page.GhostPic(0); int ink = 0;
                if (pic != null) { var bg = pic.GetPixel(pic.Width / 2, pic.Height - 3); for (int y = 0; y < pic.Height; y += 2) for (int x = 0; x < pic.Width; x += 2) { var c = pic.GetPixel(x, y); if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 90) ink++; } }
                pump(40); shot("hide-1"); pump(50); int a1 = rows[3].Top; shot("hide"); pump(60); shot("hide-3");
                pump(500); int a2 = rows[3].Top;
                line(moving0 && ghosts0 == 1 && a0 == top0[3], "row 3 hidden: the movement starts, row 4 is still at " + a0 + ", the hidden row's picture is kept (" + ghosts0 + ")");
                line(ink > 50, "that picture has the row in it (" + ink + " points of ink)");
                line(a1 < top0[3] && a1 > top0[2], "90 ms later row 4 is on its way: " + a1 + " (from " + top0[3] + " to " + top0[2] + ")");
                line(a2 == top0[2] && rows[5].Top == top0[4] && !page.Moving && page.GhostCount == 0, "then in place: row 4 at " + a2 + ", row 6 at " + rows[5].Top + "; nothing moves, no picture left");

                // shown again: at its place at once, uncovered from the top; the rows under it glide down
                rows[2].Visible = true; page.Relayout();
                bool cut0 = rows[2].Region != null; int b0 = rows[3].Top, own = rows[2].Top;
                pump(90); int b1 = rows[3].Top; bool cut1 = rows[2].Region != null; shot("show");
                pump(500);
                line(page.GhostCount == 0 && cut0 && own == top0[2] && b0 == top0[2], "row 3 shown again: it stands at its place (" + own + ") cut to nothing, row 4 still over it at " + b0);
                line(b1 > top0[2] && b1 < top0[3] && cut1, "90 ms later row 4 is on its way down: " + b1 + ", row 3 is partly uncovered");
                line(rows[2].Region == null && !page.Moving && rows.Select(r => r.Top).SequenceEqual(top0), "then all six where they were, row 3 whole");

                // a change in the middle of a movement: on from where things stand
                rows[1].Visible = false; page.Relayout(); pump(70); int mid = rows[4].Top; rows[1].Visible = true; page.Relayout(); int after = rows[4].Top; pump(600);
                line(mid < top0[4] && after == mid && rows.Select(r => r.Top).SequenceEqual(top0) && !page.Moving && page.GhostCount == 0 && rows[1].Region == null, "hidden and shown again halfway: no jump (row 5 at " + mid + " before and " + after + " after the turn), all back in place");

                // many rows at once: no movement
                // (a page hides its rows one by one, and each change places them anew — as a switched tab does)
                pump(120);
                for (int i = 1; i < 6; i++) rows[i].Visible = false;
                bool still = !page.Moving && page.GhostCount == 0;
                for (int i = 1; i < 6; i++) rows[i].Visible = true;
                line(still && !page.Moving && page.GhostCount == 0 && rows.All(r => r.Region == null) && rows.Select(r => r.Top).SequenceEqual(top0), "five rows hidden one after another and shown again: placed at once, no movement");

                // something that grows: the rows under it glide down
                pump(120);
                block.Height += Theme.S(40); int g0 = rows[0].Top; pump(90); int g1 = rows[0].Top; pump(500);
                line(g0 == top0[0] && g1 > top0[0] && g1 < top0[0] + Theme.S(40) && rows[0].Top == top0[0] + Theme.S(40), "the block over the rows made 40 taller: row 1 glides down (" + g0 + ", " + g1 + ", " + rows[0].Top + ")");
                block.Height -= Theme.S(40); pump(600);

                // another width (the window resized) in the middle of a movement: placed at once
                rows[3].Visible = false; bool wasMoving = page.Moving; f.ClientSize = new Size(Theme.S(700), Theme.S(520)); page.Relayout();
                line(wasMoving && !page.Moving && page.GhostCount == 0 && rows[4].Top == top0[3], "the window resized while rows glide: they are placed at once (row 5 at " + rows[4].Top + ")");
            }
            System.IO.File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // --ui-edit-check <file>: two sliders and a button on a window of their own, driven by the mouse messages Windows
        // sends: the pointer over a slider's knob after the mouse left it through its number, and the number being typed
        // when something else is clicked (19.11, both were wrong). Changes nothing of the program's settings.
        public static void EditCheck(string file)
        {
            var sb = new System.Text.StringBuilder();
            Func<int, int, IntPtr> at = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
            Action wait = () => { var until = DateTime.Now.AddMilliseconds(250); while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(15); } };
            Application.AddMessageFilter(new ClickAway());
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(Theme.S(500), Theme.S(200)), BackColor = Theme.Window })
            {
                var a = new Slider(0, 100, 10, "%") { Location = new Point(Theme.S(20), Theme.S(20)) };
                var b = new Slider(0, 100, 20, "%") { Location = new Point(Theme.S(20), Theme.S(70)) };
                var btn = new FlatButton("OK") { Location = new Point(Theme.S(20), Theme.S(130)) };
                int changes = 0; a.Changed += (o, e) => changes++;
                f.Controls.AddRange(new Control[] { a, b, btn });
                f.Show(); f.Activate(); wait();
                int knobX = Theme.S(60), numX = a.Width - Theme.S(20), y = a.Height / 2;

                // the pointer: knob, number, away through the number, back to the knob
                SendMessage(a.Handle, 0x200, IntPtr.Zero, at(knobX, y)); var c1 = a.Cursor;
                SendMessage(a.Handle, 0x200, IntPtr.Zero, at(numX, y)); var c2 = a.Cursor;
                SendMessage(a.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero); var c3 = a.Cursor;     // WM_MOUSELEAVE
                SendMessage(b.Handle, 0x200, IntPtr.Zero, at(knobX, y)); var c4 = b.Cursor;
                SendMessage(a.Handle, 0x200, IntPtr.Zero, at(knobX, y)); var c5 = a.Cursor;
                Func<Cursor, string> name = c => c == Cursors.Hand ? "hand" : c == Cursors.IBeam ? "text" : "other";
                bool ok1 = c1 == Cursors.Hand && c2 == Cursors.IBeam && c3 == Cursors.Hand && c4 == Cursors.Hand && c5 == Cursors.Hand;
                sb.AppendLine((ok1 ? "ok    " : "FAIL  ") + "pointer: knob " + name(c1) + ", number " + name(c2) + ", after leaving through the number " + name(c3) + ", the other slider's knob " + name(c4) + ", back on the first knob " + name(c5));

                // typing a number, then a click on the button: the typing ends and the number is taken
                PostMessage(a.Handle, 0x201, (IntPtr)1, at(numX, y)); PostMessage(a.Handle, 0x202, IntPtr.Zero, at(numX, y)); wait();
                var box = a.Controls.Count > 0 ? a.Controls[0] as TextBox : null;
                bool opened = box != null, focused = box != null && box.Focused;
                if (box != null) box.Text = "42";
                PostMessage(btn.Handle, 0x201, (IntPtr)1, at(5, 5)); PostMessage(btn.Handle, 0x202, IntPtr.Zero, at(5, 5)); wait();
                bool closed = a.Controls.Count == 0;
                sb.AppendLine((opened && closed && a.Value == 42 ? "ok    " : "FAIL  ") + "a click on a button ends the typing: field opened " + opened + " (with the caret: " + focused + "), closed after the click " + closed + ", value " + a.Value + " (typed 42)");

                // typing, then a click on the same slider's track: the typed number is taken first, then the track sets its own
                PostMessage(a.Handle, 0x201, (IntPtr)1, at(numX, y)); PostMessage(a.Handle, 0x202, IntPtr.Zero, at(numX, y)); wait();
                box = a.Controls.Count > 0 ? a.Controls[0] as TextBox : null; if (box != null) box.Text = "77";
                PostMessage(a.Handle, 0x201, (IntPtr)1, at(knobX, y)); PostMessage(a.Handle, 0x202, IntPtr.Zero, at(knobX, y)); wait();
                sb.AppendLine((a.Controls.Count == 0 && a.Value != 77 && a.Value != 42 ? "ok    " : "FAIL  ") + "a click on the slider's own track ends the typing and the track wins: closed " + (a.Controls.Count == 0) + ", value " + a.Value);

                // typing, then a click on the other slider's number: the first field closes, the second opens
                PostMessage(a.Handle, 0x201, (IntPtr)1, at(numX, y)); PostMessage(a.Handle, 0x202, IntPtr.Zero, at(numX, y)); wait();
                PostMessage(b.Handle, 0x201, (IntPtr)1, at(numX, y)); PostMessage(b.Handle, 0x202, IntPtr.Zero, at(numX, y)); wait();
                sb.AppendLine((a.Controls.Count == 0 && b.Controls.Count == 1 ? "ok    " : "FAIL  ") + "a click on another slider's number moves the typing there: first closed " + (a.Controls.Count == 0) + ", second open " + (b.Controls.Count == 1));
                // ...and a click on the empty window closes that one too
                PostMessage(f.Handle, 0x201, (IntPtr)1, at(Theme.S(400), Theme.S(180))); PostMessage(f.Handle, 0x202, IntPtr.Zero, at(Theme.S(400), Theme.S(180))); wait();
                sb.AppendLine((b.Controls.Count == 0 && b.Value == 20 ? "ok    " : "FAIL  ") + "a click on empty space ends the typing, nothing typed changes nothing: closed " + (b.Controls.Count == 0) + ", value " + b.Value);
                sb.AppendLine("the first slider reported " + changes + " changes");
            }
            System.IO.File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        // --eq-strips-check <file>: the strips beside the equalizer's plot (a fader and two dials per band), driven by the
        // mouse messages Windows sends, on a window of its own with five bands of no gain. Changes nothing of the settings.
        public static void EqCheck(string file)
        {
            Theme.WarpPointer = false; // the real pointer stays where it is
            var sb = new System.Text.StringBuilder();
            Func<int, int, IntPtr> at = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
            Action wait = () => { var until = DateTime.Now.AddMilliseconds(120); while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } };
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(Theme.S(860), Theme.S(320)), BackColor = Theme.Window })
            {
                var q = new EqGraph { Location = new Point(0, 0), Width = Theme.S(860) };
                int changes = 0; q.Changed += (o, e) => changes++;
                f.Controls.Add(q); f.Show(); wait();
                var p = q.P; // the points at rest: twelve bells of no gain, evenly from 27 Hz to 15 kHz (EvenBands)
                // the check drives four of them as it did the old five: the lowest, 84 Hz, 845 Hz and 4.75 kHz
                int i80 = 2, i250 = 4, i1k = 6, i4k = 9;
                p.Bands[i80].Type = 2; p.Bands[i80].Freq = 80; p.Bands[i80].Q = 0.7f; p.Bands[i250].Freq = 250; p.Bands[i1k].Freq = 1000; p.Bands[i4k].Freq = 4000;
                for (int b = 0; b < p.Bands.Count; b++) if (b != i80) p.Bands[b].Q = 1f;
                var rest = SoundParams.EvenBands();
                line(rest.Count == 12 && rest.All(b => b.Type == 1 && b.Gain == 0) && rest.Zip(rest.Skip(1), (x, y) => Math.Log(y.Freq / x.Freq)).All(r => Math.Abs(r - Math.Log(Math.Pow(1000, 1.0 / 12))) < 0.04),
                    "the points at rest: twelve bells of no gain, evenly spread: " + string.Join(", ", rest.Select(b => b.Freq)));
                var reset = SoundParams.Defaults(true);
                line(reset.On && !reset.Eq && !reset.Comp && !reset.Limit && !reset.Gate && !reset.Denoise && !reset.Dereverb && reset.Preamp == 0 && reset.Split == 0 && reset.Bands.Count == 12,
                    "«Сбросить»: every effect off, the processing kept on, the points at rest");
                Action<int, int> down = (x, y) => SendMessage(q.Handle, 0x201, (IntPtr)1, at(x, y));
                Action<int, int> move = (x, y) => SendMessage(q.Handle, 0x200, (IntPtr)1, at(x, y));
                Action<int, int> hover = (x, y) => SendMessage(q.Handle, 0x200, IntPtr.Zero, at(x, y));
                Action<int, int> up = (x, y) => SendMessage(q.Handle, 0x202, IntPtr.Zero, at(x, y));
                Action<int, int> wheelPress = (x, y) => { SendMessage(q.Handle, 0x207, (IntPtr)0x10, at(x, y)); SendMessage(q.Handle, 0x208, IntPtr.Zero, at(x, y)); }; // 20.11: the reset
                int top = q.FaderTop, bottom = q.FaderBottom, mid = (top + bottom) / 2;

                line(q.Panel.Width > 0 && q.Order().Length == 12 && p.Bands.Count == 12, "twelve bands from the start: twelve strips (panel " + q.Panel.Width + " px wide): " + string.Join(", ", p.Bands.Select(b => b.Freq)));

                // the fader of the third strip (1 kHz): a quarter down from the top is half the scale up, then all the way down
                int x2 = q.StripX(i1k);
                // (20.11: a press does not move the knob, the pointer goes onto it; the drag counts from where it was taken)
                int y2 = top + (bottom - top) / 4;
                down(x2, y2); float g0 = p.Bands[i1k].Gain; move(x2, y2 - (bottom - top) / 4); float g1 = p.Bands[i1k].Gain;
                move(x2, bottom + 40); float g2 = p.Bands[i1k].Gain; up(x2, bottom + 40);
                line(g0 == 0 && Math.Abs(g1 - 9f) < 0.3f && g2 == -18f && p.Bands[i1k - 1].Gain == 0 && p.Bands[i1k + 1].Gain == 0, "fader: the press leaves it at " + g0 + " dB, a quarter of the travel up gives " + g1 + " dB (9 expected), far below " + g2 + " dB (-18), the neighbours stay at 0");

                // 20.11: a double click on it changes nothing any more; a press of the wheel: no gain
                down(x2, mid + 30); up(x2, mid + 30); SendMessage(q.Handle, 0x203, (IntPtr)1, at(x2, mid + 30)); up(x2, mid + 30); float gd = p.Bands[i1k].Gain;
                wheelPress(x2, mid + 30);
                line(gd == -18f && p.Bands[i1k].Gain == 0, "double click on a fader keeps " + gd + " dB (-18); a press of the wheel: gain " + p.Bands[i1k].Gain + " dB");

                // the frequency dial of the first strip (80 Hz): half the travel up is half the range in octaves, x31.6
                int x0 = q.StripX(i80);
                down(x0, q.FreqY); move(x0, q.FreqY - Theme.S(110)); float f1 = p.Bands[i80].Freq; int[] during = (int[])q.Order().Clone();
                up(x0, q.FreqY - Theme.S(110)); hover(x0, q.FreqY); int[] after = q.Order();
                line(f1 > 2300 && f1 < 2800 && during[i80] == i80 && after[i80] == i80, "frequency dial: 80 Hz -> " + f1 + " Hz; the strip stays where it was, held and after (21.11, the user)");
                p.Bands[i80].Freq = 80;

                // the width dial of the strip that is first now (250 Hz): a quarter of the travel down
                down(q.StripX(i250), q.WideY); move(q.StripX(i250), q.WideY + Theme.S(55)); float w1 = p.Bands[i250].Q; up(q.StripX(i250), q.WideY + Theme.S(55));
                line(w1 < 0.5f && w1 > 0.2f, "width dial: Q 1 -> " + w1 + " (about 0.36 expected)");
                // 21.11: a press of the wheel on a frequency dial: back to where that strip stands at rest
                p.Bands[i250].Freq = 333; wheelPress(q.StripX(i250), q.FreqY); float fr = p.Bands[i250].Freq;
                wheelPress(q.StripX(i250), q.WideY); float qr = p.Bands[i250].Q;
                line(fr == SoundParams.EvenBands()[i250].Freq && qr == SoundParams.EvenBands()[i250].Q, "a press of the wheel: the frequency dial back to " + fr + " Hz, the width dial to Q " + qr + " (at rest)");
                p.Bands[i250].Freq = 250; p.Bands[i250].Q = w1;
                // 21.11: the wheel over a kind picture goes through the kinds; a press of the wheel there: a bell
                int kx = q.StripX(i4k), ky = q.Panel.Y + Theme.S(9);
                down(kx, ky); up(kx, ky);   /* a left click on the picture takes it (no list), and the wheel works at once */
                SendMessage(q.Handle, 0x20A, (IntPtr)(-120 << 16), at(0, 0)); int kd1 = p.Bands[i4k].Type;
                SendMessage(q.Handle, 0x20A, (IntPtr)(-120 << 16), at(0, 0)); int kd2 = p.Bands[i4k].Type;
                SendMessage(q.Handle, 0x20A, (IntPtr)(120 << 16), at(0, 0)); int kd3 = p.Bands[i4k].Type;
                wheelPress(kx, ky); int kd4 = p.Bands[i4k].Type;
                line(kd1 == 2 && kd2 == 3 && kd3 == 2 && kd4 == 1, "the wheel over a kind picture: bell -> " + EqGraph.Kinds[kd1] + " -> " + EqGraph.Kinds[kd2] + ", back -> " + EqGraph.Kinds[kd3] + "; a press of the wheel -> " + EqGraph.Kinds[kd4]);

                // the wheel over a fader after a click on the graph: half a dB a notch
                hover(q.StripX(3), mid); SendMessage(q.Handle, 0x20A, (IntPtr)(120 << 16), at(0, 0)); SendMessage(q.Handle, 0x20A, (IntPtr)(120 << 16), at(0, 0));
                int b3 = q.Order()[3];
                line(p.Bands[b3].Gain == 1f, "wheel over a fader: two notches up give " + p.Bands[b3].Gain + " dB");

                // 20.11: the pre-gain's strip left of the bands: its fader (±24 dB by half a dB), the points ride on the line
                // it moves, a point dragged keeps its own gain, the wheel and a double click
                int xm = q.MasterX; float k0 = q.Knob(i1k).Y;
                int ym = top + (bottom - top) / 4; down(xm, ym); float m0 = p.Preamp;
                move(xm, ym - (bottom - top) / 4); float m1 = p.Preamp; move(xm, bottom + 40); float m2 = p.Preamp; move(xm, ym + (bottom - top) / 8); float m3 = p.Preamp; up(xm, ym + (bottom - top) / 8);
                float k1 = q.Knob(i1k).Y;
                line(m0 == 0 && Math.Abs(m1 - 12f) < 0.6f && m2 == -24f && Math.Abs(m3 + 6f) < 0.6f && p.Bands.All(b => b.Gain == 0 || b == p.Bands[q.Order()[3]]),
                    "pre-gain fader: the press leaves it at " + m0 + ", a quarter of the travel up " + m1 + " dB (12), far below " + m2 + " (-24), an eighth under where it was taken " + m3 + " (-6); the bands keep their gains");
                line(k1 > k0 + 5, "the points ride on the line: the 1 kHz point went down " + (k1 - k0).ToString("0") + " px with the pre-gain at " + p.Preamp + " dB");
                var kp = q.Knob(i1k); down((int)kp.X, (int)kp.Y); move((int)kp.X, (int)kp.Y - 1); up((int)kp.X, (int)kp.Y - 1);
                line(Math.Abs(p.Bands[i1k].Gain) < 0.35f, "a point taken where it is drawn keeps its gain: " + p.Bands[i1k].Gain + " dB with the pre-gain at " + p.Preamp);
                hover(xm, mid); SendMessage(q.Handle, 0x20A, (IntPtr)(120 << 16), at(0, 0)); float m4 = p.Preamp;
                wheelPress(xm, mid);
                line(Math.Abs(m4 - m3 - 0.5f) < 0.01f && p.Preamp == 0, "pre-gain: the wheel " + m3 + " -> " + m4 + " dB, a press of the wheel -> " + p.Preamp);
                p.Bands[i1k].Gain = 0;

                // 21.11: a point dragged on the plot past its neighbours keeps its strip; a click right of the last strip adds nothing
                var k4 = q.Knob(i1k); down((int)k4.X, (int)k4.Y); move((int)k4.X + Theme.S(160), (int)k4.Y); int[] held = (int[])q.Order().Clone(); up((int)k4.X + Theme.S(160), (int)k4.Y);
                float fMoved = p.Bands[i1k].Freq; int[] left = q.Order();
                down(q.StripX(11) + Theme.S(27), mid); up(q.StripX(11) + Theme.S(27), mid);
                line(fMoved > 4000 && held[i1k] == i1k && left[i1k] == i1k && p.Bands.Count == 12, "a point dragged past 4 kHz (now " + fMoved + " Hz) keeps strip " + left[i1k] + "; past the last strip nothing is added (" + p.Bands.Count + " bands)");
                // ...and after the settings are saved and read again (a restart), it is still on the same strip
                var again = SoundParams.Parse(p.ToString());
                line(again.Bands[i1k].Freq == fMoved && again.Bands.Select(x => x.Freq).SequenceEqual(p.Bands.Select(x => x.Freq)), "read back after a restart the points keep their strips: strip " + i1k + " still " + again.Bands[i1k].Freq + " Hz");
                p.Bands[i1k].Freq = 1000;
                // settings with fewer points get the rest: the old five, a preset of four
                var four = SoundParams.Parse("eq=1 b=1,100,3,1 b=1,500,0,1 b=1,2000,0,1 b=3,8000,2,0.7");
                line(four.Bands.Count == 12 && four.Bands.Count(b => b.Gain != 0) == 2 && four.Bands.Zip(four.Bands.Skip(1), (a, b) => a.Freq <= b.Freq).All(x => x),
                    "a preset of four points gets twelve, in order, its gains kept: " + string.Join(", ", four.Bands.Select(b => b.Freq + (b.Gain != 0 ? "(" + b.Gain + ")" : ""))));

                // 21.11: the slope dial: dragged up a long way 48 dB/oct on a cut, the wheel a step down, kept in the text
                p.Bands[i80].Type = 4; p.Bands[i80].Freq = 1000; p.Bands[i80].Q = 0.707f; q.Invalidate(); wait();
                int xs = q.StripX(i80); float fwas = p.Bands[i80].Gain; down(xs, q.SlopeRowY); move(xs, q.SlopeRowY - Theme.S(160)); int sTop = p.Bands[i80].Slope; up(xs, q.SlopeRowY - Theme.S(160));
                double r500 = AudioFx.Response(p, 500, 48000, i80), r1k = AudioFx.Response(p, 1000, 48000, i80);
                hover(xs, q.SlopeRowY); SendMessage(q.Handle, 0x20A, (IntPtr)(-120 << 16), at(0, 0)); int sWheel = p.Bands[i80].Slope;
                var back = SoundParams.Parse(p.ToString()); var cut = back.Bands.FirstOrDefault(b => b.Type == 4);
                line(sTop == 4 && Math.Abs(r500 + 48.2) < 0.3 && Math.Abs(r1k + 3.01) < 0.05 && sWheel == 3 && cut != null && cut.Slope == 3,
                    "cut slope: the slope dial dragged up gives " + sTop + " (48 dB/oct: " + r500.ToString("0.0") + " dB an octave under, " + r1k.ToString("0.00") + " at 1 kHz); the wheel a notch down " + sWheel + "; read back " + (cut != null ? cut.Slope.ToString() : "?"));
                // 21.11 (the user: the slope dial lagged): drawing the curve of twelve straight bells, with and without the
                // sections kept per band
                {
                    var twelve = new SoundParams(); twelve.Bands = SoundParams.EvenBands(); foreach (var b in twelve.Bands) { b.Gain = 6; b.Slope = 4; }
                    Func<double> paint = () => { var sw = System.Diagnostics.Stopwatch.StartNew(); for (int k = 0; k < 3; k++) for (int i = 0; i <= 400; i++) AudioFx.Response(twelve, 20 * Math.Pow(1000, i / 400.0), 48000); return sw.Elapsed.TotalMilliseconds / 3; };
                    AudioFx.PlanCache = false; double slow = paint(); AudioFx.PlanCache = true; paint(); double fast = paint();
                    line(fast < 15 && fast < slow / 3, "the curve of twelve straight bells: " + fast.ToString("0.0") + " ms a picture with the sections kept, " + slow.ToString("0.0") + " ms without");
                }
                // 21.11: what one picture of the equalizer costs (it is drawn 30 times a second while «Звук» is open, with the spectrum)
                {
                    var spec = new double[1025]; for (int i = 0; i < spec.Length; i++) spec[i] = -40 - 30.0 * i / spec.Length;
                    q.Feed(spec);
                    using (var bmp = new Bitmap(q.Width, q.Height))
                    {
                        q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size));
                        var sw = System.Diagnostics.Stopwatch.StartNew(); for (int k = 0; k < 20; k++) q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size));
                        double ms = sw.Elapsed.TotalMilliseconds / 20;
                        line(ms < 12, "one picture of the equalizer with the spectrum: " + ms.ToString("0.0") + " ms (30 a second: " + (ms * 3).ToString("0") + " % of a core)");
                    }
                    q.Feed(null);
                }
                // a bell made straight keeps its gain at its frequency
                var bell = new EqBand { Type = 1, Freq = 1000, Gain = 9, Q = 1, Slope = 3 }; var sp1 = new SoundParams(); sp1.Bands.Add(bell);
                double atF = AudioFx.Response(sp1, 1000, 48000), atEdge = AudioFx.Response(sp1, 1000 * Math.Pow(2, 0.4), 48000);
                line(Math.Abs(atF - 9) < 0.6 && atEdge > 8, "a straight bell (slope 3): " + atF.ToString("0.0") + " dB at its frequency, " + atEdge.ToString("0.0") + " dB 0.4 octave out");
                p.Bands[i80].Type = 2; p.Bands[i80].Freq = 80; p.Bands[i80].Q = 0.7f; p.Bands[i80].Slope = 1;

                // switched off: nothing moves
                q.Dim = true; float was = p.Bands[i1k].Gain; down(q.StripX(i1k), top + 10); move(q.StripX(i1k), top + 20); up(q.StripX(i1k), top + 20);
                line(p.Bands[i1k].Gain == was, "equalizer switched off: a fader does not move");
                sb.AppendLine("changes reported: " + changes);
                using (var bmp = new Bitmap(q.Width, q.Height)) { q.Dim = false; p.Preamp = -6; p.Bands[i250].Gain = 6; q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size)); bmp.Save(System.IO.Path.ChangeExtension(file, ".png"), System.Drawing.Imaging.ImageFormat.Png); }
            }
            System.IO.File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        // --comp-graph-check <file>: the compressor's picture (the points on the curve, the threshold line, the dials),
        // driven by the mouse messages Windows sends, on a window of its own. Changes nothing of the settings.
        public static void CompCheck(string file)
        {
            Theme.WarpPointer = false; // the real pointer stays where it is
            var sb = new System.Text.StringBuilder();
            Func<int, int, IntPtr> at = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
            Action wait = () => { var until = DateTime.Now.AddMilliseconds(120); while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } };
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(Theme.S(1100), Theme.S(420)), BackColor = Theme.Window })
            {
                var q = new CompGraph { Location = new Point(0, 0), Width = Theme.S(1100) };
                int changes = 0; q.Changed += (o, e) => changes++;
                f.Controls.Add(q); f.Show(); wait();
                var p = q.P; p.Thr = -18; p.Ratio = 3; p.Knee = 0; p.Makeup = 0; p.Attack = 10; p.Release = 150;
                Action<int, int> down = (x, y) => SendMessage(q.Handle, 0x201, (IntPtr)1, at(x, y));
                Action<int, int> move = (x, y) => SendMessage(q.Handle, 0x200, (IntPtr)1, at(x, y));
                Action<int, int> up = (x, y) => SendMessage(q.Handle, 0x202, IntPtr.Zero, at(x, y));
                Action<int, int> wheelPress = (x, y) => { SendMessage(q.Handle, 0x207, (IntPtr)0x10, at(x, y)); SendMessage(q.Handle, 0x208, IntPtr.Zero, at(x, y)); }; // 20.11: the reset
                Func<CompGraph.Part, Point> knob = part => Point.Round(q.KnobAt(part));
                double perDb = q.Curve.Height / 60.0;
                // 21.11 (the user): a dial the character does not listen to is grey and not taken (the tube: the knee; the optical: the release)
                {
                    float kneeWas = p.Knee, relWas = p.Release;
                    p.CompChar = 3; q.Invalidate(); wait();
                    var kd = Point.Round(q.DialAt(2)); down(kd.X, kd.Y); move(kd.X, kd.Y - 80); up(kd.X, kd.Y - 80);
                    SendMessage(q.Handle, 0x200, IntPtr.Zero, at(kd.X, kd.Y)); SendMessage(q.Handle, 0x20A, (IntPtr)(120 << 16), at(0, 0)); float kneeTube = p.Knee;
                    p.CompChar = 2; q.Invalidate(); wait();
                    var rd = Point.Round(q.DialAt(1)); down(rd.X, rd.Y); move(rd.X, rd.Y - 80); up(rd.X, rd.Y - 80); float relOpto = p.Release;
                    p.CompChar = 0; q.Invalidate(); wait();
                    down(kd.X, kd.Y); move(kd.X, kd.Y - 40); up(kd.X, kd.Y - 40); float kneeClean = p.Knee;
                    line(kneeTube == kneeWas && relOpto == relWas && kneeClean != kneeWas, "dials by the character: the tube's knee stays " + kneeTube + " (dragged, wheeled), the optical's release stays " + relOpto + "; the clean one's knee moves (" + kneeWas + " -> " + kneeClean + ")");
                    p.Knee = kneeWas; p.CompChar = 0; q.Invalidate(); wait();
                }
                // 21.11: what one picture of the compressor costs, with its meters moving (30 a second while «Звук» is open)
                {
                    for (int kc = 0; kc < 200; kc++) q.Feed(true, 0.3f + 0.2f * (kc % 7) / 7f, 0.25f, -4f - (kc % 5), -1f, -20f + (kc % 9));
                    using (var bmp = new Bitmap(q.Width, q.Height))
                    {
                        q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size));
                        var sw = System.Diagnostics.Stopwatch.StartNew(); for (int kc = 0; kc < 20; kc++) { q.Feed(true, 0.4f, 0.3f, -5f, -1f, -18f); q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size)); }
                        double ms = sw.Elapsed.TotalMilliseconds / 20;
                        line(ms < 12, "one picture of the compressor with its meters: " + ms.ToString("0.0") + " ms (30 a second: " + (ms * 3).ToString("0") + " % of a core)");
                    }
                }

                line(q.History.Width > 0 && q.Panel.Width > 0, "at this width the curve, the last seconds (" + q.History.Width + " px) and the dials (" + q.Panel.Width + " px) all show");
                line(Math.Abs(q.Gr(0) + 12) < 0.001 && q.Gr(-30) == 0, "the curve's formula: threshold -18, 3:1, at 0 dB in it takes " + q.Gr(0).ToString("0.##") + " dB (-12 expected), under the threshold nothing");

                // the threshold point: 12 dB to the left and well down — only the threshold moves
                var k = knob(CompGraph.Part.Thr);
                down(k.X + 3, k.Y + 2); float t0 = p.Thr; move(k.X + 3 - (int)(12 * perDb), k.Y + 60); float t1 = p.Thr; up(0, 0);
                line(t0 == -18 && Math.Abs(t1 + 30) <= 1 && p.Makeup == 0 && p.Ratio == 3, "threshold point: taken without a jump (" + t0 + "), 12 dB to the left gives " + t1 + " dB; the gain after and the ratio stay (" + p.Makeup + ", " + p.Ratio + ")");

                // the ratio point (the right end): the output at 0 dB in put to -27 -> 30 / 3 = 10:1; far down 20:1, far up 1:1
                p.Thr = -30; k = knob(CompGraph.Part.Ratio);
                down(k.X, k.Y); move(k.X, (int)q.Y(-27)); float r1 = p.Ratio; move(k.X, q.Curve.Bottom + 50); float r2 = p.Ratio; move(k.X, q.Curve.Y - 50); float r3 = p.Ratio; up(0, 0);
                line(Math.Abs(r1 - 10) < 1.5 && r2 == 20 && r3 == 1, "ratio point: the end of the curve at -27 dB gives " + r1 + " : 1 (10 expected), below the plot " + r2 + " : 1, above it " + r3 + " : 1");

                // the gain-after point: 12 dB up
                p.Ratio = 3; k = knob(CompGraph.Part.Makeup);
                down(k.X, k.Y); move(k.X + 40, k.Y - (int)Math.Round(12 * perDb)); float m1 = p.Makeup; up(0, 0);
                line(Math.Abs(m1 - 12) <= 1 && p.Thr == -30, "gain-after point: 12 dB up gives " + m1 + " dB; the threshold stays (" + p.Thr + ")");
                p.Makeup = 0;

                // the dashed threshold line over the last seconds
                int hx = q.History.X + q.History.Width / 2;
                down(hx, (int)q.Y(p.Thr)); move(hx, (int)q.Y(-24)); float t2 = p.Thr; up(0, 0);
                line(Math.Abs(t2 + 24) <= 1, "threshold line: dragged to -24 dB gives " + t2 + " dB");

                // the dials: half the travel up is half the range (attack 10 ms x 200^0.5 = 141), the knee by 12 dB
                // (19.11: they stand one under another between the curve and the last seconds)
                var da = q.DialAt(0); var dr = q.DialAt(1); var dk = q.DialAt(2); int xa = da.X, dy = da.Y;
                line(q.Panel.X > q.Curve.Right && q.History.X > q.Panel.Right && da.Y < dr.Y && dr.Y < dk.Y && da.X == dk.X, "the order across: the curve, the dials one under another, the last seconds");
                down(xa, dy); move(xa, dy - Theme.S(110)); float a1 = p.Attack; up(0, 0);
                down(dr.X, dr.Y); move(dr.X, dr.Y + Theme.S(500)); float rl = p.Release; up(0, 0);
                down(dk.X, dk.Y); move(dk.X, dk.Y - Theme.S(110)); float kn = p.Knee; up(0, 0);
                line(a1 > 130 && a1 < 150 && rl == 10 && kn == 12, "dials: attack 10 -> " + a1 + " ms (141 expected), release far down " + rl + " ms (10, the least), knee 0 -> " + kn + " dB (12)");

                // 20.11: a double click on a dial changes nothing any more; a press of the wheel: the value a new device starts with
                float aWas = p.Attack; down(xa, dy); up(xa, dy); SendMessage(q.Handle, 0x203, (IntPtr)1, at(xa, dy)); up(xa, dy); float aDbl = p.Attack;
                wheelPress(xa, dy);
                line(aDbl == aWas && p.Attack == SoundParams.Start().Attack, "double click on the attack dial keeps " + aDbl + " ms; a press of the wheel: " + p.Attack + " ms");

                // 20.11: the wheel on a dial: one notch = one step of its scale; a smooth wheel's half notches make one step together
                Action<int, int, int> wheel = (x, y, delta) => { SendMessage(q.Handle, 0x200, IntPtr.Zero, at(x, y)); SendMessage(q.Handle, 0x20A, (IntPtr)(delta << 16), at(0, 0)); };
                p.Attack = 20; p.Release = 150; p.Ratio = 1.5f;
                wheel(xa, dy, 120); float wa = p.Attack; wheel(dr.X, dr.Y, 120); float wr = p.Release;
                var dWr = q.DialAt(4); float wq0 = p.Ratio; if (q.HasSixDials) wheel(dWr.X, dWr.Y, 120); float wq = p.Ratio;
                wheel(xa, dy, 60); float wh1 = p.Attack; wheel(xa, dy, 60); float wh2 = p.Attack; wheel(xa, dy, -120); float wd = p.Attack;
                line(wa == 21 && wr == 160 && (!q.HasSixDials || Math.Abs(wq - 1.6f) < 0.001f) && wh1 == 21 && wh2 == 22 && wd == 21,
                    "wheel on the dials: attack 20 -> " + wa + " ms, release 150 -> " + wr + " ms, ratio " + wq0 + " -> " + wq + "; half a notch " + wh1 + ", the second half " + wh2 + ", a notch down " + wd);
                p.Attack = 10; p.Release = 150; p.Ratio = 3;

                // switched off: nothing moves
                q.Dim = true; k = knob(CompGraph.Part.Thr); float was = p.Thr; down(k.X, k.Y); move(k.X - 80, k.Y); up(0, 0);
                line(p.Thr == was, "compressor switched off: the threshold point does not move");

                // 19.11, the limiter in the same picture: its ceiling is a line that can be dragged, over the last seconds
                // and over the curve, also while the compressor is off; with the limiter off there is nothing to take
                p.Limit = true; p.Ceil = -1f;
                down(hx, (int)q.Y(-1)); move(hx, (int)q.Y(-6)); float c1 = p.Ceil; up(0, 0);
                int cx2 = q.Curve.X + q.Curve.Width / 3;
                down(cx2, (int)q.Y(p.Ceil)); move(cx2, q.Curve.Bottom + 40); float c2 = p.Ceil; up(0, 0);
                line(Math.Abs(c1 + 6) <= 0.5f && c2 == -12f, "limiter on, compressor off: the ceiling line dragged to -6 dB gives " + c1 + " dB; over the curve, far down, " + c2 + " dB (-12, the least)");
                p.Limit = false; p.Ceil = -3f; down(hx, (int)q.Y(-3)); move(hx, (int)q.Y(-9)); up(0, 0);
                line(p.Ceil == -3f, "limiter off: no ceiling line to take (" + p.Ceil + " dB stays)");
                // 20.11: the ceiling's fader right of the monitor: the knob comes to the press, follows the mouse by half a dB,
                // the wheel, a double click; still while the limiter is off
                var cst = q.CeilStrip; int fx = cst.X + cst.Width / 2;
                down(fx, q.CeilTop + 4); move(fx, q.CeilTop - 4); up(0, 0); float f0 = p.Ceil;
                p.Limit = true;
                int fm = (q.CeilTop + q.CeilBottom) / 2; down(fx, fm); float f1 = p.Ceil; move(fx, fm + (q.CeilBottom - q.CeilTop) / 4); float f3 = p.Ceil; move(fx, q.CeilBottom + 400); float f2 = p.Ceil; up(0, 0);
                SendMessage(q.Handle, 0x200, IntPtr.Zero, at(fx, (int)q.CeilY(p.Ceil))); SendMessage(q.Handle, 0x20A, (IntPtr)(120 << 16), at(0, 0)); float f4 = p.Ceil;
                { int fy = (int)q.CeilY(p.Ceil); wheelPress(fx, fy); } float f5 = p.Ceil;
                line(cst.Width > 0 && cst.X > q.History.Right && f0 == -3f && f1 == -3f && f2 == -12f && f3 == -6f && f4 == -11.5f && f5 == SoundParams.Start().Ceil,
                    "ceiling fader right of the monitor: limiter off " + f0 + " stays; on: the press leaves it at " + f1 + " dB, a quarter of the travel down " + f3 + " (-6), far below " + f2 + " (-12), wheel up " + f4 + " (-11.5), a press of the wheel " + f5);
                p.Limit = false;
                // 19.11, the bands: with three at work the picture shows the one the page picked, and only that band's settings move
                q.Dim = false; p.Limit = false; p.Split = 250; p.Split2 = 4000; p.Parts[0].Thr = -20; p.Parts[1].Thr = -30; p.Parts[2].Thr = -40; q.Band = 2;
                k = knob(CompGraph.Part.Thr); down(k.X, k.Y); move(k.X + (int)(10 * perDb), k.Y); up(0, 0);
                line(Math.Abs(p.Parts[2].Thr + 30) <= 1 && p.Parts[0].Thr == -20 && p.Parts[1].Thr == -30, "three bands, the top shown: its threshold point moved 10 dB right gives " + p.Parts[2].Thr + " dB; the bass and the middle stay (" + p.Parts[0].Thr + ", " + p.Parts[1].Thr + ")");
                q.Band = 1; float k0 = p.Parts[0].Knee, k1 = p.Parts[1].Knee, k2 = p.Parts[2].Knee; var dq = q.DialAt(2); down(dq.X, dq.Y); move(dq.X, dq.Y - Theme.S(55)); up(0, 0);
                line(p.Parts[1].Knee == k1 + 6 && p.Parts[0].Knee == k0 && p.Parts[2].Knee == k2, "the middle shown: its knee dial turns only the middle (" + p.Parts[0].Knee + " / " + p.Parts[1].Knee + " / " + p.Parts[2].Knee + " dB)");
                // 19.11, the second monitor: the bands across the frequencies. A click on a column takes that band, the splits
                // are dragged left and right within the sliders' limits, a column's threshold line is dragged up and down
                q.SetView(true); q.Dim = false; p.Limit = false; p.Split = 250; p.Split2 = 4000; q.Band = 0; int picked = 0; q.BandPicked += (o, e) => picked++;
                var hr = q.History; var v0 = q.ViewRect(false); var v1 = q.ViewRect(true);
                line(v0.Width > 0 && v0.Right < v1.X && v1.Right <= hr.Right && v0.Y >= q.Curve.Bottom, "the two words that switch the monitor stand under it at the right (" + v0.X + ".." + v0.Right + ", " + v1.X + ".." + v1.Right + ")");
                var zs = q.Zones();
                line(zs.Length == 3 && zs[0].Part == 0 && zs[1].Part == 1 && zs[2].Part == 2 && q.FX(250) > hr.X && q.FX(250) < q.FX(4000) && q.FX(4000) < hr.Right, "three bands: three columns, the splits at " + (int)q.FX(250) + " and " + (int)q.FX(4000) + " px of " + hr.X + ".." + hr.Right);
                int zx = (int)((q.FX(250) + q.FX(4000)) / 2), zy = hr.Y + hr.Height / 4;
                down(zx, zy); up(zx, zy);
                line(q.Band == 1 && picked == 1, "a click on the middle column takes the middle (band " + q.Band + ", reported " + picked + ")");
                var s1 = Point.Round(q.SplitAt(0)); down(s1.X, s1.Y); move((int)q.FX(500), s1.Y); float sp1 = p.Split; move(hr.Right, s1.Y); float sp1x = p.Split; up(0, 0);
                line(Math.Abs(sp1 - 500) <= 20 && sp1x == 600 && q.Band == 1, "the lower split dragged to 500 Hz gives " + sp1 + " Hz, to the right edge " + sp1x + " Hz (600, the most); the band shown stays");
                var s2 = Point.Round(q.SplitAt(1)); down(s2.X, s2.Y); move((int)q.FX(8000), s2.Y); float sp2 = p.Split2; move(hr.X, s2.Y); float sp2n = p.Split2; up(0, 0);
                line(Math.Abs(sp2 - 8000) <= 300 && sp2n == 1200, "the upper split dragged to 8 kHz gives " + sp2 + " Hz, to the left edge " + sp2n + " Hz (1200, the least)");
                p.Split = 250; p.Split2 = 4000; p.Parts[1].Thr = -30; p.Parts[2].Thr = -40; int tx = (int)q.FX(9000);
                down(tx, (int)q.Y(-40)); move(tx, (int)q.Y(-12)); up(0, 0);
                line(q.Band == 2 && Math.Abs(p.Parts[2].Thr + 12) <= 1 && p.Parts[1].Thr == -30, "the top column's threshold line dragged to -12 dB: the top is taken and gets " + p.Parts[2].Thr + " dB; the middle stays (" + p.Parts[1].Thr + ")");
                down(v0.X + v0.Width / 2, v0.Y + v0.Height / 2); up(0, 0); bool toTime = !q.ByFreq;
                down(v1.X + v1.Width / 2, v1.Y + v1.Height / 2); up(0, 0);
                line(toTime && q.ByFreq, "a click on each of the two words switches the monitor");
                p.Split2 = 0; q.Band = 0; down((int)q.FX(1000), zy); up(0, 0);
                line(q.Zones().Length == 2 && q.Band == 2, "two bands: two columns, a click above the split takes the top (band " + q.Band + ")");
                p.Split = 0; q.Band = 0; picked = 0; down((int)q.FX(1000), zy); up(0, 0);
                line(q.Zones().Length == 1 && q.Band == 0 && picked == 0, "one band: one column, nothing to take");
                p.Split = 250; p.Split2 = 4000; q.Dim = true; q.Band = 0; down(zx, zy); up(zx, zy); var sd = Point.Round(q.SplitAt(0)); down(sd.X, sd.Y); move(sd.X + 60, sd.Y); up(0, 0);
                line(q.Band == 0 && p.Split == 250, "compressor switched off: no column is taken, no split moves");
                // 19.11, listening to the bands: S and M under the name of each column of the monitor by frequencies
                q.Dim = false; q.SetView(true); p.Comp = true; p.Split = 250; p.Split2 = 4000; q.Band = 0; int heard = 0; q.ListenChanged += (o, e) => heard++;
                var sMid = q.ListenRect(1, false); var mMid = q.ListenRect(1, true); var mLow = q.ListenRect(0, true);
                line(sMid.Width > 0 && sMid.Right <= mMid.X && sMid.Y == mMid.Y && sMid.Y > hr.Y && sMid.X > q.FX(250) && mMid.Right < q.FX(4000) && mLow.Right < q.FX(250), "S and M stand side by side under the name of their column (the middle's: " + sMid.X + ".." + mMid.Right + " of " + (int)q.FX(250) + ".." + (int)q.FX(4000) + ")");
                Func<Rectangle, Point> ctr = r => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                var cs = ctr(sMid); down(cs.X, cs.Y); up(cs.X, cs.Y); int sil1 = q.Silent; int bandWas = q.Band;
                line(q.SoloMask == 2 && sil1 == 5 && heard == 1 && bandWas == 0, "S of the middle: the bass and the top are silent (" + sil1 + "), reported once; the band being set stays");
                var cm = ctr(mLow); down(cm.X, cm.Y); up(cm.X, cm.Y); int sil2 = q.Silent;
                down(cs.X, cs.Y); up(cs.X, cs.Y); int sil3 = q.Silent;
                line(sil2 == 5 && sil3 == 1 && q.MuteMask == 1 && q.SoloMask == 0, "M of the bass beside it changes nothing more (" + sil2 + "); S of the middle off: only the bass is silent (" + sil3 + ")");
                down(cm.X, cm.Y); up(cm.X, cm.Y); SendMessage(q.Handle, 0x203, (IntPtr)1, at(cm.X, cm.Y)); up(cm.X, cm.Y);
                line(q.MuteMask == 1 && heard == 5, "a quick pair of presses on M counts as two: off and on again (" + q.MuteMask + ", reported " + heard + " in all)");
                p.Comp = false; int silOff = q.Silent; p.Comp = true; p.Split2 = 0; q.MuteMask = 2; int silTwo = q.Silent; q.SoloMask = 4; int silTwoSolo = q.Silent;
                line(silOff == 0 && silTwo == 0 && silTwoSolo == 1, "compressor off: nothing is silent; two bands: the middle's bit means nothing (" + silTwo + "), S of the top silences the bass (" + silTwoSolo + ")");
                p.Split = 0; bool none = q.ListenRect(0, false).Width == 0 && q.Silent == 0; q.ClearListen(); p.Split = 250; p.Split2 = 4000; q.SetView(false); bool noneTime = q.ListenRect(1, false).Width == 0;
                line(none && noneTime && q.SoloMask == 0 && q.MuteMask == 0, "one band, or the monitor of the last seconds: no S and M");
                p.Comp = false; q.Dim = true;
                // 19.11, the bands' column between the dials and the monitor: the number of bands and the band being set
                q.Dim = false; q.SetView(false); p.Split = 250; p.Split2 = 4000; q.Ghost1 = 250; q.Ghost2 = 4000; q.Band = 1; picked = 0;
                q.Settle(); var bcol = q.BandsCol;
                line(q.HasBands && bcol.Right < q.Curve.X - Theme.S(40) && q.Panel.X > q.Curve.Right && q.History.X > q.Panel.Right && q.CountRect(2).Right <= bcol.Right && q.BandRect(2).Bottom <= bcol.Bottom && Math.Abs((q.CountRect(0).Y - Theme.S(26) - bcol.Y) - (bcol.Bottom - q.BandRect(2).Bottom)) <= 1, "the order across: the bands' column (" + bcol.X + ".." + bcol.Right + "), the curve (" + q.Curve.X + ".." + q.Curve.Right + "), the dials (" + q.Panel.X + ".." + q.Panel.Right + "), the monitor (" + q.History.X + ".." + q.History.Right + ")");
                Func<Rectangle, Point> mid = r => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                var cnt2 = mid(q.CountRect(1)); down(cnt2.X, cnt2.Y); up(cnt2.X, cnt2.Y);
                line(p.PartCount == 2 && p.Split == 250 && p.Split2 == 0 && q.Band == 0 && !q.ByFreq, "«2» in the column: two bands, the middle (which was shown) gives way to the bass, the monitor stays over time (20.11)");
                int afterMid = q.Band;
                var pt = mid(q.BandRect(2)); down(pt.X, pt.Y); up(pt.X, pt.Y);
                line(afterMid == 0 && q.Band == 2 && picked == 1 && q.BandRect(1).Height == 0 && Math.Abs(q.BandRect(2).Y - q.BandRect(0).Y - Theme.S(40)) <= 1, "two bands: two plates, one under another (the middle has none); the top's plate takes the top (reported " + picked + ")");
                int yIn = q.CountRect(0).Y; SendMessage(q.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero); q.Settle(); // the mouse leaves the column
                line(yIn == mid(q.CountRect(1)).Y - Theme.S(15) - (q.CountRect(0).Y - yIn) && q.CountRect(0).Y - yIn == Theme.S(20), "while the mouse is over the column the numbers stay where they were clicked; when it has left, the block of two plates moves to the middle (" + (q.CountRect(0).Y - yIn) + " down)");
                line(Math.Abs((q.CountRect(0).Y - Theme.S(26) - bcol.Y) - (bcol.Bottom - q.BandRect(2).Bottom)) <= 1, "two plates: the block stands in the middle of the plot's height (top " + (q.CountRect(0).Y - Theme.S(26) - bcol.Y) + ", bottom " + (bcol.Bottom - q.BandRect(2).Bottom) + ")");
                var cnt3 = mid(q.CountRect(2)); down(cnt3.X, cnt3.Y); up(cnt3.X, cnt3.Y); int n3 = p.PartCount; float s23 = p.Split2;
                var cnt1 = mid(q.CountRect(0)); down(cnt1.X, cnt1.Y); up(cnt1.X, cnt1.Y);
                line(n3 == 3 && s23 == 4000 && p.PartCount == 1 && p.Split == 0 && q.Band == 0, "«3»: three bands with the upper split back at " + s23 + " Hz; «1»: one band, the whole sound");
                // 20.11: by frequencies the monitor stays so too, whatever is picked in the column
                bool timeKept = !q.ByFreq; q.SetView(true);
                down(cnt3.X, cnt3.Y); up(cnt3.X, cnt3.Y); var pMid = mid(q.BandRect(1)); down(pMid.X, pMid.Y); up(pMid.X, pMid.Y);
                bool freqKept = q.ByFreq; int bandMid = q.Band; q.SetView(false); down(cnt1.X, cnt1.Y); up(cnt1.X, cnt1.Y);
                line(timeKept && freqKept && bandMid == 1 && p.PartCount == 1, "picking in the column leaves the monitor as it was: over time after the picks above (" + timeKept + "), by frequencies after «3» and the middle (" + freqKept + ", band " + bandMid + ")");
                SendMessage(q.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero); q.Settle();
                line(q.BandRect(1).Height == 0 && q.BandRect(2).Height == 0 && Math.Abs((q.CountRect(0).Y - Theme.S(26) - bcol.Y) - (bcol.Bottom - q.BandRect(0).Bottom)) <= 1, "one band: one plate, the block again in the middle (top " + (q.CountRect(0).Y - Theme.S(26) - bcol.Y) + ", bottom " + (bcol.Bottom - q.BandRect(0).Bottom) + ")");
                q.Dim = true; cnt3 = mid(q.CountRect(2)); down(cnt3.X, cnt3.Y); up(cnt3.X, cnt3.Y); line(p.PartCount == 1, "compressor switched off: the numbers do not work"); q.Dim = false;
                int wide = q.Width; q.Width = Theme.S(600); bool gone = !q.HasBands && q.BandsCol.Width == 0 && q.Panel.Width > 0; q.Width = wide;
                line(gone && q.HasBands, "in a narrow picture (600) the column is gone, the curve is back at the left edge (" + q.Curve.X + " in the wide one) and the dials stay; the page then shows its two rows");
                // the plates come and go with a movement: drawn (which starts it), the middle's plate is still whole, a moment
                // later it is partly there, half a second later it is gone; and back the same way
                Action paint = () => { using (var bmp = new Bitmap(q.Width, q.Height)) q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size)); };
                Action<int> pump = ms => { var until = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } };
                cnt3 = mid(q.CountRect(2)); down(cnt3.X, cnt3.Y); up(cnt3.X, cnt3.Y); paint(); pump(700); paint(); float pl0 = q.PlateShown(1);
                cnt2 = mid(q.CountRect(1)); down(cnt2.X, cnt2.Y); up(cnt2.X, cnt2.Y); paint(); float pl1 = q.PlateShown(1); pump(70); float pl2 = q.PlateShown(1); pump(700); float pl3 = q.PlateShown(1);
                cnt3 = mid(q.CountRect(2)); down(cnt3.X, cnt3.Y); up(cnt3.X, cnt3.Y); paint(); pump(70); float pl4 = q.PlateShown(1); pump(700); float pl5 = q.PlateShown(1);
                line(pl0 == 1 && pl1 == 1 && pl2 > 0.05f && pl2 < 0.95f && pl3 == 0 && pl4 > 0.05f && pl4 < 0.95f && pl5 == 1, "the middle's plate goes and comes with a movement: 3 -> 2 bands " + pl1.ToString("0.00") + " -> " + pl2.ToString("0.00") + " (70 ms) -> " + pl3.ToString("0.00") + "; 2 -> 3 bands " + pl4.ToString("0.00") + " (70 ms) -> " + pl5.ToString("0.00"));
                cnt1 = mid(q.CountRect(0)); down(cnt1.X, cnt1.Y); up(cnt1.X, cnt1.Y); SendMessage(q.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero); q.Settle();
                cnt3 = mid(q.CountRect(2)); down(cnt3.X, cnt3.Y); up(cnt3.X, cnt3.Y);
                int yHeld = q.CountRect(0).Y; SendMessage(q.Handle, 0x2A3, IntPtr.Zero, IntPtr.Zero); paint(); pump(60); int yMid = q.CountRect(0).Y; pump(700); int yFree = q.CountRect(0).Y;
                line(Math.Abs(yHeld - yFree - Theme.S(40)) <= 1 && yMid < yHeld && yMid > yFree, "one plate became three under the mouse: the block held its place (" + yHeld + "), and with the mouse gone it glides to the middle (" + yMid + " after 60 ms, then " + yFree + ")");
                cnt1 = mid(q.CountRect(0)); down(cnt1.X, cnt1.Y); up(cnt1.X, cnt1.Y); paint(); pump(700);
                // 19.11, six dials: the threshold, the ratio and the gain after in a column left of the attack, the release and the knee
                var gThr = q.DialAt(3); var gRat = q.DialAt(4); var gMk = q.DialAt(5); var gAtt = q.DialAt(0);
                line(q.HasSixDials && gThr.X > q.Curve.Right && gThr.X < gAtt.X && gAtt.X < q.History.X && gThr.Y == gAtt.Y && gThr.Y < gRat.Y && gRat.Y < gMk.Y, "six dials: the column of the threshold, the ratio and the gain after (x " + gThr.X + ") stands between the curve and the column of the attack (x " + gAtt.X + ")");
                p.Thr = -40; p.Ratio = 2; p.Makeup = 0;
                down(gThr.X, gThr.Y); move(gThr.X, gThr.Y - Theme.S(110)); float dThr = p.Thr; up(0, 0);
                down(gRat.X, gRat.Y); move(gRat.X, gRat.Y - Theme.S(110)); float dRat = p.Ratio; move(gRat.X, gRat.Y + Theme.S(600)); float dRatLow = p.Ratio; up(0, 0);
                down(gMk.X, gMk.Y); move(gMk.X, gMk.Y - Theme.S(110)); float dMk = p.Makeup; up(0, 0);
                line(dThr == -10 && Math.Abs(dRat - 8.9f) < 0.15f && dRatLow == 1 && dMk == 18, "half the travel up: threshold -40 -> " + dThr + " dB (-10), ratio 2 -> " + dRat + " : 1 (8.9), far down " + dRatLow + " : 1 (1, the least), gain after 0 -> " + dMk + " dB (18)");
                down(gMk.X, gMk.Y); up(gMk.X, gMk.Y); SendMessage(q.Handle, 0x203, (IntPtr)1, at(gMk.X, gMk.Y)); up(gMk.X, gMk.Y); float mkDbl = p.Makeup;
                wheelPress(gMk.X, gMk.Y);
                line(mkDbl == 18 && p.Makeup == 0, "double click on the gain-after dial keeps " + mkDbl + " dB (18); a press of the wheel: " + p.Makeup + " dB");
                // 19.11, two more dials under the six: the splits between the bands, alive only where their split is at work
                p.Split = 100; p.Split2 = 2000; q.Ghost1 = 250; q.Ghost2 = 4000; q.SetView(false); q.Band = 0;
                var gS1 = q.DialAt(6); var gS2 = q.DialAt(7);
                line(gS1.X == gThr.X && gS2.X == gAtt.X && gS1.Y == gS2.Y && gS1.Y > gMk.Y && q.DialCell(6).Bottom <= q.Panel.Bottom + 2, "the two dials of the splits stand in a fourth row, under the columns of the six (y " + gS1.Y + ", the gain after at " + gMk.Y + ")");
                down(gS1.X, gS1.Y); move(gS1.X, gS1.Y - Theme.S(110)); float ds1 = p.Split; bool turned = q.ByFreq; move(gS1.X, gS1.Y + Theme.S(600)); float ds1low = p.Split; up(0, 0);
                down(gS2.X, gS2.Y); move(gS2.X, gS2.Y - Theme.S(110)); float ds2 = p.Split2; move(gS2.X, gS2.Y - Theme.S(600)); float ds2top = p.Split2; up(0, 0);
                line(ds1 == 270 && ds1low == 80 && ds2 == 6300 && ds2top == 12000 && turned, "half the travel up: the lower split 100 -> " + ds1 + " Hz (270), far down " + ds1low + " (80, the least); the upper 2000 -> " + ds2 + " Hz (6300), far up " + ds2top + " (12000, the most); the monitor turned to the bands");
                p.Split = 250; p.Split2 = 0; down(gS2.X, gS2.Y); move(gS2.X, gS2.Y - Theme.S(110)); up(0, 0); float off2 = p.Split2;
                down(gS1.X, gS1.Y); move(gS1.X, gS1.Y - Theme.S(55)); up(0, 0); float on1 = p.Split;
                p.Split = 0; down(gS1.X, gS1.Y); move(gS1.X, gS1.Y - Theme.S(110)); up(0, 0);
                line(off2 == 0 && on1 > 250 && p.Split == 0 && p.Split2 == 0, "two bands: the upper split's dial is still, the lower one works (" + on1 + " Hz); one band: both are still");
                p.Split = 0; p.Split2 = 0; q.SetView(false);
                int wide6 = q.Width; q.Width = Theme.S(700); bool one = q.HasBands && !q.HasSixDials && q.Panel.Width > 0 && q.DialCell(3).Width == 0; q.Width = wide6;
                line(one && q.HasSixDials, "in a picture of 700 the bands' column stays and the dials are the three of before; the page then shows the three rows");
                p.Split = 250; p.Split2 = 4000; q.SetView(false);
                // 20.11: the compression over time is kept per band: a band picked shows its own last seconds at once
                using (var hq = new CompGraph { Width = Theme.S(1100) })
                {
                    hq.P.Split = 250; hq.P.Split2 = 4000; hq.Band = 0;
                    for (int kk = 0; kk < 20; kk++) { hq.FeedBands(new[] { -6f, -2f, -0.5f }, new[] { -10f, -20f, -30f }); hq.Feed(true, 0.5f, 0.3f, -6f, 0f, -10f); }
                    float g0 = hq.GrAgo(0, 5), g1 = hq.GrAgo(1, 5), g2 = hq.GrAgo(2, 5);
                    hq.FeedBands(null, null); hq.Feed(true, 0.5f, 0.5f, 0f, 0f, -10f); float g1now = hq.GrAgo(1, 0), g1before = hq.GrAgo(1, 1);
                    line(g0 == -6f && g1 == -2f && g2 == -0.5f && g1now == 0 && g1before == -2f,
                        "compression over time per band, while the bass was shown: bass " + g0 + ", middle " + g1 + ", top " + g2 + " dB kept apart; compressor off: " + g1now + " now, the middle's " + g1before + " before stays");
                }
                sb.AppendLine("changes reported: " + changes);
                q.Dim = false; p.Limit = true; p.Ceil = -6f; p.Thr = -30; p.Ratio = 2; p.Makeup = 9; p.Parts[1].Ratio = 4; p.Parts[2].Ratio = 1.5f; q.Band = 1; q.Demo();
                using (var bmp = new Bitmap(q.Width, q.Height)) { q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size)); bmp.Save(System.IO.Path.ChangeExtension(file, ".png"), System.Drawing.Imaging.ImageFormat.Png); }
                // the same by frequencies: three bands, then two, then one, in files beside
                q.SetView(true);
                foreach (int n in new[] { 3, 2, 1 })
                {
                    p.Split = n >= 2 ? 250 : 0; p.Split2 = n >= 3 ? 4000 : 0; q.Band = n == 3 ? 1 : n == 2 ? 2 : 0;
                    p.Comp = true; q.SoloMask = n == 3 ? 2 : 0; q.MuteMask = n == 2 ? 1 : 0;
                    // where the small buttons stand in the picture of three bands, for measuring the text in them (inkcentre.ps1)
                    if (n == 3)
                    {
                        Func<Rectangle, string> rt = r => "\"" + r.X + "," + r.Y + "," + r.Width + "," + r.Height + "\"";
                        sb.AppendLine("buttons: " + rt(q.CountRect(0)) + " " + rt(q.CountRect(1)) + " " + rt(q.CountRect(2)) + " " + rt(q.ListenRect(1, false)) + " " + rt(q.ListenRect(1, true)) + " " + rt(q.ListenRect(0, false)) + " " + rt(q.ListenRect(0, true)));
                    }
                    using (var bmp = new Bitmap(q.Width, q.Height)) { q.DrawToBitmap(bmp, new Rectangle(Point.Empty, q.Size)); bmp.Save(System.IO.Path.ChangeExtension(file, null) + "-freq" + n + ".png", System.Drawing.Imaging.ImageFormat.Png); }
                }
            }
            System.IO.File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        // --sound-undo-check <file>: Ctrl+Z / Ctrl+Y on «Звук» (SoundPage.UndoCheck), on a window of its own; reads the
        // devices, sends nothing to the effect and saves nothing
        public static void SoundUndoCheck(string file, Settings s)
        {
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(Theme.S(1200), Theme.S(700)), BackColor = Theme.Window })
            {
                var page = new SoundPage(s) { Dry = true }; f.Controls.Add(page); f.Show();
                var until = DateTime.Now.AddMilliseconds(400); while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                System.IO.File.WriteAllText(file, page.UndoCheck(), new System.Text.UTF8Encoding(true));
            }
        }

        public static void Render(string what, string file, Settings s)
        {
            int tall = 620; int.TryParse(Environment.GetEnvironmentVariable("RT_PNG_H") ?? "", out tall); if (tall < 200) tall = 620; // RT_PNG_H: a taller picture, for a page longer than the window
            int wide = 900; int.TryParse(Environment.GetEnvironmentVariable("RT_PNG_W") ?? "", out wide); if (wide < 300) wide = 900; // RT_PNG_W: another width, for how a page looks in a narrow window
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(Theme.S(wide), Theme.S(tall)), BackColor = Theme.Window })
            {
                Control page; Action late = null;
                CoreGrid swapGrid = null;
                if (what == "amdpower") page = new AmdPowerPage(s);
                else if (what == "gpu") page = new GpuPage(s);
                else if (what == "ram") page = new RedTune.Ram.RamPage(); // reads the timings and the BIOS settings, writes nothing
                else if (what == "home") page = new HomePage(new PowerService(), () => { }, s, () => { });
                else if (what == "settings") { Osd.Init(s); page = new SettingsPage(s, () => { }, v => { }); } // the language list and the rest, nothing is changed
                else if (what == "comp") page = new ComponentsPage(s);
                else if (what == "about") page = new AboutPage(s);
                else if (what == "startup") page = new StartupPage(); // 20.11: reads the list when it shows, changes nothing
                else if (what == "display")
                {
                    // 20.11: «Дисплей», reads the monitors when it shows, changes nothing; RT_TAB=<n>: that monitor's tab
                    var dp = new DisplayPage(s); page = dp; int dt;
                    if (int.TryParse(Environment.GetEnvironmentVariable("RT_TAB") ?? "", out dt)) late = () => { var w = DateTime.Now.AddMilliseconds(600); while (DateTime.Now < w) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } dp.ShowTab(dt); };
                }
                else if (what == "tabs")
                {
                    // 20.11: the tabs of «Питание» alone, «Кривые» picked and «Схемы питания» under the mouse (RT_HOT=<n>), to measure the gaps
                    var tp = new Page(); var tabs = tp.Add(new PageTabs(4, "Процессор", "Видеокарта", "Вентиляторы", "Схемы питания", "Кривые"), 0);
                    int hot; if (!int.TryParse(Environment.GetEnvironmentVariable("RT_HOT") ?? "3", out hot)) hot = 3;
                    typeof(PageTabs).GetField("hot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(tabs, hot);
                    page = tp;
                }
                else if (what == "dist")
                {
                    // 20.11: «Распределение» on an engine that is never started (no program is moved); RT_FAKE_L3=32,96 plays an X3D
                    // with the cache on CCD2; RT_FAKE_HINTS=1 adds three made-up hints: a game in front, a background job, a render
                    var eng = new Dist.Engine();
                    if (Environment.GetEnvironmentVariable("RT_FAKE_HINTS") == "1")
                    {
                        eng.FakeHint(@"D:\Games\Some Game\Game-Win64-Shipping.exe", 300, 280, 3.5);
                        eng.FakeHint(@"C:\Tools\Sync\syncworker.exe", 120, 0, 1.2);
                        eng.FakeHint(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", 600, 500, 24);
                    }
                    page = new Dist.DistPage(eng);
                }
                else if (what == "sound")
                {
                    // 18.11: «Звук» (reads the devices and their effects, changes nothing). RT_COMP_DEMO=1: a made-up sound in the compressor's picture
                    var sound = new SoundPage(s); page = sound;
                    if (Environment.GetEnvironmentVariable("RT_COMP_DEMO") == "1") late = () => sound.DemoComp();
                    if (Environment.GetEnvironmentVariable("RT_MIC") == "1") { var before = late; late = () => { if (before != null) before(); sound.ShowMic(); }; } // 21.11: the «Микрофон» tab
                    if (Environment.GetEnvironmentVariable("RT_C2") == "1") { var before2 = late; late = () => { if (before2 != null) before2(); sound.ShowComp2Demo(); }; } // the second compressor shown on
                }
                else if (what == "device") page = Hw.AsusFw ? (Page)new AsusDevicePage(s) : new DevicePage(s); // 18.11: «Устройство» (reads the laptop's settings when it shows, sets nothing)
                else if (what == "power")
                {
                    // 18.11: the power page and its tabs (RT_TAB=0..4); the fan control is made but never started, nothing is written
                    // RT_NO_TUNE_PLAN=1: as on a computer without the plan «Тюнинг»; RT_PRESET=<name>: that preset on the page
                    PowerPage.PretendNoTunePlan = Environment.GetEnvironmentVariable("RT_NO_TUNE_PLAN") == "1";
                    PowerPage.PretendTuneDrift = Environment.GetEnvironmentVariable("RT_TUNE_DRIFT") == "1"; // as if the plan «Тюнинг» had been changed outside
                    var ps = new PowerService(); var pp = new PowerPage(ps, new CurveService(s), new FanControl(ps, s), s);
                    var pre = Environment.GetEnvironmentVariable("RT_PRESET"); if (!string.IsNullOrEmpty(pre)) pp.ShowPreset(pre);
                    // RT_TAB_LATE=1: the tab is switched after the page has its size, as a click in the window does
                    int tb; bool lateTab = Environment.GetEnvironmentVariable("RT_TAB_LATE") == "1";
                    if (int.TryParse(Environment.GetEnvironmentVariable("RT_TAB") ?? "", out tb)) { if (lateTab) late = () => pp.ShowTab(tb); else pp.ShowTab(tb); }
                    // the page follows the active preset when it shows, so with a late tab the preset is picked late too
                    if (lateTab && !string.IsNullOrEmpty(pre)) { var first = late; late = () => { pp.ShowPreset(pre); if (first != null) first(); }; }
                    if (Environment.GetEnvironmentVariable("RT_DC") == "1") pp.ShowCfgSource(true); // the plan block on its battery side
                    if (Environment.GetEnvironmentVariable("RT_FAN_ON") == "1") { var before0 = late; late = () => { if (before0 != null) before0(); pp.PretendFanCurveOn(); }; } // the fan curve switched on in the editor (not saved)
                    var cfgPlanName = Environment.GetEnvironmentVariable("RT_CFG_PLAN"); // RT_CFG_PLAN=<name>: the plan block shows that plan (picked late: the page follows its preset when it shows)
                    if (!string.IsNullOrEmpty(cfgPlanName)) { var before = late; late = () => { if (before != null) before(); pp.ShowCfgPlan(cfgPlanName); }; }
                    page = pp;
                }
                else if (what == "curve") page = new CurvePage(new CurveService(s), new Tuner(s), () => { }, () => { }); // 20.11: the real page (the screenshots), reads the presets, applies nothing
                else if (what == "watch") // 20.11: «Сторож» (reads its state, decides and saves nothing: Dry)
                {
                    var wcs = new CurveService(s);
                    BlackBox.Wanted = () => s.BlackBoxMode == 1 || (s.BlackBoxMode == 0 && s.WatchOn);
                    page = new WatchPage(new Watchdog(s, wcs, () => false) { Dry = true }, wcs, s, i => { });
                }
                else if (what == "tune") page = new TunePage(new Tuner(s), new CurveService(s), s, () => { }, () => { }); // 18.11: the auto-tune page with its mode switch (reads the tune's state, starts nothing)
                else
                {
                    var p = new Page();
                    p.Row("Кривая · " + Hw.CurveCores + " " + "ядер, чиплетов: " + Hw.CurveChips, Hw.Describe(new int[Hw.CurveCores]));
                    var grid = p.Add(new CoreGrid());
                    var v = new int[Hw.CurveCores];
                    for (int i = 0; i < v.Length; i++) v[i] = -5 - (i * 7) % 20;
                    grid.Load(v, null);
                    page = p;
                    if (what == "curveswap") swapGrid = grid;
                }
                page.Dock = DockStyle.Fill;
                f.Controls.Add(page);
                // RT_FRAME=1 (20.11, the screenshots of the README): the program's own menu at the left, the page's item lit,
                // the window wider by the menu
                if (Environment.GetEnvironmentVariable("RT_FRAME") == "1")
                {
                    var key = what == "tune" || what == "watch" ? "curve" : what == "device" || what == "comp" ? "settings" : what == "dist" || what == "startup" ? "apps" : what == "amdpower" ? "power" : what;
                    var nav = new NavBar(MainForm.MenuItems().Where(it => it.Key == NavBar.ToggleKey || Hw.PageFits(it.Key)).ToArray()) { Current = key };
                    f.ClientSize = new Size(f.ClientSize.Width + nav.Width, f.ClientSize.Height);
                    f.Controls.Add(nav); nav.SendToBack(); page.BringToFront();
                }
                var host = page as Page;
                if (host != null && host.Footer != null) { host.Footer.Dock = DockStyle.Bottom; host.Footer.Height = Theme.S(64); f.Controls.Add(host.Footer); }
                f.Show();
                if (late != null) { var t0 = DateTime.Now.AddMilliseconds(700); while (DateTime.Now < t0) { Application.DoEvents(); System.Threading.Thread.Sleep(30); } late(); }
                var until = DateTime.Now.AddSeconds(what == "amdpower" ? 8 : what == "home" || what == "gpu" || what == "ram" ? 5 : what == "tune" || what == "power" ? 3 : 1);
                while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                // curveswap: «по чипам» → «общая» → «по чипам», eight frames of each change on one sheet (16.11: the change
                // had no movement); the grid alone, half size
                if (swapGrid != null)
                {
                    swapGrid.SetMode(1, false);
                    Application.DoEvents();
                    int cols = 4, rows = 4, fw = swapGrid.Width / 2, fh = swapGrid.Height / 2;
                    using (var sheet = new Bitmap(cols * fw, rows * fh))
                    using (var sg = Graphics.FromImage(sheet))
                    {
                        sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            swapGrid.SetMode(pass == 0 ? 2 : 1);
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            for (int k = 0; k < 8; k++)
                            {
                                while (clock.ElapsedMilliseconds < k * 65) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                                using (var one = new Bitmap(swapGrid.Width, swapGrid.Height))
                                {
                                    swapGrid.DrawToBitmap(one, new Rectangle(Point.Empty, swapGrid.Size));
                                    int n = pass * 8 + k;
                                    sg.DrawImage(one, new Rectangle(n % cols * fw, n / cols * fh, fw, fh));
                                    TextRenderer.DrawText(sg, clock.ElapsedMilliseconds + " ms", Theme.Small, new Point(n % cols * fw + 4, n / cols * fh + fh - 20), Color.White);
                                }
                            }
                            while (clock.ElapsedMilliseconds < 700) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        }
                        sheet.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    return;
                }
                // RT_SCROLL=<n>: the page scrolled down by n (at 100 %) first, for a part lower than the window can reach
                int down; var sc = page as ScrollableControl;
                if (sc != null && int.TryParse(Environment.GetEnvironmentVariable("RT_SCROLL") ?? "", out down) && down > 0)
                {
                    sc.AutoScrollPosition = new Point(0, Theme.S(down));
                    var t1 = DateTime.Now.AddMilliseconds(500); while (DateTime.Now < t1) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                }
                using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
    }
}
