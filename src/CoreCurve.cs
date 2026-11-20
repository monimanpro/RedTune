using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // 14.11: the voltage/clock curve of one core, measured. The processor does not give out a core's factory curve, so it
    // is taken the way it shows in work: the core is loaded alone (one busy thread pinned to it), its clock is capped step
    // by step (Windows' «maximum processor frequency» of the active plan, put back afterwards) and at each step the core's
    // clock and voltage are read from the processor's telemetry table (the PM table that HWiNFO and others read, through
    // PawnIO's RyzenSMU module: reads only). The Curve Optimizer value moves this curve down or up, so a measurement is
    // kept with the value the core had: measured at −29 and at 0, both curves show on one graph.
    //
    // The table's layout differs by processor family and firmware. Known here: version 0x540208 (Dragon Range, Ryzen
    // 7045/8945HX, checked 14.11 on a Legion R7000P: a core at −41 read 0.83 V at 4.45 GHz, a core at 0 about 1.03 V).
    // On another version the measurement says it is not supported rather than guess.
    static class CoreCurve
    {
        public class Point { public float Ghz, Volt; }
        public class Run
        {
            public int Core, Co;
            public DateTime Time;
            public List<Point> Points = new List<Point>();
        }

        // Tdc, Edc: the index of the limit (A) in the table, the value drawn now follows it. Found 16.11 on the R7000P by
        // setting each limit for a moment (with the user's OK): TDC 115 -> 84 moved entry 8, EDC -> 114 moved entry 62;
        // entry 9 follows power / voltage under load (27.6 A at 50 W), entry 63 is the peak (33.8 A all cores, about
        // 16-22 A with one busy core while entry 9 shows 5 A). Entry 6, an "85" with a value of 0, is neither.
        class Layout { public int Bytes, Volt, Freq, C0, Tdc, Edc; }
        static readonly Dictionary<uint, Layout> Layouts = new Dictionary<uint, Layout>
        {
            { 0x540208, new Layout { Bytes = 0x994, Volt = 0x4E8, Freq = 0x568, C0 = 0x5E8, Tdc = 8, Edc = 62 } },
        };

        // the processor's current limits and what it draws now, for the power page: read only, nothing is ever written.
        // TDC: the current it may hold for long, EDC: the peak current. null when the table is not known or not readable.
        public class AmpsNow { public float TdcLimit, Tdc, EdcLimit, Edc; }
        public static AmpsNow Amps()
        {
            if (WhyNot() != null || layout.Tdc <= 0 || layout.Edc <= 0) return null;
            var t = ReadTable();
            if (t == null) return null;
            var a = new AmpsNow { TdcLimit = t[layout.Tdc], Tdc = t[layout.Tdc + 1], EdcLimit = t[layout.Edc], Edc = t[layout.Edc + 1] };
            Func<float, bool> limit = v => v >= 5 && v <= 1000, value = v => v >= 0 && v <= 1000;
            return limit(a.TdcLimit) && limit(a.EdcLimit) && value(a.Tdc) && value(a.Edc) ? a : null;
        }

        static uint version;
        static bool resolved;
        static Layout layout;

        // null = it can be measured here; otherwise why not
        public static string WhyNot()
        {
            if (!Hw.Amd) return "Только для процессоров AMD Ryzen";
            if (!Tctl.Available) return "Нужен PawnIO (вкладка «Компоненты»)";
            if (!resolved)
            {
                var r = Tctl.Execute("ioctl_resolve_pm_table", new ulong[0], 2);
                if (r == null) return "Таблица телеметрии процессора не читается";
                version = (uint)r[0]; resolved = true;
                Layouts.TryGetValue(version, out layout);
                Store.Log("кривая ядра: таблица телеметрии 0x" + version.ToString("X") + (layout == null ? " (раскладка неизвестна)" : ""));
            }
            return layout == null ? "Раскладка телеметрии этого процессора (0x" + version.ToString("X") + ") пока неизвестна" : null;
        }

        // every core's state now, for the black box: "core:busy %:GHz:V;…" (null when the table cannot be read)
        public static string Sample()
        {
            if (layout == null) return null;
            var t = ReadTable();
            if (t == null) return null;
            var sb = new StringBuilder();
            for (int c = 0; c < Hw.CurveCores; c++)
            {
                float v = t[layout.Volt / 4 + c], f = t[layout.Freq / 4 + c], c0 = t[layout.C0 / 4 + c];
                if (c > 0) sb.Append(';');
                sb.Append(c).Append(':').Append(c0.ToString("0", CultureInfo.InvariantCulture)).Append(':').Append(f.ToString("0.00", CultureInfo.InvariantCulture)).Append(':').Append(v.ToString("0.000", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // for --heavy-test: the mean clock (GHz), voltage and busy share of these cores, the processor's power (W) and its
        // currents (A) now; null when the table cannot be read
        public static float[] Now(int[] cores)
        {
            if (WhyNot() != null) return null;
            var t = ReadTable();
            if (t == null) return null;
            return new[] { cores.Average(c => t[layout.Freq / 4 + c]), cores.Average(c => t[layout.Volt / 4 + c]), cores.Average(c => t[layout.C0 / 4 + c]),
                t[3], layout.Tdc > 0 ? t[layout.Tdc + 1] : 0, layout.Edc > 0 ? t[layout.Edc + 1] : 0 };
        }

        static float[] ReadTable() { return Smu.Table(layout.Bytes); } // asks again when a refresh is refused

        // ---------- the store: "core|co|time|ghz:volt,ghz:volt…", the last run per core and value ----------

        static string FilePath { get { return Path.Combine(Store.DataDir, "corecurves.txt"); } }

        public static List<Run> Load()
        {
            var list = new List<Run>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var p = line.Split('|');
                    int core, co; DateTime t;
                    if (p.Length < 4 || !int.TryParse(p[0], out core) || !int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out co)) continue;
                    if (!DateTime.TryParseExact(p[2], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    var run = new Run { Core = core, Co = co, Time = t };
                    foreach (var pt in p[3].Split(','))
                    {
                        var xy = pt.Split(':'); float x, y;
                        if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                            run.Points.Add(new Point { Ghz = x, Volt = y });
                    }
                    if (run.Points.Count > 1) list.Add(run);
                }
            }
            catch (Exception ex) { Store.Log("кривая ядра: " + ex.Message); }
            return list;
        }

        static void Save(Run run)
        {
            var all = Load().Where(r => !(r.Core == run.Core && r.Co == run.Co)).ToList();
            all.Add(run);
            Write(all);
        }

        // a run that turned out not to be what it says (16.11: the curve was applied again while the core was measured at 0)
        public static void Forget(int core, int co)
        {
            try { Write(Load().Where(r => !(r.Core == core && r.Co == co)).ToList()); } catch (Exception ex) { Store.Log("кривая ядра: " + ex.Message); }
        }

        static void Write(List<Run> all)
        {
            Directory.CreateDirectory(Store.DataDir);
            File.WriteAllLines(FilePath, all.OrderBy(r => r.Core).ThenBy(r => r.Co).Select(r => string.Join("|", r.Core.ToString(), r.Co.ToString(CultureInfo.InvariantCulture),
                r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                string.Join(",", r.Points.Select(p => p.Ghz.ToString("0.000", CultureInfo.InvariantCulture) + ":" + p.Volt.ToString("0.000", CultureInfo.InvariantCulture))))), new UTF8Encoding(true));
        }

        // ---------- the clock cap: «maximum processor frequency» of the active plan ----------

        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        static Guid Processor = new Guid("54533251-82be-4824-96c1-47b60b740d00"), MaxState = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100") /* «maximum processor frequency», MHz, 0 = no limit */;

        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

        // the scheme whose values the processor follows: the overlay of the performance slider when one is in effect (its
        // values win over the plan's: 14.11, the cap written to the plan did nothing while the overlay held 4500 MHz),
        // else the active plan
        static Guid CapScheme()
        {
            try { Guid o; if (PowerGetEffectiveOverlayScheme(out o) == 0 && o != Guid.Empty) return o; } catch { }
            return ActiveScheme();
        }

        static Guid ActiveScheme()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) throw new Exception("план электропитания не читается");
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); }
        }

        static void ReadCap(Guid scheme, out uint ac, out uint dc)
        {
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref MaxState, out ac) != 0 || PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref MaxState, out dc) != 0)
                throw new Exception("«максимальная частота процессора» в плане питания не читается");
        }

        static void WriteCap(Guid scheme, uint ac, uint dc)
        {
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref MaxState, ac);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref MaxState, dc);
            var active = ActiveScheme();
            PowerSetActiveScheme(IntPtr.Zero, ref active); // the plan again: its values and the overlay's take effect
        }

        // 21.11 (the user's wish): the plan's «minimum processor state» holds the clock up (80 % of the 2.5 GHz base =
        // 2.0 GHz in the user's «Balance»), and the measurement stopped there. While it runs the minimum is 0 in the same
        // scheme, put back right after, as the cap. Three settings (PROCTHROTTLEMIN, …1, …2: the core classes), AC and DC;
        // one the scheme has not got stays uint.MaxValue and is not written.
        static readonly Guid[] MinIds = { new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"), new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d"), new Guid("893dee8e-2bef-41e0-89c6-b55d0929964e") };

        static uint[] ReadMin(Guid scheme)
        {
            var r = new uint[MinIds.Length * 2];
            for (int i = 0; i < MinIds.Length; i++)
            {
                var id = MinIds[i]; uint v;
                r[2 * i] = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref id, out v) == 0 ? v : uint.MaxValue;
                r[2 * i + 1] = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref id, out v) == 0 ? v : uint.MaxValue;
            }
            return r;
        }

        // the values into the scheme (made active again by WriteCap, which follows)
        static void WriteMin(Guid scheme, uint[] v)
        {
            for (int i = 0; i < MinIds.Length; i++)
            {
                var id = MinIds[i];
                if (v[2 * i] != uint.MaxValue) PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref id, v[2 * i]);
                if (v[2 * i + 1] != uint.MaxValue) PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Processor, ref id, v[2 * i + 1]);
            }
        }

        // --minstate-check: on a copy of the active plan (made and deleted here, never active) the minimum is read,
        // set to 0 and put back, as a measurement does
        internal static string MinStateCheck()
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, t) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + t);
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", "/duplicatescheme " + ActiveScheme()) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            string outp; using (var p = System.Diagnostics.Process.Start(psi)) { outp = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var m = System.Text.RegularExpressions.Regex.Match(outp, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!m.Success) { line(false, "no copy of the plan: " + outp.Trim()); return sb.ToString(); }
            var copy = new Guid(m.Value);
            try
            {
                var set = ReadMin(copy).Select(v => v == uint.MaxValue ? v : 80u).ToArray(); WriteMin(copy, set);
                var before = ReadMin(copy);
                line(before[0] == 80, "the copy holds 80 %, as the user's plan: " + Mins(before));
                WriteMin(copy, before.Select(v => v == uint.MaxValue ? v : 0u).ToArray());
                var low = ReadMin(copy);
                line(low.All(v => v == uint.MaxValue || v == 0), "lowered for the measurement: " + Mins(low) + " (" + low.Count(v => v != uint.MaxValue) + " of 6 settings)");
                WriteMin(copy, before);
                var back = ReadMin(copy);
                line(back.SequenceEqual(before), "put back: " + Mins(back));
                line(ActiveScheme() != copy, "the copy never became the active plan");
            }
            finally
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg.exe", "/delete " + copy) { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit();
            }
            return sb.ToString();
        }

        static string Mins(uint[] v) { return v[0] == uint.MaxValue ? "—" : v[0] + " / " + (v[1] == uint.MaxValue ? "—" : v[1].ToString()) + " %"; }

        // the plan's own values, kept in a file while a measurement runs: if the app dies in the middle, the next start
        // puts them back. scheme|cap AC|cap DC, and since 21.11 the six minimum states after them
        static string LeftPath { get { return Path.Combine(Store.DataDir, "maxstate.restore"); } }

        // the cap held by someone else than a measurement (the auto-tune's light-load and step stages, 14.11): begin keeps
        // the plan's values (also in the file, for a crash), set moves the cap, end puts the values back
        static Guid heldScheme; static uint heldAc, heldDc; static bool held;
        public static void CapBegin()
        {
            if (held) return;
            heldScheme = CapScheme(); ReadCap(heldScheme, out heldAc, out heldDc);
            Directory.CreateDirectory(Store.DataDir);
            File.WriteAllText(LeftPath, heldScheme + "|" + heldAc + "|" + heldDc);
            held = true;
        }
        public static void CapSet(int mhz) { if (held) WriteCap(heldScheme, (uint)mhz, (uint)mhz); }
        public static void CapEnd()
        {
            if (!held) return;
            held = false;
            try { WriteCap(heldScheme, heldAc, heldDc); File.Delete(LeftPath); } catch (Exception ex) { Store.Log("тест: возврат плана: " + ex.Message); }
        }

        public static void RestoreIfLeft()
        {
            try
            {
                if (!File.Exists(LeftPath)) return;
                var p = File.ReadAllText(LeftPath).Trim().Split('|');
                if (p.Length == 3 + MinIds.Length * 2)
                {
                    var mins = p.Skip(3).Select(uint.Parse).ToArray();
                    WriteMin(new Guid(p[0]), mins);
                    Store.Log("кривая ядра: минимальное состояние процессора в плане возвращено (" + Mins(mins) + ") после прерванного замера");
                }
                if (p.Length >= 3) { WriteCap(new Guid(p[0]), uint.Parse(p[1]), uint.Parse(p[2])); Store.Log("кривая ядра: максимальная частота процессора в плане возвращена (" + p[1] + " / " + p[2] + " МГц) после прерванного замера"); }
                File.Delete(LeftPath);
            }
            catch (Exception ex) { Store.Log("кривая ядра: возврат плана: " + ex.Message); }
        }

        // ---------- the measurement ----------

        public static bool Measuring { get; private set; }
        static volatile bool cancel;
        // the load thread runs until this is set. 14.11: it was a captured local, and the JIT kept the thread on a copy it had
        // read once, so the thread never saw it set and every measured core stayed fully busy after its measurement
        // (16 cores at 100 % after «Измерить все»). volatile: the thread reads it afresh each time round.
        static volatile bool loadStop;
        public static void Cancel() { cancel = true; }

        // progress: 0..1 and the points so far; done: the run, or null with the reason
        // stepMhz: how far apart the clock caps are (14.11: 200 MHz for «Измерить все», 100 MHz for one core: more points, a
        // longer run; 400 MHz was the first version)
        public static void Measure(int core, int co, Action<float, List<Point>> progress, Action<Run, string> done, int stepMhz = 400)
        {
            var ui = SynchronizationContext.Current;
            Action<Action> post = a => { if (ui != null) ui.Post(_ => a(), null); else a(); };
            var why = WhyNot();
            if (why != null) { done(null, why); return; }
            if (Measuring) { done(null, "Замер уже идёт"); return; }
            Measuring = true; cancel = false;
            Task.Run(() =>
            {
                var run = new Run { Core = core, Co = co, Time = DateTime.Now };
                string err = null;
                loadStop = false;
                Thread load = null;
                Guid scheme = Guid.Empty; uint ac = 100, dc = 100; bool capped = false;
                uint[] mins = null; bool minLowered = false;
                try
                {
                    scheme = CapScheme();
                    ReadCap(scheme, out ac, out dc);
                    mins = ReadMin(scheme);
                    Directory.CreateDirectory(Store.DataDir);
                    File.WriteAllText(LeftPath, scheme + "|" + ac + "|" + dc + "|" + string.Join("|", mins));
                    capped = true;
                    // the minimum to 0 where it is above (it is written with the first cap, which makes the scheme active again)
                    if (mins.Any(v => v != uint.MaxValue && v > 0))
                    {
                        WriteMin(scheme, mins.Select(v => v == uint.MaxValue ? v : 0u).ToArray());
                        minLowered = true;
                        Store.Log("кривая ядра: минимальное состояние процессора " + Mins(mins) + " → 0 на время замера");
                    }

                    int cpu = core * Hw.ThreadsPerCore; // the core's first logical processor
                    load = new Thread(() =>
                    {
                        SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
                        double x = 1;
                        while (!loadStop) for (int i = 0; i < 200000; i++) x = Math.Sqrt(x + i);
                    }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                    load.Start();

                    // MHz; 0 = no limit (the top of the curve). «Maximum processor state» in percent was tried first: this processor
                    // ignores it (14.11: 4.48 GHz at every step from 100 % down to 30 %)
                    var capList = new List<int> { 0 };
                    for (int mhz = stepMhz >= 400 ? 4800 : 5400; mhz >= 1200; mhz -= Math.Max(50, stepMhz)) capList.Add(mhz);
                    var steps = capList.ToArray();
                    for (int s = 0; s < steps.Length && !cancel; s++)
                    {
                        WriteCap(scheme, (uint)steps[s], (uint)steps[s]);
                        Thread.Sleep(s == 0 ? 1200 : stepMhz <= 200 ? 550 : 700); // the clock settles (a small step settles sooner)
                        // the rail feeds every core, and another core waking up can only raise it: of the readings with this
                        // core fully busy, the lowest voltage is its own
                        float best = float.MaxValue, ghz = 0;
                        for (int k = 0; k < 5 && !cancel; k++)
                        {
                            var t = ReadTable();
                            if (t != null)
                            {
                                float v = t[layout.Volt / 4 + core], f = t[layout.Freq / 4 + core], c0 = t[layout.C0 / 4 + core];
                                if (c0 >= 97f && v > 0.2f && v < 1.7f && f > 0.3f && f < 7f && v < best) { best = v; ghz = f; }
                            }
                            Thread.Sleep(110);
                        }
                        if (best < float.MaxValue && !run.Points.Any(p => Math.Abs(p.Ghz - ghz) < 0.04f))
                            run.Points.Add(new Point { Ghz = ghz, Volt = best });
                        var copy = run.Points.OrderBy(p => p.Ghz).ToList(); float part = (s + 1f) / steps.Length;
                        post(() => progress(part, copy));
                    }
                    if (cancel) err = "Замер остановлен";
                    else if (run.Points.Count < 3) err = "Мало точек: ядро не удалось держать занятым (закрой тяжёлые программы и повтори)";
                }
                catch (Exception ex) { err = ex.Message; }
                finally
                {
                    loadStop = true;
                    if (capped)
                    {
                        try
                        {
                            if (minLowered) WriteMin(scheme, mins);
                            WriteCap(scheme, ac, dc); // also makes the scheme active again: the minimum and the cap take effect
                            File.Delete(LeftPath);
                            if (minLowered) Store.Log("кривая ядра: минимальное состояние процессора возвращено (" + Mins(mins) + ")");
                        }
                        catch (Exception ex) { Store.Log("кривая ядра: возврат плана: " + ex.Message); }
                    }
                    if (load != null) load.Join(1000);
                    Measuring = false;
                }
                run.Points = run.Points.OrderBy(p => p.Ghz).ToList();
                if (err == null)
                {
                    try { Save(run); } catch (Exception ex) { Store.Log("кривая ядра: " + ex.Message); }
                    Store.Log("кривая ядра " + Hw.CoreName(core) + " (" + co + "): " + string.Join(" ", run.Points.Select(p => p.Ghz.ToString("0.00", CultureInfo.InvariantCulture) + "ГГц/" + p.Volt.ToString("0.000", CultureInfo.InvariantCulture) + "В")));
                }
                post(() => done(err == null ? run : null, err));
            });
        }
    }

    // the graph: clock across, voltage up; one line per measured Curve Optimizer value of the core
    class CoreCurveGraph : Painted
    {
        public List<CoreCurve.Run> Runs = new List<CoreCurve.Run>();
        public List<CoreCurve.Point> LivePoints; // a measurement in progress
        public int NowCo;                        // the core's value now: its run is the bright one
        public string Empty = "";
        // 14.11: every core on one graph («Все ядра»): a line per core, its run at the value it has now (else its latest),
        // CCD1 in red, CCD2 in pale pink, the core's number at the top end of its line
        public bool Overview;
        public Func<int, int?> CoOf = c => null;
        public HashSet<int> Hidden = new HashSet<int>(); // 16.11: cores switched off under the graph («Все ядра»)
        // 16.11: a core's own window: which of its curves are drawn, switched by the lamps under the graph (RunToggles).
        // 0: the run at the value the core has now, 1: the run at 0 (the factory curve), 2: the earlier runs at other values
        public HashSet<int> HiddenKinds = new HashSet<int>();
        // 16.11: the factory curve (0) and its lamp are amber, the warm light of an old studio unit's lit button (the user's
        // photo of a 1176); the line being measured takes it too while a measurement at 0 runs
        public static readonly Color ZeroCol = Color.FromArgb(0xf7, 0x94, 0x2c);
        public Color? LiveCol;
        public int Kind(CoreCurve.Run r) { return r.Co == NowCo ? 0 : r.Co == 0 ? 1 : 2; }

        // 14.11: the scale is fixed, the same for every core and every measurement, so the curves of different cores can be
        // compared by eye. 1.5–5.5 GHz and 0.55–1.35 V: where this processor's cores actually run
        const float X0 = 1.5f, X1 = 5.5f, Y0 = 0.55f, Y1 = 1.35f;

        // 14.11: the point under the mouse: dashed lines from it to both axes, the values on the axes, and whose point it is.
        // Between two measured points the mouse goes along the line and the values are taken on the straight piece between
        // them (Approx, shown with «≈»): only the points themselves were measured.
        class Hit { public PointF At; public float Ghz, Volt; public Color Col; public string Who, Key; public bool Approx; }
        readonly List<Hit> hits = new List<Hit>();
        readonly List<List<Hit>> lines = new List<List<Hit>>(); // each line's points in order, for the pieces between them
        Hit hot;
        // 14.11: the glass knobs on a line show only near the mouse, growing as it comes closer (42 knobs at once were a
        // row of beads); elsewhere the points are small dots
        PointF mouse; bool mouseIn;

        public CoreCurveGraph() { BackColor = Theme.Card; }

        RectangleF Plot { get { return new RectangleF(Theme.S(56), Theme.S(18), Width - Theme.S(76), Height - Theme.S(64)); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location; mouseIn = true; Invalidate(); // the knobs near the mouse follow it
            // a measured point near the mouse first, else the nearest place on a line
            Hit best = null; float bd = Theme.S(10) * Theme.S(10);
            foreach (var h in hits)
            {
                float dx = h.At.X - e.X, dy = h.At.Y - e.Y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = h; }
            }
            if (best == null)
            {
                float sd = Theme.S(12);
                foreach (var line in lines)
                    for (int i = 0; i + 1 < line.Count; i++)
                    {
                        var a = line[i]; var b = line[i + 1];
                        float vx = b.At.X - a.At.X, vy = b.At.Y - a.At.Y, len2 = vx * vx + vy * vy;
                        if (len2 < 0.5f) continue;
                        float t = Math.Max(0f, Math.Min(1f, ((e.X - a.At.X) * vx + (e.Y - a.At.Y) * vy) / len2));
                        float px = a.At.X + vx * t, py = a.At.Y + vy * t, d = (float)Math.Sqrt((px - e.X) * (px - e.X) + (py - e.Y) * (py - e.Y));
                        if (d < sd)
                        {
                            sd = d;
                            best = new Hit { At = new PointF(px, py), Ghz = a.Ghz + (b.Ghz - a.Ghz) * t, Volt = a.Volt + (b.Volt - a.Volt) * t, Col = a.Col, Who = a.Who, Approx = true };
                        }
                    }
            }
            bool changed = (best == null) != (hot == null) || (best != null && (best.Approx || hot.Approx || best.Key != hot.Key));
            hot = best;
            if (changed) { Cursor = hot == null ? Cursors.Default : Cursors.Cross; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouseIn = false; hot = null; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            hits.Clear(); lines.Clear();
            var plot = Plot;
            var all = Runs.SelectMany(r => r.Points).Concat(LivePoints ?? new List<CoreCurve.Point>()).ToList();
            Func<float, float> X = v => plot.X + (v - X0) / (X1 - X0) * plot.Width, Y = v => plot.Bottom - (v - Y0) / (Y1 - Y0) * plot.Height;
            using (var grid = new Pen(Color.FromArgb(0x35, 0x34, 0x31)))
            {
                for (int k = (int)Math.Ceiling(Y0 * 10 - 0.001); k <= (int)Math.Floor(Y1 * 10 + 0.001); k++)
                {
                    float v = k / 10f;
                    g.DrawLine(grid, plot.X, Y(v), plot.Right, Y(v));
                    TextRenderer.DrawText(g, v.ToString("0.0"), Theme.Small, new Rectangle(0, (int)Y(v) - Theme.S(9), (int)plot.X - Theme.S(8), Theme.S(18)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                for (float v = X0; v <= X1 + 0.001f; v += 0.5f)
                {
                    g.DrawLine(grid, X(v), plot.Y, X(v), plot.Bottom);
                    TextRenderer.DrawText(g, v.ToString("0.0"), Theme.Small, new Rectangle((int)X(v) - Theme.S(24), (int)plot.Bottom + Theme.S(4), Theme.S(48), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
                g.DrawLine(grid, plot.X, plot.Bottom, plot.Right, plot.Bottom);
            }
            TextRenderer.DrawText(g, L.T("В"), Theme.Small, new Rectangle((int)plot.X + Theme.S(6), (int)plot.Y + Theme.S(2), Theme.S(40), Theme.S(16)), Theme.Muted, TextFormatFlags.Left);
            TextRenderer.DrawText(g, L.T("ГГц"), Theme.Small, new Rectangle((int)plot.Right - Theme.S(44), (int)plot.Bottom + Theme.S(22), Theme.S(48), Theme.S(18)), Theme.Muted, TextFormatFlags.Right);

            if (all.Count == 0)
            {
                TextRenderer.DrawText(g, L.T(Empty), Theme.Body, Rectangle.Round(plot), Theme.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            // the lines stay inside the scale (a point a hair outside it, like 1.49 GHz, is cut at the edge)
            var st = g.Save();
            g.SetClip(RectangleF.Inflate(plot, Theme.S(6), Theme.S(6)), CombineMode.Intersect);
            if (Overview)
            {
                var chipCol = new[] { Theme.Heat(0.95f), Theme.AccentFg };
                foreach (var grp in Runs.Where(r => !Hidden.Contains(r.Core)).GroupBy(r => r.Core).OrderBy(gr => gr.Key))
                {
                    int c = grp.Key; var co = CoOf(c);
                    var run = grp.FirstOrDefault(r => co.HasValue && r.Co == co.Value) ?? grp.OrderByDescending(r => r.Time).First();
                    var col = chipCol[Math.Min(1, Hw.ChipOf(c))];
                    Line(g, run.Points, X, Y, Color.FromArgb(LivePoints != null ? 120 : 230, col), Theme.S(1.6f), false, false, Hw.CoreName(c) + " · " + L.T("смещение") + " " + (run.Co > 0 ? "+" : "") + run.Co);
                    var top = run.Points.OrderBy(p => p.Ghz).Last();
                    string num = (c - Hw.ChipCores(Hw.ChipOf(c))[0] + 1).ToString();
                    TextRenderer.DrawText(g, num, Theme.Small, new Rectangle((int)X(top.Ghz) + Theme.S(5), (int)Y(top.Volt) - Theme.S(9), Theme.S(24), Theme.S(18)), col, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                if (LivePoints != null && LivePoints.Count > 0) Line(g, LivePoints, X, Y, Theme.Heat(0.9f), Theme.S(2.2f), false, true, L.T("идёт замер"));
                g.Restore(st);
                if (Hw.CurveChips > 1)
                {
                    int lx = (int)plot.Right - Theme.S(150), lyy = (int)plot.Y + Theme.S(4);
                    for (int k = 0; k < 2; k++)
                    {
                        using (var pen = new Pen(chipCol[k], Theme.S(2))) g.DrawLine(pen, lx, lyy + Theme.S(8) + k * Theme.S(18), lx + Theme.S(24), lyy + Theme.S(8) + k * Theme.S(18));
                        TextRenderer.DrawText(g, "CCD" + (k + 1), Theme.Small, new Rectangle(lx + Theme.S(30), lyy + k * Theme.S(18), Theme.S(110), Theme.S(18)), Theme.Text2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    }
                }
            }
            else
            {
                // older values first and dim, the core's value now last and bright; 0 (the factory curve) dotted
                var legend = new List<Tuple<Color, bool, bool, string>>();
                // 16.11: of the earlier measurements («Прошлые») only the latest one is drawn: every value the core ever
                // had keeps its run in the store, and after a few tunes the graph was a bundle of grey lines
                var lastOld = Runs.Where(r => Kind(r) == 2).OrderByDescending(r => r.Time).FirstOrDefault();
                foreach (var r in Runs.Where(r => !HiddenKinds.Contains(Kind(r)) && (Kind(r) != 2 || r == lastOld)).OrderBy(r => r.Co == NowCo ? 1 : 0).ThenBy(r => r.Co))
                {
                    // dimmed while it is being measured again; while the factory curve is measured it stays as it is
                    bool now = r.Co == NowCo && (LivePoints == null || LiveCol != null);
                    var col = now ? Theme.Heat(0.9f) : r.Co == 0 ? ZeroCol : Theme.Muted;
                    string co = (r.Co > 0 ? "+" : "") + r.Co;
                    Line(g, r.Points, X, Y, col, now ? Theme.S(2.2f) : r.Co == 0 ? Theme.S(1.8f) : Theme.S(1.4f), r.Co == 0 && !now, now, L.T("смещение") + " " + co);
                    legend.Add(Tuple.Create(col, r.Co == 0 && !now, now, co + (r.Co == 0 ? " · " + L.T("заводская") : "") + (now ? " · " + L.T("сейчас") : "") + "  " + r.Time.ToString("dd.MM HH:mm")));
                }
                if (LivePoints != null && LivePoints.Count > 0) Line(g, LivePoints, X, Y, LiveCol ?? Theme.Heat(0.9f), Theme.S(2.2f), false, true, L.T("идёт замер"));
                g.Restore(st);
                int ly = (int)plot.Y + Theme.S(4);
                foreach (var t in legend)
                {
                    using (var pen = new Pen(t.Item1, Theme.S(2)) { DashStyle = t.Item2 ? DashStyle.Dot : DashStyle.Solid })
                        g.DrawLine(pen, plot.Right - Theme.S(190), ly + Theme.S(8), plot.Right - Theme.S(166), ly + Theme.S(8));
                    TextRenderer.DrawText(g, t.Item4, Theme.Small, new Rectangle((int)plot.Right - Theme.S(160), ly, Theme.S(160), Theme.S(18)), t.Item3 ? Theme.Text : Theme.Text2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    ly += Theme.S(18);
                }
            }

            // a measured point is looked up again (the graph may have moved); a place on a line stays where it was found
            var shown = hot == null ? null : hot.Approx ? hot : hits.FirstOrDefault(h => h.Key == hot.Key);
            if (shown != null) DrawHot(g, plot, shown);
        }

        // the point under the mouse: lines from it to the voltage axis and to the clock axis, the two values in boxes on the
        // axes, a ring on the point and whose point it is beside it
        void DrawHot(Graphics g, RectangleF plot, Hit h)
        {
            var p = h.At;
            using (var pen = new Pen(Color.FromArgb(200, h.Col), Math.Max(1f, Theme.S(1.2f))) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(pen, plot.X, p.Y, p.X, p.Y);
                g.DrawLine(pen, p.X, p.Y, p.X, plot.Bottom);
            }
            if (h.Approx)
                using (var pen = new Pen(h.Col, Math.Max(1f, Theme.S(1.6f)))) g.DrawEllipse(pen, p.X - Theme.S(4.5f), p.Y - Theme.S(4.5f), Theme.S(9), Theme.S(9));
            else Theme.GlassKnob(g, p.X, p.Y, Theme.S(14), 1f, h.Col, Theme.S(4f), false, 180f, new PointF?[0]);
            Action<string, RectangleF> box = (text, r) =>
            {
                Theme.Fill(g, r, Theme.S(5), Theme.Raised);
                Theme.Stroke(g, r, Theme.S(5), h.Col);
                TextRenderer.DrawText(g, text, Theme.Small, Rectangle.Round(r), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            string approx = h.Approx ? "≈ " : "";
            string v = approx + h.Volt.ToString("0.000") + " " + L.T("В"), f = approx + h.Ghz.ToString("0.00") + " " + L.T("ГГц");
            int vw = TextRenderer.MeasureText(v, Theme.Small).Width + Theme.S(10), fw = TextRenderer.MeasureText(f, Theme.Small).Width + Theme.S(10), bh = Theme.S(20);
            box(v, new RectangleF(Math.Max(1, plot.X - vw - Theme.S(2)), p.Y - bh / 2f, vw, bh));          // on the voltage axis
            box(f, new RectangleF(p.X - fw / 2f, plot.Bottom + Theme.S(2), fw, bh));                       // on the clock axis
            // whose point: above it, kept inside the graph
            string who = h.Approx ? h.Who + " · " + L.T("между замерами") : h.Who;
            int ww = TextRenderer.MeasureText(who, Theme.Small).Width + Theme.S(12);
            float wx = Math.Max(plot.X + Theme.S(2), Math.Min(plot.Right - ww, p.X - ww / 2f)), wy = Math.Max(plot.Y + Theme.S(2), p.Y - Theme.S(14) - bh);
            var wr = new RectangleF(wx, wy, ww, bh);
            Theme.Fill(g, wr, Theme.S(5), Color.FromArgb(235, Theme.Raised));
            TextRenderer.DrawText(g, who, Theme.Small, Rectangle.Round(wr), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void Line(Graphics g, List<CoreCurve.Point> pts, Func<float, float> X, Func<float, float> Y, Color col, float width, bool dotted, bool knobs, string who)
        {
            var src = pts.OrderBy(q => q.Ghz).ToList();
            var p = src.Select(q => new PointF(X(q.Ghz), Y(q.Volt))).ToArray();
            if (p.Length > 1) using (var pen = new Pen(col, width) { DashStyle = dotted ? DashStyle.Dot : DashStyle.Solid, LineJoin = LineJoin.Round }) g.DrawLines(pen, p);
            var mine = new List<Hit>(); lines.Add(mine);
            for (int i = 0; i < p.Length; i++)
            {
                float near = 0f;
                if (knobs && mouseIn)
                {
                    float dx = p[i].X - mouse.X, dy = p[i].Y - mouse.Y, reach = Theme.S(70);
                    near = Math.Max(0f, 1f - (float)Math.Sqrt(dx * dx + dy * dy) / reach);
                }
                float kd = Theme.S(12) * near;
                if (kd > Theme.S(5)) Theme.GlassKnob(g, p[i].X, p[i].Y, kd, 1f, col, Theme.S(4f) * near, false, 180f, new PointF?[] { i > 0 ? p[i - 1] : (PointF?)null, i < p.Length - 1 ? p[i + 1] : (PointF?)null });
                else { float r = Theme.S(2.2f); using (var b = new SolidBrush(col)) g.FillEllipse(b, p[i].X - r, p[i].Y - r, 2 * r, 2 * r); }
                var hit = new Hit { At = p[i], Ghz = src[i].Ghz, Volt = src[i].Volt, Col = Color.FromArgb(255, col), Who = who, Key = who + "|" + src[i].Ghz.ToString("0.000") };
                hits.Add(hit); mine.Add(hit);
            }
        }
    }

    // 16.11: the switches under «Кривые всех ядер»: per chiplet «CCD1» (all of its cores on or off) and a button per core
    // as a round lamp button (Theme.LensButton) with its name over it, like the lit buttons of studio gear: the lamp burns
    // red while its core is shown and is dark when the core is switched off (the light fades in and out); a core
    // switched off leaves the graph
    class CoreToggles : Painted
    {
        readonly CoreCurveGraph graph;
        readonly List<Tuple<RectangleF, int, int>> items = new List<Tuple<RectangleF, int, int>>(); // place, chip, core (−1: the chip button)
        int hot = -2, hotChip = -1;
        // the lamps burn in Red Tune's red; CCD2 a shade paler, as its lines are
        static readonly Color Red = Color.FromArgb(0xe2, 0x30, 0x38);
        static Color ChipCol(int chip) { return chip == 0 ? Red : Color.FromArgb(0xee, 0x5c, 0x62); }
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        // the press: the button under the held mouse goes in fast and comes back slower when let go
        readonly Dictionary<int, float> push = new Dictionary<int, float>();
        int held = int.MinValue; // the key of the button held down
        readonly Dictionary<int, float> glow = new Dictionary<int, float>(); // how lit each button is now, by chip * 1000 + core + 1
        readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };

        public CoreToggles(CoreCurveGraph graph)
        {
            this.graph = graph; BackColor = Theme.Window; Height = Theme.S(62); Cursor = Cursors.Hand;
            fade.Tick += (s, e) =>
            {
                bool moving = false;
                // by the time passed, not by the tick: timer messages wait while the graph repaints under the mouse
                float step = Math.Min(1f, clock.ElapsedMilliseconds / 130f); clock.Restart();
                foreach (var it in items)
                {
                    int key = it.Item2 * 1000 + it.Item3 + 1;
                    float want = Want(it), now = glow.ContainsKey(key) ? glow[key] : want;
                    if (Math.Abs(want - now) > 0.01f) { now += Math.Sign(want - now) * Math.Min(step, Math.Abs(want - now)); moving = true; } else now = want;
                    glow[key] = now;
                    float pw = key == held ? 1f : 0f, pn = push.ContainsKey(key) ? push[key] : 0f;
                    if (Math.Abs(pw - pn) > 0.01f) { pn += Math.Sign(pw - pn) * Math.Min(step * (pw > pn ? 2.6f : 1.1f), Math.Abs(pw - pn)); moving = true; } else pn = pw;
                    push[key] = pn;
                }
                if (!moving) fade.Stop();
                Invalidate(); Update();
            };
        }

        // lit when the core is shown; a dark button under the mouse gets a little light
        float Want(Tuple<RectangleF, int, int> it) { return On(it.Item2, it.Item3) ? 1f : hot == it.Item3 && hotChip == it.Item2 ? 0.22f : 0f; }

        protected override void Dispose(bool disposing) { if (disposing) fade.Dispose(); base.Dispose(disposing); }

        void Build(Graphics g)
        {
            items.Clear();
            // a column per button: the name on top, the lamp under it; the columns are wide enough for the lamps' halos
            float x = Theme.S(2), h = Height, cw = Theme.S(40);
            for (int chip = 0; chip < Hw.CurveChips; chip++)
            {
                string name = Hw.CurveChips > 1 ? "CCD" + (chip + 1) : L.T("Все");
                float w = Math.Max(cw, TextRenderer.MeasureText(g, name, Theme.Small).Width + Theme.S(10));
                items.Add(Tuple.Create(new RectangleF(x, 0, w, h), chip, -1)); x += w;
                foreach (var c in Hw.ChipCores(chip)) { items.Add(Tuple.Create(new RectangleF(x, 0, cw, h), chip, c)); x += cw; }
                x += Theme.S(18);
            }
        }

        bool On(int chip, int core) { return core >= 0 ? !graph.Hidden.Contains(core) : Hw.ChipCores(chip).Any(c => !graph.Hidden.Contains(c)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Build(g);
            foreach (var it in items)
            {
                bool on = On(it.Item2, it.Item3);
                int key = it.Item2 * 1000 + it.Item3 + 1;
                if (!glow.ContainsKey(key)) glow[key] = Want(it);
                var r = it.Item1;
                float d = Theme.S(28), top = Theme.S(18);
                Theme.LensButton(g, r.X + r.Width / 2f, top + Theme.S(6) + d / 2f, d, glow[key], ChipCol(it.Item2), push.ContainsKey(key) ? push[key] : 0f);
                string text = it.Item3 < 0 ? (Hw.CurveChips > 1 ? "CCD" + (it.Item2 + 1) : L.T("Все")) : (it.Item3 - Hw.ChipCores(it.Item2)[0] + 1).ToString();
                TextRenderer.DrawText(g, text, Theme.Small, new Rectangle((int)r.X, 0, (int)r.Width, (int)top), on ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        Tuple<RectangleF, int, int> At(System.Drawing.Point p) { return items.FirstOrDefault(it => it.Item1.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var it = At(e.Location);
            int h = it == null ? -2 : it.Item3, hc = it == null ? -1 : it.Item2;
            if (h != hot || hc != hotChip) { hot = h; hotChip = hc; clock.Restart(); fade.Start(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -2; hotChip = -1; clock.Restart(); fade.Start(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var it = At(e.Location);
            if (it == null || e.Button != MouseButtons.Left) return;
            held = it.Item2 * 1000 + it.Item3 + 1;
            // most of the way in at once, so that a quick click shows too
            push[held] = Math.Max(push.ContainsKey(held) ? push[held] : 0f, 0.75f);
            Invalidate();
            clock.Restart(); fade.Start();
        }

        // let go over the same button: it switches (the lamp changes while the button comes back up)
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var it = At(e.Location);
            bool same = it != null && held == it.Item2 * 1000 + it.Item3 + 1;
            held = int.MinValue;
            clock.Restart(); fade.Start();
            if (!same || e.Button != MouseButtons.Left) return;
            if (it.Item3 >= 0) { if (!graph.Hidden.Remove(it.Item3)) graph.Hidden.Add(it.Item3); }
            else
            {
                // the chip button: everything of the chip on, or (when all of it is on) everything off
                var cores = Hw.ChipCores(it.Item2);
                bool allOn = cores.All(c => !graph.Hidden.Contains(c));
                foreach (var c in cores) if (allOn) graph.Hidden.Add(c); else graph.Hidden.Remove(c);
            }
            clock.Restart(); fade.Start(); graph.Invalidate();
        }
    }

    // 14.11: «Все ядра» on «Кривая»: every core's curve on one graph, and a measurement of all of them in a row (one core at
    // a time, the same measurement as a core's own window: about 15 s each)
    class AllCoresForm : DarkForm
    {
        readonly Func<int[]> coNow; // every core's value on the processor now (null: not read yet)
        readonly CoreCurveGraph graph = new CoreCurveGraph { Overview = true };
        readonly CoreToggles toggles;
        readonly FlatButton measure = new FlatButton("Измерить все", true), close = new FlatButton("Закрыть");
        readonly Label note = new Label();
        bool closing, running, stopAll;
        int at, failed;
        int[] co;

        public AllCoresForm(Func<int[]> coNow)
        {
            this.coNow = coNow;
            Text = L.T("Кривые всех ядер");
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(Theme.S(820), Theme.S(560));
            MinimumSize = new Size(Theme.S(580), Theme.S(440));
            note.ForeColor = Theme.Text2; note.Font = Theme.Small; note.BackColor = Theme.Window;
            measure.BackColor = close.BackColor = Theme.Window;
            toggles = new CoreToggles(graph);
            Controls.Add(graph); Controls.Add(toggles); Controls.Add(note); Controls.Add(measure); Controls.Add(close);
            graph.CoOf = c => { var v = coNow(); return v != null && c < v.Length ? v[c] : (int?)null; };
            measure.Click += (s, e) => { if (running) { stopAll = true; CoreCurve.Cancel(); } else Start(); };
            close.Click += (s, e) => Close();
            Show0();
        }

        void Show0()
        {
            graph.Runs = CoreCurve.Load();
            graph.LivePoints = null;
            var why = CoreCurve.WhyNot();
            int have = graph.Runs.Select(r => r.Core).Distinct().Count();
            graph.Empty = why ?? "Ни одно ядро ещё не измерено. «Измерить все» снимет кривые всех ядер по очереди";
            measure.Enabled = why == null && coNow() != null;
            measure.Text = "Измерить все";
            Note(why ?? (have == 0 ? "Замер всех ядер по очереди займёт около " + Math.Max(1, (int)Math.Round(Hw.CurveCores * 32 / 60.0)) + " мин; на это время частота ступенями ограничивается"
                : "Измерено ядер: " + have + " из " + Hw.CurveCores));
            graph.Invalidate();
        }

        void Note(string text) { note.Text = L.T(text); }

        void Start()
        {
            co = coNow();
            if (co == null) return;
            running = true; stopAll = false; at = 0; failed = 0;
            measure.Text = "Остановить"; close.Enabled = false;
            Next();
        }

        void Next()
        {
            if (closing || IsDisposed) return;
            if (stopAll || at >= Hw.CurveCores)
            {
                running = false; close.Enabled = true;
                bool stopped = stopAll;
                Show0();
                if (stopped) Note("Замер остановлен");
                else if (failed > 0) Note("Готово, но не измерено ядер: " + failed + " (подробности в журнале)");
                else Note("Готово: измерены все ядра");
                return;
            }
            int c = at;
            graph.Runs = CoreCurve.Load().Where(r => r.Core != c).ToList(); // the one being measured shows live, not its old line
            graph.LivePoints = new List<CoreCurve.Point>(); graph.Invalidate();
            CoreCurve.Measure(c, co[c],
                (part, pts) =>
                {
                    if (closing || IsDisposed) return;
                    graph.LivePoints = pts; graph.Invalidate();
                    Note(Hw.CoreName(c) + " · " + (c + 1) + " из " + Hw.CurveCores + " · " + (int)(part * 100) + " %");
                },
                (run, err) =>
                {
                    if (closing || IsDisposed) return;
                    if (err != null && !stopAll) { failed++; Store.Log("кривая ядра " + Hw.CoreName(c) + ": " + err); }
                    at++;
                    Next();
                }, 200);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running) { stopAll = true; CoreCurve.Cancel(); } // the plan's values go back in the measurement's own finally
            closing = true;
            base.OnFormClosing(e);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (toggles == null) return; // a layout from the base constructor
            int m = Theme.S(16), bh = measure.Height, nh = Theme.S(22), th = toggles.Height;
            graph.Bounds = new Rectangle(m, m, ClientSize.Width - 2 * m, ClientSize.Height - 3 * m - bh - nh - th - Theme.S(16));
            toggles.Bounds = new Rectangle(m, graph.Bottom + Theme.S(10), ClientSize.Width - 2 * m, th);
            note.Bounds = new Rectangle(m, toggles.Bottom + Theme.S(6), ClientSize.Width - 2 * m, nh);
            note.TextAlign = ContentAlignment.MiddleLeft; note.AutoEllipsis = true;
            close.Location = new System.Drawing.Point(ClientSize.Width - m - close.Width, ClientSize.Height - m - bh);
            measure.Location = new System.Drawing.Point(close.Left - Theme.S(8) - measure.Width, close.Top);
        }
    }

    // 16.11: the lamps under a core's graph (the same lit buttons as under «Кривые всех ядер»): which of the core's curves
    // are drawn. «Сейчас»: the one measured at the value the core has now, «При 0»: the factory curve, «Прошлые»: the
    // earlier measurements at other values. A lamp with no curve behind it stays dark and does nothing.
    class RunToggles : Painted
    {
        readonly CoreCurveGraph graph;
        static readonly string[] Names = { "Сейчас", "При 0", "Прошлые" };
        // the colours the lines have on the graph: red, the amber of the dotted factory line, the dim grey of old runs
        static readonly Color[] Cols = { Color.FromArgb(0xe2, 0x30, 0x38), CoreCurveGraph.ZeroCol, Color.FromArgb(0x96, 0x92, 0x8e) };
        readonly RectangleF[] place = new RectangleF[3];
        readonly float[] glow = { -1f, -1f, -1f }, push = new float[3];
        int hot = -1, held = -1;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };

        public RunToggles(CoreCurveGraph graph)
        {
            this.graph = graph; BackColor = Theme.Window; Height = Theme.S(62);
            fade.Tick += (s, e) =>
            {
                bool moving = false;
                // by the time passed, not by the tick (as CoreToggles): timer messages wait while the graph repaints
                float step = Math.Min(1f, clock.ElapsedMilliseconds / 130f); clock.Restart();
                for (int k = 0; k < 3; k++)
                {
                    float want = Want(k), now = glow[k] < 0 ? want : glow[k];
                    if (Math.Abs(want - now) > 0.01f) { now += Math.Sign(want - now) * Math.Min(step, Math.Abs(want - now)); moving = true; } else now = want;
                    glow[k] = now;
                    float pw = k == held ? 1f : 0f, pn = push[k];
                    if (Math.Abs(pw - pn) > 0.01f) { pn += Math.Sign(pw - pn) * Math.Min(step * (pw > pn ? 2.6f : 1.1f), Math.Abs(pw - pn)); moving = true; } else pn = pw;
                    push[k] = pn;
                }
                if (!moving) fade.Stop();
                Invalidate(); Update();
            };
        }

        protected override void Dispose(bool disposing) { if (disposing) fade.Dispose(); base.Dispose(disposing); }

        // «При 0» also burns while the factory curve is being measured (its old run has given way to the live line)
        bool Has(int k) { return graph.Runs.Any(r => graph.Kind(r) == k) || (k == 1 && graph.LiveCol != null && graph.LivePoints != null); }
        bool On(int k) { return Has(k) && !graph.HiddenKinds.Contains(k); }
        // lit while its curve is drawn; a dark lamp that has a curve gets a little light under the mouse
        float Want(int k) { return On(k) ? 1f : hot == k && Has(k) ? 0.22f : 0f; }

        // the graph got other runs (a measurement ended, another one began): the lamps follow
        public void Sync() { clock.Restart(); fade.Start(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = Theme.S(2), d = Theme.S(28), top = Theme.S(18);
            // 16.11: one width for all three, the widest name's: the lamps stand at equal distances (each column used to
            // be as wide as its own name, so «Прошлые» stood further from «При 0» than «При 0» from «Сейчас»)
            float w = Theme.S(64);
            foreach (var n in Names) w = Math.Max(w, TextRenderer.MeasureText(g, L.T(n), Theme.Small).Width + Theme.S(16));
            for (int k = 0; k < 3; k++)
            {
                string name = L.T(Names[k]);
                place[k] = new RectangleF(x, 0, w, Height); x += w;
                if (glow[k] < 0) glow[k] = Want(k);
                Theme.LensButton(g, place[k].X + w / 2f, top + Theme.S(6) + d / 2f, d, glow[k], Cols[k], push[k]);
                TextRenderer.DrawText(g, name, Theme.Small, new Rectangle((int)place[k].X, 0, (int)w, (int)top), On(k) ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        int At(System.Drawing.Point p) { for (int k = 0; k < 3; k++) if (place[k].Contains(p)) return k; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = At(e.Location);
            Cursor = h >= 0 && Has(h) ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; clock.Restart(); fade.Start(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; clock.Restart(); fade.Start(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int k = At(e.Location);
            if (k < 0 || !Has(k) || e.Button != MouseButtons.Left) return;
            held = k;
            push[k] = Math.Max(push[k], 0.75f); // most of the way in at once, so that a quick click shows too
            Invalidate();
            clock.Restart(); fade.Start();
        }

        // let go over the same lamp: its curve is shown or hidden
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int k = At(e.Location);
            bool same = k >= 0 && k == held;
            held = -1;
            clock.Restart(); fade.Start();
            if (!same || e.Button != MouseButtons.Left) return;
            if (!graph.HiddenKinds.Remove(k)) graph.HiddenKinds.Add(k);
            graph.Invalidate();
        }
    }

    // the window that opens from a core's button on «Кривая»
    class CoreCurveForm : DarkForm
    {
        readonly int core;
        readonly Func<int?> coNow; // the core's value on the processor now (null: not read yet)
        // 16.11: two measurements, a button each. «Измерить»: at the value the core has now. «Измерить при 0»: the
        // factory curve to compare with; for it the core is set to 0 (allNow: every core's value, write: a whole set to
        // the processor, then whether it took) and gets its value back when the measurement ends, whatever ends it: done,
        // stopped, the window closed. If the curve was applied by something else meanwhile (a power mode, the charger),
        // the run at 0 is dropped and nothing is put back over the newer set.
        readonly Func<int[]> allNow;
        readonly Action<int[], string, Action<bool>> write;
        int[] original, zeroSet;
        bool running, stopped;
        readonly CoreCurveGraph graph = new CoreCurveGraph();
        readonly RunToggles toggles;
        readonly FlatButton measure = new FlatButton("Измерить", true), measureZero = new FlatButton("Измерить при 0"), close = new FlatButton("Закрыть");
        FlatButton active; // the button whose measurement runs: it reads «Остановить»
        readonly Label note = new Label();
        bool closing;

        public CoreCurveForm(int core, Func<int?> coNow, Func<int[]> allNow = null, Action<int[], string, Action<bool>> write = null)
        {
            this.core = core; this.coNow = coNow; this.allNow = allNow; this.write = write;
            Text = L.T("Кривая ядра") + " · " + Hw.CoreName(core);
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(Theme.S(760), Theme.S(590));
            MinimumSize = new Size(Theme.S(620), Theme.S(490));
            note.ForeColor = Theme.Text2; note.Font = Theme.Small; note.BackColor = Theme.Window;
            measure.BackColor = measureZero.BackColor = close.BackColor = Theme.Window;
            toggles = new RunToggles(graph);
            Controls.Add(graph); Controls.Add(toggles); Controls.Add(note); Controls.Add(measureZero); Controls.Add(measure); Controls.Add(close);
            EventHandler stop = (s, e) => { stopped = true; if (CoreCurve.Measuring) CoreCurve.Cancel(); };
            measure.Click += (s, e) => { if (running) stop(s, e); else Start(false); };
            measureZero.Click += (s, e) => { if (running) stop(s, e); else Start(true); };
            close.Click += (s, e) => Close();
            Show0();
        }

        static string Sign(int v) { return (v > 0 ? "+" : "") + v; }

        void Show0()
        {
            graph.Runs = CoreCurve.Load().Where(r => r.Core == core).ToList();
            graph.NowCo = coNow() ?? int.MinValue;
            graph.LivePoints = null;
            var why = CoreCurve.WhyNot();
            graph.Empty = why ?? "Кривая этого ядра ещё не измерена. «Измерить» нагрузит ядро примерно на минуту и снимет напряжение на разных частотах";
            var co = coNow();
            measure.Enabled = why == null && co.HasValue;
            // at 0 already: «Измерить» is the measurement at 0
            measureZero.Enabled = why == null && co.HasValue && co.Value != 0 && write != null && allNow != null;
            measure.Text = "Измерить"; measureZero.Text = "Измерить при 0";
            active = null;
            Note(why ?? (!measureZero.Enabled ? "На время замера частота процессора ступенями ограничивается, потом всё возвращается" :
                "«Измерить» — при текущем смещении. «Измерить при 0» на минуту ставит ядру 0 и потом возвращает смещение"));
            graph.Invalidate(); toggles.Sync();
        }

        void Note(string text) { note.Text = L.T(text); }

        void Start(bool zero)
        {
            var co = coNow();
            if (!co.HasValue) return;
            running = true; stopped = false;
            active = zero ? measureZero : measure;
            active.Text = "Остановить"; (zero ? measure : measureZero).Enabled = false; close.Enabled = false;
            if (zero) ZeroPass(co.Value);
            else Pass(co.Value, co.Value, err => { if (!closing && !IsDisposed) Finish(err); });
        }

        // one measurement, drawn as it goes; then: null, or why it failed
        void Pass(int co, int nowCo, Action<string> then)
        {
            string label = "Замер при " + Sign(co) + ": ";
            Note(label + "0 %");
            graph.Runs = CoreCurve.Load().Where(r => r.Core == core && r.Co != co).ToList(); // its old line gives way to the live one
            graph.LiveCol = co == 0 && nowCo != 0 ? CoreCurveGraph.ZeroCol : (Color?)null;
            graph.NowCo = nowCo; graph.LivePoints = new List<CoreCurve.Point>(); graph.Invalidate(); toggles.Sync();
            CoreCurve.Measure(core, co,
                (part, pts) => { if (closing || IsDisposed) return; graph.LivePoints = pts; graph.Invalidate(); Note(label + (int)(part * 100) + " %"); },
                (run, err) => then(err), 100);
        }

        // the factory curve: the core at 0, every other core as it is
        void ZeroPass(int co)
        {
            var all = allNow != null ? allNow() : null;
            if (write == null || all == null || core >= all.Length) { Finish(null); return; }
            if (RedTune.Ram.MemTest.Busy != null && RedTune.Ram.MemTest.Busy()) { Finish("Идёт автотюн: замер при 0 сейчас не запускается"); return; }
            original = all; zeroSet = (int[])all.Clone(); zeroSet[core] = 0;
            Note("Ставлю ядру 0 для замера…");
            write(zeroSet, Hw.CoreName(core) + " → 0", ok =>
            {
                if (!ok)
                {
                    // a write that did not take whole may still have moved something: the set from before goes back
                    const string msg = "Не удалось поставить ядру 0: замер не начат";
                    var back = original; original = null;
                    var cur = allNow();
                    if (cur != null && !cur.SequenceEqual(back)) write(back, Hw.CoreName(core) + " → " + Sign(back[core]), _ => Finish(msg)); else Finish(msg);
                    return;
                }
                if (closing || IsDisposed || stopped) { Restore(stopped ? "Замер остановлен" : null); return; }
                Pass(0, co, err =>
                {
                    // the core has to be at 0 to the end: a set applied meanwhile (a power mode, the charger) took it away
                    var now = allNow();
                    if (err == null && (now == null || !now.SequenceEqual(zeroSet)))
                    {
                        CoreCurve.Forget(core, 0);
                        err = "Во время замера при 0 кривую применили заново: этот замер не сохранён";
                    }
                    Restore(err);
                });
            });
        }

        // the core's value back; not over a set someone else has applied since
        void Restore(string err)
        {
            var back = original; original = null;
            var now = allNow();
            if (back == null || now == null || !now.SequenceEqual(zeroSet)) { Finish(err); return; }
            if (!closing && !IsDisposed) Note("Возвращаю смещение " + Sign(back[core]) + "…");
            write(back, Hw.CoreName(core) + " → " + Sign(back[core]), ok => Finish(ok ? err : "Смещение ядра не вернулось: примените пресет на странице «Кривая»"));
        }

        void Finish(string err)
        {
            running = false;
            if (closing || IsDisposed) return;
            close.Enabled = true;
            Show0();
            if (err != null) Note(err);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // the plan's values go back in the measurement's own finally; a core set to 0 gets its value back in Restore,
            // which the measurement's end calls also when the window is gone
            stopped = true;
            if (CoreCurve.Measuring) CoreCurve.Cancel();
            closing = true;
            base.OnFormClosing(e);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (toggles == null) return; // a layout from the base constructor
            int m = Theme.S(16), bh = measure.Height, nh = Theme.S(22), th = toggles.Height;
            graph.Bounds = new Rectangle(m, m, ClientSize.Width - 2 * m, ClientSize.Height - 3 * m - bh - nh - th - Theme.S(16));
            toggles.Bounds = new Rectangle(m, graph.Bottom + Theme.S(10), ClientSize.Width - 2 * m, th);
            note.Bounds = new Rectangle(m, toggles.Bottom + Theme.S(6), ClientSize.Width - 2 * m, nh);
            note.TextAlign = ContentAlignment.MiddleLeft; note.AutoEllipsis = true;
            close.Location = new System.Drawing.Point(ClientSize.Width - m - close.Width, ClientSize.Height - m - bh);
            measure.Location = new System.Drawing.Point(close.Left - Theme.S(8) - measure.Width, close.Top);
            measureZero.Location = new System.Drawing.Point(measure.Left - Theme.S(8) - measureZero.Width, close.Top);
        }
    }
}
