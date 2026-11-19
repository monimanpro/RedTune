using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RedTune
{
    // 17.11: the program's own heavy load with a self-check, for the auto-tune: what Prime95 is used for. On trial
    // beside Prime95 (Settings.TuneLoad), until a comparison on real hardware shows it finds a weak value as early.
    //
    // The work itself is RedTuneStress.dll (native\stress.h: Fermat tests with exact floating-point arithmetic, every
    // result has to be 1). Like Prime95 it runs outside the program, one process per core: on a core whose value is
    // too deep the worker itself may die, and that must not take Red Tune with it. The worker is RedTune.exe --stress;
    // it tells its parent about wrong results on its output and ends by itself when the parent is gone.
    //   avx     4 numbers at a time (AVX2 + FMA) on blocks that fit the core's own caches
    //   avx512  the same 8 at a time
    //   sse     2 at a time on 4–32 MB a thread: light on current, the core clocks higher, the shared cache and the
    //           memory take part (Prime95's large SSE transforms)
    // The kernels (native\): Fermat passes in three forms (in registers, through memory on short rows, on rows of
    // 1024 numbers), a transform with a rounding check and an exact remainder (stress_fft.c), fused multiply-adds only
    // (stress_fma.c), sums made through the fused units as Prime95 makes them (rt_stress_fma1), 64-bit integer
    // multiply and divide. Which runs when: the comment in Worker.
    // 17.11, calibrated against Prime95 on one core of a Ryzen 9 8945HX set deeper than its own value (--calib-step):
    // corrected cache errors (WHEA 47) in a minute at 15 steps deeper: rows of 1024 alone 4, Prime95 1 and 2; at 18
    // steps: 40 and 62, Prime95 34, 37 and 54. In registers only: none at 21 steps. Neither load reported a wrong result
    // of its own at those values: what gives the core away there is the WHEA record, which the auto-tune watches.
    static class Stress
    {
        const string Dll = "RedTuneStress.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_caps();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern ulong rt_stress_prime();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr rt_stress_alloc(UIntPtr bytes);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void rt_stress_free(IntPtr p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_run(int kernel, IntPtr buf, int n, ulong seed, int ms, int corrupt, ref long passes, ref long errors, ref int first);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_selftest(int kernel, int count, ulong seed);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_fma(ulong seed, int ms, int corrupt, ref long rounds, ref long errors);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_fma1(ulong seed, int ms, int corrupt, ref long rounds, ref long errors);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_add1(ulong seed, int ms, int corrupt, ref long rounds, ref long errors);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_fma2(ulong seed, int ms, int corrupt, ref long rounds, ref long errors);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_like(IntPtr buf, ulong seed, int ms, int corrupt, ref long rounds, ref long errors);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_like_norm(IntPtr buf, ulong seed, int ms, int corrupt, ref long rounds, ref long errors);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_dwt(IntPtr buf, int n, int tenths, int kind, ulong seed, int ms, int corrupt, ref long iters, ref long errors, ref double worst);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int rt_stress_fft(IntPtr buf, int n, int bits, ulong seed, int ms, int corrupt, ref long iters, ref long errors, ref double worst);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        const int KInt = 0, K128 = 1, KAvx2 = 2, KAvx512 = 3, KMem = 0x10, KFill = 0x20, Row = 8192; // added to a vector kernel: every step through memory, on short rows (KMem) or on rows longer than the first-level cache (KFill, 4 rows of Row numbers behind the block)

        static int caps = -1;
        // which kernels this processor runs (bit 0 integer, 1 FMA 128, 2 AVX2, 3 AVX-512); 0: the dll is missing or does not load
        public static int Caps
        {
            get
            {
                if (caps >= 0) return caps;
                try { caps = File.Exists(Path.Combine(Comp.AppDir, Dll)) ? rt_stress_caps() : 0; }
                catch (Exception ex) { caps = 0; Store.Log("Red Tune Test: " + Dll + " не загрузился: " + ex.Message); }
                return caps;
            }
        }
        // the own load can stand in for Prime95 on this processor: it needs AVX2 with FMA (every Ryzen has them)
        public static bool Available { get { return (Caps & (1 << KAvx2)) != 0; } }

        // ---------- the worker: RedTune.exe --stress <avx|avx512|sse> <first logical processor> <threads> <parent pid> [seconds] [corrupt] ----------
        // Output lines: «H passes» twice a second, «E thread wrong first kernel numbers» when a result is not 1.
        // corrupt (a check only): one bit is flipped in the middle of every pass, so every pass must report an error.
        public static int Worker(string[] a)
        {
            try
            {
                // the mode may name the form of the pass for a measurement: «avx-mem» only through memory, «avx-reg» only in registers
                string form = a[0].Contains("-") ? a[0].Substring(a[0].IndexOf('-') + 1) : "";
                string mode = a[0].Contains("-") ? a[0].Substring(0, a[0].IndexOf('-')) : a[0]; int first = int.Parse(a[1]), threads = int.Parse(a[2]), parent = int.Parse(a[3]);
                int seconds = a.Length > 4 ? int.Parse(a[4]) : 0, corrupt = a.Length > 5 && a[5] == "corrupt" ? 20 : -1;
                // «fill1024»: the long-row form with rows of 1024 numbers (a measurement of which length loads the cache most)
                // «reg+p2000x500»: the load in pulses, 2000 ms of work and 500 ms of rest, both threads together (a measurement)
                int pulseOn = 0, pulseOff = 0;
                if (form.Contains("+p")) { var pp = form.Substring(form.IndexOf("+p") + 2).Split('x'); form = form.Substring(0, form.IndexOf("+p")); if (pp.Length == 2) { int.TryParse(pp[0], out pulseOn); int.TryParse(pp[1], out pulseOff); } }
                int forced = 0; if (form.StartsWith("fill") && form.Length > 4) { int.TryParse(form.Substring(4), out forced); form = "fill"; }
                // 17.11: the worker takes its processors itself. It inherits the mask of whoever started it, and under a
                // narrower one the pinning of its threads failed without a word and the load ran on another core (seen in a
                // measurement started from a pinned console: the core under test stood 5 % busy). A load on the wrong core
                // would pass any value, so no pin: no test.
                try { long mask = 0; for (int t = 0; t < threads; t++) mask |= 1L << (first + t); Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask; }
                catch (Exception ex) { Console.Out.WriteLine("X the processors of the core cannot be taken: " + ex.Message); Console.Out.Flush(); return 6; }
                int have = rt_stress_caps();
                int k = mode == "avx512" ? KAvx512 : mode == "sse" ? K128 : KAvx2;
                if ((have & (1 << k)) == 0) { Console.Out.WriteLine("X no such kernel here"); return 3; }
                // numbers per block (8 bytes each): the core's own caches for the wide kernels, megabytes for «sse»
                int[] sizes = mode == "sse" ? new[] { 524288, 2097152, 4194304 } : new[] { 3072, 24576, 98304 };
                int hold = mode == "sse" ? 4000 : 2000; // ms on one block size
                int[] FftSizes = { 4096, 16384, 32768 };  // points of the transform kernel (native\stress_fft.c): 64 KB to 512 KB of numbers a thread
                long passes = 0; int stopAll = 0;
                var wall = Stopwatch.StartNew(); // one clock for all threads: they rest and start together
                var said = new object();
                var pool = new List<Thread>();
                for (int t = 0; t < threads; t++)
                {
                    int id = t;
                    var th = new Thread(() =>
                    {
                        if (SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1UL << (first + id))) == UIntPtr.Zero)
                        { lock (said) { Console.Out.WriteLine("X thread " + id + " cannot be pinned to processor " + (first + id)); Console.Out.Flush(); } Interlocked.Exchange(ref stopAll, 2); return; }
                        IntPtr buf = rt_stress_alloc((UIntPtr)((ulong)Math.Max(sizes.Max() + 4 * Row, 4 * FftSizes.Max() + 64) * 8));
                        if (buf == IntPtr.Zero) { lock (said) Console.Out.WriteLine("X no memory"); Interlocked.Exchange(ref stopAll, 2); return; }
                        var sw = Stopwatch.StartNew(); ulong seed = (ulong)(Environment.TickCount * 31 + id * 7919 + first);
                        for (int slice = 0; Volatile.Read(ref stopAll) == 0; slice++)
                        {
                            int sliceMs = 250;
                            if (pulseOn > 0)
                            {
                                long ph = wall.ElapsedMilliseconds % (pulseOn + pulseOff);
                                if (ph >= pulseOn) { Thread.Sleep((int)(pulseOn + pulseOff - ph)); slice--; continue; }
                                sliceMs = (int)Math.Max(1, Math.Min(250, pulseOn - ph));
                            }
                            int n = sizes[(int)(sw.ElapsedMilliseconds / hold) % sizes.Length];
                            // What runs when the mode names no form (17.11, from the measurements against Prime95 on cores set 15 to 18
                            // steps too deep; the cores gave way in different places, so the kernels take turns):
                            // avx, avx512: by the clock all threads share, 10 seconds round (18.11 order):
                            //   3 s    the moment a load begins, over and over («ramp30x200»: 30 ms of light integer work at the highest clock,
                            //          200 ms of the weighted transform of radix 4 at the edge, all threads by one clock). After a rest a core
                            //          holds 4.6 GHz for 0.2 to 0.6 s and then jumps to 5.2 under the load, and Prime95 finds CCD1 Core 8 just
                            //          there, 0.5 to 0.7 s after it starts. 18.11, CCD1 Core 8 at 18 steps: a wrong result after 2.3 s (400/300 ms)
                            //          and 3.9 s (30/200); the longer light part once reset the laptop at 5.4 GHz (57 % of the time at 5.4 GHz
                            //          there, 7.5 % with 30/200). First in the round: it is the first seconds that count.
                            //   0.5 s  rest
                            //   1.5 s  sums through the fused multiply-add units (native\stress_fma.c: rt_stress_fma1, and rt_stress_fma2
                            //          with the factors the other way round, round by round). CCD1 Core 5: a wrong result within 0.4
                            //          to 1.2 s at 18 steps, in most runs at 15 (Prime95: 1 to 50 s, and none of 3 at 15). It errs
                            //          in the first second after a load begins, so the turn is short and comes after a rest.
                            //   0.5 s  rest
                            //   3 s    the transform with its own check (native\stress_fft.c), 4K, 16K and 32K points by turns, in rounds of
                            //          eight: the squaring of radix 4 (qdwt, qdwt1, qfft: Prime95's shape, at the edge of precision) four, the plain
                            //          transform of radix 2 two, chained fused operations one, sums through the fused units one. CCD1 Core 8 at 18
                            //          steps, 30 s runs: qdwt 2 of 3, qdwt1 1 of 3, qfft 1 of 3, plain radix 2 up to 3 of 4 (Prime95 3 of 3); the
                            //          weighted transform of radix 2 had been 1 of 4, so it is out. Sums through the fused units on numbers in
                            //          registers, or a copy of Prime95's hot loop (native\stress_like.c), never found that core in 30 s.
                            //   0.5 s  rest
                            //   1 s    both threads of a core: the pass through memory on rows of 1024 numbers, alone: what finds a weak
                            //          cache when the clock is held down or all cores work (corrected cache errors at 4.5 GHz, 18 steps
                            //          too deep, in a minute: 40, 62, 22; Prime95 34 to 37). One thread: registers, short rows,
                            //          integers, plain sums, in turn.
                            //   0.5 s  rest
                            // sse (large blocks): the forms take turns slice by slice: rows of 1024, registers, short rows, integers.
                            string f = form;
                            if (f == "" && mode != "sse")
                            {
                                long now = wall.ElapsedMilliseconds; int ph = (int)(now % 10000), end = ph < 3000 ? 3000 : ph < 3500 ? 3500 : ph < 5000 ? 5000 : ph < 5500 ? 5500 : ph < 8500 ? 8500 : ph < 9000 ? 9000 : 10000;
                                if (end == 3500 || end == 5500 || end == 9000) { Thread.Sleep(end - ph); slice--; continue; }
                                sliceMs = Math.Max(1, Math.Min(250, end - ph));
                                long round = now / 10000;
                                f = end == 3000 ? "ramp30x200" : end == 5000 ? (round % 2 == 0 ? "fma1" : "fma2") : end == 8500 ? new[] { "qdwt", "fft", "qdwt1", "fft", "qdwt", "fftd", "qfft", "fft1" }[(int)(round % 8)] : threads > 1 ? "fill" : new[] { "reg", "mem", "int", "add1" }[(int)(round % 4)];
                            }
                            if (f.StartsWith("ramp"))
                            {
                                // 18.11: the moment a load begins. The last two resets (CCD1 Core 8 and CCD2 Core 6, far too deep) came
                                // with all cores nearly idle, as a load was starting; Prime95 finds CCD1 Core 8 within 0.5 to 0.7 s,
                                // after its light setup work, when its transform begins on a core still at its highest clock. Here the
                                // same, over and over, all threads by one clock: light integer work (rt_stress_run, the 64-bit kernel:
                                // the core stays awake at its highest clock, little current), then at once the weighted transform of
                                // radix 4 at the edge of precision (every squaring checked), then light again. «ramp» 400 ms light, 300
                                // heavy; «ramp<light>x<heavy>» in ms. Sizes 4K, 16K, 32K by turns of the heavy part.
                                int light = 400, heavy = 300; var rq = f.Substring(4).Split('x');
                                if (rq.Length == 2) { int.TryParse(rq[0], out light); int.TryParse(rq[1], out heavy); }
                                light = Math.Max(1, light); heavy = Math.Max(1, heavy);
                                long rnow = wall.ElapsedMilliseconds; int per = light + heavy, rph = (int)(rnow % per);
                                bool hv = rph >= light; int left = Math.Max(1, Math.Min(250, (hv ? per : light) - rph));
                                long rp = 0, rerr = 0; int rwhere = -1; double rworst = 0;
                                int rpoints = FftSizes[(int)(rnow / per % FftSizes.Length)];
                                if (hv) rt_stress_dwt(buf, rpoints, 0, 3, seed++, left, corrupt, ref rp, ref rerr, ref rworst);
                                else rt_stress_run(KInt, buf, 128, seed++, left, corrupt, ref rp, ref rerr, ref rwhere);
                                Interlocked.Add(ref passes, rp);
                                if (rerr > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + rerr + " ramp " + (hv ? "heavy " + rpoints + " rounding " + rworst.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "light")); Console.Out.Flush(); }
                                continue;
                            }
                            if (f.StartsWith("alt"))
                            {
                                // a measurement: the wide vector unit in turns with plain integer work, both threads by one clock,
                                // so the unit stands idle and is woken again and again. «alt» goes through half-periods of 0.2, 2 and
                                // 20 ms (2 s each), «alt500» keeps one (in microseconds)
                                long us = wall.ElapsedTicks * 1000000 / Stopwatch.Frequency; int half;
                                if (f.Length > 3 && int.TryParse(f.Substring(3), out half) && half > 0) { } else half = new[] { 200, 2000, 20000 }[(int)(us / 2000000) % 3];
                                bool wide = us / half % 2 == 0;
                                long ap = 0, ae = 0; int aw = -1;
                                rt_stress_run(wide ? k : KInt, buf, wide ? 3072 : 128, seed++, 0, corrupt, ref ap, ref ae, ref aw);
                                Interlocked.Add(ref passes, ap);
                                if (ae > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + ae + " " + aw + " alt " + (wide ? "wide" : "int") + " half " + half); Console.Out.Flush(); }
                                continue;
                            }
                            if (f == "fma" || f == "fma1" || f == "fma2" || f == "add1")
                            {
                                long mp = 0, me = 0;
                                if (f == "fma1") rt_stress_fma1(seed++, sliceMs, corrupt, ref mp, ref me); // sums through the fused units, as Prime95 makes them
                                else if (f == "fma2") rt_stress_fma2(seed++, sliceMs, corrupt, ref mp, ref me); // the same sums, the factors the other way round
                                else if (f == "add1") rt_stress_add1(seed++, sliceMs, corrupt, ref mp, ref me); // the same pairs with the adders proper
                                else rt_stress_fma(seed++, sliceMs, corrupt, ref mp, ref me);
                                Interlocked.Add(ref passes, mp);
                                if (me > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + me + " " + f); Console.Out.Flush(); }
                                continue;
                            }
                            if (f == "like" || f == "liken")
                            {
                                // the kernel shaped after Prime95's FMA transform loop (native\stress_like.c)
                                long lp = 0, le = 0;
                                if (f == "liken") rt_stress_like_norm(buf, seed++, sliceMs, corrupt, ref lp, ref le); // with the normalization step
                                else rt_stress_like(buf, seed++, sliceMs, corrupt, ref lp, ref le);
                                Interlocked.Add(ref passes, lp);
                                if (le > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + le + " " + f); Console.Out.Flush(); }
                                continue;
                            }
                            if (f.StartsWith("qfft") || f.StartsWith("qdwt"))
                            {
                                // the squaring of radix 4 (native\stress_fft.c, sq_rec, 18.11): Prime95's shape of the transform, a
                                // butterfly whole in registers and a block in the first-level cache from forward through back.
                                // «qdwt» weighted as Prime95 squares, «qfft» on whole digits; «…1» with every sum through the fused
                                // units; a size after it (4k, 16k, 32k) keeps one, else 4K, 16K and 32K by turns
                                bool w = f.StartsWith("qdwt"), one = f.Length > 4 && f[4] == '1'; string rest = f.Substring(one ? 5 : 4);
                                int points = rest == "4k" ? 4096 : rest == "16k" ? 16384 : rest == "32k" ? 32768 : FftSizes[(int)(sw.ElapsedMilliseconds / hold) % FftSizes.Length];
                                long qp = 0, qe = 0; double worst = 0;
                                if (w) rt_stress_dwt(buf, points, 0, one ? 4 : 3, seed++, sliceMs, corrupt, ref qp, ref qe, ref worst);
                                else rt_stress_fft(buf, points, one ? 0x600 : 0x400, seed++, sliceMs, corrupt, ref qp, ref qe, ref worst);
                                Interlocked.Add(ref passes, qp);
                                if (qe > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + qe + " " + f + " " + points + " rounding " + worst.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)); Console.Out.Flush(); }
                                continue;
                            }
                            if (f.StartsWith("dwt"))
                            {
                                // the weighted transform, as Prime95 squares (native\stress_fft.c, rt_stress_dwt): «dwt» goes through
                                // 4K, 16K and 32K points and the three kinds of transform by turns; «dwt4k», «dwt32k» one size
                                long dp = 0, de = 0; double worst = 0;
                                int points = f == "dwt4k" ? 4096 : f == "dwt32k" ? 32768 : FftSizes[(int)(sw.ElapsedMilliseconds / hold) % FftSizes.Length];
                                int kind = (int)(sw.ElapsedMilliseconds / (hold * FftSizes.Length) % 3);
                                rt_stress_dwt(buf, points, 0, kind, seed++, sliceMs, corrupt, ref dp, ref de, ref worst);
                                Interlocked.Add(ref passes, dp);
                                if (de > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + de + " " + f + " " + points + " kind " + kind + " rounding " + worst.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)); Console.Out.Flush(); }
                                continue;
                            }
                            if (f == "fft" || f == "fftd" || f == "fft1" || f == "fft4k" || f == "fft32k")
                            {
                                long fp = 0, fe = 0; double worst = 0; int points = f == "fft4k" ? 4096 : f == "fft32k" ? 32768 : FftSizes[(int)(sw.ElapsedMilliseconds / hold) % FftSizes.Length]; // fft4k, fft32k: one size only (measurements)
                                rt_stress_fft(buf, points, f == "fftd" ? 0x100 : f == "fft1" ? 0x200 : 0, seed++, sliceMs, corrupt, ref fp, ref fe, ref worst); // fftd: fused operations only
                                Interlocked.Add(ref passes, fp);
                                if (fe > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + fe + " fft " + points + " rounding " + worst.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)); Console.Out.Flush(); }
                                continue;
                            }
                            int kern, row = 0;
                            if (f == "mem") kern = k | KMem;
                            else if (f == "reg") kern = k;
                            else if (f == "int") kern = KInt;
                            else if (f == "fill") { kern = k | KFill; row = forced; }
                            else switch (slice % 8)
                            {
                                case 7: kern = KInt; break;
                                case 1: case 5: kern = k; break;
                                case 3: kern = k | KMem; break;
                                default: kern = k | KFill; row = 1024; break;
                            }
                            if ((kern & KFill) != 0 && row == 0) row = 1024;
                            int nn = kern == KInt ? Math.Min(n, 3072) : row > 0 && n % row != 0 ? 24576 : n;
                            long p = 0, e = 0; int where = -1;
                            rt_stress_run(kern | (row / 128 << 8), buf, nn, seed++, sliceMs, corrupt, ref p, ref e, ref where);
                            Interlocked.Add(ref passes, p);
                            if (e > 0) lock (said) { Console.Out.WriteLine("E " + id + " " + e + " " + where + " " + kern + " " + nn); Console.Out.Flush(); }
                        }
                        rt_stress_free(buf);
                    }) { IsBackground = true };
                    th.Start(); pool.Add(th);
                }
                var clock = Stopwatch.StartNew();
                while (Volatile.Read(ref stopAll) == 0)
                {
                    Thread.Sleep(500);
                    lock (said) { Console.Out.WriteLine("H " + Interlocked.Read(ref passes)); Console.Out.Flush(); }
                    if (seconds > 0 && clock.Elapsed.TotalSeconds >= seconds) break;
                    try { using (var pp = Process.GetProcessById(parent)) if (pp.HasExited) break; } catch { break; } // the parent is gone
                }
                if (Volatile.Read(ref stopAll) == 2) return 4;
                Interlocked.Exchange(ref stopAll, 1);
                foreach (var th in pool) th.Join(3000);
                return 0;
            }
            catch (Exception ex) { try { Console.Out.WriteLine("X " + ex.Message); } catch { } return 5; }
        }

        // ---------- the parent's side: a worker per core, as Load.P95Run is for Prime95 ----------
        static readonly List<Process> running = new List<Process>();

        public sealed class Run : Load.IHeavy
        {
            readonly Dictionary<int, Process> procs = new Dictionary<int, Process>();
            readonly HashSet<int> wrong = new HashSet<int>();
            readonly Dictionary<int, long> passes = new Dictionary<int, long>();

            public string Name { get { return "Red Tune Test"; } }

            public Run(IEnumerable<int> cores, bool avx, bool avx512 = false, bool oneThread = false, int seconds = 0, bool corrupt = false, string form = null)
            {
                StopAll();
                if (!Available) throw new Exception("Red Tune Test недоступен: нет " + Dll + " или процессор без AVX2");
                string mode = (avx512 && (Caps & (1 << KAvx512)) != 0 ? "avx512" : avx ? "avx" : "sse") + (string.IsNullOrEmpty(form) ? "" : "-" + form);
                int tpc = Hw.ThreadsPerCore, me = Process.GetCurrentProcess().Id;
                foreach (var c in cores)
                {
                    int core = c;
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--stress " + mode + " " + (tpc * c) + " " + (oneThread ? 1 : tpc) + " " + me + " " + seconds + (corrupt ? " corrupt" : ""))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    p.OutputDataReceived += (o, e) =>
                    {
                        var line = e.Data; if (string.IsNullOrEmpty(line)) return;
                        if (line[0] == 'H') { long v; if (long.TryParse(line.Substring(1).Trim(), out v)) lock (passes) passes[core] = v; }
                        else if (line[0] == 'E' || line[0] == 'X')
                        {
                            bool first; lock (wrong) first = wrong.Add(core);
                            if (first) Store.Log("Red Tune Test, " + Hw.CoreName(core) + " (" + mode + "): " + (line[0] == 'E' ? "неверный результат: " + line : line));
                        }
                    };
                    p.Start(); p.BeginOutputReadLine();
                    procs[c] = p; lock (running) running.Add(p);
                }
            }

            // cores whose worker reported a wrong result or died
            public List<int> Failed()
            {
                var bad = new List<int>();
                foreach (var kv in procs)
                {
                    bool dead; try { dead = kv.Value.HasExited; } catch { dead = true; }
                    bool err; lock (wrong) err = wrong.Contains(kv.Key);
                    if (dead || err) bad.Add(kv.Key);
                }
                return bad;
            }

            // Fermat passes done so far on a core (0 before its first report): shows the worker is alive and how fast it goes
            public long Passes(int core) { lock (passes) { long v; return passes.TryGetValue(core, out v) ? v : 0; } }

            public void Dispose()
            {
                foreach (var p in procs.Values) { try { if (!p.HasExited) p.Kill(); } catch { } lock (running) running.Remove(p); try { p.Dispose(); } catch { } }
                procs.Clear();
            }
        }

        public static void StopAll()
        {
            Process[] all; lock (running) { all = running.ToArray(); running.Clear(); }
            foreach (var p in all) try { if (!p.HasExited) p.Kill(); } catch { }
        }

        // for --heavy-test: a load of the auto-tune (engine own | p95; mode avx | sse | avx512 | one = AVX2 on one thread) on a
        // core or on all of them for some seconds, with what the processor does under it every second, and the means from
        // the 6th second on. Nothing is written to the processor.
        // for --start-trace: the first moments of a load on one core, the curve as it is (nothing is written to the processor):
        // the core's clock, voltage, busy share and the processor's power and currents as often as the table gives them,
        // from a second before the load starts. Both loads find a weak core in the first second after they start
        // (17.11), so what that second looks like under each is worth seeing.
        public static string StartTrace(Settings s, string engine, string mode, int core, int ms)
        {
            var sb = new System.Text.StringBuilder(); var inv = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                string form = mode.Contains("-") ? mode.Substring(mode.IndexOf('-') + 1) : null; if (form != null) mode = mode.Substring(0, mode.IndexOf('-'));
                bool avx = mode != "sse", wide = mode == "avx512", one = mode == "one";
                var cores = new[] { core };
                sb.AppendLine(engine + " " + mode + (form != null ? "-" + form : "") + " · " + Hw.CoreName(core));
                sb.AppendLine("ms\tGHz\tV\tbusy %\tW\tTDC A\tEDC A\t°C");
                var sw = Stopwatch.StartNew(); Load.IHeavy h = null; float[] last = null;
                try
                {
                    while (sw.ElapsedMilliseconds < 1000 + ms)
                    {
                        if (h == null && sw.ElapsedMilliseconds >= 1000) h = engine == "own" ? (Load.IHeavy)new Run(cores, avx, wide, one, 0, false, form) : new Load.P95Run(s, cores, avx, wide, one);
                        var t = CoreCurve.Now(cores);
                        if (t != null && (last == null || !t.SequenceEqual(last)))
                        {
                            last = t;
                            sb.AppendLine((sw.ElapsedMilliseconds - 1000) + "\t" + t[0].ToString("0.000", inv) + "\t" + t[1].ToString("0.000", inv) + "\t" + t[2].ToString("0", inv) + "\t" + t[3].ToString("0.0", inv) + "\t" + t[4].ToString("0.0", inv) + "\t" + t[5].ToString("0.0", inv) + "\t" + Tctl.Read());
                        }
                        Thread.Sleep(5);
                    }
                }
                finally { if (h != null) h.Dispose(); }
            }
            catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
            return sb.ToString();
        }

        public static string Compare(Settings s, string engine, string mode, string where, int seconds, bool corrupt)
        {
            var sb = new System.Text.StringBuilder(); var inv = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                int[] cores = where == "all" ? Enumerable.Range(0, Hw.CurveCores).ToArray() : where.Split(',').Select(x => int.Parse(x.Trim())).ToArray(); // «8,9,10»: a part of the cores
                string form = mode.Contains("-") ? mode.Substring(mode.IndexOf('-') + 1) : null; if (form != null) mode = mode.Substring(0, mode.IndexOf('-'));
                bool avx = mode != "sse", wide = mode == "avx512", one = mode == "one";
                sb.AppendLine(engine + " " + mode + " · " + (where == "all" ? "all cores" : string.Join(", ", cores.Select(Hw.CoreName))) + " · " + seconds + " s" + (corrupt ? " · planted error" : ""));
                sb.AppendLine("s\tGHz\tV\tbusy %\tW\tTDC A\tEDC A\t°C\tpasses\tfailed");
                var sum = new double[7]; int n = 0; long last = 0; var failed = new List<int>(); int firstFail = -1; var since = DateTime.Now;
                using (Load.IHeavy h = engine == "own" ? (Load.IHeavy)new Run(cores, avx, wide, one, 0, corrupt, form) : new Load.P95Run(s, cores, avx, wide, one))
                {
                    var own = h as Run; var sw = Stopwatch.StartNew();
                    for (int sec = 1; sec <= seconds; sec++)
                    {
                        Thread.Sleep(Math.Max(0, sec * 1000 - (int)sw.ElapsedMilliseconds));
                        var seen = CoreCurve.Now(cores); var t = seen ?? new float[6]; int temp = Tctl.Read(); // a reading can fail when another program holds the bus: such a second is shown as zeros and left out of the means
                        last = own == null ? 0 : cores.Sum(c => own.Passes(c));
                        failed = h.Failed(); if (failed.Count > 0 && firstFail < 0) firstFail = sec;
                        sb.AppendLine(sec + "\t" + t[0].ToString("0.00", inv) + "\t" + t[1].ToString("0.000", inv) + "\t" + t[2].ToString("0", inv) + "\t" + t[3].ToString("0.0", inv) + "\t" + t[4].ToString("0.0", inv) + "\t" + t[5].ToString("0.0", inv) + "\t" + temp + "\t" + last + "\t" + string.Join(",", failed));
                        if (sec > 5 && seen != null && temp > 0) { for (int k = 0; k < 6; k++) sum[k] += t[k]; sum[6] += temp; n++; }
                    }
                }
                if (n > 0) sb.AppendLine("mean\t" + (sum[0] / n).ToString("0.00", inv) + "\t" + (sum[1] / n).ToString("0.000", inv) + "\t" + (sum[2] / n).ToString("0", inv) + "\t" + (sum[3] / n).ToString("0.0", inv) + "\t" + (sum[4] / n).ToString("0.0", inv) + "\t" + (sum[5] / n).ToString("0.0", inv) + "\t" + (sum[6] / n).ToString("0.0", inv));
                sb.AppendLine("passes " + last + " · failed cores: " + (failed.Count == 0 ? "none" : string.Join(", ", failed.Select(Hw.CoreName)) + " (first seen at second " + firstFail + ")"));
                // 18.11: corrected cache errors (WHEA 47) come without a wrong result: an all-core run on the stable curve
                // logged two for CCD1 Core 5 while the line above said «none». So they are counted here too, by core.
                Thread.Sleep(1500); // the event log takes a moment
                var whea = Load.WheaHits(since);
                sb.AppendLine("WHEA " + whea.Count + (whea.Count == 0 ? "" : ": " + string.Join(", ", whea.GroupBy(x => x.Core).Select(g => (g.Key >= 0 ? Hw.CoreName(g.Key) : "not a core") + " ×" + g.Count()))));
            }
            catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
            return sb.ToString();
        }

        // for --calib-step, a measurement that writes to the processor, so only with the user's go-ahead: one core gets a
        // Curve Optimizer value deeper than its own, a load of the auto-tune runs on it until its first error or for
        // `seconds`, and the curve is written back as it was. A line goes to the log before the load starts (through to the
        // disk, so a reset leaves a trace) and one after. The curve as it was is kept in <log>.orig until it is back, for
        // --calib-restore. To see whether the own load finds a weak value as early as Prime95 does.
        public static string CalibStep(Settings s, string log, int core, int value, string engine, string mode, int seconds)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Action<string> say = line =>
            {
                using (var f = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                { var b = System.Text.Encoding.UTF8.GetBytes(DateTime.Now.ToString("HH:mm:ss") + "\t" + line + "\r\n"); f.Write(b, 0, b.Length); f.Flush(true); }
            };
            string result;
            try
            {
                var orig = Smu.Read();
                if (core < 0 || core >= orig.Length) throw new Exception("no such core");
                if (value < Smu.Min || value > Smu.Max) throw new Exception("value out of range");
                using (var f = new FileStream(log + ".orig", FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                { var b = System.Text.Encoding.ASCII.GetBytes(string.Join(",", orig)); f.Write(b, 0, b.Length); f.Flush(true); }
                string form = mode.Contains("-") ? mode.Substring(mode.IndexOf('-') + 1) : null; if (form != null) mode = mode.Substring(0, mode.IndexOf('-'));
                bool avx = mode != "sse", wide = mode == "avx512", one = mode == "one";
                var cores = new[] { core }; var since = DateTime.Now;
                double failAt = -1; var sum = new double[5]; int n = 0, seen = int.MinValue; bool restored = false; long passes = 0;
                var loadStart = DateTime.Now; double tempSum = 0; int tempN = 0;
                try
                {
                    var deep = (int[])orig.Clone(); deep[core] = value;
                    // RT_WARM_MS=10000: the load starts first, on the curve as it is, and the deep value is written only after
                    // that many milliseconds, while it runs: whether the load finds the core in its steady work or only in its
                    // first moments after a start (17.11, Prime95 on CCD1 Core 8 erred at 0.7 s after its start, every time)
                    int warmMs = 0; int.TryParse(Environment.GetEnvironmentVariable("RT_WARM_MS") ?? "", out warmMs);
                    Load.IHeavy early = null;
                    if (warmMs > 0)
                    {
                        early = engine == "own" ? (Load.IHeavy)new Run(cores, avx, wide, one, 0, false, form) : new Load.P95Run(s, cores, avx, wide, one);
                        Thread.Sleep(warmMs);
                        if (early.Failed().Count > 0) throw new Exception("the load failed on the curve as it is, before the deep value");
                    }
                    var got = Smu.Write(deep);
                    if (got == null || got[core] != value) { if (early != null) early.Dispose(); throw new Exception("the value was not taken"); }
                    say("start\t" + Hw.CoreName(core) + "\t" + orig[core] + " -> " + value + "\t" + engine + " " + mode + "\tup to " + seconds + " s" + (warmMs > 0 ? "\tload running " + warmMs + " ms before" : ""));
                    // RT_CAP=5300,5200,5100 (MHz) and RT_CAP_MS=3000: the clock held under a cap that steps through the list
                    // while the load runs, the way the auto-tune moves it in its clock steps (CoreCurve.CapSet: the
                    // «maximum processor frequency» of the power plan, put back at the end). For measurements (17.11).
                    var capList = (Environment.GetEnvironmentVariable("RT_CAP") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    int capMs = 3000; int.TryParse(Environment.GetEnvironmentVariable("RT_CAP_MS") ?? "", out capMs); if (capMs < 200) capMs = 3000;
                    int capAt = -1;
                    if (capList.Length > 0) { CoreCurve.CapBegin(); CoreCurve.CapSet(capList[0]); capAt = 0; }
                    var sw = Stopwatch.StartNew(); double next = 1; loadStart = DateTime.Now;
                    using (Load.IHeavy h = early ?? (engine == "own" ? (Load.IHeavy)new Run(cores, avx, wide, one, 0, false, form) : new Load.P95Run(s, cores, avx, wide, one)))
                    {
                        while (sw.Elapsed.TotalSeconds < seconds)
                        {
                            Thread.Sleep(200);
                            if (h.Failed().Count > 0) { failAt = sw.Elapsed.TotalSeconds; break; }
                            if (capAt >= 0) { int want = (int)(sw.ElapsedMilliseconds / capMs % capList.Length); if (want != capAt) { capAt = want; CoreCurve.CapSet(capList[capAt]); } }
                            if (sw.Elapsed.TotalSeconds >= next)
                            {
                                next += 1; var t = CoreCurve.Now(cores);
                                if (t != null) { for (int k = 0; k < 5; k++) sum[k] += t[k]; n++; }
                                int tc = Tctl.Read(); if (tc > 0) { tempSum += tc; tempN++; }
                            }
                        }
                        var own = h as Run; if (own != null) passes = own.Passes(core);
                    }
                    try { seen = Smu.Read()[core]; } catch { }
                }
                finally
                {
                    try { CoreCurve.CapSet(0); CoreCurve.CapEnd(); } catch { }
                    try { var back = Smu.Write(orig); restored = back != null && back.SequenceEqual(orig); }
                    catch (Exception ex) { say("RESTORE FAILED\t" + ex.Message); }
                }
                if (restored) try { File.Delete(log + ".orig"); } catch { }
                Thread.Sleep(1500);
                var whea = Load.WheaHits(since);
                var mine = whea.Where(w => w.Core == core).ToList(); var others = whea.Where(w => w.Core != core).ToList(); // this core's cache errors, and anything else
                result = "end\t" + Hw.CoreName(core) + "\t" + value + "\t" + engine + " " + mode + "\t"
                    + (failAt >= 0 ? "ERROR at " + failAt.ToString("0.0", inv) + " s" : "no error in " + seconds + " s")
                    + "\t" + (n > 0 ? (sum[0] / n).ToString("0.00", inv) + " GHz " + (sum[1] / n).ToString("0.000", inv) + " V " + (sum[3] / n).ToString("0.0", inv) + " W " + (sum[4] / n).ToString("0.0", inv) + " A" + (tempN > 0 ? " " + (tempSum / tempN).ToString("0", inv) + " C" : "") : "no telemetry")
                    + "\tpasses " + passes + "\tWHEA " + mine.Count + (mine.Count > 0 ? " first at " + Math.Max(0, (mine.Min(w => w.When) - loadStart).TotalSeconds).ToString("0.0", inv) + " s" : "")
                    + (others.Count > 0 ? " OTHER " + others.Count + " (" + string.Join("; ", others.Select(w => w.Text).Take(3)) + ")" : "")
                    + "\t" + (seen == value ? "value held" : "VALUE AT THE END " + seen) + "\t" + (restored ? "curve restored" : "CURVE NOT RESTORED");
            }
            catch (Exception ex) { result = "error\t" + ex.Message; }
            try { say(result); } catch { }
            return result;
        }

        // for --calib-restore: the curve a --calib-step saved and did not get to write back
        public static string CalibRestore(string log)
        {
            try
            {
                if (!File.Exists(log + ".orig")) return "nothing to restore";
                var orig = File.ReadAllText(log + ".orig").Split(',').Select(int.Parse).ToArray();
                var back = Smu.Write(orig);
                bool ok = back != null && back.SequenceEqual(orig);
                if (ok) File.Delete(log + ".orig");
                return (ok ? "restored: " : "NOT RESTORED, the curve now: ") + string.Join(",", back ?? new int[0]);
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        // for --stress-check: the dll, its kernels, the arithmetic against integers, and that a flipped bit is caught
        public static string Check()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                int have = Caps;
                sb.AppendLine(Dll + ": " + (have == 0 ? "нет или не загрузился" : "ядра 0x" + have.ToString("X") + ", простое число " + rt_stress_prime()));
                if (have == 0) return sb.ToString();
                string[] names = { "int", "fma128", "avx2", "avx512" };
                IntPtr buf = rt_stress_alloc((UIntPtr)(8UL * 98304));
                for (int k = 0; k < 4; k++)
                {
                    if ((have & (1 << k)) == 0) { sb.AppendLine(names[k] + ": нет на этом процессоре"); continue; }
                    int self = rt_stress_selftest(k, 1000000, (ulong)(1234 + k));
                    long p = 0, e = 0, p2 = 0, e2 = 0; int w = -1, w2 = -1;
                    rt_stress_run(k, buf, 4096, 1, 300, -1, ref p, ref e, ref w);
                    rt_stress_run(k, buf, 4096, 1, 1, 20, ref p2, ref e2, ref w2);
                    if (k > 0)
                    {
                        long pm = 0, em = 0, pc = 0, ec = 0; int wm = -1, wc = -1;
                        rt_stress_run(k | KMem, buf, 4096, 1, 300, -1, ref pm, ref em, ref wm);
                        rt_stress_run(k | KMem, buf, 4096, 1, 1, 20, ref pc, ref ec, ref wc);
                        long pf = 0, ef = 0, pg = 0, eg = 0; int wf = -1, wg = -1;
                        rt_stress_run(k | KFill, buf, 24576, 1, 300, -1, ref pf, ref ef, ref wf);
                        rt_stress_run(k | KFill, buf, 24576, 1, 1, 20, ref pg, ref eg, ref wg);
                        sb.AppendLine(names[k] + " через память, длинные ряды: за 0,3 с проходов по 24576 чисел " + pf + ", ошибок " + ef + " · с подложенной ошибкой: проходов " + pg + ", поймано " + eg + " (место " + wg + ")");
                        sb.AppendLine(names[k] + " через память: за 0,3 с проходов " + pm + ", ошибок " + em + " · с подложенной ошибкой: проходов " + pc + ", поймано " + ec + " (место " + wc + ")");
                    }
                    if (k == KAvx2)
                    {
                        long mr = 0, me = 0, cr = 0, ce = 0;
                        rt_stress_fma(9, 300, -1, ref mr, ref me); rt_stress_fma(9, 20, 1, ref cr, ref ce);
                        sb.AppendLine("только FMA: за 0,3 с кругов " + mr + ", ошибок " + me + " · с подложенной ошибкой: кругов " + cr + ", поймано " + ce);
                        mr = me = cr = ce = 0;
                        rt_stress_fma1(9, 300, -1, ref mr, ref me); rt_stress_fma1(9, 20, 1, ref cr, ref ce);
                        sb.AppendLine("суммы через FMA: за 0,3 с кругов " + mr + ", ошибок " + me + " · с подложенной ошибкой: кругов " + cr + ", поймано " + ce);
                        mr = me = cr = ce = 0;
                        rt_stress_add1(9, 300, -1, ref mr, ref me); rt_stress_add1(9, 20, 1, ref cr, ref ce);
                        sb.AppendLine("обычные суммы: за 0,3 с кругов " + mr + ", ошибок " + me + " · с подложенной ошибкой: кругов " + cr + ", поймано " + ce);
                        IntPtr lb = rt_stress_alloc((UIntPtr)(16384UL * 8));
                        mr = me = cr = ce = 0;
                        rt_stress_like(lb, 9, 300, -1, ref mr, ref me); rt_stress_like(lb, 9, 30, 1, ref cr, ref ce);
                        sb.AppendLine("цикл как у Prime95: за 0,3 с кругов " + mr + ", ошибок " + me + " · с подложенной ошибкой: кругов " + cr + ", поймано " + ce);
                        mr = me = cr = ce = 0;
                        rt_stress_like_norm(lb, 9, 300, -1, ref mr, ref me); rt_stress_like_norm(lb, 9, 30, 1, ref cr, ref ce);
                        rt_stress_free(lb);
                        sb.AppendLine("он же с нормализацией: за 0,3 с кругов " + mr + ", ошибок " + me + " · с подложенной ошибкой: кругов " + cr + ", поймано " + ce);
                    }
                    if (k == KAvx2)
                        foreach (int points in new[] { 1024, 4096, 16384, 32768, 65536 })
                        {
                            IntPtr db = rt_stress_alloc((UIntPtr)((4UL * 65536 + 64) * 8));
                            long di = 0, de = 0, ci = 0, ce = 0; double worst = 0, cw = 0;
                            rt_stress_dwt(db, points, 0, 0, 5, 300, -1, ref di, ref de, ref worst);
                            rt_stress_dwt(db, points, 0, 2, 5, 30, 1, ref ci, ref ce, ref cw);
                            rt_stress_free(db);
                            sb.AppendLine("взвешенное преобразование, " + points + " точек: за 0,3 с возведений " + di + ", ошибок " + de + ", худшее округление " + worst.ToString("0.000") + " · с подложенной ошибкой: возведений " + ci + ", поймано " + ce);
                        }
                    if (k == KAvx2)
                        foreach (int points in new[] { 1024, 4096, 16384, 32768, 65536 })
                        {
                            IntPtr qb = rt_stress_alloc((UIntPtr)((4UL * 65536 + 64) * 8));
                            long qi = 0, qe = 0, ci = 0, ce = 0, ri = 0, re = 0; double worst = 0, cw = 0, rw = 0;
                            rt_stress_dwt(qb, points, 0, 3, 5, 300, -1, ref qi, ref qe, ref worst);
                            rt_stress_dwt(qb, points, 0, 4, 5, 300, -1, ref ri, ref re, ref rw);
                            rt_stress_dwt(qb, points, 0, 4, 5, 30, 1, ref ci, ref ce, ref cw);
                            rt_stress_free(qb);
                            sb.AppendLine("основание 4, взвешенное, " + points + " точек: за 0,3 с возведений " + qi + ", ошибок " + qe + ", худшее округление " + worst.ToString("0.000") + " · суммы через FMA: возведений " + ri + ", ошибок " + re + ", худшее " + rw.ToString("0.000") + " · с подложенной ошибкой: возведений " + ci + ", поймано " + ce);
                        }
                    if (k == KAvx2)
                        foreach (int points in new[] { 4096, 16384, 32768, 65536 })
                        {
                            IntPtr fb = rt_stress_alloc((UIntPtr)((4UL * 65536 + 64) * 8));
                            long fi = 0, fe = 0, ci = 0, ce = 0; double worst = 0, cw = 0;
                            rt_stress_fft(fb, points, 0x100, 5, 300, -1, ref fi, ref fe, ref worst);
                            rt_stress_fft(fb, points, 0x100, 5, 20, 1, ref ci, ref ce, ref cw);
                            rt_stress_free(fb);
                            sb.AppendLine("преобразование, " + points + " точек: за 0,3 с возведений " + fi + ", ошибок " + fe + ", худшее округление " + worst.ToString("0.000") + " · с подложенной ошибкой: возведений " + ci + ", поймано " + ce);
                        }
                    sb.AppendLine(names[k] + ": умножение неверно " + self + " из 1000000 · за 0,3 с проходов " + p + ", ошибок " + e + " · с подложенной ошибкой: проходов " + p2 + ", поймано " + e2 + " (место " + w2 + ")");
                }
                rt_stress_free(buf);
            }
            catch (Exception ex) { sb.AppendLine("ошибка: " + ex.Message); }
            return sb.ToString();
        }
    }
}
