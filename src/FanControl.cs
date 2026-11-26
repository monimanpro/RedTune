using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RedTune
{
    // Fan curves driven by the app. Twice a second the CPU and GPU temperatures go through the preset's curve
    // (11 points, 0..100 °C → % of the fan's top speed) and the speed is given to the firmware
    // (GetFeatureValue / SetFeatureValue 0x04030001 CPU fan, 0x04030002 GPU fan; 0 = the firmware drives the fan again).
    // Checked on the R7000P on 2026-11-12: 2400 → 3500 RPM in ~5 s, back to auto after writing 0. Only in the custom
    // mode: the firmware ignores the speed elsewhere.
    //
    // 2026-11-19: written anew. The first version followed the CustomFanCurve plugin of LenovoLegionToolkit-Plugins
    // step by step; this one is our own design. What the user sees stayed: the eight extras on the page and the numbers
    // in their descriptions. How they are worked out is described at FanBrain. The split in two is ours as well:
    // FanBrain decides and touches nothing, FanControl reads the firmware and gives it the brain's answers —
    // so the brain can be run on made-up temperatures (--fan-check) with no firmware near.

    // which extras are on: a copy of the settings for one step
    struct FanOptions
    {
        public bool Floor, Emergency, Band, Average, Glide, Kick, Pair, Ahead;

        public static FanOptions From(Settings s)
        {
            return new FanOptions { Floor = s.FanSafety, Emergency = s.FanCritical, Band = s.FanHyst, Average = s.FanEma, Glide = s.FanGlide, Kick = s.FanBoost, Pair = s.FanSync, Ahead = s.FanPredict };
        }
    }

    // The deciding part. One Step per reading: temperatures and the fans' real speeds in, for each fan an order out
    // (−1 say nothing, 0 hand the fan to the firmware, otherwise RPM). Time comes from outside, in seconds.
    //
    // A fan goes through three stages:
    //   1. the temperature it answers to — its own sensor (the other one when its own gives nothing), then, if on:
    //      «Сглаживание» the mean of the last 3 s; «Упреждение» a straight line fitted to the last 2 s, and when it
    //      climbs faster than 5 °C/s the temperature it will reach in 2 s (no more than 20 °C ahead);
    //      «Гистерезис» a 3 °C slack: up at once, down only by what the temperature fell beyond the slack;
    //   2. the speed the curve asks for at that temperature; «Страховка» lifts it to a floor that grows from 75 °C
    //      to 80 % at 90 °C by the hotter sensor; «Синхронизация» gives both fans the higher of the two;
    //      «Аварийный режим» is a latch: on from 95 °C, off under 90 °C, both fans at the top while it is on;
    //   3. the way there — up at once; down at once, or with «Плавный спад» by 400 RPM a second (a fan taken from
    //      the firmware while it turns faster starts from its real speed). Under the fan's minimum the curve means
    //      «the firmware's own table»: the fan goes to its minimum, and when the curve has asked for less for 3 s in
    //      a row it is handed over — so a temperature sitting on the line does not flip the fan between two masters.
    //      «Разгон при старте» holds 3000 RPM for 0.6 s when a fan that really stands still is started lower.
    // An order is given when it differs from the last by 60 RPM or more, and repeated every 2 s while the app drives.
    // With no temperature at all the last orders are repeated for 5 s, then Blind is set: the fans must go back to
    // the firmware, which has sensors of its own.
    sealed class FanBrain
    {
        public const double WindowS = 3, SlopeS = 2, RiseRate = 5, AheadS = 2, AheadMax = 20, BandC = 3;
        public const double FloorFrom = 75, FloorTo = 90, FloorTop = 80, PanicOn = 95, PanicOff = 90;
        public const double FallRate = 400, KickS = 0.6, LeaveS = 3, RefreshS = 2, BlindS = 5;
        public const int KickRpm = 3000, Standing = 500, Notch = 60;

        public int MaxRpm = 5100, MinRpm = 1700;
        public bool Panic { get; private set; }
        public bool Blind { get; private set; }
        public readonly int[] Target = new int[2]; // where each fan is being led (0 = the firmware)

        sealed class Chan
        {
            public readonly List<double> At = new List<double>(), Deg = new List<double>(); // the last readings
            public double Held = double.NaN;  // «Гистерезис»: the temperature the fan is kept at
            public double Set;                // the speed the fan is led to; 0 = the firmware has it
            public double LowSince = -1;      // since when the fan sits at its minimum with the curve asking for less
            public int Sent = -1;             // the last order, −1 = none yet
            public double SentAt, KickTill = -1;

            public void Forget() { At.Clear(); Deg.Clear(); Held = double.NaN; }
        }

        readonly Chan[] ch = { new Chan(), new Chan() };
        double last = -1, blindSince = -1;
        bool shared;

        public void Reset()
        {
            ch[0] = new Chan(); ch[1] = new Chan();
            last = -1; blindSince = -1; Panic = false; Blind = false; Target[0] = Target[1] = 0;
        }

        // the order was not taken: say it again at the next step
        public void Unsaid(int fan) { ch[fan].Sent = -1; }

        public int[] Step(double now, int cpu, int gpu, int[] rpm, int[] curveCpu, int[] curveGpu, bool oneCurve, int source, FanOptions o)
        {
            var order = new[] { -1, -1 };
            double dt = last < 0 ? 0 : Math.Max(0, Math.Min(now - last, 2));
            last = now;

            int hot = Math.Max(cpu, gpu);
            if (hot <= 0)
            {
                if (blindSince < 0) blindSince = now;
                Blind = now - blindSince >= BlindS;
                if (!Blind)
                    for (int i = 0; i < 2; i++)
                        if (ch[i].Sent > 0 && now - ch[i].SentAt >= RefreshS) { order[i] = ch[i].Sent; ch[i].SentAt = now; }
                return order;
            }
            blindSince = -1; Blind = false;

            if (!o.Emergency) Panic = false;
            else if (hot >= PanicOn) Panic = true;
            else if (hot < PanicOff) Panic = false;

            // one curve ↔ two: the readings were of another temperature, begin them afresh (the fans stay where they are)
            if (oneCurve != shared) { shared = oneCurve; ch[0].Forget(); ch[1].Forget(); }

            var want = new double[2];
            if (oneCurve)
            {
                int t = FanControl.SharedTemp(source, cpu, gpu);
                want[0] = want[1] = Want(ch[0], now, t > 0 ? t : hot, hot, curveCpu, o);
            }
            else
            {
                want[0] = Want(ch[0], now, cpu > 0 ? cpu : hot, hot, curveCpu, o);
                want[1] = Want(ch[1], now, gpu > 0 ? gpu : hot, hot, curveGpu, o);
                if (o.Pair) want[0] = want[1] = Math.Max(want[0], want[1]);
            }
            if (Panic) want[0] = want[1] = MaxRpm;

            for (int i = 0; i < 2; i++)
            {
                var c = ch[i];
                double w = want[i];
                int turns = rpm != null && rpm.Length > i ? rpm[i] : 0;
                if (w >= MinRpm)
                {
                    c.LowSince = -1;
                    if (c.Set <= 0)
                    {
                        // taking the fan from the firmware
                        c.Set = o.Glide ? Math.Max(w, Math.Min(turns, MaxRpm)) : w;
                        if (o.Kick && !Panic && w < KickRpm && turns < Standing) c.KickTill = now + KickS;
                    }
                    else if (w >= c.Set || !o.Glide) c.Set = w;
                    else c.Set = Math.Max(w, c.Set - FallRate * dt);
                }
                else if (c.Set > 0)
                {
                    c.Set = o.Glide && c.Set > MinRpm ? Math.Max(MinRpm, c.Set - FallRate * dt) : MinRpm;
                    if (c.Set <= MinRpm)
                    {
                        if (c.LowSince < 0) c.LowSince = now;
                        else if (now - c.LowSince >= LeaveS) { c.Set = 0; c.LowSince = -1; }
                    }
                }

                int say = c.Set <= 0 ? 0 : Math.Max(MinRpm, Math.Min(MaxRpm, (int)Math.Round(c.Set)));
                Target[i] = say;
                if (say > 0 && c.KickTill >= 0)
                {
                    if (now < c.KickTill) say = Math.Min(MaxRpm, Math.Max(say, KickRpm));
                    else c.KickTill = -1;
                }
                else c.KickTill = -1;

                bool give;
                if (c.Sent < 0) give = true;                       // the first word: also «0», so nothing stale stays in the firmware
                else if (say == 0 || c.Sent == 0) give = say != c.Sent;
                else give = Math.Abs(say - c.Sent) >= Notch || now - c.SentAt >= RefreshS;
                if (give) { order[i] = say; c.Sent = say; c.SentAt = now; }
            }
            return order;
        }

        // stages 1 and 2 for one fan: the speed its curve asks for now
        double Want(Chan c, double now, double t, double hot, int[] curve, FanOptions o)
        {
            c.At.Add(now); c.Deg.Add(t);
            while (c.At.Count > 1 && now - c.At[0] > WindowS) { c.At.RemoveAt(0); c.Deg.RemoveAt(0); }

            double eff = o.Average ? c.Deg.Average() : t;
            if (o.Ahead)
            {
                double climb = Slope(c, now);
                if (climb > RiseRate) eff = Math.Max(eff, t + Math.Min(climb * AheadS, AheadMax));
            }
            if (o.Band)
            {
                if (double.IsNaN(c.Held) || eff > c.Held) c.Held = eff;
                else if (eff < c.Held - BandC) c.Held = eff + BandC;
                eff = c.Held;
            }
            else c.Held = double.NaN;

            double w = FanControl.Percent(curve, (float)eff) / 100.0 * MaxRpm;
            if (o.Floor) w = Math.Max(w, FloorPercent(Math.Max(t, hot)) / 100.0 * MaxRpm);
            return Math.Min(w, MaxRpm);
        }

        // °C a second: a straight line through the readings of the last SlopeS seconds (least squares); 0 with too few of them
        static double Slope(Chan c, double now)
        {
            int from = 0;
            while (from < c.At.Count && now - c.At[from] > SlopeS) from++;
            int n = c.At.Count - from;
            if (n < 3 || c.At[c.At.Count - 1] - c.At[from] < 0.9) return 0;
            double mt = 0, md = 0;
            for (int k = from; k < c.At.Count; k++) { mt += c.At[k]; md += c.Deg[k]; }
            mt /= n; md /= n;
            double up = 0, down = 0;
            for (int k = from; k < c.At.Count; k++) { up += (c.At[k] - mt) * (c.Deg[k] - md); down += (c.At[k] - mt) * (c.At[k] - mt); }
            return down > 0 ? up / down : 0;
        }

        public static double FloorPercent(double t)
        {
            if (t <= FloorFrom) return 0;
            return t >= FloorTo ? FloorTop : (t - FloorFrom) / (FloorTo - FloorFrom) * FloorTop;
        }
    }

    sealed class FanControl : IDisposable
    {
        public const uint CpuFanId = 0x04030001, GpuFanId = 0x04030002;
        static readonly uint[] Ids = { CpuFanId, GpuFanId };
        public static readonly int[] DefaultCurve = { 0, 0, 0, 0, 0, 35, 42, 50, 65, 85, 100 }; // % at 0,10..100 °C; under the minimum = the firmware's own table
        const int EveryMs = 500;
        // 27.11: the firmware says 1700, but the fans of the R7000P hold any speed the app gives down to ~800 RPM
        // (probe: 1400, 1100, 800 held steady on both fans; at 500 the motor stalls to 0 and restarts in a loop).
        // An expert switch lets the curve go down to 1000, with room above where the motor gives up.
        public const int LowMinRpm = 1000;

        public int MaxRpm = 5100, FirmwareMin = 1700;
        // the lowest speed the app gives: the firmware's, or LowMinRpm with the expert switch (never above the firmware's)
        public int MinRpm { get { return s.Expert && s.FanLowMin ? Math.Min(FirmwareMin, LowMinRpm) : FirmwareMin; } }
        readonly Func<PowerPreset> presetNow;  // the active preset and the firmware's mode: PowerService's, or the live test's own
        readonly Func<int> modeNow;
        readonly Settings s;
        readonly FanBrain brain = new FanBrain();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly object gate = new object();
        Thread thread;
        volatile bool stop, suspended;
        bool panicSaid;
        long failSaid = -1;

        // what the page shows
        public volatile bool Active;
        public int CpuTemp = -1, GpuTemp = -1;
        public readonly int[] Rpm = new int[2], Target = new int[2];
        public event Action Updated; // from the worker thread
        // 20.11: «Питание» is open and shows the live temperatures and speeds. While the curve is off and nobody looks,
        // the loop reads nothing: 4 firmware reads every half second cost ~3 % of a core in Windows' WMI service for
        // numbers nobody used. The fans themselves are the firmware's then; the curve, once on, is read as before.
        public volatile bool Watched;
        public event Action<int, int> Sent; // a speed the firmware took: fan (0 CPU, 1 GPU), RPM; from the worker thread

        static FanControl current;

        public FanControl(PowerService power, Settings s) : this(() => power.Active, () => power.Mode, s) { }

        FanControl(Func<PowerPreset> presetNow, Func<int> modeNow, Settings s)
        {
            this.presetNow = presetNow; this.modeNow = modeNow; this.s = s;
            current = this;
        }

        public void Start()
        {
            ReadLimits();
            SystemEvents.PowerModeChanged += OnPowerMode;
            SystemEvents.SessionEnding += OnSessionEnding;
            thread = new Thread(Loop) { IsBackground = true, Name = "fans" };
            thread.Start();
        }

        // the fans' own limits from LENOVO_FAN_TEST_DATA (here 1700..5100 for both fans)
        void ReadLimits()
        {
            int max = MaxRpm, min = FirmwareMin;
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_FAN_TEST_DATA"))
                    foreach (ManagementObject o in q.Get())
                    {
                        var tops = o["FanMaxSpeed"] as uint[]; var lows = o["FanMinSpeed"] as uint[];
                        if (tops != null && tops.Length > 0 && tops.Min() > 0) max = (int)tops.Min();
                        if (lows != null && lows.Length > 0 && lows.Max() > 0) min = (int)lows.Max();
                    }
            }
            catch (Exception ex) { Store.Log("вентиляторы: нет LENOVO_FAN_TEST_DATA (" + ex.Message + "), беру 1700..5100"); }
            if (min >= max) { Store.Log("вентиляторы: пределы из прошивки не годятся (" + min + ".." + max + "), беру 1700..5100"); min = 1700; max = 5100; }
            MaxRpm = brain.MaxRpm = max; FirmwareMin = min; brain.MinRpm = MinRpm;
        }

        void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) { suspended = true; Release("сон"); }
            else if (e.Mode == PowerModes.Resume) suspended = false;
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e) { stop = true; Release("выход из Windows"); }

        // the firmware drives the fans again; also called on crashes (Program) so a fan is never left on a low RPM
        public static void ReleaseAll() { var c = current; if (c != null) c.Release("аварийно"); }

        void Release(string why)
        {
            // a crash may come while the worker holds the gate: do not wait for it for ever
            bool mine = Monitor.TryEnter(gate, 1500);
            try
            {
                if (!Active) return;
                Active = false;
                foreach (var id in Ids) try { Lenovo.SetFeature(id, 0); } catch (Exception ex) { Store.Quiet("вентиляторы: отдать прошивке", ex); }
                brain.Reset(); Target[0] = Target[1] = 0; panicSaid = false;
                Store.Log("вентиляторы: управление отдано прошивке (" + why + ")");
            }
            finally { if (mine) Monitor.Exit(gate); }
        }

        void Loop()
        {
            while (!stop)
            {
                try { Cycle(); } catch (Exception ex) { Store.Log("вентиляторы: " + ex.Message); }
                var h = Updated; if (h != null) try { h(); } catch { }
                Thread.Sleep(EveryMs);
            }
        }

        static int Temp(uint id) { var v = Lenovo.GetFeature(id); return v == int.MinValue || v < 0 || v > 150 ? 0 : v; }

        void Cycle()
        {
            var p = presetNow();
            string off = suspended || stop ? "пауза" : p == null || !p.FanCurveOn ? "кривая выключена" : p.FanMax ? "полная скорость" : modeNow() != Lenovo.Custom ? "не «Свой» режим" : null;
            if (off != null && !Active && !Watched) return; // the firmware drives the fans and nobody shows the numbers

            int cpu = Temp(Lenovo.CpuTemp), gpu = Temp(Lenovo.GpuTemp);
            CpuTemp = cpu; GpuTemp = gpu;
            for (int i = 0; i < 2; i++) { var r = Lenovo.GetFeature(Ids[i]); Rpm[i] = r == int.MinValue ? 0 : r; }
            lock (gate)
            {
                if (off != null) { Release(off); return; }
                if (!Active)
                {
                    if (cpu <= 0 && gpu <= 0) return; // nothing to go by: the firmware keeps the fans
                    Active = true; brain.Reset();
                    Store.Log("вентиляторы: своя кривая (пресет «" + p.Name + "»)");
                }

                brain.MinRpm = MinRpm; // the expert switch acts at once
                var order = p.FanShared
                    ? brain.Step(clock.Elapsed.TotalSeconds, cpu, gpu, Rpm, p.FanBoth ?? DefaultCurve, p.FanBoth ?? DefaultCurve, true, p.FanSource, FanOptions.From(s))
                    : brain.Step(clock.Elapsed.TotalSeconds, cpu, gpu, Rpm, p.FanCpu ?? DefaultCurve, p.FanGpu ?? DefaultCurve, false, 0, FanOptions.From(s));
                if (brain.Blind) { Release("нет показаний температуры"); return; }
                if (brain.Panic != panicSaid)
                {
                    panicSaid = brain.Panic;
                    Store.Log(panicSaid ? "вентиляторы: аварийный режим, оба на максимум (ЦП " + cpu + " °C, ГП " + gpu + " °C)" : "вентиляторы: аварийный режим снят");
                }
                for (int i = 0; i < 2; i++)
                {
                    Target[i] = brain.Target[i];
                    if (order[i] >= 0 && !Give(i, order[i])) brain.Unsaid(i);
                }
            }
        }

        bool Give(int i, int rpm)
        {
            try
            {
                Lenovo.SetFeature(Ids[i], rpm);
                var h = Sent; if (h != null) try { h(i, rpm); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                long sec = clock.ElapsedMilliseconds / 1000;
                if (failSaid < 0 || sec - failSaid >= 30) { failSaid = sec; Store.Log("вентиляторы: запись " + rpm + " об/мин: " + ex.Message); }
                return false;
            }
        }

        // the temperature of the shared curve: 0 CPU, 1 GPU, 2 the hotter of the two
        public static int SharedTemp(int source, int cpu, int gpu) { return source == 0 ? cpu : source == 1 ? gpu : Math.Max(cpu, gpu); }

        // the curve between its 11 points (0, 10 .. 100 °C): straight lines
        public static double Percent(int[] curve, float t)
        {
            if (curve == null || curve.Length < 11) curve = DefaultCurve;
            if (!(t > 0)) return curve[0];
            if (t >= 100) return curve[10];
            int left = (int)Math.Floor(t / 10.0);
            double part = t / 10.0 - left;
            return curve[left] * (1 - part) + curve[left + 1] * part;
        }

        public void Dispose()
        {
            stop = true;
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.SessionEnding -= OnSessionEnding;
            if (thread != null) thread.Join(2000);
            Release("выход");
        }

        // ---------- --fan-live-test <file> [seconds of load]: THE REAL FANS. Writes speeds to the firmware. ----------
        // Only with the user's word. A FanControl of its own with a preset that lives in this run only (nothing is saved,
        // the user's presets and the running app are not touched; the running app must not drive the fans itself).
        // Stages: the firmware as it is · a flat curve of 50 % · of 70 % · back to 50 % with «Плавный спад» · a curve of
        // zeros (the minimum, 3 s, the firmware) · the default curve under a load on every core, then cooling · release.
        // Whatever happens, the fans are given back to the firmware at the end.
        public static string LiveTest(int loadS)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            int bad = 0, all = 0;
            Action<bool, string> line = (ok, text) => { all++; if (!ok) bad++; sb.AppendLine((ok ? "ok    " : "FAIL  ") + text); };
            int mode = Lenovo.GetPowerMode();
            if (mode != Lenovo.Custom) return "the laptop is not in the custom mode (mode " + mode + "): the firmware would ignore the speeds; nothing was written\r\n";

            var s = new Settings();
            s.FanSafety = true; s.FanCritical = true; s.FanHyst = s.FanEma = s.FanGlide = s.FanBoost = s.FanSync = s.FanPredict = false;
            var preset = new PowerPreset { Name = "проверка вентиляторов" };
            preset.FanCurveOn = false; preset.FanMax = false; preset.FanShared = false;
            var watch = Stopwatch.StartNew();
            var given = new[] { new List<KeyValuePair<double, int>>(), new List<KeyValuePair<double, int>>() };
            var rows = new StringBuilder();
            var fans = new FanControl(() => preset, () => mode, s);
            fans.Sent += (i, v) => { lock (given) given[i].Add(new KeyValuePair<double, int>(watch.Elapsed.TotalSeconds, v)); };

            Func<int, int[]> flat = pct => Enumerable.Repeat(pct, 11).ToArray();
            // look for some seconds, a row every half second: ЦП °C, ГП °C, the fans' real RPM, where the app leads them
            Func<string, double, List<int[]>> look = (stage, seconds) =>
            {
                var got = new List<int[]>();
                double end = watch.Elapsed.TotalSeconds + seconds;
                while (watch.Elapsed.TotalSeconds < end)
                {
                    Thread.Sleep(500);
                    var r = new[] { fans.CpuTemp, fans.GpuTemp, fans.Rpm[0], fans.Rpm[1], fans.Target[0], fans.Target[1] };
                    got.Add(r);
                    rows.AppendLine(string.Format(inv, "{0,6:0.0}  {1,-9} {2,3} {3,3}   {4,5} {5,5}   {6,5} {7,5}   {8}", watch.Elapsed.TotalSeconds, stage, r[0], r[1], r[2], r[3], r[4], r[5], fans.Active ? "app" : "firmware"));
                }
                return got;
            };
            Func<int, double, List<KeyValuePair<double, int>>> since = (fan, from) => { lock (given) return given[fan].Where(g => g.Key >= from).ToList(); };
            Func<List<KeyValuePair<double, int>>, string> show = l => string.Join(" ", l.Select(g => g.Value));
            // the last two seconds of a stage: the fan turns within a tenth of the speed it was given
            Func<List<int[]>, int, int, bool> near = (got, fan, goal) => got.Count >= 4 && got.Skip(got.Count - 4).All(r => Math.Abs(r[2 + fan] - goal) <= goal / 10);
            Func<List<int[]>, int, string> ends = (got, fan) => got.Count > 0 ? got[got.Count - 1][2 + fan].ToString() : "?";
            Func<List<KeyValuePair<double, int>>, double> steepest = l =>
            {
                double worst = 0;
                for (int k = 1; k < l.Count; k++)
                    if (l[k].Value > 0 && l[k - 1].Value > 0 && l[k].Key > l[k - 1].Key) worst = Math.Max(worst, (l[k - 1].Value - l[k].Value) / (l[k].Key - l[k - 1].Key));
                return worst;
            };

            var burn = new[] { false };
            var heaters = new List<Thread>();
            int max = 5100, min = 1700;
            try
            {
                fans.Start();
                max = fans.MaxRpm; min = fans.MinRpm;
                int r50 = (int)Math.Round(0.5 * max), r70 = (int)Math.Round(0.7 * max);
                sb.AppendLine("the fan curves on the real fans; the firmware says they do " + min + ".." + max + " RPM");
                sb.AppendLine();

                var before = look("firmware", 4);
                bool quiet; lock (given) quiet = given[0].Count == 0 && given[1].Count == 0;
                line(quiet && !fans.Active, "the curve is off: nothing is written, the firmware turns the fans at " + ends(before, 0) + " and " + ends(before, 1) + " RPM (ЦП " + before[before.Count - 1][0] + " °C, ГП " + before[before.Count - 1][1] + " °C)");

                double t = watch.Elapsed.TotalSeconds;
                preset.FanCpu = flat(50); preset.FanGpu = flat(50); preset.FanCurveOn = true;
                var got = look("50 %", 12);
                var g0 = since(0, t); var g1 = since(1, t);
                line(g0.Count > 0 && g1.Count > 0 && g0[0].Value == r50 && g1[0].Value == r50 && g0.All(g => g.Value == r50), "a flat curve of 50 %: " + r50 + " RPM is given to both fans and said again every 2 s (" + show(g0) + ")");
                line(near(got, 0, r50) && near(got, 1, r50), "the fans come to it: " + ends(got, 0) + " and " + ends(got, 1) + " RPM after 12 s");

                t = watch.Elapsed.TotalSeconds;
                preset.FanCpu = flat(70); preset.FanGpu = flat(70);
                got = look("70 %", 12);
                g0 = since(0, t + 0.01);
                line(g0.Count > 0 && g0.Any(g => g.Value == r70) && g0[g0.Count - 1].Value == r70, "the curve raised to 70 %: " + r70 + " RPM is given (" + show(g0) + ")");
                line(near(got, 0, r70) && near(got, 1, r70), "the fans come to it: " + ends(got, 0) + " and " + ends(got, 1) + " RPM after 12 s");

                s.FanGlide = true;
                t = watch.Elapsed.TotalSeconds;
                preset.FanCpu = flat(50); preset.FanGpu = flat(50);
                got = look("glide", 9);
                g0 = since(0, t + 0.01).Where(g => g.Value != r70).ToList();
                bool down = true; for (int k = 1; k < g0.Count; k++) if (g0[k].Value > g0[k - 1].Value) down = false;
                line(g0.Count >= 4 && down && steepest(g0) <= FanBrain.FallRate * 1.15 && g0[g0.Count - 1].Value == r50, "back to 50 % with «Плавный спад»: down by " + steepest(g0).ToString("0", inv) + " RPM a second at the most (" + show(g0) + ")");
                line(near(got, 0, r50) && near(got, 1, r50), "the fans follow: " + ends(got, 0) + " and " + ends(got, 1) + " RPM");
                s.FanGlide = false;

                t = watch.Elapsed.TotalSeconds;
                preset.FanCpu = flat(0); preset.FanGpu = flat(0);
                got = look("zeros", 9);
                g0 = since(0, t + 0.01).Where(g => g.Value != r50).ToList();
                double minAt = g0.Count > 0 ? g0[0].Key : 0, zeroAt = g0.Count > 0 ? g0[g0.Count - 1].Key : 0;
                line(g0.Count >= 2 && g0[0].Value == min && g0[g0.Count - 1].Value == 0 && g0.All(g => g.Value == min || g.Value == 0) && zeroAt - minAt >= FanBrain.LeaveS - 0.2 && zeroAt - minAt <= FanBrain.LeaveS + 1.2,
                    "a curve of zeros (under the fans' minimum): the minimum first, the firmware " + (zeroAt - minAt).ToString("0.0", inv) + " s later (" + show(g0) + ")");
                line(fans.Target[0] == 0 && fans.Target[1] == 0, "the firmware has the fans again: they turn at " + ends(got, 0) + " and " + ends(got, 1) + " RPM");

                // the default curve under a real load
                t = watch.Elapsed.TotalSeconds;
                var cpuCurve = (int[])DefaultCurve.Clone();
                preset.FanCpu = cpuCurve; preset.FanGpu = (int[])DefaultCurve.Clone();
                burn[0] = true;
                for (int k = 0; k < Environment.ProcessorCount; k++)
                {
                    var th = new Thread(() => { double x = 1.1; while (burn[0]) for (int n = 0; n < 100000; n++) x = Math.Sqrt(x * 1.0000001 + 1.3); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    heaters.Add(th); th.Start();
                }
                var hotRows = look("load", loadS);
                burn[0] = false;
                var coolRows = look("cooling", 40);
                var both = hotRows.Concat(coolRows).ToList();
                // where the curve puts the processor's fan at a temperature (with the floor of «Страховка»): the lowest and the highest within ±2 °C
                int off = 0;
                foreach (var r in both)
                {
                    bool fits = false;
                    for (int d = -2; d <= 2 && !fits; d++)
                    {
                        double w = Math.Max(Percent(cpuCurve, r[0] + d), FanBrain.FloorPercent(Math.Max(r[0], r[1]) + d)) / 100.0 * max;
                        fits = w < min ? r[4] == 0 || r[4] == min : Math.Abs(r[4] - w) <= 80;
                    }
                    if (!fits) off++;
                }
                int peak = both.Max(r => r[0]), topTarget = both.Max(r => r[4]), topRpm = both.Max(r => r[2]);
                var lastHot = hotRows[hotRows.Count - 1]; var lastCool = coolRows[coolRows.Count - 1];
                line(off <= both.Count / 8, "the default curve, " + loadS + " s of load on every core and 40 s of cooling: the processor's fan is led by the curve at its temperature in " + (both.Count - off) + " of " + both.Count + " rows");
                List<KeyValuePair<double, int>> gAll; lock (given) gAll = given[0].Concat(given[1]).ToList();
                line(gAll.All(g => g.Value == 0 || (g.Value >= min && g.Value <= max)), "every speed given in the whole run is «0» or " + min + ".." + max + " RPM (" + gAll.Count + " in all)");
                line(lastHot[4] == 0 || Math.Abs(lastHot[2] - lastHot[4]) <= lastHot[4] / 8, "at the end of the load: ЦП " + lastHot[0] + " °C, ГП " + lastHot[1] + " °C; the processor's fan is led to " + lastHot[4] + " and turns at " + lastHot[2] + " RPM; peak " + peak + " °C, highest speed asked " + topTarget + ", reached " + topRpm);
                // no verdict here: whether the processor cools in 40 s depends on what else the laptop is doing (19.11: it kept warming)
                sb.AppendLine("      40 s after the load: ЦП " + lastCool[0] + " °C; led to " + lastCool[4] + (lastCool[4] == 0 ? " (the firmware)" : "") + ", turns at " + lastCool[2] + " RPM");
            }
            catch (Exception ex) { line(false, "the run broke off: " + ex.Message); }
            finally
            {
                burn[0] = false;
                fans.Dispose();
            }

            // released: what the fans do in the firmware's hands
            var after = new List<string>();
            for (int k = 0; k < 12; k++) { Thread.Sleep(500); after.Add(Lenovo.GetFeature(CpuFanId) + "/" + Lenovo.GetFeature(GpuFanId)); }
            line(!fans.Active, "the fans are given back to the firmware; over the next 6 s they turn at " + string.Join(" ", after) + " RPM");

            sb.AppendLine();
            sb.AppendLine(bad == 0 ? "all " + all + " checks pass" : bad + " of " + all + " checks FAIL");
            sb.AppendLine();
            sb.AppendLine("  time  stage      ЦП  ГП     RPM   RPM     led   led   who");
            sb.Append(rows);
            return sb.ToString();
        }

        // ---------- --fan-check <file>: the brain on made-up temperatures; nothing here reaches the firmware ----------

        // A pretended laptop: follows FanControl.Cycle (take the fans when a temperature is seen, give them back when
        // the brain goes blind) and keeps every order. A fan turns towards its order by 800 RPM a second; while the
        // firmware has it, towards Firmware.
        sealed class Bench
        {
            public readonly FanBrain B = new FanBrain();
            public FanOptions O;
            public int[] CurveCpu = DefaultCurve, CurveGpu = DefaultCurve;
            public bool Shared; public int Source = 2, Firmware, Releases;
            public double Now;
            public readonly int[] Rpm = new int[2];
            readonly int[] said = { -1, -1 };
            bool active;
            public readonly List<double>[] When = { new List<double>(), new List<double>() };
            public readonly List<int>[] Orders = { new List<int>(), new List<int>() };
            public readonly List<int[]> Targets = new List<int[]>();
            public readonly List<int[]> Temps = new List<int[]>();

            public Bench(int firmware) { Firmware = Rpm[0] = Rpm[1] = firmware; }

            void Say(int i, int v) { said[i] = v; When[i].Add(Now); Orders[i].Add(v); }

            public void Go(double seconds, Func<double, int> cpu, Func<double, int> gpu)
            {
                for (double end = Now + seconds - 1e-9; Now < end; Now += 0.5)
                {
                    int c = cpu(Now), g = gpu(Now);
                    bool stepped = false;
                    if (!active && (c > 0 || g > 0)) { active = true; B.Reset(); }
                    if (active)
                    {
                        var order = B.Step(Now, c, g, Rpm, CurveCpu, CurveGpu, Shared, Source, O);
                        if (B.Blind) { for (int i = 0; i < 2; i++) Say(i, 0); active = false; Releases++; B.Reset(); }
                        else { for (int i = 0; i < 2; i++) if (order[i] >= 0) Say(i, order[i]); stepped = true; }
                    }
                    Targets.Add(stepped ? (int[])B.Target.Clone() : new[] { 0, 0 });
                    Temps.Add(new[] { c, g });
                    for (int i = 0; i < 2; i++)
                    {
                        int goal = said[i] > 0 ? said[i] : Firmware;
                        Rpm[i] += Math.Max(-400, Math.Min(400, goal - Rpm[i]));
                    }
                }
            }

            public void Go(double seconds, int cpu, int gpu) { Go(seconds, x => cpu, x => gpu); }

            // the orders a fan got from the moment `from` on
            public List<int> Since(int fan, double from) { var r = new List<int>(); for (int k = 0; k < Orders[fan].Count; k++) if (When[fan][k] >= from - 1e-9) r.Add(Orders[fan][k]); return r; }
            public int Last(int fan) { return Orders[fan].Count > 0 ? Orders[fan][Orders[fan].Count - 1] : -1; }
            public string Show(int fan, double from) { return string.Join(" ", Since(fan, from)); }

            // the steepest fall between two orders in a row, RPM a second (orders of 0 = hand-over do not count)
            public double SteepestFall(int fan)
            {
                double worst = 0;
                for (int k = 1; k < Orders[fan].Count; k++)
                {
                    if (Orders[fan][k] <= 0 || Orders[fan][k - 1] <= 0) continue;
                    double span = When[fan][k] - When[fan][k - 1];
                    if (span > 0) worst = Math.Max(worst, (Orders[fan][k - 1] - Orders[fan][k]) / span);
                }
                return worst;
            }

            public int Changes(int fan) { int n = 0; for (int k = 1; k < Orders[fan].Count; k++) if (Orders[fan][k] != Orders[fan][k - 1]) n++; return n; }
        }

        static int OnCurve(int[] curve, double deg, int top) { return (int)Math.Round(Percent(curve, (float)deg) / 100.0 * top); }

        public static string Check()
        {
            var sb = new StringBuilder();
            int bad = 0, all = 0;
            Action<bool, string> line = (ok, text) => { all++; if (!ok) bad++; sb.AppendLine((ok ? "ok    " : "FAIL  ") + text); };
            var none = new FanOptions();
            var zero = new int[11];
            int max = 5100, min = 1700;
            int r50 = OnCurve(DefaultCurve, 50, max), r60 = OnCurve(DefaultCurve, 60, max), r70 = OnCurve(DefaultCurve, 70, max), r80 = OnCurve(DefaultCurve, 80, max);
            sb.AppendLine("the brain of the fan curves on made-up temperatures; fans of " + min + ".." + max + " RPM, the default curve: 50 °C " + r50 + ", 60 °C " + r60 + ", 70 °C " + r70 + ", 80 °C " + r80 + " RPM");
            sb.AppendLine();

            // the curve itself
            var b = new Bench(2000); b.Go(10, 45, 45);
            line(b.Show(0, 0) == "0" && b.Show(1, 0) == "0", "45 °C, the curve is under the fans' minimum: one «0» to each fan at the start and nothing more (" + b.Show(0, 0) + " | " + b.Show(1, 0) + ")");

            b = new Bench(2000); b.Go(10, 70, 70);
            line(b.Show(0, 0) == string.Join(" ", Enumerable.Repeat(r70, 5)) && b.Show(1, 0) == b.Show(0, 0), "70 °C held for 10 s: " + r70 + " RPM at once, said again every 2 s and nothing else (" + b.Show(0, 0) + ")");

            b = new Bench(2000); b.Go(5, 60, 60); b.Go(5, 80, 80);
            line(b.Since(0, 5)[0] == r80, "60 → 80 °C: the fan goes up in the same step (" + b.Show(0, 5) + ")");

            b = new Bench(2000); b.Go(5, 80, 80); b.Go(8, 60, 60);
            line(b.Since(0, 5)[0] == r60, "80 → 60 °C without «Плавный спад»: down at once (" + b.Show(0, 5) + ")");

            // «Плавный спад»
            b = new Bench(2000) { O = new FanOptions { Glide = true } }; b.Go(5, 80, 80); b.Go(8, 60, 60);
            line(b.SteepestFall(0) <= FanBrain.FallRate + 1 && b.Since(0, 5).Count >= 4 && b.Last(0) == r60, "the same with «Плавный спад»: down by " + b.SteepestFall(0).ToString("0") + " RPM a second at the most, ends at " + b.Last(0) + " (" + b.Show(0, 5) + ")");

            b = new Bench(3500) { O = new FanOptions { Glide = true } }; b.Go(8, 60, 60);
            line(b.Orders[0][0] == 3500 && b.SteepestFall(0) <= FanBrain.FallRate + 1 && b.Last(0) == r60, "«Плавный спад», the fan is taken from the firmware at 3500 RPM while the curve asks " + r60 + ": it starts from its real speed and glides down (" + b.Show(0, 0) + ")");
            b = new Bench(3500); b.Go(4, 60, 60);
            line(b.Orders[0][0] == r60, "the same without «Плавный спад»: " + b.Orders[0][0] + " at once");

            // under the minimum: the minimum, 3 s, the firmware
            b = new Bench(2000); b.Go(5, 70, 70); b.Go(8, 45, 45);
            var tail = b.Since(0, 5);
            double zeroAt = b.When[0][b.Orders[0].LastIndexOf(0)];
            line(tail[0] == min && tail[tail.Count - 1] == 0 && Math.Abs(zeroAt - 5 - FanBrain.LeaveS) < 0.01 && tail.All(v => v == min || v == 0), "70 → 45 °C (under the minimum): the minimum at once, the firmware " + (zeroAt - 5).ToString("0.0") + " s later (" + b.Show(0, 5) + ")");

            b = new Bench(2000); b.Go(5, 70, 70);
            for (int k = 0; k < 10; k++) { b.Go(1, 48, 48); b.Go(1, 51, 51); } // the minimum's line is at 49.5 °C
            line(!b.Since(0, 5).Contains(0), "a temperature that keeps crossing the minimum's line (48 ↔ 51 °C every second) never flips the fan to the firmware and back (" + string.Join(" ", b.Since(0, 5).Distinct()) + ")");

            b = new Bench(2000) { O = new FanOptions { Glide = true } }; b.Go(5, 80, 80); b.Go(12, 45, 45);
            tail = b.Since(0, 5);
            bool falling = true; for (int k = 1; k < tail.Count; k++) if (tail[k] > tail[k - 1]) falling = false;
            line(falling && tail.Contains(min) && tail[tail.Count - 1] == 0 && b.SteepestFall(0) <= FanBrain.FallRate + 1, "80 → 45 °C with «Плавный спад»: glides to the minimum, waits, then the firmware (" + b.Show(0, 5) + ")");

            // «Гистерезис»
            Func<double, int> wobble = x => ((int)Math.Round(x * 2)) % 2 == 0 ? 70 : 68;
            b = new Bench(2000); b.Go(20, wobble, wobble);
            var b2 = new Bench(2000) { O = new FanOptions { Band = true } }; b2.Go(20, wobble, wobble);
            line(b.Changes(0) >= 10 && b2.Changes(0) == 0 && b2.Last(0) == r70, "70 ↔ 68 °C every half second: " + b.Changes(0) + " changes of speed without «Гистерезис», " + b2.Changes(0) + " with it (stays at " + b2.Last(0) + ")");
            b2 = new Bench(2000) { O = new FanOptions { Band = true } }; b2.Go(5, 70, 70); b2.Go(5, 66, 66);
            line(b2.Last(0) == OnCurve(DefaultCurve, 69, max), "«Гистерезис», 70 → 66 °C: the fan comes down by what is beyond the 3 °C slack, to the curve's 69 °C (" + b2.Last(0) + ")");

            // «Сглаживание»
            Func<double, int> spike = x => Math.Abs(x - 5) < 0.01 ? 80 : 60;
            b = new Bench(2000); b.Go(10, spike, spike);
            b2 = new Bench(2000) { O = new FanOptions { Average = true } }; b2.Go(10, spike, spike);
            line(b.Orders[0].Max() == r80 && b2.Orders[0].Max() < r60 + 150, "one reading of 80 °C among 60 °C: the fan jumps to " + b.Orders[0].Max() + " without «Сглаживание», to " + b2.Orders[0].Max() + " at the most with it");

            // «Страховка»
            b = new Bench(2000) { O = new FanOptions { Floor = true }, CurveCpu = zero, CurveGpu = zero }; b.Go(4, 74, 74); int f74 = b.Last(0); b.Go(4, 82, 82); int f82 = b.Last(0); b.Go(4, 90, 90); int f90 = b.Last(0);
            line(f74 == 0 && f82 == (int)Math.Round((82 - 75) / 15.0 * 0.8 * max) && f90 == (int)Math.Round(0.8 * max), "«Страховка» under a curve of all zeros: 74 °C the firmware, 82 °C " + f82 + ", 90 °C " + f90 + " RPM (80 % of " + max + ")");
            b = new Bench(2000) { O = new FanOptions { Floor = true }, CurveCpu = zero, CurveGpu = zero }; b.Go(4, 60, 90);
            line(b.Last(0) == (int)Math.Round(0.8 * max) && b.Last(1) == b.Last(0), "«Страховка» goes by the hotter sensor: ЦП 60 °C, ГП 90 °C — both fans at " + b.Last(0));
            b = new Bench(2000) { CurveCpu = zero, CurveGpu = zero }; b.Go(4, 90, 90);
            line(b.Last(0) == 0, "without «Страховка» the same curve leaves the fans to the firmware at 90 °C");

            // «Аварийный режим»
            b = new Bench(2000) { O = new FanOptions { Emergency = true } }; b.Go(3, 96, 70); int p96 = b.Last(0), p96g = b.Last(1); b.Go(3, 92, 70); int p92 = b.Last(0); b.Go(3, 89, 70); int p89 = b.Last(0);
            line(p96 == max && p96g == max && p92 == max && p89 == OnCurve(DefaultCurve, 89, max), "«Аварийный режим»: 96 °C on the processor — both fans at " + max + "; still so at 92 °C; back to the curve under 90 °C (" + p89 + ")");
            b = new Bench(2000); b.Go(3, 96, 70);
            line(b.Last(0) == OnCurve(DefaultCurve, 96, max) && b.Last(1) == r70, "without it 96 °C is just the curve: " + b.Last(0) + " and " + b.Last(1));

            // «Синхронизация», one curve
            b = new Bench(2000); b.Go(3, 80, 50);
            b2 = new Bench(2000) { O = new FanOptions { Pair = true } }; b2.Go(3, 80, 50);
            line(b.Last(0) == r80 && b.Last(1) == r50 && b2.Last(0) == r80 && b2.Last(1) == r80, "ЦП 80 °C, ГП 50 °C: " + b.Last(0) + " and " + b.Last(1) + " apart, both " + b2.Last(1) + " with «Синхронизация»");
            var src = new int[3];
            for (int k = 0; k < 3; k++) { b = new Bench(2000) { Shared = true, Source = k }; b.Go(3, 60, 80); src[k] = b.Last(0) == b.Last(1) ? b.Last(0) : -1; }
            line(src[0] == r60 && src[1] == r80 && src[2] == r80, "one curve for both fans, ЦП 60 °C, ГП 80 °C: by the processor " + src[0] + ", by the card " + src[1] + ", by the hotter " + src[2] + " — the same on both fans");

            // «Разгон при старте»
            b = new Bench(0) { O = new FanOptions { Kick = true } }; b.Go(4, 50, 50);
            line(b.Orders[0][0] == FanBrain.KickRpm && b.Orders[0][1] == r50 && b.When[0][1] <= 1.01, "«Разгон при старте», the fan stands still: " + FanBrain.KickRpm + " first, " + r50 + " within a second (" + b.Show(0, 0) + ")");
            b = new Bench(2000) { O = new FanOptions { Kick = true } }; b.Go(4, 50, 50);
            line(b.Orders[0][0] == r50, "a fan that already turns (2000 RPM) gets no push: " + b.Orders[0][0]);
            b = new Bench(0); b.Go(4, 50, 50);
            line(b.Orders[0][0] == r50, "without «Разгон при старте» a standing fan gets " + b.Orders[0][0] + " at once");

            // «Упреждение»
            Func<double, int> ramp = x => x < 5 ? 50 : (int)Math.Min(82, 50 + (x - 5 + 0.5) * 8);
            b = new Bench(2000); b.Go(14, ramp, ramp);
            b2 = new Bench(2000) { O = new FanOptions { Ahead = true } }; b2.Go(14, ramp, ramp);
            int lead = 0; bool sane = true;
            for (int k = 0; k < b.Targets.Count; k++)
            {
                lead = Math.Max(lead, b2.Targets[k][0] - b.Targets[k][0]);
                if (b2.Targets[k][0] < b.Targets[k][0] || b2.Targets[k][0] > OnCurve(DefaultCurve, b.Temps[k][0] + FanBrain.AheadMax, max)) sane = false;
            }
            line(lead >= 400 && sane && b2.Last(0) == b.Last(0) && b.Last(0) == OnCurve(DefaultCurve, 82, max), "50 → 82 °C at 8 °C a second: with «Упреждение» the fan is up to " + lead + " RPM ahead of the plain curve, never beyond the curve 20 °C on, and ends at the same " + b.Last(0));
            Func<double, int> slow = x => (int)Math.Min(82, 50 + x * 2);
            b = new Bench(2000); b.Go(20, slow, slow);
            b2 = new Bench(2000) { O = new FanOptions { Ahead = true } }; b2.Go(20, slow, slow);
            bool same = true; for (int k = 0; k < b.Targets.Count; k++) if (b.Targets[k][0] != b2.Targets[k][0]) same = false;
            line(same, "a rise of 2 °C a second is not a spike: «Упреждение» changes nothing");

            // sensors
            b = new Bench(2000); b.Go(3, 0, 70);
            line(b.Last(0) == r70 && b.Last(1) == r70, "the processor's sensor gives nothing, the card reads 70 °C: the processor's fan goes by the card (" + b.Last(0) + ")");
            b = new Bench(2000); b.Go(5, 70, 70); b.Go(4.5, 0, 0);
            bool kept = b.Since(0, 5).All(v => v == r70) && b.Releases == 0;
            b.Go(4, 0, 0); int rel = b.Releases; int afterLoss = b.Last(0); b.Go(3, 70, 70);
            line(kept && rel == 1 && afterLoss == 0 && b.Last(0) == r70, "both sensors gone: the last speed is repeated for 5 s, then the fans go to the firmware (handed over " + rel + " time); the readings are back — the curve again (" + b.Last(0) + ")");
            b = new Bench(2000); b.Go(6, 0, 0);
            line(b.Orders[0].Count == 0 && b.Orders[1].Count == 0, "no temperature from the very start: the fans are not taken at all");

            // an order the firmware did not take is said again
            var br = new FanBrain(); var rp = new[] { 2000, 2000 };
            int o1 = br.Step(0, 70, 70, rp, DefaultCurve, DefaultCurve, false, 0, none)[0]; br.Unsaid(0);
            int o2 = br.Step(0.5, 70, 70, rp, DefaultCurve, DefaultCurve, false, 0, none)[0];
            int o3 = br.Step(1.0, 70, 70, rp, DefaultCurve, DefaultCurve, false, 0, none)[0];
            line(o1 == r70 && o2 == r70 && o3 == -1, "an order the firmware refused is given again at the next step (" + o1 + ", " + o2 + "), a taken one is not (" + o3 + ")");

            // a laptop that heats and cools: no hunting
            foreach (bool every in new[] { false, true })
            {
                var opt = every ? new FanOptions { Floor = true, Emergency = true, Band = true, Average = true, Glide = true, Kick = true, Pair = true, Ahead = true } : new FanOptions { Floor = true, Emergency = true };
                var lap = new Bench(2000) { O = opt };
                double deg = 45; int peak = 0;
                for (int k = 0; k < 360; k++)
                {
                    int read = (int)Math.Round(deg); peak = Math.Max(peak, read);
                    lap.Go(0.5, read, read - 10);
                    double rest = 100 - 35.0 * lap.Rpm[0] / max;   // where this load would settle at this fan speed
                    deg += (rest - deg) * 0.5 / 8;
                }
                var lastT = lap.Targets.Skip(lap.Targets.Count - 60).Select(x => x[0]).ToList();
                int swing = lastT.Max() - lastT.Min();
                line(swing <= 300 && lastT.Min() >= min && peak < 95, "a load that settles between 65 and 100 °C by the fan's speed, 3 minutes, " + (every ? "every extra on" : "the two default extras") + ": peak " + peak + " °C, settles at " + (int)Math.Round(deg) + " °C, the speed swings by " + swing + " RPM over the last 30 s");
            }

            // anything at all: the orders stay within what a fan can do, the nets hold
            var rnd = new Random(20261119);
            int runs = 300, steps = 0; string broke = null;
            for (int run = 0; run < runs && broke == null; run++)
            {
                int m = rnd.Next(256);
                var opt = new FanOptions { Floor = (m & 1) != 0, Emergency = (m & 2) != 0, Band = (m & 4) != 0, Average = (m & 8) != 0, Glide = (m & 16) != 0, Kick = (m & 32) != 0, Pair = (m & 64) != 0, Ahead = (m & 128) != 0 };
                var c1 = new int[11]; var c2 = new int[11];
                for (int k = 0; k < 11; k++) { c1[k] = Math.Max(k > 0 ? c1[k - 1] : 0, rnd.Next(101)); c2[k] = Math.Max(k > 0 ? c2[k - 1] : 0, rnd.Next(60)); }
                var f = new FanBrain(); bool sh = rnd.Next(3) == 0; int so = rnd.Next(3);
                double now = 0; int tc = 30 + rnd.Next(60), tg = 30 + rnd.Next(60); var turn = new[] { rnd.Next(2) * 2000, rnd.Next(2) * 2000 };
                for (int k = 0; k < 240 && broke == null; k++)
                {
                    now += rnd.Next(10) == 0 ? 0.5 + rnd.NextDouble() * 3 : 0.5;
                    tc = Math.Max(20, Math.Min(105, tc + rnd.Next(-6, 7))); tg = Math.Max(20, Math.Min(105, tg + rnd.Next(-6, 7)));
                    int sc = rnd.Next(25) == 0 ? 0 : tc, sg = rnd.Next(25) == 0 ? 0 : tg;
                    var ord = f.Step(now, sc, sg, turn, c1, c2, sh, so, opt); steps++;
                    int hot = Math.Max(sc, sg);
                    for (int i = 0; i < 2; i++)
                    {
                        int said = ord[i], led = f.Target[i];
                        double floor = FanBrain.FloorPercent(hot) / 100 * max;
                        if (!(said == -1 || said == 0 || (said >= min && said <= max))) broke = "an order of " + said;
                        else if (!(led == 0 || (led >= min && led <= max))) broke = "a target of " + led;
                        else if (hot > 0 && opt.Floor && floor >= min && led < (int)Math.Round(floor) - 1) broke = "under the floor: " + led + " at " + hot + " °C";
                        else if (hot >= 95 && opt.Emergency && led != max) broke = "no top speed at " + hot + " °C: " + led;
                        if (said > 0) turn[i] = said; else if (said == 0) turn[i] = 0;
                    }
                }
            }
            line(broke == null, "random temperatures, curves, extras, lost readings and late steps (" + runs + " runs, " + steps + " steps): every order is «0» or " + min + ".." + max + " RPM, «Страховка» and «Аварийный режим» always hold" + (broke != null ? " — BROKEN: " + broke : ""));

            sb.AppendLine();
            sb.AppendLine(bad == 0 ? "all " + all + " checks pass" : bad + " of " + all + " checks FAIL");
            return sb.ToString();
        }
    }
}
