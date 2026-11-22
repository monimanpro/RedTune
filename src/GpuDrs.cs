using System;
using System.Runtime.InteropServices;

namespace RedTune
{
    // 23.11 (the user's wish): «Держать частоты под нагрузкой», a part of a GPU preset (GpuPreset.Hold, set on «Видеокарта» under
    // the memory clock, applied with the preset: «Применить», start, power modes) = the NVIDIA driver's global «Power management
    // mode: Prefer maximum performance» (DRS setting PREFERRED_PSTATE 0x1057EB71 = 1 in the current global profile).
    // Why: the RTX 5070 Laptop's memory clock is fixed per P-state (P0 12001, P2 11001, P4 9001 MHz; the NVML memory offset
    // moves P0 and P2), and under full load the driver often sits in P2 after a short dip. Measured 22–23.11 with
    // RedTune-devtools\gpuload + nvidia-smi: P0 24 % → 84 % at stock and 54 % → 98 % at +1000 with the setting; at idle the
    // card still drops to P4 / 9001 (logs in RedTune-devtools\vram-pstate). The value lives in the driver, not in Red Tune:
    // it stays after a reboot and after Red Tune closes; «off» deletes the value (back to the driver's default), and the
    // uninstaller runs --gpu-uninstall for the same. Through nvapi64.dll's QueryInterface (the DRS functions are public NVAPI).
    static class GpuDrs
    {
        const uint PreferredPstate = 0x1057EB71, PreferMax = 1;
        const int SettingNotFound = -160;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr QI(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int F0();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FOut(out IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FH(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FHOut(IntPtr h, out IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FHPBuf(IntPtr h, IntPtr p, byte[] buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FHPIdBuf(IntPtr h, IntPtr p, uint id, byte[] buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FHPId(IntPtr h, IntPtr p, uint id);
        static T Fn<T>(uint id) where T : class
        {
            var p = QI(id);
            if (p == IntPtr.Zero) throw new Exception("NVAPI: нет функции " + id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // NVDRS_SETTING_V1: version, name (2048 UTF-16), id @4100, type @4104, location @4108, …, current value @8220; 12320 bytes
        const int SettingSize = 12320;
        static byte[] SettingBuf() { var b = new byte[SettingSize]; BitConverter.GetBytes((uint)(SettingSize | (1 << 16))).CopyTo(b, 0); return b; }

        static readonly object gate = new object();
        static bool inited;

        // a session on the current global profile; the callback gets (session, profile)
        static T WithGlobal<T>(Func<IntPtr, IntPtr, T> work)
        {
            lock (gate)
            {
                if (!inited) { int r = Fn<F0>(0x0150E828)(); if (r != 0) throw new Exception("NVAPI не запустился (код " + r + ")"); inited = true; }
                IntPtr h;
                int rc = Fn<FOut>(0x0694D52E)(out h); if (rc != 0) throw new Exception("профили драйвера недоступны (код " + rc + ")");
                try
                {
                    rc = Fn<FH>(0x375DBD6B)(h); if (rc != 0) throw new Exception("профили драйвера не прочитались (код " + rc + ")");
                    IntPtr p;
                    rc = Fn<FHOut>(0x617BFF9F)(h, out p); if (rc != 0) throw new Exception("нет глобального профиля (код " + rc + ")");
                    return work(h, p);
                }
                finally { try { Fn<FH>(0xDAD9CFF8)(h); } catch { } }
            }
        }

        // the setting is on (prefer maximum performance) in the global profile
        public static bool IsOn()
        {
            return WithGlobal((h, p) =>
            {
                var b = SettingBuf();
                int rc = Fn<FHPIdBuf>(0x73BF8338)(h, p, PreferredPstate, b);
                if (rc == SettingNotFound) return false;
                if (rc != 0) throw new Exception("не прочиталось (код " + rc + ")");
                return BitConverter.ToUInt32(b, 8220) == PreferMax;
            });
        }

        // on: the value 1; off: the value deleted (the driver's default). Returns null or the error
        public static string Set(bool on)
        {
            if (Hw.Simulated) return "Симуляция: в драйвер ничего не пишется";
            try
            {
                return WithGlobal((h, p) =>
                {
                    int rc;
                    if (on)
                    {
                        var b = SettingBuf();
                        BitConverter.GetBytes(PreferredPstate).CopyTo(b, 4100); // type 0 = DWORD
                        BitConverter.GetBytes(PreferMax).CopyTo(b, 8220);
                        rc = Fn<FHPBuf>(0x577DD202)(h, p, b);
                    }
                    else
                    {
                        rc = Fn<FHPId>(0xE4A26362)(h, p, PreferredPstate);
                        if (rc == SettingNotFound) return null; // nothing to take back
                    }
                    if (rc != 0) return "драйвер не принял настройку (код " + rc + ")";
                    rc = Fn<FH>(0xFCBC7E14)(h);
                    if (rc != 0) return "драйвер не сохранил настройку (код " + rc + ")";
                    Store.Log("видеокарта: держать частоты под нагрузкой — " + (on ? "вкл" : "выкл"));
                    return null;
                });
            }
            catch (Exception ex) { return ex.Message; }
        }

        // the value now, or null when the driver profiles cannot be read (no NVAPI, an old driver)
        public static bool? TryIsOn() { try { return IsOn(); } catch { return null; } }

        // a preset's value: written only when the driver has another one. null = on the driver now (or the profiles cannot
        // be read: nothing to do then), else the error
        public static string Ensure(bool want)
        {
            var now = TryIsOn();
            if (now == null || now.Value == want) return null;
            return Set(want);
        }
    }
}
