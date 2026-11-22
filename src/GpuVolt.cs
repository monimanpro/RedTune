using System;
using System.Runtime.InteropServices;

namespace RedTune
{
    // 23.11 (the user's wish, like mVolt+'s «Core voltage offset»): the core clock domain's voltage-demand offset. Positive asks
    // for more voltage at every clock, negative for less (the rail limits and other domains on the rail still have the last
    // word). Worked out from mVolt+ 0.50.13 (static RE, RedTune-devtools\gpu-domain-vf-notes.md) and checked on the RTX 5070
    // Laptop, driver 621.14, with the user's word: −10 mV moved every point of the curve's evaluated voltage by −10 mV (Status v2
    // +0x8 vs +0x10), 0 put it back; NVML memory writes and curve writes leave it alone.
    //   D 0x57B5A5DF clock domains INFO (v4, 0x86AC): mask +8, domain k at 0xAC + k*0x430: +0 type, +4 clock key (core = 0)
    //   0xF58938F5 / 0xD14B69CF clock domains CONTROL get / set (v2, 0x61A4): mask +8 = 1 << k, entry k at 0x124 + k*0x304
    //   (+0 type, the same as D's: 10 or 15); four rail slots of int µV at k*0x304 + 0x23C (type 15) or + 0x234 (types 1, 3..10),
    //   slot 0 = NVVDD, the core's rail. Written like mVolt+: the buffer as read with one slot changed, read back, and the old
    //   value written again when the driver did not keep the new one.
    // The value lives in the driver until a reboot, so the preset brings it back at start (GpuStore.ApplySaved).
    static class GpuVolt
    {
        public const int MinMv = -100, MaxMv = 50; // the driver reports no bounds (mVolt+ shows «unknown» too); our own frame

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] static extern IntPtr Q(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out] IntPtr[] gpus, out int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BufFn(IntPtr gpu, byte[] buf);

        const uint InfoId = 0x57B5A5DF, GetId = 0xF58938F5, SetId = 0xD14B69CF;
        const int InfoSize = 0x86AC, InfoVer = 4, CtlSize = 0x61A4, CtlVer = 2;
        static readonly object gate = new object();

        public static string Error;

        static int Call(uint id, IntPtr gpu, byte[] buf)
        {
            var p = Q(id);
            if (p == IntPtr.Zero) return -1;
            return ((BufFn)Marshal.GetDelegateForFunctionPointer(p, typeof(BufFn)))(gpu, buf);
        }

        static IntPtr Gpu()
        {
            var init = Q(0x0150E828); var en = Q(0xE5AC921F);
            if (init == IntPtr.Zero || en == IntPtr.Zero) return IntPtr.Zero;
            if (((InitFn)Marshal.GetDelegateForFunctionPointer(init, typeof(InitFn)))() != 0) return IntPtr.Zero;
            var list = new IntPtr[64]; int n;
            return ((EnumFn)Marshal.GetDelegateForFunctionPointer(en, typeof(EnumFn)))(list, out n) == 0 && n > 0 ? list[0] : IntPtr.Zero;
        }

        // a fresh CONTROL buffer for domain k (4 KB of slack after the struct, as mVolt+ gives the driver)
        static byte[] Ctl(int k)
        {
            var b = new byte[CtlSize + 0x1000];
            BitConverter.GetBytes(CtlSize | (CtlVer << 16)).CopyTo(b, 0);
            BitConverter.GetBytes(1u << k).CopyTo(b, 8);
            return b;
        }

        // 23.11: the XBAR / SYS / Video domains too (GpuDomains.Tunable order); on this laptop all four are on NVVDD
        public static readonly int[] Tunable = { 1, 2, 21 };

        // a domain (clock key 0 core, 1 XBAR, 2 SYS, 21 Video): its index k, the CONTROL buffer read now and where its own rail's
        // slot is (rail at D k*0x430 + 0x124: 1 NVVDD = slot 0, 2 MSVDD = slot 1); null with Error set
        class Dom { public IntPtr Gpu; public int K, Slot; public byte[] Buf; }

