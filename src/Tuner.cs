using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RedTune
{
    // Saved after every round, so a reset loses nothing: the round that was running counts as failed.
    class TuneState
    {
        public string Mode { get; set; } = "full";      // full = phases 1-3, check = phase 3 on the active preset
        public int Phase { get; set; } = 1;
        public bool Done { get; set; }
        public bool Paused { get; set; }
        public string Failed { get; set; }
        public int[] Cur { get; set; } = new int[Hw.CurveCores];
        public string StartedAt { get; set; }
        public string Round { get; set; }               // set while a round runs: still set after a reset
        public int Rounds { get; set; }
        public int Resets { get; set; }
        public int Wheas { get; set; }
        public List<string> Log { get; set; } = new List<string>();
        public List<string> Raised { get; set; } = new List<string>();
        // phase 1
        public int[] Order { get; set; } = new int[0];
        public int CoreIdx { get; set; }
        public int Value { get; set; } = -25;
        public int StepIdx { get; set; }
        public int LastPass { get; set; } = 1;          // 1 = nothing passed yet
        public bool Confirm { get; set; }
        // phases 2-3
        public int StageIdx { get; set; }
        public string StagePhase { get; set; } = "full";
        public List<int> Suspects { get; set; } = new List<int>();
        public List<int> Proven { get; set; } = new List<int>();
        public List<int> Test { get; set; } = new List<int>();
        public List<int> StageProven { get; set; } = new List<int>();
        public int[] P1Set { get; set; }                // the set phase 1 left: phases 2-3 can be run again from it
        // the quick tune since 18.11 (Ver 2): its second phase works on the whole set (Tuner, «the whole set»)
        public int Ver { get; set; }
        public long Seq { get; set; }                   // grows with every save: the newest whole copy on disk is the one to go on from
        public int[] Edge { get; set; }                 // what phase 1 found for each core, nothing added
        public int[] Base { get; set; }                 // the set as it was before the trial that runs now
        public int[] Gain { get; set; }                 // not used since the Level below (kept for older files)
        public int[] Level { get; set; }                // the set that passed the «set» step, lifted with every core raised since: what a deeper core falls back to
        public string Step { get; set; } = "set";       // set, deep, final
        public int Sub { get; set; }                    // the part of the step's check that runs next
        public int Uni { get; set; }                    // uni: the common value the loaded cores stand at now
        public int Passed { get; set; } = 1;            // uni: the last common value that passed (1: none yet)
        public int Drops { get; set; }                  // uni: failures in a row at this level that named nobody
        public string Kind { get; set; } = "strict";    // 18.11: daily (edges, then the set down with all cores loaded) or strict (the common level with a chain of loads)
        public List<int> Fixed { get; set; } = new List<int>(); // cores that erred at a value of their own: not deepened any more
        public int[] Strikes { get; set; }              // deep: how many times the load or WHEA reported the core in a trial that took it deeper
        public List<TryGroup> Queue { get; set; } = new List<TryGroup>(); // deep: the groups of cores still to be tried deeper
    }

    // cores that are tried deeper together, and how many steps their line has gone already
    class TryGroup
    {
        public List<int> Cores { get; set; } = new List<int>();
        public int Depth { get; set; }
    }

    // one part of the whole-set check
    class Sub
    {
        public string Kind, What;   // idle, bursts, storm, light, heavy, core
        public int Core = -1, Chip = -1, Sec;
        public int[] Cores;         // bursts, heavy: the cores that are loaded (null: all)
    }

    class Stage
    {
        public string Name, What, Load; // Load: ccd1, ccd2, all, storm, browser1, browser2, steps
        public int[] Tune; public bool OthersZero;
        public int Avx, Sse, OnOff, Storm; // minutes
        public int Minutes;                // browser* and steps
    }

    // The one-night plan from the 2026-11 tuning: 1) each core alone, bisect from −25 (4 min steps, 10 min confirm),
    // 2) one CCD loaded while the other idles and back (12-min rounds, a culprit found by halving gets +3),
    // 3) the final set: long rounds A, B, E and a storm. Runs on a background thread inside the app.
    class Tuner
    {
        static readonly string FilePath = Path.Combine(Store.DataDir, "tune.json");
        // the cores of each chiplet of this processor (Hw): 8 + 8 here, one CCD of 4–8 cores on smaller Ryzens
        static int[] Ccd1 { get { return Hw.ChipCores(0); } }
        static int[] Ccd2 { get { return Hw.CurveChips > 1 ? Hw.ChipCores(1) : new int[0]; } }
        static int[] All { get { return Enumerable.Range(0, Hw.CurveCores).ToArray(); } }
        static readonly int[] Steps = { 12, 6, 3 };
        // 16.11: the quick tune (Mode "quick", the page's «Начать»). The full one took 28 hours, and its log shows why:
        // 16 of them went into the 32-minute confirmations, and 18 of their 22 failures came in minutes 24–29, in the last
        // part (clock steps), after 24 minutes of loads that found nothing. So the quick search tests with what does find a
        // weak value: 25 s of Prime95 on the core (its errors come in the first minute and cost no reset), then 60 s of clock
        // steps. Halving from −25 by 13, 6, 3, 2, 1 gives the edge to one step in six probes; the result is the edge plus
        // QuickMargin (a 90-second probe sees the reset edge, the errors that take minutes lie a few steps before it).
        // Then every core together (Quick3): a core named by Prime95 or WHEA gets +3. The long check stays as «Проверить
        // текущую кривую», when the user wants it.
        static readonly int[] QuickSteps = { 13, 6, 3, 2, 1 };
        // 8, not 6: in the full tune's log the value clean for 32 minutes lay 3–6 steps before the one that reset the laptop,
        // so the edge of a 90-second probe plus 6 would land on that value itself, with nothing beyond it
        public const int QuickMargin = 8;
        // 18.11, after the first quick tune on hardware (7 hours with a 4.5-hour hang, 30 resets, no result). What its log shows:
        //  - the edges of phase 1 were fine as edges, but the set «edge + 8» died the moment the load began on all cores, eight
        //    times of eight, and nothing named a core. The search by halving ran its halves with the other cores at 0, and a set
        //    with cores at 0 is another, easier set (less power, a lower clock for everyone), so every half passed and the core
        //    added last took the blame: CCD2 Core 8 went −29 → −17 for nothing.
        //  - how far a core must stay from its edge differs: 8 to 18 steps on this processor, more on the second chiplet. One
        //    margin for all is either too little or wasteful.
        // So the second phase (Ver 2, «the whole set» below) never looks for a culprit among cores it cannot tell apart. It
        // starts from «edge + Level0» on every core and only ever changes one thing against a set that has passed:
        //   set    the whole set through the check (idle, load starting on all cores, storm, light load, heavy load). A core
        //          named by the load or by WHEA goes up RaiseStep; a failure that names nothing lifts every core (or the
        //          chiplet under the light load) LevelStep. The part that failed is repeated, the parts already passed are not:
        //          a set that went up is only safer for them.
        //   deep   all cores DeepStep deeper, again and again, a short check each time. A failure that names nothing takes
        //          the step back and splits the group: the chiplets apart, then every core alone (NextTrial).
        //   final  the whole set through the long check with every core's own part; then FinalMargin on top.
        // A reset while one core was under test counts against that core (the tune knows which part was running).
        // Phase 1 is shorter: three halvings (12, 6, 3) from −24, four probes of about a minute. The edge to a single step
        // bought nothing, the distance to the final value is found by the second phase anyway.
        static readonly int[] QuickSteps2 = { 12, 6, 3 };
        // 18.11 evening, after three runs of the second phase on the 8945HX (reports of that day, sections 6 to 8):
        //  - «edge + 13» as the start was a number fitted to that laptop, and it cost a reset every time. Now the start is
        //    shallow on purpose (Level0 = 22) and the whole set goes down from it, DeepStep at a time, with a short check a
        //    step: how exact the start is no longer matters, a step too many costs a minute and no reset.
        //  - what took the set down were single cores that are weak only when all cores work (CCD2 Core 6: −33 alone, a
        //    wrong result at −17 with all cores loaded), and the own test names them without a reset. So a named core
        //    steps back and the others go on; named a second time, it is left alone (the first report may be chance:
        //    CCD2 Core 2 erred once at −29 and then held −32 through every check).
        //  - the limit of three steps is gone: in the third run 14 cores reached it without one failure. What stops a
        //    core now is a failure, or MinLift: no closer than 6 steps to the edge it showed alone in a one-minute probe
        //    (with FinalMargin the result is at least edge + 9; nothing a run of an hour can show supports less).
        public const int Level0 = 22, LevelStep = 3, FinalLevelStep = 2, MinLift = 6;
        public const int DeepStep = 3, FinalMargin = 3;
        // 18.11: the daily start set is «edge + Level0», but no deeper than StartCap. On the 8945HX the core with the edge −47
        // started at −25, deeper than the −21 all its cores hold as equals; it held only because shallower cores worked
        // beside it. On a processor with deep edges and a shallow common limit such a start would cost resets. From the
        // cap every core still goes down to its own floor, so the shape of the curve is the same; it takes a few steps more.
        public const int StartCap = -12;
        public static int StartOf(int edge) { return edge < 0 ? Math.Min(0, Math.Max(edge + Level0, StartCap)) : 0; }
        public const double QuickCoreMinutes = 4.5; // per core with its resets, for the estimate
        bool Quick { get { return State != null && State.Mode == "quick"; } }
        bool Quick2 { get { return Quick && State.Ver >= 2; } }
        bool SetPhase { get { return Quick2 && State.Phase == 3; } }
        int[] StepList { get { return Quick2 ? QuickSteps2 : Quick ? QuickSteps : Steps; } }
        int StartValue { get { return Quick2 ? -24 : -25; } }
        static Stage[] quick3;
        public static Stage[] Quick3 { get { return quick3 ?? (quick3 = new[]
        {
            new Stage { Name = "E", What = "все ядра, 8 мин", Load = "all", Tune = All, Avx = 5, Sse = 2, OnOff = 1 },
            new Stage { Name = "Шторм", What = "всплески на всех ядрах, 5 мин", Load = "storm", Tune = All, Storm = 5 },
            new Stage { Name = "Фон CCD2", What = "лёгкая нагрузка как видео в браузере на CCD2, 5 мин", Load = "browser2", Tune = Ccd2, Minutes = 5 },
            new Stage { Name = "Фон CCD1", What = "лёгкая нагрузка как видео в браузере на CCD1, 5 мин", Load = "browser1", Tune = Ccd1, Minutes = 5 },
        }.Where(s => Hw.CurveChips >= 2 || s.Load != "browser2").ToArray()); } }
        Stage[] List3 { get { return Quick ? Quick3 : Phase3; } }
        public static double StageMinutes(Stage st) { return st.Load == "storm" ? st.Storm : st.Minutes > 0 ? st.Minutes : st.Avx + st.Sse + st.OnOff; }
        public const int RaiseStep = 3;
        // 14.11: the value that goes into the result is the confirmed edge plus this (≈25 mV at ~5 mV a step). The edge
        // itself kept failing in light use days later: 4- and 10-minute tests pass a core that errs once in a few hours.
        public const int Margin = 5;
        // 13.11: the minutes per step were fitted to 16 cores in one night (phase 1 alone ≈ 7.5 h). A Ryzen with 8 cores
        // or fewer finishes phase 1 in half that, so each core gets longer steps (6 and 15 min instead of 4 and 10):
        // the same night, a curve tested harder. Between 9 and 12 cores the steps are in between.
        public static int P1Minutes { get { return Hw.CurveCores <= 8 ? 6 : Hw.CurveCores <= 12 ? 5 : 4; } }
        // 14.11: the confirmation of a found value is four parts of 8 minutes (ConfirmCore), whatever the core count
        public static int ConfirmMinutes { get { return 32; } }

        // one chiplet: no «one idles, the other works» stages, just the chiplet under load
        static Stage[] phase2, phase3;
        static bool Extra(Stage s) { return s.Load.StartsWith("browser") || s.Load == "steps"; }
        public static Stage[] Phase2 { get { return phase2 ?? (phase2 = Hw.CurveChips >= 2 ? Phase2Two() : new[] { new Stage { Name = "C", What = "CCD1 под нагрузкой", Load = "ccd1", Tune = Ccd1, Avx = 6, Sse = 4, OnOff = 2 } }); } }
        public static Stage[] Phase3 { get { return phase3 ?? (phase3 = Hw.CurveChips >= 2 ? Phase3Two() : Phase3Two().Where(s => s.Load == "all" || s.Load == "storm" || s.Load == "browser1" || s.Load == "steps").ToArray()); } }
        static Stage[] Phase2Two() { return new[]
        {
            new Stage { Name = "A", What = "CCD2 в простое, CCD1 под нагрузкой", Load = "ccd1", Tune = Ccd2, OthersZero = true, Avx = 6, Sse = 4, OnOff = 2 },
            new Stage { Name = "B", What = "CCD1 в простое, CCD2 под нагрузкой", Load = "ccd2", Tune = Ccd1, OthersZero = true, Avx = 6, Sse = 4, OnOff = 2 },
            new Stage { Name = "C", What = "CCD1 под нагрузкой", Load = "ccd1", Tune = Ccd1, Avx = 6, Sse = 4, OnOff = 2 },
            new Stage { Name = "D", What = "CCD2 под нагрузкой", Load = "ccd2", Tune = Ccd2, Avx = 6, Sse = 4, OnOff = 2 },
        }; }
        static Stage[] Phase3Two() { return new[]
        {
            new Stage { Name = "A", What = "CCD2 в простое, 25 мин", Load = "ccd1", Tune = Ccd2, OthersZero = true, Avx = 10, Sse = 10, OnOff = 5 },
            new Stage { Name = "B", What = "CCD1 в простое, 25 мин", Load = "ccd2", Tune = Ccd1, OthersZero = true, Avx = 10, Sse = 10, OnOff = 5 },
            new Stage { Name = "E", What = "все ядра, 25 мин", Load = "all", Tune = All, Avx = 10, Sse = 10, OnOff = 5 },
            new Stage { Name = "Шторм", What = "всплески на всех ядрах, 20 мин", Load = "storm", Tune = All, Storm = 20 },
            // 14.11, after a reset with a video playing in Chrome on CCD2 and another during a core-curve measurement: light
            // background load like a browser playing a video, with the clock cap moving through middle clocks; and one core
            // at a time on clock steps. Added at the end so a tune already under way keeps its stage numbers.
            new Stage { Name = "Фон CCD2", What = "лёгкая нагрузка как видео в браузере на CCD2, 12 мин", Load = "browser2", Tune = Ccd2, Minutes = 12 },
            new Stage { Name = "Фон CCD1", What = "лёгкая нагрузка как видео в браузере на CCD1, 12 мин", Load = "browser1", Tune = Ccd1, Minutes = 12 },
            new Stage { Name = "Ступени", What = "одно ядро на ступенях частоты, все ядра по очереди, 10 мин", Load = "steps", Tune = All, Minutes = 10 },
        }; }

        public TuneState State;
        public string Now = "";          // what runs right now, for the page
        public DateTime SegmentEnd;
        public event Action Changed;
        readonly Settings settings;
        readonly SynchronizationContext ui;
        Thread thread;
        CancellationTokenSource cts;
        public Func<bool> Simulate;      // tests only: the round result without loading anything

        public Tuner(Settings s)
        {
            settings = s;
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            State = LoadState();
        }

        public bool Running { get { return thread != null && thread.IsAlive; } }
        public bool HasRun { get { return State != null && !State.Done; } }

        static TuneState LoadState() { return ReadState(FilePath); }

        // 18.11: the state is what a tune goes on from after a reset, and resets come seconds after a save. Written the plain
        // way, the file was once left full of zeros (14:09: its data had not reached the disk when the laptop went down), and
        // the tune and its edges were gone. So: a new file written straight through to the disk, then put in the place of
        // the old one, which stays as .bak. Reading takes the newest whole one of the three (Seq): if the reset comes
        // before the exchange is on disk, the .new file is there and is whole.
        static TuneState ReadState(string path)
        {
            TuneState best = null;
            foreach (var f in new[] { path + ".new", path, path + ".bak" })
                try
                {
                    if (!File.Exists(f)) continue;
                    var st = new JavaScriptSerializer().Deserialize<TuneState>(File.ReadAllText(f, Encoding.UTF8).TrimStart('\uFEFF'));
                    if (st != null && st.Cur != null && st.Order != null && (best == null || st.Seq > best.Seq)) best = st;
                }
                catch (Exception ex) { Store.Log(Path.GetFileName(f) + ": " + ex.Message); }
            return best;
        }

        static void WriteState(string path, TuneState state)
        {
            state.Seq++;
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(state))).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".new";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            try { if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path); }
            catch (Exception ex) { Store.Log("tune.json: " + ex.Message); } // the .new file stays and is read
        }

        void Save() { if (Simulate != null) return; WriteState(FilePath, State); }

        // --tune-state-dry <folder> <out>: the saving and reading of the state on files of a test folder (a check)
        public static void StateDry(string folder, string outFile)
        {
            var sb = new StringBuilder(); var p = Path.Combine(folder, "tune.json");
            foreach (var f in new[] { p, p + ".new", p + ".bak" }) if (File.Exists(f)) File.Delete(f);
            Func<string> read = () => { var r = ReadState(p); return r == null ? "nothing" : "seq " + r.Seq + " round «" + r.Round + "»"; };
            var st = new TuneState { Mode = "quick", Ver = 2, Order = new[] { 0, 1 }, Cur = new[] { -5, -6 } };
            sb.AppendLine("no files: " + read());
            st.Round = "a"; WriteState(p, st); sb.AppendLine("one save: " + read() + " · files " + string.Join(", ", Directory.GetFiles(folder, "tune.json*").Select(Path.GetFileName)));
            st.Round = "b"; WriteState(p, st); sb.AppendLine("two saves: " + read() + " · files " + string.Join(", ", Directory.GetFiles(folder, "tune.json*").Select(Path.GetFileName)));
            File.WriteAllBytes(p, new byte[1256]); sb.AppendLine("main file zeroed (as on 18.11 14:09): " + read());
            st.Round = "c"; WriteState(p, st); sb.AppendLine("saved over the zeroed file: " + read());
            File.Copy(p, p + ".new"); var txt = File.ReadAllText(p + ".new").Replace("\"Seq\":3", "\"Seq\":9"); File.WriteAllText(p + ".new", txt);
            sb.AppendLine("a whole .new left by a reset (seq 9): " + read());
            File.WriteAllBytes(p + ".new", new byte[300]); sb.AppendLine("a zeroed .new: " + read());
            File.Delete(p); sb.AppendLine("main file gone, zeroed .new, .bak: " + read());
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
        }

        void Note(string text)
        {
            var line = DateTime.Now.ToString("dd.MM HH:mm") + "  " + text;
            State.Log.Add(line);
            if (State.Log.Count > 300) State.Log.RemoveAt(0);
            if (Simulate == null) Store.Log("автотюн: " + text);
            Post();
        }

        void Post() { if (Simulate != null) return; ui.Post(_ => { var h = Changed; if (h != null) h(); }, null); }

        static string CoreName(int c) { return Hw.CoreName(c); }

        // ---------- start / stop ----------

        public void Start(string mode, int[] fromPreset)
        {
            State = new TuneState { Mode = mode, StartedAt = DateTime.Now.ToString("s"), Order = Load.CoreOrder() };
            // 18.11: the quick tune has no phase 1 any more: the descent of the common level ends with the last cores alone (BeginUni)
            // 18.11: two modes (Settings.TuneMode, picked on the page). Six runs on the 8945HX that day came to this: under
            // full load the cores hold one common level (about −21), and deeper values of their own hold only while
            // shallower cores work beside them. A curve that holds with any set of busy cores is flat and shallow (strict);
            // a deep one with a value of its own for every core, like the curve in use, counts on a heavy load taking all
            // cores at once (daily). The user picks.
            if (mode == "quick")
            {
                State.Ver = 2; State.Edge = new int[Hw.CurveCores]; State.Gain = new int[Hw.CurveCores];
                if (settings.TuneMode == "strict") { State.Kind = "strict"; State.CoreIdx = State.Order.Length; BeginUni("Старт подбора (строгий): спуск с проверкой каждого шага"); }
                else { State.Kind = "daily"; State.Value = -24; Note("Старт подбора (повседневный). Фаза 1: край каждого ядра по одному: " + string.Join(", ", State.Order.Select(CoreName))); }
            }
            // 18.11: the second phase of the quick tune again, from the edges a tune already found (fromPreset is those edges here)
            if (mode == "set2")
            {
                State.Mode = "quick"; State.Ver = 2; State.Kind = "daily"; State.CoreIdx = State.Order.Length;
                State.Edge = (int[])fromPreset.Clone(); State.Gain = new int[Hw.CurveCores];
                State.Cur = fromPreset.Select(StartOf).ToArray(); State.P1Set = (int[])State.Cur.Clone();
                BeginDaily("Повседневный подбор от готовых краёв");
            }
            if (mode == "check") { State.Phase = 3; State.Cur = (int[])fromPreset.Clone(); }
            // 16.11: phases 2-3 again from the set phase 1 left (fromPreset is that set here), phase 1 itself is not repeated
            if (mode == "p23") { State.Phase = 2; State.Cur = (int[])fromPreset.Clone(); State.P1Set = (int[])fromPreset.Clone(); }
            if (mode != "set2" && mode != "quick") Note(mode == "check" ? "Проверка текущей кривой: " + Store.Join(State.Cur)
                : mode == "p23" ? "Фазы 2–3 заново от набора после фазы 1: " + Store.Join(State.Cur)
                : mode == "set2" ? "Фаза 2 заново от краёв фазы 1: " + Store.Join(State.Edge) + " · старт «край +" + Level0 + "»: " + Store.Join(State.Cur)
                : mode == "quick" ? "Старт быстрого подбора. Порядок ядер: " + string.Join(", ", State.Order.Select(CoreName))
                : "Старт полного тюнинга. Порядок ядер: " + string.Join(", ", State.Order.Select(CoreName)));
            Save();
            Run(false);
        }

        public void Resume(bool afterBoot)
        {
            if (State == null || State.Done) return;
            State.Paused = false;
            Run(afterBoot);
        }

        void Run(bool afterBoot)
        {
            if (Running) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            thread = new Thread(() => Loop(token, afterBoot)) { IsBackground = true, Name = "auto-tune" };
            thread.Start();
            Post();
        }

        public void Pause()
        {
            if (State != null) { State.Paused = true; Save(); }
            if (cts != null) cts.Cancel();
        }

        public void Discard()
        {
            Pause();
            if (thread != null) thread.Join(30000);
            try { File.Delete(FilePath); } catch { }
            State = null;
            Post();
        }

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        void Loop(CancellationToken stop, bool afterBoot)
        {
            SetThreadExecutionState(0x80000001); // keep the PC awake
            // 14.11: for the whole tune Windows' clock cap is lifted («maximum processor frequency» of the scheme in effect:
            // on the first tune a 4500 MHz cap kept every core out of 4.5–5.45 GHz, so that part of the curve was never
            // tested). Put back when the tune stops; after a reset, at the app's next start (CoreCurve.RestoreIfLeft).
            if (Simulate == null) try { CoreCurve.CapBegin(); CoreCurve.CapSet(0); } catch (Exception ex) { Store.Log("автотюн: потолок частоты Windows не снят: " + ex.Message); }
            try
            {
                if (afterBoot) { Now = Quick ? "Жду 45 с после входа в Windows" : "Жду 90 с после входа в Windows"; Post(); if (stop.WaitHandle.WaitOne(Quick ? 45000 : 90000)) return; }
                Upgrade();
                wheaFrom = DateTime.Now;
                int[] written = null; // what the processor holds now, when the last round left its set there
                if (State.Round != null)
                {
                    State.Resets++;
                    Note("Перезагрузка во время раунда: " + State.Round);
                    if (BlackBox.LastReport != null) Note("Чёрный ящик перед перезагрузкой: " + BlackBox.LastReport);
                    Record(false, "перезагрузка");
                    State.Round = null; Save();
                }
                // 18.11: in the whole-set phase the set stays on the processor between the parts of the check, so a reset that
                // came between two parts (or one whose round was not on disk yet) is the set's too: it counts against the part
                // that was to run next. Otherwise the same part would start again on the same set, reset after reset.
                else if (afterBoot && SetPhase && BlackBox.LastReport != null)
                {
                    State.Resets++;
                    Note("Перезагрузка между частями проверки набора");
                    Note("Чёрный ящик перед перезагрузкой: " + BlackBox.LastReport);
                    lastBad = new List<int>();
                    Record(false, "перезагрузка");
                    Save();
                }
                while (!State.Done && !stop.IsCancellationRequested)
                {
                    int[] values; string desc; Func<CancellationToken, string> body;
                    Plan(out values, out desc, out body);
                    if (State.Done) break;
                    State.Round = desc; State.Rounds++; Save();
                    lastBad = new List<int>(); roundValues = values;
                    Note("Раунд: " + desc + " · " + Hw.Describe(values));
                    string why;
                    if (Simulate != null) why = Simulate() ? null : "имитация";
                    else
                    {
                        // 18.11: the parts of the whole-set check follow one another on the same set, as in use: the set is
                        // not taken off between them and is written again only when it changed
                        bool same = written != null && written.SequenceEqual(values);
                        // WHEA from before this write belongs to the set that stood before it (18.11: two events a second apart,
                        // the second came into the log after the core had been raised for the first, and raised it again)
                        if (!same) { Smu.Write(values); written = (int[])values.Clone(); wheaFrom = DateTime.Now; Thread.Sleep(3000); }
                        why = body(stop);
                        Load.StopAll();
                        if (!SetPhase) { try { Smu.Write(new int[Hw.CurveCores]); } catch (Exception ex) { Store.Quiet("автотюн: ядра на 0", ex); } written = null; }
                    }
                    if (stop.IsCancellationRequested && why == null) { State.Round = null; Save(); break; } // paused mid-round: the round doesn't count
                    if (why != null && why.StartsWith("WHEA")) State.Wheas++;
                    bool held = SetPhase;
                    Record(why == null, why);
                    State.Round = null; Save();
                    Note(why == null ? "Пройден" : "Не пройден: " + why);
                    if (Simulate == null && held && (!SetPhase || State.Done)) { try { Smu.Write(new int[Hw.CurveCores]); } catch (Exception ex) { Store.Quiet("автотюн: ядра на 0", ex); } written = null; }
                    if (Simulate == null && !State.Done) stop.WaitHandle.WaitOne(held && why == null ? 1500 : Quick && State.Phase == 1 ? (Quick2 ? 5000 : 8000) : held ? 8000 : 30000); // cool down
                }
                if (State.Done) Finish();
            }
            catch (Exception ex) { Note("Ошибка: " + ex.Message); State.Paused = true; Save(); }
            finally
            {
                Load.StopAll();
                if (Simulate == null)
                {
                    try { Smu.Write(ActiveValues()); } catch (Exception ex) { Store.Quiet("автотюн: возврат активной кривой", ex); }
                    SetThreadExecutionState(0x80000000);
                    CoreCurve.CapEnd();
                }
                Now = State != null && State.Done ? "Готово" : "Остановлено";
                Post();
            }
        }

        // the cores Prime95 named in the round that just ran, and the values that round wrote
        List<int> lastBad = new List<int>();
        int[] roundValues;

        // the set phase 1 left: kept in tune.json since 16.11, for a tune before that read from the log line
        // «Фаза 2: чиплеты по очереди. Набор после фазы 1: …»
        public static int[] Phase1Set(TuneState s)
        {
            if (s != null && s.P1Set != null && s.P1Set.Length == Hw.CurveCores) return s.P1Set;
            try
            {
                const string mark = "Набор после фазы 1: ";
                var f = Path.Combine(Store.DataDir, "redtune.log");
                if (!File.Exists(f)) return null;
                var line = File.ReadAllLines(f, Encoding.UTF8).LastOrDefault(l => l.Contains(mark));
                if (line == null) return null;
                var v = line.Substring(line.IndexOf(mark) + mark.Length).Split(',').Select(x => int.Parse(x.Trim())).ToArray();
                return v.Length == Hw.CurveCores && v.All(x => x <= 0 && x >= -50) ? v : null;
            }
            catch { return null; }
        }

        public Func<int[]> ActiveValues = () => new int[Hw.CurveCores];
        public Action<int[]> Finished;

        void Finish()
        {
            Note(State.Failed != null ? "Остановлено: " + State.Failed : "Готово. Итог: " + Hw.Describe(State.Cur));
            Save();
            if (State.Failed == null && Finished != null) { var v = (int[])State.Cur.Clone(); ui.Post(_ => Finished(v), null); }
        }

        // ---------- what the next round is ----------

        Stage CurStage { get { return (State.Phase == 2 ? Phase2 : List3)[State.StageIdx]; } }

        void Plan(out int[] values, out string desc, out Func<CancellationToken, string> body)
        {
            values = new int[Hw.CurveCores]; desc = ""; body = null;
            if (State.Phase == 1)
            {
                int c = State.Order[State.CoreIdx];
                values[c] = State.Value;
                int minutes = State.Confirm ? ConfirmMinutes : P1Minutes;
                desc = "фаза 1 · " + CoreName(c) + " · " + (State.Confirm ? "проверка " + State.Value : "шаг " + (State.StepIdx + 1) + " из " + (StepList.Length + 1) + ": " + State.Value);
                bool confirm = State.Confirm, quick = Quick;
                body = t => quick ? QuickProbe(c, t) : confirm ? ConfirmCore(c, t) : SingleCore(c, minutes, t);
                return;
            }
            if (SetPhase) { PlanSet(out values, out desc, out body); return; }
            var st = CurStage;
            if (State.StagePhase == "bisect") { var s = State.Suspects; State.Test = s.Take((s.Count + 1) / 2).ToList(); }
            else State.Test = new List<int>();
            var deep = State.StagePhase == "bisect" ? State.Proven.Concat(State.Test).ToList() : st.Tune.ToList();
            for (int i = 0; i < Hw.CurveCores; i++)
                values[i] = st.Tune.Contains(i) ? (deep.Contains(i) ? State.Cur[i] : 0) : st.OthersZero ? 0 : State.Cur[i];
            desc = "фаза " + (Quick ? 2 : State.Phase) + " · этап " + st.Name + " (" + st.What + ")" + (State.StagePhase == "bisect" ? " · поиск: " + string.Join(", ", State.Test.Select(CoreName)) : "");
            var stage = st; bool bisect = State.StagePhase == "bisect";
            // the search for a weak core uses 12-minute rounds, only the full check of a stage runs at full length
            if (bisect && !Quick && stage.Load != "storm" && !Extra(stage)) stage = new Stage { Name = stage.Name, What = stage.What, Load = stage.Load, Tune = stage.Tune, OthersZero = stage.OthersZero, Avx = 6, Sse = 4, OnOff = 2 };
            int extraMin = bisect ? Math.Max(5, stage.Minutes / 2) : stage.Minutes;
            body = t => stage.Load == "storm" ? StormRound(bisect ? Math.Min(10, stage.Storm) : stage.Storm, t)
                : stage.Load.StartsWith("browser") ? BrowserRound(stage, extraMin, t)
                : stage.Load == "steps" ? StepsRound(stage, extraMin, t)
                : CcdRound(stage, t);
        }

        // ---------- recording a result ----------

        void Record(bool ok, string why)
        {
            if (State.Phase == 1) { RecordCore(ok); return; }
            if (SetPhase) { RecordSet(ok, why); return; }
            var st = CurStage;
            // 16.11: when Prime95 names the core whose result went wrong and that core ran on its own value this round, that
            // core gets +3 and the stage starts over. Before, the halving search ignored the name and blamed the half under
            // test: on 15.11 Prime95 reported CCD1 Core 8 five times and CCD2 Core 8 went −20 → −8 instead (it helped only
            // because all cores share one voltage, and the cores that erred stayed on the edge). The search by halving is
            // left for what names no core: a reset, WHEA, or an error on a core that ran at 0 this round.
            var named = ok || roundValues == null ? new List<int>() : lastBad.Distinct().Where(i => i >= 0 && i < roundValues.Length && roundValues[i] < 0 && st.Tune.Contains(i)).ToList();
            if (named.Count > 0)
            {
                foreach (var c in named)
                {
                    int old = State.Cur[c];
                    State.Cur[c] = Math.Min(0, old + RaiseStep);
                    State.Raised.Add(st.Name + ": " + CoreName(c) + " " + old + " → " + State.Cur[c]);
                    Note("Ошибка на самом ядре: " + CoreName(c) + " " + old + " → " + State.Cur[c]);
                }
                State.StageProven = State.StageProven.Where(i => !named.Contains(i)).ToList();
                State.StagePhase = "full"; State.Suspects = new List<int>(); State.Proven = new List<int>(); State.Test = new List<int>();
                return;
            }
            if (State.StagePhase == "full")
            {
                if (ok) { Note("Этап " + st.Name + " пройден"); NextStage(); return; }
                var keep = State.StageProven.Where(i => State.Cur[i] < 0).ToList();
                State.Suspects = st.Tune.Where(i => State.Cur[i] < 0 && !keep.Contains(i)).ToList();
                State.Proven = keep;
                if (State.Suspects.Count == 0 && keep.Count > 0) { State.Suspects = keep; State.Proven = new List<int>(); State.StageProven = new List<int>(); }
                if (State.Suspects.Count == 0) { State.Failed = "этап " + st.Name + " не проходит даже на нулях — дело не в кривой"; State.Done = true; return; }
                State.StagePhase = "bisect";
            }
            else if (ok)
            {
                State.Proven = State.Proven.Concat(State.Test).ToList();
                State.StageProven = State.Proven.ToList();
                State.Suspects = State.Suspects.Where(i => !State.Test.Contains(i)).ToList();
            }
            else State.Suspects = State.Test.ToList();
            if (State.Suspects.Count <= 1)
            {
                if (State.Suspects.Count == 1)
                {
                    int c = State.Suspects[0], old = State.Cur[c];
                    State.Cur[c] = Math.Min(0, old + RaiseStep);
                    State.Raised.Add(st.Name + ": " + CoreName(c) + " " + old + " → " + State.Cur[c]);
                    Note("Слабое ядро: " + CoreName(c) + " " + old + " → " + State.Cur[c]);
                }
                State.StagePhase = "full"; State.Suspects = new List<int>(); State.Proven = new List<int>(); State.Test = new List<int>();
            }
        }

        void NextStage()
        {
            State.StageIdx++;
            State.StagePhase = "full"; State.Suspects = new List<int>(); State.Proven = new List<int>(); State.Test = new List<int>(); State.StageProven = new List<int>();
            var list = State.Phase == 2 ? Phase2 : List3;
            if (State.StageIdx < list.Length) return;
            if (State.Phase == 2) { State.Phase = 3; State.StageIdx = 0; Note("Фаза 3: итоговая проверка"); }
            else State.Done = true;
        }

        void RecordCore(bool ok)
        {
            int c = State.Order[State.CoreIdx];
            if (State.Confirm)
            {
                if (ok || State.Value >= 0)
                {
                    State.Cur[c] = Math.Min(0, State.Value + (State.Value < 0 ? Margin : 0));
                    Note(CoreName(c) + ": край " + State.Value + ", с запасом +" + Margin + " итог " + State.Cur[c]);
                    NextCore(); return;
                }
                State.Value = Math.Min(0, State.Value + RaiseStep);
                return;
            }
            if (ok) State.LastPass = State.Value;
            if (State.StepIdx < StepList.Length)
            {
                State.Value = Math.Max(-50, Math.Min(0, State.Value + (ok ? -StepList[State.StepIdx] : StepList[State.StepIdx])));
                State.StepIdx++;
                return;
            }
            State.Value = State.LastPass <= 0 ? State.LastPass : 0;
            if (State.Value >= 0) { State.Cur[c] = 0; Note(CoreName(c) + ": итог 0"); NextCore(); return; }
            if (Quick2)
            {
                // the edge as it is; what is added to it is found by the second phase
                State.Edge[c] = State.Value; State.Cur[c] = StartOf(State.Value);
                Note(CoreName(c) + ": край " + State.Value);
                NextCore(); return;
            }
            if (Quick)
            {
                // no long confirmation: the edge is known to one step, the margin and the all-core stages do the rest
                State.Cur[c] = Math.Min(0, State.Value + QuickMargin);
                Note(CoreName(c) + ": край " + State.Value + ", с запасом +" + QuickMargin + " итог " + State.Cur[c]);
                NextCore(); return;
            }
            State.Confirm = true;
        }

        void NextCore()
        {
            State.CoreIdx++;
            State.Value = StartValue; State.StepIdx = 0; State.LastPass = 1; State.Confirm = false;
            if (State.CoreIdx < State.Order.Length) return;
            State.StageIdx = 0; State.P1Set = (int[])State.Cur.Clone();
            if (Quick2) { if (State.Kind == "daily") BeginDaily("Фаза 2: спуск всем набором"); else BeginUni("Фаза 2: собственный предел каждого ядра под нагрузкой"); }
            else if (Quick) { State.Phase = 3; Note("Фаза 2: все ядра вместе. Набор после фазы 1: " + Store.Join(State.Cur)); }
            else { State.Phase = 2; Note("Фаза 2: чиплеты по очереди. Набор после фазы 1: " + Store.Join(State.Cur)); }
        }

        // ---------- the quick tune's second phase (18.11): the whole set ----------

        // a quick tune begun before 18.11 goes on by the new second phase: its edges are what phase 1 left minus the old margin
        void Upgrade()
        {
            if (!Quick || State.Ver >= 2) return;
            int n = Hw.CurveCores;
            State.Edge = new int[n]; State.Gain = new int[n]; State.Fixed = new List<int>(); State.Queue = new List<TryGroup>();
            var p1 = State.P1Set != null && State.P1Set.Length == n ? State.P1Set : State.Cur;
            int done = State.Phase == 1 ? State.CoreIdx : State.Order.Length;
            for (int k = 0; k < done && k < State.Order.Length; k++)
            {
                int c = State.Order[k];
                State.Edge[c] = p1[c] < 0 ? Math.Max(-50, p1[c] - QuickMargin) : 0;
                State.Cur[c] = Math.Min(0, State.Edge[c] + (State.Edge[c] < 0 ? Level0 : 0));
            }
            State.Ver = 2;
            if (State.Phase == 1) { State.Value = StartValue; State.StepIdx = 0; State.LastPass = 1; State.Confirm = false; }
            else
            {
                State.Round = null;
                State.StagePhase = "full"; State.Suspects = new List<int>(); State.Proven = new List<int>(); State.Test = new List<int>(); State.StageProven = new List<int>();
                State.Kind = "strict"; BeginUni("Вторая фаза по-новому, от краёв прошлой фазы 1");
            }
            Save();
        }

        // the edges a quick tune found, once its phase 1 is behind: the second phase can be run again from them (Start, set2)
        public static int[] Edges(TuneState s)
        {
            if (s != null && !s.Done && !(s.Mode == "quick" && s.Phase == 3)) return null; // a tune of another kind is under way
            if (s != null && s.Mode == "quick" && s.Ver >= 2 && s.Phase == 3 && s.Edge != null && s.Edge.Length == Hw.CurveCores && s.Edge.Any(e => e < 0)) return s.Edge;
            return EdgesFromLog();
        }

        // the edges of the last quick tune as its notes in the log give them («… Края: −33,… · старт «край +13»: …»): a state
        // file that was lost (18.11 14:09) need not cost phase 1 again
        static int[] EdgesFromLog()
        {
            try
            {
                var f = Path.Combine(Store.DataDir, "redtune.log");
                var rx = new System.Text.RegularExpressions.Regex(@"(?:Края|фазы 1): (-?\d+(?:,-?\d+)*) · старт");
                // the newest note first: the log, then the part of it that was put aside when it grew past its size
                foreach (var line in new[] { f, f + ".old" }.Where(File.Exists).SelectMany(x => File.ReadAllLines(x, Encoding.UTF8).Reverse()))
                {
                    var m = rx.Match(line);
                    if (!m.Success) continue;
                    var v = m.Groups[1].Value.Split(',').Select(int.Parse).ToArray();
                    if (v.Length == Hw.CurveCores && v.All(x => x <= 0 && x >= -50) && v.Any(x => x < 0)) return v;
                    if (v.Length == Hw.CurveCores) continue; // a note of a tune without edges (all zeros): an older one may have them
                    return null;
                }
            }
            catch { }
            return null;
        }

        static string ChipName(int chip) { return "CCD" + (chip + 1); }

        // the parts of a step's check, in the order they run. Every part is on the whole set; «core» loads one core of it.
        // changed: the cores a trial of the «deep» step took deeper (one core: that core's own part; several: their chiplets' light load)
        static List<Sub> SubsFor(string step, List<int> changed, int[] order, int[] cur = null)
        {
            var l = new List<Sub>();
            Action<int> idle = sec => l.Add(new Sub { Kind = "idle", Sec = sec, What = "простой " + sec + " с" });
            Action<int> bursts = sec => l.Add(new Sub { Kind = "bursts", Sec = sec, What = "старт нагрузки на всех ядрах, рывками " + sec + " с" });
            Action<int> storm = sec => l.Add(new Sub { Kind = "storm", Sec = sec, What = "шторм всплесков " + sec + " с" });
            Action<int, int> light = (chip, sec) => l.Add(new Sub { Kind = "light", Chip = chip, Sec = sec, What = "лёгкий фон на " + ChipName(chip) + " " + sec + " с" });
            Action<int> heavy = sec => l.Add(new Sub { Kind = "heavy", Sec = sec, What = "нагрузка на всех ядрах " + sec + " с" });
            Action<int> core = c => l.Add(new Sub { Kind = "core", Core = c, Sec = CoreSubSec, What = Hw.CoreName(c) + ": нагрузка, ступени частоты, пробуждения" });
            int chips = Hw.CurveChips;
            if (step == "uni")
            {
                // changed: here the cores that still go down. The stopped ones (the rest) stand at their own, shallower values.
                var down = (changed ?? order.ToList()).OrderBy(i => i).ToArray();
                var every = Enumerable.Range(0, Hw.CurveCores).ToArray();
                idle(6);
                // all cores together: the stopped ones work too, as they will in use
                bursts(20); heavy(40);
                // then the same values without the stopped cores, the shallowest first: without 1, 2, 4… of them, and at last
                // the descending cores alone. The run of 18:05 passed both ends of this chain and fell in the middle of it.
                var stopped = every.Where(i => !down.Contains(i)).OrderByDescending(i => cur != null && i < cur.Length ? cur[i] : 0).ThenBy(i => i).ToList();
                if (stopped.Count > 0 && down.Length > 0)
                {
                    var ks = new List<int>(); for (int k = 1; k < stopped.Count; k *= 2) ks.Add(k);
                    ks.Add(stopped.Count);
                    foreach (int k in ks.Distinct())
                    {
                        var cores = every.Where(i => !stopped.Take(k).Contains(i)).ToArray();
                        l.Add(new Sub { Kind = "heavy", Sec = 30, Cores = cores, What = (k == stopped.Count ? "нагрузка только на спускающихся ядрах" : "нагрузка без " + k + " самых мелких ядер") + " (" + cores.Length + ") 30 с" });
                    }
                }
                // the last cores work nearly alone, at the top clocks: there the clock steps count
                if (down.Length <= 2) l.Add(new Sub { Kind = "steps", Sec = 14 * down.Length, Cores = down, What = "ступени частоты на " + down.Length + " ядрах" });
            }
            else if (step == "deep")
            {
                changed = changed ?? new List<int>();
                // 18.11: with the all-core load running on (heavy). The first run on hardware took every trial through
                // starts of 6 s only; the set they left died 21 s into the 90 s of the final check, and nobody knew who.
                if (changed.Count == 1) { idle(6); core(changed[0]); bursts(20); heavy(30); idle(6); }
                // cores of more than one chiplet: a step of the descent of the whole set. What failed in every run so far
                // was the load starting on all cores and the load running on; the light load and the storm come in the
                // chiplets' own trials and in the final check.
                else if (changed.Select(Hw.ChipOf).Distinct().Count() > 1) { idle(6); bursts(20); heavy(40); idle(6); }
                else
                {
                    idle(8); bursts(20); heavy(40);
                    foreach (int ch in changed.Select(Hw.ChipOf).Distinct().OrderBy(x => x)) light(ch, 25);
                    storm(20); idle(8);
                }
            }
            else if (step == "final")
            {
                idle(10); bursts(30); storm(40);
                for (int ch = 0; ch < chips; ch++) light(ch, 40);
                heavy(90);
                // 18.11: the load without the shallowest cores. Near all-core load the shallow cores hold the voltage up for the
                // rest (measured that day: the stable curve erred within 17 s with its two shallow cores idle), so the set is
                // also loaded without them.
                if (cur != null && cur.Length == Hw.CurveCores && Hw.CurveCores >= 4)
                {
                    var shallowFirst = Enumerable.Range(0, cur.Length).OrderByDescending(i => cur[i]).ThenBy(i => i).ToList();
                    // every step of the staircase: without the 1, 2, 4, 8… shallowest cores and without three quarters of them
                    var ks = new List<int>(); for (int k = 1; k < cur.Length; k *= 2) ks.Add(k);
                    if (cur.Length * 3 / 4 < cur.Length) ks.Add(cur.Length * 3 / 4);
                    foreach (int k in ks.Distinct().OrderBy(x => x))
                        l.Add(new Sub { Kind = "heavy", Sec = 40, Cores = shallowFirst.Skip(k).OrderBy(i => i).ToArray(), What = "нагрузка без " + k + " самых мелких ядер 40 с" });
                }
                foreach (int c in order) core(c);
                idle(30);
            }
            else
            {
                // the shallow set the descent starts from: only that it holds at all
                idle(8); bursts(20); heavy(30); idle(5);
            }
            return l;
        }

        const int CoreSubSec = 6 + 14 + 8; // the heavy load, one sweep of clock steps, wake-ups

        // the cores the running trial took deeper than the set that passed
        List<int> Deepened()
        {
            if (State.Step != "deep" || State.Base == null || State.Base.Length != State.Cur.Length) return new List<int>();
            return Enumerable.Range(0, State.Cur.Length).Where(i => State.Cur[i] != State.Base[i]).ToList();
        }

        List<Sub> Subs() { return SubsFor(State.Step, State.Step == "uni" ? Active() : Deepened(), State.Order, State.Kind == "daily" ? null : State.Cur); }

        // «CCD1», «CCD2 Core 3», «CCD1 Core 1, 2, 5»
        static string GroupLabel(List<int> cores)
        {
            if (cores.Count == 0) return "—";
            if (cores.Count == Hw.CurveCores && Hw.CurveCores > 1) return "все ядра";
            var parts = new List<string>();
            foreach (var g in cores.GroupBy(Hw.ChipOf).OrderBy(g => g.Key))
            {
                var all = Hw.ChipCores(g.Key);
                if (g.Count() == all.Length && all.Length > 1) parts.Add(ChipName(g.Key));
                else parts.Add(ChipName(g.Key) + " Core " + string.Join(", ", g.OrderBy(c => c).Select(c => c - g.Key * Hw.PerChip + 1)));
            }
            return string.Join(" · ", parts);
        }

        string StepName
        {
            get
            {
                if (State.Step == "deep") return "проба глубже на " + DeepStep + ": " + GroupLabel(Deepened());
                if (State.Step == "uni") return "уровень " + State.Uni + " · спускаются " + Active().Count + " из " + State.Cur.Length;
                return State.Step == "final" ? "итоговая проверка" : "весь набор";
            }
        }

        void PlanSet(out int[] values, out string desc, out Func<CancellationToken, string> body)
        {
            var subs = Subs();
            if (State.Sub >= subs.Count) State.Sub = subs.Count - 1;
            var sub = subs[State.Sub];
            values = (int[])State.Cur.Clone();
            desc = "подбор · " + StepName + " · " + (State.Sub + 1) + " из " + subs.Count + ": " + sub.What;
            body = t => RunSub(sub, t);
        }

        void Lift(int c, int by, string because)
        {
            int old = State.Cur[c];
            State.Cur[c] = Math.Min(0, old + by);
            if (State.Base != null && State.Base.Length == State.Cur.Length) State.Base[c] = Math.Max(State.Base[c], State.Cur[c]);
            if (State.Level != null && State.Level.Length == State.Cur.Length) State.Level[c] = Math.Max(State.Level[c], State.Cur[c]);
            State.Raised.Add(because + ": " + CoreName(c) + " " + old + " → " + State.Cur[c]);
        }

        void Fix(int c) { if (!State.Fixed.Contains(c)) State.Fixed.Add(c); }

        static string Who(Sub s) { return s.Cores == null || s.Cores.Length == Hw.CurveCores ? "всех ядрах" : s.Cores.Length + " ядрах"; }

        // ---------- the «uni» step (18.11): the staircase of the cores' own limits ----------
        // What the day's runs and one measurement showed: the cores share one voltage, and under load it is the loaded core
        // with the shallowest value that holds it up; the others ride on it. Two runs gave two different curves for the
        // second chiplet (whoever erred first was raised, then the rest passed deep), and the curve in use for days erred
        // within 17 s once its two shallow cores were left idle under a 14-core load.
        // So a core shows its own limit only while no loaded core is shallower than it, and the tune is built on that:
        // the loaded cores all stand at one common value, from UniStart down, DeepStep at a time, with a short check a level
        // (idle, the load starting, 40 s of it running; clock steps too once one or two cores are left). A core the load or
        // WHEA reports gets the last value that passed as its limit and LEAVES the load (an idle core holds nobody:
        // measured), and the rest go deeper without it. The weakest core thus gets its limit with all cores loaded, the next
        // one with one core fewer, the last one alone: a staircase, a value of its own for every core. It is safe for any
        // mix of busy cores, because the shallowest of them was checked at that value with at least as many cores loaded
        // (fewer loaded cores mean a higher voltage: 0.86–0.96 V with 5–8 cores against 0.80 V with all, measured). A run
        // of 17:11 stopped the descent when a quarter of the cores had left and gave the rest one level («cut by a ruler»);
        // that stop is gone.
        // A failure that names nobody (a reset): one core of those still loaded, the last in the order of preference, stays
        // at the last value that passed and holds the voltage for that step from then on; the others try the same level
        // again without it. No deeper than UniMin.
        // 18.11 night. The run of that staircase (18:05): the descent was fine (30 min, 12 of 16 cores named), the final check
        // was not (17 resets). What it showed: 14 cores at one value −21 hold; the same 14 at −19…−42 reset the laptop until
        // one of them stood at −7. A deeper core does not ride for free on a shallower one, and a value found with few
        // cores loaded does not hold with more. So a level now counts only when a chain of loads has passed at it: all
        // cores (the stopped ones work too), then without the 1, 2, 4… shallowest stopped cores, then the descending cores
        // alone. A core that errs goes back to the last value that passed and stops there, but stays in the all-core
        // load. A failure that names nobody: every core still descending takes the last value that passed, and the descent
        // is over — no core is left to hold a step, and nothing is repaired afterwards by resets.
        public const int UniStart = -6, UniMin = -45;
        List<int> Active() { return Enumerable.Range(0, State.Cur.Length).Where(i => !State.Fixed.Contains(i)).ToList(); }

        // the daily mode's second phase: the set «edge + Level0» is checked, then it goes down with all cores loaded (the
        // «set» and «deep» steps below; the edges are in State.Edge, the start set in State.Cur)
        void BeginDaily(string how)
        {
            State.Phase = 3; State.Step = "set"; State.Sub = 0;
            State.Fixed = new List<int>(); State.Queue = new List<TryGroup>(); State.Base = null; State.Level = null; State.Strikes = null;
            Note(how + ". Края: " + Store.Join(State.Edge) + " · старт «край +" + Level0 + "», не глубже " + StartCap + ": " + Store.Join(State.Cur));
        }

        void BeginUni(string how)
        {
            int n = Hw.CurveCores;
            State.Phase = 3; State.Step = "uni"; State.Sub = 0; State.Uni = UniStart; State.Passed = 1;
            State.Fixed = new List<int>(); State.Queue = new List<TryGroup>(); State.Base = null; State.Level = null;
            if (State.Gain == null || State.Gain.Length != n) State.Gain = new int[n];
            if (State.Edge == null || State.Edge.Length != n) State.Edge = new int[n];
            State.Cur = Enumerable.Repeat(UniStart, n).ToArray();
            Note(how + " · старт: все ядра на " + UniStart);
        }

        void EndUni(string why)
        {
            State.Step = "final"; State.Sub = 0; State.Base = null; State.Level = null;
            Note("Спуск окончен" + (why == null ? "" : " (" + why + ")") + ": " + Store.Join(State.Cur) + " · дальше итоговая проверка");
        }

        void RecordUni(bool ok, string why, Sub sub, int parts)
        {
            var active = Active(); int n = State.Cur.Length;
            if (ok)
            {
                State.Sub++;
                if (State.Sub < parts) return;
                State.Passed = State.Uni; State.Sub = 0; State.Drops = 0;
                Note("Уровень " + State.Uni + " держится, спускаются " + active.Count + " из " + n);
                int next = State.Uni - DeepStep;
                if (next < UniMin) { EndUni("глубже " + UniMin + " подбор не идёт"); return; }
                State.Uni = next;
                foreach (int c in active) State.Cur[c] = next;
                return;
            }
            var named = lastBad.Distinct().Where(i => i >= 0 && i < n).ToList();
            var mine = named.Where(active.Contains).ToList();
            int back = State.Passed <= 0 ? State.Passed : Math.Min(0, State.Uni + DeepStep);
            // the only core that still descends, alone in the load: a reset is its own
            if (mine.Count == 0 && active.Count == 1 && State.Passed <= 0 && sub.Cores != null && sub.Cores.Length == 1) mine.Add(active[0]);
            if (mine.Count > 0)
            {
                foreach (int c in mine)
                {
                    Note(CoreName(c) + ": не держит " + State.Cur[c] + " (" + why + ", «" + sub.What + "»), останавливается на " + back);
                    State.Raised.Add("предел: " + CoreName(c) + " " + State.Cur[c] + " → " + back);
                    State.Cur[c] = back; Fix(c);
                }
                if (Active().Count == 0) { EndUni(null); return; }
                // the all-core parts are only safer with a core gone up; the chain without the stopped cores has changed and starts again
                State.Sub = Math.Min(State.Sub, 3);
                return;
            }
            // a core that had stopped already is reported: a step up, and the chain again
            var others = named.Where(c => !active.Contains(c) && State.Cur[c] < 0).ToList();
            if (others.Count > 0)
            {
                foreach (int c in others) { int old = State.Cur[c]; Lift(c, RaiseStep, "уровень"); Note("Ошибка на остановившемся ядре: " + CoreName(c) + " " + old + " → " + State.Cur[c]); }
                State.Sub = Math.Min(State.Sub, 3);
                return;
            }
            // nobody is named
            if (State.Passed > 0)
            {
                // not even the start holds: everyone a step up and again
                if (State.Uni >= 0) { State.Failed = "нагрузка не проходит даже на нулях — дело не в кривой"; State.Done = true; return; }
                State.Uni = Math.Min(0, State.Uni + DeepStep); State.Sub = 0;
                foreach (int c in active) State.Cur[c] = State.Uni;
                Note("Сбой без имени ядра на стартовом уровне (" + why + "): старт с " + State.Uni);
                return;
            }
            foreach (int c in active) State.Cur[c] = State.Passed;
            Note("Сбой без имени ядра на уровне " + State.Uni + " (" + why + ", «" + sub.What + "»): спускавшиеся ядра (" + active.Count + ") встают на последний пройденный уровень " + State.Passed);
            EndUni(null);
        }

        // The «deep» step: the shallow start holds, now the descent. Groups of cores wait in a queue, first all cores as one.
        // A trial takes the first group DeepStep deeper against the set that passed (Base) and runs a short check. Passed:
        // kept, and the same group again, down to MinLift above the edges. Failed with a core reported by the load or WHEA:
        // that core steps back and the rest go on; reported a second time it is left alone. Failed with nothing named: all
        // back, and the group is split, all cores into chiplets, a chiplet into single cores, each tried alone with its
        // own part in the check; a single core that fails stays where it was. Always against a set that is
        // known to hold, with only the cores under trial changed: that is what the old search by halving lacked (its
        // untested half stood at 0, another set altogether). One by one and not in halves: when most cores of a chiplet
        // are at their limit, as on the second chiplet of the 8945HX, halves cost 13 resets a chiplet, singles 9.
        void NextTrial()
        {
            while (State.Step == "deep")
            {
                if (State.Queue == null || State.Queue.Count == 0)
                {
                    State.Step = "final"; State.Sub = 0; State.Base = null;
                    Note("Итоговая проверка набора: " + Store.Join(State.Cur));
                    return;
                }
                var g = State.Queue[0];
                var next = (int[])State.Cur.Clone(); bool any = false;
                foreach (int c in g.Cores)
                {
                    if (c < 0 || c >= next.Length || State.Fixed.Contains(c) || State.Cur[c] >= 0) continue;
                    int v = State.Cur[c] - DeepStep; // a whole step or none: a last step of one or two would risk a reset for next to nothing
                    if (v >= State.Edge[c] + MinLift) { next[c] = v; any = true; }
                }
                if (!any) { State.Queue.RemoveAt(0); continue; }
                State.Base = (int[])State.Cur.Clone(); State.Cur = next; State.Sub = 0;
                return;
            }
        }

        void RecordSet(bool ok, string why)
        {
            var subs = Subs();
            var sub = subs[Math.Min(State.Sub, subs.Count - 1)];
            if (State.Gain == null || State.Gain.Length != Hw.CurveCores) State.Gain = new int[Hw.CurveCores];
            if (State.Step == "uni") { RecordUni(ok, why, sub, subs.Count); return; }
            if (ok)
            {
                State.Sub++;
                if (State.Sub < subs.Count) return;
                // the step's check is through
                if (State.Step == "set")
                {
                    Note("Стартовый набор держится: " + Store.Join(State.Cur));
                    State.Level = (int[])State.Cur.Clone();
                    State.Step = "deep"; State.Strikes = new int[Hw.CurveCores];
                    State.Queue = new List<TryGroup> { new TryGroup { Cores = State.Order.ToList() } };
                    NextTrial();
                }
                else if (State.Step == "deep")
                {
                    var changed = Deepened();
                    Note(GroupLabel(changed) + " глубже на " + DeepStep + " держится: " + Store.Join(State.Cur));
                    if (State.Queue.Count > 0) State.Queue[0].Depth++;
                    NextTrial();
                }
                else
                {
                    var passed = Store.Join(State.Cur);
                    for (int i = 0; i < State.Cur.Length; i++) if (State.Cur[i] < 0) State.Cur[i] = Math.Min(0, State.Cur[i] + FinalMargin);
                    Note("Набор прошёл итоговую проверку: " + passed + " · с запасом +" + FinalMargin + " итог " + Store.Join(State.Cur));
                    State.Done = true;
                }
                return;
            }

            // who is named: the cores the load or WHEA reported; failing that, the core that alone was under test
            var named = lastBad.Distinct().Where(i => i >= 0 && i < State.Cur.Length && State.Cur[i] < 0).ToList();
            if (named.Count == 0 && sub.Core >= 0 && State.Cur[sub.Core] < 0) named.Add(sub.Core);
            string where = "«" + sub.What + "»";

            if (State.Step == "deep")
            {
                var changed = Deepened();
                var outside = named.Where(c => !changed.Contains(c)).ToList();
                var inside = named.Where(changed.Contains).ToList();
                // a core this trial did not touch erred at the value it already had: up for good, and the part is repeated
                foreach (int c in outside)
                {
                    Lift(c, RaiseStep, "проба");
                    Fix(c);
                    Note("Ошибка на ядре вне пробы: " + CoreName(c) + " → " + State.Cur[c]);
                }
                if (outside.Count > 0 && inside.Count == 0) return;
                // named cores of the trial step back; the others go on with the same part. A core the load or WHEA reported
                // gets one more try with a later step (a first report may be chance); reported twice, or named only because
                // the laptop reset while it alone was under test, it stays where it was.
                if (inside.Count > 0)
                {
                    if (State.Strikes == null || State.Strikes.Length != State.Cur.Length) State.Strikes = new int[State.Cur.Length];
                    foreach (int c in inside)
                    {
                        bool again = lastBad.Contains(c) && ++State.Strikes[c] < 2;
                        Note(CoreName(c) + " " + State.Cur[c] + " не держится (" + why + "): " + (again ? "шаг назад на " + State.Base[c] + ", ещё одна попытка позже" : "остаётся " + State.Base[c]));
                        State.Cur[c] = State.Base[c];
                        if (!again) Fix(c);
                    }
                    if (inside.Count < changed.Count) return;
                    NextTrial(); // the group stays in the queue while one of its cores can still be tried
                    return;
                }
                // nothing named: back to the set that passed, and the group in parts: chiplets, then single cores
                bool byChip = changed.Select(Hw.ChipOf).Distinct().Count() > 1;
                Note(GroupLabel(changed) + " глубже на " + DeepStep + " не держится (" + why + ", " + where + "): возвращено" + (changed.Count <= 1 ? "" : byChip ? ", дальше по чиплетам" : ", дальше по одному ядру"));
                State.Cur = (int[])State.Base.Clone();
                var g = State.Queue[0];
                State.Queue.RemoveAt(0);
                // the parts are made of the group's own cores: one that stepped back inside this trial («one more try later») is
                // not among the changed ones any more, and on 18.11 15:37 CCD2 Core 8 fell out of every group that way
                var own = g.Cores.Where(c => c >= 0 && c < State.Cur.Length && !State.Fixed.Contains(c)).ToList();
                if (changed.Count <= 1) { foreach (int c in changed) Fix(c); own = own.Where(c => !changed.Contains(c)).ToList(); if (own.Count > 0) State.Queue.Insert(0, new TryGroup { Cores = own, Depth = g.Depth }); }
                else if (byChip) State.Queue.InsertRange(0, own.GroupBy(Hw.ChipOf).OrderBy(x => x.Key).Select(x => new TryGroup { Cores = x.ToList(), Depth = g.Depth }));
                else State.Queue.InsertRange(0, own.Select(c => new TryGroup { Cores = new List<int> { c }, Depth = g.Depth }));
                NextTrial();
                return;
            }

            if (named.Count > 0)
            {
                foreach (int c in named)
                {
                    int old = State.Cur[c];
                    Lift(c, RaiseStep, State.Step == "final" ? "итог" : "набор");
                    Note((lastBad.Contains(c) ? "Ошибка на ядре: " : "Сбой, когда под нагрузкой было одно ядро: ") + CoreName(c) + " " + old + " → " + State.Cur[c]);
                }
                return;
            }
            // nothing names a core. In the final check the cores that went deeper than the level that passed step back first,
            // DeepStep at a time: they were the last change. (The first run on hardware, 18.11, took all their gains back at
            // once, and twice for a core raised in between: the result came out 6 to 11 steps shallower than the curve in use.)
            if (State.Step == "final" && State.Level != null && State.Level.Length == State.Cur.Length)
            {
                var deeper = Enumerable.Range(0, State.Cur.Length).Where(i => State.Cur[i] < State.Level[i]).ToList();
                var ofChip = sub.Chip >= 0 ? deeper.Where(i => Hw.ChipOf(i) == sub.Chip).ToList() : deeper;
                if (ofChip.Count > 0) deeper = ofChip;
                if (deeper.Count > 0)
                {
                    foreach (int i in deeper) State.Cur[i] = Math.Min(State.Level[i], State.Cur[i] + DeepStep);
                    Note("Сбой без имени ядра (" + why + ", " + where + "): ядра глубже общего уровня +" + DeepStep + " → " + Store.Join(State.Cur));
                    return;
                }
            }
            // 18.11 night: under load the cores at the deepest level step back (the run of 18:05 lifted the shallowest loaded
            // core instead, the one that «holds the voltage»: it took four lifts and four resets a step, and left four cores
            // at −7 for the rest to lean on)
            if (State.Step == "final" && (sub.Kind == "heavy" || sub.Kind == "bursts"))
            {
                var loaded = (sub.Cores ?? All).Where(i => State.Cur[i] < 0).ToList();
                if (loaded.Count > 0)
                {
                    int bottom = loaded.Min(i => State.Cur[i]);
                    var deepest = loaded.Where(i => State.Cur[i] == bottom).ToList();
                    foreach (int c in deepest) Lift(c, RaiseStep, "итог");
                    Note("Сбой без имени ядра (" + why + ", " + where + "): ядра на самом глубоком уровне (" + deepest.Count + ") " + bottom + " → " + (bottom + RaiseStep));
                    return;
                }
            }
            // the whole set up a step, or the chiplet that was under the light load
            int step = State.Step == "final" ? FinalLevelStep : LevelStep;
            var scope = (sub.Chip >= 0 ? Hw.ChipCores(sub.Chip) : All).Where(i => State.Cur[i] < 0).ToList();
            if (scope.Count == 0) scope = All.Where(i => State.Cur[i] < 0).ToList();
            if (scope.Count == 0) { State.Failed = where + " не проходит даже на нулях — дело не в кривой"; State.Done = true; return; }
            foreach (int c in scope)
            {
                State.Cur[c] = Math.Min(0, State.Cur[c] + step);
                if (State.Level != null && State.Level.Length == State.Cur.Length) State.Level[c] = Math.Max(State.Level[c], State.Cur[c]);
            }
            Note("Сбой без имени ядра (" + why + ", " + where + "): " + (scope.Count == State.Cur.Length || sub.Chip < 0 ? "весь набор" : ChipName(sub.Chip)) + " +" + step + " → " + Store.Join(State.Cur));
        }

        // WHEA since the last look, across the parts of the check: the processor's corrected errors reach the log up to a
        // minute late (the 18.11 log: each exactly 60 s after the load began), so a part may find what an earlier one caused.
        // The core is named either way.
        DateTime wheaFrom = DateTime.Now;

        string WheaSet()
        {
            var hits = Load.WheaHits(wheaFrom);
            if (hits.Count == 0) return null;
            wheaFrom = hits.Max(h => h.When).AddSeconds(1);
            var cores = hits.Where(h => h.Core >= 0).Select(h => h.Core).Distinct().ToList();
            if (cores.Count > 0) lastBad = lastBad.Concat(cores).Distinct().ToList();
            return hits[0].Text;
        }

        // waits, looking at the load and at WHEA every two seconds; the failure or null
        string Hold(int seconds, string what, CancellationToken stop, Load.IHeavy load)
        {
            Now = what; Post();
            var end = DateTime.Now.AddSeconds(seconds);
            while (true)
            {
                int left = (int)(end - DateTime.Now).TotalMilliseconds;
                if (left > 0 && stop.WaitHandle.WaitOne(Math.Min(2000, left))) return null;
                if (load != null) { var bad = load.Failed(); if (bad.Count > 0) { lastBad = lastBad.Concat(bad).Distinct().ToList(); return HeavyError + string.Join(", ", bad.Select(CoreName)); } }
                var w = WheaSet();
                if (w != null) return w;
                if (left <= 0) return null;
            }
        }

        string RunSub(Sub s, CancellationToken stop)
        {
            SegmentEnd = DateTime.Now.AddSeconds(s.Sec);
            string r;
            switch (s.Kind)
            {
                case "idle":
                    return Hold(s.Sec, "Простой на записанном наборе", stop, null);
                case "bursts":
                    // what took the 18.11 set down eight times: the load beginning on every core at once
                    while (DateTime.Now < SegmentEnd && !stop.IsCancellationRequested)
                    {
                        using (var p = Load.Heavy(settings, s.Cores ?? All, true))
                        {
                            r = Hold(6, "Старт нагрузки на " + Who(s) + ": 6 с работа, 4 с простой", stop, p);
                            if (r != null || stop.IsCancellationRequested) return r;
                        }
                        Load.StopAll();
                        r = Hold(4, "Старт нагрузки на " + Who(s) + ": 6 с работа, 4 с простой", stop, null);
                        if (r != null || stop.IsCancellationRequested) return r;
                    }
                    return null;
                case "storm":
                    using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        var storm = new Thread(() => Load.Storm(s.Sec, cs.Token)) { IsBackground = true };
                        storm.Start();
                        r = Hold(s.Sec, "Шторм всплесков на всех ядрах", stop, null);
                        cs.Cancel(); storm.Join(30000);
                        return r;
                    }
                case "light":
                    return LightRound(Hw.ChipCores(s.Chip), ChipName(s.Chip), s.Sec, 5, stop);
                case "steps":
                    try
                    {
                        using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                        {
                            var cores = s.Cores ?? All;
                            var load = new Thread(() => Load.ClockSteps(cores, s.Sec - 1, CoreCurve.CapSet, cs.Token)) { IsBackground = true };
                            load.Start();
                            r = Hold(s.Sec, "Ступени частоты на " + Who(s), stop, null);
                            cs.Cancel(); load.Join(10000);
                            return r;
                        }
                    }
                    finally { CoreCurve.CapSet(0); }
                case "heavy":
                    using (var p = Load.Heavy(settings, s.Cores ?? All, true))
                    {
                        if (s.Sec < 60) return Hold(s.Sec, HeavyName + " AVX2 на " + Who(s), stop, p);
                        r = Hold(s.Sec * 2 / 3, HeavyName + " AVX2 на " + Who(s), stop, p);
                        if (r != null || stop.IsCancellationRequested) return r;
                    }
                    using (var p = Load.Heavy(settings, s.Cores ?? All, false))
                        return Hold(s.Sec - s.Sec * 2 / 3, HeavyName + " " + (Own ? "128 бит" : "SSE") + " на " + Who(s), stop, p);
                default:
                    return CoreSub(s.Core, stop);
            }
        }

        // one core of the set: the heavy load starting on it, one sweep of clock steps under a busy thread, then short
        // wake-ups with checked arithmetic (the own test in pulses: a wrong result names the core instead of a reset)
        string CoreSub(int c, CancellationToken stop)
        {
            string r;
            using (var p = Load.Heavy(settings, new[] { c }, true))
            {
                r = Hold(6, HeavyName + " на " + CoreName(c), stop, p);
                if (r != null || stop.IsCancellationRequested) return r;
            }
            Load.StopAll();
            try
            {
                using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var load = new Thread(() => Load.ClockSteps(new[] { c }, 13, CoreCurve.CapSet, cs.Token)) { IsBackground = true };
                    load.Start();
                    r = Hold(14, "Ступени частоты на " + CoreName(c), stop, null);
                    cs.Cancel(); load.Join(10000);
                    if (r != null || stop.IsCancellationRequested) return r;
                }
            }
            finally { CoreCurve.CapSet(0); }
            if (Stress.Available)
            {
                foreach (var form in new[] { "fma1+p25x175", "qdwt+p40x160" })
                    using (var p = new Stress.Run(new[] { c }, true, false, false, 0, false, form))
                    {
                        r = Hold(4, "Пробуждения на " + CoreName(c), stop, p);
                        if (r != null || stop.IsCancellationRequested) return r;
                    }
                return null;
            }
            using (var burst = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var t = Load.IdleBursts(c, burst.Token);
                r = Hold(8, "Пробуждения на " + CoreName(c), stop, null);
                burst.Cancel(); t.Join(5000);
                return r;
            }
        }

        // the light load like a browser video on some cores, the clock cap moving through middle clocks
        string LightRound(int[] cores, string where, int seconds, int capEverySec, CancellationToken stop)
        {
            try
            {
                using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var load = new Thread(() => Load.BrowserVideo(cores, seconds, cs.Token)) { IsBackground = true };
                    load.Start();
                    var end = DateTime.Now.AddSeconds(seconds);
                    var caps = new[] { 0, 4500, 3800, 3000, 2400, 3400, 2000, 4200, 2800 };
                    int i = 0; string r = null;
                    while (DateTime.Now < end && r == null && !stop.IsCancellationRequested)
                    {
                        CoreCurve.CapSet(caps[i++ % caps.Length]);
                        r = Hold(capEverySec, "Лёгкая фоновая нагрузка на " + where + " (как видео в браузере)", stop, null);
                    }
                    cs.Cancel(); load.Join(5000);
                    return r;
                }
            }
            finally { CoreCurve.CapSet(0); } // lifted again: the tune holds the cap
        }

        // ---------- rounds ----------

        // the first WHEA error since the round started, or null; a WHEA 47 from a core's own MCA bank names that core (Load.WheaHits)
        string CheckWhea(DateTime since)
        {
            var hits = Load.WheaHits(since);
            if (hits.Count == 0) return null;
            var cores = hits.Where(h => h.Core >= 0).Select(h => h.Core).Distinct().ToList();
            if (cores.Count > 0) lastBad = lastBad.Concat(cores).Distinct().ToList();
            return hits[0].Text;
        }

        // 17.11: the heavy load is Prime95 or the program's own test (Load.Heavy): its name in the lines of the page and the log
        bool Own { get { return Load.OwnLoad(settings); } }
        string HeavyName { get { return Own ? "Red Tune Test" : "Prime95"; } }
        string HeavyLow { get { return Own ? "Red Tune Test" : "Prime95"; } }
        string HeavyError { get { return Own ? "ошибка Red Tune Test на " : "ошибка Prime95 на "; } }

        // waits up to minutes, checking every 10 s; returns the failure or null
        string Watch(int minutes, string what, DateTime since, CancellationToken stop, Load.IHeavy p95) { return WatchSec(minutes * 60, 10000, what, since, stop, p95); }

        string WatchSec(int seconds, int everyMs, string what, DateTime since, CancellationToken stop, Load.IHeavy p95)
        {
            Now = what; SegmentEnd = DateTime.Now.AddSeconds(seconds); Post();
            while (DateTime.Now < SegmentEnd)
            {
                if (stop.WaitHandle.WaitOne(everyMs)) return null;
                if (p95 != null) { var bad = p95.Failed(); if (bad.Count > 0) { lastBad = bad; return HeavyError + string.Join(", ", bad.Select(CoreName)); } }
                var w = CheckWhea(since);
                if (w != null) return w;
            }
            return null;
        }

        string SingleCore(int c, int minutes, CancellationToken stop)
        {
            var since = DateTime.Now;
            int half = Math.Max(1, minutes / 2);
            using (var p = Load.Heavy(settings, new[] { c }, true))
            {
                var r = Watch(half, HeavyName + " AVX2 на " + CoreName(c), since, stop, p);
                if (r != null || stop.IsCancellationRequested) return r;
            }
            Load.StopAll();
            using (var burst = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var t = Load.IdleBursts(c, burst.Token);
                var r = Watch(minutes - half, "Всплески после простоя на " + CoreName(c), since, stop, null);
                burst.Cancel(); t.Join(5000);
                return r;
            }
        }

        // 16.11: a probe of the quick search, about 90 s: Prime95 on both threads of the core (a value far too deep errs
        // here within seconds, without a reset), then the clock steps, the load that found nearly every weak value of the
        // full tune (four to five sweeps of 5.4 → 1.6 GHz)
        string QuickProbe(int c, CancellationToken stop)
        {
            var since = DateTime.Now;
            string r;
            // 18.11 (Ver 2): 20 s and two sweeps of clock steps. The own test and Prime95 both err within seconds at a value
            // that is too deep, and every core's steps are swept again on the whole set in the final check.
            int heavySec = Quick2 ? 20 : 25, stepsSec = Quick2 ? 27 : 60;
            using (var p = Load.Heavy(settings, new[] { c }, true))
            {
                r = WatchSec(heavySec, Quick2 ? 2000 : 5000, HeavyName + " AVX2 на " + CoreName(c), since, stop, p);
                if (r != null || stop.IsCancellationRequested) return r;
            }
            Load.StopAll();
            try
            {
                using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var load = new Thread(() => Load.ClockSteps(new[] { c }, stepsSec - 1, CoreCurve.CapSet, cs.Token)) { IsBackground = true };
                    load.Start();
                    r = WatchSec(stepsSec, Quick2 ? 3000 : 5000, "Ступени частоты на " + CoreName(c), since, stop, null);
                    cs.Cancel(); load.Join(10000);
                    return r;
                }
            }
            finally { CoreCurve.CapSet(0); }
        }

        // 14.11: the long confirmation of a core's value, 32 minutes in four parts, each a different place on the core's curve:
        // one thread (the top boost clock, the highest voltage), both threads under Prime95 (AVX2, and AVX-512 where the
        // processor has it: the highest current), short bursts after idle (the jumps from sleep), and clock steps (the
        // middle clocks one by one, the pattern that reset the laptop during a core-curve measurement).
        string ConfirmCore(int c, CancellationToken stop)
        {
            var since = DateTime.Now;
            string r;
            using (var p = Load.Heavy(settings, new[] { c }, true, false, true))
            {
                r = Watch(8, "Подтверждение " + CoreName(c) + ": один поток, максимальный буст", since, stop, p);
                if (r != null || stop.IsCancellationRequested) return r;
            }
            bool wide = Load.HasAvx512;
            using (var p = Load.Heavy(settings, new[] { c }, true))
            {
                r = Watch(wide ? 4 : 8, "Подтверждение " + CoreName(c) + ": оба потока, " + HeavyLow + " AVX2", since, stop, p);
                if (r != null || stop.IsCancellationRequested) return r;
            }
            if (wide)
                using (var p = Load.Heavy(settings, new[] { c }, true, true))
                {
                    r = Watch(4, "Подтверждение " + CoreName(c) + ": оба потока, " + HeavyLow + " AVX-512", since, stop, p);
                    if (r != null || stop.IsCancellationRequested) return r;
                }
            Load.StopAll();
            using (var burst = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var t = Load.IdleBursts(c, burst.Token);
                r = Watch(8, "Подтверждение " + CoreName(c) + ": всплески после простоя", since, stop, null);
                burst.Cancel(); t.Join(5000);
                if (r != null || stop.IsCancellationRequested) return r;
            }
            try
            {
                using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var load = new Thread(() => Load.ClockSteps(new[] { c }, 8 * 60, CoreCurve.CapSet, cs.Token)) { IsBackground = true };
                    load.Start();
                    r = Watch(8, "Подтверждение " + CoreName(c) + ": ступени частоты", since, stop, null);
                    cs.Cancel(); load.Join(10000);
                    return r;
                }
            }
            finally { CoreCurve.CapSet(0); }
        }

        string CcdRound(Stage st, CancellationToken stop)
        {
            var cores = st.Load == "ccd1" ? Ccd1 : st.Load == "ccd2" ? Ccd2 : All;
            var where = st.Load == "ccd1" ? "CCD1" : st.Load == "ccd2" ? "CCD2" : "всех ядрах";
            var since = DateTime.Now;
            foreach (var seg in new[] { Tuple.Create(true, st.Avx, "AVX2"), Tuple.Create(false, st.Sse, "SSE") })
                using (var p = Load.Heavy(settings, cores, seg.Item1))
                {
                    var r = Watch(seg.Item2, HeavyName + " " + (Own && !seg.Item1 ? "128 бит" : seg.Item3) + " на " + where, since, stop, p);
                    if (r != null || stop.IsCancellationRequested) return r;
                }
            Now = "Рывки на " + where + ": 20 с нагрузка, 10 с простой"; SegmentEnd = DateTime.Now.AddMinutes(st.OnOff); Post();
            while (DateTime.Now < SegmentEnd && !stop.IsCancellationRequested)
            {
                using (var p = Load.Heavy(settings, cores, true))
                {
                    if (stop.WaitHandle.WaitOne(20000)) return null;
                    var bad = p.Failed();
                    if (bad.Count > 0) lastBad = bad;
                    if (bad.Count > 0) return HeavyError + string.Join(", ", bad.Select(CoreName));
                }
                Load.StopAll();
                var w = CheckWhea(since);
                if (w != null) return w;
                if (stop.WaitHandle.WaitOne(10000)) return null;
            }
            return null;
        }

        // the light load like a browser video on one chiplet, the clock cap moving through middle clocks every 15 s
        string BrowserRound(Stage st, int minutes, CancellationToken stop)
        {
            var cores = st.Load == "browser2" ? Ccd2 : Ccd1;
            var where = st.Load == "browser2" ? "CCD2" : "CCD1";
            var since = DateTime.Now;
            try
            {
                using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var load = new Thread(() => Load.BrowserVideo(cores, minutes * 60, cs.Token)) { IsBackground = true };
                    load.Start();
                    Now = "Лёгкая фоновая нагрузка на " + where + " (как видео в браузере)"; SegmentEnd = DateTime.Now.AddMinutes(minutes); Post();
                    var caps = new[] { 0, 4500, 3800, 3000, 2400, 3400, 2000, 4200, 2800 };
                    int i = 0;
                    string r = null;
                    while (DateTime.Now < SegmentEnd && r == null)
                    {
                        CoreCurve.CapSet(caps[i++ % caps.Length]);
                        if (stop.WaitHandle.WaitOne(15000)) break;
                        var w = CheckWhea(since);
                        if (w != null) r = w;
                    }
                    cs.Cancel(); load.Join(5000);
                    return r;
                }
            }
            finally { CoreCurve.CapSet(0); } // lifted again: the tune holds the cap
        }

        // one core at a time fully busy while the clock cap steps down, like a core-curve measurement
        string StepsRound(Stage st, int minutes, CancellationToken stop)
        {
            var since = DateTime.Now;
            var cores = State.StagePhase == "bisect" ? State.Proven.Concat(State.Test).Where(st.Tune.Contains).ToArray() : st.Tune;
            if (cores.Length == 0) cores = st.Tune;
            try
            {
                using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var load = new Thread(() => Load.ClockSteps(cores, minutes * 60, CoreCurve.CapSet, cs.Token)) { IsBackground = true };
                    load.Start();
                    var r = Watch(minutes, "Ступени частоты: одно ядро под нагрузкой, " + cores.Length + " ядер по очереди", since, stop, null);
                    cs.Cancel(); load.Join(10000);
                    return r;
                }
            }
            finally { CoreCurve.CapSet(0); } // lifted again: the tune holds the cap
        }

        string StormRound(int minutes, CancellationToken stop)
        {
            var since = DateTime.Now;
            using (var cs = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var storm = new Thread(() => Load.Storm(minutes * 60, cs.Token)) { IsBackground = true };
                storm.Start();
                var r = Watch(minutes, "Шторм на всех ядрах", since, stop, null);
                cs.Cancel(); storm.Join(30000);
                return r;
            }
        }

        // minutes of the quick tune, for the estimates (some parts run twice and a few resets are counted in)
        public static double UniMinutes()
        {
            var order = Enumerable.Range(0, Hw.CurveCores).ToArray();
            double level = SubsFor("uni", null, order).Sum(s => s.Sec + 3) / 60.0;
            // five levels with all cores only, four with the chain of loads (about three times as long), a few parts again, a reset
            return 5 * level + 4 * 3 * level + Math.Max(2, Hw.CurveCores / 3) * 1.2 + 1.7;
        }

        // the daily mode: the edges (four probes a core), then the set, its descent and the final check
        public static double EdgeMinutes() { return Hw.CurveCores * QuickCoreMinutes; }

        public static double DailyMinutes()
        {
            var order = Enumerable.Range(0, Hw.CurveCores).ToArray();
            Func<string, List<int>, double> len = (step, ch) => SubsFor(step, ch, order).Sum(s => s.Sec + 3) / 60.0;
            double deep = 9 * len("deep", order.ToList()) + 2 + (Hw.CurveChips >= 2 ? Hw.CurveChips * (len("deep", Hw.ChipCores(0).ToList()) + 1) : 0) + Hw.CurveCores * 0.5 * (len("deep", new List<int> { 0 }) + 1.2);
            return len("set", null) + 1 + deep + len("final", null) + 3;
        }

        public static double FinalMinutes()
        {
            var order = Enumerable.Range(0, Hw.CurveCores).ToArray();
            return SubsFor("final", null, order, new int[Hw.CurveCores]).Sum(s => s.Sec + 3) / 60.0 + 4;
        }

        public static double SetPhaseMinutes(string from = "set", int coresDone = 0)
        {
            return from == "final" ? FinalMinutes() : UniMinutes() * Math.Max(0.15, 1 - coresDone / (double)Math.Max(1, Hw.CurveCores)) + FinalMinutes();
        }

        // The quick tune of 18.11 without loading anything, on a model of a processor:
        //   spec "E=-35,-40,…;T=-23,-32,…;N=4,7;R=8,9;P=7"
        //   E  the edge of each core alone (a probe of phase 1 fails below it); R: the cores whose probe fails by a reset
        //   T  what each core really holds in the whole set: a part of the check fails when a core it covers is below it.
        //      The failure names the core when it is one of N (the load or WHEA reports it) or the core alone under test;
        //      otherwise it is a reset that names nothing.
        //   P  (optional) a seed: a core 1, 2, 3 steps too deep is then found only 25, 50, 75 % of the time by each part
        //   W  (optional) the checks of the «deep» step do not see a core that is up to W steps too deep
        //   S  (optional) the same, but the all-core load of those checks does see it (the 18.11 run on hardware)
        //   K  (optional) daily: the daily mode (edges, then the set down with all cores loaded); otherwise the strict one
        //   U  (optional) what each core holds alone. With it the model is the shared voltage: under a load the voltage level
        //      is the shallowest value among the loaded cores, and a loaded core fails when that level is deeper than what
        //      it holds with that many cores loaded (from T with all of them, in a straight line, to U alone). The file
        //      then also says for how many of all the possible sets of busy cores the result would not hold.
        // The file gets every round, the notes, the result against T, the resets and the time.
        static void SimulateQuick(string spec, string outFile)
        {
            int n = Hw.CurveCores;
            var part = spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim());
            Func<string, int[]> ints = k => part.ContainsKey(k) && part[k] != "" ? part[k].Split(',').Select(x => int.Parse(x.Trim())).ToArray() : new int[0];
            int[] E = ints("E"), T = ints("T"); var N = ints("N").ToList(); var R = ints("R").ToList();
            if (E.Length != n || T.Length != n) { File.WriteAllText(outFile, "E and T need " + n + " values each", new UTF8Encoding(true)); return; }
            Random rnd = part.ContainsKey("P") ? new Random(int.Parse(part["P"])) : null;
            int weak = part.ContainsKey("W") ? int.Parse(part["W"]) : 0, soft = part.ContainsKey("S") ? int.Parse(part["S"]) : 0;
            int[] U = ints("U"); bool shared = U.Length == n;
            bool daily = part.ContainsKey("K") && part["K"] == "daily";
            // R (optional, percent, 100 by default): how much of the gap to the shallowest loaded core a deeper core gets (18:05: about 60)
            double ride = part.ContainsKey("R") ? int.Parse(part["R"]) / 100.0 : 1.0;
            Func<int, int, int> holds = (i, cnt) => !shared || n < 2 ? T[i] : (int)Math.Round(U[i] + (T[i] - U[i]) * (cnt - 1) / (double)(n - 1));
            var t = new Tuner(new Settings());
            t.Simulate = () => true;
            t.State = new TuneState { Mode = "quick", Ver = 2, Order = Enumerable.Range(0, n).ToArray(), Edge = new int[n], Gain = new int[n] };
            if (daily) { t.State.Kind = "daily"; t.State.Value = -24; } else { t.State.CoreIdx = n; t.BeginUni("Старт подбора (строгий): спуск с проверкой каждого шага"); }
            var sb = new StringBuilder();
            int rounds = 0, resets = 0, errors = 0, notes = 0; double sec = 0, sec1 = 0;
            while (!t.State.Done && rounds < 3000)
            {
                int[] values; string desc; Func<CancellationToken, string> body;
                t.Plan(out values, out desc, out body);
                if (t.State.Done) break;
                rounds++;
                bool ok, reset = false; var named = new List<int>(); double took;
                if (t.State.Phase == 1)
                {
                    int c = t.State.Order[t.State.CoreIdx];
                    ok = values[c] >= (shared ? U[c] : E[c]);
                    reset = !ok && R.Contains(c);
                    took = ok ? 58 : reset ? 100 : 20;
                    sec1 += took;
                }
                else
                {
                    var sub = t.Subs()[t.State.Sub];
                    var scope = sub.Cores ?? (sub.Kind == "core" ? new[] { sub.Core } : sub.Kind == "light" ? Hw.ChipCores(sub.Chip) : All);
                    int miss = t.State.Step != "deep" ? 0 : sub.Kind == "heavy" ? weak : Math.Max(weak, soft);
                    var bad = scope.Where(i => values[i] < T[i] - miss).ToList();
                    if (shared)
                    {
                        bool loads = sub.Kind == "bursts" || sub.Kind == "heavy" || sub.Kind == "steps" || sub.Kind == "core";
                        int level = scope.Max(i => values[i]);
                        bad = loads ? scope.Where(i => values[i] + ride * (level - values[i]) < holds(i, scope.Length)).ToList() : new List<int>();
                    }
                    // a core of N gives way under load only, and then it is reported (a wrong result, WHEA)
                    bool load = sub.Kind == "bursts" || sub.Kind == "heavy" || sub.Kind == "core" || sub.Kind == "steps";
                    if (!load) bad = bad.Where(i => !N.Contains(i)).ToList();
                    if (rnd != null) bad = bad.Where(i => rnd.NextDouble() < 0.25 * (T[i] - values[i])).ToList();
                    ok = bad.Count == 0;
                    if (!ok)
                    {
                        if (bad.All(N.Contains)) named = bad;
                        else reset = true;
                    }
                    took = ok ? sub.Sec + 3 : reset ? 100 : 20;
                }
                if (reset) resets++; else if (!ok) errors++;
                sec += took;
                t.lastBad = named; t.roundValues = values;
                sb.AppendLine(rounds + ". " + desc + " :: " + string.Join(" ", values) + " -> " + (ok ? "ok" : reset ? "RESET" : "ERROR " + string.Join(",", named)));
                t.Record(ok, ok ? null : reset ? "перезагрузка" : "ошибка теста");
                for (; notes < t.State.Log.Count; notes++) sb.AppendLine("      " + t.State.Log[notes].Substring(Math.Min(13, t.State.Log[notes].Length)));
            }
            sb.AppendLine();
            sb.AppendLine("done=" + t.State.Done + " failed=" + t.State.Failed + " rounds=" + rounds + " resets=" + resets + " errors=" + errors);
            sb.AppendLine("edge:    " + string.Join(" ", t.State.Edge));
            sb.AppendLine("truth:   " + string.Join(" ", T));
            sb.AppendLine("result:  " + string.Join(" ", t.State.Cur));
            sb.AppendLine("above T: " + string.Join(" ", t.State.Cur.Select((v, i) => v - T[i])) + "   (0 = exactly what the core holds; below 0 = too deep)");
            sb.AppendLine("minutes: " + (sec / 60).ToString("0"));
            if (shared && n <= 20)
            {
                sb.AppendLine("alone:   " + string.Join(" ", U));
                // every possible set of busy cores: the level is its shallowest value, and each of them must hold it
                int unsafeSets = 0, worst = 0; var r = t.State.Cur;
                for (int mask = 1; mask < (1 << n); mask++)
                {
                    int cnt = 0, level = int.MinValue;
                    for (int i = 0; i < n; i++) if ((mask >> i & 1) != 0) { cnt++; if (r[i] > level) level = r[i]; }
                    int deficit = 0;
                    for (int i = 0; i < n; i++) if ((mask >> i & 1) != 0) deficit = Math.Max(deficit, (int)Math.Ceiling(holds(i, cnt) - (r[i] + ride * (level - r[i]))));
                    if (deficit > 0) { unsafeSets++; if (deficit > worst) worst = deficit; }
                }
                sb.AppendLine("sets of busy cores that would not hold: " + unsafeSets + " of " + ((1 << n) - 1) + (unsafeSets > 0 ? ", the worst " + worst + " steps short" : ""));
            }
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
        }

        // --tune-upgrade-dry <tune.json> <out>: what a quick tune begun before 18.11 becomes when it is continued (nothing is
        // written, neither the file nor the processor): its edges, the set the second phase starts from, the first round
        public static void UpgradeDry(string file, string outFile)
        {
            var t = new Tuner(new Settings());
            t.Simulate = () => true;
            t.State = new JavaScriptSerializer().Deserialize<TuneState>(File.ReadAllText(file, Encoding.UTF8));
            var sb = new StringBuilder();
            sb.AppendLine("before: mode " + t.State.Mode + " ver " + t.State.Ver + " phase " + t.State.Phase + " paused " + t.State.Paused + " round «" + t.State.Round + "»");
            sb.AppendLine("cur:    " + string.Join(" ", t.State.Cur));
            t.Upgrade();
            sb.AppendLine("after:  ver " + t.State.Ver + " phase " + t.State.Phase + " step " + t.State.Step + " sub " + t.State.Sub + " round «" + t.State.Round + "»");
            sb.AppendLine("edge:   " + string.Join(" ", t.State.Edge ?? new int[0]));
            sb.AppendLine("cur:    " + string.Join(" ", t.State.Cur));
            if (!t.State.Done)
            {
                int[] values; string desc; Func<CancellationToken, string> body;
                t.Plan(out values, out desc, out body);
                sb.AppendLine("next:   " + desc + " :: " + string.Join(" ", values));
                sb.AppendLine("left:   " + (int)t.Estimate().TotalMinutes + " min");
            }
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
        }

        // tests the search without loading anything: spec "4:-1,9:-33" = core 4 fails below -1, core 9 below -33
        public static void SimulateToFile(string spec, string mode, string outFile)
        {
            if (mode == "quick" && spec.Contains("=")) { SimulateQuick(spec, outFile); return; }
            var need = spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).ToDictionary(p => int.Parse(p[0]), p => int.Parse(p[1]));
            var t = new Tuner(new Settings());
            int[] last = null;
            t.Simulate = () => { var v = last; return need.All(kv => v[kv.Key] >= kv.Value); };
            // capture the values of each round through the log line
            t.State = new TuneState { Mode = mode, Order = Load.CoreOrder() };
            if (mode == "check") { t.State.Phase = 3; t.State.Cur = new[] { -29, -36, -41, -38, -5, -39, -29, -28, -13, -39, -36, -14, -32, -19, -23, -28 }; }
            var sb = new StringBuilder();
            int rounds = 0;
            while (!t.State.Done && rounds < 400)
            {
                int[] values; string desc; Func<CancellationToken, string> body;
                t.Plan(out values, out desc, out body);
                if (t.State.Done) break;
                last = values; rounds++;
                bool ok = t.Simulate();
                sb.AppendLine(rounds + ". " + desc + " :: " + string.Join(" ", values) + " -> " + (ok ? "ok" : "FAIL"));
                t.Record(ok, ok ? null : "sim");
            }
            sb.AppendLine("done=" + t.State.Done + " failed=" + t.State.Failed + " rounds=" + rounds);
            sb.AppendLine("result: " + string.Join(" ", t.State.Cur));
            foreach (var r in t.State.Raised) sb.AppendLine("raised " + r);
            double minutes = 0; // rough time: phase 1 rounds 4/10 min, phase 2 12, phase 3 25 (storm 20/10)
            foreach (var line in sb.ToString().Split('\n'))
                minutes += line.Contains("фаза 1") ? (line.Contains("проверка") ? 10 : 4) : line.Contains("фаза 2") ? 12 : line.Contains("Шторм") ? (line.Contains("поиск") ? 10 : 20) : line.Contains("фаза 3") ? (line.Contains("поиск") ? 12 : 25) : 0;
            sb.AppendLine("approx minutes (all rounds full length): " + minutes);
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
        }

        // rough time left for the page
        public TimeSpan Estimate()
        {
            if (State == null || State.Done) return TimeSpan.Zero;
            double min = 0;
            if (Quick2 && State.Kind == "daily") return TimeSpan.FromMinutes(State.Phase == 1 ? (State.Order.Length - State.CoreIdx) * QuickCoreMinutes + DailyMinutes() : DailyMinutes() * (State.Step == "final" ? 0.3 : State.Step == "deep" ? 0.75 : 1));
            if (Quick2) return TimeSpan.FromMinutes(SetPhaseMinutes(State.Step, State.Step == "uni" ? State.Fixed.Count : 0));
            if (Quick) return TimeSpan.FromMinutes((State.Phase == 1 ? (State.Order.Length - State.CoreIdx) * QuickCoreMinutes : 0) + Quick3.Skip(State.Phase == 3 ? State.StageIdx : 0).Sum(StageMinutes));
            if (State.Phase == 1) min += (State.Order.Length - State.CoreIdx) * (4 * P1Minutes + ConfirmMinutes + 2) + 120;
            if (State.Phase <= 2) min += (State.Phase == 2 ? (Phase2.Length - State.StageIdx) : Phase2.Length) * 30;
            min += Phase3.Skip(State.Phase == 3 ? State.StageIdx : 0).Sum(st => st.Load == "storm" ? st.Storm : st.Minutes > 0 ? st.Minutes : st.Avx + st.Sse + st.OnOff);
            return TimeSpan.FromMinutes(min);
        }
    }
}
