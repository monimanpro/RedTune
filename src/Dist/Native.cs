using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RedTune.Dist
{
    // Windows API: process path and affinity, CPU topology (cores and L3 caches = chips), dark title bar
    static class Native
    {
        const uint QueryLimited = 0x1000, SetInformation = 0x0200;

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr proc, out UIntPtr sys);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int relation, IntPtr buffer, ref int length);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string l);

        public static string GetPath(int pid)
        {
            var h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
            }
            finally { CloseHandle(h); }
        }

        // 0 = ok, otherwise the Win32 error (5 = access denied: protected process)
        public static int GetMask(int pid, out ulong mask)
        {
            mask = 0;
            var h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try
            {
                UIntPtr pm, sm;
                if (!GetProcessAffinityMask(h, out pm, out sm)) return Marshal.GetLastWin32Error();
                mask = pm.ToUInt64();
                return 0;
            }
            finally { CloseHandle(h); }
        }

        // only PROCESS_SET_INFORMATION, like .NET Process.ProcessorAffinity: the old Distribute.ps1 moved CoD (sp26-cod)
        // this way, while a handle that also asked for QUERY_LIMITED was refused (2026-11-03)
        public static bool SetMask(int pid, ulong mask)
        {
            var h = OpenProcess(SetInformation, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return SetProcessAffinityMask(h, new UIntPtr(mask)); }
            finally { CloseHandle(h); }
        }

        // Some anti-cheats (CoD sp26-cod, 2026-11-03) refuse handles from unsigned programs but let Microsoft-signed ones in:
        // the old Distribute.ps1 moved CoD from inside powershell.exe while this exe was refused. So ask PowerShell to do it.
        public static bool SetMaskViaPowerShell(int pid, ulong mask)
        {
            var ps = System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var cmd = "try { (Get-Process -Id " + pid + " -ErrorAction Stop).ProcessorAffinity = [IntPtr]" + (long)mask + "; exit 0 } catch { exit 1 }";
            var psi = new System.Diagnostics.ProcessStartInfo(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            };
            try
            {
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // CPU Sets: a softer "preferred cores" setting that needs only PROCESS_SET_LIMITED_INFORMATION.
        // Anti-cheats usually strip PROCESS_SET_INFORMATION (affinity) from other processes but not this right.
        const uint SetLimitedInformation = 0x2000;
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetSystemCpuSetInformation(IntPtr info, int length, out int returned, IntPtr process, int flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, int count);
        static Dictionary<int, uint> cpuSetIds;

        // logical processor index -> CPU Set id (SYSTEM_CPU_SET_INFORMATION: Size at 0, Id at 8, LogicalProcessorIndex at 14)
        static Dictionary<int, uint> CpuSetIds()
        {
            if (cpuSetIds != null) return cpuSetIds;
            var map = new Dictionary<int, uint>();
            int len;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
            if (len > 0)
            {
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0))
                        for (int off = 0; off + 16 <= len;)
                        {
                            int size = Marshal.ReadInt32(buf, off);
                            if (size <= 0) break;
                            map[Marshal.ReadByte(buf, off + 14)] = (uint)Marshal.ReadInt32(buf, off + 8);
                            off += size;
                        }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return cpuSetIds = map;
        }

        // all = true clears the CPU Sets (the process may run anywhere again)
        public static bool SetCpuSets(int pid, ulong mask, bool all)
        {
            var h = OpenProcess(SetLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (all) return SetProcessDefaultCpuSets(h, null, 0);
                var map = CpuSetIds();
                var ids = map.Where(kv => kv.Key < 64 && (mask & (1UL << kv.Key)) != 0).Select(kv => kv.Value).ToArray();
                return ids.Length > 0 && SetProcessDefaultCpuSets(h, ids, ids.Length);
            }
            finally { CloseHandle(h); }
        }

        static List<byte[]> Records(int relation)
        {
            int len = 0;
            GetLogicalProcessorInformationEx(relation, IntPtr.Zero, ref len);
            var list = new List<byte[]>();
            if (len <= 0) return list;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetLogicalProcessorInformationEx(relation, buf, ref len)) return list;
                int off = 0;
                while (off + 8 <= len)
                {
                    int size = Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    var rec = new byte[size];
                    Marshal.Copy(IntPtr.Add(buf, off), rec, 0, size);
                    list.Add(rec);
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        // CACHE_RELATIONSHIP: Level at 8, GROUP_AFFINITY.Mask at 40
        public static List<ulong> CacheMasks(int level)
        {
            return Records(2).Where(r => r.Length >= 48 && r[8] == level).Select(r => BitConverter.ToUInt64(r, 40)).ToList();
        }

        // 20.11: every cache of a level with its size in bytes (CacheSize at 12): which chip carries 3D V-Cache
        public static List<KeyValuePair<ulong, long>> CacheSizes(int level)
        {
            return Records(2).Where(r => r.Length >= 48 && r[8] == level)
                .Select(r => new KeyValuePair<ulong, long>(BitConverter.ToUInt64(r, 40), BitConverter.ToUInt32(r, 12))).ToList();
        }

        // 16.11: the size in bytes of one cache of that level that holds data (CACHE_RELATIONSHIP: CacheSize at 12, Type
        // at 16: 0 unified, 2 data), the biggest when cores differ; 0 when there is none. For the latency curve's marks
        public static long CacheBytes(int level)
        {
            try
            {
                return Records(2).Where(r => r.Length >= 20 && r[8] == level && (BitConverter.ToInt32(r, 16) == 0 || BitConverter.ToInt32(r, 16) == 2))
                    .Select(r => (long)BitConverter.ToUInt32(r, 12)).DefaultIfEmpty(0).Max();
            }
            catch { return 0; }
        }

        // PROCESSOR_RELATIONSHIP: GROUP_AFFINITY.Mask at 32
        public static List<ulong> CoreMasks()
        {
            return Records(0).Where(r => r.Length >= 40).Select(r => BitConverter.ToUInt64(r, 32)).ToList();
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
        }

        public static void DarkScrollBars(IntPtr hwnd) { SetWindowTheme(hwnd, "DarkMode_Explorer", null); }

        public static void CueBanner(IntPtr hwnd, string text) { SendMessage(hwnd, 0x1501, (IntPtr)1, text); }
    }
}
