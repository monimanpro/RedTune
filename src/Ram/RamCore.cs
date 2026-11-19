using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace RedTune.Ram
{
    // Memory timings, speed, FCLK and UCLK mode through AMD's overclocking interface (WMI AMD_ACPI, the one Ryzen Master uses).
    // Ported from RamPanel 1.2.0 (RamTune\RamPanel.ps1), verified-device path only: writing is allowed on the Legion R7000P
    // 83LT with BIOS RLCN31WW and the AodSetupRpl/428 layout; anything else is read-only.
    //
    // Checked on this laptop (10.11.2026): "Set T*" stores any number with no range check (only this code checks);
    // "Set Mem Clock" takes MHz = MT/s / 2; one field can't go back to Auto - only "Reset Mem Para" (all timings, speed,
    // FCLK and UCLK mode to Auto), so Auto for some fields = reset + rewrite of everything that stays manual.
    // Everything takes effect after a reboot.

    class TimingDef
    {
        public string Key, Cmd, Live, Label;
        public TimingDef(string key, string live, string label) { Key = key; Cmd = "Set " + key; Live = live; Label = label; }
    }

    class TimingRow
    {
        public string Key, Label;
        public uint? Id;
        public int? Live, Min, Max;
        public int Step = 1;
        public int Stored;      // the value in the BIOS settings (meaningful when Manual)
        public bool Manual = true;
        public bool Editable { get { return Id != null; } }
    }

    class RamData
    {
        public List<TimingRow> Rows = new List<TimingRow>();
        public bool Known;
        public int? Refi, SpeedLive, FclkLive, UclkLive, MclkLive, UclkLiveMode; // UclkLiveMode: 1 = 1:1, 0 = 1:2
        public string Gdm = "", Cmd2T = "", MemType = "";
        public int SpeedStored, FclkRaw, UclkRaw = 0xFF;
        public bool SpeedManual = true, FclkManual = true, UclkManual;
        public List<int> SpeedList = new List<int>(), FclkList = new List<int>();
        public List<int> FclkAll = new List<int>(); // every FCLK the BIOS takes as a manual value, FclkList is the part offered
        public List<int> SpeedAll = new List<int>(); // the same for the speed, MT/s

        public int PendingCount
        {
            get
            {
                int n = Rows.Count(r => r.Manual && r.Live != null && r.Stored != r.Live);
                if (SpeedManual && SpeedLive != null && SpeedStored != SpeedLive) n++;
                if (FclkManual && FclkLive != null && Math.Abs(FclkRaw - FclkLive.Value) > 20) n++;
                if (UclkManual && UclkLiveMode != null && UclkRaw != UclkLiveMode) n++;
                return n;
            }
        }
    }

    // what the user wants; null = Auto
    class Want
    {
        public Dictionary<string, int?> Timings = new Dictionary<string, int?>();
        public int? Speed, Fclk, Uclk;
    }

    class AodCommand { public uint Id, Arg; public string Text; }

    class ApplyPlan
    {
        public bool Reset;
        public List<AodCommand> Sets = new List<AodCommand>();
        public List<string> Diff = new List<string>();
        public int Tighter;
        public bool Empty { get { return Diff.Count == 0; } }
    }

    static class RamCore
    {
        public static readonly TimingDef[] T =
        {
            new TimingDef("Tcl", "CL", "tCL"), new TimingDef("Trcd", "RCDRD", "tRCD"), new TimingDef("Trp", "RP", "tRP"),
            new TimingDef("Tras", "RAS", "tRAS"), new TimingDef("Trc", "RC", "tRC"), new TimingDef("Twr", "WR", "tWR"),
            new TimingDef("Trfc1", "RFC", "tRFC"), new TimingDef("Trfc2", "RFC2", "tRFC2"), new TimingDef("TrfcSb", "RFCsb", "tRFCsb"),
            new TimingDef("Trtp", "RTP", "tRTP"), new TimingDef("TrrdL", "RRDL", "tRRDL"), new TimingDef("TrrdS", "RRDS", "tRRDS"),
            new TimingDef("Tfaw", "FAW", "tFAW"), new TimingDef("TwtrL", "WTRL", "tWTRL"), new TimingDef("TwtrS", "WTRS", "tWTRS"),
            new TimingDef("TrdrdScL", "RDRDSCL", "tRDRDSCL"), new TimingDef("TrdrdSc", "RDRDSC", "tRDRDSC"), new TimingDef("TrdrdSd", "RDRDSD", "tRDRDSD"),
            new TimingDef("TrdrdDd", "RDRDDD", "tRDRDDD"), new TimingDef("TwrwrScL", "WRWRSCL", "tWRWRSCL"), new TimingDef("TwrwrSc", "WRWRSC", "tWRWRSC"),
            new TimingDef("TwrwrSd", "WRWRSD", "tWRWRSD"), new TimingDef("TwrwrDd", "WRWRDD", "tWRWRDD"), new TimingDef("Twrrd", "WRRD", "tWRRD"),
            new TimingDef("Trdwr", "RDWR", "tRDWR"),
        };
        const string CmdStart = "Command Buffer Start", CmdEnd = "Command Buffer End", CmdReset = "Reset Mem Para";
        const string CmdMem = "Set Mem Clock", CmdFclk = "Set FCLK Frequency", CmdUclk = "Set UCLK DIV1";
        const string AodGuid = "{5ed15dc0-edef-4161-9151-6014c4cc630c}";

        // the verified device and its BIOS settings layout: 0x3C speed flag (1 manual, FF Auto), 0x3D speed MT/s (u16),
        // 0x3F.. timings as [flag][value u16] in the order of T, 0x94 FCLK MHz (u16, FFFF Auto), 0x96 UCLK mode (FF Auto)
        const string VModel = "83LT", VBios = "RLCN31WW", VCpu = "Ryzen 9 8945HX", VLayout = "AodSetupRpl/428";
        const int LSpeedFlag = 0x3C, LSpeed = 0x3D, LTimings = 0x3F, LCount = 25, LFclk = 0x94, LUclk = 0x96;
        // 16.11: which frequencies are offered out of what the BIOS takes, by the kind of memory (the user: wider, and DDR4
        // too; it used to be 4000–6400 MT/s and FCLK 1800–2400 for the one DDR5 laptop). DDR4 runs FCLK = half its speed
        // up to about 1900; DDR5 keeps FCLK near 2000, and 1733 is what this BIOS falls back to. Above 2400 the laptop
        // overheats, so that stays the top.
        static void Corridor(string memType, out int sMin, out int sMax, out int fMin, out int fMax)
        {
            string m = (memType ?? "").ToUpperInvariant();
            if (m.Contains("DDR4")) { sMin = 1600; sMax = 5000; fMin = 1000; fMax = 2000; }
            // the user: a couple of steps below 3000 and below 1600 too. Below 3200 this BIOS takes only 2000 and 2400 MT/s.
            else if (m.Contains("DDR5")) { sMin = 2000; sMax = 8000; fMin = 1500; fMax = 2400; }
            else { sMin = 1600; sMax = 8000; fMin = 1000; fMax = 2400; }
        }

        public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedTune", "Ram");
        public static string ProfileDir { get { return Path.Combine(DataDir, "profiles"); } }
        static string BackupDir { get { return Path.Combine(DataDir, "backup"); } }
        static string PendingFile { get { return Path.Combine(DataDir, "pending.json"); } }
        static string LogFile { get { return Path.Combine(DataDir, "ram.log"); } }

        // ---------- device and support ----------
        public static string DeviceText = "", DeviceKey = "", Why = "";
        public static bool CanWrite, Ready;
        // Experimental: the device is not the verified one but can be written in experimental mode; LayoutKnown: its BIOS
        // settings variable has the verified format, so what is stored can be read and a write compared
        public static bool Experimental, LayoutKnown;
        const string ExpWhy = "устройство не проверено — запись включается ниже, строкой «Экспериментальная запись»";
        static string ConsentFile { get { return Path.Combine(DataDir, "experimental.txt"); } }

        // the owner's consent to experimental writes, kept for this very device (vendor | model | BIOS version): a BIOS
        // update or another machine asks again
        public static bool Consent
        {
            get { try { return DeviceKey.Length > 0 && File.Exists(ConsentFile) && File.ReadAllText(ConsentFile, Encoding.UTF8).Trim() == DeviceKey; } catch { return false; } }
        }

        public static void SetConsent(bool on)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    if (on) File.WriteAllText(ConsentFile, DeviceKey, Encoding.UTF8); else if (File.Exists(ConsentFile)) File.Delete(ConsentFile);
                }
                catch { }
                if (!Experimental) return;
                CanWrite = Consent;
                Why = CanWrite ? "" : ExpWhy;
                Log("experimental writes " + (CanWrite ? "allowed" : "switched off") + " for " + DeviceKey);
            }
        }
        static Dictionary<string, uint> cmds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        static string varName;
        static ManagementObject aod;
        static readonly object gate = new object();

        public static void Log(string m)
        {
            try { Directory.CreateDirectory(DataDir); File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        static uint? Cmd(string name) { uint id; return cmds.TryGetValue(name, out id) ? id : (uint?)null; }

        static string Wmi(string cls, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT " + prop + " FROM " + cls))
                    foreach (ManagementObject o in s.Get()) return Convert.ToString(o[prop]).Trim();
            }
            catch { }
            return "";
        }

        // slow (WMI, firmware): call it off the UI thread; the first live read decides LP/non-LP
        public static void Init(Dictionary<string, string> live)
        {
            lock (gate)
            {
                string vendor = Wmi("Win32_ComputerSystemProduct", "Vendor"), model = Wmi("Win32_ComputerSystemProduct", "Name"), family = Wmi("Win32_ComputerSystemProduct", "Version");
                string bios = Wmi("Win32_BIOS", "SMBIOSBIOSVersion"), cpu = System.Text.RegularExpressions.Regex.Replace(Wmi("Win32_Processor", "Name"), @"\s+", " "), cpuVendor = Wmi("Win32_Processor", "Manufacturer");
                // 16.11: a self-built PC has «System Product Name» there: its board is its model (the key of the consent
                // to experimental writes would otherwise be the same on every such PC with the same BIOS version)
                if (Hw.Blank(model) || Hw.Blank(vendor))
                {
                    string bv = Wmi("Win32_BaseBoard", "Manufacturer"), bp = Wmi("Win32_BaseBoard", "Product");
                    if (!Hw.Blank(bp)) { vendor = bv; model = bp; family = ""; }
                }
                if (Hw.Blank(family)) family = "";
                DeviceText = (family.Length > 0 ? vendor + " " + family + " (" + model + ")" : vendor + " " + model) + " · BIOS " + bios + " · " + cpu;
                DeviceKey = vendor + "|" + model + "|" + bios;
                bool verified = model == VModel && bios == VBios && cpu.Contains(VCpu);
                bool isAmd = cpuVendor.Contains("AMD") || cpu.Contains("AMD") || cpu.Contains("Ryzen");

                try
                {
                    using (var s = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM AMD_ACPI")))
                        aod = s.Get().Cast<ManagementObject>().FirstOrDefault();
                }
                catch { aod = null; }
                cmds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                if (aod != null)
                    foreach (var m in new[] { "GetObjectID", "GetObjectID2" })
                        try
                        {
                            var pack = (ManagementBaseObject)aod.InvokeMethod(m, null, null)["pack"];
                            var ids = (uint[])pack["ID"]; var names = (string[])pack["IDstring"];
                            for (int i = 0; i < Math.Min(ids.Length, names.Length); i++) { var n = (names[i] ?? "").Trim(); if (n.Length > 0) cmds[n] = ids[i]; }
                        }
                        catch { }

                var bytes = FindVar();
                bool layoutOk = bytes != null && (varName + "/" + bytes.Length) == VLayout && LayoutSane(bytes);
                // a check without another machine: RT_RAM_SIM=experimental makes this device "not verified",
                // "experimental,unknownlayout" also hides its settings format. It only ever takes support away.
                // The same words in the file Ram\sim.txt of the data folder do it for the program started the usual way.
                string sim = (Environment.GetEnvironmentVariable("RT_RAM_SIM") ?? "").ToLowerInvariant();
                try { var sf = Path.Combine(DataDir, "sim.txt"); if (sim.Length == 0 && File.Exists(sf)) sim = File.ReadAllText(sf).Trim().ToLowerInvariant(); } catch { }
                if (sim.Contains("experimental")) verified = false;
                if (sim.Contains("unknownlayout")) layoutOk = false;
                int timingCmds = T.Count(t => Cmd(t.Cmd) != null);
                string mem; live.TryGetValue("Type", out mem); mem = mem ?? "";

                // 16.11: three levels, as in RamPanel 1.1. Verified: this laptop with this BIOS, the write is compared byte by
                // byte. Experimental: any other AMD machine with the overclocking interface and socketed DDR4/DDR5 — the
                // command numbers and the ranges come from its own BIOS, writing needs the owner's consent given on the page
                // (kept per device), and where the settings format is not the known one nothing can be compared before the
                // reboot. Everything else is view only.
                CanWrite = false; Experimental = false; LayoutKnown = false;
                if (!isAmd) Why = "процессор не AMD";
                else if (aod == null) Why = "в BIOS нет интерфейса разгона AMD";
                else if (timingCmds == 0 || Cmd(CmdStart) == null || Cmd(CmdEnd) == null) Why = "в интерфейсе разгона AMD нет команд для таймингов";
                else if (mem.Contains("LP")) Why = "распаянная память " + mem + " — запись не поддерживается";
                else if (!verified)
                {
                    Experimental = true; LayoutKnown = layoutOk;
                    CanWrite = Consent;
                    Why = CanWrite ? "" : ExpWhy;
                }
                else if (!layoutOk) Why = "формат настроек BIOS не совпал с проверенным";
                else { CanWrite = true; LayoutKnown = true; Why = ""; }
                Ready = true;
            }
        }

        // ---------- AMD_ACPI ----------
        static void Run(uint id, uint arg)
        {
            var inp = aod.GetMethodParameters("RunCommand");
            var buf = new byte[8];
            BitConverter.GetBytes(id).CopyTo(buf, 0); BitConverter.GetBytes(arg).CopyTo(buf, 4);
            inp["Inbuf"] = buf;
            // 14.11: through System.Management this class gives back only Outbuf, no ReturnValue (CIM in the PowerShell panel made
            // one up, always true). Asking for it threw «Не найдено» after the command had already run, so a write from this
            // page stopped at «Command Buffer Start» and never reached the BIOS. A call that comes back is a command taken;
            // a refused one throws by itself. What the BIOS stored is checked byte by byte in Send.
            ManagementBaseObject o;
            try { o = aod.InvokeMethod("RunCommand", inp, null); }
            catch (Exception ex) { throw new Exception("BIOS отклонил команду 0x" + id.ToString("X") + " со значением " + arg + ": " + ex.Message.Trim()); }
            string res = "";
            try
            {
                var ob = o != null ? o["Outbuf"] as ManagementBaseObject : null;
                var bytes = ob != null ? ob["result"] as byte[] : null;
                if (bytes != null) res = BitConverter.ToString(bytes, 0, Math.Min(8, bytes.Length));
            }
            catch { }
            Log("cmd 0x" + id.ToString("X") + " arg " + arg + " -> " + (res.Length > 0 ? res : "ok"));
        }

        static ManagementBaseObject Pack(string method, uint id)
        {
            try
            {
                var inp = aod.GetMethodParameters(method);
                inp["ID"] = id;
                return (ManagementBaseObject)aod.InvokeMethod(method, inp, null)["pack"];
            }
            catch { return null; }
        }

        static List<int> DList(uint id)
        {
            var res = new List<int>();
            var p = Pack("Getdvalues", id);
            if (p == null) return res;
            try
            {
                int len = Convert.ToInt32(p["Length"]);
                var arr = (Array)p["DValuesBuffer"];
                for (int i = 0; i < Math.Min(len, arr.Length); i++) { int v = Convert.ToInt32(arr.GetValue(i)); if (v > 0) res.Add(v); }
            }
            catch { }
            return res;
        }

        // ---------- the BIOS settings variable ----------
        [DllImport("ntdll.dll")] static extern int RtlAdjustPrivilege(int p, bool e, bool t, out bool w);
        [DllImport("ntdll.dll")] static extern int NtEnumerateSystemEnvironmentValuesEx(int cls, IntPtr buf, ref int len);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint GetFirmwareEnvironmentVariableExW(string n, string g, byte[] b, uint s, out uint a);

        static byte[] ReadVar(string name)
        {
            bool w; RtlAdjustPrivilege(22, true, false, out w);
            var b = new byte[65536]; uint a;
            uint r = GetFirmwareEnvironmentVariableExW(name, AodGuid, b, (uint)b.Length, out a);
            if (r == 0) return null;
            var o = new byte[r]; Array.Copy(b, o, r); return o;
        }

        static List<string> VarNames()
        {
            var res = new List<string>();
            bool w; RtlAdjustPrivilege(22, true, false, out w);
            var want = new Guid(AodGuid); int len = 0;
            NtEnumerateSystemEnvironmentValuesEx(2, IntPtr.Zero, ref len);
            if (len <= 0) len = 4 * 1024 * 1024;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtEnumerateSystemEnvironmentValuesEx(2, buf, ref len) != 0) return res;
                int off = 0;
                while (true)
                {
                    int next = Marshal.ReadInt32(buf, off);
                    var g = new byte[16]; Marshal.Copy(IntPtr.Add(buf, off + 16), g, 0, 16);
                    if (new Guid(g) == want) res.Add(Marshal.PtrToStringUni(IntPtr.Add(buf, off + 32)));
                    if (next == 0) break;
                    off += next;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        static byte[] FindVar()
        {
            var names = new List<string>();
            try { names = VarNames().Where(n => n.StartsWith("AodSetup")).ToList(); } catch { }
            if (names.Count == 0) names = new List<string> { "AodSetupRpl", "AodSetup", "AodSetupPhx", "AodSetupStx", "AodSetupRmb", "AodSetupMdn" };
            foreach (var n in names)
            {
                byte[] b = null;
                try { b = ReadVar(n); } catch { }
                if (b != null) { varName = n; return b; }
            }
            varName = null;
            return null;
        }

        static bool LayoutSane(byte[] b)
        {
            for (int i = 0; i < LCount; i++) { byte f = b[LTimings + 3 * i]; if (f != 0 && f != 1) return false; }
            return b[LSpeedFlag] == 1 || b[LSpeedFlag] == 0xFF;
        }

        static byte[] ReadAod() { var b = varName != null ? ReadVar(varName) : null; if (b == null) throw new Exception("не читаются настройки BIOS (" + varName + ")"); return b; }

        // ---------- the running timings (17.11: the app's own reader; before, ZenStates-Core in a separate PowerShell) ----------
        public static Dictionary<string, string> ReadLive() { return LiveTimings.Read(); }

        static int? Int(Dictionary<string, string> d, string k)
        {
            string s; int v;
            return d.TryGetValue(k, out s) && int.TryParse(s, out v) ? v : (int?)null;
        }

        // ---------- everything the page shows ----------
        public static RamData Load(Dictionary<string, string> lv)
        {
            lock (gate)
            {
                byte[] vb = null;
                if (LayoutKnown) try { vb = ReadAod(); } catch { }
                bool known = LayoutKnown && vb != null && vb.Length == 428;
                var d = new RamData { Known = known };
                for (int i = 0; i < T.Length; i++)
                {
                    var t = T[i];
                    uint? id = aod != null ? Cmd(t.Cmd) : null;
                    int? live = Int(lv, t.Live);
                    if (id == null && live == null) continue;
                    var row = new TimingRow { Key = t.Key, Label = t.Label, Id = id, Live = live, Stored = live ?? 0 };
                    if (id != null)
                    {
                        var rg = Pack("GetMinMaxSteppingValues", id.Value);
                        if (rg != null) { row.Min = Convert.ToInt32(rg["Min"]); row.Max = Convert.ToInt32(rg["Max"]); row.Step = Math.Max(1, Convert.ToInt32(rg["Stepping"])); }
                    }
                    if (known) { row.Stored = BitConverter.ToUInt16(vb, LTimings + 1 + 3 * i); row.Manual = vb[LTimings + 3 * i] == 1; }
                    d.Rows.Add(row);
                }
                d.Refi = Int(lv, "REFI"); d.SpeedLive = Int(lv, "Frequency"); d.FclkLive = Int(lv, "FCLK"); d.UclkLive = Int(lv, "UCLK"); d.MclkLive = Int(lv, "MCLK");
                string s;
                d.Gdm = lv.TryGetValue("GDM", out s) ? s : ""; d.Cmd2T = lv.TryGetValue("Cmd2T", out s) ? s : ""; d.MemType = lv.TryGetValue("Type", out s) ? s : "";
                d.SpeedStored = d.SpeedLive ?? 0; d.FclkRaw = d.FclkLive ?? 0;
                if (d.UclkLive != null && d.MclkLive != null)
                {
                    if (Math.Abs(d.UclkLive.Value - d.MclkLive.Value) < 20) d.UclkLiveMode = 1;
                    else if (Math.Abs(2 * d.UclkLive.Value - d.MclkLive.Value) < 40) d.UclkLiveMode = 0;
                }
                if (d.UclkLiveMode != null) d.UclkRaw = d.UclkLiveMode.Value;
                d.UclkManual = d.UclkLiveMode != null;
                if (known)
                {
                    d.SpeedStored = BitConverter.ToUInt16(vb, LSpeed); d.SpeedManual = vb[LSpeedFlag] == 1;
                    d.FclkRaw = BitConverter.ToUInt16(vb, LFclk); d.FclkManual = d.FclkRaw != 0xFFFF;
                    d.UclkRaw = vb[LUclk]; d.UclkManual = d.UclkRaw != 0xFF;
                }
                if (!known && Experimental) StoredFromPending(d);
                int SpeedMin, SpeedMax, FclkMin, FclkMax;
                Corridor(d.MemType, out SpeedMin, out SpeedMax, out FclkMin, out FclkMax);
                var memId = aod != null ? Cmd(CmdMem) : null;
                if (memId != null)
                {
                    d.SpeedAll = DList(memId.Value).Select(v => v * 2).Distinct().OrderBy(v => v).ToList();
                    d.SpeedList = d.SpeedAll.Where(v => v >= SpeedMin && v <= SpeedMax).ToList();
                    if (d.SpeedManual && d.SpeedStored > 0 && !d.SpeedList.Contains(d.SpeedStored)) { d.SpeedList.Add(d.SpeedStored); d.SpeedList.Sort(); }
                }
                var fId = aod != null ? Cmd(CmdFclk) : null;
                if (fId != null)
                {
                    var allFclk = DList(fId.Value).Distinct().OrderBy(v => v).ToList();
                    d.FclkAll = allFclk;
                    d.FclkList = allFclk.Where(v => v >= FclkMin && v <= FclkMax).ToList();
                    if (d.FclkManual && d.FclkRaw > 0 && !d.FclkList.Contains(d.FclkRaw)) { d.FclkList.Add(d.FclkRaw); d.FclkList.Sort(); }
                    // 16.11: the FCLK that runs now is offered too when the BIOS takes it as a manual value, even below the
                    // usual 1800 (after a start on the fallback settings it runs at 1733): then the list can say what is
                    // really set, and that value can be kept by hand. The power table gives it rounded (1733 for 1733.3).
                    if (d.FclkLive != null && !d.FclkList.Any(v => Math.Abs(v - d.FclkLive.Value) <= 2))
                    {
                        var same = allFclk.Where(v => Math.Abs(v - d.FclkLive.Value) <= 2).ToList();
                        if (same.Count > 0) { d.FclkList.Add(same[0]); d.FclkList.Sort(); }
                    }
                }
                return d;
            }
        }

        public static bool HasSpeed(RamData d) { return Cmd(CmdMem) != null && d.SpeedList.Count > 0; }
        public static bool HasFclk(RamData d) { return Cmd(CmdFclk) != null && d.FclkList.Count > 0; }
        public static bool HasUclk { get { return Cmd(CmdUclk) != null; } }

        public static string CheckValue(TimingRow r, int v)
        {
            if (r.Min != null && (v < r.Min || v > r.Max)) return "допустимо " + r.Min + "–" + r.Max;
            if (r.Min != null && r.Step > 1 && (v - r.Min.Value) % r.Step != 0) return "шаг " + r.Step + ", начиная с " + r.Min;
            return null;
        }

        public static string UclkText(int? v) { return v == null || v == 0xFF ? "Auto" : v == 1 ? "1:1" : "1:2"; }

        // the current BIOS state as a Want (what "nothing changed" looks like)
        public static Want Current(RamData d)
        {
            var w = new Want { Speed = d.SpeedManual ? d.SpeedStored : (int?)null, Fclk = d.FclkManual ? d.FclkRaw : (int?)null, Uclk = d.UclkManual ? d.UclkRaw : (int?)null };
            foreach (var r in d.Rows) w.Timings[r.Key] = r.Manual ? r.Stored : (int?)null;
            return w;
        }

        // compares the wish with what the BIOS holds and decides which commands to send
        public static ApplyPlan Plan(RamData d, Want w)
        {
            var p = new ApplyPlan();
            foreach (var r in d.Rows)
            {
                if (r.Id == null) continue;
                int? v; w.Timings.TryGetValue(r.Key, out v);
                string from = r.Manual ? r.Stored.ToString() : "Auto";
                if (v == null) { if (r.Manual) { p.Diff.Add(r.Label + ": " + from + " → Auto"); p.Reset = true; } }
                else if (!r.Manual || v != r.Stored)
                {
                    string mark = "";
                    if (r.Live != null && v < r.Live) { mark = "   (жёстче, чем работает сейчас)"; p.Tighter++; }
                    p.Diff.Add(r.Label + ": " + from + " → " + v + mark);
                }
            }
            bool speedCh = false, fclkCh = false, uclkCh = false;
            if (Cmd(CmdMem) != null)
            {
                string from = d.SpeedManual ? d.SpeedStored.ToString() : "Auto";
                if (w.Speed == null) { if (d.SpeedManual) { p.Diff.Add("Скорость: " + from + " → Auto"); p.Reset = true; } }
                else if (!d.SpeedManual || w.Speed != d.SpeedStored)
                {
                    string mark = ""; if (d.SpeedLive != null && w.Speed > d.SpeedLive) { mark = "   (выше, чем работает сейчас)"; p.Tighter++; }
                    p.Diff.Add("Скорость: " + from + " → " + w.Speed + " MT/s" + mark); speedCh = true;
                }
            }
            if (Cmd(CmdFclk) != null)
            {
                string from = d.FclkManual ? d.FclkRaw.ToString() : "Auto";
                if (w.Fclk == null) { if (d.FclkManual) { p.Diff.Add("FCLK: " + from + " → Auto"); p.Reset = true; } }
                else if (!d.FclkManual || w.Fclk != d.FclkRaw)
                {
                    string mark = ""; if (d.FclkLive != null && w.Fclk > d.FclkLive) { mark = "   (выше, чем работает сейчас)"; p.Tighter++; }
                    p.Diff.Add("FCLK: " + from + " → " + w.Fclk + " МГц" + mark); fclkCh = true;
                }
            }
            if (Cmd(CmdUclk) != null)
            {
                string from = d.UclkManual ? UclkText(d.UclkRaw) : "Auto";
                if (w.Uclk == null) { if (d.UclkManual) { p.Diff.Add("UCLK: " + from + " → Auto"); p.Reset = true; } }
                else if (!d.UclkManual || w.Uclk != d.UclkRaw)
                {
                    string mark = ""; if (w.Uclk == 1 && d.UclkLiveMode == 0) { mark = "   (выше, чем работает сейчас)"; p.Tighter++; }
                    p.Diff.Add("UCLK: " + from + " → " + UclkText(w.Uclk) + mark); uclkCh = true;
                }
            }
            if (p.Reset && Cmd(CmdReset) == null) throw new Exception("в этом BIOS нет команды «Reset Mem Para», Auto поставить нельзя");
            foreach (var r in d.Rows)
            {
                if (r.Id == null) continue;
                int? v; w.Timings.TryGetValue(r.Key, out v);
                if (v == null) continue;
                // after a reset every manual value goes again, without it only the changed ones
                if (p.Reset || !r.Manual || v != r.Stored) p.Sets.Add(new AodCommand { Id = r.Id.Value, Arg = (uint)v.Value, Text = r.Label + "=" + v });
            }
            if (w.Speed != null && (p.Reset || speedCh) && Cmd(CmdMem) != null) p.Sets.Add(new AodCommand { Id = Cmd(CmdMem).Value, Arg = (uint)(w.Speed.Value / 2), Text = "speed=" + w.Speed });
            if (w.Fclk != null && (p.Reset || fclkCh) && Cmd(CmdFclk) != null) p.Sets.Add(new AodCommand { Id = Cmd(CmdFclk).Value, Arg = (uint)w.Fclk.Value, Text = "FCLK=" + w.Fclk });
            if (w.Uclk != null && (p.Reset || uclkCh) && Cmd(CmdUclk) != null) p.Sets.Add(new AodCommand { Id = Cmd(CmdUclk).Value, Arg = (uint)w.Uclk.Value, Text = "UCLK_DIV1=" + w.Uclk });

            // 16.11: this BIOS keeps the speed on Auto only while everything is on Auto. Any value sent turns the speed flag
            // to "manual" by itself and leaves whatever number lay in the field (seen: tRAS set from all-Auto -> speed
            // "3200", caught by the check after the write, and a reboot would have started the memory at 3200). So when
            // something is sent and the speed was to stay on Auto, the speed is sent too: the stored manual one if there
            // is one, otherwise the speed the memory runs at now. w is changed, so the check after the write and the
            // check after the reboot expect it.
            if (w.Speed == null && p.Sets.Count > 0 && Cmd(CmdMem) != null)
            {
                p.Diff.RemoveAll(x => x.StartsWith("Скорость: "));
                if (d.SpeedManual && d.SpeedStored > 0)
                {
                    w.Speed = d.SpeedStored;
                    p.Diff.Add("Скорость: остаётся " + w.Speed + " MT/s" + SpeedPinNote);
                    if (p.Reset) p.Sets.Add(new AodCommand { Id = Cmd(CmdMem).Value, Arg = (uint)(w.Speed.Value / 2), Text = "speed=" + w.Speed });
                }
                else
                {
                    if (d.SpeedLive == null) throw new Exception("рядом с ручными значениями этот BIOS сам переводит скорость в ручную, а узнать текущую скорость не удалось");
                    w.Speed = d.SpeedLive;
                    p.Diff.Add("Скорость: Auto → " + w.Speed + " MT/s" + SpeedPinNote);
                    p.Sets.Add(new AodCommand { Id = Cmd(CmdMem).Value, Arg = (uint)(w.Speed.Value / 2), Text = "speed=" + w.Speed });
                }
            }
            // experimental mode, settings format unknown: what the BIOS holds for the speed cannot be seen, and the trap
            // above (the speed turning "manual" with a stale number) cannot be noticed after the write either. So the speed
            // goes with every write, as the number chosen on the page (the running one unless it was changed).
            if (!d.Known && p.Sets.Count > 0 && w.Speed != null && Cmd(CmdMem) != null && !p.Sets.Any(c => c.Text.StartsWith("speed=")))
            {
                p.Diff.Add("Скорость: " + w.Speed + " MT/s" + SpeedBlindNote);
                p.Sets.Add(new AodCommand { Id = Cmd(CmdMem).Value, Arg = (uint)(w.Speed.Value / 2), Text = "speed=" + w.Speed });
            }
            return p;
        }

        public const string SpeedBlindNote = "   (записывается с каждым изменением: что хранит BIOS, на этом устройстве не видно)";

        public const string SpeedPinNote = "   (Auto для скорости возможна, только когда всё в Auto)";

        // --ram-plan "tRAS=86;speed=auto;fclk=2000;uclk=auto;all=auto" <out.txt>: what the memory page would send for these
        // changes on top of what the BIOS holds now. Reads only, nothing is written (a check without touching the BIOS).
        public static void PlanToFile(string spec, string file)
        {
            var o = new List<string>();
            try
            {
                var lv = ReadLive();
                Init(lv);
                var d = Load(lv);
                var w = Current(d);
                o.Add("device: " + DeviceText + "   can write: " + CanWrite + (CanWrite ? "" : " (" + Why + ")"));
                o.Add("level: " + (Experimental ? "experimental" : CanWrite ? "verified" : "view only") + "; consent: " + Consent + "; settings format known: " + LayoutKnown + "; stored values readable: " + d.Known);
                o.Add("bios now: speed " + (d.SpeedManual ? d.SpeedStored.ToString() : "Auto") + ", live " + d.SpeedLive + "; manual timings: " + string.Join(" ", d.Rows.Where(r => r.Manual).Select(r => r.Label + "=" + r.Stored)));
                o.Add("memory: " + d.MemType + "; speed offered: " + string.Join(" ", d.SpeedList) + "; all the BIOS takes: " + string.Join(" ", d.SpeedAll));
                o.Add("fclk: live " + d.FclkLive + ", bios " + (d.FclkManual ? d.FclkRaw.ToString() : "Auto") + "; offered: " + string.Join(" ", d.FclkList) + "; all the BIOS takes: " + string.Join(" ", d.FclkAll));
                foreach (var part in (spec ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('='); if (kv.Length != 2) throw new Exception("не понял: " + part);
                    string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim().ToLowerInvariant();
                    int n = 0; bool auto = v == "auto"; if (!auto && !int.TryParse(v, out n)) throw new Exception("не число: " + part);
                    if (k == "all") { foreach (var r in d.Rows) w.Timings[r.Key] = null; w.Speed = w.Fclk = w.Uclk = null; }
                    else if (k == "speed") w.Speed = auto ? (int?)null : n;
                    else if (k == "fclk") w.Fclk = auto ? (int?)null : n;
                    else if (k == "uclk") w.Uclk = auto ? (int?)null : n;
                    else
                    {
                        var row = d.Rows.FirstOrDefault(r => r.Label.ToLowerInvariant() == k || r.Key.ToLowerInvariant() == k);
                        if (row == null) throw new Exception("нет такого тайминга: " + kv[0]);
                        w.Timings[row.Key] = auto ? (int?)null : n;
                    }
                }
                var p = Plan(d, w);
                o.Add("changes: " + (p.Empty ? "none" : ""));
                o.AddRange(p.Diff.Select(x => "  " + x));
                o.Add("reset first: " + p.Reset);
                o.Add("commands: " + (p.Sets.Count == 0 ? "none" : string.Join(" ", p.Sets.Select(c => c.Text))));
                o.Add("speed expected in the BIOS after the write: " + (w.Speed == null ? "Auto" : w.Speed.ToString()));
            }
            catch (Exception e) { o.Add("error: " + e.Message); }
            File.WriteAllLines(file, o, new UTF8Encoding(true));
        }

        // sends the commands and checks the BIOS settings byte by byte; throws when the BIOS stored something else
        public static void Send(RamData d, Want w, ApplyPlan p)
        {
            lock (gate)
            {
                if (!CanWrite) throw new Exception("запись отключена: " + Why);
                // on a device whose settings format is not known (experimental mode) the variable may not even be readable:
                // a copy is kept when it is, and nothing is compared afterwards
                byte[] before = null;
                try { before = ReadAod(); } catch { if (d.Known) throw; }
                if (before != null)
                {
                    Directory.CreateDirectory(BackupDir);
                    File.WriteAllBytes(Path.Combine(BackupDir, varName + "_before_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin"), before);
                }
                if (p.Reset) { Run(Cmd(CmdStart).Value, 0); Run(Cmd(CmdReset).Value, 0); Run(Cmd(CmdEnd).Value, 0); }
                if (p.Sets.Count > 0)
                {
                    Run(Cmd(CmdStart).Value, 0);
                    foreach (var c in p.Sets) Run(c.Id, c.Arg);
                    Run(Cmd(CmdEnd).Value, 0);
                }
                SavePending(w);
                if (!d.Known)
                {
                    // nothing to compare with: what was asked is remembered (pending.json) and checked after the reboot
                    Log("applied [experimental, settings format unknown]: reset=" + p.Reset + " " + string.Join(" ", p.Sets.Select(c => c.Text)) + "  not verifiable before a reboot");
                    return;
                }
                var after = ReadAod();
                var wrong = new List<string>();
                if (after.Length != before.Length) wrong.Add("размер настроек BIOS изменился: " + before.Length + " → " + after.Length);
                else
                {
                    int areaEnd = LTimings + 3 * LCount - 1;
                    for (int k = 0; k < before.Length; k++)
                    {
                        bool inside = (k >= LSpeedFlag && k <= areaEnd) || (k >= LFclk && k <= LUclk);
                        if (!inside && before[k] != after[k]) wrong.Add(string.Format("байт 0x{0:X}: было {1:X2}, стало {2:X2}", k, before[k], after[k]));
                    }
                    for (int i = 0; i < T.Length; i++)
                    {
                        var r = d.Rows.FirstOrDefault(x => x.Key == T[i].Key);
                        if (r == null || r.Id == null) continue;
                        int? v; w.Timings.TryGetValue(r.Key, out v);
                        byte flag = after[LTimings + 3 * i]; int val = BitConverter.ToUInt16(after, LTimings + 1 + 3 * i);
                        if (v == null) { if (flag != 0) wrong.Add(r.Label + ": должен быть Auto, а в BIOS " + val); }
                        else if (flag != 1 || val != v) wrong.Add(r.Label + ": просили " + v + ", в BIOS " + (flag == 1 ? val.ToString() : "Auto"));
                    }
                    if (Cmd(CmdMem) != null)
                    {
                        byte sf = after[LSpeedFlag]; int sv = BitConverter.ToUInt16(after, LSpeed);
                        if (w.Speed == null) { if (sf == 1) wrong.Add("Скорость: должна быть Auto, а в BIOS " + sv); }
                        else if (sf != 1 || sv != w.Speed) wrong.Add("Скорость: просили " + w.Speed + ", в BIOS " + (sf == 1 ? sv.ToString() : "Auto"));
                    }
                    if (Cmd(CmdFclk) != null)
                    {
                        int fv = BitConverter.ToUInt16(after, LFclk);
                        if (w.Fclk == null) { if (fv != 0xFFFF) wrong.Add("FCLK: должен быть Auto, а в BIOS " + fv); }
                        else if (fv != w.Fclk) wrong.Add("FCLK: просили " + w.Fclk + ", в BIOS " + (fv == 0xFFFF ? "Auto" : fv.ToString()));
                    }
                    if (Cmd(CmdUclk) != null)
                    {
                        int uv = after[LUclk];
                        if (w.Uclk == null) { if (uv != 0xFF) wrong.Add("UCLK: должен быть Auto, а в BIOS " + UclkText(uv)); }
                        else if (uv != w.Uclk) wrong.Add("UCLK: просили " + UclkText(w.Uclk) + ", в BIOS " + UclkText(uv));
                    }
                }
                Log("applied" + (Experimental ? " [experimental]" : "") + ": reset=" + p.Reset + " " + string.Join(" ", p.Sets.Select(c => c.Text)) + (wrong.Count > 0 ? "  MISMATCH: " + string.Join("; ", wrong) : "  verified"));
                if (wrong.Count > 0) throw new Exception("В BIOS записалось не то, что просили. Не перезагружайтесь, пока не исправите:\n" + string.Join("\n", wrong));
            }
        }

        public static void Reboot() { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 15 /c \"Red Tune: новые настройки памяти. Отмена: shutdown /a\"") { UseShellExecute = false, CreateNoWindow = true }); }

        // ---------- what was written, checked after the next reboot ----------
        static DateTime BootTime()
        {
            using (var s = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                foreach (ManagementObject o in s.Get()) return ManagementDateTimeConverter.ToDateTime(Convert.ToString(o["LastBootUpTime"]));
            return DateTime.MinValue;
        }

        static object Ser(int? v) { return v == null ? (object)"auto" : v.Value; }
        static int? Des(object o) { if (o == null) return null; var s = Convert.ToString(o); int v; return s == "auto" ? (int?)null : int.TryParse(s, out v) ? v : (int?)null; }

        static void SavePending(Want w)
        {
            try
            {
                var tm = new Dictionary<string, object>();
                foreach (var t in T) { int? v; if (w.Timings.TryGetValue(t.Key, out v)) tm[t.Key] = Ser(v); }
                var o = new Dictionary<string, object> { { "device", DeviceKey }, { "boot", BootTime().ToString("o") }, { "time", DateTime.Now.ToString("o") },
                    { "speed", Ser(w.Speed) }, { "fclk", Ser(w.Fclk) }, { "uclk", Ser(w.Uclk) }, { "timings", tm } };
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(PendingFile, new JavaScriptSerializer().Serialize(o), Encoding.UTF8);
            }
            catch { }
        }

        public static bool HasPending { get { return File.Exists(PendingFile); } }

        // null when there is nothing to check (no write, or no reboot since); otherwise ok + text, and the file is gone
        // experimental mode with an unknown settings format: what the BIOS holds cannot be read, but until the reboot the
        // last write made from here is known (pending.json). Without it a value just written could not be seen or undone:
        // the page would show the running numbers as if nothing waited.
        static void StoredFromPending(RamData d)
        {
            try
            {
                if (!File.Exists(PendingFile)) return;
                var pd = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PendingFile, Encoding.UTF8));
                if (Convert.ToString(pd["device"]) != DeviceKey) return;
                var boot = DateTime.Parse(Convert.ToString(pd["boot"]), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (BootTime() > boot.AddSeconds(30)) return; // written before this boot: CheckPending compares it with what runs
                var tm = pd.ContainsKey("timings") ? pd["timings"] as Dictionary<string, object> : null;
                if (tm != null)
                    foreach (var r in d.Rows)
                    {
                        object o; if (!tm.TryGetValue(r.Key, out o)) continue;
                        var v = Des(o); r.Manual = v != null; if (v != null) r.Stored = v.Value;
                    }
                object so;
                if (pd.TryGetValue("speed", out so)) { var v = Des(so); d.SpeedManual = v != null; if (v != null) d.SpeedStored = v.Value; }
                if (pd.TryGetValue("fclk", out so)) { var v = Des(so); d.FclkManual = v != null; if (v != null) d.FclkRaw = v.Value; }
                if (pd.TryGetValue("uclk", out so)) { var v = Des(so); d.UclkManual = v != null; if (v != null) d.UclkRaw = v.Value; }
            }
            catch { }
        }

        public static Tuple<bool, string> CheckPending(RamData d, Dictionary<string, string> lv)
        {
            try
            {
                if (!File.Exists(PendingFile)) return null;
                var pd = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PendingFile, Encoding.UTF8));
                if (Convert.ToString(pd["device"]) != DeviceKey) { File.Delete(PendingFile); return null; }
                var boot = DateTime.Parse(Convert.ToString(pd["boot"]), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (BootTime() <= boot.AddSeconds(30)) return null;
                var bad = new List<string>(); int n = 0;
                var tm = pd.ContainsKey("timings") ? pd["timings"] as Dictionary<string, object> : null;
                if (tm != null)
                    foreach (var t in T)
                    {
                        object o; if (!tm.TryGetValue(t.Key, out o)) continue;
                        int? v = Des(o); if (v == null) continue;
                        int? cur = Int(lv, t.Live); if (cur == null) continue;
                        n++; if (cur != v) bad.Add(t.Label + ": просили " + v + ", работает " + cur);
                    }
                int? sp = pd.ContainsKey("speed") ? Des(pd["speed"]) : null;
                if (sp != null) { n++; if (d.SpeedLive != sp) bad.Add("Скорость: просили " + sp + ", работает " + d.SpeedLive); }
                int? fc = pd.ContainsKey("fclk") ? Des(pd["fclk"]) : null;
                if (fc != null && d.FclkLive != null) { n++; if (Math.Abs(d.FclkLive.Value - fc.Value) > 20) bad.Add("FCLK: просили " + fc + ", работает " + d.FclkLive); }
                int? uc = pd.ContainsKey("uclk") ? Des(pd["uclk"]) : null;
                if (uc != null && d.UclkLiveMode != null) { n++; if (d.UclkLiveMode != uc) bad.Add("UCLK: просили " + UclkText(uc) + ", работает " + UclkText(d.UclkLiveMode)); }
                File.Delete(PendingFile);
                var when = DateTime.Parse(Convert.ToString(pd["time"])).ToString("dd.MM HH:mm");
                string text = bad.Count > 0 ? "Настройки памяти от " + when + " включились не полностью:\n" + string.Join("\n", bad) + "\n\nBIOS мог отклонить значения или откатить их после неудачного старта."
                            : n == 0 ? "Настройки памяти от " + when + ": всё в Auto, значения BIOS выбрал сам."
                            : "Настройки памяти от " + when + " включились полностью (" + n + " знач.).";
                Log("after-reboot check of the write from " + when + ": " + (bad.Count == 0 ? "all " + n + " running" : string.Join("; ", bad)));
                return Tuple.Create(bad.Count == 0, text);
            }
            catch { return null; }
        }

        // ---------- profiles (same JSON as RamPanel's) ----------
        public static void SaveProfile(string path, Want w)
        {
            var tm = new Dictionary<string, object>();
            foreach (var t in T) { int? v; if (w.Timings.TryGetValue(t.Key, out v)) tm[t.Key] = Ser(v); }
            var o = new Dictionary<string, object> { { "device", DeviceText }, { "speed", Ser(w.Speed) }, { "fclk", Ser(w.Fclk) }, { "uclk", Ser(w.Uclk) }, { "timings", tm } };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(o), Encoding.UTF8);
        }

        // keys missing in the file stay out of the result (the page keeps its own value for them)
        public static Want LoadProfile(string path, out bool hasSpeed, out bool hasFclk, out bool hasUclk)
        {
            var pd = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            var w = new Want();
            hasSpeed = pd.ContainsKey("speed") && pd["speed"] != null; hasFclk = pd.ContainsKey("fclk") && pd["fclk"] != null; hasUclk = pd.ContainsKey("uclk") && pd["uclk"] != null;
            if (hasSpeed) w.Speed = Des(pd["speed"]);
            if (hasFclk) w.Fclk = Des(pd["fclk"]);
            if (hasUclk) w.Uclk = Des(pd["uclk"]);
            var tm = pd.ContainsKey("timings") ? pd["timings"] as Dictionary<string, object> : null;
            if (tm != null) foreach (var kv in tm) w.Timings[kv.Key] = Des(kv.Value);
            return w;
        }

        // the old RamPanel's profiles come along once
        public static void ImportOld()
        {
            try
            {
                var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RamPanel", "profiles");
                if (!Directory.Exists(old)) return;
                Directory.CreateDirectory(ProfileDir);
                foreach (var f in Directory.GetFiles(old, "*.json"))
                {
                    var dst = Path.Combine(ProfileDir, Path.GetFileName(f));
                    if (!File.Exists(dst)) File.Copy(f, dst);
                }
            }
            catch { }
        }
    }
}
