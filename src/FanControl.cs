using System;
using System.Linq;
using System.Management;
using System.Threading;
using Microsoft.Win32;

namespace RedTune
{
    // Fan curves driven by the app, ported from the CustomFanCurve plugin of LenovoLegionToolkit-Plugins (GPL-3):
    // every 0.5 s the CPU and GPU temperatures go through the preset's curve (11 points, 0..100 °C → % of max RPM)
    // and the RPM is written straight to the firmware (GetFeatureValue/SetFeatureValue 0x04030001 CPU fan,
    // 0x04030002 GPU fan; 0 = the firmware drives the fan again). Checked on the R7000P on 2026-11-12: 2400 → 3500 RPM
    // in ~5 s, back to auto after writing 0. Only in the custom mode, like the plugin (the firmware ignores it elsewhere).
    // Not ported: the fuzzy-logic "smart auto" and the acoustic beat offset.
    sealed class FanControl : IDisposable
    {
        public const uint CpuFanId = 0x04030001, GpuFanId = 0x04030002;
        static readonly uint[] Ids = { CpuFanId, GpuFanId };
        public static readonly int[] DefaultCurve = { 0, 0, 0, 0, 0, 35, 42, 50, 65, 85, 100 }; // % at 0,10..100 °C; under the minimum = the firmware's own table

        // the plugin's defaults
        const int SensorMs = 500, CalcMs = 1000, KeepAliveMs = 2500, MinChange = 50, StepDownSpam = 400, StepDownRate = 400;
        const int SafeMinTemp = 75, SafeMaxTemp = 90, SafeMaxPercent = 80, CriticalTemp = 95, HystTemp = 3, SpikeRate = 5, Lookahead = 2;
        const int BoostRpm = 3000, BoostMs = 300;
        const double EmaAlpha = 0.2;

        public int MaxRpm = 5100, MinRpm = 1700;
        readonly PowerService power;
        readonly Settings s;
        Thread thread;
        volatile bool stop, suspended;

        // what the page shows
        public volatile bool Active;
        public int CpuTemp = -1, GpuTemp = -1;
        public readonly int[] Rpm = new int[2], Target = new int[2];
        public event Action Updated; // from the worker thread

        // per-fan state, like the plugin's dictionaries
        readonly int?[] lastApplied = new int?[2];
        readonly int?[] lastCalc = new int?[2], lastIdeal = new int?[2];
        readonly long[] lastCalcTick = new long[2], lastWrite = new long[2], lastRawTick = new long[2];
        readonly double?[] ema = new double?[2];
        readonly float[] lastRaw = new float[2];
        bool lastShared;

        static FanControl current;

        public FanControl(PowerService power, Settings s)
        {
            this.power = power; this.s = s;
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

        // min/max from LENOVO_FAN_TEST_DATA (here 1700..5100 for both fans)
        void ReadLimits()
        {
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_FAN_TEST_DATA"))
                    foreach (ManagementObject o in q.Get())
                    {
                        var max = o["FanMaxSpeed"] as uint[]; var min = o["FanMinSpeed"] as uint[];
                        if (max != null && max.Length > 0 && max.Min() > 0) MaxRpm = (int)max.Min();
                        if (min != null && min.Length > 0 && min.Max() > 0) MinRpm = (int)min.Max();
                    }
            }
            catch (Exception ex) { Store.Log("вентиляторы: нет LENOVO_FAN_TEST_DATA (" + ex.Message + "), беру 1700..5100"); }
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
            if (!Active) return;
            Active = false;
            foreach (var id in Ids) try { Lenovo.SetFeature(id, 0); } catch { }
            Reset();
            Store.Log("вентиляторы: управление отдано прошивке (" + why + ")");
        }

        void Reset()
        {
            for (int i = 0; i < 2; i++) { lastApplied[i] = lastCalc[i] = lastIdeal[i] = null; lastCalcTick[i] = lastWrite[i] = lastRawTick[i] = 0; ema[i] = null; Target[i] = 0; }
        }

        void Loop()
        {
            while (!stop)
            {
                try { Cycle(); } catch (Exception ex) { Store.Log("вентиляторы: " + ex.Message); }
                var h = Updated; if (h != null) try { h(); } catch { }
                Thread.Sleep(SensorMs);
            }
        }

