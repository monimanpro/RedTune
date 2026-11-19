using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace RedTune
{
    // The AMD processor's SMU, the app's own way (17.11). Before: the ryzen-smu-cli tool for the curve (a separate
    // process, about a second per call, .NET 8 and two old port drivers in tow) and ZenStates-Core in a PowerShell
    // for the limits, the power table and the memory registers.
    // Everything goes through PawnIO's RyzenSMU module (Tctl): SMN registers, the power table, and commands. A
    // command is sent through a mailbox, three SMN registers (message, response, arguments), with the exchange
    // ZenStates-Core does (ryzen-smu-cli is built on that library): wait until the response is not 0, clear it, write
    // the six arguments, write the message number, wait for the response (1 = done), read the first argument back.
    // The message numbers, mailbox addresses and power table layouts of each processor family are ZenStates-Core's
    // (1.79, GPL-3.0 as Red Tune); the laptop chips from Strix Point on keep MP1 at RyzenAdj's addresses.
    // Tried on Dragon Range (8945HX); the other families go by the same tables and are «не проверено» (Hw).
    // Curve core order: 0..7 = CCD1 Core 1..8, 8..15 = CCD2 Core 1..8, only the cores that exist. Never on the UI
    // thread: a PCI bus held by another program is waited for.
    static class Smu
    {
        static readonly object gate = new object();
        public const int Min = -50, Max = 30; // the curve: the sliders show this whole range

        struct Box
        {
            public readonly uint Msg, Rsp, Arg;
            public Box(uint msg, uint rsp, uint arg) { Msg = msg; Rsp = rsp; Arg = arg; }
        }

        // which default layout an unlisted power table version falls back to (ZenStates-Core's rule)
        enum Tbl { None, Zen1, ZenP, Zen2, Zen3, Cpu, ApuOld, Apu }

        // a message number of 0: that mailbox has no such command. The curve is read through RSMU when it has the
        // command, else MP1, and written through MP1 first (ZenStates-Core's order)
        sealed class Family
        {
            public string Name, Code;                        // for people; ZenStates-Core's code name (settings, logs)
            public Box Rsmu, Mp1;
            public uint GetRsmu, GetMp1, SetRsmu, SetMp1;    // the curve
            public uint Ppt, Tdc, Edc;                       // the RSMU limits (mW, mA)
            public bool Apu;                                 // STAPM / fast / slow through MP1 (RyzenAdj's 0x14..0x16) instead
            public Tbl Table;
            public bool Curve { get { return GetRsmu != 0 || GetMp1 != 0; } }
        }

        static readonly Box RsmuZen1 = new Box(0x3B1051C, 0x3B10568, 0x3B10590), Mp1Zen1 = new Box(0x3B10528, 0x3B10564, 0x3B10598);
        static readonly Box RsmuCpu = new Box(0x3B10524, 0x3B10570, 0x3B10A40), Mp1Cpu = new Box(0x3B10530, 0x3B1057C, 0x3B109C4);
        static readonly Box RsmuApu = new Box(0x3B10A20, 0x3B10A80, 0x3B10A88), Mp1Apu = new Box(0x3B10528, 0x3B10564, 0x3B10998);
        static readonly Box Mp1Phoenix = new Box(0x3B10528, 0x3B10578, 0x3B10998), Mp1Strix = new Box(0x3B10928, 0x3B10978, 0x3B10998);

        static Family Zen1(string code) { return new Family { Code = code, Rsmu = RsmuZen1, Mp1 = Mp1Zen1, Table = Tbl.Zen1 }; }
        static Family ZenP(string code) { return new Family { Code = code, Rsmu = RsmuZen1, Mp1 = Mp1Zen1, Ppt = 0x64, Tdc = 0x65, Edc = 0x66, Table = Tbl.ZenP }; }
        static Family Zen2(string code) { return new Family { Code = code, Rsmu = RsmuCpu, Mp1 = Mp1Cpu, Ppt = 0x53, Tdc = 0x54, Edc = 0x55, Table = Tbl.Zen2 }; }
        static Family Zen3(string code) { return new Family { Code = code, Rsmu = RsmuCpu, Mp1 = Mp1Cpu, Ppt = 0x53, Tdc = 0x54, Edc = 0x55, GetRsmu = 0x7C, SetRsmu = 0x0A, GetMp1 = 0x48, SetMp1 = 0x35, Table = Tbl.Zen3 }; }
        // Zen 5 has the same numbers
        static Family Zen4(string code) { return new Family { Code = code, Rsmu = RsmuCpu, Mp1 = Mp1Cpu, Ppt = 0x56, Tdc = 0x57, Edc = 0x58, GetRsmu = 0xD5, SetRsmu = 0x06, Table = Tbl.Cpu }; }
        // Raven Ridge .. Dali: the curve can be set (0x58) but not read back, so the app leaves it alone
        static Family Apu0(string code) { return new Family { Code = code, Rsmu = RsmuApu, Mp1 = Mp1Apu, Ppt = 0x2F, Tdc = 0x34, Edc = 0x36, Table = Tbl.ApuOld }; }
        static Family Apu1(string code, bool apu) { return new Family { Code = code, Rsmu = RsmuApu, Mp1 = Mp1Apu, Ppt = 0x33, Tdc = 0x38, Edc = 0x3A, Apu = apu, Table = Tbl.Apu }; }
        static Family Apu2(string code, Box mp1, Tbl table = Tbl.Apu) { return new Family { Code = code, Rsmu = RsmuApu, Mp1 = mp1, Ppt = 0x33, Tdc = 0x38, Edc = 0x3A, Apu = true, GetRsmu = 0xE1, SetRsmu = 0x53, SetMp1 = 0x4B, Table = table }; }

        // by the CPUID family and model, as ZenStates-Core tells the code names apart; where it looks at the package
        // (CPUID is not reachable from here) the model name decides instead
        static Family Lookup(int family, int model, string cpu)
        {
            bool tr = cpu.IndexOf("Threadripper", StringComparison.OrdinalIgnoreCase) >= 0;
            Family f = null;
            if (family == 0x17)
                switch (model)
                {
                    case 0x01: f = Zen1("SummitRidge"); break;
                    case 0x08: f = ZenP("PinnacleRidge"); if (tr) { f.Code = "Colfax"; f.Ppt = 0; f.Tdc = 0x6B; f.Edc = 0x6C; } break;
                    case 0x11: f = Apu0("RavenRidge"); break;
                    case 0x18: f = Apu0("Picasso"); break;
                    case 0x20: f = Apu0("Dali"); break;
                    case 0x50: f = Apu0("FireFlight"); break;
                    case 0x31: f = Zen2(tr ? "CastlePeak" : "Rome"); if (!tr) f.Ppt = f.Tdc = f.Edc = 0; break;
                    case 0x71: f = Zen2("Matisse"); break;
                    case 0x60: f = Apu1("Renoir", true); break;
                    case 0x68: f = Apu1("Lucienne", true); break;
                    case 0x90: case 0x91: f = Apu1("VanGogh", false); break;
                    case 0x98: f = Apu1("Mero", false); break;
                    case 0xA0: f = Apu2("Mendocino", Mp1Phoenix); f.GetRsmu = f.SetRsmu = f.SetMp1 = 0; break; // Zen 2 cores: no curve
                }
            else if (family == 0x19)
                switch (model)
                {
                    case 0x01: f = Zen3("Milan"); break;
                    case 0x08: f = Zen3("Chagall"); break;
                    case 0x21: f = Zen3("Vermeer"); break;
                    case 0x11: f = Zen4("Genoa"); break;
                    case 0x18: f = Zen4("StormPeak"); break;
                    case 0x61: f = Zen4(Regex.IsMatch(cpu, @"\d{4}HX\b", RegexOptions.IgnoreCase) ? "DragonRange" : "Raphael"); break;
                    case 0x50: f = Apu1("Cezanne", true); f.GetRsmu = 0xC3; f.SetRsmu = 0x52; break;
                    case 0x44: f = Apu2("Rembrandt", Mp1Phoenix); break;
                    case 0x74: case 0x75: f = Apu2(Regex.IsMatch(cpu, @"\b8\d{3}") ? "HawkPoint" : "Phoenix", Mp1Phoenix); break;
                    case 0x78: f = Apu2("Phoenix2", Mp1Phoenix); break;
                    case 0x7C: f = Apu2("HawkPoint", Mp1Phoenix); break;
                }
            else if (family == 0x1A)
                switch (model)
                {
                    case 0x02: f = Zen4("Turin"); break;
                    case 0x11: f = Zen4("TurinD"); break;
                    case 0x44: f = Zen4("GraniteRidge"); break;
                    case 0xA0: f = Zen4("Bergamo"); break;
                    case 0x08: f = Zen4("ShimadaPeak"); f.Rsmu = new Box(0x3B10924, 0x3B10970, 0x3B10A40); f.Mp1 = new Box(); f.GetRsmu = 0xA3; break;
                    case 0x20: case 0x24: f = Apu2("StrixPoint", Mp1Strix); break;
                    case 0x60: f = Apu2("KrackanPoint", Mp1Strix, Tbl.None); break;
                    case 0x70: f = Apu2("StrixHalo", Mp1Strix); break;
                    // ZenStates-Core gives it the desktop Zen 4 numbers; a laptop APU all the same
                    case 0x68: f = Zen4("KrackanPoint2"); f.Mp1 = Mp1Strix; f.Apu = true; f.Table = Tbl.None; break;
                }
            if (f != null) f.Name = Regex.Replace(f.Code, "(?<=[a-z])(?=[A-Z0-9])", " "); // «DragonRange» → «Dragon Range»
            return f;
        }

        static Family family; static bool looked; static string cpuId = "";
        static int[] slots; // the mailbox slot (chiplet × 8 + core) of each core that exists, found on the first read

        // «AMD64 Family 25 Model 97 Stepping 2»: Windows keeps the CPUID numbers here
        static Family Detect()
        {
            if (looked) return family;
            lock (gate)
            {
                if (looked) return family;
                try
                {
                    string name = "";
                    using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                        if (k != null) { cpuId = k.GetValue("Identifier") as string ?? ""; name = k.GetValue("ProcessorNameString") as string ?? ""; }
                    var m = Regex.Match(cpuId, @"Family (\d+) Model (\d+)");
                    if (m.Success && name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0) family = Lookup(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), name);
                }
                catch (Exception ex) { Store.Log("процессор: семейство: " + ex.Message); }
                Store.Log("процессор: " + (family != null ? family.Code : "семейства нет в таблице") + " (" + cpuId + ")");
                looked = true;
                return family;
            }
        }

        // ZenStates-Core's code name («DragonRange»), "" when the processor is not in the table
        public static string Code { get { var f = Detect(); return f != null ? f.Code : ""; } }
        // the code name for people when the curve of this processor is known, else null
        public static string FamilyName { get { var f = Detect(); return f != null && f.Curve ? f.Name : null; } }

        // null when the curve can be reached, else what is missing (TunePage, the report)
        public static string Missing()
        {
            var f = Detect();
            if (f == null || !f.Curve) return "Кривая этого процессора программе не известна";
            if (!Tctl.Available) return "Нужен PawnIO (вкладка «Компоненты»)";
            return null;
        }

        // ---------- one command ----------
        const int Done = 1, NotIdle = -1, NoAnswer = -2, RegFailed = -3, BusBusy = -4;

        static string Why(int status)
        {
            switch (status)
            {
                case NotIdle: return "процессор занят другой командой";
                case NoAnswer: return "процессор не ответил на команду";
                case RegFailed: return "PawnIO не дал доступ к процессору";
                case BusBusy: return "шину процессора держит другая программа";
                case 0xFF: return "процессор отклонил команду";
                case 0xFE: return "процессор не знает такую команду";
                case 0xFD: case 0xFC: return "процессор сейчас не принимает команду";
                default: return "процессор ответил кодом 0x" + status.ToString("X2");
            }
        }

        static int Send(Box box, uint msg, ref uint arg)
        {
            int status = RegFailed; uint a = arg;
            if (!Tctl.Hold(5000, () => status = Exchange(box, msg, ref a))) return Tctl.Available ? BusBusy : RegFailed;
            arg = a;
            return status;
        }

        // under the PCI mutex (Tctl.Hold)
        static int Exchange(Box box, uint msg, ref uint arg)
        {
            if (!Idle(box)) return NotIdle;
            if (!Tctl.WriteReg(box.Rsp, 0)) return RegFailed;
            for (uint k = 0; k < 6; k++) if (!Tctl.WriteReg(box.Arg + 4 * k, k == 0 ? arg : 0)) return RegFailed;
            if (!Tctl.WriteReg(box.Msg, msg)) return RegFailed;
            if (!Idle(box)) return NoAnswer;
            uint status;
            if (!Tctl.ReadReg(box.Rsp, out status)) return RegFailed;
            status &= 0xFF;
            if (status == Done && !Tctl.ReadReg(box.Arg, out arg)) return RegFailed;
            return (int)status;
        }

        static bool Idle(Box box)
        {
            uint v;
            for (int n = 0; n < 0x2000; n++) if (Tctl.ReadReg(box.Rsp, out v) && v != 0) return true;
            return false;
        }

        // ---------- the curve ----------
        static uint Mask(int slot) { return (uint)(((slot / 8) << 8) | (slot % 8)) << 20; }

        static int Get(Family f, int slot, out int value)
        {
            uint arg = Mask(slot);
            int st = f.GetRsmu != 0 ? Send(f.Rsmu, f.GetRsmu, ref arg) : Send(f.Mp1, f.GetMp1, ref arg);
            value = (int)arg;
            return st;
        }

        static int Set(Family f, int slot, int value)
        {
            uint arg = Mask(slot) | (uint)(value & 0xFFFF); // the value in 16 bits, a negative one as two's complement
            return f.SetMp1 != 0 ? Send(f.Mp1, f.SetMp1, ref arg) : Send(f.Rsmu, f.SetRsmu, ref arg);
        }

        static Family Need()
        {
            var why = Missing();
            if (why != null) throw new Exception(why);
            return family;
        }

        // every core's value. The first time all 16 slots are asked: a slot without a core is refused by the SMU, and
        // the ones that answered must be as many as the processor has cores (a core switched off in the BIOS hides its value)
        static int[] ReadAll(Family f)
        {
            if (slots != null)
            {
                var r = new int[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    int st = Get(f, slots[i], out r[i]);
                    if (st != Done) throw new Exception("Чтение кривой, ядро " + (i + 1) + ": " + Why(st));
                }
                return r;
            }
            for (int attempt = 1; ; attempt++)
            {
                var found = new List<int>(); var values = new List<int>(); int last = Done;
                for (int slot = 0; slot < 16; slot++)
                {
                    int v, st = Get(f, slot, out v);
                    if (st == RegFailed || st == BusBusy) throw new Exception("Чтение кривой: " + Why(st));
                    if (st == Done) { found.Add(slot); values.Add(v); } else last = st;
                }
                if (found.Count > 0 && (Hw.Simulated || found.Count == Hw.Cores))
                {
                    slots = found.ToArray();
                    Store.Log("кривая: ядра в ячейках " + string.Join(",", slots));
                    return values.ToArray();
                }
                if (attempt < 3) { Thread.Sleep(50); continue; }
                throw new Exception(found.Count == 0 ? "Чтение кривой: " + Why(last) : "Чтение кривой: ядер ответило " + found.Count + ", а в процессоре " + Hw.Cores + " (часть ядер отключена в BIOS?)");
            }
        }

        public static int[] Read()
        {
            lock (gate) return Hw.Fit(ReadAll(Need())); // a simulated smaller CPU sees the first cores
        }

        // writes, reads back, up to 3 tries; returns the values the CPU reports afterwards
        public static int[] Write(int[] values)
        {
            if (Hw.Simulated) throw new Exception("Симуляция другого процессора: в процессор ничего не пишется");
            if (values == null || values.Length != Hw.CurveCores || values.Any(v => v < Min || v > Max)) throw new ArgumentException("Значения должны быть от " + Min + " до " + Max);
            lock (gate)
            {
                var f = Need();
                if (slots == null) ReadAll(f);
                if (slots.Length != values.Length) throw new Exception("Запись кривой: ядер " + slots.Length + ", значений " + values.Length);
                int[] now = null; string refused = null;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    refused = null;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        int st = Set(f, slots[i], values[i]);
                        if (st == RegFailed || st == BusBusy) throw new Exception("Запись кривой: " + Why(st));
                        if (st != Done && refused == null) refused = "Запись кривой, ядро " + (i + 1) + ": " + Why(st);
                    }
                    Thread.Sleep(100);
                    now = ReadAll(f);
                    if (now.SequenceEqual(values)) return now;
                }
                if (refused != null) throw new Exception(refused);
                return now;
            }
        }

        // ---------- the power limits ----------
        // the families whose power is three limits (STAPM, fast, slow) through MP1: the laptop APUs since Renoir
        public static bool Apu { get { var f = Detect(); return f != null && f.Apu && f.Mp1.Msg != 0; } }

        // which of PPT, TDC, EDC, STAPM, fast, slow this processor takes (AmdPower.Keys order)
        public static bool[] Can()
        {
            var f = Detect(); var can = new bool[6];
            if (f == null) return can;
            bool apu = Apu;
            can[0] = f.Ppt != 0 && !apu; can[1] = f.Tdc != 0 && !apu; can[2] = f.Edc != 0 && !apu;
            can[3] = can[4] = can[5] = apu;
            return can;
        }

        // limit k (AmdPower.Keys order) in W or A; the answer in the words the log has always had: OK, or why not
        public static string SetLimit(int k, int value)
        {
            var f = Detect();
            if (f == null) return "UNKNOWN_CPU";
            uint arg = (uint)value * 1000; // mW, mA
            int st;
            if (k >= 3)
            {
                if (!Apu) return "NOT_APU";
                st = Send(f.Mp1, (uint)(0x14 + k - 3), ref arg);
            }
            else
            {
                uint id = k == 0 ? f.Ppt : k == 1 ? f.Tdc : f.Edc;
                if (id == 0) return "UNKNOWN_CMD";
                st = Send(f.Rsmu, id, ref arg);
            }
            switch (st)
            {
                case Done: return "OK";
                case NotIdle: return "MAILBOX_BUSY";
                case NoAnswer: return "TIMEOUT";
                case RegFailed: return "NO_PAWNIO";
                case BusBusy: return "PCI_BUS_BUSY";
                case 0xFF: return "FAILED";
                case 0xFE: return "UNKNOWN_CMD";
                case 0xFD: return "REJECTED_PREREQ";
                case 0xFC: return "REJECTED_BUSY";
                default: return "STATUS_0x" + st.ToString("X");
            }
        }

        // STAPM, fast and slow in W as the SMU enforces them now, or null. The power table of AMD's laptop chips starts
        // with STAPM limit / value, fast limit / value, slow limit / value (the layout RyzenAdj reads on all of them
        // since Renoir; checked on Dragon Range, table 0x540208). Elsewhere the numbers are taken only when they look
        // like watts. They follow the Lenovo mode, unlike the firmware's GetFeatureValue
        public static int[] Limits()
        {
            var f = Detect();
            if (f == null || !(f.Apu || f.Code == "DragonRange")) return null;
            var t = Table(24);
            if (t == null) return null;
            var l = new[] { (int)Math.Round(t[0]), (int)Math.Round(t[2]), (int)Math.Round(t[4]) };
            return l.All(w => w >= 3 && w <= 300) ? l : null;
        }

        // ---------- the power table ----------
        static uint tableVersion; static bool tableKnown;

        // the version of the SMU's power table, 0 when it cannot be found
        public static uint TableVersion()
        {
            if (tableKnown) return tableVersion;
            var r = Tctl.Execute("ioctl_resolve_pm_table", new ulong[0], 2);
            if (r == null) return 0;
            tableVersion = (uint)r[0]; tableKnown = true;
            return tableVersion;
        }

        // the first «bytes» of the table, fresh, as the floats it is made of; null when it cannot be read
        public static float[] Table(int bytes)
        {
            if (TableVersion() == 0) return null;
            // 17.11: a refresh asked for right after another one is refused now and then (seen in --limit-shift-test:
            // three readings in a row, the third came back empty), so it is asked again a moment later
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (attempt > 1) Thread.Sleep(15);
                if (Tctl.Execute("ioctl_update_pm_table", new ulong[0], 0) == null) { TableMisses++; continue; }
                var o = Tctl.Execute("ioctl_read_pm_table", new ulong[0], (bytes + 7) / 8);
                if (o == null) { TableMisses++; continue; }
                var t = new float[bytes / 4];
                Buffer.BlockCopy(o, 0, t, 0, t.Length * 4);
                return t;
            }
            return null;
        }
        public static int TableMisses; // refused refreshes and reads since start, for the check

        // where a table version keeps FCLK, UCLK and MCLK (byte offsets); 0 = that version has none. ZenStates-Core's list
        static readonly uint[] Clocks =
        {
            0x1E0001, 0x460, 0x464, 0x468,  0x1E0002, 0x474, 0x478, 0x47C,  0x1E0003, 0x298, 0x29C, 0x2A0,  0x1E0004, 0x298, 0x29C, 0x2A0,
            0x000010, 0x298, 0x29C, 0x2A0,  0x260001, 0x028, 0x02C, 0x030,  0x370000, 0x4B4, 0x4B8, 0x4BC,  0x370001, 0x5A4, 0x5A8, 0x5AC,
            0x370002, 0x5AC, 0x5B0, 0x5B4,  0x370003, 0x5CC, 0x5D0, 0x5D4,  0x370004, 0x5CC, 0x5D0, 0x5D4,  0x370005, 0x5E8, 0x5EC, 0x5F0,
            0x000011, 0x5E8, 0x5EC, 0x5F0,  0x400001, 0x624, 0x628, 0x62C,  0x400002, 0x63C, 0x640, 0x644,  0x400003, 0x660, 0x664, 0x668,
            0x400004, 0x664, 0x668, 0x66C,  0x400005, 0x664, 0x668, 0x66C,  0x450004, 0x664, 0x668, 0x66C,  0x450005, 0x6B0, 0x6B4, 0x6B8,
            0x000012, 0x664, 0x668, 0x66C,
            0x650004, 0, 0, 0,  0x650005, 0, 0, 0,  0x650006, 0, 0, 0,  0x5D0008, 0, 0, 0,  0x5D0009, 0, 0, 0,  0x5D000A, 0, 0, 0,
            0x640107, 0, 0, 0,  0x640108, 0, 0, 0,  0x640109, 0, 0, 0,  0x64010A, 0, 0, 0,  0x64010C, 0, 0, 0,
            0x640207, 0, 0, 0,  0x640208, 0, 0, 0,  0x640209, 0, 0, 0,  0x64020A, 0, 0, 0,  0x64020C, 0, 0, 0,
            0x000100, 0x084, 0x084, 0x084,  0x000101, 0x084, 0x084, 0x084,  0x000200, 0x0B0, 0x0B8, 0x0BC,  0x240802, 0x0BC, 0x0C4, 0x0C8,
            0x240902, 0x0BC, 0x0C4, 0x0C8,  0x000202, 0x0BC, 0x0C4, 0x0C8,  0x240003, 0x0B0, 0x0B8, 0x0BC,  0x240503, 0x0C0, 0x0C8, 0x0CC,
            0x240603, 0x0C0, 0x0C8, 0x0CC,  0x240703, 0x0C0, 0x0C8, 0x0CC,  0x240803, 0x0C0, 0x0C8, 0x0CC,  0x240903, 0x0C0, 0x0C8, 0x0CC,
            0x000203, 0x0C0, 0x0C8, 0x0CC,  0x2D0008, 0x0BC, 0x0C4, 0x0C8,  0x2D0803, 0x0BC, 0x0C4, 0x0C8,  0x2D0903, 0x0BC, 0x0C4, 0x0C8,
            0x380005, 0x0C0, 0x0C8, 0x0CC,  0x380505, 0x0C0, 0x0C8, 0x0CC,  0x380605, 0x0C0, 0x0C8, 0x0CC,  0x380705, 0x0C0, 0x0C8, 0x0CC,
            0x380804, 0x0C0, 0x0C8, 0x0CC,  0x380805, 0x0C0, 0x0C8, 0x0CC,  0x380904, 0x0C0, 0x0C8, 0x0CC,  0x380905, 0x0C0, 0x0C8, 0x0CC,
            0x000300, 0x0C0, 0x0C8, 0x0CC,
            0x540100, 0x118, 0x128, 0x138,  0x540101, 0x118, 0x128, 0x138,  0x540102, 0x118, 0x128, 0x138,  0x540103, 0x118, 0x128, 0x138,
            0x540104, 0x118, 0x128, 0x138,  0x540105, 0x118, 0x128, 0x138,  0x540108, 0x118, 0x128, 0x138,  0x540000, 0x118, 0x128, 0x138,
            0x540001, 0x118, 0x128, 0x138,  0x540002, 0x118, 0x128, 0x138,  0x540003, 0x118, 0x128, 0x138,  0x540004, 0x118, 0x128, 0x138,
            0x540005, 0x118, 0x128, 0x138,  0x540208, 0x11C, 0x12C, 0x13C,
            0x5C0002, 0x194, 0x1A8, 0x1BC,  0x5C0003, 0x194, 0x1A8, 0x1BC,  0x5C0102, 0x194, 0x1A8, 0x1BC,  0x5C0103, 0x194, 0x1A8, 0x1BC,
            0x5C0202, 0x194, 0x1A8, 0x1BC,  0x5C0203, 0x194, 0x1A8, 0x1BC,  0x5C0302, 0x194, 0x1A8, 0x1BC,  0x5C0303, 0x19C, 0x1B0, 0x1C4,
            0x5C0402, 0x19C, 0x1B0, 0x1C4,  0x5C0403, 0x19C, 0x1B0, 0x1C4,
            0x4C0003, 0x174, 0x184, 0x194,  0x4C0004, 0x174, 0x184, 0x194,  0x4C0005, 0x174, 0x184, 0x194,  0x4C0006, 0x174, 0x184, 0x194,
            0x4C0007, 0x174, 0x184, 0x194,  0x4C0008, 0x164, 0x174, 0x184,  0x4C0009, 0x164, 0x174, 0x184,
            0x0005C0, 0x19C, 0x1B0, 0x1C4,  0x000400, 0x118, 0x128, 0x138,  0x0004C0, 0x164, 0x174, 0x184,
            0x621102, 0x11C, 0x12C, 0x13C,  0x621202, 0x11C, 0x12C, 0x13C,  0x620205, 0x11C, 0x12C, 0x13C,  0x620105, 0x11C, 0x12C, 0x13C,
            0x000620, 0x11C, 0x12C, 0x13C,
            0x730204, 0x20C, 0x21C, 0x22C,  0x730404, 0x20C, 0x21C, 0x22C,  0x730604, 0x20C, 0x21C, 0x22C,  0x730804, 0x20C, 0x21C, 0x22C,
            0x730A04, 0x20C, 0x21C, 0x22C,  0x730C04, 0x20C, 0x21C, 0x22C,  0x730E04, 0x20C, 0x21C, 0x22C,  0x731004, 0x20C, 0x21C, 0x22C,
            0x000730, 0x20C, 0x21C, 0x22C,
        };

        static int ClockRow(uint version)
        {
            for (int i = 0; i < Clocks.Length; i += 4) if (Clocks[i] == version) return i;
            return -1;
        }

        // FCLK, UCLK and MCLK in MHz from the power table, or null (the table is not readable or has no clocks)
        public static int[] MemClocks()
        {
            var f = Detect();
            uint v = TableVersion();
            if (f == null || v == 0) return null;
            int row = ClockRow(v);
            if (row < 0)
            {
                // a version not in the list: the family's default layout
                uint hi = v >> 16, d = 0;
                switch (f.Table)
                {
                    case Tbl.Zen1: d = 0x100; break;
                    case Tbl.ZenP: d = 0x101; break;
                    case Tbl.Zen2: d = (v & 7) == 0 ? 0x200u : (v & 7) == 1 || (v & 7) == 2 || (v & 7) == 4 ? 0x202u : 0x203u; break;
                    case Tbl.Zen3: d = 0x300; break;
                    case Tbl.ApuOld: d = 0x10; break;
                    case Tbl.Apu: d = hi == 0x37 ? 0x11u : hi == 0x4C ? 0x4C0u : 0x12u; break;
                    case Tbl.Cpu: d = hi == 0x5C ? 0x5C0u : hi == 0x62 ? 0x620u : hi == 0x73 ? 0x730u : 0x400u; break;
                }
                row = d != 0 ? ClockRow(d) : -1;
            }
            if (row < 0 || Clocks[row + 1] == 0) return null;
            var t = Table((int)Math.Max(Clocks[row + 1], Math.Max(Clocks[row + 2], Clocks[row + 3])) + 4);
            if (t == null) return null;
            return new[] { (int)Math.Round(t[Clocks[row + 1] / 4]), (int)Math.Round(t[Clocks[row + 2] / 4]), (int)Math.Round(t[Clocks[row + 3] / 4]) };
        }

        // ---------- SMN registers (the memory controller's: Ram\Timings) ----------
        // several registers in one go under the PCI mutex; null when the bus is busy. A register that does not read
        // (a memory channel this processor does not have) comes back as 0, as in ZenStates-Core; «unread» counts them
        public static uint[] Registers(uint[] addr, out int unread)
        {
            var r = new uint[addr.Length]; int bad = 0;
            bool held = Tctl.Hold(2000, () =>
            {
                for (int i = 0; i < addr.Length; i++)
                {
                    bool got = false;
                    for (int n = 0; n < 3 && !got; n++) got = Tctl.ReadSmn(addr[i], out r[i]);
                    if (!got) { r[i] = 0; bad++; }
                }
            });
            unread = bad;
            return held ? r : null;
        }
    }
}
