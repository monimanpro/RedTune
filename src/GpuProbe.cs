using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RedTune
{
    // 22.11 (the user: could Red Tune have the fabric clocks mVolt+ shows — XBAR, SYS, Video?). mVolt+ is closed (its GitHub
    // holds only docs and the exe), so the driver is asked directly, READ ONLY: the whole ClockClient VfPoints table (Red
    // Tune reads only the core curve and the memory points from it; RTX 50 may keep XBAR/SYS/Video/MSVDD curves there too)
    // and a few well-known NVAPI Get functions whose struct size and version are found by trying (the driver answers
    // NVAPI_INCOMPATIBLE_STRUCT_VERSION, −9, until they fit). No Set function is called. --gpu-probe <file>.
    static class GpuProbe
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] static extern IntPtr Q(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out] IntPtr[] gpus, out int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CallFn(IntPtr gpu, IntPtr buf);

        // Get functions only (names as the community lists give them; what each returns is read off the answer)
        static readonly Tuple<uint, string>[] Gets =
        {
            Tuple.Create(0x507B4B59u, "ClockClient VfPoints GetInfo (Red Tune uses it)"),
            Tuple.Create(0x21537AD4u, "ClockClient VfPoints GetStatus (Red Tune uses it)"),
            Tuple.Create(0x23F1B133u, "ClockClient VfPoints GetControl (Red Tune uses it)"),
            Tuple.Create(0x64B43A6Au, "GetClockBoostRanges / ClkDomains GetInfo"),
            Tuple.Create(0xDCB616C3u, "GetAllClockFrequencies (public)"),
            Tuple.Create(0x6FF81213u, "GetPstates20"),
            Tuple.Create(0x465F9BCFu, "GetCurrentVoltage / VoltRails GetStatus"),
            Tuple.Create(0x409D9841u, "PerfPolicies GetInfo"),
            Tuple.Create(0x3D358A0Cu, "PerfPolicies GetStatus"),
            Tuple.Create(0x34206D86u, "ClientPowerPolicies GetInfo"),
            Tuple.Create(0x70916171u, "ClientPowerPolicies GetStatus"),
        };

        // 22.11: the "Domain V/F" functions mVolt+ 0.50.13 uses for the XBAR / SYS / Video curves (ids decoded from its
        // obfuscated table, sizes and versions read off its read routine). READ functions only; the setter (0xFEC00D04) is
        // never called. Version = size + (v << 16) (the size does not fit 16 bits). mask: whether the 256-byte mask the
        // first answer carries at +4 goes in at +4 (mVolt+ does so for reads 2 and 5).
        static readonly Tuple<uint, int, int[], bool, string>[] DomainReads =
        {
            Tuple.Create(0x8895B510u, 0x68604, new[] { 1 }, false, "domain V/F read 1 (info?)"),
            Tuple.Create(0xDA025C3Eu, 0x424604, new[] { 5 }, true, "domain V/F read 2"),
            Tuple.Create(0x5A769461u, 0x12EE28, new[] { 0 }, false, "domain V/F read 3"),
            Tuple.Create(0x57B5A5DFu, 0x86AC, new[] { 4 }, false, "domain V/F read 4"),
            Tuple.Create(0x7FEE9032u, 0x1E8604, new[] { 0 }, true, "domain V/F read 5"),
        };

        public static string RunDomain(string dumpDir)
        {
            var sb = new StringBuilder();
            var init = F<InitFn>(0x0150E828); var en = F<EnumFn>(0xE5AC921F);
            if (init == null || en == null || init() != 0) return "nvapi: no init";
            var list = new IntPtr[64]; int n;
            if (en(list, out n) != 0 || n < 1) return "nvapi: no GPU";
            var g = list[0];
            sb.AppendLine("setter 0xFEC00D04 present: " + (Q(0xFEC00D04) != IntPtr.Zero) + " (not called)");
            byte[] mask = null;
            foreach (var t in DomainReads)
            {
                var p = Q(t.Item1);
                if (p == IntPtr.Zero) { sb.AppendLine(string.Format("{0:X8} {1}: not in this driver", t.Item1, t.Item5)); continue; }
                var f = (CallFn)Marshal.GetDelegateForFunctionPointer(p, typeof(CallFn));
                int size = t.Item2; bool ok = false;
                foreach (var v in t.Item3)
                {
                    var buf = Marshal.AllocHGlobal(size + 0x1000);
                    try
                    {
                        var data = new byte[size + 0x1000];
                        BitConverter.GetBytes(size + (v << 16)).CopyTo(data, 0);
                        if (t.Item4 && mask != null) Array.Copy(mask, 0, data, 4, mask.Length);
                        Marshal.Copy(data, 0, buf, data.Length);
                        int st = f(g, buf);
                        if (st != 0) { sb.AppendLine(string.Format("{0:X8} {1}: v{2} status {3}", t.Item1, t.Item5, v, st)); continue; }
                        var res = new byte[size]; Marshal.Copy(buf, res, 0, size);
                        if (mask == null) { mask = new byte[256]; Array.Copy(res, 4, mask, 0, 256); }
                        int nz = 0; for (int i = 0; i < res.Length; i++) if (res[i] != 0) nz++;
                        sb.AppendLine(string.Format("{0:X8} {1}: v{2} OK, {3} bytes, {4} non-zero", t.Item1, t.Item5, v, size, nz));
                        Dump(sb, res, 160);
                        if (dumpDir != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dumpDir, string.Format("domain-{0:X8}.bin", t.Item1)), res);
                        ok = true; break;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                if (!ok) sb.AppendLine(string.Format("{0:X8} {1}: no version answered", t.Item1, t.Item5));
            }
            return sb.ToString();
        }

        public static string Run()
        {
            var sb = new StringBuilder();
            var init = F<InitFn>(0x0150E828); var en = F<EnumFn>(0xE5AC921F);
            if (init == null || en == null || init() != 0) return "nvapi: no init";
            var list = new IntPtr[64]; int n;
            if (en(list, out n) != 0 || n < 1) return "nvapi: no GPU";
            var g = list[0];
            sb.AppendLine("GPUs: " + n);
            var answers = new Dictionary<uint, byte[]>();
            byte[] realMask = null; // the VfPoints mask GetInfo gives: Status and Control answer zeros to an all-ones one
            foreach (var t in Gets)
            {
                if (realMask == null && answers.ContainsKey(0x507B4B59)) { realMask = new byte[32]; Array.Copy(answers[0x507B4B59], 4, realMask, 0, 32); }
                var f = F<CallFn>(t.Item1);
                if (f == null) { sb.AppendLine(string.Format("{0:X8} {1}: not in this driver", t.Item1, t.Item2)); continue; }
                int last = 0; byte[] got = null; int gotSize = 0, gotVer = 0;
                for (int fill = 1; fill >= 0 && got == null; fill--)
                for (int ver = 1; ver <= 4 && got == null; ver++)
                    for (int size = 8; size <= 0x4000 && got == null; size += 4)
                    {
                        var buf = Marshal.AllocHGlobal(size);
                        try
                        {
                            var data = new byte[size];
                            BitConverter.GetBytes(size | (ver << 16)).CopyTo(data, 0);
                            // a point/domain mask right after the version, all ones, as the VfPoints functions want it
                            // (then once more with zeros: some Get functions read a type or flags there)
                            bool vf = t.Item1 == 0x21537AD4 || t.Item1 == 0x23F1B133;
                            if (fill == 1) for (int i = 4; i < Math.Min(36, size); i++) data[i] = vf && realMask != null ? realMask[i - 4] : (byte)0xFF;
                            Marshal.Copy(data, 0, buf, size);
                            last = f(g, buf);
                            if (last == 0) { got = new byte[size]; Marshal.Copy(buf, got, 0, size); gotSize = size; gotVer = ver; }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                if (got == null) { sb.AppendLine(string.Format("{0:X8} {1}: no size fits (last status {2})", t.Item1, t.Item2, last)); continue; }
                answers[t.Item1] = got;
                sb.AppendLine(string.Format("{0:X8} {1}: v{2}, {3} bytes (0x{3:X})", t.Item1, t.Item2, gotVer, gotSize));
                Dump(sb, got, 1200);
            }
            // the VfPoints table as a whole: every point in the mask, status (freq, volt) and control (+0 type, +20 offset)
            byte[] info, st, ct;
            if (answers.TryGetValue(0x507B4B59, out info) && answers.TryGetValue(0x21537AD4, out st) && answers.TryGetValue(0x23F1B133, out ct))
            {
                sb.AppendLine();
                sb.AppendLine("VfPoints table (index: status kHz, µV | control type, offset kHz | info entry if any):");
                for (int k = 0; k < 256; k++)
                {
                    if ((info[4 + (k >> 3)] >> (k & 7) & 1) == 0) continue;
                    int so = 68 + k * 28, co = 68 + k * 36, io = 68 + k * 24;
                    if (so + 28 > st.Length || co + 36 > ct.Length) break;
                    sb.Append(string.Format("  {0,3}: {1,8} kHz {2,8} µV | type {3} off {4}", k, BitConverter.ToInt32(st, so + 4), BitConverter.ToInt32(st, so + 8), BitConverter.ToInt32(ct, co), BitConverter.ToInt32(ct, co + 20)));
                    if (io + 24 <= info.Length) { sb.Append(" | info:"); for (int b = 0; b < 24; b += 4) sb.Append(" " + BitConverter.ToInt32(info, io + b)); }
                    sb.Append(" | st:");
                    for (int b = 0; b < 28; b += 4) sb.Append(" " + BitConverter.ToInt32(st, so + b));
                    sb.Append(" | ct:");
                    for (int b = 0; b < 36; b += 4) sb.Append(" " + BitConverter.ToInt32(ct, co + b));
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        static T F<T>(uint id) where T : class
        {
            var p = Q(id);
            return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // the non-zero int32 words with their offsets (up to max of them)
        static void Dump(StringBuilder sb, byte[] b, int max)
        {
            int shown = 0; var line = new StringBuilder();
            for (int o = 4; o + 4 <= b.Length && shown < max; o += 4)
            {
                int v = BitConverter.ToInt32(b, o); if (v == 0) continue;
                line.Append(string.Format(" +{0}={1}", o, v)); shown++;
                if (line.Length > 180) { sb.AppendLine("   " + line); line.Clear(); }
            }
            if (line.Length > 0) sb.AppendLine("   " + line);
        }
    }
}
