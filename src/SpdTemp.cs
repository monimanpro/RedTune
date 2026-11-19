using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RedTune
{
    // 16.11: the temperature of the memory modules, for «Главная». Every DDR5 module carries an SPD hub (SPD5118) with a
    // temperature sensor in it; the hubs sit on the SMBus of the AMD chipset (PCI 1022:790B, its host controller at I/O
    // 0xB00, the PIIX4 kind) at addresses 0x50..0x57. Found on the Legion R7000P: two hubs, 0x50 and 0x51, 55 °C idle.
    // Reads only: the one SMBus command used is «read byte data» (the address with the read bit and a register number),
    // the controller's port selection is not touched, and the bus is taken under the mutex that HWiNFO, CPU-Z and
    // LibreHardwareMonitor share.
    // 17.11: the bus is reached through PawnIO's SmbusPIIX4 module (ram\SmbusPIIX4.bin, the one ZenStates-Core
    // carries) instead of inpoutx64.dll and its own kernel driver: the module does the transaction and refuses to load
    // on any other controller. DDR4 modules have no hub (Sensors asks only for DDR5), another chipset has no such
    // controller, no PawnIO: then there is no temperature and «Главная» shows the card without it.
    static class SpdTemp
    {
        static readonly object gate = new object();
        static bool tried;
        static IntPtr bus = IntPtr.Zero;
        static int[] hubs = new int[0];
        static float[] last = new float[0];
        static DateTime at = DateTime.MinValue;

        public static int[] Hubs { get { return hubs; } }

        // °C of every module that has a sensor, in the order of their addresses; empty when there is none.
        // At most one round of the bus in 3 s; a busy bus leaves the last reading
        public static float[] Read()
        {
            lock (gate)
            {
                if ((DateTime.Now - at).TotalSeconds < 3) return last;
                at = DateTime.Now;
                try
                {
                    if (!tried) { tried = true; Find(); }
                    if (hubs.Length == 0) return last;
                    var now = Bus(() => hubs.Select(Temp).Where(t => !float.IsNaN(t)).ToArray());
                    if (now != null) last = now;
                }
                catch (Exception ex) { Store.Log("температура памяти: " + ex.Message); hubs = new int[0]; last = new float[0]; }
                return last;
            }
        }

        static void Find()
        {
            if (!Hw.Amd) return;
            if (bus == IntPtr.Zero) bus = Tctl.LoadModule("SmbusPIIX4.bin");
            if (bus == IntPtr.Zero) { Store.Log("температура памяти: шина SMBus недоступна (нет PawnIO или контроллер не AMD)"); return; }
            var found = Bus(() =>
            {
                var list = new List<int>();
                for (int a = 0x50; a <= 0x57; a++)
                    if (ReadByte(a, 0) == 0x51 && ReadByte(a, 1) == 0x18) list.Add(a); // the device type: SPD5118
                return list.ToArray();
            });
            hubs = found ?? new int[0];
            if (found == null) tried = false; // the bus was busy: look again next time
            else Store.Log("температура памяти: датчики на модулях: " + (hubs.Length > 0 ? string.Join(", ", hubs.Select(a => "0x" + a.ToString("X2"))) : "нет"));
        }

        // registers 0x31 (low) and 0x32 (high): 0.25 °C per step from bit 2, the sign in bit 12. The two bytes are read
        // apart, so the high one is read on both sides of the low one and has to agree
        static float Temp(int addr)
        {
            for (int n = 0; n < 2; n++)
            {
                int hi = ReadByte(addr, 0x32), lo = ReadByte(addr, 0x31), hi2 = ReadByte(addr, 0x32);
                if (hi < 0 || lo < 0 || hi2 < 0) return float.NaN;
                if (hi != hi2) continue;
                int v = (((hi & 0x1F) << 8) | lo) >> 2;
                if ((hi & 0x10) != 0) v -= 2048;
                float t = v * 0.25f;
                return t > -40 && t < 125 ? t : float.NaN;
            }
            return float.NaN;
        }

        static T Bus<T>(Func<T> f) where T : class
        {
            Mutex m = null;
            try
            {
                m = new Mutex(false, "Global\\Access_SMBUS.HTP.Method");
                bool got;
                try { got = m.WaitOne(100); } catch (AbandonedMutexException) { got = true; }
                if (!got) return null;
                try { return f(); } finally { m.ReleaseMutex(); }
            }
            finally { if (m != null) m.Dispose(); }
        }

        // one «read byte data» through the module: the address, 1 = read, the register, 2 = byte data; the byte comes
        // back in the first cell. Below zero: no such device, or the controller did not finish
        static int ReadByte(int addr, int reg)
        {
            var o = new ulong[1];
            return Tctl.Call(bus, "ioctl_smbus_xfer", new ulong[] { (ulong)addr, 1, (ulong)reg, 2 }, o) ? (int)(o[0] & 0xFF) : -1;
        }
    }
}
