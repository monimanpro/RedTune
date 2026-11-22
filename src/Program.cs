using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Red Tune")]
[assembly: AssemblyDescription("Питание, кривая и настройки ноутбуков на AMD Ryzen")]
[assembly: AssemblyProduct("Red Tune")]
[assembly: AssemblyVersion("0.4.8.0")]
[assembly: AssemblyFileVersion("0.4.8.0")]

namespace RedTune
{
    static class Program
    {
        public static Icon AppIcon;

        // --tray             start hidden (logon task)
        // --quit             close the running copy
        // --make-icon <file> write the app icon (build step)
        // --after <pid>      wait for an old copy to exit (restart)
        // --autostart on|off the logon task, the same one Settings creates (the installer and the uninstaller call this)
        // --safe             start without applying the curve, the limits or the preset (Safety)
        [STAThread]
        static int Main(string[] args)
        {
            Func<string, int> at = name => Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            // --stress …: a worker of the program's own heavy load, started by the auto-tune (Stress.Run); nothing else of the program runs in it
            if (args.Length >= 5 && args[0] == "--stress") return Stress.Worker(args.Skip(1).ToArray());
            Store.MoveOld(); // the settings of Legion Tune, before anything touches the folder

            // --audio-uninstall: what the sound page put into Windows is taken back (the uninstaller runs it before the files go)
            if (at("--audio-uninstall") >= 0)
            {
                try { var what = AudioFx.Uninstall(); try { Store.Log("удаление: " + what.Replace("\r\n", "; ")); } catch { } return 0; }
                catch (Exception ex) { try { Store.Log("удаление, звук: " + ex.Message); } catch { } return 1; }
            }
            // --gpu-uninstall: the NVIDIA driver setting of «Держать частоты под нагрузкой» back to the default (GpuDrs; the uninstaller)
            if (at("--gpu-uninstall") >= 0)
            {
                try { var e = GpuDrs.Set(false); try { Store.Log("удаление, видеокарта: " + (e ?? "настройка драйвера сброшена")); } catch { } return e == null ? 0 : 1; }
                catch (Exception ex) { try { Store.Log("удаление, видеокарта: " + ex.Message); } catch { } return 1; }
            }
            int a0 = at("--autostart");
            if (a0 >= 0 && a0 + 1 < args.Length)
            {
                try { Autostart.Set(args[a0 + 1].Equals("on", StringComparison.OrdinalIgnoreCase)); return 0; }
                catch (Exception ex) { try { Store.Log("автозапуск из установщика: " + ex.Message); } catch { } return 1; }
            }

            // 19.11: the checks and the off-screen renders place everything at once (RT_MOTION=1: with the movement, for its own check)
            if (args.Any(a => a == "--page-png" || a.EndsWith("-check")) && Environment.GetEnvironmentVariable("RT_MOTION") != "1") Page.Motion = false;
            int i = at("--make-icon");
            if (i >= 0 && i + 1 < args.Length) { Theme.Init(100); Logo.WriteIcon(args[i + 1]); return 0; }

            // --hw-report <file>: what Hw finds and which sections it would show (RT_HW_SIM simulates another machine), a check
            i = at("--hw-report");
            if (i >= 0 && i + 1 < args.Length) { Hw.Detect(); System.IO.File.WriteAllText(args[i + 1], Hw.Report() + "\r\nМеню: " + string.Join(", ", new[] { "home", "power", "curve", "dist", "ram", "device", "display", "startup", "clean", "comp", "settings", "about" }.Where(Hw.PageFits)) + "\r\nПитание: " + (Hw.LenovoPower ? "Lenovo" : Hw.Amd ? "лимиты AMD (SMU)" : "—") + "\r\nКоротко: " + Hw.Short() + "\r\n", new System.Text.UTF8Encoding(true)); return 0; }
            // --update-check <file> [--download]: what GitHub's latest release says (Updater.Latest), and with --download the
            // setup fetched and checked against GitHub's SHA-256 (into %TEMP%\RedTune-Update; nothing is installed)
            i = at("--update-check");
            if (i >= 0 && i + 1 < args.Length)
            {
                var o = new System.Text.StringBuilder();
                try
                {
                    int fv = at("--for"); // --for <version>: that version's release, as «Вернуть …» takes it
                    var u = fv >= 0 && fv + 1 < args.Length ? Updater.ForVersion(args[fv + 1]) : Updater.Latest();
                    o.AppendLine("current " + Updater.Current + " · latest " + u.Tag + " (" + u.Version + ") · newer " + Updater.Newer(u) + " · installed copy " + Updater.Installed);
                    o.AppendLine("page " + u.Page); o.AppendLine("asset " + u.AssetName + " · " + u.Size + " bytes · sha256 " + (u.Sha256 ?? "—"));
                    if (args.Contains("--download")) o.AppendLine("downloaded and checked: " + Updater.Download(u, null));
                }
                catch (Exception ex) { o.AppendLine("error: " + ex.Message); }
                System.IO.File.WriteAllText(args[i + 1], o.ToString(), new System.Text.UTF8Encoding(true)); return 0;
            }
            // --backup-check <zip>: the backup of the data folder into that file, and what went in (nothing is loaded back)
            i = at("--backup-check");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                int n = Backup.Export(args[i + 1]);
                var o = new System.Text.StringBuilder("files " + n + " · manifest: " + string.Join(" | ", Backup.ManifestOf(args[i + 1]) ?? new string[0]) + " · same machine " + Backup.SameMachine(Backup.ManifestOf(args[i + 1])) + "\r\n");
                using (var z = System.IO.Compression.ZipFile.OpenRead(args[i + 1])) foreach (var e in z.Entries) o.AppendLine(e.FullName + "  " + e.Length);
                System.IO.File.WriteAllText(args[i + 1] + ".txt", o.ToString(), new System.Text.UTF8Encoding(true)); return 0;
            }
            // --crash-check <file>: a made-up exception with a real stack through Crash.Build and Crash.BugUrl, nothing written
            // to the data folder: the record (names taken out) and how long the filled-in Bug form's address gets
            i = at("--crash-check");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                Exception made = null;
                try { var arr = new int[1]; arr[Environment.TickCount < 0 ? 0 : 3].ToString(); } catch (Exception ex) { made = new InvalidOperationException("crash check from " + Store.DataDir, ex); }
                var text = Crash.Build(made, true, "crash check");
                var url = Crash.BugUrl(new Crash.Info { Time = DateTime.Now, Fatal = true, Kind = "System.InvalidOperationException: crash check", Text = text }, Hw.Report());
                System.IO.File.WriteAllText(args[i + 1], "url length " + url.Length + "\r\n" + url + "\r\n\r\n" + text, new System.Text.UTF8Encoding(true)); return 0;
            }
            // --fps-probe <file> [seconds] [--krnl-all]: the frame counter (Fps) listens for some seconds and writes which
            // programs draw and at what rate, and how many events of each kind came (--krnl-all: every event of the
            // graphics kernel, to look for the right one). Reads only.
            i = at("--fps-probe");
            if (i >= 0 && i + 1 < args.Length)
            {
                int secs = 4; if (i + 2 < args.Length) int.TryParse(args[i + 2], out secs);
                var sb = new System.Text.StringBuilder();
                Fps.Histogram = new System.Collections.Generic.Dictionary<string, int>();
                var why = Fps.Start(args.Contains("--krnl-all"));
                if (why != null) sb.AppendLine("не запустился: " + why);
                else
                {
                    System.Threading.Thread.Sleep(Math.Max(2, secs) * 1000);
                    Func<int, string> name = pid => { try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { return "?"; } };
                    foreach (var x in Fps.All().OrderByDescending(x => double.IsNaN(x.Item2) ? x.Item3 : x.Item2))
                        sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "pid {0,-6} {1,-28} api {2,7:0.0}  kernel {3,7:0.0}", x.Item1, name(x.Item1), x.Item2, x.Item3));
                    sb.AppendLine("--- events in " + secs + " s");
                    lock (Fps.Histogram) foreach (var kv in Fps.Histogram.OrderByDescending(kv => kv.Value).Take(40)) sb.AppendLine(kv.Value.ToString().PadLeft(7) + "  " + kv.Key);
                    Fps.Stop();
                }
                System.IO.File.WriteAllText(args[i + 1], sb.ToString(), new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --latency <file>: the memory latency measurement of «Память» into a text file; the run is not kept in the store
            i = at("--latency-probe"); // 21.11: why AIDA64 shows less
            if (i >= 0 && i + 1 < args.Length) { Hw.Detect(); System.IO.File.WriteAllText(args[i + 1], RedTune.Ram.Latency.Probe(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--latency");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                var ended = new System.Threading.ManualResetEvent(false); string text = "";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var no = RedTune.Ram.Latency.Start("cli", null, (run, why) =>
                {
                    text = why ?? RedTune.Ram.Latency.Summary(run, null) + "\r\n" + string.Join("\r\n", run.Points.Select(p => RedTune.Ram.Latency.Size(p.Bytes, false).PadLeft(8) + "  " + p.Ns.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ns"));
                    ended.Set();
                }, false);
                if (no != null) text = no; else ended.WaitOne();
                System.IO.File.WriteAllText(args[i + 1], text + "\r\nL2 " + RedTune.Ram.Latency.CacheL2 + " L3 " + RedTune.Ram.Latency.CacheL3 + " · " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s\r\n", new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --diag <file>: the full report of «Компоненты → Полный отчёт в файл» (reads only), for a check
            i = at("--diag");
            if (i >= 0 && i + 1 < args.Length) { Hw.Detect(); System.IO.File.WriteAllText(args[i + 1], Diag.Full(Store.LoadSettings()), new System.Text.UTF8Encoding(true)); return 0; }
            // --curve-read <file>: the Curve Optimizer values as the app's own SMU code reads them (reads only), a check
            i = at("--curve-read");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                string text;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { var first = Smu.Read(); long ms1 = sw.ElapsedMilliseconds; sw.Restart(); var again = Smu.Read(); text = (Smu.FamilyName ?? "?") + "\r\n" + Store.Join(first) + "\r\n" + Store.Join(again) + "\r\nfirst " + ms1 + " ms, again " + sw.ElapsedMilliseconds + " ms"; }
                catch (Exception ex) { text = "ошибка: " + ex.Message; }
                System.IO.File.WriteAllText(args[i + 1], text + "\r\n", new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --smu-read <file>: what the app's own SMU code reads (nothing is written): the family, which limits it takes,
            // the limits now, the power table version, the memory clocks and the running timings; a check
            i = at("--smu-read");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                var lines = new System.Collections.Generic.List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    lines.Add("CODENAME:" + Smu.Code);
                    lines.Add("MODE:" + (Smu.Apu ? "apu" : "rsmu"));
                    lines.Add("CAN:" + string.Join(",", AmdPower.Keys.Select((k, n) => k + "=" + (Smu.Can()[n] ? 1 : 0))));
                    var lim = Smu.Limits(); lines.Add("LIMITS:" + (lim != null ? string.Join(",", lim) : "-"));
                    lines.Add("TABLE:0x" + Smu.TableVersion().ToString("X"));
                    long ms = sw.ElapsedMilliseconds; sw.Restart();
                    var t = RedTune.Ram.LiveTimings.Read();
                    lines.Add("{" + string.Join(",", t.Select(kv => "\"" + kv.Key + "\":\"" + kv.Value + "\"")) + "}");
                    lines.Add("limits " + ms + " ms, timings " + sw.ElapsedMilliseconds + " ms");
                    sw.Restart(); RedTune.Ram.LiveTimings.Read(); lines.Add("timings again " + sw.ElapsedMilliseconds + " ms");
                    // the power table 200 times in a row: how often a refresh is refused, and whether asking again covers it
                    int empty = 0; Smu.TableMisses = 0; sw.Restart();
                    for (int n = 0; n < 200; n++) if (Smu.Limits() == null) empty++;
                    lines.Add("limits x200: empty " + empty + ", refused refreshes " + Smu.TableMisses + ", " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex) { lines.Add("ошибка: " + ex.Message); }
                System.IO.File.WriteAllLines(args[i + 1], lines, new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --limit-shift-test <file>: PPT one watt under the fast limit the SMU enforces now, then back to it: a check
            // that a limit written by the app's own code lands. The table rows that matter are written down around it
            i = at("--limit-shift-test");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                var lines = new System.Collections.Generic.List<string>();
                Func<string> rows = () =>
                {
                    var t = Smu.Table(0x100); var l = Smu.Limits();
                    return (l != null ? string.Join("/", l) : "-") + " W | " + (t == null ? "-" : string.Join(" ", new[] { 0, 1, 2, 3, 4, 5, 8, 9, 62, 63 }.Select(n => n + "=" + t[n].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))));
                };
                try
                {
                    var before = Smu.Limits();
                    lines.Add("before  " + rows());
                    if (before == null) throw new Exception("лимиты не читаются");
                    int fast = before[1];
                    try
                    {
                        lines.Add("PPT " + (fast - 1) + ": " + Smu.SetLimit(0, fast - 1));
                        System.Threading.Thread.Sleep(1500);
                        lines.Add("shifted " + rows());
                    }
                    finally
                    {
                        lines.Add("PPT " + fast + ": " + Smu.SetLimit(0, fast));
                        System.Threading.Thread.Sleep(1500);
                        lines.Add("back    " + rows());
                    }
                }
                catch (Exception ex) { lines.Add("ошибка: " + ex.Message); }
                System.IO.File.WriteAllLines(args[i + 1], lines, new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --curve-rewrite <file>: reads the curve and writes the same values back (the processor ends up as it was), a check of the write path
            i = at("--curve-rewrite");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                string text;
                try
                {
                    var before = Smu.Read(); var sw = System.Diagnostics.Stopwatch.StartNew();
                    var after = Smu.Write(before);
                    text = "before " + Store.Join(before) + "\r\nafter  " + Store.Join(after) + "\r\n" + (before.SequenceEqual(after) ? "same" : "DIFFERENT") + ", " + sw.ElapsedMilliseconds + " ms";
                }
                catch (Exception ex) { text = "ошибка: " + ex.Message; }
                System.IO.File.WriteAllText(args[i + 1], text + "\r\n", new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --curve-shift-test <file> [seconds]: the first core one step towards 0 for some seconds (<file>.mid is written
            // then, so another reader can look at the processor meanwhile), then back: a check that a changed value lands
            i = at("--curve-shift-test");
            if (i >= 0 && i + 1 < args.Length)
            {
                Hw.Detect();
                int hold = 4; if (i + 2 < args.Length) int.TryParse(args[i + 2], out hold);
                var lines = new System.Collections.Generic.List<string>();
                try
                {
                    var before = Smu.Read(); lines.Add("before  " + Store.Join(before));
                    var shifted = (int[])before.Clone(); shifted[0] += before[0] < 0 ? 1 : -1;
                    try
                    {
                        var got = Smu.Write(shifted); lines.Add("shifted " + Store.Join(got) + (got.SequenceEqual(shifted) ? "  ok" : "  NOT AS WRITTEN"));
                        System.IO.File.WriteAllLines(args[i + 1] + ".mid", lines, new System.Text.UTF8Encoding(true));
                        System.Threading.Thread.Sleep(Math.Max(1, Math.Min(30, hold)) * 1000);
                    }
                    finally { var back = Smu.Write(before); lines.Add("back    " + Store.Join(back) + (back.SequenceEqual(before) ? "  ok" : "  NOT RESTORED")); }
                }
                catch (Exception ex) { lines.Add("ошибка: " + ex.Message); }
                System.IO.File.WriteAllLines(args[i + 1], lines, new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --watch-dry <yyyy-MM-ddTHH:mm> <out.txt>: what the watchdog would advise for the WHEA errors since then (nothing saved, nothing applied)
            i = at("--watch-dry");
            if (i >= 0 && i + 2 < args.Length)
            {
                Hw.Detect();
                var ws = Store.LoadSettings(); ws.WatchOn = true;
                var wc = new CurveService(ws);
                using (var w = new Watchdog(ws, wc, () => false) { Dry = true })
                {
                    w.State = new WatchState { LastSeen = args[i + 1], Since = args[i + 1] };
                    w.CheckNow();
                    var outLines = new System.Collections.Generic.List<string> { "preset: " + wc.Active.Name + " = " + Store.Join(wc.Active.Values), "say: " + w.LastSay,
                        "pending: " + string.Join(", ", w.State.Pending.Select(c => Hw.CoreName(c) + " " + wc.Active.Values[c] + " -> " + Math.Min(0, wc.Active.Values[c] + Watchdog.RaiseStep))),
                        "frozen: " + string.Join(", ", w.State.Frozen.Select(Hw.CoreName)), "wheas: " + w.State.Wheas };
                    outLines.AddRange(w.State.Events.Select(e => "event: " + e.Text));
                    System.IO.File.WriteAllLines(args[i + 2], outLines, new System.Text.UTF8Encoding(true));
                }
                return 0;
            }

            // --whea-scan <yyyy-MM-ddTHH:mm> <out.txt>: the WHEA errors since then as the auto-tune reads them (a check of Load.McaCore)
// --audio-status <file>: the output devices, their effects and the state of Red Tune's own one (AudioFx), a check (reads only)
            i = at("--audio-status");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], AudioFx.Report(), new System.Text.UTF8Encoding(true)); return 0; }
            // --audio-response <file>: the equalizer curve the page draws, at the frequencies of the effect's test bench, a check
            // --audio-attach <device id|default> <file>: the effect into the device's chain with the saved settings switched on, and
            // the Windows sound service restarted (what the switch on the page does); --audio-detach <device id|default|all> <file>:
            // out of it again; --audio-set <device id|default> "<settings>" <file>: settings to the running effect (not saved);
            // --audio-meters <device id|default> <file>: what the effect reports. Checks of AudioFx.
            // --ui-edit-check <file>: the pointer over sliders and the end of typing a slider's number on a click elsewhere (PagePng.EditCheck), a check
            i = at("--ui-edit-check");
            if (i >= 0 && i + 1 < args.Length) { Theme.Init(100); L.Set("ru"); PagePng.EditCheck(args[i + 1]); return 0; }
            // --eq-strips-check <file>: the strips of the equalizer under the mouse (PagePng.EqCheck), a check; a picture goes beside the file
            // --dist-check <file>: «Распределение»: where programs land, what the hints offer (Dist.Engine.Check); moves nothing
            i = at("--dist-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], Dist.Engine.Check(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--dist-live-check");
            if (i >= 0 && i + 2 < args.Length) { System.IO.File.WriteAllText(args[i + 1], Dist.Engine.LiveCheck(int.Parse(args[i + 2])), new System.Text.UTF8Encoding(true)); return 0; }
            // --power-undo-check <file>: Ctrl+Z / Ctrl+Y on «Питание» (PowerPage.UndoCheck); nothing applied or saved
            i = at("--battery-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], Energy.BatteryReport(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--display-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], Display.Check(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--mic-listen-check"); // 21.11: records 2 s from the default microphone (thrown away) and says what came; nothing goes out
            if (i >= 0 && i + 1 < args.Length)
            {
                var mid = AudioFx.DefaultDevice(true); MicListen.Start(mid); System.Threading.Thread.Sleep(2000); MicListen.Stop();
                System.IO.File.WriteAllText(args[i + 1], "microphone " + mid.ToString("B") + " (capture: " + AudioFx.IsCapture(mid) + "): " + MicListen.Format + ", frames in 2 s: " + MicListen.Frames + "\r\n", new System.Text.UTF8Encoding(true)); return 0;
            }
            i = at("--minstate-check"); // 21.11: the core-curve measurement lowers the plan's minimum state and puts it back
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], CoreCurve.MinStateCheck(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--slider-warp-check");
            if (i >= 0 && i + 1 < args.Length) { Theme.Init(100); L.Set("ru"); Hw.Detect(); System.IO.File.WriteAllText(args[i + 1], Slider.WarpCheck(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--curve-undo-check");
            if (i >= 0 && i + 1 < args.Length)
            {
                // Ctrl+Z / Ctrl+Y on «Кривая» (CurvePage.UndoCheck); nothing applied or saved
                var su = Store.LoadSettings(); Comp.S = su; Theme.Init(100); L.Set("ru"); Hw.Detect();
                // the service posts its readings to the context it was made on: the UI thread here, as in the app
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var cs = new CurveService(su); var cp = new CurvePage(cs, new Tuner(su), () => { }, () => { });
                using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(1000, 800) })
                { f.Controls.Add(cp); f.Show(); Application.DoEvents(); System.IO.File.WriteAllText(args[i + 1], cp.UndoCheck(), new System.Text.UTF8Encoding(true)); }
                return 0;
            }
            i = at("--power-undo-check");
            if (i >= 0 && i + 1 < args.Length)
            {
                var su = Store.LoadSettings(); Comp.S = su; Theme.Init(100); L.Set("ru"); Hw.Detect();
                var ps = new PowerService(); var pp = new PowerPage(ps, new CurveService(su), new FanControl(ps, su), su);
                using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(1000, 800) })
                { f.Controls.Add(pp); f.Show(); Application.DoEvents(); System.IO.File.WriteAllText(args[i + 1], pp.UndoCheck(), new System.Text.UTF8Encoding(true)); }
                return 0;
            }
            i = at("--eq-strips-check");
            if (i >= 0 && i + 1 < args.Length) { Theme.Init(100); L.Set("ru"); PagePng.EqCheck(args[i + 1]); return 0; }
            // --color-mode-check <file>: each screen and whether Windows runs it in advanced colour (the mini panel goes without the blur there); reads only
            i = at("--color-mode-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], ColorMode.Report(), new System.Text.UTF8Encoding(true)); return 0; }
            // --comp-graph-check <file>: the points and dials of the compressor's picture under the mouse (PagePng.CompCheck), a check; a picture goes beside the file
            i = at("--comp-graph-check");
            if (i >= 0 && i + 1 < args.Length) { int zoom = 100; int.TryParse(Environment.GetEnvironmentVariable("RT_CHECK_ZOOM") ?? "", out zoom); Theme.Init(zoom >= 75 && zoom <= 300 ? zoom : 100); L.Set("ru"); PagePng.CompCheck(args[i + 1]); return 0; } // RT_CHECK_ZOOM: the picture at another scale
            // --audio-record <device id|default> <seconds> <file.wav> <report>: the sound the effect puts out, recorded (AudioFx.Record)
            i = at("--audio-record");
            if (i >= 0 && i + 4 < args.Length)
            {
                string rep; Guid rid; double sec;
                if (args[i + 1] == "default") rid = AudioFx.DefaultDevice(); else Guid.TryParse(args[i + 1], out rid);
                if (!double.TryParse(args[i + 2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sec)) sec = 5;
                try { rep = AudioFx.Record(rid, sec, args[i + 3]); } catch (Exception ex) { rep = "FAILED: " + ex; }
                System.IO.File.WriteAllText(args[i + 4], rep + "\r\n", new System.Text.UTF8Encoding(true)); return 0;
            }
            // --audio-nahimic <device id|default> on|off <file>: Nahimic's effects back into the device's chain or out of it, and
            // the Windows sound service restarted (what the switch «Эффекты Nahimic» does)
            foreach (var cmd in new[] { "--audio-attach", "--audio-detach", "--audio-set", "--audio-meters", "--audio-nahimic" })
            {
                i = at(cmd);
                if (i < 0 || i + 2 >= args.Length) continue;
                System.IO.File.WriteAllText(args[args.Length - 1], AudioFx.Cli(cmd, args[i + 1], cmd == "--audio-set" || cmd == "--audio-nahimic" ? args[i + 2] : null), new System.Text.UTF8Encoding(true));
                return 0;
            }
            i = at("--audio-response");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], AudioFx.ResponseCheck(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--whea-scan");
            if (i >= 0 && i + 2 < args.Length) { Hw.Detect(); var hits = Load.WheaHits(DateTime.Parse(args[i + 1])); System.IO.File.WriteAllText(args[i + 2], string.Join("\r\n", hits.Select(h => h.Text + " | core " + h.Core)) + "\r\ncount " + hits.Count + "\r\n", new System.Text.UTF8Encoding(true)); return 0; }

            // --asus-check <file>: what would be sent to an ASUS laptop's firmware, on the pretended Vivobook of the report (Asus.Check); no driver is touched
            i = at("--asus-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], Asus.Check(), new System.Text.UTF8Encoding(true)); return 0; }
            // --fan-check <file>: the deciding part of the app's fan curves on made-up temperatures (FanControl.Check); the firmware is not touched
            i = at("--fan-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], FanControl.Check(), new System.Text.UTF8Encoding(true)); return 0; }
            // --fan-live-test <file> [seconds of load]: the fan curves on the real fans (FanControl.LiveTest) — WRITES fan speeds to the
            // firmware for about two minutes, loads every core, then gives the fans back. Only with the user's word.
            i = at("--fan-live-test");
            if (i >= 0 && i + 1 < args.Length)
            {
                int loadS = 40; if (i + 2 < args.Length) int.TryParse(args[i + 2], out loadS);
                AppDomain.CurrentDomain.UnhandledException += (o, e) => { try { FanControl.ReleaseAll(); } catch { } };
                System.IO.File.WriteAllText(args[i + 1], FanControl.LiveTest(Math.Max(10, Math.Min(120, loadS))), new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --sound-paint-time <file> [scale %]: what one whole paint of the sound page's live pictures costs, ms (the
            // equalizer with 12 bands and a spectrum, the compressor in three bands with a made-up sound, the level meter)
            i = at("--sound-paint-time");
            if (i >= 0 && i + 1 < args.Length)
            {
                int zoom = 150; if (i + 2 < args.Length) int.TryParse(args[i + 2], out zoom);
                Theme.Init(zoom); L.Set("ru");
                var eq = new EqGraph { Width = Theme.S(1100), Height = Theme.S(320) };
                var comp = new CompGraph { Location = new System.Drawing.Point(0, eq.Height), Width = Theme.S(1100) };
                var meter = new LevelMeter { Location = new System.Drawing.Point(0, eq.Height + comp.Height), Width = Theme.S(1100) };
                using (var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), ClientSize = new System.Drawing.Size(eq.Width, eq.Height + comp.Height + meter.Height) })
                {
                    f.Controls.Add(eq); f.Controls.Add(comp); f.Controls.Add(meter); f.Show(); Application.DoEvents();
                    for (int b = eq.P.Bands.Count; b < 12; b++) eq.P.Bands.Add(new EqBand { Type = 1, Freq = (float)(40 * Math.Pow(2, b)), Gain = (b % 3 - 1) * 3, Q = 1 });
                    var db = new double[AudioFx.SpectrumSize / 2 + 1]; for (int k = 0; k < db.Length; k++) db[k] = -24 - 9 * Math.Log(k + 1, 2);
                    eq.Rate = 48000; eq.Feed(db);
                    comp.P.Split = 250; comp.P.Split2 = 4000; comp.Demo(); meter.Set(true, 0.3f, 0.35f, -4, -1);
                    Func<Control, double> time = c =>
                    {
                        using (var bmp = new System.Drawing.Bitmap(c.Width, c.Height))
                        {
                            c.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, c.Width, c.Height));
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            for (int k = 0; k < 30; k++) c.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, c.Width, c.Height));
                            return sw.Elapsed.TotalMilliseconds / 30;
                        }
                    };
                    double te = time(eq), tct = time(comp); comp.SetView(true); double tcf = time(comp); double tm = time(meter);
                    System.IO.File.WriteAllText(args[i + 1], string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "scale {0} %, one whole paint, ms:\r\nequalizer (12 bands, spectrum) {1:0.0}\r\ncompressor, monitor over time {2:0.0}\r\ncompressor, monitor by frequencies {3:0.0}\r\nlevel meter {4:0.0}\r\nall three, every moment (a moment = 33 ms) {5:0.0}\r\n",
                        zoom, te, tct, tcf, tm, te + Math.Max(tct, tcf) + tm), new System.Text.UTF8Encoding(true));
                }
                return 0;
            }
            // --toggle-frames <file> [scale %]: how often a toggle's slide gets a frame while the sound page's live pictures are
            // repainted 30 times a second beside it (20.11: the toggles on «Звук» looked slower than on the other pages)
            i = at("--toggle-frames");
            if (i >= 0 && i + 1 < args.Length)
            {
                int zoom = 150; if (i + 2 < args.Length) int.TryParse(args[i + 2], out zoom);
                Theme.Init(zoom); L.Set("ru");
                System.IO.File.WriteAllText(args[i + 1], "scale " + zoom + " %, a toggle slide (260 ms) beside the sound page's pictures:\r\n" + ToggleFrames.Run(), new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --motion-frames <file> [scale]: how smoothly rows come out (MotionFrames): the help card and the slide of «Питание»
            i = at("--motion-frames");
            if (i >= 0 && i + 1 < args.Length)
            {
                int zoom = 150; if (i + 2 < args.Length) int.TryParse(args[i + 2], out zoom);
                Theme.Init(zoom); L.Set("ru");
                System.IO.File.WriteAllText(args[i + 1], "scale " + zoom + " %, motion " + Page.Motion + ":" + Environment.NewLine + MotionFrames.Run(), new System.Text.UTF8Encoding(true));
                return 0;
            }
            // --motion-check <file>: the movement of rows that come and go on the pages (PagePng.MotionCheck), a check on a page of its own; pictures go beside the file
            i = at("--motion-check");
            if (i >= 0 && i + 1 < args.Length) { Theme.Init(100); L.Set("ru"); PagePng.MotionCheck(args[i + 1]); return 0; }
            // --sound-undo-check <file>: Ctrl+Z / Ctrl+Y on «Звук» (PagePng.SoundUndoCheck), a check; reads the devices, sends nothing to the effect, saves nothing
            i = at("--sound-undo-check");
            if (i >= 0 && i + 1 < args.Length) { var su = Store.LoadSettings(); Comp.S = su; Theme.Init(100); L.Set("ru"); Hw.Detect(); PagePng.SoundUndoCheck(args[i + 1], su); return 0; }
            // --page-png <curve|amdpower> <out.png> [--fresh] [--expert]: a page drawn off screen (--fresh: as right after install) (with RT_HW_SIM: as on another processor), a check
            i = at("--page-png");
            if (i >= 0 && i + 2 < args.Length)
            {
                var s0 = args.Contains("--fresh") ? new Settings() : Store.LoadSettings(); if (args.Contains("--expert")) s0.Expert = true; if (args.Contains("--novice")) s0.Expert = false; /* 20.11: as without the expert functions */ if (args.Contains("--strict")) s0.TuneMode = "strict"; if (args.Contains("--daily")) s0.TuneMode = "daily"; // the auto-tune page in a mode (not saved)
                int li = at("--lang"); if (li >= 0 && li + 1 < args.Length) s0.Language = args[li + 1]; // --lang <code>: the page in that language
                int zi = at("--ui-scale"), zv; if (zi >= 0 && zi + 1 < args.Length && int.TryParse(args[zi + 1], out zv) && zv >= 50 && zv <= 400) s0.UiScale = zv; // --ui-scale <percent>: a sharper picture (not saved)
                Comp.S = s0; Theme.Init(s0.UiScale); L.Set(s0.Language); Hw.Detect(); PagePng.Render(args[i + 1], args[i + 2], s0); return 0;
            }
            // --lang-check <file>: for every language, how many of the pairs its file covers and which Russian texts are
            // left untranslated by it (shown in English); a check of lang\*.txt
            // --stress-check <file>: the own heavy load: its dll, the kernels, their arithmetic, and that a planted error is caught; a check
            // --plan-dry <file>: what the «Создать «Тюнинг»» button of the power page would write into its plan (nothing is written), a check
            // --gpu-probe <file>: what the NVIDIA driver answers to its Get functions (read only, GpuProbe.cs), a check
            // --gpu-domain-probe <file>: the "Domain V/F" read functions (XBAR/SYS/Video), read only; raw answers beside the file
            i = at("--gpu-domain-probe");
            if (i >= 0 && i + 1 < args.Length) { string r; try { r = GpuProbe.RunDomain(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[i + 1]))); } catch (Exception ex) { r = "FAILED: " + ex; } System.IO.File.WriteAllText(args[i + 1], r, new System.Text.UTF8Encoding(true)); return 0; }
            // --gpu-domain-set <key 1|2|21> <MHz> <file>: one offset on every point of XBAR / SYS / Video (GpuDomains.SetOffset,
            // with its checks and its own rollback), the curves before and after in the file. 0 MHz takes the offset off.
            // --gpu-clocks <file>: the measured clock of every domain key 0..31 (GpuDomains.Measured), read only
            i = at("--gpu-clocks");
            if (i >= 0 && i + 1 < args.Length)
            {
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < 32; k++) { int v = GpuDomains.Measured(k); sb.AppendLine("key " + k + ": " + v + " (status " + GpuDomains.LastStatus + ")"); }
                if (sb.Length == 0) sb.AppendLine("no answer");
                System.IO.File.WriteAllText(args[i + 1], sb.ToString(), new System.Text.UTF8Encoding(true)); return 0;
            }
            // --gpu-volt <file> | --gpu-volt-set <mV> <file> [key] (23.11): the core voltage-demand offset (GpuVolt), read / set with its read-back
            i = at("--gpu-volt");
            if (i >= 0 && i + 1 < args.Length)
            {
                var v = GpuVolt.Read();
                var dv = GpuVolt.ReadDomains();
                System.IO.File.WriteAllText(args[i + 1], (v == null ? "read failed: " + GpuVolt.Error : "core voltage offset " + v + " mV") + "; XBAR/SYS/Video " + (dv == null ? "read failed" : string.Join("/", dv)) + " mV");
                return 0;
            }
            i = at("--gpu-volt-set");
            if (i >= 0 && i + 2 < args.Length)
            {
                int key = i + 3 < args.Length ? int.Parse(args[i + 3]) : 0; // an optional clock key after the file: 1 XBAR, 2 SYS, 21 Video
                var before = GpuVolt.Read(key); var e = GpuVolt.Set(int.Parse(args[i + 1]), key); var after = GpuVolt.Read(key);
                System.IO.File.WriteAllText(args[i + 2], "before " + before + " mV, set: " + (e ?? "ok") + ", after " + after + " mV");
                return e == null ? 0 : 1;
            }
            i = at("--gpu-domain-set");
            if (i >= 0 && i + 3 < args.Length)
            {
                var sb = new System.Text.StringBuilder();
                Func<string> tops = () => { var c = GpuDomains.Read(); return c == null ? "read failed: " + GpuDomains.Error : string.Join(" · ", c.Select(x => x.Name + " " + x.Mhz.Min() + "–" + x.Mhz.Max() + " MHz, offset " + string.Join("/", x.OffsetKhz.Distinct()) + " kHz")); };
                try
                {
                    sb.AppendLine("before: " + tops());
                    var r = GpuDomains.SetOffset(int.Parse(args[i + 1]), int.Parse(args[i + 2]));
                    sb.AppendLine("set: " + (r ?? "OK"));
                    sb.AppendLine("after:  " + tops());
                }
                catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
                System.IO.File.WriteAllText(args[i + 3], sb.ToString(), new System.Text.UTF8Encoding(true)); return 0;
            }
            i = at("--gpu-probe");
            if (i >= 0 && i + 1 < args.Length) { string r; try { r = GpuProbe.Run(); } catch (Exception ex) { r = "FAILED: " + ex; } System.IO.File.WriteAllText(args[i + 1], r, new System.Text.UTF8Encoding(true)); return 0; }
            // --uninst-list <file> [name]: what «Удаление программ» lists (Store apps marked), and for [name] the leftovers it
            // would offer after removing it (nothing is removed); a check
            i = at("--uninst-list");
            if (i >= 0 && i + 1 < args.Length)
            {
                L.Set("ru"); var sb = new System.Text.StringBuilder(); var all = Uninst.List();
                foreach (var a in all) sb.AppendLine((a.IsStore ? "[Store] " : "        ") + a.Name + " · " + a.Publisher + " · " + a.Version + (a.IsStore ? " · " + a.Package : ""));
                if (i + 2 < args.Length) { var one = all.FirstOrDefault(a => a.Name.Equals(args[i + 2], StringComparison.OrdinalIgnoreCase)); sb.AppendLine(); sb.AppendLine("leftovers of " + args[i + 2] + ":"); if (one != null) foreach (var l in Uninst.Leftovers(one, false)) sb.AppendLine("  " + l.Kind + " · " + l.What); else sb.AppendLine("  (not in the list)"); }
                System.IO.File.WriteAllText(args[i + 1], sb.ToString(), new System.Text.UTF8Encoding(true)); return 0;
            }
            // --uninst-rules-png <file> [scale]: the dialog of the search rules drawn off screen (RT_SCROLL: scrolled), a check
            i = at("--uninst-rules-png");
            if (i >= 0 && i + 1 < args.Length) { int z = 100; if (i + 2 < args.Length) int.TryParse(args[i + 2], out z); Theme.Init(z >= 75 && z <= 300 ? z : 100); int lg = at("--lang"); L.Set(lg >= 0 && lg + 1 < args.Length ? args[lg + 1] : "ru"); UninstRulesDialog.Render(args[i + 1]); return 0; }
            // --uninst-orphans <file>: what «Найти хвосты удалённых программ» would offer (nothing is removed); a check
            i = at("--uninst-orphans");
            if (i >= 0 && i + 1 < args.Length)
            {
                L.Set("ru"); var sw = System.Diagnostics.Stopwatch.StartNew(); var sb = new System.Text.StringBuilder();
                var left = Uninst.Orphans(Uninst.List());
                foreach (var l in left) sb.AppendLine(l.Kind + " · " + l.What + " · " + l.Path);
                sb.AppendLine(left.Count + " found in " + sw.ElapsedMilliseconds + " ms");
                System.IO.File.WriteAllText(args[i + 1], sb.ToString(), new System.Text.UTF8Encoding(true)); return 0;
            }
            i = at("--plan-dry");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], WinPower.TuningPlanText(), new System.Text.UTF8Encoding(true)); return 0; }
            // --plan-values <file>: the five processor settings of every Windows plan as the power page reads them (nothing is written), a check
            i = at("--plan-values");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], WinPower.CpuValuesText(), new System.Text.UTF8Encoding(true)); return 0; }
            // --plan-roundtrip <file>: a copy of the active plan is made, the five settings are written into it and read back, the copy is deleted; a check
            i = at("--plan-roundtrip");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], WinPower.CpuRoundTrip(), new System.Text.UTF8Encoding(true)); return 0; }
            // --tune-preset-dry <file>: what the tuning preset «00 Тюнинг» would hold by the rule of 18.11, here and on a machine with one weak preset (nothing is saved), a check
            i = at("--tune-preset-dry");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], PowerService.TunePresetText(new PowerService().Data), new System.Text.UTF8Encoding(true)); return 0; }
            // --plan-ui-check <file>: the plan block of the power page on a copy of the active plan (made and deleted by the check): changes wait for «Записать в схему», a check
            i = at("--plan-ui-check");
            if (i >= 0 && i + 1 < args.Length) { var s1 = Store.LoadSettings(); Comp.S = s1; Theme.Init(s1.UiScale); L.Set(s1.Language); Hw.Detect(); PagePng.PlanUiCheck(args[i + 1], s1); return 0; }
            // --brightness-check <file>: the screen brightness now and in every power plan (read), and a write into a copy of the active plan made and deleted by the check
            i = at("--brightness-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], WinPower.BrightnessCheck(), new System.Text.UTF8Encoding(true)); return 0; }
            // --power-mode-check <file>: Windows' power mode now and what each mode holds (read); the mode of the power source not in use is changed and put back, a check of the write
            i = at("--power-mode-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], WinPower.PowerModeCheck(), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--stress-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], Stress.Check(), new System.Text.UTF8Encoding(true)); return 0; }
            // --heavy-test <file> <own|p95> <avx|sse|avx512|one> <core|all> <seconds> [corrupt]: the auto-tune's heavy load on a core (or on all)
            // at the curve as it is (nothing is written to the processor), with the clock, the voltage, the power and the currents
            // every second: to compare the own load with Prime95
            // --start-trace <file> <own|p95> <mode> <core> <ms>: the first moments of a load on one core in fine steps (nothing is written), a measurement
            i = at("--start-trace");
            if (i >= 0 && i + 5 < args.Length) { Hw.Detect(); System.IO.File.WriteAllText(args[i + 1], Stress.StartTrace(Store.LoadSettings(), args[i + 2], args[i + 3], int.Parse(args[i + 4]), int.Parse(args[i + 5])), new System.Text.UTF8Encoding(true)); return 0; }
            i = at("--heavy-test");
            if (i >= 0 && i + 5 < args.Length) { Hw.Detect(); System.IO.File.WriteAllText(args[i + 1], Stress.Compare(Store.LoadSettings(), args[i + 2], args[i + 3], args[i + 4], int.Parse(args[i + 5]), args.Contains("corrupt")), new System.Text.UTF8Encoding(true)); return 0; }
            // --calib-step <log> <core> <value> <own|p95> <avx|sse|avx512|one> <seconds>: WRITES to the processor (one core's Curve
            // Optimizer value, for the time of the load, then the curve as it was): how soon the load errs at a value that is
            // too deep. --calib-restore <log>: writes back the curve of a step that did not end
            i = at("--calib-step");
            if (i >= 0 && i + 6 < args.Length) { Hw.Detect(); Console.Out.WriteLine(Stress.CalibStep(Store.LoadSettings(), args[i + 1], int.Parse(args[i + 2]), int.Parse(args[i + 3]), args[i + 4], args[i + 5], int.Parse(args[i + 6]))); return 0; }
            i = at("--calib-restore");
            if (i >= 0 && i + 1 < args.Length) { Hw.Detect(); System.IO.File.AppendAllText(args[i + 1], Stress.CalibRestore(args[i + 1]) + "\r\n"); return 0; }
            i = at("--lang-check");
            if (i >= 0 && i + 1 < args.Length) { System.IO.File.WriteAllText(args[i + 1], L.Coverage(), new System.Text.UTF8Encoding(true)); return 0; }

            // --osd-png <out.png> <backdrop.png> [--all] [--noglass]: the notification drawn over a picture (a check; the live one is not in screenshots)
            i = at("--osd-png");
            if (i >= 0 && i + 2 < args.Length) { Theme.Init(100); Osd.RenderTo(args[i + 1], args[i + 2], args.Contains("--all"), !args.Contains("--noglass")); return 0; }

            // --gpu-rewrite <out.txt>: a test of the GPU write path that changes nothing — the curve is read, the same offsets
            // are written back, read again and compared (13.11: run only with the user's OK)
            i = at("--gpu-rewrite");
            if (i >= 0 && i + 1 < args.Length)
            {
                var c0 = Gpu.Read();
                string r = c0 == null ? "read failed: " + Gpu.Error : (Gpu.Write(c0.Delta) ?? "written");
                var c1 = Gpu.Read();
                bool same = c0 != null && c1 != null && c0.Delta.SequenceEqual(c1.Delta) && c0.Mhz.SequenceEqual(c1.Mhz);
                System.IO.File.WriteAllLines(args[i + 1], new[] { r, "before: " + (c0 == null ? "-" : Gpu.Describe(c0.Delta)), "after: " + (c1 == null ? "-" : Gpu.Describe(c1.Delta)), "same: " + same });
                return 0;
            }

            // --gpu-shift-test <out.txt>: a real change, small: +15 MHz on every point of the curve, read back (offsets and
            // the frequencies the driver reports), then the offsets that were there before are written back (13.11: user's OK)
            i = at("--gpu-shift-test");
            if (i >= 0 && i + 1 < args.Length)
            {
                var log = new System.Collections.Generic.List<string>();
                var c0 = Gpu.Read();
                if (c0 == null) { System.IO.File.WriteAllLines(args[i + 1], new[] { "read failed: " + Gpu.Error }); return 0; }
                log.Add("before: " + Gpu.Describe(c0.Delta) + " · point 60: " + c0.Mv[60] + " mV " + c0.Mhz[60] + " MHz offset " + c0.Delta[60]);
                var plus = c0.Delta.Select(d => d + 15).ToArray();
                log.Add("write +15: " + (Gpu.Write(plus) ?? "OK"));
                System.Threading.Thread.Sleep(500);
                var c1 = Gpu.Read();
                if (c1 != null)
                {
                    int moved = Enumerable.Range(0, c0.Count).Count(k => c1.Mhz[k] != c0.Mhz[k]);
                    log.Add("after: " + Gpu.Describe(c1.Delta) + " · point 60: " + c1.Mhz[60] + " MHz offset " + c1.Delta[60] + " · points with another frequency: " + moved + " of " + c0.Count);
                }
                log.Add("write back: " + (Gpu.Write(c0.Delta) ?? "OK"));
                var c2 = Gpu.Read();
                if (c2 != null) log.Add("restored: " + c0.Delta.SequenceEqual(c2.Delta) + " · point 60: " + c2.Mhz[60] + " MHz");
                System.IO.File.WriteAllLines(args[i + 1], log);
                return 0;
            }

            // --menu-png <out.png> <backdrop.png>: a drop-down drawn over a picture (a check of its glass)
            i = at("--menu-png");
            if (i >= 0 && i + 2 < args.Length) { Theme.Init(100); DarkRenderer.RenderTo(args[i + 1], args[i + 2]); return 0; }

            // --list-clean <file> [close list]: what «Как после загрузки» would close and which services it would stop, nothing is touched (a check);
            // the optional "a.exe|b.exe" stands in for «Закрывать всегда»
            i = at("--list-clean");
            if (i >= 0 && i + 1 < args.Length)
            {
                var cs = Store.LoadSettings();
                if (i + 2 < args.Length) cs.CleanClose = args[i + 2];
                var found = CleanSlate.Find(cs);
                System.IO.File.WriteAllLines(args[i + 1], found.Select(p => p.Start.ToString("HH:mm:ss") + "  " + p.Pid + "  " + p.Name + "  " + p.Path)
                    .Concat(CleanSlate.Services(cs).Select(x => "service  " + x.Name + "  " + x.Display + "  " + x.Exe)));
                return 0;
            }

            // --list-uninst <file> [name]: installed programs, the leftovers that would be offered for [name] and the orphan folders; nothing is removed (a check)
            i = at("--list-uninst");
            if (i >= 0 && i + 1 < args.Length)
            {
                var apps = Uninst.List();
                var lines = apps.Select(a => a.Name + "  |  " + a.Publisher + "  |  " + a.Version + "  |  " + a.Location + "  |  " + a.Uninstall).ToList();
                if (i + 2 < args.Length)
                    foreach (var a in apps.Where(a => a.Name.IndexOf(args[i + 2], StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        lines.Add(""); lines.Add("== leftovers of " + a.Name + " (names: " + string.Join(", ", Uninst.Names(a)) + ")");
                        lines.AddRange(Uninst.Leftovers(a, false).Select(l => "  [" + l.Kind + "] " + l.What));
                    }
                lines.Add(""); lines.Add("== orphans");
                lines.AddRange(Uninst.Orphans(apps).Select(l => "  " + l.What));
                System.IO.File.WriteAllLines(args[i + 1], lines);
                return 0;
            }

            i = at("--tune-state-dry");
            if (i >= 0 && i + 2 < args.Length) { Hw.Detect(); Tuner.StateDry(args[i + 1], args[i + 2]); return 0; }
            // --tune-edges <out>: the edges the «Фазу 2 заново» button would start from (the state file, else the log), a check
            i = at("--tune-edges");
            if (i >= 0 && i + 1 < args.Length) { Hw.Detect(); var ed = Tuner.Edges(new Tuner(Store.LoadSettings()).State); System.IO.File.WriteAllText(args[i + 1], ed == null ? "none" : string.Join(" ", ed)); return 0; }
            i = at("--tune-upgrade-dry");
            if (i >= 0 && i + 2 < args.Length) { Hw.Detect(); Tuner.UpgradeDry(args[i + 1], args[i + 2]); return 0; }
            i = at("--simulate-tune");
            if (i >= 0 && i + 3 < args.Length) { Hw.Detect(); Tuner.SimulateToFile(args[i + 1], args[i + 2], args[i + 3]); return 0; }
            // --memtest-dry <megabytes> <seconds> <file> [inject]: the memory check on a small size, the result into a file
            i = at("--memtest-dry");
            if (i >= 0 && i + 3 < args.Length) { Ram.MemTest.RunToFile(args[i + 1], args[i + 2], args[i + 3], at("inject") > i); return 0; }
            // --ram-plan "tRAS=86;speed=auto" <out.txt>: what the memory page would send to the BIOS for these changes (reads only, nothing written)
            i = at("--ram-plan");
            if (i >= 0 && i + 2 < args.Length) { Hw.Detect(); Ram.RamCore.PlanToFile(args[i + 1], args[i + 2]); return 0; }

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RedTune.App.Show");
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RedTune.App.Quit");
            if (at("--quit") >= 0) { quit.Set(); return 0; }
            // --mini: the running copy opens (or closes) its mini panel by the mouse: for a shortcut or a hot key of
            // one's own (16.11). Nothing happens when no copy runs.
            var miniSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RedTune.App.Mini");
            if (at("--mini") >= 0) { miniSignal.Set(); return 0; }

            i = at("--after");
            int oldPid;
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out oldPid))
                try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(10000); } catch { }

            bool created;
            using (new Mutex(true, "Local\\RedTune.App", out created))
            {
                if (!created) { show.Set(); return 0; }
                // 21.11: a backup chosen in Settings goes in now, before anything reads the settings (Backup)
                { var imported = Backup.ApplyPending(); if (imported != null) Store.Log(imported); }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 21.11: every exception nothing caught is also kept for a report (Crash, «О программе»)
                Application.ThreadException += (s, e) => { Store.Log("ошибка: " + e.Exception); Crash.Record(e.Exception, false, "UI thread"); };
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { try { Store.Log("ошибка в задаче: " + e.Exception); Crash.Record(e.Exception, false, "task"); e.SetObserved(); } catch { } };
                // a crash must not leave a fan on a low RPM: give the fans back to the firmware
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { Store.Log("сбой: " + e.ExceptionObject); Crash.Record(e.ExceptionObject as Exception, e.IsTerminating, "unhandled"); FanControl.ReleaseAll(); } catch { } };
                var settings = Store.LoadSettings();
                Comp.S = settings;
                Hw.Detect(); // which parts of the app this computer can run
                if (settings.CleanVer < 3) { try { CleanSlate.Upgrade(settings); } catch { } settings.CleanVer = 3; Store.SaveSettings(settings); }
                if (settings.CleanVer < 4) { try { CleanSlate.Upgrade4(settings); } catch { } settings.CleanVer = 4; Store.SaveSettings(settings); }
                if (settings.CleanVer < 5) { try { CleanSlate.Upgrade5(settings); } catch { } settings.CleanVer = 5; Store.SaveSettings(settings); }
                {   // Store-app folders saved with their version (before «any version» existed) match every version from now on
                    string k = CleanSlate.AnyVersionList(settings.CleanKeep), c = CleanSlate.AnyVersionList(settings.CleanClose);
                    if (k != settings.CleanKeep || c != settings.CleanClose) { settings.CleanKeep = k; settings.CleanClose = c; Store.SaveSettings(settings); }
                }
                Theme.Init(settings.UiScale);
                L.Set(settings.Language);
                try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { AppIcon = SystemIcons.Application; }

                Application.AddMessageFilter(new ClickAway()); // 19.11: a click outside a text field ends the typing in it
                var form = new MainForm(settings);
                var handle = form.Handle; // the window exists even when started hidden, so the signals can reach it
                bool hidden = at("--tray") >= 0;

                new Thread(() =>
                {
                    while (true)
                    {
                        int n = WaitHandle.WaitAny(new WaitHandle[] { show, quit, miniSignal });
                        try
                        {
                            if (n == 0) form.BeginInvoke((Action)form.ShowFromTray);
                            else if (n == 2) form.BeginInvoke((Action)form.ToggleMini);
                            else { form.BeginInvoke((Action)form.ExitApp); return; }
                        }
                        catch { return; }
                    }
                }) { IsBackground = true }.Start();

                Store.Log("запуск" + (hidden ? " в трее" : "") + (form.TuneOnStart ? ", идёт автотюн: окно на вкладке «Автотюн»" : ""));
                if (form.TuneOnStart) form.BeginInvoke((Action)form.ShowTune);
                if (!hidden) form.Show();
                Application.Run();
                return 0;
            }
        }
    }
}
