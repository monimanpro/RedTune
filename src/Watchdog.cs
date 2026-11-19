using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RedTune
{
    // 16.11: «Сторож» (the third tab of «Кривая»). A curve that passed every test can still be a step too deep for some
    // core: the processor then corrects the error itself and only writes a WHEA record, nothing falls, nobody notices
    // (15–16.11: 110 such records from CCD1 Core 5 in one night, found by hand). The watchdog reads those records once a
    // minute in ordinary use and, after a restart without a shutdown, takes the cores the flight recorder saw busy. It
    // changes nothing by itself: it says which core erred and offers +3 for it; the user presses the button.
    // The second half, off unless asked for: after some days without errors it offers one step deeper for every core
    // that has not erred yet, so ordinary use takes the place of the long tests. A core that errs is raised and left
    // alone from then on.
    class WatchEvent
    {
        public string Time { get; set; }
        public int Core { get; set; } = -1;    // −1: no single core (a deepening, a restart with every core idle)
        public string Kind { get; set; }       // whea, reset, raise, deepen
        public int Count { get; set; }
        public int Value { get; set; }         // the core's value then
        public string Text { get; set; }       // the line for the journal
    }

    class WatchState
    {
        public string LastSeen { get; set; }   // WHEA records are read from here on
        public string Since { get; set; }      // the quiet period began: the last error or the last change of the curve
        public List<WatchEvent> Events { get; set; } = new List<WatchEvent>();
        public List<int> Pending { get; set; } = new List<int>();   // cores with an offer of +3
        public List<int> Frozen { get; set; } = new List<int>();    // cores that erred: never deepened
        public bool DeepenOffer { get; set; }
        public int Wheas { get; set; }
        public int Resets { get; set; }
    }

    sealed class Watchdog : IDisposable
    {
        public const int RaiseStep = 3, DeepenStep = 1;
        static readonly string FilePath = Path.Combine(Store.DataDir, "watch.json");
        public WatchState State;
        public event Action Changed;
        public Action<string> Notify = t => { };
        public DateTime LastCheck = DateTime.MinValue;
        public bool Dry; // --watch-dry: reads and decides, saves and says nothing
        readonly Settings settings;
        readonly CurveService curve;
        readonly Func<bool> testing; // an auto-tune, a core-curve measurement: their errors are theirs
        readonly SynchronizationContext ui;
        readonly Timer timer;
        int busy;

        public Watchdog(Settings settings, CurveService curve, Func<bool> testing)
        {
            this.settings = settings; this.curve = curve; this.testing = testing;
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            State = ReadState();
            timer = new Timer(_ => Check(), null, 20000, 60000);
        }

        static WatchState ReadState()
        {
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<WatchState>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new WatchState(); }
            catch (Exception ex) { Store.Log("watch.json: " + ex.Message); }
            return new WatchState();
        }

        void Save()
        {
            if (Dry) return;
            try { Directory.CreateDirectory(Store.DataDir); File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(State), new UTF8Encoding(true)); }
            catch (Exception ex) { Store.Log("watch.json: " + ex.Message); }
        }

        static string Stamp(DateTime t) { return t.ToString("s"); }
        static DateTime Parse(string s, DateTime or) { DateTime t; return s != null && DateTime.TryParse(s, out t) ? t : or; }
        public DateTime Since { get { return Parse(State.Since, DateTime.Now); } }
        void Post() { ui.Post(_ => { var h = Changed; if (h != null) h(); }, null); }

        void Log(WatchEvent e)
        {
            e.Time = Stamp(DateTime.Now);
            State.Events.Add(e);
            while (State.Events.Count > 60) State.Events.RemoveAt(0);
            if (!Dry) Store.Log("сторож: " + e.Text);
        }

        int ValueOf(int core) { var p = curve.Active; return p != null && core >= 0 && core < p.Values.Length ? p.Values[core] : 0; }

        // switched on (or the curve was changed by hand): the count starts now, old records are not looked at
        public void Restart()
        {
            lock (this) { State.LastSeen = State.Since = Stamp(DateTime.Now); State.DeepenOffer = false; Save(); }
            Post();
        }

        public string LastSay;
        public void CheckNow() { Check(); }

        // once a minute, on the timer's thread
        void Check()
        {
            if (!settings.WatchOn || !Hw.Curve) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var now = DateTime.Now;
                string say = null;
                lock (this)
                {
                    if (State.LastSeen == null) { State.LastSeen = State.Since = Stamp(now); Save(); }
                    // while something is being tested the errors belong to the test
                    // saved: 18.11 the mark stayed in memory only, the app was restarted after a tune, and the errors of the tune's own values were counted against the curve in use
                    if (testing()) { State.LastSeen = Stamp(now); LastCheck = now; Save(); return; }
                    var hits = Load.WheaHits(Parse(State.LastSeen, now)).Where(h => h.Core >= 0).GroupBy(h => h.Core).ToList();
                    State.LastSeen = Stamp(now); LastCheck = now;
                    foreach (var g in hits)
                    {
                        int core = g.Key, v = ValueOf(core), n = g.Count();
                        State.Wheas += n;
                        Log(new WatchEvent { Core = core, Kind = "whea", Count = n, Value = v, Text = Hw.CoreName(core) + ": исправленных ошибок " + n + " при значении " + v });
                        if (!State.Frozen.Contains(core)) State.Frozen.Add(core);
                        if (v < 0 && !State.Pending.Contains(core)) State.Pending.Add(core);
                        State.Since = Stamp(now); State.DeepenOffer = false;
                    }
                    if (hits.Count > 0)
                        say = "Сторож: ошибки на " + string.Join(", ", hits.Select(g => Hw.CoreName(g.Key))) + ". Совет — во вкладке «Кривая» → «Сторож»";
                    else if (settings.WatchDeepen && !State.DeepenOffer && State.Pending.Count == 0 && (now - Since).TotalDays >= Math.Max(1, settings.WatchDays) && CanDeepen())
                    {
                        State.DeepenOffer = true;
                        say = "Сторож: " + Math.Max(1, settings.WatchDays) + " дн. без ошибок. Можно попробовать на шаг глубже — вкладка «Кривая» → «Сторож»";
                    }
                    if (hits.Count > 0 || say != null) Save();
                }
                if (say != null && !Dry) { var text = say; ui.Post(_ => Notify(text), null); }
                LastSay = say;
                Post();
            }
            catch (Exception ex) { Store.Log("сторож: " + ex.Message); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        bool CanDeepen() { var p = curve.Active; return p != null && !p.BuiltIn && Enumerable.Range(0, p.Values.Length).Any(c => !State.Frozen.Contains(c) && p.Values[c] > Smu.Min); }

        // at the app's start: the computer restarted without a shutdown and the flight recorder knows which cores were busy
        public void ResetSeen(List<int> cores, string report)
        {
            if (!settings.WatchOn || !Hw.Curve) return;
            lock (this)
            {
                State.Resets++;
                var deep = cores.Where(c => ValueOf(c) < 0).ToList();
                Log(new WatchEvent { Kind = "reset", Core = deep.Count == 1 ? deep[0] : -1, Text = "перезагрузка без завершения работы; работали: " + (report ?? "неизвестно") });
                foreach (var c in deep)
                {
                    if (!State.Frozen.Contains(c)) State.Frozen.Add(c);
                    if (!State.Pending.Contains(c)) State.Pending.Add(c);
                }
                State.Since = State.LastSeen = Stamp(DateTime.Now); State.DeepenOffer = false;
                Save();
            }
            Post();
        }

        // ---------- what the user decides (UI thread) ----------

        // the active preset with one core changed, saved and applied
        void Change(Func<int, int, int> newValue, string source, Action<bool> done)
        {
            var p = curve.Active;
            if (p == null || p.BuiltIn) { if (done != null) done(false); return; }
            for (int c = 0; c < p.Values.Length; c++)
            {
                int old = p.Values[c], v = newValue(c, old);
                if (v == old) continue;
                v = Math.Max(Smu.Min, Math.Min(Math.Max(0, old), v)); // a raise stops at 0, a core set above 0 by hand stays there
                p.Values[c] = v;
                if (p.Core != null && c < p.Core.Length) p.Core[c] = v;
            }
            p.Mode = 0; // a value per core from now on: a chiplet or common value cannot hold one core's change
            curve.SavePresets();
            curve.ApplyActive(source, done);
        }

        public void Raise(int core, Action<bool> done = null)
        {
            int old = ValueOf(core), now = Math.Min(0, old + RaiseStep);
            lock (this)
            {
                State.Pending.Remove(core);
                Log(new WatchEvent { Core = core, Kind = "raise", Value = now, Text = Hw.CoreName(core) + ": поднято " + old + " → " + now });
                State.Since = Stamp(DateTime.Now); Save();
            }
            Change((c, v) => c == core ? v + RaiseStep : v, "сторож", done);
            Post();
        }

        public void Skip(int core)
        {
            lock (this) { State.Pending.Remove(core); Save(); }
            Post();
        }

        public void Deepen(Action<bool> done = null)
        {
            lock (this)
            {
                State.DeepenOffer = false;
                Log(new WatchEvent { Kind = "deepen", Text = "все ядра, кроме ошибавшихся, на " + DeepenStep + " глубже" });
                State.Since = Stamp(DateTime.Now); Save();
            }
            var frozen = new HashSet<int>(State.Frozen);
            Change((c, v) => frozen.Contains(c) ? v : v - DeepenStep, "сторож, глубже", done);
            Post();
        }

        // «не сейчас»: the count of quiet days starts over
        public void SkipDeepen()
        {
            lock (this) { State.DeepenOffer = false; State.Since = Stamp(DateTime.Now); Save(); }
            Post();
        }

        // cores that erred may be deepened again: the user's decision (after a BIOS update, a repaste, another power preset)
        public void Forget()
        {
            lock (this) { State.Frozen.Clear(); State.Pending.Clear(); State.DeepenOffer = false; State.Since = Stamp(DateTime.Now); Save(); }
            Post();
        }

        public void Dispose() { timer.Dispose(); }
    }
}
