using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RedTune
{
    // --toggle-frames: a check of how smoothly a toggle slides next to the sound page's live pictures (20.11). The
    // pictures get a made-up sound and are invalidated 30 times a second, as the page's own moment does; the toggle is
    // flipped a few times, every frame it paints is timed. Measured twice: with the pictures idle and with them live.
    static class ToggleFrames
    {
        class Probe : Toggle
        {
            public readonly List<double> Frames = new List<double>();
            public Stopwatch Clock;
            protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if (Clock != null) Frames.Add(Clock.Elapsed.TotalMilliseconds); }
        }

        public static string Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("pictures still:                          " + Measure(false, 0));
            sb.AppendLine("pictures live 30/s:                      " + Measure(true, 0));
            sb.AppendLine("live + click paints both pictures (old): " + Measure(true, 1));
            sb.AppendLine("live + click only invalidates (new):     " + Measure(true, 2));
            return sb.ToString();
        }

        static string Measure(bool live, int click)
        {
            var eq = new EqGraph { Width = Theme.S(1100), Height = Theme.S(320) };
            var comp = new CompGraph { Location = new Point(0, eq.Height), Width = Theme.S(1100) };
            var meter = new LevelMeter { Location = new Point(0, eq.Height + comp.Height), Width = Theme.S(1100) };
            var tg = new Probe { Location = new Point(Theme.S(10), eq.Height + comp.Height + meter.Height + Theme.S(10)) };
            using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), BackColor = Theme.Card, Opacity = 0.01, FormBorderStyle = FormBorderStyle.None,
                ClientSize = new Size(eq.Width, tg.Bottom + Theme.S(10)) })
            {
                f.Controls.Add(eq); f.Controls.Add(comp); f.Controls.Add(meter); f.Controls.Add(tg); f.Show(); Application.DoEvents();
                for (int b = eq.P.Bands.Count; b < 12; b++) eq.P.Bands.Add(new EqBand { Type = 1, Freq = (float)(40 * Math.Pow(2, b)), Gain = (b % 3 - 1) * 3, Q = 1 });
                var db = new double[AudioFx.SpectrumSize / 2 + 1]; for (int k = 0; k < db.Length; k++) db[k] = -24 - 9 * Math.Log(k + 1, 2);
                eq.Rate = 48000; eq.Feed(db);
                comp.P.Split = 250; comp.P.Split2 = 4000; comp.Demo(); meter.Set(true, 0.3f, 0.35f, -4, -1);
                var page = new Timer { Interval = 33 };
                int n = 0;
                page.Tick += (s, e) => { n++; db[5] = -20 - n % 7; eq.Feed(db); eq.Invalidate(); comp.Invalidate(); meter.Invalidate(); };
                if (live) page.Start();
                // what the page does when a toggle is clicked: 1 = repaints the pictures at once (Dim before 20.11), 2 = only marks them
                tg.CheckedChanged += (s, e) => { if (click == 0) return; eq.Invalidate(); comp.Invalidate(); if (click == 1) { eq.Update(); comp.Update(); } };
                var clock = Stopwatch.StartNew(); tg.Clock = clock;
                var flips = new List<double>();
                // five slides, 450 ms apart (each 260 ms long), in the program's own message loop; the clicks come as posted
                // messages from a pool timer, as a mouse click would (ahead of paints and timers)
                double next = 300; bool done = false;
                var clicker = new System.Threading.Timer(_ =>
                {
                    if (done || clock.Elapsed.TotalMilliseconds < next) return;
                    next += 450;
                    try { f.BeginInvoke((Action)(() => { if (flips.Count < 5) { flips.Add(clock.Elapsed.TotalMilliseconds); tg.Checked = !tg.Checked; } else { done = true; f.Close(); } })); } catch { }
                }, null, 0, 5);
                f.Hide(); Application.Run(f);
                clicker.Dispose(); page.Stop(); page.Dispose();
                // frames inside each slide: how many, the largest gap between two of them (from the click on)
                var counts = new List<int>(); var gaps = new List<double>();
                foreach (var t0 in flips)
                {
                    var fr = tg.Frames.Where(x => x >= t0 && x <= t0 + 260).ToList();
                    counts.Add(fr.Count);
                    double last = t0, gap = 0; foreach (var x in fr) { gap = Math.Max(gap, x - last); last = x; }
                    gap = Math.Max(gap, t0 + 260 - last); gaps.Add(gap);
                }
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "frames per slide {0} (≈{1:0} fps), largest gap {2:0} ms (avg of largest {3:0})",
                    string.Join(" ", counts), counts.Average() / 0.26, gaps.Max(), gaps.Average());
            }
        }
    }
}
