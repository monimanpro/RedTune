using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RedTune
{
    // ASUS laptops (19.11, after the report of a Vivobook Pro 14 OLED M3401QA with a Ryzen 5 5600H): the firmware is asked
    // and told through ASUS's own driver, the device \\.\ATKACPI that MyASUS and Armoury Crate use — the method DSTS reads
    // the state of a "device" (a setting with a number), DEVS sets it. The numbers and what the values mean are those of
    // the Linux kernel's asus-wmi driver and of G-Helper (see THIRD-PARTY.md); both agree on everything used here.
    //
    // What this file can do, each only where the firmware says it has the device (bit 0x10000 of the answer):
    //   - the performance mode (Vivobook: 0 balanced, 1 quiet, 2 performance; ROG/TUF: 0 balanced, 1 turbo, 2 quiet);
    //   - the fan speeds, read only (the answer's low 16 bits, in hundreds of turns a minute);
    //   - the battery's charge limit, in percent (the firmware cannot be asked for it back, so the choice is kept in the
    //     settings and given again at every start);
    //   - the keyboard's backlight level (0..3) and the Fn lock.
    // NOT TRIED ON A REAL ASUS LAPTOP by the time of writing: written from the report and the two sources above. Every
    // write goes to the journal with the firmware's answer (1 = taken), so a report shows what happened.
    // RT_HW_SIM=asus pretends to be that Vivobook: the answers of its report, nothing reaches a driver.
    static class Asus
    {
        public const uint VivoMode = 0x00110019, RogMode = 0x00120075, CpuFan = 0x00110013, GpuFan = 0x00110014,
            BatteryLimit = 0x00120057, KbdLight = 0x00050021, FnLock = 0x00100023;
        const uint Ioctl = 0x0022240C, Dsts = 0x53545344, Devs = 0x53564544;
        public const int Failed = int.MinValue;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, ref uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static readonly object gate = new object();
        static bool? driver;
        // the Vivobook of the report: what its firmware answered (DSTS), by device number
        static Dictionary<uint, uint> sim;
        public static bool Simulated { get { return sim != null; } }
        public static void Simulate()
        {
            sim = new Dictionary<uint, uint> { { VivoMode, 0x00030000 }, { CpuFan, 0x00010019 }, { GpuFan, 0x00010018 }, { BatteryLimit, 0x00090000 }, { KbdLight, 0x00358000 }, { FnLock, 0x00010000 } };
        }

        // the driver is there (ASUS System Control Interface, installed with the laptop's drivers)
        public static bool Available
        {
            get
            {
                if (sim != null) return true;
                lock (gate)
                {
                    if (driver == null)
                    {
                        IntPtr h = Open();
                        driver = Good(h);
                        if (Good(h)) CloseHandle(h);
                    }
                    return driver.Value;
                }
            }
        }

        static IntPtr Open() { try { return CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero); } catch { return IntPtr.Zero; } }
        static bool Good(IntPtr h) { return h != IntPtr.Zero && h != new IntPtr(-1); }

        // one call of the firmware: the method, the device and (for DEVS) the value; the first four bytes of the answer
        static bool Call(uint method, uint id, uint value, out uint answer)
        {
            answer = 0;
            lock (gate)
            {
                IntPtr h = Open();
                if (!Good(h)) return false;
                try
                {
                    var inBuf = new byte[16];
                    BitConverter.GetBytes(method).CopyTo(inBuf, 0); BitConverter.GetBytes(8u).CopyTo(inBuf, 4);
                    BitConverter.GetBytes(id).CopyTo(inBuf, 8); BitConverter.GetBytes(value).CopyTo(inBuf, 12);
                    var outBuf = new byte[16]; uint got = 0;
                    if (!DeviceIoControl(h, Ioctl, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, ref got, IntPtr.Zero)) return false;
                    answer = BitConverter.ToUInt32(outBuf, 0);
                    return true;
                }
                catch { return false; }
                finally { CloseHandle(h); }
            }
        }

        // the firmware's answer for a device as it is, or null when it gave none
        public static uint? Raw(uint id)
        {
            if (sim != null) { uint s; return sim.TryGetValue(id, out s) ? s : (uint?)null; }
            uint v; return Call(Dsts, id, 0, out v) ? v : (uint?)null;
        }

        public static bool Has(uint id)
        {
            var v = Raw(id);
            return v != null && (v.Value & 0x10000) != 0 && v.Value != 0xFFFFFFFE && v.Value != 0xFFFFFFFF;
        }

        // the low 16 bits of a device's state, or Failed
        public static int Get(uint id) { var v = Raw(id); return v == null ? Failed : (int)(v.Value & 0xFFFF); }

        // sets a device; the firmware's answer (1 = taken) or Failed. Written to the journal under the given name.
        public static int Set(uint id, int value, string what)
        {
            int result;
            if (sim != null) { uint was; sim.TryGetValue(id, out was); sim[id] = (was & 0xFFFF0000) | ((uint)value & 0xFFFF); result = 1; }
            else { uint a; result = Call(Devs, id, (uint)value, out a) ? (int)a : Failed; }
            try { Store.Log("ASUS: " + what + " = " + value + " (0x" + id.ToString("X8") + "): " + (result == 1 ? "принято" : result == Failed ? "драйвер не ответил" : "ответ " + result) + (sim != null ? " [симуляция]" : "")); } catch { }
            return result;
        }

        // --asus-check <file>: the numbers that go to the firmware, against the pretended Vivobook of the report (no driver is touched)
        public static string Check()
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            Simulate();
            line(Available && Has(VivoMode) && !Has(RogMode) && HasModes, "the Vivobook's mode device is there, the ROG/TUF one is not");
            line(FanRpm(CpuFan) == 2500 && FanRpm(GpuFan) == 2400 && FanRpm(0x00110031) == -1, "fans: " + FanRpm(CpuFan) + " and " + FanRpm(GpuFan) + " rpm (2500 and 2400 in the report), the middle fan there is none of");
            int q = SetMode(0), vq = Get(VivoMode), b = SetMode(1), vb = Get(VivoMode), p = SetMode(2), vp = Get(VivoMode);
            line(q == 1 && b == 1 && p == 1 && vq == 1 && vb == 0 && vp == 2, "modes quiet / balanced / performance go to the Vivobook as " + vq + " / " + vb + " / " + vp + " (1 / 0 / 2)");
            line(SetMode(3) == Failed && Get(VivoMode) == 2, "a mode there is none of is not sent");
            SetChargeLimit(80); int c80 = Get(BatteryLimit); SetChargeLimit(10); int cLow = Get(BatteryLimit); SetChargeLimit(250); int cHigh = Get(BatteryLimit);
            line(c80 == 80 && cLow == 40 && cHigh == 100, "the charge limit goes as percent: 80 -> " + c80 + "; kept within 40..100 (" + cLow + ", " + cHigh + ")");
            SetKeyboardLight(2); int k2 = Get(KbdLight); SetKeyboardLight(0); int k0 = Get(KbdLight); SetKeyboardLight(9); int k9 = Get(KbdLight);
            line(k2 == 0x82 && k0 == 0x80 && k9 == 0x83, "the keyboard light goes with the flag 0x80: level 2 -> 0x" + k2.ToString("X") + ", off -> 0x" + k0.ToString("X") + ", too much -> 0x" + k9.ToString("X"));
            SetFnLock(true); int f1 = Get(FnLock); SetFnLock(false); int f0 = Get(FnLock);
            line(f1 == 1 && f0 == 0, "the Fn lock goes as 1 and 0");
            line(!Has(0x001200A0) && Raw(0x001200A0) == null, "a device the report says is missing (a power limit) is not there");
            var st = new Settings { AsusCharge = 60, AsusMode = 0 }; ApplySaved(st);
            line(Get(BatteryLimit) == 60 && Get(VivoMode) == 1, "at a start the saved charge limit (60) and mode (quiet) are given again");
            var none = new Settings(); SetChargeLimit(80); SetMode(2); ApplySaved(none);
            line(Get(BatteryLimit) == 80 && Get(VivoMode) == 2, "with nothing picked in the settings a start sends nothing");
            return sb.ToString();
        }

        // ---------- what the pages use ----------

        // turns a minute, or -1 (no such fan, or an answer that is no speed)
        public static int FanRpm(uint id)
        {
            var v = Raw(id);
            if (v == null || (v.Value & 0x10000) == 0) return -1;
            int f = (int)(v.Value & 0xFFFF);
            return f > 120 ? -1 : f * 100;
        }

        // the performance mode as the pages count it: 0 quiet, 1 balanced, 2 performance
        public static bool HasModes { get { return Has(RogMode) || Has(VivoMode); } }
        static readonly int[] VivoValue = { 1, 0, 2 }, RogValue = { 2, 0, 1 };
        public static int SetMode(int mode)
        {
            if (mode < 0 || mode > 2) return Failed;
            // (the order G-Helper asks in: the ROG/TUF device first, the Vivobook one where that is missing)
            return Has(RogMode) ? Set(RogMode, RogValue[mode], "режим производительности") : Set(VivoMode, VivoValue[mode], "режим производительности (Vivobook)");
        }

        public static int SetChargeLimit(int percent) { return Set(BatteryLimit, Math.Max(40, Math.Min(100, percent)), "предел заряда, %"); }
        public static int SetKeyboardLight(int level) { return Set(KbdLight, 0x80 | (Math.Max(0, Math.Min(3, level)) & 0x7F), "подсветка клавиатуры"); }
        public static int SetFnLock(bool on) { return Set(FnLock, on ? 1 : 0, "блокировка Fn"); }

        // what the firmware forgets is given again when the program starts: the charge limit (it has no way to be read
        // back, and ASUS's own program sets it anew as well) and the performance mode picked here
        public static void ApplySaved(Settings s)
        {
            try
            {
                if (!Available) return;
                if (s.AsusCharge >= 40 && s.AsusCharge < 100 && Has(BatteryLimit)) SetChargeLimit(s.AsusCharge);
                if (s.AsusMode >= 0 && s.AsusMode <= 2 && HasModes) SetMode(s.AsusMode);
            }
            catch (Exception ex) { try { Store.Log("ASUS, при запуске: " + ex.Message); } catch { } }
        }
    }
}
