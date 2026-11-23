using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RedTune
{
    // One custom-mode preset (LLT's "Свой" mode): CPU and GPU limits, fan table, Windows power plan
    class PowerPreset
    {
        public string Name { get; set; }
        public int CpuLong { get; set; } = 50;
        public int CpuShort { get; set; } = 50;
        public int CpuPeak { get; set; } = 50;
        public int CpuCross { get; set; } = 50;
        public int CpuTemp { get; set; } = 85;
        public int GpuBoost { get; set; } = 15;
        public int GpuTgp { get; set; } = 100;
        public int GpuTemp { get; set; } = 87;
        public int GpuTotal { get; set; } = 100;
        public int GpuToCpu { get; set; } = 0;
        public int[] Fan { get; set; } = (int[])Lenovo.DefaultFanTable.Clone();
        public bool FanMax { get; set; }
        // the app drives the fans by these curves (FanControl): % of max RPM at 0,10..100 °C; off = the firmware table above
        public bool FanCurveOn { get; set; }
        public int[] FanCpu { get; set; } = (int[])FanControl.DefaultCurve.Clone();
        public int[] FanGpu { get; set; } = (int[])FanControl.DefaultCurve.Clone();
        // one curve for both fans (they spin alike) instead of a curve each; its temperature: 0 CPU, 1 GPU, 2 the hotter of the two
        public bool FanShared { get; set; }
        public int FanSource { get; set; } = 2;
        public int[] FanBoth { get; set; } = (int[])FanControl.DefaultCurve.Clone();
        // advanced overclocking (only with the BIOS overclocking mode on): off = not touched at all
        public bool OcOn { get; set; }
        public int PboScalar { get; set; } = 1;
        public int PboBoost { get; set; }
        public bool AllCoreOn { get; set; }
        public int AllCore { get; set; }
        public string Plan { get; set; } = "";
        public string Curve { get; set; } = ""; // curve preset switched on with this preset ("" = don't change)
        public string GpuPreset { get; set; } = ""; // «Видеокарта» preset applied with this preset ("" = don't change; 13.11)

        public PowerPreset Clone()
        {
            var p = (PowerPreset)MemberwiseClone();
            p.Fan = (int[])Fan.Clone();
            p.FanCpu = (int[])(FanCpu ?? FanControl.DefaultCurve).Clone();
            p.FanGpu = (int[])(FanGpu ?? FanControl.DefaultCurve).Clone();
            p.FanBoth = (int[])(FanBoth ?? FanControl.DefaultCurve).Clone();
            return p;
        }

        public bool SameAs(PowerPreset o)
        {
            return o != null && CpuLong == o.CpuLong && CpuShort == o.CpuShort && CpuPeak == o.CpuPeak && CpuCross == o.CpuCross && CpuTemp == o.CpuTemp &&
                GpuBoost == o.GpuBoost && GpuTgp == o.GpuTgp && GpuTemp == o.GpuTemp && GpuTotal == o.GpuTotal && GpuToCpu == o.GpuToCpu &&
                Fan.SequenceEqual(o.Fan) && FanMax == o.FanMax && OcOn == o.OcOn && PboScalar == o.PboScalar && PboBoost == o.PboBoost && AllCoreOn == o.AllCoreOn && AllCore == o.AllCore && FanCurveOn == o.FanCurveOn && (FanCpu ?? FanControl.DefaultCurve).SequenceEqual(o.FanCpu ?? FanControl.DefaultCurve) && (FanGpu ?? FanControl.DefaultCurve).SequenceEqual(o.FanGpu ?? FanControl.DefaultCurve) &&
                FanShared == o.FanShared && FanSource == o.FanSource && (FanBoth ?? FanControl.DefaultCurve).SequenceEqual(o.FanBoth ?? FanControl.DefaultCurve) && (Plan ?? "") == (o.Plan ?? "") && (Curve ?? "") == (o.Curve ?? "") && (GpuPreset ?? "") == (o.GpuPreset ?? "");
        }
    }

    class PowerStore
    {
        public string Active { get; set; } = "";
        public List<PowerPreset> Presets { get; set; } = new List<PowerPreset>();
        public bool TuneMade { get; set; } // «00 Тюнинг» was made (kept for old files; since 18.11 the preset comes back whenever it is missing)
        // Windows plan for the other modes ("" = don't change)
        public string PlanQuiet { get; set; } = "";
        public string PlanBalance { get; set; } = "";
        public string PlanPerformance { get; set; } = "";
        public string PlanExtreme { get; set; } = "";
        // curve preset for the other modes ("" = don't change)
        public string CurveQuiet { get; set; } = "";
        public string CurveBalance { get; set; } = "";
        public string CurvePerformance { get; set; } = "";
        // «Видеокарта» preset for the other modes ("" = don't change)
        public string GpuQuiet { get; set; } = "";
        public string GpuBalance { get; set; } = "";
        public string GpuPerformance { get; set; } = "";
        // overclocking was switched on by us once: switching it off then writes 0 (never written otherwise)
        public bool OcWritten { get; set; }
    }

    // Ranges: what LLT offered on this laptop (its godmode.json min/max with its -20 W offset)
    // The ranges of the custom-mode settings, as this laptop's firmware states them (13.11, a step to other Legions):
    // LENOVO_CAPABILITY_DATA_01 gives min / max per setting and mode (feature id | mode << 8, custom = 0xFF), and
    // LENOVO_DISCRETE_DATA the allowed values of the stepped ones (GPU boost, TGP, GPU→CPU). The numbers after each id are
    // only used when the firmware says nothing (on the R7000P it states 50–125 W for the long-term limit, for example).
    static class Limits
    {
        static Dictionary<uint, int[]> ranges;   // id → {min, max}
        static Dictionary<uint, int[]> steps;    // id → allowed values

        static void Load()
        {
            if (ranges != null) return;
            ranges = new Dictionary<uint, int[]>(); steps = new Dictionary<uint, int[]>();
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT IDs, MinValue, MaxValue FROM LENOVO_CAPABILITY_DATA_01"))
                    foreach (ManagementObject o in q.Get())
                    {
                        uint id = Convert.ToUInt32(o["IDs"]); int min = Convert.ToInt32(o["MinValue"]), max = Convert.ToInt32(o["MaxValue"]);
                        if (max > 0 && max >= min) ranges[id] = new[] { min, max };
                    }
                var d = new Dictionary<uint, List<int>>();
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT IDs, Value FROM LENOVO_DISCRETE_DATA"))
                    foreach (ManagementObject o in q.Get())
                    {
                        uint id = Convert.ToUInt32(o["IDs"]); if (id == 0) continue;
                        List<int> l; if (!d.TryGetValue(id, out l)) d[id] = l = new List<int>();
                        l.Add(Convert.ToInt32(o["Value"]));
                    }
                foreach (var kv in d) steps[kv.Key] = kv.Value.Distinct().OrderBy(x => x).ToArray();
                Store.Log("лимиты из прошивки: " + ranges.Count + " диапазонов, " + steps.Count + " списков шагов");
            }
            catch (Exception ex) { Store.Log("лимиты из прошивки: " + ex.Message + " — беру свою таблицу"); }
        }

        const uint Custom = 0xFF00;
        // the firmware's top is kept (more than the laptop is built for is not offered); its bottom is only a suggestion —
        // on the R7000P it says 60 W for the short-term limit while a preset at 50 W runs fine (the SMU table shows 50) — so
        // the lower of the two bottoms stays, and existing presets are not cut
        static int[] Range(uint feature, int min, int max)
        {
            Load(); int[] r;
            return ranges.TryGetValue(feature | Custom, out r) ? new[] { Math.Min(r[0], min), Math.Max(r[1], Math.Min(r[0], min) + 1) } : new[] { min, max };
        }
        static int[] Steps(uint feature, params int[] fallback) { Load(); int[] s; return steps.TryGetValue(feature | Custom, out s) && s.Length > 0 ? s : fallback; }

        public static readonly int[] CpuLong = Range(Lenovo.CpuLong, 30, 125), CpuShort = Range(Lenovo.CpuShort, 40, 130), CpuPeak = Range(Lenovo.CpuPeak, 50, 150),
            CpuCross = Range(Lenovo.CpuCross, 10, 85), CpuTemp = Range(Lenovo.CpuTempLimit, 65, 100);
        public static readonly int[] GpuTemp = Range(Lenovo.GpuTempLimit, 55, 87), GpuTotal = Range(Lenovo.GpuTotal, 0, 100);
        public static readonly int[] GpuBoostSteps = Steps(Lenovo.GpuBoost, 0, 10, 15), GpuTgpSteps = Steps(Lenovo.GpuTgp, 50, 65, 80, 90, 100), GpuToCpuSteps = Steps(Lenovo.GpuToCpu, 0, 10, 15);

        // «CPU long 50–125 W …» for the system report
        public static string Describe()
        {
            Load();
            return "ЦП долг. " + CpuLong[0] + "–" + CpuLong[1] + " W, кратк. " + CpuShort[0] + "–" + CpuShort[1] + " W, пик " + CpuPeak[0] + "–" + CpuPeak[1] + " W, ЦП+ГП " + CpuCross[0] + "–" + CpuCross[1] +
                " W, t ЦП " + CpuTemp[0] + "–" + CpuTemp[1] + " °C · ГП t " + GpuTemp[0] + "–" + GpuTemp[1] + " °C, TGP " + string.Join("/", GpuTgpSteps) + " W, Boost " + string.Join("/", GpuBoostSteps) +
                " W · " + (ranges.Count > 0 ? "из прошивки" : "своя таблица");
        }
    }

    class PowerService : IDisposable
    {
        public PowerStore Data;
        public string LastError;
        public event Action Changed;
        public event Action<int> ModeChanged; // after the start: Fn+Q, the home page, other programs
        // a curve / «Видеокарта» preset the mode or the preset asks for (on the UI thread); the bool is true for the mode found at
        // the program's start (23.11: then the pages' «Применять автоматически» decide, like for the rest of the start)
        public event Action<string, bool> CurveWanted;
        public event Action<string, bool> GpuWanted;
        readonly SynchronizationContext ui;
        ManagementEventWatcher watcher;
        static readonly string FilePath = Path.Combine(Store.DataDir, "power.json");

        public PowerService()
        {
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            Data = Load();
        }

        public PowerPreset Active { get { return Data.Presets.FirstOrDefault(p => p.Name == Data.Active) ?? Data.Presets.FirstOrDefault(); } }

        static PowerStore Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<PowerStore>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null && s.Presets.Count > 0) { if (MakeTunePreset(s)) Save(s); return s; }
                }
            }
            catch (Exception ex) { Store.Log("power.json: " + ex.Message); }
            var fresh = ImportLlt() ?? FromFirmware();
            try { MakeTunePreset(fresh); } catch (Exception ex) { Store.Log("пресет «" + TuneName + "»: " + ex.Message); }
            Save(fresh);
            return fresh;
        }

        public const string TuneName = "00 Тюнинг";

        // 14.11: the preset to run the auto-tune under. The curve was first tuned at 50 W and was valid only there; a tune
        // has to run in the widest conditions the laptop is used in. Its Windows plan lets the cores slow down and sleep
        // (the Balance plan of the «Баланс» mode or of the user's lightest preset: a «maximum performance» plan keeps
        // every core at full clock, and the jumps from idle are never seen); no curve or graphics preset of its own.
        // 18.11: the preset is always there (the auto-tune finds it by its name): made again at the start when it is
        // missing, and the power page does not delete or rename it. Its limits come from FillTune.
        static bool MakeTunePreset(PowerStore s)
        {
            if (s.Presets.Any(p => p.Name == TuneName)) { if (!s.TuneMade) { s.TuneMade = true; return true; } return false; }
            var top = Strongest(s.Presets);
            var calm = s.Presets.OrderBy(p => p.CpuLong).First();
            var t = top.Clone();
            t.Name = TuneName;
            t.Plan = !string.IsNullOrEmpty(s.PlanBalance) ? s.PlanBalance : calm.Plan;
            string from = FillTune(s, t);
            s.Presets.Insert(0, t);
            s.TuneMade = true;
            Store.Log("питание: создан пресет «" + TuneName + "» для автотюна (" + from + "): " + t.CpuLong + " / " + t.CpuShort + " / " + t.CpuPeak + " W, " + t.CpuTemp + " °C");
            return true;
        }

        static PowerPreset Strongest(IEnumerable<PowerPreset> list)
        {
            return list.OrderByDescending(p => p.CpuLong).ThenByDescending(p => p.CpuPeak).ThenByDescending(p => p.CpuTemp).First();
        }

        // 18.11: what the tuning preset holds, read from the machine. The processor's power limits: for each of them the
        // larger of the factory «Макс» mode (the firmware gives its values) and the user's most powerful preset, within
        // what the firmware accepts. Not the firmware's ceiling itself: on the R7000P that is 125 / 130 / 150 W, above
        // every factory mode in long-term power, a regime the laptop is never used in. The temperature limit and the
        // graphics limits are those of the user's most powerful preset (the factory mode has 100 °C; a user who set 90
        // does not work above it). Overclocking off, no curve and no graphics preset of its own. The fans and the plan
        // are left as they are. Returns where the values came from, for the log.
        public static string FillTune(PowerStore s, PowerPreset t)
        {
            var others = s.Presets.Where(p => p.Name != TuneName && p != t).ToList();
            var top = others.Count > 0 ? Strongest(others) : t;
            Dictionary<uint, int> f;
            try { f = Lenovo.ModeDefaults(Lenovo.Performance); } catch { f = new Dictionary<uint, int>(); }
            Func<uint, int, int[], int> pick = (id, own, range) =>
            {
                int v; int best = f.TryGetValue(id, out v) && v > own ? v : own;
                return Math.Max(range[0], Math.Min(range[1], best));
            };
            int cpuLong = pick(Lenovo.CpuLong, top.CpuLong, Limits.CpuLong), cpuShort = pick(Lenovo.CpuShort, top.CpuShort, Limits.CpuShort),
                cpuPeak = pick(Lenovo.CpuPeak, top.CpuPeak, Limits.CpuPeak), cpuCross = pick(Lenovo.CpuCross, top.CpuCross, Limits.CpuCross);
            // the limits keep their order: ЦП+ГП ≤ long ≤ short ≤ peak
            cpuShort = Math.Max(cpuShort, cpuLong); cpuPeak = Math.Max(cpuPeak, cpuShort); cpuCross = Math.Min(cpuCross, cpuLong);
            t.CpuLong = cpuLong; t.CpuShort = cpuShort; t.CpuPeak = cpuPeak; t.CpuCross = cpuCross;
            t.CpuTemp = top.CpuTemp;
            t.GpuBoost = top.GpuBoost; t.GpuTgp = top.GpuTgp; t.GpuTemp = top.GpuTemp; t.GpuTotal = top.GpuTotal; t.GpuToCpu = top.GpuToCpu;
            t.Curve = ""; t.GpuPreset = "";
            t.OcOn = false; t.AllCoreOn = false; t.AllCore = 0; t.PboBoost = 0; t.PboScalar = 1;
            return "из «" + top.Name + "»" + (f.ContainsKey(Lenovo.CpuLong) ? " и заводского режима «Макс»" : "");
        }

        // for --tune-preset-dry: what the rule gives here and on a machine with one weak preset; nothing is saved
        public static string TunePresetText(PowerStore s)
        {
            var sb = new StringBuilder();
            Func<PowerPreset, string> show = p => p.CpuLong + " / " + p.CpuShort + " / " + p.CpuPeak + " W, ЦП+ГП " + p.CpuCross + " W, " + p.CpuTemp + " °C, разгон " + (p.OcOn ? "вкл" : "выкл") + ", кривая «" + p.Curve + "», пресет видеокарты «" + p.GpuPreset + "»";
            foreach (var p in s.Presets) sb.AppendLine("  " + p.Name.PadRight(16) + show(p));
            Dictionary<uint, int> f;
            try { f = Lenovo.ModeDefaults(Lenovo.Performance); } catch (Exception ex) { f = new Dictionary<uint, int>(); sb.AppendLine("factory «Макс»: " + ex.Message); }
            int v;
            sb.AppendLine("factory «Макс»: " + string.Join(" / ", new[] { Lenovo.CpuLong, Lenovo.CpuShort, Lenovo.CpuPeak, Lenovo.CpuCross, Lenovo.CpuTempLimit }.Select(id => f.TryGetValue(id, out v) ? v.ToString() : "-")) + "  (long / short / peak / cross / temp)");
            sb.AppendLine("firmware accepts: long " + Limits.CpuLong[0] + "–" + Limits.CpuLong[1] + ", short " + Limits.CpuShort[0] + "–" + Limits.CpuShort[1] + ", peak " + Limits.CpuPeak[0] + "–" + Limits.CpuPeak[1] + ", cross " + Limits.CpuCross[0] + "–" + Limits.CpuCross[1]);
            var now = s.Presets.FirstOrDefault(p => p.Name == TuneName);
            var t = (now ?? Strongest(s.Presets)).Clone(); t.Name = TuneName;
            sb.AppendLine("the rule here (" + FillTune(s, t) + "):");
            sb.AppendLine("  " + show(t) + (now != null ? (t.SameAs(now) ? "   = as the preset is now" : "   DIFFERS from the preset as it is now") : "   (no such preset now)"));
            // a fresh install: one preset as the firmware had it, and the tuning preset made from it
            var weak = new PowerStore(); weak.Presets.Add(new PowerPreset { Name = "Свой", CpuLong = 50, CpuShort = 50, CpuPeak = 50, CpuCross = 50, CpuTemp = 85 });
            MakeTunePreset(weak);
            sb.AppendLine("a machine with one preset of 50 / 50 / 50 W, 85 °C:");
            foreach (var p in weak.Presets) sb.AppendLine("  " + p.Name.PadRight(16) + show(p));
            // deleted: it comes back
            var gone = new PowerStore { TuneMade = true }; gone.Presets.Add(new PowerPreset { Name = "A", CpuLong = 120, CpuShort = 125, CpuPeak = 145, CpuCross = 80, CpuTemp = 95 });
            sb.AppendLine("a file where the preset was deleted (TuneMade set): made again " + MakeTunePreset(gone) + ", " + show(gone.Presets[0]));
            return sb.ToString();
        }

        public void Save() { Save(Data); }

        static void Save(PowerStore s)
        {
            Directory.CreateDirectory(Store.DataDir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(s), new UTF8Encoding(true));
        }

        // first start: LLT's presets and plan mapping, so nothing has to be set again
        static PowerStore ImportLlt()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LenovoLegionToolkit");
                var gm = Path.Combine(dir, "godmode.json");
                if (!File.Exists(gm)) return null;
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.DeserializeObject(File.ReadAllText(gm)) as Dictionary<string, object>;
                var presets = root["Presets"] as Dictionary<string, object>;
                var store = new PowerStore();
                Func<Dictionary<string, object>, string, int, int> val = (p, k, d) =>
                {
                    var o = p.ContainsKey(k) ? p[k] as Dictionary<string, object> : null;
                    return o != null && o.ContainsKey("Value") ? Convert.ToInt32(o["Value"]) : d;
                };
                foreach (var kv in presets.OrderBy(k => (k.Value as Dictionary<string, object>)["Name"] as string))
                {
                    var p = kv.Value as Dictionary<string, object>;
                    var pr = new PowerPreset
                    {
                        Name = p["Name"] as string,
                        CpuLong = val(p, "CPULongTermPowerLimit", 50), CpuShort = val(p, "CPUShortTermPowerLimit", 50), CpuPeak = val(p, "CPUPeakPowerLimit", 50),
                        CpuCross = val(p, "CPUCrossLoadingPowerLimit", 50), CpuTemp = val(p, "CPUTemperatureLimit", 85),
                        GpuBoost = val(p, "GPUPowerBoost", 15), GpuTgp = val(p, "GPUConfigurableTGP", 100), GpuTemp = val(p, "GPUTemperatureLimit", 87),
                        GpuTotal = val(p, "GPUTotalProcessingPowerTargetOnAcOffsetFromBaseline", 100), GpuToCpu = val(p, "GPUToCPUDynamicBoost", 0),
                        FanMax = p.ContainsKey("FanFullSpeed") && p["FanFullSpeed"] is bool && (bool)p["FanFullSpeed"],
                    };
                    var ft = p.ContainsKey("FanTable") ? p["FanTable"] as Dictionary<string, object> : null;
                    if (ft != null) pr.Fan = Enumerable.Range(0, 10).Select(i => Convert.ToInt32(ft["FSS" + i])).ToArray();
                    var ov = p.ContainsKey("Overrides") ? p["Overrides"] as Dictionary<string, object> : null;
                    if (ov != null && ov.ContainsKey("PowerPlan")) pr.Plan = ov["PowerPlan"] as string ?? "";
                    store.Presets.Add(pr);
                    if (kv.Key == root["ActivePresetId"] as string) store.Active = pr.Name;
                }
                var st = Path.Combine(dir, "settings.json");
                if (File.Exists(st))
                {
                    var s = js.DeserializeObject(File.ReadAllText(st)) as Dictionary<string, object>;
                    var plans = s != null && s.ContainsKey("PowerPlans") ? s["PowerPlans"] as Dictionary<string, object> : null;
                    if (plans != null)
                    {
                        Func<string, string> pl = k => plans.ContainsKey(k) ? plans[k] as string ?? "" : "";
                        store.PlanQuiet = pl("Quiet"); store.PlanBalance = pl("Balance"); store.PlanPerformance = pl("Performance"); store.PlanExtreme = pl("Extreme");
                    }
                }
                Store.Log("питание: импортировано из LLT " + store.Presets.Count + " пресетов");
                return store.Presets.Count > 0 ? store : null;
            }
            catch (Exception ex) { Store.Log("импорт LLT: " + ex.Message); return null; }
        }

        static PowerStore FromFirmware()
        {
            Func<uint, int, int> f = (id, d) => { var v = Lenovo.GetFeature(id); return v == int.MinValue || v <= 0 && d > 0 ? d : v; };
            var p = new PowerPreset
            {
                Name = L.T("01 Мой"), CpuLong = f(Lenovo.CpuLong, 50), CpuShort = f(Lenovo.CpuShort, 50), CpuPeak = f(Lenovo.CpuPeak, 50), CpuCross = f(Lenovo.CpuCross, 50),
                CpuTemp = f(Lenovo.CpuTempLimit, 85), GpuBoost = f(Lenovo.GpuBoost, 15), GpuTgp = f(Lenovo.GpuTgp, 100), GpuTemp = f(Lenovo.GpuTempLimit, 87),
                GpuTotal = f(Lenovo.GpuTotal, 100), GpuToCpu = Math.Max(0, Lenovo.GetFeature(Lenovo.GpuToCpu)),
            };
            return new PowerStore { Active = p.Name, Presets = { p } };
        }

        void WantGpu(string name, bool atStart = false)
        {
            if (string.IsNullOrEmpty(name)) return;
            var h = GpuWanted; if (h != null) ui.Post(_ => h(name, atStart), null);
        }

        public string ModeGpu(int m) { return m == Lenovo.Quiet ? Data.GpuQuiet : m == Lenovo.Balance ? Data.GpuBalance : m == Lenovo.Performance ? Data.GpuPerformance : m == Lenovo.Custom && Active != null ? Active.GpuPreset : ""; }

        void WantCurve(string name, bool atStart = false)
        {
            if (string.IsNullOrEmpty(name)) return;
            var h = CurveWanted; if (h != null) ui.Post(_ => h(name, atStart), null);
        }

        public string ModeCurve(int m) { return m == Lenovo.Quiet ? Data.CurveQuiet : m == Lenovo.Balance ? Data.CurveBalance : m == Lenovo.Performance ? Data.CurvePerformance : m == Lenovo.Custom && Active != null ? Active.Curve : ""; }

        void Post(Action a) { ui.Post(_ => { a(); var h = Changed; if (h != null) h(); }, null); }

        // writes the preset into the firmware (only meaningful in the custom mode) and switches the Windows plan
        void Write(PowerPreset p)
        {
            Lenovo.SetFeature(Lenovo.CpuLong, p.CpuLong);
            Lenovo.SetFeature(Lenovo.CpuShort, p.CpuShort);
            Lenovo.SetFeature(Lenovo.CpuPeak, p.CpuPeak);
            Lenovo.SetFeature(Lenovo.CpuCross, p.CpuCross);
            Lenovo.SetFeature(Lenovo.CpuTempLimit, p.CpuTemp);
            foreach (var kv in new[] { Tuple.Create(Lenovo.GpuBoost, p.GpuBoost), Tuple.Create(Lenovo.GpuTgp, p.GpuTgp), Tuple.Create(Lenovo.GpuTempLimit, p.GpuTemp),
                                       Tuple.Create(Lenovo.GpuTotal, p.GpuTotal), Tuple.Create(Lenovo.GpuToCpu, p.GpuToCpu) })
                try { Lenovo.SetFeature(kv.Item1, kv.Item2); } catch (Exception ex) { Store.Log("видеокарта 0x" + kv.Item1.ToString("X8") + ": " + ex.Message); }
            Lenovo.SetFeature(Lenovo.FanFullSpeed, p.FanMax ? 1 : 0);
            if (!p.FanMax)
            {
                var fan = p.Fan.Select((v, i) => Math.Max(Lenovo.MinimumFanTable[i], Math.Min(10, v))).ToArray();
                Lenovo.SetFanTable(fan);
            }
            WriteOc(p);
            Guid plan;
            if (Guid.TryParse(p.Plan, out plan)) WinPower.SetActive(plan);
        }

        // advanced overclocking: written only when the preset has it on, or to switch it off after we switched it on
        void WriteOc(PowerPreset p)
        {
            try
            {
                if (!p.OcOn && !Data.OcWritten) return;
                if (!Lenovo.BiosOcOn) return;
                if (p.OcOn)
                {
                    Lenovo.SetFeature(Lenovo.CpuOcEnable, 1);
                    Lenovo.SetOcData(Lenovo.PboScalar, Math.Max(1, Math.Min(10, p.PboScalar)));
                    Lenovo.SetOcData(Lenovo.PboBoost, Math.Max(0, Math.Min(200, p.PboBoost)));
                    if (p.AllCoreOn) Lenovo.SetOcData(Lenovo.AllCoreCurve, Math.Max(0, Math.Min(20, p.AllCore)));
                    Store.Log("разгон: PBO x" + p.PboScalar + ", +" + p.PboBoost + " МГц" + (p.AllCoreOn ? ", кривая всех ядер " + p.AllCore : ""));
                    if (!Data.OcWritten) { Data.OcWritten = true; Save(); }
                }
                else
                {
                    Lenovo.SetFeature(Lenovo.CpuOcEnable, 0);
                    Data.OcWritten = false; Save();
                    Store.Log("разгон: выключен");
                }
            }
            catch (Exception ex) { Store.Log("разгон: " + ex.Message); }
        }

        // makes it the active preset, switches to the custom mode if needed and writes it
        public void Apply(PowerPreset p, Action<bool> done = null)
        {
            Data.Active = p.Name; Save();
            var copy = p.Clone();
            Task.Run(() =>
            {
                string err = null;
                try
                {
                    // the mode is set here and its preset written right below: ModeNow must not do it a second time
                    if (Lenovo.GetPowerMode() != Lenovo.Custom) { Lenovo.SetPowerMode(Lenovo.Custom); Thread.Sleep(500); lock (modeGate) lastMode = Lenovo.Custom; }
                    Write(copy);
                    int got = Lenovo.GetFeature(Lenovo.CpuLong);
                    if (got != copy.CpuLong) err = "прошивка вернула " + got + " W вместо " + copy.CpuLong;
                    Store.Log("питание: пресет «" + copy.Name + "» " + (err ?? "ok"));
                    if (err == null) { WantCurve(copy.Curve); WantGpu(copy.GpuPreset); }
                }
                catch (Exception ex) { err = ex.Message; Store.Log("питание: " + err); }
                Post(() => { LastError = err; if (done != null) done(err == null); });
            });
        }

        // ---------- power mode changes (Fn+Q, the home page, other programs) ----------

        public void Start()
        {
            try
            {
                watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new WqlEventQuery("SELECT * FROM LENOVO_GAMEZONE_SMART_FAN_MODE_EVENT"));
                watcher.EventArrived += (s, e) => Task.Run(() => OnMode());
                watcher.Start();
            }
            catch (Exception ex) { Store.Log("событие режима: " + ex.Message); }
            Task.Run(() => OnMode());
        }

        int lastMode = -1;
        public int Mode { get { return lastMode; } }
        readonly object modeGate = new object();

        // 16.11: the firmware raises its event for Fn+Q, but not when a program sets the mode (LLT, too, tells its own
        // listener by hand): a mode picked on «Главная» changed the mode and nothing else, so its plan, curve and
        // graphics card preset were not applied (the card kept the custom mode's preset in «Тихий»). Whoever sets the
        // mode in this app calls this; no notification is shown, the button itself was just pressed.
        public void Notify() { Task.Run(() => OnMode(false)); }

        void OnMode(bool announce = true)
        {
            try
            {
                Thread.Sleep(300);
                lock (modeGate) ModeNow(announce); // the event and Notify may come together: one of them does the work
            }
            catch (Exception ex) { Store.Log("смена режима: " + ex.Message); }
        }

        // 21.11 (the user's wish): the firmware's Fn+Q goes round Тихий → Баланс → Макс (→ Экстремальный) and back, «Свой»
        // is not in it (six presses at 00:38 never reached it). Where this is on, the step from the last mode back to
        // «Тихий» becomes «Свой» instead: Тихий → Баланс → Макс → Свой → Тихий. Only for the firmware's own event (Fn+Q):
        // a mode set by a program, this one's home page included, raises none, and Notify passes announce = false.
        public static Func<bool> FnQCustom = () => false;

        static string ModeLog(int m) { return m == Lenovo.Quiet ? "«Тихий»" : m == Lenovo.Balance ? "«Баланс»" : m == Lenovo.Performance ? "«Макс»" : m == Lenovo.Extreme ? "«Экстрим»" : m == Lenovo.Custom ? "«Свой»" : m.ToString(); }

        void ModeNow(bool announce)
        {
            {
                int m = Lenovo.GetPowerMode();
                if (m == lastMode) return;
                if (announce && lastMode != -1) Store.Log("Fn+Q: прошивка " + ModeLog(lastMode) + " → " + ModeLog(m));
                // 23.11 (the user: «кнопкой Fn+Q режим тихий не применяется»): from «Свой» the firmware's next step is not «Тихий»
                // (the log of 16:39: three presses from «Свой» to «Свой», Тихий never reached), so the circle closes here too:
                // the press after «Свой» gives «Тихий», whatever the firmware picked (it goes to «Баланс»). 23.11 later (the user):
                // also with «Fn+Q включает «Свой»» off — that switch only decides whether «Свой» is in the circle
                if (announce && lastMode == Lenovo.Custom && m != Lenovo.Quiet && m != Lenovo.Custom)
                {
                    try
                    {
                        Lenovo.SetPowerMode(Lenovo.Quiet);
                        Thread.Sleep(150);
                        int now = Lenovo.GetPowerMode();
                        if (now == Lenovo.Quiet) { Store.Log("Fn+Q: «Тихий» после «Своего» вместо " + ModeLog(m)); m = now; }
                        else Store.Log("Fn+Q: «Тихий» не встал, режим " + now);
                    }
                    catch (Exception ex) { Store.Log("Fn+Q: «Тихий» не включился: " + ex.Message); }
                }
                else if (announce && m == Lenovo.Quiet && (lastMode == Lenovo.Performance || lastMode == Lenovo.Extreme) && FnQCustom())
                {
                    try
                    {
                        Lenovo.SetPowerMode(Lenovo.Custom);
                        Thread.Sleep(150);
                        int now = Lenovo.GetPowerMode();
                        if (now == Lenovo.Custom) { m = now; Store.Log("Fn+Q: «Свой» вместо «Тихого» (круг через «Свой»)"); }
                        else Store.Log("Fn+Q: «Свой» не встал, режим " + now);
                    }
                    catch (Exception ex) { Store.Log("Fn+Q: «Свой» не включился: " + ex.Message); }
                }
                bool first = lastMode == -1;
                lastMode = m;
                if (!first && announce) { var h = ModeChanged; if (h != null) ui.Post(_ => h(m), null); }
                if (m == Lenovo.Custom)
                {
                    var p = Active;
                    if (p != null && !(first && Safety.SafeStart)) { Write(p.Clone()); Store.Log("режим «Свой»: пресет «" + p.Name + "»"); WantCurve(p.Curve, first); WantGpu(p.GpuPreset, first); } // safe start: not at the start itself
                }
                else
                {
                    var plan = m == Lenovo.Quiet ? Data.PlanQuiet : m == Lenovo.Balance ? Data.PlanBalance : m == Lenovo.Performance ? Data.PlanPerformance : m == Lenovo.Extreme ? Data.PlanExtreme : "";
                    Guid g;
                    if (Guid.TryParse(plan, out g)) WinPower.SetActive(g);
                    WantCurve(ModeCurve(m), first); WantGpu(ModeGpu(m), first);
                }
                Post(() => { });
            }
        }

        // ---------- LLT: it writes the same settings ----------

        public static bool LltRunning { get { return Process.GetProcessesByName("Lenovo Legion Toolkit").Length > 0; } }

        public void Dispose() { if (watcher != null) try { watcher.Stop(); watcher.Dispose(); } catch { } }
    }
}
