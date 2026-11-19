using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RedTune
{
    // 14.11: a flight recorder for sudden resets. A Curve Optimizer value that is too low usually takes the laptop down at
    // once: no blue screen, no hardware error in the log, nothing to tell which core it was (six such resets after the first
    // tune, one more during a core-curve measurement). So every 2 seconds the last minute of every core's state (how busy,
    // its clock, its voltage, from the processor's telemetry table) is written to disk, straight through the cache. After a
    // restart that was not a clean one the last record says which cores were working, at what clock and voltage.
    // Only where the telemetry layout is known (CoreCurve.WhyNot); elsewhere it stays off.
    static class BlackBox
    {
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        static string FilePath { get { return Path.Combine(Store.DataDir, "blackbox.txt"); } }
        static string HistoryPath { get { return Path.Combine(Store.DataDir, "resets.txt"); } }

        static Thread thread;
        static volatile bool stop;
        static readonly List<string> lines = new List<string>();
        const int Keep = 30, EveryMs = 2000;

        // what the recorder saw just before the last unexpected restart; null when the last end was clean or nothing is known
        public static string LastReport { get; private set; }
        // the cores of that report that were busy (30 % and more), for the watchdog (16.11)
        public static List<int> LastCores = new List<int>();

        static DateTime BootTime { get { return DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64()); } }

        // at the app's start, before the recorder starts again: was the computer restarted without a clean end?
        public static string CheckLast()
        {
            LastReport = null;
            try
            {
                if (!File.Exists(FilePath)) return null;
                var all = File.ReadAllLines(FilePath, Encoding.UTF8);
                if (all.Length < 2 || all[0] != "running") return null; // "clean": the app or Windows closed properly
                var samples = all.Skip(1).Where(l => l.Contains("|")).ToList();
                if (samples.Count == 0) return null;
                var last = samples[samples.Count - 1].Split('|');
                DateTime at;
                if (!DateTime.TryParseExact(last[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) return null;
                // the computer has started again since that record: it was a restart, not just the app ending
                if (BootTime < at.AddSeconds(5)) return null;
                var busy = new List<Tuple<int, float, float, float>>();
                foreach (var part in last[1].Split(';'))
                {
                    var f = part.Split(':'); int core; float c0, ghz, volt;
                    if (f.Length == 4 && int.TryParse(f[0], out core) && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out c0)
                        && float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ghz) && float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out volt))
                        busy.Add(Tuple.Create(core, c0, ghz, volt));
                }
                var top = busy.Where(b => b.Item2 >= 30f).OrderByDescending(b => b.Item2).Take(4).ToList();
                LastCores = top.Select(b => b.Item1).ToList();
                string what = top.Count == 0
                    ? "все ядра почти в простое (самое занятое: " + string.Join(", ", busy.OrderByDescending(b => b.Item2).Take(2).Select(Describe)) + ")"
                    : string.Join(", ", top.Select(Describe));
                LastReport = at.ToString("dd.MM HH:mm:ss") + " · " + what;
                Store.Log("чёрный ящик: перезагрузка без завершения работы; последняя запись " + LastReport);
                try { File.AppendAllText(HistoryPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "|" + LastReport + Environment.NewLine, new UTF8Encoding(true)); } catch { }
                return LastReport;
            }
            catch (Exception ex) { Store.Log("чёрный ящик: " + ex.Message); return null; }
        }

        static string Describe(Tuple<int, float, float, float> b)
        {
            return Hw.CoreName(b.Item1) + " " + b.Item2.ToString("0") + " % " + b.Item3.ToString("0.00", CultureInfo.InvariantCulture) + " ГГц " + b.Item4.ToString("0.000", CultureInfo.InvariantCulture) + " В";
        }

        public static void Start()
        {
            if (thread != null || CoreCurve.WhyNot() != null) return;
            stop = false;
            thread = new Thread(() =>
            {
                while (!stop)
                {
                    try
                    {
                        var s = CoreCurve.Sample();
                        if (s != null)
                        {
                            lock (lines)
                            {
                                lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "|" + s);
                                while (lines.Count > Keep) lines.RemoveAt(0);
                                Write("running");
                            }
                        }
                    }
                    catch { }
                    Thread.Sleep(EveryMs);
                }
            }) { IsBackground = true, Name = "black box", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        // a clean end (the app quits, Windows signs out or shuts down): the next start will not take it for a reset
        public static void Stop()
        {
            stop = true;
            try { lock (lines) Write("clean"); } catch { }
        }

        static void Write(string state)
        {
            var text = state + "\r\n" + string.Join("\r\n", lines) + "\r\n";
            var bytes = new UTF8Encoding(false).GetBytes(text);
            Directory.CreateDirectory(Store.DataDir);
            // straight to the disk: a reset a moment later must not lose it in the cache
            using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                fs.Write(bytes, 0, bytes.Length);
        }
    }
}
