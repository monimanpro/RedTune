using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RedTune
{
    // NVIDIA overclocking the way MSI Afterburner does it (13.11, worked out on the R7000P's RTX 5070 Laptop, driver 621.14):
    //  • the V/F curve: NVAPI's private ClockClient VfPoints functions (looked up by id through nvapi_QueryInterface).
    //    GetInfo (v1, 0x182C bytes) gives the mask of the points (32 bytes at +4); GetStatus (v1, 0x1C28) the points now:
    //    entries of 28 bytes from +68 with frequency (kHz) at +4 and voltage (µV) at +8; GetControl / SetControl (v1,
    //    0x2420) the offsets: entries of 36 bytes from +68 with the point type at +0 (0 core, 1 memory) and the offset (kHz) at
    //    +20. The layout was read off the driver while Afterburner applied a curve: +356 MHz up to 935 mV, −1602 MHz above
    //    (a flat top — the driver keeps the curve from falling, so a point pushed low just follows the one before it).
    //  • the memory clock: NVML's nvmlDeviceSetClockOffsets (type 2 = memory, P0), official since driver R555.
    // The driver forgets both on a reboot, so Red Tune applies the saved preset at start (GpuStore.ApplyOnStart).
    static class Gpu
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] static extern IntPtr Q(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out] IntPtr[] gpus, out int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CallFn(IntPtr gpu, IntPtr buf);

        const uint IdInit = 0x0150E828, IdEnum = 0xE5AC921F, IdInfo = 0x507B4B59, IdStatus = 0x21537AD4, IdControl = 0x23F1B133, IdSet = 0x0733E009;
        const int InfoSize = 0x182C, StatusSize = 0x1C28, ControlSize = 0x2420, Head = 68, StatusEntry = 28, ControlEntry = 36;

        static IntPtr gpu = IntPtr.Zero;
        static byte[] mask;
        static string error;
        static readonly object gate = new object();

        static T F<T>(uint id) where T : class
        {
            var p = Q(id);
            return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // NVIDIA card and the curve functions present, the point mask read; the reason in Error otherwise
        public static bool Open()
        {
            lock (gate)
            {
                if (gpu != IntPtr.Zero) return true;
                if (error != null) return false;
                try
                {
                    var init = F<InitFn>(IdInit); var en = F<EnumFn>(IdEnum);
                    if (init == null || en == null || init() != 0) { error = "драйвер NVIDIA не ответил"; return false; }
                    var list = new IntPtr[64]; int n;
                    if (en(list, out n) != 0 || n < 1) { error = "видеокарта NVIDIA не найдена"; return false; }
                    var info = Call(list[0], IdInfo, InfoSize, null);
                    if (info == null) { error = "драйвер не отдаёт кривую частот (функция Afterburner)"; return false; }
                    mask = new byte[32]; Array.Copy(info, 4, mask, 0, 32);
                    gpu = list[0];
                    return true;
                }
                catch (DllNotFoundException) { error = "драйвер NVIDIA не найден"; return false; }
                catch (Exception ex) { error = ex.Message; return false; }
            }
        }

        public static string Error { get { return error; } }

        // one call with a v1 struct of the given size: the mask copied in (or all ones for GetInfo), the answer out
        static byte[] Call(IntPtr g, uint id, int size, byte[] input)
        {
            var f = F<CallFn>(id); if (f == null) return null;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var data = input ?? new byte[size];
                BitConverter.GetBytes(size | (1 << 16)).CopyTo(data, 0);
                if (input == null) { if (mask != null) Array.Copy(mask, 0, data, 4, 32); else for (int i = 4; i < 36; i++) data[i] = 0xFF; }
                Marshal.Copy(data, 0, buf, size);
                if (f(g, buf) != 0) return null;
                var res = new byte[size]; Marshal.Copy(buf, res, 0, size);
                return res;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }


        // the curve now: for each core point its voltage, its frequency now and the offset set on it
        public class Curve
        {
            public int[] Mv, Mhz, Delta; // per core point (the points rise in voltage; memory points after them are left out)
            // the highest memory clock now (the memory points after the core curve), MHz as NVML reports it; 0 if none
            public int MemTop;
            public int Count { get { return Mv.Length; } }
        }

        public static Curve Read()
        {
            lock (gate)
            {
                if (!Open()) return null;
                var st = Call(gpu, IdStatus, StatusSize, null);
                var ct = Call(gpu, IdControl, ControlSize, null);
                if (st == null || ct == null) return null;
                var mv = new List<int>(); var mhz = new List<int>(); var d = new List<int>();
                for (int k = 0; k < 255; k++)
                {
                    if ((mask[k >> 3] >> (k & 7) & 1) == 0) break;
                    int so = Head + k * StatusEntry, co = Head + k * ControlEntry;
                    if (so + StatusEntry > st.Length || co + ControlEntry > ct.Length) break;
                    int v = BitConverter.ToInt32(st, so + 8) / 1000;
                    if (mv.Count > 0 && v < mv[mv.Count - 1]) break; // the core curve ends where the voltage falls back
                    mv.Add(v); mhz.Add(BitConverter.ToInt32(st, so + 4) / 1000); d.Add(BitConverter.ToInt32(ct, co + 20) / 1000);
                }
                int memTop = 0;
                for (int k = mv.Count; k < 255; k++)
                {
                    if ((mask[k >> 3] >> (k & 7) & 1) == 0 || Head + (k + 1) * StatusEntry > st.Length) break;
                    memTop = Math.Max(memTop, BitConverter.ToInt32(st, Head + k * StatusEntry + 4) / 1000);
                }
                return mv.Count >= 8 ? new Curve { Mv = mv.ToArray(), Mhz = mhz.ToArray(), Delta = d.ToArray(), MemTop = memTop } : null;
            }
        }

        // writes the offsets (MHz, one per core point; 0 = none) the way Afterburner does: the offset in kHz;
        // the points after the core curve are kept as they are. Returns null or why not.
        public static string Write(int[] delta)
        {
            if (Hw.Simulated) return "Симуляция: в видеокарту ничего не пишется";
            lock (gate)
            {
                if (!Open()) return error;
                var ct = Call(gpu, IdControl, ControlSize, null);
                if (ct == null) return "не прочиталась текущая кривая";
                // only the offset at +20; +0 is the point's type (0 core, 1 the memory points after the curve), not a
                // "set" flag: writing 1 there made the driver refuse the whole curve (13.11)
                for (int k = 0; k < delta.Length; k++)
                    BitConverter.GetBytes(delta[k] * 1000).CopyTo(ct, Head + k * ControlEntry + 20);
                var res = Call(gpu, IdSet, ControlSize, ct);
                if (res == null) return "драйвер не принял кривую";
                // read back: the driver may answer OK and keep the old curve (13.11: a preset saved right after
                // «Применить» came out as the factory curve); say so instead of «applied»
                var back = Call(gpu, IdControl, ControlSize, null);
                int wrong = 0;
                if (back != null)
                    for (int k = 0; k < delta.Length; k++)
                        if (BitConverter.ToInt32(back, Head + k * ControlEntry + 20) / 1000 != delta[k]) wrong++;
                if (back == null || wrong > 0)
                {
                    Store.Log("видеокарта: команда принята, но кривая не изменилась (" + (back == null ? "не перечиталась" : wrong + " из " + delta.Length + " точек") + ")");
                    return "драйвер ответил «принято», но кривая не изменилась" + (back == null ? "" : " (" + wrong + " из " + delta.Length + " точек)");
                }
                Store.Log("видеокарта: кривая записана (" + Describe(delta) + ")");
                return null;
            }
        }

        // the memory clock and the curve together. Memory FIRST: NVML's SetClockOffsets clears the V/F curve offsets
        // (13.11: +15 MHz on the curve was 0 right after the memory was set to +0), so the curve has to come after it.
        // The memory is written only when it differs from what the card has, so a curve-only change never touches it.
        // A memory change can also clear the curve a moment later, so after one the curve is checked again 2 s on and
        // written once more if it was lost.
        public static string WriteAll(int[] delta, int? memory)
        {
            bool memChanged = false;
            if (memory.HasValue)
            {
                var now = ReadMemory();
                if (now == null || now[0] != memory.Value)
                {
                    var e = WriteMemory(memory.Value); if (e != null) return e;
                    memChanged = true;
                    System.Threading.Thread.Sleep(1500); // let the driver finish with the memory before the curve
                }
            }
            var r = Write(delta);
            if (r != null || !memChanged) return r;
            System.Threading.Thread.Sleep(2000);
            var c = Read();
            if (c != null && !c.Delta.SequenceEqual(delta))
            {
                Store.Log("видеокарта: кривая сбросилась после смены памяти — записываю ещё раз");
                r = Write(delta);
            }
            return r;
        }

        public static string Describe(int[] delta)
        {
            if (delta.All(x => x == 0)) return "заводская";
            int first = delta.FirstOrDefault(x => x != 0);
            int cut = Array.FindIndex(delta, x => x < first - 300);
            return (first > 0 ? "+" : "") + first + " МГц" + (cut > 0 ? ", срез с точки " + cut : "");
        }

        // ---------- the memory clock (NVML) ----------
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr d);
        [StructLayout(LayoutKind.Sequential)] struct Offset { public uint Version; public int Type, Pstate, Mhz, Min, Max; }
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockOffsets(IntPtr d, ref Offset o);
        [DllImport("nvml.dll")] static extern int nvmlDeviceSetClockOffsets(IntPtr d, ref Offset o);

        static IntPtr nvml = IntPtr.Zero;
        static bool NvmlOpen()
        {
            if (nvml != IntPtr.Zero) return true;
            try { IntPtr d; if (nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out d) == 0) nvml = d; } catch { }
            return nvml != IntPtr.Zero;
        }

        static Offset MemOffset() { return new Offset { Version = (uint)(Marshal.SizeOf(typeof(Offset)) | (1 << 24)), Type = 2, Pstate = 0 }; }

        // the memory offset now and the range the driver allows, or null
        public static int[] ReadMemory()
        {
            lock (gate)
            {
                if (!NvmlOpen()) return null;
                var o = MemOffset();
                try { return nvmlDeviceGetClockOffsets(nvml, ref o) == 0 ? new[] { o.Mhz, o.Min, o.Max } : null; } catch { return null; }
            }
        }

        public static string WriteMemory(int mhz)
        {
            if (Hw.Simulated) return "Симуляция: в видеокарту ничего не пишется";
            lock (gate)
            {
                if (!NvmlOpen()) return "NVML не ответил";
                var o = MemOffset(); o.Mhz = mhz;
                int rc;
                try { rc = nvmlDeviceSetClockOffsets(nvml, ref o); } catch (Exception ex) { return ex.Message; }
                if (rc != 0) return "драйвер не принял частоту памяти (код " + rc + ")";
                Store.Log("видеокарта: память " + (mhz > 0 ? "+" : "") + mhz + " МГц");
                return null;
            }
        }
    }

    // GPU presets in %APPDATA%\RedTune\gpu.txt: «name|memory MHz|d0,d1,…» per line, the active one in «active=name»
    class GpuPreset { public string Name; public int Memory; public int[] Delta = new int[0]; public int[] Domain = new int[3]; } // Domain (22.11): XBAR, SYS, Video offsets, MHz

    static class GpuStore
    {
        static string FilePath { get { return Path.Combine(Store.DataDir, "gpu.txt"); } }
        static string StockPath { get { return Path.Combine(Store.DataDir, "gpu_stock.txt"); } }

        public static List<GpuPreset> Presets = new List<GpuPreset>();
        public static string Active = "";
        public static bool ApplyOnStart;

        public static void Load()
        {
            Presets.Clear(); Active = ""; ApplyOnStart = false;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (line.StartsWith("active=")) { Active = line.Substring(7); continue; }
                    if (line.StartsWith("start=")) { ApplyOnStart = line.Substring(6) == "1"; continue; }
                    var p = line.Split('|'); if (p.Length < 3) continue;
                    int mem; int.TryParse(p[1], out mem);
                    var gp = new GpuPreset { Name = p[0], Memory = mem, Delta = p[2].Split(',').Select(x => { int v; int.TryParse(x, out v); return v; }).ToArray() };
                    // a fourth field (22.11): the XBAR / SYS / Video offsets; older files have none
                    if (p.Length > 3) { var dm = p[3].Split(',').Select(x => { int v; int.TryParse(x, out v); return v; }).ToArray(); if (dm.Length == 3) gp.Domain = dm; }
                    Presets.Add(gp);
                }
            }
            catch (Exception ex) { Store.Log("видеокарта: пресеты не прочитались: " + ex.Message); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Store.DataDir);
                var lines = new List<string> { "active=" + Active, "start=" + (ApplyOnStart ? "1" : "0") };
                lines.AddRange(Presets.Select(p => p.Name.Replace("|", "/") + "|" + p.Memory + "|" + string.Join(",", p.Delta) + "|" + string.Join(",", p.Domain)));
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception ex) { Store.Log("видеокарта: пресеты не сохранились: " + ex.Message); }
        }

        // the factory curve (MHz per point), kept the first time the curve is read with no offsets on it: under a flat top
        // the driver shows only the top, so the factory frequencies there cannot be worked out later
        public static int[] Stock()
        {
            try { return File.Exists(StockPath) ? File.ReadAllText(StockPath).Split(',').Select(int.Parse).ToArray() : null; } catch { return null; }
        }

        public static void KeepStock(Gpu.Curve c)
        {
            if (c == null || c.Delta.Any(x => x != 0)) return;
            try { Directory.CreateDirectory(Store.DataDir); File.WriteAllText(StockPath, string.Join(",", c.Mhz)); } catch { }
        }

        // the factory frequency of every point: from the kept file, else frequency − offset where the point is not
        // pinned under a flat top, and the slope of the last known points above it
        public static int[] Base(Gpu.Curve c) { bool[] known; return Base(c, out known); }

        public static int[] Base(Gpu.Curve c, out bool[] known)
        {
            var stock = Stock();
            if (stock != null && stock.Length == c.Count) { known = Enumerable.Repeat(true, c.Count).ToArray(); return stock; }
            var b = new int[c.Count]; known = new bool[c.Count];
            for (int k = 0; k < c.Count; k++)
            {
                bool pinned = k > 0 && c.Delta[k] < c.Delta[k - 1] - 100 && c.Mhz[k] <= c.Mhz[k - 1];
                if (k > 0 && !known[k - 1] && c.Delta[k] == c.Delta[k - 1]) pinned = true;
                if (!pinned) { b[k] = c.Mhz[k] - c.Delta[k]; known[k] = true; }
            }
            // under a flat top the factory frequencies are hidden: a gentle guess (1.4 MHz per mV, the top of the RTX 5070
            // Laptop's factory curve) only for drawing; writes never depend on it (they move the offsets, see GpuPage)
            var kn = known;
            int last = Array.FindLastIndex(kn, x => x);
            for (int k = 0; k < c.Count; k++) if (!kn[k]) b[k] = last >= 0 ? (int)Math.Round(b[last] + 1.4 * (c.Mv[k] - c.Mv[last])) : c.Mhz[k];
            return b;
        }

        // the saved preset again (start, after sleep): only with the expert switch and not on a safe start
        public static void ApplySaved(Settings s, string why)
        {
            if (!ApplyOnStart || !s.Expert || Safety.SafeStart || Hw.Simulated) return;
            var p = Presets.FirstOrDefault(x => x.Name == Active);
            if (p == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                var c = Gpu.Read();
                KeepStock(c); // right after a reboot the curve is the factory one: the moment to keep it
                if (c == null || p.Delta.Length != c.Count) { Store.Log("видеокарта (" + why + "): кривая не подходит к этой карте"); return; }
                var e = Gpu.WriteAll(p.Delta, p.Memory);
                if (e == null && p.Domain.Any(x => x != 0)) e = GpuDomains.Apply(p.Domain); // XBAR / SYS / Video (22.11)
                Store.Log("видеокарта (" + why + "): «" + p.Name + "» " + (e ?? "применён"));
            });
        }
    }
}
