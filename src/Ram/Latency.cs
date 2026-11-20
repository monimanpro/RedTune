using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RedTune.Ram
{
    // 16.11: memory latency, the number AIDA64 shows, and the curve it comes from. One thread on one core follows a chain
    // of pointers: every 64 bytes (one cache line) of a block holds the address of another line of the same block, all
    // of them in one random loop, so the next address is known only when the previous read has arrived and the
    // processor can neither read ahead nor run two reads at once. The time per step is the delay of one read. The block
    // grows from 4 KB to 1 GB: while it fits the L1 cache the step costs L1's delay, then L2's, then L3's, and past the L3 every
    // step is a read from the memory modules: the latency that timings and the memory speed move.
    // The pages are ordinary 4 KB ones (large pages need a Windows right ordinary accounts lack), so on big blocks the
    // step also carries the page-table lookup, the same as every program sees it. Nothing is written anywhere but the
    // block itself; the runs are kept in Ram\latency.txt to compare one set of timings with another.
    static unsafe class Latency
    {
        public class Point { public long Bytes; public double Ns; }
        public class Run
        {
            public DateTime Time; public string Mem = "";
            public List<Point> Points = new List<Point>();
            // 21.11: measured on 2 MB pages (Windows gave them): no page lookups in the steps, the memory figure as AIDA64's
            public bool Large;
            // the delay at the biggest measured block that is not over this size; NaN when there is none
            public double At(long bytes) { var p = Points.Where(x => x.Bytes <= bytes).OrderBy(x => x.Bytes).LastOrDefault(); return p == null ? double.NaN : p.Ns; }
            // on large pages at a block of six times the L3 (AidaBytes; 192 MB here: 76.7–77.2 ns against AIDA64's 76.6 on
            // 21.11; a bigger block adds misses of the 2 MB page entries too), else the biggest block, as before
            public double Ram
            {
                get
                {
                    if (Points.Count == 0) return double.NaN;
                    if (Large) { var a = At(AidaBytes); if (!double.IsNaN(a)) return a; }
                    return Points.OrderBy(x => x.Bytes).Last().Ns;
                }
            }
            // inside a cache: at half its size, where the block surely fits beside everything else the core keeps there
            public double L1 { get { return CacheL1 > 0 ? At(CacheL1 / 2) : double.NaN; } }
            public double L2 { get { return CacheL2 > 0 ? At(CacheL2 / 2) : double.NaN; } }
            public double L3 { get { return CacheL3 > 0 ? At(CacheL3 / 2) : double.NaN; } }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        public const long MinBytes = 4 << 10; // 16.11: from inside the L1 (it was 64 KB, the L2)
        static long l1 = -1, l2 = -1, l3 = -1;
        // 21.11 (the user's choice): six times the L3, so about the same share of the reads (~17 %) still come from it on any
        // processor, as in AIDA64's 76.6 ns here; at least 64 MB (a small L3 would bring the block back into the caches);
        // 192 MB when the L3 size is unknown
        public static long AidaBytes { get { return CacheL3 > 0 ? Math.Max(64L << 20, 6 * CacheL3) : 192L << 20; } }
        public static long CacheL1 { get { if (l1 < 0) l1 = RedTune.Dist.Native.CacheBytes(1); return l1; } }
        public static long CacheL2 { get { if (l2 < 0) l2 = RedTune.Dist.Native.CacheBytes(2); return l2; } }
        public static long CacheL3 { get { if (l3 < 0) l3 = RedTune.Dist.Native.CacheBytes(3); return l3; } }

        public static volatile bool Running;
        static volatile bool stop;
        public static void Stop() { stop = true; }

        // 4 KB, 6 KB, 8 KB … up to max: two points per doubling
        public static long[] Sizes(long max)
        {
            var list = new List<long>();
            for (long s = MinBytes; s <= max; s *= 2) { list.Add(s); if (s * 3 / 2 <= max) list.Add(s * 3 / 2); }
            return list.ToArray();
        }

        // how big the biggest block may be with the memory that is free now
        public static long MaxBytes()
        {
            ulong total, avail;
            if (!RedTune.Native.Memory(out total, out avail)) return 256L << 20;
            return avail >= (4UL << 30) ? 1L << 30 : avail >= (2UL << 30) ? 512L << 20 : avail >= (1UL << 30) ? 256L << 20 : 0;
        }

        // progress(the run so far, points done, points in all) and done(the run or null, why not) come on the worker thread
        // keep: false for the command-line check (--latency), which leaves no run in the store
        public static string Start(string mem, Action<Run, int, int> progress, Action<Run, string> done, bool keep = true)
        {
            if (Running) return "Замер уже идёт";
            long max = MaxBytes();
            if (max == 0) return "Мало свободной памяти: для замера нужен хотя бы 1 ГБ";
            Running = true; stop = false;
            var t = new Thread(() =>
            {
                var run = new Run { Time = DateTime.Now, Mem = mem ?? "" };
                string err = null;
                IntPtr block = IntPtr.Zero;
                try
                {
                    // one core, not the first (Windows keeps its own work there); the other thread of the core stays free
                    int core = Math.Min(1, Hw.CurveCores - 1), cpu = core * Hw.ThreadsPerCore;
                    if (cpu < 64) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
                    // 21.11: 2 MB pages where Windows gives them (the right to lock pages; the app runs as administrator):
                    // ordinary 4 KB ones add a page lookup to every step past ~12 MB (about 16 ns on the memory, AIDA64 has none)
                    long lp = (long)(ulong)GetLargePageMinimum();
                    if (lp > 0 && LockPagesRight())
                    {
                        long want = (max + lp - 1) / lp * lp;
                        block = VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)want, 0x3000 | 0x20000000, 4);
                        run.Large = block != IntPtr.Zero;
                    }
                    if (block == IntPtr.Zero) block = VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)max, 0x3000, 4);
                    if (block == IntPtr.Zero) throw new Exception("Windows не дал " + (max >> 20) + " МБ памяти");
                    byte* b = (byte*)block;
                    // the clock up before the first point: the cache delays are counted in the core's cycles
                    var spin = System.Diagnostics.Stopwatch.StartNew(); double x = 1;
                    while (spin.ElapsedMilliseconds < 300) for (int i = 0; i < 100000; i++) x = Math.Sqrt(x + i);
                    var sizes = Sizes(max);
                    ulong seed = 0x9E3779B97F4A7C15UL ^ (ulong)DateTime.Now.Ticks;
                    for (int k = 0; k < sizes.Length && !stop; k++)
                    {
                        long n = sizes[k] / 64;
                        Build(b, n, ref seed);
                        byte* p = b;
                        if (n <= (4L << 20)) p = Chase(p, n);           // a small block: once round, so it sits in the cache
                        double best = double.MaxValue;
                        var sw = new System.Diagnostics.Stopwatch();
                        for (int rep = 0; rep < 3 && !stop; rep++)
                        {
                            // at least 0.12 s and 2^17 steps per try; the least disturbed of three tries counts
                            long steps = 0; sw.Restart();
                            do { p = Chase(p, 1 << 17); steps += 1 << 17; } while (sw.ElapsedMilliseconds < 120);
                            double ns = sw.Elapsed.TotalMilliseconds * 1e6 / steps;
                            if (ns < best) best = ns;
                        }
                        if (stop) break;
                        run.Points.Add(new Point { Bytes = sizes[k], Ns = best });
                        if (progress != null) try { progress(run, k + 1, sizes.Length); } catch { }
                    }
                    if (stop) err = "Замер остановлен";
                    else if (x < 0) err = "?"; // x is used: the spin loop stays
                }
                catch (Exception ex) { err = ex.Message; }
                finally
                {
                    if (block != IntPtr.Zero) VirtualFree(block, UIntPtr.Zero, 0x8000);
                    Running = false;
                }
                if (err == null)
                {
                    if (keep) try { Save(run); } catch (Exception ex) { Store.Log("латентность памяти: " + ex.Message); }
                    if (keep) Store.Log("латентность памяти" + (run.Mem.Length > 0 ? " (" + run.Mem + ")" : "") + ": " + Summary(run, null) + " · " +
                        string.Join(" ", run.Points.Select(q => Size(q.Bytes, false) + "=" + q.Ns.ToString("0.0", CultureInfo.InvariantCulture))));
                }
                if (done != null) try { done(err == null ? run : null, err); } catch { }
            }) { IsBackground = true, Name = "latency", Priority = ThreadPriority.Highest };
            t.Start();
            return null;
        }

        // ---------- 21.11: a probe of why AIDA64 shows less (--latency-probe) ----------
        [DllImport("kernel32.dll")] static extern UIntPtr GetLargePageMinimum();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokPriv newState, int len, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TokPriv { public int Count; public long Luid; public int Attr; }

        // the right to lock pages (large pages need it): enabled in this process if the account holds it
        static bool LockPagesRight()
        {
            IntPtr tok; long luid;
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out tok) || !LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out luid)) return false;
            var tp = new TokPriv { Count = 1, Luid = luid, Attr = 2 };
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }

        static double Time(byte* p, out byte* end)
        {
            double best = double.MaxValue; var sw = new System.Diagnostics.Stopwatch();
            for (int rep = 0; rep < 3; rep++)
            {
                long steps = 0; sw.Restart();
                do { p = Chase(p, 1 << 17); steps += 1 << 17; } while (sw.ElapsedMilliseconds < 300);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / steps);
            }
            end = p; return best;
        }

        // one line in every 4 KB page of the block, the pages in one random loop, the line inside each page spread over
        // the 64 (else all of them fall into the same cache sets): every step is a page lookup, the data itself from the L3
        static void BuildPages(byte* b, long pages, ref ulong seed)
        {
            Func<long, long> at = i => i * 4096 + ((i * 37) & 63) * 64;
            var order = new long[pages];
            for (long i = 0; i < pages; i++) order[i] = i;
            ulong s = seed;
            for (long i = pages - 1; i > 0; i--) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; long j = (long)(s % (ulong)i); long v = order[i]; order[i] = order[j]; order[j] = v; }
            seed = s;
            // order is one cycle (Sattolo): page i points to page order[i]
            for (long i = 0; i < pages; i++) *(long*)(b + at(i)) = (long)(b + at(order[i]));
        }

        internal static string Probe()
        {
            var sb = new StringBuilder();
            int core = Math.Min(1, Hw.CurveCores - 1), cpu = core * Hw.ThreadsPerCore;
            if (cpu < 64) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
            System.Threading.Thread.CurrentThread.Priority = ThreadPriority.Highest;
            var spin = System.Diagnostics.Stopwatch.StartNew(); double x = 1;
            while (spin.ElapsedMilliseconds < 500) for (int i = 0; i < 100000; i++) x = Math.Sqrt(x + i);
            long size = 1L << 30; ulong seed = 0x9E3779B97F4A7C15UL ^ (ulong)DateTime.Now.Ticks; byte* end;
            // 1. large pages
            bool right = LockPagesRight(); long lp = (long)(ulong)GetLargePageMinimum();
            IntPtr big = right && lp > 0 ? VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)size, 0x3000 | 0x20000000, 4) : IntPtr.Zero;
            sb.AppendLine("large pages: right " + (right ? "yes" : "no") + ", page " + (lp >> 10) + " KB, block " + (big != IntPtr.Zero ? "given" : "not given (" + Marshal.GetLastWin32Error() + ")"));
            if (big != IntPtr.Zero)
            {
                Build((byte*)big, size / 64, ref seed);
                sb.AppendLine("  1 GB random on large pages: " + Time((byte*)big, out end).ToString("0.0", CultureInfo.InvariantCulture) + " ns");
                foreach (long mb in new long[] { 48, 64, 96, 128, 192, 256, 384, 512 })
                {
                    Build((byte*)big, (mb << 20) / 64, ref seed);
                    sb.AppendLine("  " + mb + " MB random on large pages: " + Time((byte*)big, out end).ToString("0.0", CultureInfo.InvariantCulture) + " ns");
                }
                VirtualFree(big, UIntPtr.Zero, 0x8000);
            }
            // 2. ordinary pages: the full random chase, the page lookups alone, the L3 alone
            IntPtr blk = VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)size, 0x3000, 4);
            byte* b = (byte*)blk;
            Build(b, size / 64, ref seed);
            double full = Time(b, out end);
            BuildPages(b, size / 4096, ref seed);
            double walk = Time(b, out end);
            Build(b, (8L << 20) / 64, ref seed); end = Chase(b, (8L << 20) / 64);
            double l3 = Time(end, out end);
            Build(b, (256L << 10) / 64, ref seed); end = Chase(b, (256L << 10) / 64);
            double l2 = Time(end, out end);
            VirtualFree(blk, UIntPtr.Zero, 0x8000);
            Func<double, string> f = v => v.ToString("0.0", CultureInfo.InvariantCulture);
            sb.AppendLine("1 GB random, 4 KB pages: " + f(full) + " ns");
            sb.AppendLine("one line per page over 1 GB (lookup + L3): " + f(walk) + " ns");
            sb.AppendLine("8 MB random (L3): " + f(l3) + " ns, 256 KB (L2): " + f(l2) + " ns  → clock ≈ " + (14 / l2).ToString("0.00", CultureInfo.InvariantCulture) + " GHz (L2 = 14 cycles)");
            sb.AppendLine("page lookup ≈ " + f(walk - l3) + " ns → memory without it ≈ " + f(full - (walk - l3)) + " ns");
            if (x < 0) sb.AppendLine("?");
            return sb.ToString();
        }

        // n lines of the block in one random loop: line i first holds i, Sattolo's shuffle makes the numbers one cycle
        // through all of them, then every number becomes the address of its line
        static void Build(byte* b, long n, ref ulong seed)
        {
            for (long i = 0; i < n; i++) *(long*)(b + i * 64) = i;
            ulong s = seed;
            for (long i = n - 1; i > 0; i--)
            {
                s ^= s << 13; s ^= s >> 7; s ^= s << 17;
                long j = (long)(s % (ulong)i);
                long* pi = (long*)(b + i * 64), pj = (long*)(b + j * 64);
                long v = *pi; *pi = *pj; *pj = v;
            }
            seed = s;
            for (long i = 0; i < n; i++) { long* q = (long*)(b + i * 64); *q = (long)(b + *q * 64); }
        }

        // the chain itself: each read gives the address of the next one
        [MethodImpl(MethodImplOptions.NoInlining)]
        static byte* Chase(byte* p, long steps)
        {
            for (long k = steps >> 3; k > 0; k--)
            {
                p = *(byte**)p; p = *(byte**)p; p = *(byte**)p; p = *(byte**)p;
                p = *(byte**)p; p = *(byte**)p; p = *(byte**)p; p = *(byte**)p;
            }
            for (long k = steps & 7; k > 0; k--) p = *(byte**)p;
            return p;
        }

        // ---------- the store: "time|memory|bytes:ns,bytes:ns…", the last runs ----------

        static string FilePath { get { return Path.Combine(RamCore.DataDir, "latency.txt"); } }
        const int Keep = 12;

        public static List<Run> Load()
        {
            var list = new List<Run>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var p = line.Split('|'); DateTime t;
                    if (p.Length < 3 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    var run = new Run { Time = t, Mem = p[1], Large = p.Length > 3 && p[3] == "lp" };
                    foreach (var pt in p[2].Split(','))
                    {
                        var xy = pt.Split(':'); long by; double ns;
                        if (xy.Length == 2 && long.TryParse(xy[0], out by) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ns)) run.Points.Add(new Point { Bytes = by, Ns = ns });
                    }
                    if (run.Points.Count > 2) list.Add(run);
                }
            }
            catch (Exception ex) { Store.Log("латентность памяти: " + ex.Message); }
            return list.OrderBy(r => r.Time).ToList();
        }

        static void Save(Run run)
        {
            var all = Load(); all.Add(run);
            Directory.CreateDirectory(RamCore.DataDir);
            File.WriteAllLines(FilePath, all.Skip(Math.Max(0, all.Count - Keep)).Select(r => string.Join("|", r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.Mem.Replace('|', '/'),
                string.Join(",", r.Points.Select(q => q.Bytes + ":" + q.Ns.ToString("0.00", CultureInfo.InvariantCulture))) + (r.Large ? "|lp" : ""))), new UTF8Encoding(true));
        }

        // ---------- texts ----------

        public static string Ns(double v) { return double.IsNaN(v) ? "—" : v.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")); }

        // «64 КБ», «1,5 МБ», «1 ГБ»
        public static string Size(long bytes, bool local = true)
        {
            bool en = !local || L.En;
            Func<double, string> n = v => v.ToString("0.#", local ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture);
            return bytes >= 1L << 30 ? n(bytes / 1073741824.0) + (en ? " GB" : " ГБ") : bytes >= 1L << 20 ? n(bytes / 1048576.0) + (en ? " MB" : " МБ") : n(bytes / 1024.0) + (en ? " KB" : " КБ");
        }

        // «RAM 78,4 нс (было 81,2) · L3 11,9 нс · L2 2,9 нс · L1 0,9 нс»
        public static string Summary(Run r, Run before)
        {
            var s = "RAM " + Ns(r.Ram) + " нс";
            // a run on the other kind of pages is no comparison: 4 KB ones carry ~16 ns of page lookups
            if (before != null && before.Large == r.Large && !double.IsNaN(before.Ram)) s += " (было " + Ns(before.Ram) + ")";
            if (!double.IsNaN(r.L3)) s += " · L3 " + Ns(r.L3) + " нс";
            if (!double.IsNaN(r.L2)) s += " · L2 " + Ns(r.L2) + " нс";
            if (!double.IsNaN(r.L1)) s += " · L1 " + Ns(r.L1) + " нс";
            if (!r.Large) s += " · на обычных страницах";
            return s;
        }
    }

    // the curve: block size across (a log scale, 4 KB … 1 GB), the delay of one read up (a log scale too: the caches
    // answer in a few nanoseconds, the memory in tens). Dashed lines stand where the L1, the L2 and the L3 end; left of the first
    // the block is in L1, then in L2, in L3, and right of the last in the memory itself. The last run is red, the one
    // before it grey, a run in progress grows point by point.
    class LatencyGraph : Painted
    {
        public Latency.Run Now, Before, Live;
        int hot = -1;

        public LatencyGraph() { BackColor = Theme.Window; Height = Theme.S(290); }

        RectangleF Plot { get { return new RectangleF(Theme.S(64), Theme.S(34), Width - Theme.S(92), Height - Theme.S(72)); } }
        Latency.Run Top { get { return Live ?? Now; } }
        long XMax { get { long m = 1L << 30; foreach (var r in new[] { Now, Before, Live }) if (r != null && r.Points.Count > 0) m = Math.Max(m, r.Points.Max(p => p.Bytes)); return m; } }
        double YMax
        {
            get
            {
                double m = 100;
                foreach (var r in new[] { Now, Before, Live }) if (r != null && r.Points.Count > 0) m = Math.Max(m, r.Points.Max(p => p.Ns));
                foreach (var t in new[] { 100.0, 200, 500, 1000 }) if (m <= t) return t;
                return 2000;
            }
        }
        const double YMin = 0.5; // an L1 read is under a nanosecond

        float X(RectangleF plot, long bytes) { return plot.X + (float)(Math.Log(bytes / (double)Latency.MinBytes) / Math.Log(XMax / (double)Latency.MinBytes)) * plot.Width; }
        float Y(RectangleF plot, double ns) { ns = Math.Max(YMin, Math.Min(YMax, ns)); return plot.Bottom - (float)(Math.Log(ns / YMin) / Math.Log(YMax / YMin)) * plot.Height; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var r = Top; int best = -1;
            if (r != null)
            {
                var plot = Plot; float bd = Theme.S(22);
                for (int i = 0; i < r.Points.Count; i++) { float d = Math.Abs(X(plot, r.Points[i].Bytes) - e.X); if (d < bd) { bd = d; best = i; } }
                if (e.Y < plot.Y - Theme.S(10) || e.Y > plot.Bottom + Theme.S(10)) best = -1;
            }
            if (best != hot) { hot = best; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, card, Theme.S(10), Theme.Card);
            Theme.Stroke(g, card, Theme.S(10), Theme.Border);
            var plot = Plot;
            long xmax = XMax; double ymax = YMax;
            string ns = L.T("нс");

            using (var grid = new Pen(Color.FromArgb(0x35, 0x34, 0x31)))
            {
                foreach (var v in new[] { 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000 })
                {
                    if (v > ymax) break;
                    float y = Y(plot, v);
                    g.DrawLine(grid, plot.X, y, plot.Right, y);
                    TextRenderer.DrawText(g, v.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")), Theme.Small, new Rectangle(0, (int)y - Theme.S(9), (int)plot.X - Theme.S(8), Theme.S(18)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                for (long s = Latency.MinBytes; s <= xmax; s *= 4)
                {
                    float x = X(plot, s);
                    g.DrawLine(grid, x, plot.Y, x, plot.Bottom);
                    TextRenderer.DrawText(g, Latency.Size(s), Theme.Small, new Rectangle((int)x - Theme.S(40), (int)plot.Bottom + Theme.S(4), Theme.S(80), Theme.S(18)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }
            TextRenderer.DrawText(g, ns, Theme.Small, new Rectangle((int)plot.X + Theme.S(6), (int)plot.Y + Theme.S(2), Theme.S(40), Theme.S(16)), Theme.Muted, TextFormatFlags.Left);

            // where the caches end, and what the block is read from on each side
            long c1 = Latency.CacheL1, c2 = Latency.CacheL2, c3 = Latency.CacheL3;
            var edges = new List<float> { plot.X };
            var names = new List<string>();
            using (var pen = new Pen(Theme.BorderStrong) { DashStyle = DashStyle.Dash })
            {
                if (c1 > Latency.MinBytes && c1 < xmax) { float x = X(plot, c1); g.DrawLine(pen, x, plot.Y - Theme.S(4), x, plot.Bottom); edges.Add(x); names.Add("L1 · " + Latency.Size(c1)); }
                if (c2 > Math.Max(c1, Latency.MinBytes) && c2 < xmax) { float x = X(plot, c2); g.DrawLine(pen, x, plot.Y - Theme.S(4), x, plot.Bottom); edges.Add(x); names.Add("L2 · " + Latency.Size(c2)); }
                if (c3 > c2 && c3 < xmax) { float x = X(plot, c3); g.DrawLine(pen, x, plot.Y - Theme.S(4), x, plot.Bottom); edges.Add(x); names.Add("L3 · " + Latency.Size(c3)); }
            }
            edges.Add(plot.Right); names.Add(L.T("Память"));
            for (int i = 0; i < names.Count; i++)
                TextRenderer.DrawText(g, names[i], Theme.Small, new Rectangle((int)edges[i], Theme.S(10), (int)(edges[i + 1] - edges[i]), Theme.S(18)), Theme.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Now == null && Live == null)
            {
                TextRenderer.DrawText(g, L.T("Замера ещё не было"), Theme.Body, Rectangle.Round(plot), Theme.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var red = Theme.Heat(0.9f);
            if (Before != null && Live == null) Line(g, plot, Before, Theme.Muted, Theme.S(1.4f), false);
            if (Now != null) Line(g, plot, Now, Live == null ? red : Theme.Muted, Live == null ? Theme.S(2.2f) : Theme.S(1.4f), Live == null);
            if (Live != null) Line(g, plot, Live, red, Theme.S(2.2f), true);

            // the legend, bottom right: the curve is up there, the corner is free
            var legend = new List<Tuple<Color, string>>();
            if (Live != null) legend.Add(Tuple.Create(red, L.T("идёт замер")));
            if (Now != null) legend.Add(Tuple.Create(Live == null ? red : Theme.Muted, Now.Time.ToString("dd.MM HH:mm") + (Now.Mem.Length > 0 ? "  " + Now.Mem : "")));
            if (Before != null && Live == null) legend.Add(Tuple.Create(Theme.Muted, Before.Time.ToString("dd.MM HH:mm") + (Before.Mem.Length > 0 ? "  " + Before.Mem : "")));
            int lw = legend.Count == 0 ? 0 : legend.Max(t => TextRenderer.MeasureText(g, t.Item2, Theme.Small).Width) + Theme.S(38);
            int ly = (int)plot.Bottom - Theme.S(8) - legend.Count * Theme.S(18);
            foreach (var t in legend)
            {
                using (var pen = new Pen(t.Item1, Theme.S(2))) g.DrawLine(pen, plot.Right - lw, ly + Theme.S(9), plot.Right - lw + Theme.S(24), ly + Theme.S(9));
                TextRenderer.DrawText(g, t.Item2, Theme.Small, new Rectangle((int)plot.Right - lw + Theme.S(30), ly, lw, Theme.S(18)), Theme.Text2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                ly += Theme.S(18);
            }

            // the point under the mouse: its block size and delay (and the run before it at the same size)
            var top = Top;
            if (top != null && hot >= 0 && hot < top.Points.Count)
            {
                var p = top.Points[hot];
                float px = X(plot, p.Bytes), py = Y(plot, p.Ns);
                using (var pen = new Pen(Color.FromArgb(200, red), Math.Max(1f, Theme.S(1.2f))) { DashStyle = DashStyle.Dash })
                {
                    g.DrawLine(pen, plot.X, py, px, py);
                    g.DrawLine(pen, px, py, px, plot.Bottom);
                }
                Theme.GlassKnob(g, px, py, Theme.S(14), 1f, red, Theme.S(4f), false, 180f, new PointF?[0]);
                string text = Latency.Size(p.Bytes) + " · " + Latency.Ns(p.Ns) + " " + ns;
                if (Before != null && Live == null) { double b = Before.Points.Where(q => q.Bytes == p.Bytes).Select(q => q.Ns).DefaultIfEmpty(double.NaN).First(); if (!double.IsNaN(b)) text += " (" + L.T("было") + " " + Latency.Ns(b) + ")"; }
                int tw = TextRenderer.MeasureText(g, text, Theme.Small).Width + Theme.S(12), th = Theme.S(22);
                float tx = Math.Max(plot.X + Theme.S(2), Math.Min(plot.Right - tw, px - tw / 2f)), ty = Math.Max(plot.Y + Theme.S(2), py - th - Theme.S(14));
                var box = new RectangleF(tx, ty, tw, th);
                Theme.Fill(g, box, Theme.S(5), Color.FromArgb(240, Theme.Raised));
                Theme.Stroke(g, box, Theme.S(5), red);
                TextRenderer.DrawText(g, text, Theme.Small, Rectangle.Round(box), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        void Line(Graphics g, RectangleF plot, Latency.Run r, Color col, float width, bool dots)
        {
            var pts = r.Points.OrderBy(p => p.Bytes).Select(p => new PointF(X(plot, p.Bytes), Y(plot, p.Ns))).ToArray();
            if (pts.Length > 1) using (var pen = new Pen(col, width) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
            float rad = dots ? Theme.S(2.6f) : Theme.S(1.8f);
            using (var b = new SolidBrush(col)) foreach (var p in pts) g.FillEllipse(b, p.X - rad, p.Y - rad, 2 * rad, 2 * rad);
        }
    }
}