        static Dom Find(int key)
        {
            var g = Gpu();
            if (g == IntPtr.Zero) { Error = L.T("драйвер NVIDIA не ответил"); return null; }
            var d = new byte[InfoSize + 0x1000];
            BitConverter.GetBytes(InfoSize | (InfoVer << 16)).CopyTo(d, 0);
            if (Call(InfoId, g, d) != 0) { Error = L.T("драйвер не отдаёт сдвиг напряжения"); return null; }
            uint mask = BitConverter.ToUInt32(d, 8);
            for (int k = 0; k < 32; k++)
            {
                if ((mask >> k & 1) == 0) continue;
                int e = 0xAC + k * 0x430;
                if (BitConverter.ToUInt32(d, e + 4) != key) continue;
                uint type = BitConverter.ToUInt32(d, e), rail = BitConverter.ToUInt32(d, k * 0x430 + 0x124);
                if ((type != 10 && type != 15) || (rail != 1 && rail != 2)) break;
                var b = Ctl(k);
                if (Call(GetId, g, b) != 0 || BitConverter.ToUInt32(b, 0) != (CtlSize | (CtlVer << 16)) || BitConverter.ToUInt32(b, 0x124 + k * 0x304) != type) break;
                return new Dom { Gpu = g, K = k, Buf = b, Slot = k * 0x304 + (type == 15 ? 0x23C : 0x234) + (rail == 1 ? 0 : 4) };
            }
            Error = L.T("драйвер не отдаёт сдвиг напряжения");
            return null;
        }

        static string NameOf(int key) { return key == 0 ? "ядра" : key == 1 ? "XBAR" : key == 2 ? "SYS" : key == 21 ? "Video" : key.ToString(); }

        // the domain's voltage-demand offset now, mV (rounded), or null when the driver has no such control
        public static int? Read(int key = 0)
        {
            lock (gate)
            {
                try
                {
                    var c = Find(key);
                    if (c == null) return null;
                    return (int)Math.Round(BitConverter.ToInt32(c.Buf, c.Slot) / 1000.0);
                }
                catch (Exception ex) { Error = ex.Message; return null; }
            }
        }

        // XBAR, SYS, Video now (Tunable order), or null when any of them cannot be read
        public static int[] ReadDomains()
        {
            var r = new int[Tunable.Length];
            for (int i = 0; i < r.Length; i++) { var v = Read(Tunable[i]); if (v == null) return null; r[i] = v.Value; }
            return r;
        }

        // a preset's value: nothing to do when the card has it, or when the driver has no such control and the preset asks 0
        public static string Ensure(int mv, int key = 0)
        {
            var now = Read(key);
            if (now == null) return mv == 0 ? null : Error;
            return now.Value == mv ? null : Set(mv, key);
        }

        // the three domain values of a preset (Tunable order); null when all are on the card
        public static string EnsureDomains(int[] mv)
        {
            if (mv == null) return null;
            for (int i = 0; i < Tunable.Length && i < mv.Length; i++) { var e = Ensure(mv[i], Tunable[i]); if (e != null) return e; }
            return null;
        }

        // the offset to mV: written only when the card has another one; read back, and on a refusal the old value goes back
        public static string Set(int mv, int key = 0)
        {
            if (Hw.Simulated) return "Симуляция: в видеокарту ничего не пишется";
            mv = Math.Max(MinMv, Math.Min(MaxMv, mv));
            lock (gate)
            {
                try
                {
                    var c = Find(key);
                    if (c == null) return Error;
                    int old = BitConverter.ToInt32(c.Buf, c.Slot), want = mv * 1000;
                    if (old == want) return null;
                    BitConverter.GetBytes(want).CopyTo(c.Buf, c.Slot);
                    int rc = Call(SetId, c.Gpu, c.Buf);
                    if (rc != 0) return L.T("драйвер не принял сдвиг напряжения (код ") + rc + ")";
                    var back = Ctl(c.K);
                    if (Call(GetId, c.Gpu, back) != 0 || BitConverter.ToInt32(back, c.Slot) != want)
                    {
                        // back to the buffer as first read (a failed read-back leaves «back» empty: never write that)
                        BitConverter.GetBytes(old).CopyTo(c.Buf, c.Slot);
                        Call(SetId, c.Gpu, c.Buf);
                        return L.T("драйвер не сохранил сдвиг напряжения");
                    }
                    Store.Log("видеокарта: напряжение " + NameOf(key) + " " + (mv > 0 ? "+" : "") + mv + " мВ");
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }
        }
    }
}
