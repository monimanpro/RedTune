using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RedTune
{
    // Exact (20.11): the rate the screen really runs at, from Windows' fraction (59 951/1000 for a «60 Hz» monitor whose
    // mode lists as 59); 0 when not known
    class DisplayMode { public int Width, Height, Hz; public double Exact; }

    // 20.11: a monitor that shows something now (an active path): its GDI name (\\.\DISPLAY1), the name the monitor gives
    // itself, built-in or not, the main one or not, where it stands on the desktop
    class DisplayInfo
    {
        public string Gdi, Name; public bool Internal, Primary; public int X, Y, Width, Height;
        public int Maker; // the EDID manufacturer code: with the product, the monitor whatever cable it is on (DisplayInfo.Key)
        public string Key { get { return Maker.ToString("X4") + ":" + Product.ToString("X4"); } }
        public int Product; // the EDID product code (TMA2039 → 0x2039): Lenovo's panel profiles carry it in their names
        internal object Path; // Display.PathInfo, boxed
        public string Title { get { return Internal ? "Экран ноутбука" : string.IsNullOrEmpty(Name) ? "Монитор" : Name; } }
    }

    // The built-in panel through the Windows display APIs (what LLT does with WindowsDisplayAPI): modes, scale, HDR.
    // 20.11: any monitor too (DisplayInfo, the page «Дисплей»), its brightness, Win+P and the main monitor
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

        // ---------- 20.11: every monitor ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetName
        {
            public Header H; public uint Flags, Tech; public ushort Maker, Product; public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName info);

        static bool IsInternalTech(uint t) { return t == 11 || t == 6 || t == 13 || t == 0x80000000; }

        public static List<DisplayInfo> All()
        {
            var list = new List<DisplayInfo>();
            uint np, nm;
            if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0) return list;
            var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;
            foreach (var p in paths.Take((int)np))
            {
                var sn = new SourceName { H = new Header { Type = 1, Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = p.SrcAdapter, Id = p.SrcId } };
                if (DisplayConfigGetDeviceInfo(ref sn) != 0) continue;
                if (list.Any(x => x.Gdi == sn.Gdi)) continue; // a copy of another (duplicate): one row for the picture
                var tn = new TargetName { H = new Header { Type = 2, Size = (uint)Marshal.SizeOf(typeof(TargetName)), Adapter = p.TgtAdapter, Id = p.TgtId } };
                string friendly = DisplayConfigGetDeviceInfo(ref tn) == 0 ? (tn.Friendly ?? "").Trim() : "";
                var dm = NewMode(); EnumDisplaySettings(sn.Gdi, -1, ref dm);
                list.Add(new DisplayInfo { Gdi = sn.Gdi, Name = friendly, Product = tn.Product, Maker = tn.Maker, Internal = IsInternalTech(p.Tech), Primary = dm.dmPositionX == 0 && dm.dmPositionY == 0, X = dm.dmPositionX, Y = dm.dmPositionY, Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Path = p });
            }
            // the built-in screen first, then the others from left to right
            return list.OrderBy(x => x.Internal ? 0 : 1).ThenBy(x => x.X).ToList();
        }

        static Panel Of(DisplayInfo d) { return d == null ? null : new Panel { Path = (PathInfo)d.Path, Gdi = d.Gdi }; }

        public static DisplayMode Current(DisplayInfo d) { return Current(Of(d)); }
        public static List<DisplayMode> Modes(DisplayInfo d) { return Modes(Of(d)); }
        public static void SetMode(DisplayInfo d, int width, int height, int hz) { SetMode(Of(d), width, height, hz); }
        public static int[] DpiOptions(DisplayInfo d, out int current) { return DpiOptions(Of(d), out current); }
        public static void SetDpi(DisplayInfo d, int percent) { SetDpi(Of(d), percent); }
        public static bool? Hdr(DisplayInfo d) { return Hdr(Of(d)); }
        public static void SetHdr(DisplayInfo d, bool on) { SetHdr(Of(d), on); }

        // ---------- 20.11: colour profiles ----------
        // Windows 11's colour management by adapter and source (mscms ColorProfile*), which also knows the advanced colour
        // (HDR, automatic colour management) side of a display. The profiles of a display are those associated with it;
        // the default one is what Windows applies. Scope: the one Windows uses for this display (per user or system-wide).
        // Lenovo ships factory-measured profiles of the panel (TPLCD_2039_sRGB, _DCIP3 …): picking one is picking the
        // colour space the screen shows.
        public class Profiles { public List<string> Associated = new List<string>(); public string Default; public bool Advanced; public int Scope; public string Error; }
        [DllImport("mscms.dll")] static extern int ColorProfileGetDisplayList(int scope, LUID adapter, uint source, out IntPtr list, out uint count);
        [DllImport("mscms.dll")] static extern int ColorProfileGetDisplayDefault(int scope, LUID adapter, uint source, int type, int subtype, out IntPtr name);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string name, int type, int subtype, LUID adapter, uint source);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] static extern int ColorProfileAddDisplayAssociation(int scope, string name, LUID adapter, uint source, bool setAsDefault, bool advanced);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] static extern int ColorProfileRemoveDisplayAssociation(int scope, string name, LUID adapter, uint source, bool advanced);
        [DllImport("mscms.dll")] static extern int ColorProfileGetDisplayUserScope(LUID adapter, uint source, out int scope);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool InstallColorProfileW(string machine, string path);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        const int CptIcc = 0, CpstStandard = 7, CpstExtended = 8;

        public static Profiles ColorProfiles(DisplayInfo d)
        {
            var r = new Profiles();
            var p = Of(d); if (p == null) { r.Error = "экран не найден"; return r; }
            try
            {
                var adapter = p.Path.SrcAdapter; uint src = p.Path.SrcId;
                int scope; if (ColorProfileGetDisplayUserScope(adapter, src, out scope) != 0) scope = 1;
                r.Scope = scope;
                var c = new ColorInfo { H = new Header { Type = 9, Size = (uint)Marshal.SizeOf(typeof(ColorInfo)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId } };
                r.Advanced = DisplayConfigGetDeviceInfo(ref c) == 0 && (c.Value & 2) != 0;
                IntPtr list; uint n;
                if (ColorProfileGetDisplayList(scope, adapter, src, out list, out n) == 0 && list != IntPtr.Zero)
                {
                    for (int i = 0; i < n; i++) { var s = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size)); if (!string.IsNullOrEmpty(s) && !r.Associated.Contains(s)) r.Associated.Add(s); }
                    LocalFree(list);
                }
                IntPtr name;
                if (ColorProfileGetDisplayDefault(scope, adapter, src, CptIcc, r.Advanced ? CpstExtended : CpstStandard, out name) == 0 && name != IntPtr.Zero) { r.Default = Marshal.PtrToStringUni(name); LocalFree(name); }
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        // every profile in Windows' colour folder made for a monitor (the class «mntr» in its header)
        public static List<string> InstalledProfiles()
        {
            var list = new List<string>();
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(ColorDir).Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)))
                    try
                    {
                        var head = new byte[16];
                        using (var s = System.IO.File.OpenRead(f)) if (s.Read(head, 0, 16) < 16) continue;
                        if (System.Text.Encoding.ASCII.GetString(head, 12, 4) == "mntr") list.Add(System.IO.Path.GetFileName(f));
                    }
                    catch { }
            }
            catch { }
            return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Lenovo's measured profiles of a panel: TPLCD_<product>_<space>; null: not one of them
        static readonly System.Text.RegularExpressions.Regex LenovoPanel = new System.Text.RegularExpressions.Regex(@"^TPLCD_([0-9A-Fa-f]{4})_(\w+)\.ic[cm]$");
        public static bool ForOtherPanel(DisplayInfo d, string profile)
        {
            var m = LenovoPanel.Match(profile ?? "");
            return m.Success && Convert.ToInt32(m.Groups[1].Value, 16) != d.Product;
        }

        // a name to show: Lenovo's panel profiles by their colour space, Windows' sRGB as such, the rest by the file name
        public static string ProfileTitle(string profile)
        {
            var m = LenovoPanel.Match(profile ?? "");
            if (m.Success)
            {
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "srgb": return "Lenovo · sRGB";
                    case "dcip3": return "Lenovo · DCI-P3";
                    case "displayp3": return "Lenovo · Display P3";
                    case "adobergb": return "Lenovo · Adobe RGB";
                    case "rec709": return "Lenovo · Rec.709";
                    case "native": return "Lenovo · весь охват матрицы";
                    case "default": return "Lenovo · заводской";
                    default: return "Lenovo · " + m.Groups[2].Value;
                }
            }
            if (string.Equals(profile, "sRGB Color Space Profile.icm", StringComparison.OrdinalIgnoreCase)) return "sRGB (Windows)";
            return System.IO.Path.GetFileNameWithoutExtension(profile ?? "");
        }

        // the profile Windows applies to this display (associated first if it is not yet); then its calibration curve is loaded
        public static void SetColorProfile(DisplayInfo d, string name)
        {
            var p = Of(d); if (p == null) throw new Exception("Экран не найден");
            var cur = ColorProfiles(d);
            int hr = cur.Associated.Contains(name)
                ? ColorProfileSetDisplayDefaultAssociation(cur.Scope, name, CptIcc, cur.Advanced ? CpstExtended : CpstStandard, p.Path.SrcAdapter, p.Path.SrcId)
                : ColorProfileAddDisplayAssociation(cur.Scope, name, p.Path.SrcAdapter, p.Path.SrcId, true, cur.Advanced);
            if (hr != 0) throw new Exception("Windows не принял профиль (0x" + hr.ToString("X8") + ")");
            LoadCalibration();
            Reapply(p);
        }

        // 20.11: Windows keeps a new default profile but builds the picture with it only when the display is set up
        // again (seen on the laptop screen under automatic colour management: no pick changed anything). The same mode
        // is set once more with CDS_RESET: the screen blinks for about a second and comes back with the new profile.
        static void Reapply(Panel p)
        {
            var dm = NewMode();
            if (!EnumDisplaySettings(p.Gdi, -1, ref dm)) return;
            dm.dmFields = 0x80000 | 0x100000 | 0x400000; // width, height, frequency
            int r = ChangeDisplaySettingsEx(p.Gdi, ref dm, IntPtr.Zero, 0x40000000 | 0x00000001, IntPtr.Zero); // CDS_RESET | CDS_UPDATEREGISTRY
            Store.Log("цветовой профиль: экран " + p.Gdi + " настроен заново (" + r + ")");
        }

        // no profile: the default one is taken off this display (it stays installed and can be picked again); Windows then shows sRGB
        public static void ClearColorProfile(DisplayInfo d)
        {
            var p = Of(d); if (p == null) throw new Exception("Экран не найден");
            var cur = ColorProfiles(d);
            if (string.IsNullOrEmpty(cur.Default)) return;
            int hr = ColorProfileRemoveDisplayAssociation(cur.Scope, cur.Default, p.Path.SrcAdapter, p.Path.SrcId, cur.Advanced);
            if (hr != 0) throw new Exception("Windows не снял профиль (0x" + hr.ToString("X8") + ")");
            LoadCalibration();
            Reapply(p);
        }

        // a profile from a file: copied into Windows' colour folder (if it is not there) and made this display's
        public static string InstallColorProfile(DisplayInfo d, string file)
        {
            var dir = ColorDir; var name = System.IO.Path.GetFileName(file);
            if (!string.Equals(System.IO.Path.GetDirectoryName(file).TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !InstallColorProfileW(null, file))
                throw new Exception("Windows не установил профиль (" + Marshal.GetLastWin32Error() + ")");
            SetColorProfile(d, name);
            return name;
        }

        public static string ColorDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color"); } }

        // Windows' own task that loads the calibration curve (VCGT) of the default profiles, as it does at sign-in
        static void LoadCalibration()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/run /tn \"\\Microsoft\\Windows\\WindowsColorSystem\\Calibration Loader\"") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }

        // 20.11: the signal's colour format and bits per channel, as Windows reports them («RGB», 10); null: not reported.
        // Encoding: 0 RGB, 1 YCbCr 4:4:4, 2 YCbCr 4:2:2, 3 YCbCr 4:2:0, 4 intensity
        public static Tuple<string, int> Color(DisplayInfo d)
        {
            var p = Of(d); if (p == null) return null;
            var c = new ColorInfo { H = new Header { Type = 9, Size = (uint)Marshal.SizeOf(typeof(ColorInfo)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId } };
            if (DisplayConfigGetDeviceInfo(ref c) != 0 || c.Bits == 0) return null;
            var names = new[] { "RGB", "YCbCr 4:4:4", "YCbCr 4:2:2", "YCbCr 4:2:0", "Intensity" };
            return Tuple.Create(c.Encoding < names.Length ? names[c.Encoding] : "?", (int)c.Bits);
        }

        // ---------- 20.11: brightness ----------
        // The built-in screen through Windows (WMI, as the brightness keys); a monitor through DDC/CI, the VCP code 0x10
        // (as its own buttons), which most monitors take; null: this one cannot be asked. Values in percent of its range.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PHYSICAL_MONITOR { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Desc; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hmon, out uint count);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hmon, uint count, [Out] PHYSICAL_MONITOR[] mons);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] mons);
        [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

        static T WithMonitor<T>(DisplayInfo d, Func<IntPtr, T> use, T none)
        {
            var hmon = MonitorFromPoint(new POINT { X = d.X + d.Width / 2, Y = d.Y + d.Height / 2 }, 0);
            uint n;
            if (hmon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out n) || n == 0) return none;
            var mons = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, mons)) return none;
            try { return use(mons[0].Handle); } finally { DestroyPhysicalMonitors(n, mons); }
        }

        public static int? Brightness(DisplayInfo d)
        {
            if (d.Internal)
            {
                try
                {
                    using (var q = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE"))
                        foreach (System.Management.ManagementObject o in q.Get()) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
                }
                catch { }
                return null;
            }
            return WithMonitor<int?>(d, h => { uint cur, max; return GetVCPFeatureAndVCPFeatureReply(h, 0x10, IntPtr.Zero, out cur, out max) && max > 0 ? (int?)(int)Math.Round(cur * 100.0 / max) : null; }, null);
        }

        public static void SetBrightness(DisplayInfo d, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            if (d.Internal)
            {
                using (var q = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
                    foreach (System.Management.ManagementObject o in q.Get()) using (o) { o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent }); return; }
                throw new Exception("Windows не даёт менять яркость этого экрана");
            }
            bool ok = WithMonitor(d, h => { uint cur, max; if (!GetVCPFeatureAndVCPFeatureReply(h, 0x10, IntPtr.Zero, out cur, out max) || max == 0) return false; return SetVCPFeature(h, 0x10, (uint)Math.Round(percent * max / 100.0)); }, false);
            if (!ok) throw new Exception("монитор не отвечает по DDC/CI (его можно включить в меню монитора)");
        }

        // ---------- 20.11: what Win+P picks, and the main monitor ----------
        // Topology: 1 only the built-in screen, 2 the same picture on both, 4 extended, 8 only the other screen; 0 unknown
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, out uint topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

        public static int Topology()
        {
            uint np, nm, topo;
            if (GetDisplayConfigBufferSizes(4, out np, out nm) != 0) return 0;
            var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
            return QueryDisplayConfig(4, ref np, paths, ref nm, modes, out topo) == 0 ? (int)topo : 0;
        }

        public static void SetTopology(int topology)
        {
            int r = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x80 | (uint)topology); // SDC_APPLY | SDC_TOPOLOGY_*
            if (r != 0) throw new Exception("Windows не переключил экраны (" + r + ")");
        }

        // monitors plugged in, shown or not (Win+P makes sense from two)
        public static int Connected()
        {
            uint np, nm;
            if (GetDisplayConfigBufferSizes(1, out np, out nm) != 0) return 0;
            var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(1, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return 0;
            return paths.Take((int)np).Where(p => p.Available != 0).Select(p => p.TgtAdapter.Low + ":" + p.TgtAdapter.High + ":" + p.TgtId).Distinct().Count();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);

        // the main monitor: it goes to 0,0 and the others keep their places around it
        public static void SetPrimary(DisplayInfo main)
        {
            var all = All();
            var m = all.FirstOrDefault(x => x.Gdi == main.Gdi);
            if (m == null) throw new Exception("монитор не найден");
            if (m.Primary) return;
            int dx = m.X, dy = m.Y;
            foreach (var x in all)
            {
                var dm = NewMode(); EnumDisplaySettings(x.Gdi, -1, ref dm);
                dm.dmPositionX = x.X - dx; dm.dmPositionY = x.Y - dy; dm.dmFields = 0x20; // DM_POSITION
                uint flags = 0x00000001 | 0x10000000 | (x.Gdi == m.Gdi ? 0x00000010u : 0u); // CDS_UPDATEREGISTRY | CDS_NORESET | CDS_SET_PRIMARY
                int r = ChangeDisplaySettingsEx(x.Gdi, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
                if (r != 0) throw new Exception("Windows не принял расстановку (" + r + ")");
            }
            ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // all of it at once
        }

        static DEVMODE NewMode() { return new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) }; }

        public static DisplayMode Current() { return Current(Find()); }
        static DisplayMode Current(Panel p)
        {
            if (p == null) return null;
            var dm = NewMode();
            if (!EnumDisplaySettings(p.Gdi, -1, ref dm)) return null;
            return new DisplayMode { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency, Exact = p.Path.RefreshDen == 0 ? 0 : p.Path.RefreshNum / (double)p.Path.RefreshDen };
        }

        public static List<DisplayMode> Modes() { return Modes(Find()); }
        static List<DisplayMode> Modes(Panel p)
        {
            var list = new List<DisplayMode>();
            if (p == null) return list;
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

        public static void SetMode(int width, int height, int hz) { SetMode(Find(), width, height, hz); }
        static void SetMode(Panel p, int width, int height, int hz)
        {
            if (p == null) throw new Exception("Экран не найден");
            var dm = NewMode();
            EnumDisplaySettings(p.Gdi, -1, ref dm);
            dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmDisplayFrequency = hz;
            dm.dmFields = 0x80000 | 0x100000 | 0x400000;
            int r = ChangeDisplaySettingsEx(p.Gdi, ref dm, IntPtr.Zero, 1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
            if (r != 0) throw new Exception("Windows не принял режим (" + r + ")");
        }

        // ---------- scale ----------

        public static int[] DpiOptions(out int current) { return DpiOptions(Find(), out current); }
        static int[] DpiOptions(Panel p, out int current)
        {
            current = 0;
            if (p == null) return new int[0];
            var g = new DpiGet { H = new Header { Type = -3, Size = (uint)Marshal.SizeOf(typeof(DpiGet)), Adapter = p.Path.SrcAdapter, Id = p.Path.SrcId } };
            if (DisplayConfigGetDeviceInfo(ref g) != 0) return new int[0];
            int rec = Math.Abs(g.Min);
            current = DpiSteps[Math.Max(0, Math.Min(DpiSteps.Length - 1, rec + g.Cur))];
            return DpiSteps.Take(Math.Min(DpiSteps.Length, rec + g.Max + 1)).ToArray();
        }

        public static void SetDpi(int percent) { SetDpi(Find(), percent); }
        static void SetDpi(Panel p, int percent)
        {
            if (p == null) throw new Exception("Экран не найден");
            var g = new DpiGet { H = new Header { Type = -3, Size = (uint)Marshal.SizeOf(typeof(DpiGet)), Adapter = p.Path.SrcAdapter, Id = p.Path.SrcId } };
            if (DisplayConfigGetDeviceInfo(ref g) != 0) throw new Exception("Масштаб недоступен");
            int idx = Array.IndexOf(DpiSteps, percent);
            if (idx < 0) throw new ArgumentException("percent");
            var s = new DpiSet { H = new Header { Type = -4, Size = (uint)Marshal.SizeOf(typeof(DpiSet)), Adapter = p.Path.SrcAdapter, Id = p.Path.SrcId }, Rel = idx - Math.Abs(g.Min) };
            if (DisplayConfigSetDeviceInfo(ref s) != 0) throw new Exception("Windows не принял масштаб");
        }

        // ---------- HDR ----------

        // null when HDR is not supported
        public static bool? Hdr() { return Hdr(Find()); }
        // 20.11: Windows 11 (24H2) tells HDR apart from the rest of its advanced colour (the automatic colour management a
        // 10-bit panel gets): ADVANCED_COLOR_INFO_2 (type 15). The older answer (type 9) says «advanced colour on» for both,
        // and the toggle showed HDR on for a laptop screen set to 10 bits. Bits of Value: 4 HDR supported, 5 HDR on.
        // ActiveMode: 0 SDR, 1 wide colour (automatic colour management), 2 HDR.
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo2 { public Header H; public uint Value, Encoding, Bits, ActiveMode; }
        [StructLayout(LayoutKind.Sequential)] struct HdrSet { public Header H; public uint Value; }
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo2 info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref HdrSet info);

        static bool Info2(Panel p, out ColorInfo2 c)
        {
            c = new ColorInfo2 { H = new Header { Type = 15, Size = (uint)Marshal.SizeOf(typeof(ColorInfo2)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId } };
            return DisplayConfigGetDeviceInfo(ref c) == 0;
        }

        // 20.11: Windows' automatic colour management (Settings → Display → «Автоматически управлять цветом для приложений»):
        // SET_WCG_STATE (17). Under it Windows itself maps everything on the screen to accurate sRGB through the profile, so
        // Lenovo's colour modes (profiles of one panel with different targets) all give the same picture; off, the screen
        // shows the colour mode of the profile. null: the display does not offer it. Bits of Value: 6 supported, 7 on.
        [StructLayout(LayoutKind.Sequential)] struct WcgSet { public Header H; public uint Value; }
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref WcgSet info);

        public static bool? AutoColor(DisplayInfo d)
        {
            var p = Of(d); ColorInfo2 c;
            if (p == null || !Info2(p, out c) || (c.Value & 64) == 0) return null;
            return (c.Value & 128) != 0;
        }

        public static void SetAutoColor(DisplayInfo d, bool on)
        {
            var p = Of(d); if (p == null) throw new Exception("Экран не найден");
            var s = new WcgSet { H = new Header { Type = 17, Size = (uint)Marshal.SizeOf(typeof(WcgSet)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId }, Value = on ? 1u : 0u };
            int r = DisplayConfigSetDeviceInfo(ref s);
            if (r != 0) throw new Exception("Windows не переключил автоуправление цветом (" + r + ")");
            LoadCalibration();
        }

        // the colour mode Windows runs the display in: 0 SDR, 1 wide colour (automatic colour management), 2 HDR; -1 unknown
        public static int ColorMode(DisplayInfo d)
        {
            var p = Of(d); ColorInfo2 c;
            if (p != null && Info2(p, out c)) return (int)c.ActiveMode;
            return -1;
        }

        static bool? Hdr(Panel p)
        {
            if (p == null) return null;
            ColorInfo2 c2;
            if (Info2(p, out c2)) return (c2.Value & 16) == 0 ? (bool?)null : (c2.Value & 32) != 0;
            var c = new ColorInfo { H = new Header { Type = 9, Size = (uint)Marshal.SizeOf(typeof(ColorInfo)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId } };
            if (DisplayConfigGetDeviceInfo(ref c) != 0 || (c.Value & 1) == 0) return null;
            return (c.Value & 2) != 0;
        }

        public static void SetHdr(bool on) { SetHdr(Find(), on); }
        static void SetHdr(Panel p, bool on)
        {
            if (p == null) throw new Exception("Экран не найден");
            ColorInfo2 c2;
            if (Info2(p, out c2))
            {
                // SET_HDR_STATE (16): HDR alone; the older call (10) switched the whole advanced colour, and off it would also take
                // the automatic colour management of a 10-bit screen away
                var h = new HdrSet { H = new Header { Type = 16, Size = (uint)Marshal.SizeOf(typeof(HdrSet)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId }, Value = on ? 1u : 0u };
                if (DisplayConfigSetDeviceInfo(ref h) != 0) throw new Exception("Windows не включил HDR");
                return;
            }
            var s = new ColorSet { H = new Header { Type = 10, Size = (uint)Marshal.SizeOf(typeof(ColorSet)), Adapter = p.Path.TgtAdapter, Id = p.Path.TgtId }, Value = on ? 1u : 0u };
            if (DisplayConfigSetDeviceInfo(ref s) != 0) throw new Exception("Windows не включил HDR");
        }

        // --display-check <file>: what the page «Дисплей» would read, for every monitor; nothing is changed
        public static string Check()
        {
            var sb = new System.Text.StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var all = All();
            sb.AppendLine("monitors shown: " + all.Count + ", plugged in: " + Connected() + ", Win+P now: " + Topology() + " (1 built-in only, 2 duplicate, 4 extend, 8 other only)");
            foreach (var x in all)
            {
                var cur = Current(x); int dpi; var dpis = DpiOptions(x, out dpi);
                var modes = Modes(x);
                var t0 = sw.ElapsedMilliseconds; var br = Brightness(x); var t1 = sw.ElapsedMilliseconds;
                sb.AppendLine((x.Internal ? "built-in" : "monitor") + " «" + x.Name + "» " + x.Gdi + " key " + x.Key + (x.Primary ? " (main)" : "") + " at " + x.X + "," + x.Y + " " + x.Width + "×" + x.Height
                    + "; now " + (cur == null ? "?" : cur.Width + "×" + cur.Height + " " + cur.Hz + " Hz") + "; rates at this size: " + string.Join("/", modes.Where(m => cur != null && m.Width == cur.Width && m.Height == cur.Height).Select(m => m.Hz).Distinct().OrderBy(h => h))
                    + "; sizes " + modes.Select(m => m.Width + "x" + m.Height).Distinct().Count() + "; scale " + dpi + " % of " + string.Join("/", dpis) + "; HDR " + (Hdr(x).HasValue ? Hdr(x).ToString() : "no")
                    + "; brightness " + (br.HasValue ? br + " %" : "cannot be read") + " (" + (t1 - t0) + " ms)");
            }
            // the exact rate the screen runs at (a fraction: 59.94 shows as 59 in the list of modes), and every mode Windows
            // lists at the current size with its colour depth
            foreach (var x in all)
            {
                var p = (PathInfo)x.Path;
                sb.Append("«" + x.Name + "» runs at " + p.RefreshNum + "/" + p.RefreshDen + " = " + (p.RefreshDen == 0 ? 0 : p.RefreshNum / (double)p.RefreshDen).ToString("0.000") + " Hz; modes at this size:");
                var cur = NewMode(); EnumDisplaySettings(x.Gdi, -1, ref cur);
                for (int i = 0; ; i++) { var dm = NewMode(); if (!EnumDisplaySettings(x.Gdi, i, ref dm)) break; if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight) sb.Append(" " + dm.dmDisplayFrequency + "Hz/" + dm.dmBitsPerPel + "bpp"); }
                sb.AppendLine();
            }
            foreach (var x in all)
            {
                var pr = ColorProfiles(x);
                var pp = Of(x);
                foreach (int sc in new[] { 0, 1 })
                {
                    IntPtr l; uint c; var names = new List<string>();
                    if (ColorProfileGetDisplayList(sc, pp.Path.SrcAdapter, pp.Path.SrcId, out l, out c) == 0 && l != IntPtr.Zero) { for (int i = 0; i < c; i++) names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(l, i * IntPtr.Size))); LocalFree(l); }
                    string ds = "", de = ""; IntPtr nm;
                    if (ColorProfileGetDisplayDefault(sc, pp.Path.SrcAdapter, pp.Path.SrcId, CptIcc, CpstStandard, out nm) == 0 && nm != IntPtr.Zero) { ds = Marshal.PtrToStringUni(nm); LocalFree(nm); }
                    if (ColorProfileGetDisplayDefault(sc, pp.Path.SrcAdapter, pp.Path.SrcId, CptIcc, CpstExtended, out nm) == 0 && nm != IntPtr.Zero) { de = Marshal.PtrToStringUni(nm); LocalFree(nm); }
                    sb.AppendLine("   scope " + sc + ": src " + pp.Path.SrcId + " adapter " + pp.Path.SrcAdapter.Low + ": default sdr «" + ds + "», advanced «" + de + "»; list: " + string.Join(", ", names));
                }
                var cl = Color(x); sb.AppendLine("«" + x.Name + "» signal: " + (cl == null ? "not reported" : cl.Item1 + ", " + cl.Item2 + " bits per channel"));
                sb.AppendLine("«" + x.Name + "» automatic colour management: " + (AutoColor(x).HasValue ? AutoColor(x).ToString() : "not offered"));
                sb.AppendLine("«" + x.Name + "» colour mode " + ColorMode(x) + " (0 SDR, 1 wide colour, 2 HDR), HDR " + (Hdr(x).HasValue ? Hdr(x).ToString() : "not supported"));
                sb.AppendLine("«" + x.Name + "» colour profiles (scope " + pr.Scope + ", advanced colour " + pr.Advanced + "): default «" + pr.Default + "»; associated: " + string.Join(", ", pr.Associated) + (pr.Error != null ? "; error " + pr.Error : ""));
            }
            // the choice of the rate by the power source, on this laptop's rates
            var rates = new List<int> { 30, 48, 60, 100, 120, 240 };
            sb.AppendLine("rate by power, rates 30/48/60/100/120/240: mains (not chosen) " + DisplayAuto.Pick(rates, 0, true) + ", battery (not chosen) " + DisplayAuto.Pick(rates, 0, false) + ", mains 144 chosen (not there) " + DisplayAuto.Pick(rates, 144, true) + ", battery 120 chosen " + DisplayAuto.Pick(rates, 120, false));
            // RT_PROFILE_SAME=1: every display with a default profile gets that same profile again (changes nothing)
            if (Environment.GetEnvironmentVariable("RT_PROFILE_SAME") == "1")
                foreach (var x in all)
                {
                    var pr = ColorProfiles(x); if (string.IsNullOrEmpty(pr.Default)) { sb.AppendLine("same profile: «" + x.Name + "» has none"); continue; }
                    string err = null; try { SetColorProfile(x, pr.Default); } catch (Exception ex) { err = ex.Message; }
                    sb.AppendLine("same profile: «" + x.Name + "» «" + pr.Default + "» set again " + (err == null ? "ok" : "FAILED: " + err) + ", default now «" + ColorProfiles(x).Default + "»");
                }
            // RT_BRIGHT_SAME=1: every monitor gets the brightness it has (a write that changes nothing), to see the way works
            if (Environment.GetEnvironmentVariable("RT_BRIGHT_SAME") == "1")
                foreach (var x in all)
                {
                    var b = Brightness(x); if (!b.HasValue) { sb.AppendLine("same brightness: «" + x.Name + "» cannot be read"); continue; }
                    var t0 = sw.ElapsedMilliseconds; string err = null;
                    try { SetBrightness(x, b.Value); } catch (Exception ex) { err = ex.Message; }
                    sb.AppendLine("same brightness: «" + x.Name + "» " + b + " % written back " + (err == null ? "ok" : "FAILED: " + err) + " in " + (sw.ElapsedMilliseconds - t0) + " ms, reads now " + Brightness(x) + " %");
                }
            return sb.ToString();
        }

        // all displays go dark; the PC keeps running, any key or mouse move wakes them
        // 20.11: the command goes to a hidden window of Red Tune's own, not to every window of every program
        // (HWND_BROADCAST). Broadcast, each window carried it out whenever it got to its queue; a busy one (the browser
        // with a video) did it late, after a move of the mouse had woken the screens, and put them out again: on
        // 20.11 the screens stayed dark while the laptop ran on, and it took the power button. Then Red Tune itself
        // watches for input and turns the screens on at the first key or mouse move, three ways at once.
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        static System.Windows.Forms.NativeWindow offWindow;
        static volatile int offRun; // a newer «off» ends the watch of an older one

        static uint LastInput() { var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) }; return GetLastInputInfo(ref li) ? li.dwTime : 0; }

        // call from the UI thread (the hidden window lives there)
        public static void MonitorsOff()
        {
            if (offWindow == null) { offWindow = new System.Windows.Forms.NativeWindow(); offWindow.CreateHandle(new System.Windows.Forms.CreateParams { Caption = "Red Tune displays" }); }
            IntPtr h = offWindow.Handle;
            int run = ++offRun;
            Store.Log("дисплеи: выключены по кнопке");
            PostMessage(h, 0x0112, (IntPtr)0xF170, (IntPtr)2); // WM_SYSCOMMAND, SC_MONITORPOWER, off
            var t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(800); // the release of the click and the hand leaving the mouse
                uint seen = LastInput();
                var until = DateTime.Now.AddHours(24);
                while (run == offRun && DateTime.Now < until)
                {
                    System.Threading.Thread.Sleep(100);
                    uint now = LastInput();
                    if (now == seen) continue;
                    SetThreadExecutionState(0x00000002);                    // ES_DISPLAY_REQUIRED: Windows turns the screens on
                    SendMessage(h, 0x0112, (IntPtr)0xF170, (IntPtr)(-1)); // SC_MONITORPOWER, on
                    mouse_event(0x0001, 0, 0, 0, UIntPtr.Zero);             // a move of nothing, which wakes them as a real one does
                    Store.Log("дисплеи: включены по вводу");
                    return;
                }
            }) { IsBackground = true, Name = "displays wake" };
            t.Start();
        }
    }
}
