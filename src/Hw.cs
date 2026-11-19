using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;

namespace RedTune
{
    // What this computer is and which parts of the app it can run (13.11: the first step from «built for one R7000P»
    // towards other laptops). Read once at start: a few WMI queries and the CPU topology, cached.
    // RT_HW_SIM simulates another machine to see how the app hides what does not fit, comma-separated:
    // intel (not AMD), nolenovo (no Lenovo firmware interface), nofans (fans not writable), onechip, noaod.
    static class Hw
    {
        public static string Maker = "", MachineType = "", Model = "", Bios = "", Cpu = "", Sim = "";
        public static string[] Gpus = new string[0];
        public static bool Amd, Intel, GameZone, Features, FanWrite, Aod, AsusFw; // AsusFw (19.11): ASUS's firmware interface answers (Asus.cs)
        // 16.11: a desktop PC (a board in a case, a mini PC), not a laptop: the texts say «компьютер» (L.T), the rows
        // about the charger and the battery are hidden, a failed memory setting is undone with the board's Clear CMOS
        public static bool Desktop, Battery;
        public static string Chassis = "";
        public static int Cores, Threads, Chips;
        static bool done;

        // what a self-built PC has in place of a maker and a model
        public static bool Blank(string s)
        {
            s = (s ?? "").Trim();
            return s.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(s, @"^(system manufacturer|system product name|system version|to be filled by o\.?e\.?m\.?|default string|not applicable|not specified|o\.?e\.?m\.?|none|unknown|x\.x|[0 .-]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static void Detect()
        {
            if (done) return;
            done = true;
            Each("SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct", o =>
            {
                Maker = S(o["Vendor"]); MachineType = S(o["Name"]); Model = S(o["Version"]);
            });
            // a self-built PC: the board is its model
            if (Blank(MachineType) || Blank(Maker))
                Each("SELECT Manufacturer, Product FROM Win32_BaseBoard", o =>
                {
                    if (Blank(S(o["Product"]))) return;
                    Maker = S(o["Manufacturer"]); MachineType = S(o["Product"]); Model = "";
                });
            if (Blank(Model)) Model = "";

            // laptop or desktop: the case type first (a UPS on USB shows as a battery on a desktop), then Windows' own
            // idea of the machine (PCSystemType 2 = mobile), then whether there is a battery at all
            var cases = new List<int>();
            Each("SELECT ChassisTypes FROM Win32_SystemEnclosure", o => { var a = o["ChassisTypes"] as ushort[]; if (a != null) cases.AddRange(a.Select(x => (int)x)); });
            Chassis = string.Join(",", cases);
            int sysType = 0;
            Each("SELECT PCSystemType FROM Win32_ComputerSystem", o => sysType = Convert.ToInt32(o["PCSystemType"] ?? 0));
            Each("SELECT DeviceID FROM Win32_Battery", o => Battery = true);
            int[] laptopCase = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 }, desktopCase = { 3, 4, 5, 6, 7, 13, 15, 16, 24, 35, 36 };
            Desktop = cases.Any(laptopCase.Contains) ? false : cases.Any(desktopCase.Contains) ? true : sysType == 2 ? false : !Battery;
            Each("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", o => Bios = S(o["SMBIOSBIOSVersion"]));
            Each("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", o =>
            {
                Cpu = S(o["Name"]);
                var m = S(o["Manufacturer"]);
                Amd = m.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
                Intel = m.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;
                Cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                Threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
            });
            var gpus = new List<string>();
            Each("SELECT Name FROM Win32_VideoController", o => { var n = S(o["Name"]); if (n.Length > 0) gpus.Add(n); });
            Gpus = gpus.ToArray();

            // a chip (CCD) per L3 cache: the same rule «Распределение» uses
            try { Chips = RedTune.Dist.Native.CacheMasks(3).Where(m => m != 0).Distinct().Count(); } catch { Chips = 1; }
            if (Chips < 1) Chips = 1;

            // Lenovo's firmware interface: power modes (GameZone), features by ID (fans, limits), AMD's overclocking interface
            GameZone = Has("LENOVO_GAMEZONE_DATA");
            Features = Has("LENOVO_OTHER_METHOD");
            if (Features) try { FanWrite = Lenovo.GetFeature(FanControl.CpuFanId) != int.MinValue; } catch { FanWrite = false; }
            Aod = Amd && Has("AMD_ACPI");
            if (!GameZone && !Features) try { AsusFw = Asus.Available; } catch { AsusFw = false; } // ASUS's own driver, on ASUS laptops only

            Sim = (Environment.GetEnvironmentVariable("RT_HW_SIM") ?? "").Trim().ToLowerInvariant();
            if (Sim.Length > 0)
            {
                var k = Sim.Split(',').Select(x => x.Trim()).ToList();
                if (k.Contains("intel")) { Amd = false; Intel = true; Aod = false; Cpu = "Intel Core (симуляция)"; }
                if (k.Contains("nolenovo")) { GameZone = Features = FanWrite = false; Maker = "Other"; }
                if (k.Contains("nofans")) FanWrite = false;
                // asus: the Vivobook Pro 14 OLED of the report the ASUS support was written from (its firmware's answers, nothing reaches a driver)
                AsusFw = false;
                if (k.Contains("asus")) { Asus.Simulate(); AsusFw = true; GameZone = Features = FanWrite = false; Maker = "ASUSTeK COMPUTER INC."; MachineType = "Vivobook_ASUSLaptop M3401QA_M3401QA"; Model = "1.0"; }
                if (k.Contains("onechip")) Chips = 1;
                if (k.Contains("noaod")) Aod = false;
                // igpu: Radeon graphics only (a thin AMD laptop); radeonrx: plus a Radeon discrete card
                if (k.Contains("igpu")) Gpus = new[] { "AMD Radeon 780M Graphics" };
                if (k.Contains("radeonrx")) Gpus = new[] { "AMD Radeon 780M Graphics", "AMD Radeon RX 7600S" };
                // desktop: a board in a case, no battery (laptop: the other way round); rtx5050: a desktop NVIDIA card alone
                if (k.Contains("desktop")) { Desktop = true; Battery = false; Maker = "Board maker"; MachineType = "B650M (симуляция)"; Model = ""; }
                if (k.Contains("laptop")) { Desktop = false; Battery = true; }
                if (k.Contains("rtx5050")) Gpus = new[] { "NVIDIA GeForce RTX 5050" };
                // another Ryzen: cores6 / cores8 (one chiplet), cores12 (two): the curve page, the tuner plan and the
                // power page are drawn for it; nothing is written to the processor while simulating (Smu.Write, AmdPower)
                // cpu:<name with _ for spaces>, e.g. cpu:amd_ryzen_7_7840u: another laptop processor for the power page
                // (its class and ranges); the limits are then not read from this one
                foreach (var c in k.Where(x => x.StartsWith("cpu:")))
                { Cpu = c.Substring(4).Replace('_', ' ').ToUpperInvariant().Replace("AMD RYZEN", "AMD Ryzen"); Amd = true; Intel = false; SimCpu = true; }
                foreach (var c in k.Where(x => x.StartsWith("cores")))
                {
                    int n; if (!int.TryParse(c.Substring(5), out n) || n < 2) continue;
                    Cores = n; Threads = 2 * n; Chips = n > 8 ? 2 : 1; Amd = true; Intel = false;
                    if (!SimCpu) Cpu = "AMD Ryzen (симуляция, " + n + " ядер)"; // a name given with cpu: stays
                }
            }
            Store.Log("железо: " + Line() + (Sim.Length > 0 ? " · симуляция: " + Sim : ""));
        }

        // ---------- which parts fit ----------

        public static bool Simulated { get { return Sim.Length > 0; } }
        public static bool SimCpu;

        // power: Lenovo's GameZone (modes, presets, limits) or, on any other AMD laptop, the limits straight in the SMU
        // (PPT / TDC / EDC or STAPM / fast / slow: Smu, the way UXTU does it with RyzenAdj)
        public static bool Power { get { return GameZone || Amd; } }
        public static bool LenovoPower { get { return GameZone; } }
        // the app's own fan curves write RPM through LENOVO_OTHER_METHOD
        public static bool Fans { get { return Features && FanWrite; } }
        // per-core Curve Optimizer through the SMU (Smu): any Ryzen from Zen 3 on, up to two chiplets of eight cores
        public static bool Curve { get { return Amd && Cpu.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0 && !OldCore; } }

        // 13.11: Curve Optimizer exists from Zen 3 on. Laptop chips of the 3000..7000 names that are Zen+ or Zen 2 have
        // none (ZenStates-Core has no curve messages for them): Ryzen 3000 and 4000, Lucienne (5300U, 5500U, 5700U:
        // odd hundreds in the 5000 U series) and Mendocino (7x20U). Ryzen AI names have no 4-digit number: all Zen 5.
        public static bool OldCore
        {
            get
            {
                var m = System.Text.RegularExpressions.Regex.Match(Cpu, @"\b([3-9])(\d)(\d)(\d)([A-Z]{0,2})\b");
                if (!m.Success) return false;
                int d1 = m.Groups[1].Value[0] - '0', d2 = m.Groups[2].Value[0] - '0', d3 = m.Groups[3].Value[0] - '0';
                string suffix = m.Groups[5].Value;
                if (d1 <= 4) return true;                                      // Picasso, Renoir
                if (d1 == 5 && suffix == "U" && d2 % 2 == 1) return true;       // Lucienne
                if (d1 == 7 && d3 == 2) return true;                            // Mendocino
                return false;
            }
        }

        // ---------- what was tried on a real machine ----------
        // 13.11: everything was tried on one laptop, a Lenovo Legion R7000P (machine type 83LT, Ryzen 9 8945HX). Elsewhere
        // the app works by the same rules, but the writes to the processor and the firmware were never seen working:
        // those parts say so (Компоненты → «Это устройство», the report).
        public static bool VerifiedModel { get { return GameZone && MachineType.StartsWith("83LT", StringComparison.OrdinalIgnoreCase); } }

        // Dragon Range (7x45HX, 8x45HX): the processor family the SMU writes (curve, AMD limits) were tried on
        public static bool VerifiedCpu { get { return System.Text.RegularExpressions.Regex.IsMatch(Cpu, @"\b[78]\d45HX\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase); } }

        // ok (tried on this kind of machine, or nothing in it is specific to one), unverified, hidden
        public static string Status(string key)
        {
            if (!PageFits(key)) return "hidden";
            if (VerifiedModel || key == "dist") return "ok"; // «Распределение» uses only Windows' own process APIs
            switch (key)
            {
                case "curve": return VerifiedCpu ? "ok" : "unverified";
                case "gpu": return "unverified"; // 13.11: read and written on the R7000P's RTX 5070 Laptop only
                case "power": return !LenovoPower && VerifiedCpu ? "ok" : "unverified";
                default: return "unverified"; // memory (writes are blocked off the tried model anyway), Lenovo's device features
            }
        }

        // what «не проверено» means for each part
        public static string WhyUnverified(string key)
        {
            switch (key)
            {
                case "curve": return "Кривая проверена только на Ryzen Dragon Range (7x45HX / 8x45HX). На этом процессоре должна работать, но не проверялась";
                case "power": return LenovoPower ? "Режимы и лимиты Lenovo проверены только на Legion R7000P; у этой модели набор и диапазоны могут отличаться"
                    : "Запись лимитов проверена только на Dragon Range; на этом семействе — нет";
                case "ram": return "Показ таймингов работает везде; запись проверена только на Legion R7000P и на других моделях заблокирована";
                case "device": return AsusFw ? "Функции ASUS написаны по отчёту одной модели (Vivobook M3401) и на настоящем ноутбуке ещё не проверены" : "Функции Lenovo проверены только на Legion R7000P; у этой модели часть может отсутствовать";
                case "gpu": return "Кривая видеокарты проверена на RTX 5070 Laptop (драйвер 621); на этой видеокарте ещё нет";
                default: return "";
            }
        }

        // ---------- the cores of the curve ----------
        // Smu gives one value per enabled physical core; the page groups them by chiplet (CCD1, CCD2)
        public static int CurveCores { get { return Math.Max(1, Cores); } }
        public static int CurveChips { get { return Chips >= 2 ? Chips : 1; } }
        public static int PerChip { get { return Math.Max(1, CurveCores / CurveChips); } }
        public static int ThreadsPerCore { get { return Math.Max(1, Threads / CurveCores); } }
        public static int ChipOf(int core) { return Math.Min(CurveChips - 1, core / PerChip); }
        public static int[] ChipCores(int chip) { return Enumerable.Range(0, CurveCores).Where(i => ChipOf(i) == chip).ToArray(); }
        public static string CoreName(int core) { return "CCD" + (ChipOf(core) + 1) + " Core " + (core - ChipOf(core) * PerChip + 1); }
        // «CCD1 0 -5 … | CCD2 …» for logs and the tuner page
        public static string Describe(int[] v)
        {
            return string.Join(" | ", Enumerable.Range(0, CurveChips).Select(c => "CCD" + (c + 1) + " " + string.Join(" ", ChipCores(c).Where(i => i < v.Length).Select(i => v[i]))));
        }
        // a curve read or stored for another core count: cut or padded with 0
        public static int[] Fit(int[] v)
        {
            if (v == null) return null;
            if (v.Length == CurveCores) return v;
            var r = new int[CurveCores];
            Array.Copy(v, r, Math.Min(v.Length, r.Length));
            return r;
        }
        // programs on chips: only where there are two or more
        public static bool Dist { get { return Chips >= 2; } }
        // memory timings through AMD's overclocking interface in the BIOS
        public static bool Ram { get { return Aod; } }
        // keyboard light, battery, USB, Fn keys: Lenovo's interface
        public static bool Device { get { return GameZone || Features || AsusFw; } }

        // sections that can make a PC unstable: shown only with Settings → «Экспертные функции» (Settings.Expert)
        // 13.11: «Питание» is always in the menu: without Lenovo it reads the limits and picks the Windows plan for
        // everyone, only writing to the processor waits for «Экспертные функции» (AmdPowerPage)
        public static bool Risky(string key) { return key == "curve" || key == "ram" || key == "gpu"; }
        public static bool Nvidia { get { return Gpus.Any(n => n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0); } }
        public static event Action ExpertChanged; // the switch was flipped: the menu is built again
        public static void RaiseExpertChanged() { var h = ExpertChanged; if (h != null) h(); }

        public static bool PageFits(string key)
        {
            switch (key)
            {
                case "power": return Power;
                case "curve": return Curve;
                case "gpu": return Nvidia;
                case "dist": return Dist;
                case "ram": return Ram;
                case "device": return Device;
                default: return true;
            }
        }

        // why a part is hidden, for the report
        public static string WhyNot(string key)
        {
            switch (key)
            {
                case "power": return "нет интерфейса прошивки Lenovo, и процессор не AMD";
                case "gpu": return "нет видеокарты NVIDIA (кривая видеокарты пока только для NVIDIA)";
                case "curve": return OldCore ? "процессор на ядрах Zen 2 или старше: Curve Optimizer у него нет" : "процессор не AMD Ryzen";
                case "dist": return "один чиплет: разводить программы не по чему";
                case "ram": return !Amd ? "процессор не AMD" : "в BIOS нет интерфейса разгона AMD (AMD_ACPI)";
                case "device": return "нет интерфейса прошивки Lenovo или ASUS";
                default: return "";
            }
        }

        // «Lenovo 83LT (Legion R7000P ARX9) · BIOS RLCN31WW · AMD Ryzen 9 8945HX · 16 ядер / 32 потока · 2 чиплета»
        public static string Line()
        {
            var m = (Maker + " " + MachineType).Trim() + (Model.Length > 0 && Model != MachineType ? " (" + Model + ")" : "");
            return m + " · BIOS " + Bios + " · " + Cpu + " · " + Cores + " " + Pl(Cores, "ядро", "ядра", "ядер") + " / " + Threads + " " + Pl(Threads, "поток", "потока", "потоков") + " · " + Chips + " " + Pl(Chips, "чиплет", "чиплета", "чиплетов");
        }

        // the second line under Short(): what is not in the name, «LENOVO 83LT · BIOS RLCN31WW · 16 ядер / 32 потока · 2 чиплета»
        public static string Details()
        {
            return ((Maker + " " + MachineType).Trim() + " · BIOS " + Bios + " · " + Cores + " " + Pl(Cores, "ядро", "ядра", "ядер") + " / " + Threads + " " + Pl(Threads, "поток", "потока", "потоков") + " · " + Chips + " " + Pl(Chips, "чиплет", "чиплета", "чиплетов")).Trim(' ', '·');
        }

        // a short name for «О программе»: the model and the processor without the fluff
        public static string Short()
        {
            var cpu = Cpu.Replace("AMD ", "").Replace("(R)", "").Replace("(TM)", "").Replace(" with Radeon Graphics", "");
            int at = cpu.IndexOf(" Processor", StringComparison.OrdinalIgnoreCase); if (at > 0) cpu = cpu.Substring(0, at);
            var model = Model.Length > 0 ? Model : (Maker + " " + MachineType).Trim();
            return (model + " · " + cpu.Trim()).Trim(' ', '·');
        }

        // the system report: what was found and which parts work (to send when the app is tried on another laptop)
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Red Tune " + System.Windows.Forms.Application.ProductVersion + " — отчёт о системе, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Тип: " + (Desktop ? "настольный ПК" : "ноутбук") + " (корпус " + (Chassis.Length > 0 ? Chassis : "—") + ", батарея: " + YesNo(Battery) + ")");
            sb.AppendLine("Производитель: " + Maker);
            sb.AppendLine("Модель: " + MachineType + (Model.Length > 0 ? " / " + Model : ""));
            sb.AppendLine("BIOS: " + Bios);
            sb.AppendLine("Процессор: " + Cpu + " (" + Cores + " " + Pl(Cores, "ядро", "ядра", "ядер") + ", " + Threads + " " + Pl(Threads, "поток", "потока", "потоков") + ", чиплетов: " + Chips + ")");
            sb.AppendLine("Видеокарты: " + (Gpus.Length > 0 ? string.Join("; ", Gpus) : "—"));
            sb.AppendLine("Lenovo GameZone: " + YesNo(GameZone) + " · функции по ID: " + YesNo(Features) + " · запись оборотов вентиляторов: " + YesNo(FanWrite));
            sb.AppendLine("Прошивка ASUS: " + YesNo(AsusFw) + (AsusFw ? " · режимы: " + YesNo(Asus.HasModes) + " · предел заряда: " + YesNo(Asus.Has(Asus.BatteryLimit)) + " · подсветка клавиатуры: " + YesNo(Asus.Has(Asus.KbdLight)) + " · блокировка Fn: " + YesNo(Asus.Has(Asus.FnLock)) + " · вентиляторы: " + Asus.FanRpm(Asus.CpuFan) + " / " + Asus.FanRpm(Asus.GpuFan) : ""));
            sb.AppendLine("Интерфейс разгона AMD (AMD_ACPI): " + YesNo(Aod));
            if (GameZone) sb.AppendLine("Лимиты своего режима: " + Limits.Describe());
            if (Sim.Length > 0) sb.AppendLine("Симуляция: " + Sim);
            sb.AppendLine("Проверенная модель: " + YesNo(VerifiedModel) + " · проверенное семейство процессора (Dragon Range): " + YesNo(VerifiedCpu));
            sb.AppendLine();
            foreach (var p in new[] { Tuple.Create("power", "Питание"), Tuple.Create("curve", "Кривая"), Tuple.Create("gpu", "Видеокарта"), Tuple.Create("ram", "Память"), Tuple.Create("dist", "Распределение"), Tuple.Create("device", "Устройство") })
            {
                var st = Status(p.Item1);
                sb.AppendLine((st == "ok" ? "✓ " : st == "unverified" ? "? " : "✗ ") + p.Item2 + (st == "hidden" ? " — " + WhyNot(p.Item1) : st == "unverified" ? " — не проверено" : ""));
            }
            sb.AppendLine((Fans ? "✓ " : "✗ ") + "Своя кривая вентиляторов" + (Fans ? "" : " — прошивка не даёт задавать обороты"));
            return sb.ToString();
        }

        static string Pl(int n, string one, string few, string many) { int a = n % 100, b = n % 10; return a >= 11 && a <= 14 ? many : b == 1 ? one : b >= 2 && b <= 4 ? few : many; }
        static string YesNo(bool b) { return b ? "да" : "нет"; }
        static string S(object o) { return (o as string ?? "").Trim(); }

        static void Each(string query, Action<ManagementBaseObject> f)
        {
            try { using (var q = new ManagementObjectSearcher(query)) foreach (ManagementBaseObject o in q.Get()) f(o); }
            catch (Exception ex) { Store.Log("железо: " + query + ": " + ex.Message); }
        }

        static bool Has(string cls)
        {
            try
            {
                using (var q = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM " + cls)))
                    return q.Get().Cast<ManagementBaseObject>().Any();
            }
            catch { return false; }
        }
    }
}
