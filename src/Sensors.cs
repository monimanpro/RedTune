using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RedTune
{
    class CpuInfo { public string Name = ""; public int Cores, Threads; public double LoadPct = double.NaN, ClockMHz = double.NaN, PowerW = double.NaN, LimitW = double.NaN; public int TempC = -1, FanRpm = -1; }
    class GpuInfo { public string Name = ""; public bool Ok; public string Note = ""; public uint LoadPct, ClockMHz, MaxClockMHz, MemClockMHz, MaxMemClockMHz, TempC; public double PowerW = double.NaN, LimitW = double.NaN; public ulong VramUsed, VramTotal; public int FanRpm = -1; }
    class RamSlot { public string Bank = "", Maker = "", Part = "", Locator = ""; public ulong Bytes; public int RatedMHz, MHz, MilliVolts; }
    // Temps: °C of the modules that have a sensor (16.11, SpdTemp), empty when none
    class RamInfo { public ulong Total, Used; public int SpeedMHz; public string Type = "DDR5"; public List<RamSlot> Slots = new List<RamSlot>(); public float[] Temps = new float[0]; }

    // Read on the UI timer only while the home page is visible: polling NVML keeps the dGPU awake
    sealed class Sensors : IDisposable
    {
        // 16.11: the load is «% Processor Time» (busy time, 0–100). «% Processor Utility» is busy time times the clock over
        // the base clock (2.5 GHz on the 8945HX), so a full load at 3.9 GHz showed 155 %.
        readonly Pdh cpu = new Pdh(@"\Processor Information(_Total)\% Processor Time", @"\Processor Information(_Total)\% Processor Performance",
                                   @"\Energy Meter(rapl_package0_pkg)\Power");
        readonly double baseMHz;
        // 13.11: without Lenovo's interface there is no CPU temperature from the firmware: the processor's own sensor
        // (Tctl, through PawnIO), else the ACPI thermal zones (Kelvin, the hottest one; many laptops have them, the
        // R7000P has none). No fan reading: Windows has no common way to read laptop fans.
        readonly bool tctl = !Hw.Features && Tctl.Available;
        readonly Pdh zones = Hw.Features || Tctl.Available ? null : new Pdh(@"\Thermal Zone Information(*)\Temperature");
        public bool TempFromAcpi { get { return zones != null; } }
        public readonly CpuInfo Cpu = new CpuInfo();
        public readonly GpuInfo Gpu = new GpuInfo();
        public readonly RamInfo Ram = new RamInfo();
        public bool SkipRam; // the overlay reads the processor and the card only: no module temperatures (SMBus) while a game runs

        public Sensors()
        {
            try
            {
                var key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
                var name = (Registry.GetValue(key, "ProcessorNameString", "") as string ?? "").Trim();
                int cut = name.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (cut > 0) name = name.Substring(0, cut);
                Cpu.Name = name.Replace("AMD ", "").Replace(" Processor", "").Trim();
                baseMHz = Convert.ToDouble(Registry.GetValue(key, "~MHz", 2500));
            }
            catch { baseMHz = 2500; }
            Cpu.Threads = Environment.ProcessorCount;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                    Cpu.Cores = s.Get().Cast<ManagementObject>().Sum(o => Convert.ToInt32(o["NumberOfCores"]));
            }
            catch { }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, ConfiguredVoltage, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                    foreach (ManagementObject o in s.Get())
                    {
                        Ram.Slots.Add(new RamSlot
                        {
                            Bank = (o["BankLabel"] as string ?? "").Trim(), Locator = (o["DeviceLocator"] as string ?? "").Trim(),
                            Maker = (o["Manufacturer"] as string ?? "").Trim(), Part = (o["PartNumber"] as string ?? "").Trim(), Bytes = Convert.ToUInt64(o["Capacity"]),
                            RatedMHz = Convert.ToInt32(o["Speed"] ?? 0), MHz = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0), MilliVolts = Convert.ToInt32(o["ConfiguredVoltage"] ?? 0),
                        });
                        Ram.SpeedMHz = Math.Max(Ram.SpeedMHz, Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0));
                        var t = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0);
                        Ram.Type = t == 34 ? "DDR5" : t == 26 ? "DDR4" : Ram.Type;
                    }
            }
            catch { }
        }

        public void Update(bool withGpu)
        {
            cpu.Collect();
            Cpu.LoadPct = cpu.Get(0);
            var perf = cpu.Get(1);
            Cpu.ClockMHz = double.IsNaN(perf) ? double.NaN : baseMHz * perf / 100;
            var mw = cpu.Get(2);
            Cpu.PowerW = double.IsNaN(mw) ? double.NaN : mw / 1000;
            Cpu.LimitW = CpuLimits.Watts;
            if (Hw.Features)
            {
                Cpu.TempC = Lenovo.GetFeature(Lenovo.CpuTemp);
                Cpu.FanRpm = Lenovo.GetFeature(Lenovo.CpuFan);
                Gpu.FanRpm = Lenovo.GetFeature(Lenovo.GpuFan);
            }
            else
            {
                if (Hw.AsusFw) { Cpu.FanRpm = Asus.FanRpm(Asus.CpuFan); Gpu.FanRpm = Asus.FanRpm(Asus.GpuFan); } // ASUS: the firmware tells the speeds
                else Cpu.FanRpm = Gpu.FanRpm = -1;
                if (tctl) Cpu.TempC = Tctl.Read();
                else try { zones.Collect(); var k = zones.Max(0); Cpu.TempC = double.IsNaN(k) || k < 250 || k > 400 ? -1 : (int)Math.Round(k - 273.15); }
                catch { Cpu.TempC = -1; }
            }

            ulong total, avail;
            if (Native.Memory(out total, out avail)) { Ram.Total = total; Ram.Used = total - avail; }
            // DDR5 modules carry a sensor; DDR4 ones do not, and nothing is looked for there
            if (!SkipRam && Ram.Type == "DDR5" && Ram.Slots.Count > 0) Ram.Temps = SpdTemp.Read();

            if (withGpu) Nvml.Read(Gpu);
            if (!Gpu.Ok && Hw.Features)
            {
                var t = Lenovo.GetFeature(Lenovo.GpuTemp);
                if (t > 0) Gpu.TempC = (uint)t;
            }
        }

        public void Dispose() { cpu.Dispose(); if (zones != null) zones.Dispose(); }
    }

    // NVIDIA Management Library from the driver (System32\nvml.dll)
    static class Nvml
    {
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int GetHandle(uint index, out IntPtr dev);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] static extern int GetName(IntPtr dev, StringBuilder name, uint len);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int GetUtil(IntPtr dev, out Util u);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] static extern int GetClock(IntPtr dev, int type, out uint mhz);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")] static extern int GetMaxClock(IntPtr dev, int type, out uint mhz);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] static extern int GetTemp(IntPtr dev, int sensor, out uint c);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] static extern int GetPower(IntPtr dev, out uint mw);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] static extern int GetLimit(IntPtr dev, out uint mw);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int GetMem(IntPtr dev, out Mem m);

        [StructLayout(LayoutKind.Sequential)] struct Util { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong Total, Free, Used; }

        static bool tried, ready;
        static IntPtr dev;

        public static void Read(GpuInfo g)
        {
            try
            {
                if (!tried)
                {
                    tried = true;
                    ready = Init() == 0 && GetHandle(0, out dev) == 0;
                    if (ready) { var sb = new StringBuilder(96); if (GetName(dev, sb, 96) == 0) g.Name = sb.ToString().Replace("NVIDIA ", "").Replace("GeForce ", ""); }
                }
                if (!ready) { g.Ok = false; g.Note = "нет данных NVIDIA"; return; }
                Util u; Mem m; uint v;
                g.Ok = GetUtil(dev, out u) == 0;
                if (!g.Ok) { g.Note = "видеокарта спит"; return; }
                g.LoadPct = u.Gpu;
                if (GetClock(dev, 0, out v) == 0) g.ClockMHz = v;
                if (g.MaxClockMHz == 0 && GetMaxClock(dev, 0, out v) == 0) g.MaxClockMHz = v;
                if (GetClock(dev, 2, out v) == 0) g.MemClockMHz = v;
                if (g.MaxMemClockMHz == 0 && GetMaxClock(dev, 2, out v) == 0) g.MaxMemClockMHz = v;
                if (GetTemp(dev, 0, out v) == 0) g.TempC = v;
                g.PowerW = GetPower(dev, out v) == 0 ? v / 1000.0 : double.NaN;
                g.LimitW = GetLimit(dev, out v) == 0 ? v / 1000.0 : double.NaN;
                if (GetMem(dev, out m) == 0) { g.VramUsed = m.Used; g.VramTotal = m.Total; }
                g.Note = "";
            }
            catch (DllNotFoundException) { tried = true; ready = false; g.Ok = false; g.Note = "драйвер NVIDIA не найден"; }
            catch (EntryPointNotFoundException) { tried = true; ready = false; g.Ok = false; g.Note = "старый драйвер NVIDIA"; }
        }
    }
}
