using System;
using System.Runtime.InteropServices;

namespace RedTune
{
    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string l);

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
        }

        public static void DarkScrollBars(IntPtr hwnd) { SetWindowTheme(hwnd, "DarkMode_Explorer", null); }

        public static void CueBanner(IntPtr hwnd, string text) { SendMessage(hwnd, 0x1501, (IntPtr)1, text); }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static bool Memory(out ulong total, out ulong avail)
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            bool ok = GlobalMemoryStatusEx(ref m);
            total = m.ullTotalPhys; avail = m.ullAvailPhys;
            return ok;
        }
    }

    // Performance counters by their English names: works on the Russian Windows too (PerformanceCounter wants local names)
    sealed class Pdh : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr items);

        [StructLayout(LayoutKind.Explicit)]
        struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double doubleValue; }

        readonly IntPtr query;
        readonly IntPtr[] counters;

        public Pdh(params string[] paths)
        {
            PdhOpenQuery(null, IntPtr.Zero, out query);
            counters = new IntPtr[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                IntPtr c;
                counters[i] = PdhAddEnglishCounter(query, paths[i], IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero;
            }
            PdhCollectQueryData(query);
        }

        public void Collect() { PdhCollectQueryData(query); }

        // NaN when the counter is missing or has no value yet
        public double Get(int i)
        {
            if (counters[i] == IntPtr.Zero) return double.NaN;
            PDH_FMT_COUNTERVALUE v;
            return PdhGetFormattedCounterValue(counters[i], 0x200 | 0x8000, IntPtr.Zero, out v) == 0 && v.CStatus <= 1 ? v.doubleValue : double.NaN;
        }

        // the highest value over all instances of a wildcard counter ("…(*)\…"); NaN when there is none
        public double Max(int i)
        {
            if (counters[i] == IntPtr.Zero) return double.NaN;
            uint size = 0, count = 0;
            PdhGetFormattedCounterArray(counters[i], 0x200 | 0x8000, ref size, ref count, IntPtr.Zero);
            if (size == 0) return double.NaN;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counters[i], 0x200 | 0x8000, ref size, ref count, buf) != 0) return double.NaN;
                double best = double.NaN;
                // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then the value (status at +8, double at +16)
                for (int k = 0; k < count; k++)
                {
                    var item = buf + k * (IntPtr.Size + 16);
                    if ((uint)Marshal.ReadInt32(item, IntPtr.Size) > 1) continue;
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                    if (double.IsNaN(best) || v > best) best = v;
                }
                return best;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose() { PdhCloseQuery(query); }
    }
}
