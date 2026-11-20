using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace RedTune
{
    // 16.11: the full report for a laptop the program was never tried on (the user's friend's ASUS Vivobook was the first):
    // everything needed to add support for it, read and written into one text file to send. It only reads. No names: the
    // user and computer names are cut out, serial numbers, addresses and the like are never asked for.
    //
    // The ASUS part asks the firmware, through ASUS's own driver (the device \\.\ATKACPI that MyASUS, Armoury Crate and
    // G-Helper use), for the state of the devices it may have: the method DSTS, "get status", with the device numbers the
    // Linux kernel's asus-wmi driver lists. Nothing is set (that would be the method DEVS, which this file does not have).
    static class Diag
    {
        public static string DefaultPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RedTune-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt");
        }

        public static string Full(Settings s)
        {
            var sb = new StringBuilder();
            Action<string> head = t => { sb.AppendLine(); sb.AppendLine("=== " + t + " ==="); };
            Action<string, Action> part = (t, f) => { head(t); try { f(); } catch (Exception ex) { sb.AppendLine("(не прочиталось: " + ex.Message.Trim() + ")"); } };

            sb.Append(Hw.Report());
            sb.AppendLine("Меню: " + string.Join(", ", new[] { "home", "power", "curve", "gpu", "ram", "clean", "display", "dist", "startup", "device", "comp", "settings", "about" }.Where(Hw.PageFits)));
            sb.AppendLine("Питание: " + (Hw.LenovoPower ? "Lenovo" : Hw.Amd ? "лимиты AMD (SMU)" : "—") + " · экспертные функции: " + (s != null && s.Expert ? "вкл" : "выкл"));

            part("Система", () =>
            {
                Wmi(sb, null, "Win32_OperatingSystem", "Caption", "Version", "BuildNumber", "OSArchitecture");
                Wmi(sb, null, "Win32_ComputerSystem", "Manufacturer", "Model", "SystemFamily", "SystemSKUNumber", "PCSystemType", "TotalPhysicalMemory");
                Wmi(sb, null, "Win32_BaseBoard", "Manufacturer", "Product", "Version");
                Wmi(sb, null, "Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate", "EmbeddedControllerMajorVersion", "EmbeddedControllerMinorVersion");
                Wmi(sb, null, "Win32_SystemEnclosure", "ChassisTypes");
            });
            part("Процессор", () => Wmi(sb, null, "Win32_Processor", "Name", "Caption", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "L3CacheSize", "SocketDesignation"));
            part("Видео и экран", () => Wmi(sb, null, "Win32_VideoController", "Name", "DriverVersion", "AdapterRAM", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate", "MaxRefreshRate", "VideoProcessor"));
            part("Память (модули)", () => Wmi(sb, null, "Win32_PhysicalMemory", "DeviceLocator", "BankLabel", "Manufacturer", "PartNumber", "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType", "FormFactor", "ConfiguredVoltage"));
            part("Память: температура модулей (датчики SPD, только чтение)", () =>
            {
                bool ddr5 = false;
                using (var q = new ManagementObjectSearcher("SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                    foreach (ManagementBaseObject o in q.Get()) if (Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) == 34) ddr5 = true;
                if (!ddr5) { sb.AppendLine("память не DDR5: датчиков на модулях нет, не ищу"); return; }
                var t = SpdTemp.Read();
                sb.AppendLine("Датчики: " + (SpdTemp.Hubs.Length > 0 ? string.Join(", ", SpdTemp.Hubs.Select(a => "0x" + a.ToString("X2"))) : "не найдены")
                    + (t.Length > 0 ? " · " + string.Join(" / ", t.Select(v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))) + " °C" : ""));
            });
            part("Диски", () => Wmi(sb, null, "Win32_DiskDrive", "Model", "InterfaceType", "Size", "MediaType"));
            part("Батарея и питание", () =>
            {
                Wmi(sb, null, "Win32_Battery", "Name", "DeviceID", "Chemistry", "DesignVoltage", "EstimatedChargeRemaining", "BatteryStatus");
                Wmi(sb, @"root\WMI", "BatteryStaticData", "DesignedCapacity", "ManufactureName", "DeviceName");
                Wmi(sb, @"root\WMI", "BatteryFullChargedCapacity", "FullChargedCapacity");
                sb.AppendLine("От сети: " + System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus);
                try { foreach (var p in WinPower.List()) sb.AppendLine("План Windows: " + p.Name + (p.Active ? "  (активный)" : "")); } catch (Exception ex) { sb.AppendLine("Планы Windows: " + ex.Message); }
            });

            part("Процессор AMD: SMU", () =>
            {
                if (!Hw.Amd) { sb.AppendLine("процессор не AMD"); return; }
                var lib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
                sb.AppendLine("PawnIO установлен: " + File.Exists(lib) + " · доступ к процессору: " + Tctl.Available + " · температура: " + Tctl.Read() + " °C");
                try { sb.AppendLine("Кривая: " + (Smu.FamilyName ?? "семейства нет в таблице") + (Hw.Curve && Smu.Missing() == null ? " · сейчас " + Store.Join(Smu.Read()) : "")); } catch (Exception ex) { sb.AppendLine("Кривая: " + ex.Message); }
                var i = AmdPower.Check();
                sb.AppendLine("Лимиты: " + (i.Error ?? (i.Codename + " · " + (i.Apu ? "MP1 (STAPM/fast/slow)" : "RSMU (PPT/TDC/EDC)") + " · путь " + i.Path
                    + " · умеет " + string.Join(",", AmdPower.Keys.Where((k, n) => n < i.Can.Length && i.Can[n])) + (i.Limits != null ? " · сейчас " + string.Join("/", i.Limits) + " W" : " · лимиты не прочитались"))));
                var r = Tctl.Execute("ioctl_resolve_pm_table", new ulong[0], 2);
                if (r == null) { sb.AppendLine("Таблица телеметрии: не читается"); return; }
                sb.AppendLine("Таблица телеметрии: версия 0x" + r[0].ToString("X") + ", второе число 0x" + r[1].ToString("X"));
                // the whole table as numbers: the limits, the currents and every core's voltage and clock sit in it, and
                // their places differ by family (16.11: the first 128 numbers were not enough on the R7000P, EDC is far in).
                // The table's size is not given out: the biggest read the module allows is taken; rows of zeros are left out
                if (Tctl.Execute("ioctl_update_pm_table", new ulong[0], 0) == null) return;
                ulong[] o = null;
                // 512 = 4096 bytes: the module gives no more (16.11 on the R7000P: a bigger read came back with zeros after it)
                foreach (var n in new[] { 512, 384, 320, 256, 192, 128, 64 }) { o = Tctl.Execute("ioctl_read_pm_table", new ulong[0], n); if (o != null) break; }
                if (o == null) { sb.AppendLine("Таблица не прочиталась"); return; }
                var f = new float[o.Length * 2]; Buffer.BlockCopy(o, 0, f, 0, o.Length * 8);
                int last = Array.FindLastIndex(f, v => v != 0);
                sb.AppendLine("Прочитано чисел: " + f.Length + ", последнее ненулевое: " + last);
                for (int k = 0; k <= last; k += 8)
                {
                    if (Enumerable.Range(k, 8).All(n => f[n] == 0)) continue;
                    sb.AppendLine(string.Format("  [{0,4}] ", k) + string.Join("  ", Enumerable.Range(k, 8).Select(n => f[n].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).PadLeft(9))));
                }
            });

            part("Память: интерфейс разгона AMD (AMD_ACPI)", () =>
            {
                if (!Hw.Aod) { sb.AppendLine("нет"); return; }
                using (var q = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM AMD_ACPI")))
                    foreach (ManagementObject aod in q.Get())
                        foreach (var m in new[] { "GetObjectID", "GetObjectID2" })
                            try
                            {
                                var pack = (ManagementBaseObject)aod.InvokeMethod(m, null, null)["pack"];
                                var ids = (uint[])pack["ID"]; var names = (string[])pack["IDstring"];
                                var list = new List<string>();
                                for (int k = 0; k < Math.Min(ids.Length, names.Length); k++) { var n = (names[k] ?? "").Trim(); if (n.Length > 0) list.Add(n + "=0x" + ids[k].ToString("X")); }
                                sb.AppendLine(m + " (" + list.Count + "): " + string.Join("; ", list));
                            }
                            catch (Exception ex) { sb.AppendLine(m + ": " + ex.Message.Trim()); }
            });

            part("Интерфейсы прошивки (классы WMI в root\\WMI)", () =>
            {
                var rx = new System.Text.RegularExpressions.Regex("asus|atk|lenovo|^amd|^msi|acer|^hp|dell|huawei|xiaomi|gigabyte|razer|thermal|fan|battery|wmiacpi|^acpi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                using (var q = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM meta_class")))
                    foreach (ManagementClass c in q.Get())
                    {
                        string name = Convert.ToString(c["__CLASS"]);
                        if (!rx.IsMatch(name)) continue;
                        string methods = "";
                        try { methods = string.Join(", ", c.Methods.Cast<MethodData>().Select(m => m.Name)); } catch { }
                        sb.AppendLine(name + (methods.Length > 0 ? "  →  " + methods : ""));
                    }
            });

            part("Устройства ACPI", () =>
            {
                var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var q = new ManagementObjectSearcher("SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI\\\\%'"))
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        var parts = Convert.ToString(o["PNPDeviceID"]).Split('\\');
                        if (parts.Length >= 2) ids.Add(parts[1] + "  (" + Convert.ToString(o["Name"]) + ")");
                    }
                foreach (var id in ids) sb.AppendLine(id);
            });

            part("Службы производителя", () =>
            {
                var rx = new System.Text.RegularExpressions.Regex("asus|armou?ry|atk|lenovo|legion|vantage|ghelper|g-helper|msi|acer|predator|omen|hp ?(app|sys)|dell|alienware|pawnio|ryzen|amd", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                using (var q = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode FROM Win32_Service"))
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        string n = Convert.ToString(o["Name"]), dn = Convert.ToString(o["DisplayName"]);
                        if (rx.IsMatch(n) || rx.IsMatch(dn)) sb.AppendLine(n + " · " + dn + " · " + o["State"] + " · " + o["StartMode"]);
                    }
            });

            part("ASUS: что есть у прошивки (только чтение)", () => Asus(sb));

            part("Журнал Red Tune (конец)", () =>
            {
                var log = Path.Combine(Store.DataDir, "redtune.log");
                if (!File.Exists(log)) { sb.AppendLine("нет"); return; }
                foreach (var l in File.ReadAllLines(log, Encoding.UTF8).Reverse().Take(60).Reverse()) sb.AppendLine(l);
            });

            return Clean(sb.ToString());
        }

        // no names in the report: the Windows user, the computer, the profile folder
        static string Clean(string t)
        {
            var ic = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            Func<string, string> esc = System.Text.RegularExpressions.Regex.Escape;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pc = Environment.MachineName;
            if (!string.IsNullOrEmpty(profile)) t = System.Text.RegularExpressions.Regex.Replace(t, esc(profile), "%профиль%", ic);
            // the user name only where it is a folder under Users: a short name (the author's is three letters, part of
            // "Processor") must not be cut out of ordinary words
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\\Users\\[^\\\s""<>|]+", @"\Users\<пользователь>", ic);
            if (!string.IsNullOrEmpty(pc) && pc.Length >= 4) t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<![A-Za-z0-9_-])" + esc(pc) + @"(?![A-Za-z0-9_-])", "<компьютер>", ic);
            return t;
        }

        static void Wmi(StringBuilder sb, string scope, string cls, params string[] props)
        {
            try
            {
                var q = scope == null ? new ManagementObjectSearcher("SELECT * FROM " + cls) : new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery("SELECT * FROM " + cls));
                using (q)
                {
                    int n = 0;
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        var vals = new List<string>();
                        foreach (var p in props)
                            try
                            {
                                var v = o[p]; if (v == null) continue;
                                string text = v is Array ? string.Join(",", ((Array)v).Cast<object>()) : Convert.ToString(v).Trim();
                                if (text.Length > 0) vals.Add(p + "=" + text);
                            }
                            catch { }
                        sb.AppendLine(cls + (n > 0 ? " #" + (n + 1) : "") + ": " + string.Join(" · ", vals));
                        n++;
                    }
                    if (n == 0) sb.AppendLine(cls + ": нет");
                }
            }
            catch (Exception ex) { sb.AppendLine(cls + ": " + ex.Message.Trim()); }
        }

        // ---------- ASUS ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, ref uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        const uint AtkIoctl = 0x0022240C, Dsts = 0x53545344; // "DSTS": get status. The setter (DEVS) is not here on purpose.

        // the devices an ASUS laptop's firmware may answer for (names as in the Linux kernel's asus-wmi.h)
        static readonly Tuple<uint, string>[] AsusIds =
        {
            Tuple.Create(0x00120075u, "режим производительности (ROG/TUF)"), Tuple.Create(0x00110019u, "режим производительности (Vivobook)"),
            Tuple.Create(0x00110018u, "режим вентилятора (старый)"), Tuple.Create(0x00110011u, "управление теплом"), Tuple.Create(0x00110012u, "управление вентилятором"),
            Tuple.Create(0x00110013u, "вентилятор ЦП, обороты"), Tuple.Create(0x00110014u, "вентилятор ГП, обороты"), Tuple.Create(0x00110031u, "средний вентилятор"),
            Tuple.Create(0x00110024u, "кривая вентилятора ЦП"), Tuple.Create(0x00110025u, "кривая вентилятора ГП"), Tuple.Create(0x00110032u, "кривая среднего вентилятора"),
            Tuple.Create(0x00120094u, "температура ЦП"), Tuple.Create(0x00120097u, "температура ГП"),
            Tuple.Create(0x00120057u, "предел заряда батареи"), Tuple.Create(0x0012006Cu, "вид зарядки"),
            Tuple.Create(0x001200A0u, "лимит sPPT (PL2)"), Tuple.Create(0x001200A3u, "лимит SPL (PL1)"), Tuple.Create(0x001200A1u, "лимит EDC"), Tuple.Create(0x001200A2u, "лимит TDC"),
            Tuple.Create(0x001200B0u, "лимит APU sPPT"), Tuple.Create(0x001200B1u, "лимит платформы sPPT"), Tuple.Create(0x001200C1u, "лимит fPPT"),
            Tuple.Create(0x001200C0u, "NVIDIA Dynamic Boost"), Tuple.Create(0x001200C2u, "цель температуры NVIDIA"),
            Tuple.Create(0x00090020u, "дискретная видеокарта (эко)"), Tuple.Create(0x00090016u, "переключатель видеокарт (ROG)"), Tuple.Create(0x00090026u, "переключатель видеокарт (Vivobook)"), Tuple.Create(0x00090019u, "внешняя видеокарта"),
            Tuple.Create(0x00050019u, "разгон матрицы (overdrive)"), Tuple.Create(0x0005001Eu, "Mini LED"), Tuple.Create(0x0005002Eu, "Mini LED 2"),
            Tuple.Create(0x00050011u, "подсветка экрана"), Tuple.Create(0x00050012u, "яркость экрана"), Tuple.Create(0x00050021u, "подсветка клавиатуры"), Tuple.Create(0x00050022u, "датчик света"),
            Tuple.Create(0x00100011u, "тачпад"), Tuple.Create(0x00100023u, "блокировка Fn"), Tuple.Create(0x00060013u, "камера"), Tuple.Create(0x00040017u, "индикатор микрофона"),
            Tuple.Create(0x00010011u, "Wi-Fi"), Tuple.Create(0x00010013u, "Bluetooth"), Tuple.Create(0x00130022u, "звук при включении"),
            Tuple.Create(0x00060062u, "положение крышки"), Tuple.Create(0x00060077u, "положение крышки (ROG)"), Tuple.Create(0x001200E2u, "энергосбережение контроллера"),
        };

        static void Asus(StringBuilder sb)
        {
            bool asus = Hw.Maker.IndexOf("ASUS", StringComparison.OrdinalIgnoreCase) >= 0;
            IntPtr h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                sb.AppendLine("Драйвер ASUS (\\\\.\\ATKACPI) не открылся, ошибка " + Marshal.GetLastWin32Error() + (asus ? " — нужен «ASUS System Control Interface» (ставится с MyASUS / драйверами с сайта ASUS)" : " — это не ноутбук ASUS"));
                return;
            }
            try
            {
                sb.AppendLine("Драйвер ASUS открыт. Ответы прошивки: число, бит 0x10000 = устройство есть.");
                foreach (var id in AsusIds)
                {
                    var inBuf = new byte[16];
                    BitConverter.GetBytes(Dsts).CopyTo(inBuf, 0); BitConverter.GetBytes(8u).CopyTo(inBuf, 4); BitConverter.GetBytes(id.Item1).CopyTo(inBuf, 8);
                    var outBuf = new byte[16]; uint got = 0;
                    if (!DeviceIoControl(h, AtkIoctl, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, ref got, IntPtr.Zero))
                    { sb.AppendLine(string.Format("0x{0:X8}  {1}: ошибка {2}", id.Item1, id.Item2, Marshal.GetLastWin32Error())); continue; }
                    uint v = BitConverter.ToUInt32(outBuf, 0);
                    bool present = (v & 0x10000) != 0 && v != 0xFFFFFFFE && v != 0xFFFFFFFF;
                    sb.AppendLine(string.Format("0x{0:X8}  {1}: 0x{2:X8}{3}", id.Item1, id.Item2, v, present ? "  есть, значение " + (v & 0xFFFF) : ""));
                }
            }
            finally { CloseHandle(h); }
        }
    }
}
