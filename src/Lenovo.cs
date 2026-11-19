using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace RedTune
{
    // Lenovo firmware interface (WMI root\WMI), the same one LLT uses. Checked on the R7000P (83LT) on 2026-11-11:
    // GetSmartFanMode = 255 in the custom mode; GetFeatureValue with the mode byte cleared (LLT: id & 0xFFFF00FF) returns
    // the live limits (50/50/50/85/50 W·°C, GPU 15/100/87/100/0 — what LLT showed).
    static class Lenovo
    {
        public const int Quiet = 1, Balance = 2, Performance = 3, Extreme = 224, Custom = 255;
        public const uint CpuFan = 0x04030001, GpuFan = 0x04030002, CpuTemp = 0x05040000, GpuTemp = 0x05050000;

        public const uint CpuShort = 0x01010000, CpuLong = 0x01020000, CpuPeak = 0x01030000, CpuTempLimit = 0x01040000, CpuCross = 0x01060000;
        public const uint GpuBoost = 0x02010000, GpuTgp = 0x02020000, GpuTempLimit = 0x02030000, GpuTotal = 0x02040000, GpuToCpu = 0x020B0000;
        public const uint FanFullSpeed = 0x04020000, FlipToStart = 0x00030000, InstantBootAc = 0x03010001, InstantBootUsbPd = 0x03010002;

        static ManagementObject gameZone, other, fan;
        static readonly object gate = new object();

        static ManagementObject Instance(string cls)
        {
            using (var s = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM " + cls)))
                return s.Get().Cast<ManagementObject>().FirstOrDefault();
        }

        static ManagementObject GameZone { get { return gameZone ?? (gameZone = Instance("LENOVO_GAMEZONE_DATA")); } }
        static ManagementObject Other { get { return other ?? (other = Instance("LENOVO_OTHER_METHOD")); } }
        static ManagementObject Fan { get { return fan ?? (fan = Instance("LENOVO_FAN_METHOD")); } }

        public static int GetPowerMode() { return GameZoneGet("GetSmartFanMode"); }
        public static void SetPowerMode(int mode) { GameZoneSet("SetSmartFanMode", mode); }

        public static int GameZoneGet(string method)
        {
            lock (gate) return Convert.ToInt32(GameZone.InvokeMethod(method, null, null)["Data"]);
        }

        public static void GameZoneSet(string method, int value)
        {
            lock (gate)
            {
                var inp = GameZone.GetMethodParameters(method);
                inp["Data"] = (uint)value;
                GameZone.InvokeMethod(method, inp, null);
            }
        }

        // int.MinValue when the firmware refuses
        public static int GetFeature(uint id)
        {
            try
            {
                lock (gate)
                {
                    var inp = Other.GetMethodParameters("GetFeatureValue");
                    inp["IDs"] = id;
                    return Convert.ToInt32(Other.InvokeMethod("GetFeatureValue", inp, null)["Value"]);
                }
            }
            catch { return int.MinValue; }
        }

        // ---------- AMD overclocking (LLT 2.36 GodModeController.ApplyOcIfNeededAsync) ----------
        // Works only when the BIOS overclocking mode is on: GetBIOSOCMode = 3 (here 3 on 12.11). CPUOverclockingEnable
        // switches it on (1) or off (0); the values go through LENOVO_CPU_METHOD.CPU_Set_OC_Data(mode 17, TuneID, value)
        // and cannot be read back, so the preset keeps them (as LLT does). Ranges as in LLT: scaler 1..10, boost 0..200 MHz,
        // all-core curve 0..20.
        public const uint CpuOcEnable = 0x0108FF00, PboScalar = 0x414D4401, PboBoost = 0x414D4402, AllCoreCurve = 0x414D4403;

        public static bool BiosOcOn
        {
            get { try { return GameZoneGet("GetBIOSOCMode") == 3; } catch { return false; } }
        }

        static ManagementObject cpuMethod;
        static ManagementObject CpuMethod { get { return cpuMethod ?? (cpuMethod = Instance("LENOVO_CPU_METHOD")); } }

        public static void SetOcData(uint tuneId, int value)
        {
            lock (gate)
            {
                var inp = CpuMethod.GetMethodParameters("CPU_Set_OC_Data");
                inp["mode"] = (byte)17;
                inp["TuneID"] = tuneId;
                inp["value"] = (uint)Math.Max(0, value);
                CpuMethod.InvokeMethod("CPU_Set_OC_Data", inp, null);
            }
        }

        public static void SetFeature(uint id, int value)
        {
            lock (gate)
            {
                var inp = Other.GetMethodParameters("SetFeatureValue");
                inp["IDs"] = id;
                inp["value"] = (uint)Math.Max(0, value);
                Other.InvokeMethod("SetFeatureValue", inp, null);
            }
        }

        // 10 points, each a fan level 1..10 (LLT's FanTable: FSTM=1, FSID=0, FSTL=0, then 10 ushorts, padded to 64 bytes)
        public static void SetFanTable(int[] levels)
        {
            var b = new byte[64];
            b[0] = 1;
            for (int i = 0; i < 10; i++) BitConverter.GetBytes((ushort)levels[i]).CopyTo(b, 6 + i * 2);
            lock (gate)
            {
                var inp = Fan.GetMethodParameters("Fan_Set_Table");
                inp["FanTable"] = b;
                Fan.InvokeMethod("Fan_Set_Table", inp, null);
            }
        }

        // factory values of a mode (LENOVO_CAPABILITY_DATA_01 DefaultValue; the mode is the 3rd byte of the id: 1 quiet, 2 balance, 3 performance),
        // keyed by the plain feature id (mode byte cleared), the same keys SetFeature takes
        public static Dictionary<uint, int> ModeDefaults(int mode)
        {
            var d = new Dictionary<uint, int>();
            using (var s = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM LENOVO_CAPABILITY_DATA_01")))
                foreach (ManagementObject o in s.Get())
                {
                    uint id = Convert.ToUInt32(o["IDs"]);
                    if (((id >> 8) & 0xFF) != mode) continue;
                    d[id & 0xFFFF00FF] = Convert.ToInt32(o["DefaultValue"]);
                }
            return d;
        }

        // ---------- GPU working mode (LLT HybridModeFeature, read on the 83LT 2026-11-12: IsSupportGSync 2, IsSupportIGPUMode 3) ----------
        // GSync 1 = only the discrete GPU (hybrid off), changes after a reboot; IGPU mode 0 = hybrid, 1 = iGPU only, 2 = auto.
        // Like LLT, a GSync change is written alone; the iGPU part of the wish waits for the next start (PendingIgpu).
        public const int GpuHybrid = 0, GpuHybridIgpu = 1, GpuHybridAuto = 2, GpuDiscrete = 3;
        public static readonly string[] GpuModeNames = { "Гибридный режим", "Гибридный iGPU", "Hybrid-Auto", "Дискретная видеокарта" };
        static string PendingIgpuFile { get { return System.IO.Path.Combine(Store.DataDir, "igpu-mode.pending"); } }

        public static bool GSyncSupported { get { try { return GameZoneGet("IsSupportGSync") > 0; } catch { return false; } } }
        public static bool IgpuModeSupported { get { try { return GameZoneGet("IsSupportIGPUMode") > 0; } catch { return false; } } }

        public static int GetGpuMode()
        {
            if (GSyncSupported && GameZoneGet("GetGSyncStatus") == 1) return GpuDiscrete;
            if (!IgpuModeSupported) return GpuHybrid;
            int i = GameZoneGet("GetIGPUModeStatus");
            return i == 1 ? GpuHybridIgpu : i == 2 ? GpuHybridAuto : GpuHybrid;
        }

        // true when the change needs a reboot (GSync); the iGPU mode itself switches in Windows, a reboot finishes it too
        public static bool SetGpuMode(int mode)
        {
            int gsync = mode == GpuDiscrete ? 1 : 0, igpu = mode == GpuHybridIgpu ? 1 : mode == GpuHybridAuto ? 2 : 0;
            if (GSyncSupported && GameZoneGet("GetGSyncStatus") != gsync)
            {
                GameZoneSet("SetGSyncStatus", gsync);
                try
                {
                    if (gsync == 0 && IgpuModeSupported && GameZoneGet("GetIGPUModeStatus") != igpu) System.IO.File.WriteAllText(PendingIgpuFile, igpu.ToString());
                    else System.IO.File.Delete(PendingIgpuFile);
                }
                catch { }
                Store.Log("видеокарта: GSync -> " + gsync + " (" + GpuModeNames[mode] + "), нужна перезагрузка");
                return true;
            }
            if (IgpuModeSupported && GameZoneGet("GetIGPUModeStatus") != igpu) { GameZoneSet("SetIGPUModeStatus", igpu); Store.Log("видеокарта: режим iGPU -> " + igpu + " (" + GpuModeNames[mode] + ")"); }
            return false;
        }

        // after the reboot that turned hybrid on: the iGPU part of the chosen mode
        public static void ApplyPendingIgpu()
        {
            try
            {
                if (!System.IO.File.Exists(PendingIgpuFile)) return;
                int igpu = int.Parse(System.IO.File.ReadAllText(PendingIgpuFile).Trim());
                if (GameZoneGet("GetGSyncStatus") == 0 && IgpuModeSupported) { GameZoneSet("SetIGPUModeStatus", igpu); Store.Log("видеокарта: после перезагрузки режим iGPU -> " + igpu); }
                System.IO.File.Delete(PendingIgpuFile);
            }
            catch (Exception ex) { Store.Log("видеокарта: " + ex.Message); }
        }

        public static readonly int[] DefaultFanTable = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        public static readonly int[] MinimumFanTable = { 1, 1, 1, 1, 1, 1, 1, 1, 3, 5 };

        // custom-mode fan table thresholds for the hover hints: CPU sensor 4, GPU sensor 5
        public static void FanTemps(out int[] cpu, out int[] gpu, out int[] rpm)
        {
            cpu = gpu = rpm = null;
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM LENOVO_FAN_TABLE_DATA WHERE Mode = 255")))
                    foreach (ManagementObject o in s.Get())
                    {
                        int fanId = Convert.ToInt32(o["Fan_Id"]), sensor = Convert.ToInt32(o["Sensor_ID"]);
                        var temps = ((Array)o["SensorTable_Data"]).Cast<object>().Select(Convert.ToInt32).ToArray();
                        if (fanId == 1 && sensor == 4) { cpu = temps; rpm = ((Array)o["FanTable_Data"]).Cast<object>().Select(Convert.ToInt32).ToArray(); }
                        if (fanId == 2 && sensor == 5) gpu = temps;
                    }
            }
            catch { }
        }
    }
}
