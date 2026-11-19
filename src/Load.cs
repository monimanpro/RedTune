using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace RedTune
{
    // Test loads for the auto-tune, the same ones the 2026-11 tuning used:
    //  - the heavy load: Prime95, or the program's own test (Stress.cs) while it is on trial beside it (Load.Heavy)
    //  - Prime95: one instance per physical core on both its threads (AVX2 small FFT = most current, SSE large FFT = L3)
    //  - idle bursts: short loads after idle on one core (the transitions that crashed the laptop in light use)
    //  - storm: random bursts on many cores at once (a pile of browser tabs)
    static class Load
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        [DllImport("kernel32.dll")] static extern bool IsProcessorFeaturePresent(uint feature);
        public static bool HasAvx512 { get { try { return IsProcessorFeaturePresent(41); } catch { return false; } } } // PF_AVX512F_INSTRUCTIONS_AVAILABLE
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        // next to the program (tools\p95); otherwise the folder picked on the Components page (P95Path)
        public static readonly string[] P95Defaults = { Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), "tools", "p95") };
        static readonly Regex P95Error = new Regex(@"FATAL ERROR|Hardware failure|ILLEGAL SUMOUT|SUM\(INPUTS\)|Rounding was|Possible hardware", RegexOptions.Compiled);

        public static string P95Dir(Settings s)
        {
            if (!string.IsNullOrEmpty(s.P95Path) && File.Exists(Path.Combine(s.P95Path, "prime95.exe"))) return s.P95Path;
            return P95Defaults.FirstOrDefault(d => File.Exists(Path.Combine(d, "prime95.exe")));
        }

        // ---------- the heavy load of the auto-tune: Prime95, or the program's own test (Stress) while it is on trial ----------

        public interface IHeavy : IDisposable
        {
            string Name { get; }
            List<int> Failed();     // the cores whose load reported a wrong result or died
        }

        // the own test is picked on the Components page and can run here; otherwise Prime95
        public static bool OwnLoad(Settings s) { return s.TuneLoad == "own" && Stress.Available; }

        public static IHeavy Heavy(Settings s, IEnumerable<int> cores, bool avx, bool avx512 = false, bool oneThread = false)
        {
            if (OwnLoad(s)) return new Stress.Run(cores, avx, avx512, oneThread);
            return new P95Run(s, cores, avx, avx512, oneThread);
        }

        // ---------- Prime95 ----------

        public class P95Run : IHeavy
        {
            readonly Dictionary<int, Process> procs = new Dictionary<int, Process>();
            readonly Dictionary<int, string> dirs = new Dictionary<int, string>();
            public string Name { get { return "Prime95"; } }

            // avx512: with AVX-512 (Zen 4 and later; another load on the core than AVX2). oneThread: one logical processor of
            // the core instead of both, so the core reaches its top boost clock (14.11, the long confirmation)
            public P95Run(Settings s, IEnumerable<int> cores, bool avx, bool avx512 = false, bool oneThread = false)
            {
                StopAll();
                // RT_P95_FFT=min-max (in K): another range of transform sizes than the two the auto-tune uses; RT_P95_SSE=1: no AVX
                // at all, whatever the caller asked for. For measurements of what in Prime95 finds a weak core (17.11).
                int fftMin = 0, fftMax = 0; var range = (Environment.GetEnvironmentVariable("RT_P95_FFT") ?? "").Split('-');
                if (range.Length == 2) { int.TryParse(range[0], out fftMin); int.TryParse(range[1], out fftMax); }
                if (Environment.GetEnvironmentVariable("RT_P95_SSE") == "1") { avx = false; avx512 = false; }
                var src = P95Dir(s) ?? throw new Exception("Prime95 не найден: укажите его во вкладке «Компоненты»");
                foreach (var c in cores)
                {
                    var dir = Path.Combine(Store.DataDir, "p95", "core" + c);
                    var exe = Path.Combine(dir, "prime95.exe");
                    if (!File.Exists(exe) || new FileInfo(exe).Length != new FileInfo(Path.Combine(src, "prime95.exe")).Length)
                    {
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        Directory.CreateDirectory(dir);
                        foreach (var f in Directory.GetFiles(src))
                        {
                            var n = Path.GetFileName(f).ToLowerInvariant();
                            if (n != "prime.txt" && n != "local.txt" && n != "results.txt") File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                        }
                    }
                    foreach (var n in new[] { "results.txt", "local.txt" }) { var f = Path.Combine(dir, n); if (File.Exists(f)) File.Delete(f); }
                    File.WriteAllText(Path.Combine(dir, "prime.txt"), string.Join("\r\n", new[]
                    {
                        "CpuSupportsSSE=1", "CpuSupportsSSE2=1", "CpuSupportsAVX=" + (avx ? 1 : 0), "CpuSupportsAVX2=" + (avx && Environment.GetEnvironmentVariable("RT_P95_NOFMA") != "1" ? 1 : 0), "CpuSupportsFMA3=" + (avx && Environment.GetEnvironmentVariable("RT_P95_NOFMA") != "1" ? 1 : 0),
                        "CpuSupportsAVX512F=" + (avx512 ? 1 : 0), "NumWorkers=1", "NumCores=1", "CoresPerTest=1", "TortureHyperthreading=" + (oneThread ? 0 : 1), "TortureMem=0", "TortureTime=1",
                        "MinTortureFFT=" + (fftMin > 0 ? fftMin : avx ? 36 : 448), "MaxTortureFFT=" + (fftMax > 0 ? fftMax : avx ? 248 : 4096), "TortureWeak=1048576", "EnableSetAffinity=0", "EnableSetPriority=0",
                        "StressTester=1", "UsePrimenet=0", "", "[Internals]", "V30OptionsConverted=1", "", "[Windows]", "ExitOnX=1", "",
                    }));
                    var p = Process.Start(new ProcessStartInfo(exe, "-t") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                    int tpc = Hw.ThreadsPerCore; p.ProcessorAffinity = (IntPtr)(((1L << (oneThread ? 1 : tpc)) - 1) << (tpc * c)); // all threads of core c, or its first one
                    procs[c] = p; dirs[c] = dir;
                }
            }

            // cores whose Prime95 reported an error or died
            public List<int> Failed()
            {
                var bad = new List<int>();
                foreach (var kv in procs)
                {
                    bool dead; try { dead = kv.Value.HasExited; } catch { dead = true; }
                    var res = Path.Combine(dirs[kv.Key], "results.txt");
                    bool err = false;
                    try { err = File.Exists(res) && P95Error.IsMatch(File.ReadAllText(res)); } catch { }
                    if (dead || err) bad.Add(kv.Key);
                }
                return bad;
            }

            public void Dispose() { foreach (var p in procs.Values) try { if (!p.HasExited) p.Kill(); } catch { } StopAll(); }
        }

        public static void StopAll() { Stress.StopAll(); foreach (var p in Process.GetProcessesByName("prime95")) try { if ((p.MainModule.FileName ?? "").StartsWith(Store.DataDir, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { } }

        // ---------- in-process loads: random walks through 24 MB (bigger than one CCD's 32 MB L3 share per core) ----------

        static readonly int[] table = BuildTable();

        static int[] BuildTable()
        {
            var t = new int[6 * 1024 * 1024]; // 24 MB
            var rnd = new Random(7);
            for (int i = 0; i < t.Length; i++) t[i] = i;
            for (int i = t.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); int x = t[i]; t[i] = t[j]; t[j] = x; }
            return t;
        }

        static int Work(int ms, int seed)
        {
            var end = Environment.TickCount + ms;
            int a = seed & 0xFFFFF, b = (seed * 7) & 0xFFFFF, c = (seed * 13) & 0xFFFFF, d = (seed * 31) & 0xFFFFF;
            double f = 1.0001;
            while (Environment.TickCount < end)
                for (int k = 0; k < 4000; k++) { a = table[a]; b = table[b]; c = table[c]; d = table[d]; f = f * 1.0000001 + (a & 7); }
            return a + b + c + d + (int)f;
        }

        static void Pin(int logical) { SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1UL << logical)); }

        // one core (both threads): idle 0.2-3 s, then a 20-400 ms burst, until cancelled
        public static Thread IdleBursts(int core, CancellationToken stop)
        {
            var t = new Thread(() =>
            {
                var rnd = new Random(core * 97 + Environment.TickCount);
                var helper = new Thread(() => { if (Hw.ThreadsPerCore > 1) Pin(core * Hw.ThreadsPerCore + 1); var r2 = new Random(core); while (!stop.IsCancellationRequested) { Thread.Sleep(r2.Next(200, 3000)); if (!stop.IsCancellationRequested) Work(r2.Next(20, 400), r2.Next()); } }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                helper.Start();
                Pin(core * Hw.ThreadsPerCore);
                while (!stop.IsCancellationRequested) { Thread.Sleep(rnd.Next(200, 3000)); if (!stop.IsCancellationRequested) Work(rnd.Next(20, 400), rnd.Next()); }
                helper.Join();
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            t.Start();
            return t;
        }

// 14.11: a load like a video playing in a browser in the background, nothing played or opened: the laptop reset while a
        // video ran in Chrome on CCD2. It is many small wake-ups, not much work: a sound thread every 10 ms for a fraction of a
        // millisecond, a frame every 16.7 ms (decode, composite: 1-5 ms), now and then a key frame or a page script
        // (20-80 ms), the threads hopping between the cores of the set as Windows moves them, with the timer at 1 ms as
        // browsers set it. The cores sit at middle clocks and keep waking from sleep: where a Curve Optimizer value with
        // little margin gives way.
        public static void BrowserVideo(int[] cores, int seconds, CancellationToken stop)
        {
            if (cores.Length == 0) return;
            timeBeginPeriod(1);
            try
            {
                var end = DateTime.Now.AddSeconds(seconds);
                var threads = new List<Thread>();
                // period ms, work ms min-max, chance of a long burst (‰), long burst ms min-max
                var kinds = new[]
                {
                    new[] { 10.0, 0.15, 0.6, 2, 3, 8 },      // sound
                    new[] { 16.67, 1.0, 4.0, 15, 20, 60 },   // decode
                    new[] { 16.67, 0.8, 3.0, 8, 15, 40 },    // composite
                    new[] { 33.3, 0.5, 2.0, 30, 30, 80 },    // page scripts, layout
                };
                for (int k = 0; k < kinds.Length; k++)
                {
                    var kd = kinds[k]; int id = k;
                    var th = new Thread(() =>
                    {
                        var rnd = new Random(id * 7919 + Environment.TickCount);
                        var sw = Stopwatch.StartNew();
                        double next = 0; long move = 0;
                        while (!stop.IsCancellationRequested && DateTime.Now < end)
                        {
                            if (sw.ElapsedMilliseconds >= move)
                            {
                                int c = cores[rnd.Next(cores.Length)];
                                Pin(c * Hw.ThreadsPerCore + rnd.Next(Hw.ThreadsPerCore));
                                move = sw.ElapsedMilliseconds + rnd.Next(1500, 6000);
                            }
                            double ms = rnd.Next(1000) < kd[3] ? kd[4] + rnd.NextDouble() * (kd[5] - kd[4]) : kd[1] + rnd.NextDouble() * (kd[2] - kd[1]);
                            Spin(sw, ms, rnd.Next());
                            next += kd[0];
                            double wait = next - sw.Elapsed.TotalMilliseconds;
                            if (wait >= 1) Thread.Sleep((int)wait); else if (wait < -100) next = sw.Elapsed.TotalMilliseconds; // fell behind: catch up, as a player drops frames
                        }
                    }) { IsBackground = true, Priority = id == 0 ? ThreadPriority.Highest : ThreadPriority.AboveNormal };
                    th.Start(); threads.Add(th);
                }
                foreach (var th in threads) th.Join();
            }
            finally { timeEndPeriod(1); }
        }

        // short work measured by the stopwatch (TickCount steps by 15.6 ms, too coarse for a 1 ms frame)
        static int Spin(Stopwatch sw, double ms, int seed)
        {
            double until = sw.Elapsed.TotalMilliseconds + ms;
            int a = seed & 0xFFFFF, b = (seed * 7) & 0xFFFFF;
            while (sw.Elapsed.TotalMilliseconds < until)
                for (int k = 0; k < 500; k++) { a = table[a]; b = table[b]; }
            return a + b;
        }

        // 14.11: the pattern that reset the laptop during a core-curve measurement: one core fully busy, the clock cap moved
        // down in steps (5.4 GHz to 1.6 GHz by 200 MHz, ~0.6 s each), every core of the set in turn. capSet: moves the cap.
        public static void ClockSteps(int[] cores, int seconds, Action<int> capSet, CancellationToken stop)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (!stop.IsCancellationRequested && DateTime.Now < end)
                foreach (int c in cores)
                {
                    if (stop.IsCancellationRequested || DateTime.Now >= end) return;
                    var halt = new ManualResetEventSlim(false);
                    var th = new Thread(() => { Pin(c * Hw.ThreadsPerCore); var sw = Stopwatch.StartNew(); while (!halt.IsSet) Spin(sw, 5, c); }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                    th.Start();
                    try
                    {
                        capSet(0); Thread.Sleep(800);
                        for (int mhz = 5400; mhz >= 1600 && !stop.IsCancellationRequested; mhz -= 200) { capSet(mhz); Thread.Sleep(600); }
                    }
                    finally { halt.Set(); th.Join(2000); capSet(0); }
                    Thread.Sleep(300);
                }
        }

        // storm on all logical CPUs: all at once 0.3-3 s, waves 5-20 ms apart, sometimes a heavy 5-15 s burst on every thread
        public static void Storm(int seconds, CancellationToken stop)
        {
            int n = Environment.ProcessorCount;
            var go = new ManualResetEvent[n];
            var burstMs = new int[n];
            var threads = new List<Thread>();
            bool quit = false;
            for (int i = 0; i < n; i++)
            {
                int id = i; go[i] = new ManualResetEvent(false);
                var th = new Thread(() => { Pin(id); while (true) { go[id].WaitOne(); if (quit) return; go[id].Reset(); Work(burstMs[id], id * 1009 + Environment.TickCount); } }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                th.Start(); threads.Add(th);
            }
            var rnd = new Random();
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end && !stop.IsCancellationRequested)
            {
                Thread.Sleep(rnd.Next(300, 2500));
                int pattern = rnd.Next(10);
                if (pattern < 6) { int ms = rnd.Next(300, 3000); for (int i = 0; i < n; i++) if (rnd.Next(10) < 7) { burstMs[i] = ms; go[i].Set(); } }
                else if (pattern < 9) { for (int i = 0; i < n; i++) { burstMs[i] = rnd.Next(100, 800); go[i].Set(); Thread.Sleep(rnd.Next(5, 20)); } }
                else { int ms = rnd.Next(5000, 15000); for (int i = 0; i < n; i++) { burstMs[i] = ms; go[i].Set(); } Thread.Sleep(ms); }
            }
            quit = true;
            foreach (var g in go) g.Set();
            foreach (var th in threads) th.Join(20000);
        }

        // ---------- WHEA (cores, L3) ----------

        // 16.11: WHEA 47 says «component: memory», but on this Ryzen it is a corrected machine check whose MCA bank tells what
        // erred. Bank 0–6 are the core's own (LS, IF, L2, DE, EX, FP): bank 0 status 9C20400002000185 is an ECC error on a
        // data cache read by a load — the core's L1, not RAM. 110 of them came from logical processor 8 (CCD1 Core 5) in the
        // all-core rounds of 16.11, 7 in a round the tune counted as passed, because 47 was skipped as «memory». Now a 47 from
        // a core bank fails the round and names the core; a 47 from another bank (memory controller) is still only logged.
        public class WheaHit { public string Text; public int Core = -1; public DateTime When; }

        static readonly Regex RawData = new Regex(@"<Data Name='RawData'>([0-9A-Fa-f]+)</Data>");

        public static List<WheaHit> WheaHits(DateTime since)
        {
            var list = new List<WheaHit>();
            try
            {
                var q = "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='" + since.ToUniversalTime().ToString("o") + "']]]";
                using (var r = new System.Diagnostics.Eventing.Reader.EventLogReader(new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName, q)))
                    for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
                        using (e)
                        {
                            var when = e.TimeCreated ?? DateTime.Now; var time = when.ToString("HH:mm:ss");
                            if (e.Id != 47) { list.Add(new WheaHit { Text = "WHEA " + e.Id + " " + time, When = when }); continue; }
                            int core = McaCore(e.ToXml());
                            if (core >= 0) list.Add(new WheaHit { Text = "WHEA 47 на " + Hw.CoreName(core) + " " + time, Core = core, When = when });
                        }
            }
            catch { }
            return list;
        }

        public static List<string> Whea(DateTime since) { return WheaHits(since).Select(h => h.Text).ToList(); }

        // the core of a WHEA 47 whose MCA bank is the core's own, else −1. The x64 MCA section (WHEA_XPF_MCA_SECTION) starts
        // with VersionNumber and CpuVendor (2 = AMD), then Timestamp (8), ProcessorNumber (4), GlobalStatus (8),
        // InstructionPointer (8), BankNumber (4), Status (8)
        static int McaCore(string xml)
        {
            var m = RawData.Match(xml);
            if (!m.Success) return -1;
            var hex = m.Groups[1].Value.ToUpperInvariant();
            for (int i = hex.IndexOf("0400000002000000", StringComparison.Ordinal); i >= 0; i = hex.IndexOf("0400000002000000", i + 2, StringComparison.Ordinal))
            {
                if (i % 2 != 0 || i + 16 + 16 + 8 + 16 + 16 + 8 + 16 > hex.Length) continue;
                int p = i + 16 + 16; // ProcessorNumber
                uint cpu = LeU32(hex, p);
                uint bank = LeU32(hex, p + 8 + 16 + 16);
                ulong status = LeU64(hex, p + 8 + 16 + 16 + 8);
                if ((status >> 63) == 0 || bank > 6) return -1;
                int c = (int)cpu / Hw.ThreadsPerCore;
                return c < Hw.CurveCores ? c : -1;
            }
            return -1;
        }

        static uint LeU32(string hex, int at) { uint v = 0; for (int b = 3; b >= 0; b--) v = (v << 8) | Convert.ToUInt32(hex.Substring(at + b * 2, 2), 16); return v; }
        static ulong LeU64(string hex, int at) { ulong v = 0; for (int b = 7; b >= 0; b--) v = (v << 8) | Convert.ToUInt64(hex.Substring(at + b * 2, 2), 16); return v; }

        // preferred cores first (Windows CPU sets: SchedulingClass is the CPPC ranking on AMD), one entry per physical core
        public static int[] CoreOrder()
        {
            try
            {
                var cls = new Dictionary<int, int>();
                int len;
                GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0))
                        for (int off = 0; off + 24 <= len;)
                        {
                            int size = Marshal.ReadInt32(buf, off);
                            if (size <= 0) break;
                            int logical = Marshal.ReadByte(buf, off + 14), sched = Marshal.ReadByte(buf, off + 19);
                            int core = logical / Hw.ThreadsPerCore;
                            if (!cls.ContainsKey(core) || cls[core] < sched) cls[core] = sched;
                            off += size;
                        }
                }
                finally { Marshal.FreeHGlobal(buf); }
                if (cls.Count == Hw.CurveCores) return cls.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray();
            }
            catch { }
            return Enumerable.Range(0, Hw.CurveCores).ToArray();
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetSystemCpuSetInformation(IntPtr info, int length, out int returned, IntPtr process, int flags);
    }
}
