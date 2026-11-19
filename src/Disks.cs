using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace RedTune
{
    // 16.11: the drives for the home page: model, size, how full their volumes are, temperature and wear (the storage
    // reliability counters), read and write speed (PhysicalDisk counters, instance «0 C:»). The model and the volumes are
    // read once, the temperature and the free space every 30 s on a thread of their own, the speeds with each update.
    class DiskInfo
    {
        public int Number;
        public string Model = "", Letters = "", Kind = "Диск";
        public bool Nvme;
        public ulong Size, Used, VolSize;
        public int TempC = -1, TempMaxC = -1, Wear = -1;
        public double ReadBps = double.NaN, WriteBps = double.NaN;
        internal Pdh Counters;
    }

    sealed class Disks : IDisposable
    {
        public readonly List<DiskInfo> List = new List<DiskInfo>();
        readonly Timer slow;

        public Disks()
        {
            try
            {
                var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                var letters = new Dictionary<int, List<string>>();
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                    foreach (ManagementObject p in q.Get())
                    {
                        var l = Convert.ToChar(p["DriveLetter"] ?? '\0');
                        if (l == '\0' || !char.IsLetter(l)) continue;
                        int n = Convert.ToInt32(p["DiskNumber"]);
                        if (!letters.ContainsKey(n)) letters[n] = new List<string>();
                        letters[n].Add(char.ToUpperInvariant(l) + ":");
                    }
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject d in q.Get())
                    {
                        int n; if (!int.TryParse(Convert.ToString(d["DeviceId"]), out n)) continue;
                        int media = Convert.ToInt32(d["MediaType"] ?? 0), bus = Convert.ToInt32(d["BusType"] ?? 0);
                        if (bus == 7) continue; // USB sticks and external drives stay out
                        var ls = letters.ContainsKey(n) ? letters[n].OrderBy(x => x).ToList() : new List<string>();
                        var di = new DiskInfo
                        {
                            Number = n, Model = (Convert.ToString(d["FriendlyName"]) ?? "").Trim(), Size = Convert.ToUInt64(d["Size"] ?? 0UL),
                            Letters = string.Join(" ", ls), Kind = media == 4 ? "SSD" : media == 3 ? "HDD" : "Диск", Nvme = bus == 17,
                        };
                        var inst = n + (ls.Count > 0 ? " " + string.Join(" ", ls) : "");
                        di.Counters = new Pdh(@"\PhysicalDisk(" + inst + @")\Disk Read Bytes/sec", @"\PhysicalDisk(" + inst + @")\Disk Write Bytes/sec");
                        List.Add(di);
                    }
                List.Sort((a, b) => a.Number.CompareTo(b.Number));
            }
            catch (Exception ex) { Store.Log("диски: " + ex.Message); }
            slow = new Timer(_ => Slow(), null, 0, 30000);
        }

        public void Update()
        {
            foreach (var d in List)
                if (d.Counters != null) { d.Counters.Collect(); d.ReadBps = d.Counters.Get(0); d.WriteBps = d.Counters.Get(1); }
        }

        int busy;
        void Slow()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                foreach (var d in List)
                {
                    ulong size = 0, free = 0;
                    foreach (var l in d.Letters.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        try { var di = new DriveInfo(l); if (di.IsReady) { size += (ulong)di.TotalSize; free += (ulong)di.TotalFreeSpace; } } catch { }
                    d.VolSize = size; d.Used = size - free;
                }
                // temperature and wear: MSFT_StorageReliabilityCounter of each physical disk (needs the app's admin rights)
                var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject pd in q.Get())
                    {
                        int n; if (!int.TryParse(Convert.ToString(pd["DeviceId"]), out n)) continue;
                        var d = List.FirstOrDefault(x => x.Number == n);
                        if (d == null) continue;
                        foreach (ManagementObject rc in pd.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            d.TempC = rc["Temperature"] != null ? Convert.ToInt32(rc["Temperature"]) : -1;
                            d.TempMaxC = rc["TemperatureMax"] != null ? Convert.ToInt32(rc["TemperatureMax"]) : -1;
                            d.Wear = rc["Wear"] != null ? Convert.ToInt32(rc["Wear"]) : -1;
                        }
                    }
            }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        public void Dispose() { slow.Dispose(); foreach (var d in List) if (d.Counters != null) d.Counters.Dispose(); }
    }
}
