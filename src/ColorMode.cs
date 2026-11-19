using System;
using System.Runtime.InteropServices;

namespace RedTune
{
    // 19.11: which colour mode a display runs in. Windows has an "advanced colour" mode per display (HDR, and on SDR
    // displays «Автоматическое управление цветом»): the desktop is then put together in a wide colour space and turned
    // into the display's own colours on the way out. Found on an external monitor (advanced colour on, 12 bits a
    // channel): the mini panel's blocks, which use Windows' blur behind a window, came out with their colours turned
    // inside out on the screen itself (dark glass light grey, white print black or cyan), while every screenshot of
    // them was right — the fault is in what Windows shows, not in what it composes. The blur behind a window is an
    // old, undocumented switch and is not kept right in that mode. So the panel asks here and goes without the blur
    // on such a display (MiniBlocks.Toggle).
    static class ColorMode
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct RATIONAL { public uint N, D; }
        [StructLayout(LayoutKind.Sequential)] struct SRC { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)] struct TGT { public LUID adapterId; public uint id; public uint modeInfoIdx; public int outputTechnology; public int rotation; public int scaling; public RATIONAL refreshRate; public int scanLineOrdering; public int targetAvailable; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)] struct PATH { public SRC source; public TGT target; public uint flags; }
        [StructLayout(LayoutKind.Sequential, Size = 64)] struct MODE { public uint infoType; public uint id; public LUID adapterId; }
        [StructLayout(LayoutKind.Sequential)] struct HDR { public int type; public int size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential)] struct ADV { public HDR header; public uint value; public int colorEncoding; public int bitsPerColorChannel; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SRCNAME { public HDR header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint nPaths, [Out] PATH[] paths, ref uint nModes, [Out] MODE[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ADV info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SRCNAME info);

        // gdiName: a display as Screen.DeviceName gives it («\\.\DISPLAY2»). False when Windows does not say.
        public static bool Advanced(string gdiName)
        {
            try
            {
                uint np, nm;
                if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0 || np == 0) return false; // 2: the paths in use
                var paths = new PATH[np]; var modes = new MODE[nm];
                if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
                for (int i = 0; i < np; i++)
                {
                    var n = new SRCNAME(); n.header.type = 1; n.header.size = Marshal.SizeOf(typeof(SRCNAME)); n.header.adapterId = paths[i].source.adapterId; n.header.id = paths[i].source.id;
                    if (DisplayConfigGetDeviceInfo(ref n) != 0 || !string.Equals(n.viewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase)) continue;
                    var a = new ADV(); a.header.type = 9; a.header.size = Marshal.SizeOf(typeof(ADV)); a.header.adapterId = paths[i].target.adapterId; a.header.id = paths[i].target.id;
                    if (DisplayConfigGetDeviceInfo(ref a) == 0 && (a.value & 2) != 0) return true; // bit 1: advanced colour is on
                }
            }
            catch { }
            return false;
        }

        // for --color-mode-check: every screen and what Advanced says about it
        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in System.Windows.Forms.Screen.AllScreens) sb.AppendLine(s.DeviceName + (s.Primary ? " (main)" : "") + " " + s.Bounds + ": advanced colour " + (Advanced(s.DeviceName) ? "ON — the mini panel goes without Windows' blur here" : "off"));
            return sb.ToString();
        }
    }
}