        static int Temp(uint id) { var v = Lenovo.GetFeature(id); return v == int.MinValue || v < 0 || v > 150 ? 0 : v; }

        void Cycle()
        {
            int cpu = Temp(Lenovo.CpuTemp), gpu = Temp(Lenovo.GpuTemp);
            CpuTemp = cpu; GpuTemp = gpu;
            for (int i = 0; i < 2; i++) { var r = Lenovo.GetFeature(Ids[i]); Rpm[i] = r == int.MinValue ? 0 : r; }

            var p = power.Active;
            bool want = !suspended && !stop && p != null && p.FanCurveOn && !p.FanMax && power.Mode == Lenovo.Custom;
            if (!want) { if (Active) Release(p == null || !p.FanCurveOn ? "кривая выключена" : p.FanMax ? "полная скорость" : "не «Свой» режим"); return; }
            if (!Active) { Active = true; Reset(); Store.Log("вентиляторы: своя кривая (пресет «" + p.Name + "»)"); }
            if (cpu <= 0 && gpu <= 0) return; // sensor dropout

            long now = DateTime.UtcNow.Ticks;
            // emergency: everything to the maximum
            if (s.FanCritical && Math.Max(cpu, gpu) >= CriticalTemp)
            {
                for (int i = 0; i < 2; i++)
                    if (lastApplied[i] != MaxRpm || now - lastWrite[i] >= TimeSpan.FromMilliseconds(KeepAliveMs).Ticks) Write(i, MaxRpm, now);
                return;
            }

            if (p.FanShared != lastShared) { lastShared = p.FanShared; Reset(); } // switched between one curve and two: start the math afresh
            var res = new int[2]; var write = new bool[2];
            if (p.FanShared)
            {
                // one curve, one temperature (CPU, GPU or the hotter one), the same RPM on both fans
                int t = SharedTemp(p.FanSource, cpu, gpu);
                res[0] = Compute(0, p.FanBoth ?? DefaultCurve, t, Math.Max(cpu, gpu), now, out write[0]);
                res[1] = res[0];
                write[1] = write[0] || lastApplied[1] != res[0] || now - lastWrite[1] >= TimeSpan.FromMilliseconds(KeepAliveMs).Ticks;
                for (int i = 0; i < 2; i++) { Target[i] = res[i]; if (write[i]) Write(i, res[i], now); }
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                var curve = (i == 0 ? p.FanCpu : p.FanGpu) ?? DefaultCurve;
                res[i] = Compute(i, curve, i == 0 ? cpu : gpu, Math.Max(cpu, gpu), now, out write[i]);
            }

            // both fans to the highest share any of them asks for
            if (s.FanSync)
            {
                int top = Math.Max(res[0], res[1]);
                for (int i = 0; i < 2; i++) if (res[i] != top) { res[i] = top > 0 && top < MinRpm ? MinRpm : top; write[i] = true; }
            }

            for (int i = 0; i < 2; i++) { Target[i] = res[i]; if (write[i]) Write(i, res[i], now); }
        }

