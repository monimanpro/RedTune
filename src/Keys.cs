using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // Lenovo Fn keys and lock keys, the way LLT handles them once Lenovo Hotkeys is off:
    // the firmware event LENOVO_UTILITY_EVENT (camera, Fn lock, Fn+R, Fn+PrtSc, Fn+F4, Fn+F8, backlight levels)
    // and the energy driver key event (Fn+F4, Fn+F8, Fn+F10, Fn+Space). While LLT runs it does all this itself,
    // so the actions here wait for it to be gone (otherwise Fn+F4 would toggle the microphone twice).
    class KeyService : IDisposable
    {
        ManagementEventWatcher watcher;
        Thread driverThread;
        volatile bool stop;
        readonly System.Windows.Forms.Timer locks = new System.Windows.Forms.Timer { Interval = 250 };
        bool caps = Control.IsKeyLocked(Keys.CapsLock), num = Control.IsKeyLocked(Keys.NumLock);
        readonly Dictionary<string, DateTime> last = new Dictionary<string, DateTime>();
        bool lltCache; DateTime lltChecked;

        bool LltRunning
        {
            get
            {
                if ((DateTime.Now - lltChecked).TotalSeconds > 5) { lltCache = PowerService.LltRunning; lltChecked = DateTime.Now; }
                return lltCache;
            }
        }

        public void Start()
        {
            locks.Tick += (s, e) =>
            {
                bool c = Control.IsKeyLocked(Keys.CapsLock), n = Control.IsKeyLocked(Keys.NumLock);
                if (c != caps) { caps = c; if (!LltRunning) Osd.Show("caps", c ? "Caps Lock включён" : "Caps Lock выключен"); }
                if (n != num) { num = n; if (!LltRunning) Osd.Show("num", n ? "Num Lock включён" : "Num Lock выключен"); }
            };
            locks.Start();

            // the Fn keys and the special keys come from Lenovo's firmware (WMI events, the EnergyDrv driver): elsewhere
            // there is nothing to listen to, only the lock keys above (13.11)
            if (!Hw.Device) return;

            try
            {
                watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new WqlEventQuery("SELECT * FROM LENOVO_UTILITY_EVENT"));
                watcher.EventArrived += (s, e) =>
                {
                    int v;
                    try { v = Convert.ToInt32(e.NewEvent["PressTypeDataVal"]); } catch { return; }
                    Task.Run(() => OnSpecial(v));
                };
                watcher.Start();
            }
            catch (Exception ex) { Store.Log("Fn-события: " + ex.Message); }

            driverThread = new Thread(DriverLoop) { IsBackground = true, Name = "fn keys" };
            driverThread.Start();
        }

        void DriverLoop()
        {
            try
            {
                using (var ev = new ManualResetEvent(false))
                {
                    Energy.BindKeyEvent(ev);
                    Energy.KeyValue(); // clear
                    while (!stop)
                    {
                        if (!ev.WaitOne(1000)) continue;
                        uint v = Energy.KeyValue();
                        ev.Reset();
                        OnDriverKey(v);
                    }
                }
            }
            catch (Exception ex) { Store.Log("Fn-клавиши драйвера: " + ex.Message); }
        }

        // the same key can come from both sources: act once per 600 ms
        bool Once(string what)
        {
            lock (last)
            {
                DateTime t;
                if (last.TryGetValue(what, out t) && (DateTime.Now - t).TotalMilliseconds < 600) return false;
                last[what] = DateTime.Now;
                return true;
            }
        }

        void OnDriverKey(uint v)
        {
            if (LltRunning) return;
            if ((v & 256) != 0) ToggleMic();
            if ((v & 8192) != 0) Airplane();
            if ((v & 32) != 0) { Thread.Sleep(300); try { bool off = Lenovo.GameZoneGet("GetTPStatus") == 1; Osd.Show("touchpad", off ? "Тачпад выключен" : "Тачпад включён", false, off); } catch { } }
            if ((v & 4096) != 0) { Thread.Sleep(200); try { if (Energy.KeyLightSupported) ShowLight(Energy.GetKeyLight()); } catch { } }
        }

        void OnSpecial(int v)
        {
            if (LltRunning) return;
            switch (v)
            {
                case 12: Osd.Show("camera", "Камера включена"); break;
                case 13: Osd.Show("camera", "Камера выключена", false, true); break;
                case 2: Osd.Show("fnlock", "Блокировка Fn включена"); break;
                case 3: Osd.Show("fnlock", "Блокировка Fn выключена", false, true); break;
                case 16: case 0x0041002A: ToggleRefresh(); break;
                case 4: case 45: if (Once("prtsc")) try { Process.Start("explorer", "ms-screenclip:"); } catch { } break;
                case 62: ToggleMic(); break;
                case 63: Airplane(); break;
                case 64: ShowLight(KeyLight.Off); break;
                case 65: ShowLight(KeyLight.Low); break;
                case 66: ShowLight(KeyLight.High); break;
            }
        }

        void ShowLight(KeyLight k) { Osd.Show("light", k == KeyLight.Off ? "Подсветка выключена" : k == KeyLight.Low ? "Подсветка: низкая" : "Подсветка: высокая"); }

        void ToggleMic()
        {
            if (!Once("mic")) return;
            try
            {
                var on = Audio.MicOn();
                if (on == null) return;
                Audio.SetMic(!on.Value);
                Osd.Show("mic", on.Value ? "Микрофон выключен" : "Микрофон включён", false, on.Value);
            }
            catch (Exception ex) { Store.Log("микрофон: " + ex.Message); }
        }

        void Airplane()
        {
            if (!Once("airplane")) return;
            try { Process.Start(new ProcessStartInfo("cmd", "/c start ms-settings:network-airplanemode") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            Osd.Show("airplane", "Режим полёта");
        }

        // Fn+R: next refresh rate of the built-in display at its current resolution
        void ToggleRefresh()
        {
            if (!Once("refresh")) return;
            try
            {
                var cur = Display.Current();
                var rates = Display.Modes().Where(m => m.Width == cur.Width && m.Height == cur.Height).Select(m => m.Hz).Distinct().OrderBy(x => x).ToList();
                if (rates.Count < 2) return;
                int next = rates[(rates.IndexOf(cur.Hz) + 1) % rates.Count];
                Display.SetMode(cur.Width, cur.Height, next);
                Osd.Show("refresh", "Частота обновления: " + next + " Hz");
            }
            catch (Exception ex) { Store.Log("Fn+R: " + ex.Message); }
        }

        public void Dispose()
        {
            stop = true;
            locks.Dispose();
            if (watcher != null) try { watcher.Stop(); watcher.Dispose(); } catch { }
        }
    }
}
