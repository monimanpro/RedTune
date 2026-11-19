using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RedTune.Ram
{
    // 14.11: a memory check inside the app, so that the curve auto-tune starts on memory known to be clean (mixed modules and
    // crashes before logon made the memory a suspect of its own). It takes the free memory, leaves a reserve to Windows, splits
    // it between all processor threads and goes round four tests, each of which writes data and compares what comes back:
    //   random data   a pseudo-random fill, read back, the inverse written over each word as it is checked, read back again
    //   block copy    one half copied onto the other in big and small blocks (the heaviest traffic on the memory bus)
    //   random access words visited in random order, checked and inverted (row changes instead of a steady stream)
    //   patterns      fixed patterns and a walking bit (stuck and coupled bits)
    // The memory is locked in RAM where Windows allows it, so the check does not turn into a check of the page file.
    // It finds unstable memory; it cannot tell which module or slot an error came from (only the virtual address is known).
    static unsafe class MemTest
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualLock(IntPtr addr, UIntPtr size);
        [DllImport("kernel32.dll")] static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out IntPtr min, out IntPtr max, out uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr min, IntPtr max, uint flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint f);

        // set by the main window: is the auto-tune running, and the curve that is applied now
        public static Func<bool> Busy;
        public static Func<int[]> CurveNow;

        public static volatile bool Running;
        public static event Action Finished; // on a worker thread
        public static bool Inject;           // the dry run's check of the checker: one bit flipped on purpose

        static volatile bool stop;
        static volatile string stage = "", failed;
        static long total, tested, errors, cycleSum;
        static int threads, limitSeconds;
        static bool locked, dry;
        static string memText = "";
        static DateTime started;
        static Stopwatch clock = new Stopwatch();
        static Thread control;
        static readonly List<string> errorLines = new List<string>();

        static string Dir { get { return Path.Combine(Store.DataDir, "Ram"); } }
        static string HistoryPath { get { return Path.Combine(Dir, "memtest.txt"); } }
        static string MarkerPath { get { return Path.Combine(Dir, "memtest.running"); } }
        static string LogPath { get { return Path.Combine(Dir, "memtest.log"); } }

        public static long Errors { get { return Interlocked.Read(ref errors); } }

        // ---------- one finished (or cut short) check ----------
        public class Record
        {
            public DateTime At; public int Seconds; public long Bytes, Tested, Errors; public string End = "", Mem = "";
            public string Line() { return At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "|" + Seconds + "|" + Bytes + "|" + Tested + "|" + Errors + "|" + End + "|" + Mem; }
            public static Record Parse(string s)
            {
                var f = (s ?? "").Split('|'); var r = new Record();
                if (f.Length < 7 || !DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out r.At)) return null;
                if (!int.TryParse(f[1], out r.Seconds) || !long.TryParse(f[2], out r.Bytes) || !long.TryParse(f[3], out r.Tested) || !long.TryParse(f[4], out r.Errors)) return null;
                r.End = f[5]; r.Mem = f[6];
                return r;
            }
        }

        public static Record Last()
        {
            try { return File.Exists(HistoryPath) ? File.ReadAllLines(HistoryPath, Encoding.UTF8).Select(Record.Parse).LastOrDefault(r => r != null) : null; }
            catch { return null; }
        }

        static string Gb(long bytes) { return (bytes / 1073741824.0).ToString(bytes >= 100L << 30 ? "0" : "0.0") + " ГБ"; }
        static string Span(int seconds) { return seconds >= 3600 ? seconds / 3600 + " ч " + (seconds % 3600 / 60).ToString("00") + " мин" : seconds >= 60 ? seconds / 60 + " мин" : seconds + " сек"; }
        static string Clock(int seconds) { return seconds >= 3600 ? seconds / 3600 + ":" + (seconds % 3600 / 60).ToString("00") + ":" + (seconds % 60).ToString("00") : seconds / 60 + ":" + (seconds % 60).ToString("00"); }
        static string ErrText(long n) { return n == 0 ? "ошибок нет" : "ОШИБОК: " + n + " — память нестабильна"; }

        // the line under «Проверка памяти»
        public static string StatusText()
        {
            if (Running)
            {
                if (clock.ElapsedMilliseconds < 1500 && Interlocked.Read(ref tested) == 0) return "Занимаю память…";
                int el = (int)clock.Elapsed.TotalSeconds;
                return "Идёт " + Clock(el) + (limitSeconds > 0 ? " из " + Clock(limitSeconds) : "") + " · " + Gb(total) + " · проверено " + Gb(Interlocked.Read(ref tested)) + " · " + ErrText(Errors) + " · " + stage;
            }
            if (failed != null) return "Не запустилась: " + failed;
            var r = Last();
            if (r == null) return "Ещё не запускалась";
            string end = r.End == "reset" ? " · оборвана перезагрузкой" : r.End == "closed" ? " · прервана" : r.End == "stopped" ? " · остановлена" : "";
            return "Последняя: " + r.At.ToString("dd.MM HH:mm") + " · " + Span(r.Seconds) + " · " + Gb(r.Bytes) + " · " + ErrText(r.Errors) + end + (r.Mem.Length > 0 ? " · " + r.Mem : "");
        }

        // errors to show in red: of the check that runs, or of the last one
        public static long ShownErrors { get { if (Running) return Errors; var r = Last(); return r != null ? r.Errors : 0; } }

        // ---------- start and stop ----------
        // what a check would take now: the free memory less a reserve for Windows and the programs that are open
        public static long PlanBytes()
        {
            var m = new MEMORYSTATUSEX { dwLength = 64 };
            if (!GlobalMemoryStatusEx(ref m)) return 0;
            long reserve = Math.Max(1536L << 20, (long)(m.ullTotalPhys / 12));
            return Math.Max(0, (long)m.ullAvailPhys - reserve);
        }

        // seconds = 0: until stopped. Returns the reason when it cannot start.
        public static string Start(int seconds, string mem, long bytes = 0, bool dryRun = false)
        {
            if (Running) return "Проверка уже идёт";
            long size = bytes > 0 ? bytes : PlanBytes();
            int n = Environment.ProcessorCount;
            while (n > 1 && size / n < (8L << 20)) n /= 2;
            long per = (size / n) & ~((1L << 21) - 1);
            if (per < (2L << 20) || (bytes == 0 && per * n < (1L << 30))) return "Мало свободной памяти: закройте тяжёлые программы";
            lock (errorLines) errorLines.Clear();
            failed = null; stage = ""; stop = false; dry = dryRun; memText = mem ?? ""; limitSeconds = seconds; threads = n;
            total = per * n; Interlocked.Exchange(ref tested, 0); Interlocked.Exchange(ref errors, 0); Interlocked.Exchange(ref cycleSum, 0);
            started = DateTime.Now; clock = Stopwatch.StartNew();
            Running = true;
            control = new Thread(() => Control(n, per)) { IsBackground = true, Name = "memtest" };
            control.Start();
            return null;
        }

        public static void Stop() { stop = true; }

        // the app is closing: end the check as a stopped one, not as one cut short
        public static void Abort()
        {
            if (!Running) return;
            stop = true;
            try { if (control != null) control.Join(3000); } catch { }
        }

        static void Note(string line)
        {
            if (dry) return;
            try { Directory.CreateDirectory(Dir); File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, new UTF8Encoding(true)); } catch { }
        }

        // rewritten every few seconds, straight to the disk: after a reset it says the check was running and for how long
        static void Mark()
        {
            if (dry) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var rec = new Record { At = started, Seconds = (int)clock.Elapsed.TotalSeconds, Bytes = total, Tested = Interlocked.Read(ref tested), Errors = Errors, End = "running", Mem = memText };
                var b = new UTF8Encoding(false).GetBytes(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" + rec.Line() + "\r\n");
                using (var fs = new FileStream(MarkerPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) fs.Write(b, 0, b.Length);
            }
            catch { }
        }

        // at the app's start: a check that never reached its end. Returns a text for the user when a restart cut it short.
        public static string CheckLast()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return null;
                var all = File.ReadAllLines(MarkerPath, Encoding.UTF8);
                File.Delete(MarkerPath);
                DateTime seen; Record r = all.Length >= 2 ? Record.Parse(all[1]) : null;
                if (r == null || !DateTime.TryParseExact(all[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out seen)) return null;
                var boot = DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64());
                bool reset = boot > seen.AddSeconds(5);
                r.End = reset ? "reset" : "closed";
                File.AppendAllText(HistoryPath, r.Line() + Environment.NewLine, new UTF8Encoding(true));
                Note("the check started " + r.At.ToString("dd.MM HH:mm:ss") + " did not finish: " + r.End + " after " + r.Seconds + " s, errors " + r.Errors);
                Store.Log("проверка памяти не завершилась: " + r.End + ", шла " + r.Seconds + " с");
                return reset ? "Проверка памяти оборвалась перезагрузкой: шла " + Span(r.Seconds) + ", " + ErrText(r.Errors) : null;
            }
            catch (Exception ex) { Store.Log("проверка памяти: " + ex.Message); return null; }
        }

        static void Control(int n, long per)
        {
            IntPtr oldMin = IntPtr.Zero, oldMax = IntPtr.Zero; uint oldFlags = 0; bool haveOld = false;
            try
            {
                SetThreadExecutionState(0x80000001); // the computer does not fall asleep in the middle
                // locking needs a working set as big as the memory under test
                haveOld = GetProcessWorkingSetSizeEx(GetCurrentProcess(), out oldMin, out oldMax, out oldFlags);
                locked = SetProcessWorkingSetSizeEx(GetCurrentProcess(), (IntPtr)(total + (192L << 20)), (IntPtr)(total + (2048L << 20)), 0x2 | 0x8);
                Note("start: " + Gb(total) + " in " + n + " threads, " + (limitSeconds > 0 ? Span(limitSeconds) : "until stopped") + ", " + memText + (locked ? "" : ", working set not raised (" + Marshal.GetLastWin32Error() + ")"));
                Mark();
                var workers = new Thread[n];
                for (int t = 0; t < n; t++)
                {
                    int id = t;
                    workers[t] = new Thread(() => Worker(id, per)) { IsBackground = true, Name = "memtest " + t, Priority = ThreadPriority.BelowNormal };
                    workers[t].Start();
                }
                long mark = 0;
                while (!stop && workers.Any(w => w.IsAlive))
                {
                    Thread.Sleep(200);
                    if (limitSeconds > 0 && clock.Elapsed.TotalSeconds >= limitSeconds) break;
                    if (clock.ElapsedMilliseconds - mark >= 5000) { mark = clock.ElapsedMilliseconds; Mark(); }
                }
                bool byUser = stop && failed == null;
                stop = true;
                foreach (var w in workers) w.Join();
                clock.Stop();
                var rec = new Record { At = started, Seconds = (int)clock.Elapsed.TotalSeconds, Bytes = total, Tested = Interlocked.Read(ref tested), Errors = Errors, End = failed != null ? "failed" : byUser ? "stopped" : "done", Mem = memText };
                Note("end: " + rec.End + ", " + rec.Seconds + " s, tested " + Gb(rec.Tested) + ", rounds " + Interlocked.Read(ref cycleSum) / Math.Max(1, n) + ", errors " + rec.Errors + (locked ? ", locked in RAM" : ", not locked") + (failed != null ? ", " + failed : ""));
                if (!dry && failed == null)
                {
                    try { Directory.CreateDirectory(Dir); File.AppendAllText(HistoryPath, rec.Line() + Environment.NewLine, new UTF8Encoding(true)); } catch { }
                    Store.Log("проверка памяти: " + Span(rec.Seconds) + ", " + Gb(rec.Bytes) + ", " + ErrText(rec.Errors));
                }
            }
            catch (Exception ex) { failed = ex.Message; }
            finally
            {
                if (!dry) try { File.Delete(MarkerPath); } catch { }
                if (haveOld) SetProcessWorkingSetSizeEx(GetCurrentProcess(), oldMin, oldMax, oldFlags & 0xF);
                SetThreadExecutionState(0x80000000);
                Running = false;
                var h = Finished; if (h != null) try { h(); } catch { }
            }
        }

        // ---------- the tests ----------
        const int Chunk = 1 << 16; // words between two looks at the stop flag

        static void Worker(int t, long per)
        {
            IntPtr mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)per, 0x3000, 4);
            if (mem == IntPtr.Zero) { failed = "Windows не дала память (" + (per >> 20) + " МБ на поток)"; stop = true; return; }
            try
            {
                if (locked && !VirtualLock(mem, (UIntPtr)(ulong)per)) locked = false;
                ulong* p = (ulong*)mem; long n = per / 8;
                ulong seed = ((ulong)Stopwatch.GetTimestamp() * 0x9E3779B97F4A7C15UL) ^ ((ulong)(t + 1) * 0xD1B54A32D192ED03UL) | 1;
                for (int cycle = 0; !stop; cycle++)
                {
                    seed = Mix(seed + (ulong)cycle) | 1;
                    if (t == 0) stage = "случайные данные";
                    if (!RandomData(p, n, seed, t)) break;
                    if (t == 0) stage = "копирование блоков";
                    if (!BlockCopy(p, n, seed ^ 0xA5A5A5A5A5A5A5A5UL, t)) break;
                    if (t == 0) stage = "случайный доступ";
                    if (!RandomAccess(p, n, seed ^ 0x3C3C3C3C3C3C3C3CUL, t)) break;
                    if (t == 0) stage = "шаблоны";
                    if (!Patterns(p, n, cycle, t)) break;
                    Interlocked.Increment(ref cycleSum);
                }
            }
            catch (Exception ex) { failed = ex.Message; stop = true; }
            finally { VirtualFree(mem, UIntPtr.Zero, 0x8000); }
        }

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        static void Bad(string test, int t, ulong* at, ulong want, ulong got)
        {
            long k = Interlocked.Increment(ref errors);
            if (k > 40) return; // the count goes on, the list does not
            ulong diff = want ^ got; var bits = new List<int>();
            for (int b = 0; b < 64; b++) if ((diff >> b & 1) != 0) bits.Add(b);
            var line = test + " · поток " + t + " · адрес 0x" + ((ulong)at).ToString("X") + " · ждали " + want.ToString("X16") + ", прочитано " + got.ToString("X16") + " · биты " + string.Join(",", bits);
            lock (errorLines) errorLines.Add(line);
            Note("ERROR " + line);
        }

        static void Add(long words) { Interlocked.Add(ref tested, words * 8); }

        static bool RandomData(ulong* p, long n, ulong seed, int t)
        {
            ulong x = seed; long i = 0;
            while (i < n) { if (stop) return false; long e = Math.Min(n, i + Chunk); for (; i < e; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; p[i] = x; } }
            if (Inject && t == 0) { Inject = false; p[n / 3] ^= 0x10; }
            // read back, and the inverse goes over each word right after it was checked
            x = seed; i = 0;
            while (i < n)
            {
                if (stop) return false; long e = Math.Min(n, i + Chunk);
                for (; i < e; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; ulong v = p[i]; if (v != x) Bad("случайные данные", t, p + i, x, v); p[i] = ~x; }
            }
            Add(n);
            x = seed; i = 0;
            while (i < n)
            {
                if (stop) return false; long e = Math.Min(n, i + Chunk);
                for (; i < e; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; ulong v = p[i]; if (v != ~x) Bad("случайные данные, инверсия", t, p + i, ~x, v); }
            }
            Add(n);
            return true;
        }

        static bool BlockCopy(ulong* p, long n, ulong seed, int t)
        {
            long half = n / 2; if (half < 2) return true;
            ulong x = seed; long i = 0;
            while (i < half) { if (stop) return false; long e = Math.Min(half, i + Chunk); for (; i < e; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; p[i] = x; } }
            // the first half onto the second in 4 MB blocks, taken from the end backwards
            const long Big = 1 << 19;
            for (long b = (half - 1) / Big * Big; b >= 0; b -= Big)
            {
                if (stop) return false;
                long len = Math.Min(Big, half - b);
                Buffer.MemoryCopy(p + b, p + half + b, len * 8, len * 8);
            }
            x = seed; i = 0;
            while (i < half)
            {
                if (stop) return false; long e = Math.Min(half, i + Chunk);
                for (; i < e; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; ulong v = p[half + i]; if (v != x) { Bad("копирование блоков", t, p + half + i, x, v); p[half + i] = x; } }
            }
            Add(half);
            // wipe the first half and bring it back from the copy in 32 KB blocks
            i = 0;
            while (i < half) { if (stop) return false; long e = Math.Min(half, i + Chunk); for (; i < e; i++) p[i] = 0; }
            const long Small = 1 << 12;
            for (long b = 0; b < half; b += Small)
            {
                if ((b & (Chunk - 1)) == 0 && stop) return false;
                long len = Math.Min(Small, half - b);
                Buffer.MemoryCopy(p + half + b, p + b, len * 8, len * 8);
            }
            x = seed; i = 0;
            while (i < half)
            {
                if (stop) return false; long e = Math.Min(half, i + Chunk);
                for (; i < e; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; ulong v = p[i]; if (v != x) Bad("копирование блоков, обратно", t, p + i, x, v); }
            }
            Add(half);
            return true;
        }

        static bool RandomAccess(ulong* p, long n, ulong seed, int t)
        {
            long i = 0;
            while (i < n) { if (stop) return false; long e = Math.Min(n, i + Chunk); for (; i < e; i++) p[i] = Mix((ulong)i ^ seed); }
            // a quarter of the words, in random order: each is checked and inverted
            ulong x = seed | 1; long hits = n / 4, k = 0;
            while (k < hits)
            {
                if (stop) return false; long e = Math.Min(hits, k + Chunk / 8);
                for (; k < e; k++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    long j = (long)(x % (ulong)n); ulong want = Mix((ulong)j ^ seed), v = p[j];
                    if (v == want) p[j] = ~want; else if (v == ~want) p[j] = want; else { Bad("случайный доступ", t, p + j, want, v); p[j] = want; }
                }
            }
            Add(hits);
            i = 0;
            while (i < n)
            {
                if (stop) return false; long e = Math.Min(n, i + Chunk);
                for (; i < e; i++) { ulong want = Mix((ulong)i ^ seed), v = p[i]; if (v != want && v != ~want) Bad("случайный доступ, сверка", t, p + i, want, v); }
            }
            Add(n);
            return true;
        }

        static readonly ulong[] Fixed = { 0UL, ~0UL, 0x5555555555555555UL, 0xAAAAAAAAAAAAAAAAUL, 0x00FF00FF00FF00FFUL, 0xFF00FF00FF00FF00UL, 0x0F0F0F0F0F0F0F0FUL, 0xF0F0F0F0F0F0F0F0UL };

        static bool Patterns(ulong* p, long n, int cycle, int t)
        {
            ulong a = Fixed[cycle % Fixed.Length]; long i = 0;
            while (i < n) { if (stop) return false; long e = Math.Min(n, i + Chunk); for (; i < e; i++) p[i] = a; }
            i = 0;
            while (i < n) { if (stop) return false; long e = Math.Min(n, i + Chunk); for (; i < e; i++) { ulong v = p[i]; if (v != a) Bad("шаблон " + a.ToString("X16"), t, p + i, a, v); } }
            Add(n);
            // a walking one on even rounds, a walking zero on odd ones
            ulong flip = (cycle & 1) == 0 ? 0UL : ~0UL; int sh = cycle % 64;
            i = 0;
            while (i < n) { if (stop) return false; long e = Math.Min(n, i + Chunk); for (; i < e; i++) p[i] = (1UL << (int)((i + sh) & 63)) ^ flip; }
            i = 0;
            while (i < n)
            {
                if (stop) return false; long e = Math.Min(n, i + Chunk);
                for (; i < e; i++) { ulong want = (1UL << (int)((i + sh) & 63)) ^ flip, v = p[i]; if (v != want) Bad("бегущий бит", t, p + i, want, v); }
            }
            Add(n);
            return true;
        }

        // ---------- a dry run for the build check: a small size, a few seconds, the result into a file ----------
        public static void RunToFile(string megabytes, string seconds, string file, bool inject)
        {
            var sb = new StringBuilder();
            try
            {
                long mb = long.Parse(megabytes); int sec = int.Parse(seconds);
                Inject = inject;
                var err = Start(sec, "dry run", mb << 20, true);
                if (err != null) sb.AppendLine("not started: " + err);
                else
                {
                    while (Running) Thread.Sleep(100);
                    sb.AppendLine("size " + Gb(total) + ", threads " + threads + ", locked " + locked + ", seconds " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                    sb.AppendLine("tested " + Gb(Interlocked.Read(ref tested)) + ", rounds per thread " + (Interlocked.Read(ref cycleSum) / (double)Math.Max(1, threads)).ToString("0.0", CultureInfo.InvariantCulture)
                                  + ", speed " + (Interlocked.Read(ref tested) / 1073741824.0 / Math.Max(0.1, clock.Elapsed.TotalSeconds)).ToString("0.0", CultureInfo.InvariantCulture) + " GB/s");
                    sb.AppendLine("errors " + Errors + (failed != null ? ", failed: " + failed : ""));
                    lock (errorLines) foreach (var l in errorLines) sb.AppendLine("  " + l);
                }
            }
            catch (Exception ex) { sb.AppendLine("exception: " + ex); }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