        // the plugin's ProcessCurveMath for one fan: returns the target RPM, sets whether it has to be written now
        int Compute(int i, int[] curve, float temp, float hottest, long now, out bool shouldWrite)
        {
            double smoothed = temp;
            if (s.FanEma) { smoothed = ema[i].HasValue ? temp * EmaAlpha + ema[i].Value * (1 - EmaAlpha) : temp; ema[i] = smoothed; } else ema[i] = null;
            float calcTemp = (float)smoothed;

            // a fast rise: look ahead so the fans start early
            if (s.FanPredict)
            {
                if (lastRawTick[i] > 0 && now > lastRawTick[i])
                {
                    double dt = (now - lastRawTick[i]) / (double)TimeSpan.TicksPerSecond, rate = (temp - lastRaw[i]) / dt;
                    if (rate > SpikeRate) calcTemp = Math.Max(calcTemp, (float)Math.Min(Math.Max(temp + rate * Lookahead, temp), Math.Max(temp, SafeMaxTemp)));
                }
                lastRaw[i] = temp; lastRawTick[i] = now;
            }

            bool steppingDown = lastIdeal[i].HasValue && lastCalc[i].HasValue && lastIdeal[i] < lastCalc[i];
            int cached = lastCalc[i] ?? 0;
            int target;
            if (lastCalc[i] == null || now - lastCalcTick[i] >= TimeSpan.FromMilliseconds(CalcMs).Ticks)
            {
                float evalTemp = calcTemp;
                if (s.FanHyst && cached > 0) evalTemp += HystTemp; // a spinning fan holds its speed a little longer
                int ideal = Math.Min((int)Math.Round(Percent(curve, evalTemp) / 100.0 * MaxRpm), MaxRpm);
                if (s.FanSafety) ideal = Math.Max(ideal, (int)Math.Round(SafeMin(Math.Max(calcTemp, hottest)) / 100.0 * MaxRpm));
                if (ideal > 0) ideal = Math.Max(Math.Min(MinRpm, MaxRpm), Math.Min(MaxRpm, ideal));

                int last = lastCalc[i] ?? ideal;
                target = ideal;
                if (s.FanGlide && ideal < last) // slow down gently
                {
                    double dt = Math.Min((now - lastCalcTick[i]) / (double)TimeSpan.TicksPerSecond, 2.0);
                    target = Math.Max(ideal, last - (int)(StepDownRate * dt));
                }
                if (target > 0 && target < MinRpm) target = 0;
                lastIdeal[i] = ideal; lastCalc[i] = target; lastCalcTick[i] = now;
            }
            else target = cached;

            // soft landing to the firmware: step down 100 RPM a cycle to the minimum, then 0
            if (target <= 0 && Rpm[i] > 0)
            {
                int lc = lastCalc[i] ?? 0;
                if (lc != 0 || !lastCalc[i].HasValue)
                {
                    int b = lc > 0 ? lc : Rpm[i];
                    target = b <= MinRpm ? 0 : b - 100 <= MinRpm ? MinRpm : b - 100;
                }
                lastCalc[i] = target;
            }

            bool had = lastApplied[i].HasValue;
            int delta = had ? Math.Abs(lastApplied[i].Value - target) : int.MaxValue;
            int need = steppingDown ? StepDownSpam : MinChange;
            bool close = had && (delta < need || target == lastApplied[i].Value);
            if (close) target = lastApplied[i].Value;
            bool keepAlive = now - lastWrite[i] >= TimeSpan.FromMilliseconds(KeepAliveMs).Ticks;
            shouldWrite = !close || (steppingDown && target <= 0) || keepAlive || (Math.Abs(Rpm[i] - target) > 500 && keepAlive);
            return target;
        }

        void Write(int i, int rpm, long now)
        {
            try
            {
                bool wasStopped = !lastApplied[i].HasValue || lastApplied[i].Value == 0;
                if (s.FanBoost && rpm > 0 && wasStopped && rpm < BoostRpm) { Lenovo.SetFeature(Ids[i], BoostRpm); Thread.Sleep(BoostMs); }
                Lenovo.SetFeature(Ids[i], Math.Max(0, rpm));
                lastApplied[i] = rpm; lastWrite[i] = now;
            }
            catch (Exception ex) { Store.Log("вентиляторы: запись " + rpm + " об/мин: " + ex.Message); }
        }

        // the temperature of the shared curve: 0 CPU, 1 GPU, 2 the hotter of the two
        public static int SharedTemp(int source, int cpu, int gpu) { return source == 0 ? cpu : source == 1 ? gpu : Math.Max(cpu, gpu); }

        // linear between the 11 points (0, 10 .. 100 °C)
        public static double Percent(int[] curve, float t)
        {
            if (t <= 0) return curve[0];
            if (t >= 100) return curve[10];
            int k = (int)(t / 10); float f = (t - k * 10) / 10f;
            return curve[k] + (curve[k + 1] - curve[k]) * f;
        }

        // the safety net: 0 % at 75 °C up to 80 % at 90 °C, whatever the curve says
        static double SafeMin(float t)
        {
            if (t <= SafeMinTemp) return 0;
            if (t >= SafeMaxTemp) return SafeMaxPercent;
            return (t - SafeMinTemp) / (double)(SafeMaxTemp - SafeMinTemp) * SafeMaxPercent;
        }

        public void Dispose()
        {
            stop = true;
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.SessionEnding -= OnSessionEnding;
            if (thread != null) thread.Join(2000);
            Release("выход");
        }
    }
}
