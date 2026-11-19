using System;
using System.IO;
using System.Windows.Forms;

namespace RedTune
{
    // Safe start (13.11, a step to other laptops): the curve, the power limits and the overclocking are what can make a
    // PC unstable, so their automatic application at start is guarded.
    //   · Shift held while Red Tune starts, or the --safe switch: nothing is applied, the home page says so and offers
    //     «Применить сейчас»;
    //   · crash guard: before applying, a flag file is written; it is removed after 3 minutes of running, on a normal exit
    //     and when Windows signs out. Still there at the next start = the last start crashed or froze soon after applying:
    //     this time nothing is applied either. A running auto-tune is left out (it expects crashes and handles them itself).
    static class Safety
    {
        public static bool SafeStart { get; private set; }
        public static bool Manual { get; private set; }  // Shift or --safe, not the crash guard
        public static string Reason = "";
        public static event Action Resumed;               // «Применить сейчас» on the home page
        public const int StableMinutes = 3;

        static string Flag { get { return Path.Combine(Store.DataDir, "applying.flag"); } }
        static System.Threading.Timer timer;

        public static void Begin(string[] args, bool tunerRunning)
        {
            if (Array.Exists(args, a => a.Equals("--safe", StringComparison.OrdinalIgnoreCase))) { SafeStart = Manual = true; Reason = "запуск с ключом --safe"; }
            else if ((Control.ModifierKeys & Keys.Shift) != 0) { SafeStart = Manual = true; Reason = "при запуске был зажат Shift"; }
            else if (File.Exists(Flag) && !tunerRunning) { SafeStart = true; Reason = "прошлый запуск оборвался сразу после них"; }
            if (SafeStart)
            {
                Store.Log("безопасный запуск: " + Reason);
                try { File.Delete(Flag); } catch { }
                return;
            }
            Arm();
        }

        static void Arm()
        {
            try { Directory.CreateDirectory(Store.DataDir); File.WriteAllText(Flag, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
            timer = new System.Threading.Timer(_ => Stable(), null, StableMinutes * 60000, System.Threading.Timeout.Infinite);
        }

        // ran long enough, or is closing normally: the next start may apply again
        public static void Stable() { try { File.Delete(Flag); } catch { } }

        // «Применить сейчас»: back to normal, guarded again
        public static void Resume()
        {
            if (!SafeStart) return;
            SafeStart = Manual = false; Reason = "";
            Store.Log("безопасный запуск: применяю по кнопке");
            Arm();
            var h = Resumed; if (h != null) h();
        }
    }
}
