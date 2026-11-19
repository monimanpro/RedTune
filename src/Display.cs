using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RedTune
{
    class DisplayMode { public int Width, Height, Hz; }

    // The built-in panel through the Windows display APIs (what LLT does with WindowsDisplayAPI): modes, scale, HDR
    static class Display
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        struct PathInfo
        {
            public LUID SrcAdapter; public uint SrcId, SrcModeIdx, SrcFlags;
            public LUID TgtAdapter; public uint TgtId, TgtModeIdx, Tech, Rotation, Scaling, RefreshNum, RefreshDen, ScanLine; public int Available; public uint TgtFlags;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)] struct ModeInfo { public uint InfoType, Id; public LUID Adapter; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] Data; }

        [StructLayout(LayoutKind.Sequential)] struct Header { public int Type; public uint Size; public LUID Adapter; public uint Id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName { public Header H; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }

        [StructLayout(LayoutKind.Sequential)] struct ColorInfo { public Header H; public uint Value, Encoding, Bits; }
        [StructLayout(LayoutKind.Sequential)] struct ColorSet { public Header H; public uint Value; }
        [StructLayout(LayoutKind.Sequential)] struct DpiGet { public Header H; public int Min, Cur, Max; }
        [StructLayout(LayoutKind.Sequential)] struct DpiSet { public Header H; public int Rel; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DpiGet info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref ColorSet info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref DpiSet info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

        class Panel { public PathInfo Path; public string Gdi; }

        // internal = embedded DisplayPort / LVDS / UDI or the "internal" flag; else the primary display
        static Panel Find()
        {
            uint np, nm;
            if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0) return null;
            var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
            var list = paths.Take((int)np).ToList();
            var pick = list.FirstOrDefault(p => p.Tech == 11 || p.Tech == 6 || p.Tech == 13 || p.Tech == 0x80000000);
            if (pick.Tech == 0 && list.Count > 0 && !list.Any(p => p.Tech == 11 || p.Tech == 6 || p.Tech == 13 || p.Tech == 0x80000000)) pick = list[0];
            var sn = new SourceName { H = new Header { Type = 1, Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = pick.SrcAdapter, Id = pick.SrcId } };
            if (DisplayConfigGetDeviceInfo(ref sn) != 0) return null;
            return new Panel { Path = pick, Gdi = sn.Gdi };
        }

        public static string Name { get { var p = Find(); return p == null ? null : p.Gdi; } }

        static DEVMODE NewMode() { return new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) }; }

        public static DisplayMode Current()
        {
            var p = Find(); if (p == null) return null;
            var dm = NewMode();
            if (!EnumDisplaySettings(p.Gdi, -1, ref dm)) return null;
            return new DisplayMode { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency };
        }

        public static List<DisplayMode> Modes()
        {
            var list = new List<DisplayMode>();
            var p = Find(); if (p == null) return list;
            var cur = NewMode(); EnumDisplaySettings(p.Gdi, -1, ref cur);
            for (int i = 0; ; i++)
            {
                var dm = NewMode();
                if (!EnumDisplaySettings(p.Gdi, i, ref dm)) break;
                if (dm.dmBitsPerPel != cur.dmBitsPerPel || dm.dmPelsWidth < 1024) continue;
                if (!list.Any(m => m.Width == dm.dmPelsWidth && m.Height == dm.dmPelsHeight && m.Hz == dm.dmDisplayFrequency))
                    list.Add(new DisplayMode { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency });
            }
            return list;
        }

        public static void SetMode(int width, int height, int hz)
        {
            var p = Find(); if (p == null) throw new Exception("Встроенный экран не найден");
            var dm = NewMode();
            EnumDisplaySettings(p.Gdi, -1, ref dm);
            dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmDisplayFrequency = hz;
            dm.dmFields = 0x80000 | 0x100000 | 0x400000;
            int r = ChangeDisplaySettingsEx(p.Gdi, ref dm, IntPtr.Zero, 1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
            if (r != 0) throw new Exception("Windows не принял режим (" + r + ")");
        }

        // ---------- scale ----------

        public static int[] DpiOptions(out int current)
        {
            current = 0;
            var p = Find(); if (p == null) return new int[0];
            var g = new DpiGet { H = new Header { Type = -3, Size = (uint)Marshal.SizeOf(typeof(DpiGet)), Adapter = p.Path.SrcAdapter, Id = p.Path.SrcId } };
            if (DisplayConfigGetDeviceInfo(ref g) != 0) return new int[0];
            int rec = Math.Abs(g.Min);
            current = DpiSteps[Math.Max(0, Math.Min(DpiSteps.Length - 1, rec + g.Cur))];
            return DpiSteps.Take(Math.Min(DpiSteps.Length, rec + g.Max + 1)).ToArray();
        }

        public static void SetDpi(int percent)
        {
            var p = Find(); if (p == null) throw new Exception("Встроенный экран не найден");
            var g = new DpiGet { H = new Header { Type = -3, Size = (uint)Marshal.SizeOf(typeof(DpiGet)), Adapter = p.Path.SrcAdapter, Id = p.Path.SrcId } };
            if (DisplayConfigGetDeviceInfo(ref g) != 0) throw new Exception("Масштаб недоступен");
            int idx = Array.IndexOf(DpiSteps, percent);
            if (idx < 0) throw new ArgumentException("percent");
            var s = new DpiSet { H = new Header { Type = -4, Size = (uint)Marshal.SizeOf(typeof(DpiSet)), Adapter = p.Path.SrcAdapter, Id = p.Path.SrcId }, Rel = idx - Math.Abs(g.Min) };
            if (DisplayConfigSetDeviceInfo(ref s) != 0) throw new Exception("Windows не принял масштаб");
        }

        // ---------- HDR ----------

        // null when HDR is not supported
        public static bool? Hdr()
        {
            var p = Find(); if (p == null) return null;
            var c = new ColorInfo { H = new Header { Type = 9, Size = (uint)Marshal.SizeOf(typeof(ColorInfo)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId } };
            if (DisplayConfigGetDeviceInfo(ref c) != 0 || (c.Value & 1) == 0) return null;
            return (c.Value & 2) != 0;
        }

        public static void SetHdr(bool on)
        {
            var p = Find(); if (p == null) throw new Exception("Встроенный экран не найден");
            var s = new ColorSet { H = new Header { Type = 10, Size = (uint)Marshal.SizeOf(typeof(ColorSet)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId }, Value = on ? 1u : 0u };
            if (DisplayConfigSetDeviceInfo(ref s) != 0) throw new Exception("Windows не включил HDR");
        }

        // all displays go dark; the PC keeps running, any key or mouse move wakes them
        public static void MonitorsOff() { PostMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2); }
    }
}
