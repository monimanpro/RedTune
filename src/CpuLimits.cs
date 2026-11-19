using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedTune
{
    // The CPU power limit the SMU enforces now, from its power table (Smu.Limits; 17.11: the app's own code, before
    // a PowerShell script with ZenStates-Core). Lenovo's GetFeatureValue is no good here: it returns the custom-mode
    // numbers in every mode (12.11: 50 W shown in «Макс»).
    // Shown: the higher of STAPM and slow PPT, where the averaged reading can sit; fast PPT lasts only short bursts.
    static class CpuLimits
    {
        public static double Watts = double.NaN;
        static int busy;
        static DateTime last = DateTime.MinValue;

        // at most one run at a time; skipped when the last one was under minSeconds ago
        public static void Refresh(double minSeconds = 0)
        {
            if ((DateTime.Now - last).TotalSeconds < minSeconds) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            last = DateTime.Now;
            Task.Run(() =>
            {
                try
                {
                    var l = Smu.Limits(); // STAPM, fast, slow
                    if (l == null) return;
                    double w = Math.Max(l[0], l[2]);
                    Watts = w > 0 && w < 300 ? w : double.NaN;
                }
                catch { }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }
    }
}
