using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedTune
{
    // Curve presets and applying them: the UXTU part of the app. Results come back on the UI thread.
    class CurveService
    {
        public readonly Settings Settings;
        public List<CurvePreset> Presets;
        public int[] Live;            // last values read from the CPU, null until the first read
        public string LastError;
        readonly SynchronizationContext ui;
        int busy;

        public event Action StateChanged; // Live / LastError changed

        public CurveService(Settings settings)
        {
            Settings = settings;
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            Presets = Store.LoadPresets();
        }

        public bool Busy { get { return busy > 0; } }

        public CurvePreset Active
        {
            get { return Presets.FirstOrDefault(p => p.Name == Settings.ActivePreset) ?? Presets.FirstOrDefault(p => !p.BuiltIn) ?? Presets.Last(); }
        }

        void Post(Action a) { ui.Post(_ => { a(); var h = StateChanged; if (h != null) h(); }, null); }

        public void Refresh(Action<int[]> done = null)
        {
            Interlocked.Increment(ref busy);
            Task.Run(() =>
            {
                int[] v = null; string err = null;
                try { v = Smu.Read(); } catch (Exception ex) { err = ex.Message; }
                Interlocked.Decrement(ref busy);
                Post(() => { if (v != null) Live = v; LastError = err; if (done != null) done(v); });
            });
        }

        // source: "вручную", "запуск", "зарядка", "батарея", "после сна", "история"
        // history: false for a core's measurement at 0 (16.11): it sets one core to 0 for a minute and puts the value back,
        // which is not a set anyone would want to return to; the log still has it
        public void Apply(int[] values, string presetName, string source, Action<bool> done = null, bool history = true)
        {
            values = (int[])values.Clone();
            Interlocked.Increment(ref busy);
            Task.Run(() =>
            {
                int[] now = null; string err = null;
                try { now = Smu.Write(values); } catch (Exception ex) { err = ex.Message; }
                bool ok = now != null && now.SequenceEqual(values);
                var result = ok ? "ok" : err ?? "процессор вернул " + Store.Join(now);
                if (history) Store.AddHistory(new HistoryEntry { Time = DateTime.Now, Source = source, Preset = presetName, Values = values, Result = result });
                Store.Log("кривая (" + source + ", " + presetName + "): " + Store.Join(values) + " -> " + result);
                Interlocked.Decrement(ref busy);
                Post(() => { if (now != null) Live = now; LastError = ok ? null : result; if (done != null) done(ok); });
            });
        }

        public void ApplyActive(string source, Action<bool> done = null)
        {
            var p = Active;
            Apply(p.Values, p.Name, source, done);
        }

        public void SavePresets() { Store.SavePresets(Presets); }

        // first start: the values on the CPU become the first preset, so nothing changes until the user wants it
        public void EnsureFirstPreset(Action done)
        {
            if (Presets.Any(p => !p.BuiltIn)) { done(); return; }
            Refresh(v =>
            {
                // the same number on every core is the BIOS setting (after a reboot it was +10 everywhere on 12.11),
                // not a tuned curve: then no preset, nothing is applied on its own
                if (v != null && v.Distinct().Count() > 1)
                {
                    Presets.Insert(0, new CurvePreset { Name = "Дневной", Values = v });
                    Settings.ActivePreset = "Дневной";
                    SavePresets(); Store.SaveSettings(Settings);
                }
                done();
            });
        }

        // ---------- other programs that write the curve ----------

        public static bool UxtuRunning { get { return Process.GetProcessesByName("Universal x86 Tuning Utility").Length > 0; } }

        public static bool UxtuTaskEnabled
        {
            get
            {
                try
                {
                    int code;
                    var text = Tasks.Run("/Query /TN \"UXTU\" /FO LIST", out code);
                    return code == 0 && text.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Отключено", StringComparison.OrdinalIgnoreCase) < 0;
                }
                catch { return false; }
            }
        }
    }
}
