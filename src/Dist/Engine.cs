using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace RedTune.Dist
{
    class RuleStatus
    {
        public int Count;
        public string SamplePath;
        public bool Protected;
        public bool Soft; // moved with CPU Sets because affinity is blocked
    }

    // 20.11: a program without a rule that keeps the processor busy, for the page's hints: how long it was busy (at least
    // three quarters of a core or more), how much of that it was the window in front, how many cores it took on average.
    class Hint
    {
        public string Path;
        public double Busy, Fg, LoadSum; // seconds busy, seconds of those in front, core-seconds
        public double Load { get { return Busy > 0 ? LoadSum / Busy : 0; } }
        public bool Foreground { get { return Fg >= Busy * 0.5; } }
        public string Name { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }
        public Hint Copy() { return (Hint)MemberwiseClone(); }
    }

    // Every 3 s new processes get the cores of their rule; every 60 s all of them are checked again
    // (a program may reset its own affinity). Processes the app pinned get all cores back on pause,
    // when their rule goes away and on exit.
    class Engine
    {
        public ulong MaskCCD1, MaskCCD2, MaskAll, MaskFirstThreads;
        public bool TwoChips;
        // 20.11 (X3D): the chip with the bigger L3 (3D V-Cache) is the one for games, the other one for the background.
        // With equal chips (8945HX) CCD1 stays the game chip, as before. L3 sizes in bytes, 0 = not known.
        public Group GameGroup = Group.CCD1, BackGroup = Group.CCD2;
        public bool X3D;
        public long L3First, L3Second;
        public int CoreCount, ThreadsPerCore = 1;
        int[] coresCCD1 = new int[0], coresCCD2 = new int[0];

        readonly object sync = new object();
        List<Rule> ordered = new List<Rule>();
        Settings settings = new Settings();
        volatile bool paused, force = true;
        Timer timer, retryTimer;
        int scanMs = ScanSlow;
        ManagementEventWatcher watcher;
        readonly object work = new object(); // scan, retry and start events touch the same tables
        DateTime lastFull = DateTime.MinValue;
        readonly int selfPid = Process.GetCurrentProcess().Id;
        readonly string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        readonly Dictionary<string, Rule> cache = new Dictionary<string, Rule>();   // "pid|name" -> matched rule (null = none)
        readonly Dictionary<string, string> paths = new Dictionary<string, string>();
        readonly Dictionary<string, ulong> managed = new Dictionary<string, ulong>(); // processes on cores set by this app
        readonly HashSet<string> denied = new HashSet<string>();                    // protected: affinity can't be changed
        readonly Dictionary<string, ulong> cpuSet = new Dictionary<string, ulong>(); // protected ones moved with CPU Sets
        readonly HashSet<string> viaPs = new HashSet<string>();                      // moved through PowerShell (unsigned callers refused)
        readonly Dictionary<string, DateTime> psTried = new Dictionary<string, DateTime>();
        Dictionary<string, RuleStatus> status = new Dictionary<string, RuleStatus>();
        int managedCount;
        string lastAction = "";

        static readonly Rule SkipRule = new Rule { Group = Group.SKIP, Pattern = "(launcher)" };
        // the folders games are installed in, by store; they go to the game chip without a rule of their own («Новые игры»).
        // 20.11: Xbox, Riot, EA and GOG added to Steam and Epic. The two inside a launcher's own folder are looked at before
        // the launchers are left alone (that folder is a launcher's, its games subfolder is not).
        static readonly string[,] GamePlaces =
        {
            { @"\steamapps\common\", "Игра Steam" }, { @"\Epic Games\", "Игра Epic" }, { @"\XboxGames\", "Игра Xbox" },
            { @"\ModifiableWindowsApps\", "Игра Xbox" }, { @"\Riot Games\", "Игра Riot" }, { @"\EA Games\", "Игра EA" }, { @"\GOG Games\", "Игра GOG" },
        };
        static readonly string[,] GamePlacesInLaunchers = { { @"\Ubisoft Game Launcher\games\", "Игра Ubisoft" }, { @"\GOG Galaxy\Games\", "Игра GOG" } };
        Rule[] storeRules = new Rule[0], storeRulesInLaunchers = new Rule[0];
        // a program a launcher starts that lives outside the launcher's folder is a game (Battle.net puts its games anywhere)
        Rule launchedRule;
        readonly Dictionary<string, Rule> launched = new Dictionary<string, Rule>(); // "pid|name" -> the rule it got at its start
        static readonly string[] Browsers = { "msedge", "chrome", "firefox", "opera", "brave", "browser", "vivaldi" }; // a link opened from a launcher
        // games inherit the cores of the launcher that starts them, so launchers stay on all cores
        static readonly string[] LauncherNames =
        {
            "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices",
            "RiotClientUx", "EADesktop", "EABackgroundService", "upc", "UbisoftConnect", "GalaxyClient", "GalaxyClientService",
        };
        static readonly string[] LauncherPaths =
        {
            @"\Epic Games\Launcher\", @"\Battle.net\", @"\Riot Client\", @"\Electronic Arts\EA Desktop\", @"\Ubisoft Game Launcher\", @"\GOG Galaxy\",
        };

        public Engine()
        {
            DetectChips();
            Func<string[,], Rule[]> make = a => Enumerable.Range(0, a.GetLength(0)).Select(i => new Rule { Group = GameGroup, Pattern = a[i, 0], Name = a[i, 1] }).ToArray();
            storeRules = make(GamePlaces); storeRulesInLaunchers = make(GamePlacesInLaunchers);
            launchedRule = new Rule { Group = GameGroup, Pattern = "(from launcher)", Name = "Игра из лаунчера" };
            hidden = Store.LoadHidden();
        }

        // New programs come from the start events, so the full scan only refreshes the window, cleans up and catches
        // programs that reset their own cores: every 5 s while the window is open, every 30 s in the tray.
        // Processes that refused (CoD's first seconds) get their own 3 s retry without a scan.
        const int ScanFast = 5000, ScanSlow = 30000, RetryMs = 3000;

        public void SetFastScan(bool fast)
        {
            int ms = fast ? ScanFast : ScanSlow;
            if (ms == scanMs) return;
            scanMs = ms;
            if (timer != null) timer.Change(fast ? 0 : ms, ms);
        }

        public void Start()
        {
            timer = new Timer(_ => Tick(), null, 0, scanMs);
            retryTimer = new Timer(_ => Retry(), null, RetryMs, RetryMs);
            // A protected game (anti-cheat) can't be moved, but it inherits the cores of the launcher that starts it:
            // the launcher has to be moved right at its start, before it starts the game (3 s polling was too late for
            // CoD on 2026-11-03: sp26-cod.exe started before its launcher was moved).
            try
            {
                watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                watcher.EventArrived += (s, e) =>
                {
                    try { OnStarted(Convert.ToInt32(e.NewEvent["ProcessID"]), Convert.ToString(e.NewEvent["ProcessName"]), Convert.ToInt32(e.NewEvent["ParentProcessID"])); }
                    catch (Exception ex) { Store.Log("ошибка при запуске процесса: " + ex.Message); }
                };
                watcher.Start();
            }
            catch (Exception ex) { watcher = null; Store.Log("мгновенный старт недоступен, только проверка раз в 3 с: " + ex.Message); }
        }

        public void Stop()
        {
            if (timer != null) timer.Dispose();
            if (retryTimer != null) retryTimer.Dispose();
            timer = retryTimer = null;
            if (watcher != null) { try { watcher.Stop(); watcher.Dispose(); } catch { } watcher = null; }
        }

        // a new process gets its cores right away and is recorded like a scanned one (a refusal goes to the 3 s retry)
        void OnStarted(int pid, string exeName, int parent)
        {
            if (pid <= 4 || pid == selfPid) return;
            List<Rule> rules;
            Settings s;
            lock (sync) { rules = ordered; s = settings; }
            var name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName.Substring(0, exeName.Length - 4) : exeName;
            var path = Native.GetPath(pid);
            var rule = Match(name, path, rules, s);
            string key = pid + "|" + name;
            if (rule == null && s.StoreGames && FromLauncher(name, path, parent)) rule = launchedRule;
            lock (work)
            {
                if (rule == launchedRule) launched[key] = rule;
                cache[key] = rule;
                if (path != null) paths[key] = path;
                Enforce(key, pid, name, rule);
            }
        }

        // started by a launcher (Steam, Battle.net, EA, Ubisoft, GOG, Riot, Epic) and living outside its folder: a game.
        // Not a launcher itself, not Windows' own, not a browser a link was opened in.
        bool FromLauncher(string name, string path, int parent)
        {
            return parent > 4 && path != null && FromLauncher(name, path, Native.GetPath(parent));
        }

        bool FromLauncher(string name, string path, string pp)
        {
            if (path == null || pp == null || path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) || IsLauncher(name, path)) return false;
            if (Browsers.Any(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase))) return false;
            var pname = System.IO.Path.GetFileNameWithoutExtension(pp);
            if (!IsLauncher(pname, pp)) return false;
            var pdir = System.IO.Path.GetDirectoryName(pp);
            // inside the launcher's folder: its helpers (Steam's overlay, Battle.net's agent); a Steam game there has its own rule
            return string.IsNullOrEmpty(pdir) || !path.StartsWith(pdir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        void Retry()
        {
            if (!Monitor.TryEnter(work)) return;
            try
            {
                foreach (var key in denied.Concat(cpuSet.Keys).Distinct().ToList())
                {
                    Rule rule;
                    int pid;
                    var parts = key.Split('|');
                    if (!cache.TryGetValue(key, out rule) || !int.TryParse(parts[0], out pid)) continue;
                    if (Native.GetPath(pid) == null) continue; // gone; the next scan forgets it
                    Enforce(key, pid, parts[1], rule);
                }
            }
            catch (Exception ex) { Store.Log("ошибка: " + ex.Message); }
            finally { Monitor.Exit(work); }
        }

        public bool Paused
        {
            get { return paused; }
            set { paused = value; force = true; Kick(); }
        }

        public Dictionary<string, RuleStatus> Status { get { lock (sync) return status; } }

        // ---------- 20.11: hints ----------
        // Every scan that comes at least 2 s after the last one measures the processor time of the programs without a
        // rule (Windows' own, the launchers and Red Tune's loads left out), per exe. An exe that took BusyCores or
        // more counts that time as busy (0.75, not 1: one thread flat out measures 0.98–1.01 between two scans); 60 s busy in all and it is a hint. In front for half of it or more: it looks
        // like a game → the game chip; else the background chip; more cores than half a chip has → all cores. Kept in
        // memory only (a restart starts over); «Скрыть» is kept in hints-hidden.txt.
        public const double HintBusySeconds = 60, BusyCores = 0.75;
        readonly Dictionary<string, Hint> hints = new Dictionary<string, Hint>(); // exe path, lower case -> its load
        Dictionary<string, TimeSpan> cpuWas = new Dictionary<string, TimeSpan>(); // "pid|name" -> processor time at the last sample
        DateTime lastSample = DateTime.MinValue;
        HashSet<string> hidden = new HashSet<string>();
        static readonly string[] NoHints = { "prime95", "RedTune", "RedTuneLab", "RedTune-chk", "RedTuneStress" };

        public List<Hint> Hints()
        {
            lock (sync)
                return hints.Values.Where(h => h.Busy >= HintBusySeconds && !hidden.Contains(h.Path.ToLowerInvariant()) && Match(h.Name, h.Path, ordered, settings) == null)
                    .OrderByDescending(h => h.Busy).Take(5).Select(h => h.Copy()).ToList();
        }

        public Group Suggest(Hint h)
        {
            int perChip = Math.Max(1, CoreCount / 2);
            if (h.Load > perChip * 0.75) return Group.ALL;
            return h.Foreground ? GameGroup : BackGroup;
        }

        public void Hide(string path)
        {
            HashSet<string> copy;
            lock (sync) { hidden.Add(path.ToLowerInvariant()); copy = new HashSet<string>(hidden); }
            Store.SaveHidden(copy);
        }

        // for --dist-check: a made-up load as if measured
        internal void FakeHint(string path, double busy, double fg, double cores)
        {
            lock (sync) hints[path.ToLowerInvariant()] = new Hint { Path = path, Busy = busy, Fg = fg, LoadSum = busy * cores };
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        static int ForegroundPid() { try { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return (int)p; } catch { return 0; } }
        public int ManagedCount { get { lock (sync) return managedCount; } }
        public string LastAction { get { lock (sync) return lastAction; } }

        // more specific (longer) patterns win, so "\steamapps\common\Some Game\" beats "\steamapps\common\"
        public void Apply(IEnumerable<Rule> rules, Settings s)
        {
            var list = rules.Where(r => r.Enabled && r.Pattern.Length > 0).Select(r => r.Clone()).OrderByDescending(r => r.Pattern.Length).ToList();
            lock (sync) { ordered = list; settings = s.Clone(); }
            force = true;
            Kick();
        }

        void Kick() { ThreadPool.QueueUserWorkItem(_ => Tick()); }

        public ulong MaskOf(Group g)
        {
            return g == Group.CCD1 ? MaskCCD1 : g == Group.CCD2 ? MaskCCD2 : g == Group.ALL ? MaskAll : 0;
        }

        // the rule's cores; without SMT only the first thread of each core
        public ulong MaskOf(Rule r)
        {
            var m = MaskOf(r.Group);
            return r.NoSmt && ThreadsPerCore > 1 ? m & MaskFirstThreads : m;
        }

        public string RangeText(Group g)
        {
            if (g == Group.SKIP) return "не трогать, ядра выбирает Windows";
            var a = g == Group.CCD1 ? coresCCD1 : g == Group.CCD2 ? coresCCD2 : null;
            if (a == null) return "все " + CoreCount + " ядер";
            return a.Length == 0 ? "" : "ядра " + a.First() + "–" + a.Last() + (X3D && g == GameGroup ? " · 3D V-Cache" : "");
        }

        // «CCD2 · 3D V-Cache»: the game chip's name, for the texts that point to it
        public string GameName { get { return GameGroup + (X3D ? " · 3D V-Cache" : ""); } }

        void DetectChips()
        {
            int n = Math.Min(Environment.ProcessorCount, 64);
            MaskAll = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
            var cores = Native.CoreMasks().OrderBy(m => m).ToList();
            CoreCount = cores.Count > 0 ? cores.Count : n;
            ThreadsPerCore = Math.Max(1, n / CoreCount);
            // one L3 cache per chip (CCD)
            var l3 = Native.CacheMasks(3).Where(m => m != 0).Distinct().OrderBy(m => m).ToList();
            if (l3.Count >= 2) { MaskCCD1 = l3[0]; MaskCCD2 = l3[1]; TwoChips = true; DetectCache(); }
            else
            {
                ulong lo = 0;
                for (int i = 0; i < n / 2; i++) lo |= 1UL << i;
                MaskCCD1 = lo; MaskCCD2 = MaskAll & ~lo; TwoChips = false;
            }
            MaskFirstThreads = 0;
            foreach (var c in cores) MaskFirstThreads |= c & (~c + 1); // lowest bit of each core
            if (MaskFirstThreads == 0) MaskFirstThreads = MaskAll;
            coresCCD1 = CoreNumbers(cores, MaskCCD1, n);
            coresCCD2 = CoreNumbers(cores, MaskCCD2, n);
        }

        // 20.11: which chip has the bigger L3. An X3D processor (7945HX3D, 9950X3D …) carries 3D V-Cache on one chip only,
        // and not always on the first one; games belong there. A difference of half again or more counts (96 against
        // 32 MB); equal chips leave CCD1 the game chip. RT_FAKE_L3=32,96 (MB) makes up the sizes, for the checks.
        void DetectCache()
        {
            try
            {
                var sizes = Native.CacheSizes(3);
                Func<ulong, long> size = m => sizes.Where(kv => kv.Key == m).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                L3First = size(MaskCCD1); L3Second = size(MaskCCD2);
            }
            catch { L3First = L3Second = 0; }
            var fake = Environment.GetEnvironmentVariable("RT_FAKE_L3");
            if (!string.IsNullOrEmpty(fake))
            {
                var p = fake.Split(',');
                long a, b;
                if (p.Length == 2 && long.TryParse(p[0], out a) && long.TryParse(p[1], out b)) { L3First = a << 20; L3Second = b << 20; }
            }
            if (L3First <= 0 || L3Second <= 0 || Math.Max(L3First, L3Second) * 2 < Math.Min(L3First, L3Second) * 3) return;
            X3D = true;
            if (L3Second > L3First) { GameGroup = Group.CCD2; BackGroup = Group.CCD1; }
        }

        int[] CoreNumbers(List<ulong> cores, ulong mask, int n)
        {
            if (cores.Count == 0) return Enumerable.Range(0, n).Where(i => (mask & (1UL << i)) != 0).Select(i => i + 1).ToArray();
            return Enumerable.Range(0, cores.Count).Where(i => (cores[i] & mask) != 0).Select(i => i + 1).ToArray();
        }

        void Tick()
        {
            // a forced scan (rules saved, pause) waits its turn, a timer one just skips
            if (!(force ? Monitor.TryEnter(work, 5000) : Monitor.TryEnter(work))) return;
            try
            {
                List<Rule> rules;
                Settings s;
                lock (sync) { rules = ordered; s = settings; }
                bool full = force || (DateTime.Now - lastFull).TotalSeconds >= 60;
                force = false;
                if (full) { cache.Clear(); lastFull = DateTime.Now; }

                var alive = new HashSet<string>();
                var st = new Dictionary<string, RuleStatus>();
                var now = DateTime.Now;
                double wall = (now - lastSample).TotalSeconds;
                bool sampling = wall >= 2, counting = sampling && wall <= 120; // the first scan and one after a sleep only take the times
                int fgPid = sampling ? ForegroundPid() : 0;
                var cpuNow = new Dictionary<string, TimeSpan>();
                var load = new Dictionary<string, double>();
                var pathOf = new Dictionary<string, string>();
                var front = new HashSet<string>();
                foreach (var p in Process.GetProcesses())
                {
                    int pid = p.Id;
                    string name;
                    try { name = p.ProcessName; } catch { p.Dispose(); continue; }
                    if (pid <= 4 || pid == selfPid) { p.Dispose(); continue; }
                    string key = pid + "|" + name;
                    alive.Add(key);

                    Rule rule;
                    if (!cache.TryGetValue(key, out rule))
                    {
                        string path = Native.GetPath(pid);
                        rule = Match(name, path, rules, s);
                        Rule lr;
                        if (rule == null && s.StoreGames && launched.TryGetValue(key, out lr)) rule = lr; // a game a launcher started
                        cache[key] = rule;
                        if (path != null) paths[key] = path;
                        if (path != null && rule != null && rule != SkipRule) Folders.Remember(rule.Key, path);
                        Enforce(key, pid, name, rule);
                    }

                    string pth;
                    if (sampling && rule == null && paths.TryGetValue(key, out pth) && !pth.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) && !IsLauncher(name, pth)
                        && !NoHints.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        TimeSpan cpu, was;
                        try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.MinValue; }
                        if (cpu != TimeSpan.MinValue)
                        {
                            cpuNow[key] = cpu;
                            if (counting && cpuWas.TryGetValue(key, out was))
                            {
                                var k = pth.ToLowerInvariant(); double v;
                                load.TryGetValue(k, out v); load[k] = v + (cpu - was).TotalSeconds / wall; pathOf[k] = pth;
                                if (pid == fgPid) front.Add(k);
                            }
                        }
                    }
                    p.Dispose();

                    if (rule != null && rule != SkipRule)
                    {
                        RuleStatus rs;
                        if (!st.TryGetValue(rule.Key, out rs)) st[rule.Key] = rs = new RuleStatus();
                        rs.Count++;
                        string sp;
                        if (rs.SamplePath == null && paths.TryGetValue(key, out sp)) rs.SamplePath = sp;
                        if (denied.Contains(key)) rs.Protected = true;
                        if (cpuSet.ContainsKey(key)) rs.Soft = true;
                    }
                }

                foreach (var k in cache.Keys.Where(k => !alive.Contains(k)).ToList()) { cache.Remove(k); paths.Remove(k); }
                foreach (var k in managed.Keys.Where(k => !alive.Contains(k)).ToList()) managed.Remove(k);
                denied.RemoveWhere(k => !alive.Contains(k));
                foreach (var k in cpuSet.Keys.Where(k => !alive.Contains(k)).ToList()) cpuSet.Remove(k);
                foreach (var k in psTried.Keys.Where(k => !alive.Contains(k)).ToList()) psTried.Remove(k);
                viaPs.RemoveWhere(k => !alive.Contains(k));
                foreach (var k in launched.Keys.Where(k => !alive.Contains(k)).ToList()) launched.Remove(k);
                if (sampling) { cpuWas = cpuNow; lastSample = now; }
                lock (sync)
                {
                    status = st; managedCount = managed.Count;
                    double w = Math.Min(wall, 35); // a long gap (the tray's 30 s) counts no more than one scan of it
                    foreach (var kv in load)
                    {
                        if (kv.Value < BusyCores) continue;
                        Hint h;
                        if (!hints.TryGetValue(kv.Key, out h)) hints[kv.Key] = h = new Hint { Path = pathOf[kv.Key] };
                        h.Busy += w; h.LoadSum += kv.Value * w;
                        if (front.Contains(kv.Key)) h.Fg += w;
                    }
                }
            }
            catch (Exception ex) { Store.Log("ошибка: " + ex.Message); }
            finally { Monitor.Exit(work); }
            if (force) Kick();
        }

        void Enforce(string key, int pid, string name, Rule rule)
        {
            ulong target = 0;
            if (!paused && rule != null && rule.Group != Group.SKIP) target = MaskOf(rule);
            if (target == 0)
            {
                if (managed.ContainsKey(key))
                {
                    ulong was;
                    if (cpuSet.ContainsKey(key)) { if (Native.SetCpuSets(pid, 0, true)) Note(name, pid, "все ядра (решает Windows)"); cpuSet.Remove(key); }
                    else if (Native.GetMask(pid, out was) == 0 && was != MaskAll &&
                             (Native.SetMask(pid, MaskAll) || (viaPs.Contains(key) && Native.SetMaskViaPowerShell(pid, MaskAll))))
                        Note(name, pid, "все ядра (решает Windows)");
                    managed.Remove(key);
                    viaPs.Remove(key);
                }
                return;
            }
            var label = rule.Name.Length > 0 ? rule.Name : name;
            ulong cur;
            bool readable = Native.GetMask(pid, out cur) == 0;
            if (readable && cur == target && !cpuSet.ContainsKey(key)) { managed[key] = target; denied.Remove(key); return; }
            if (Native.SetMask(pid, target))
            {
                if (!readable || cur != target) Note(label, pid, Where(rule) +(denied.Contains(key) ? " (античит отпустил)" : ""));
                if (cpuSet.ContainsKey(key)) { Native.SetCpuSets(pid, 0, true); cpuSet.Remove(key); }
                denied.Remove(key);
                managed[key] = target;
                return;
            }
            // refused: the game may only let signed programs in, so PowerShell (signed by Microsoft) tries; at most every 10 s
            DateTime last;
            if (!psTried.TryGetValue(key, out last) || (DateTime.Now - last).TotalSeconds >= 10)
            {
                psTried[key] = DateTime.Now;
                if (Native.SetMaskViaPowerShell(pid, target))
                {
                    Note(label, pid, Where(rule) +" (через PowerShell: программа пускает только подписанные)");
                    if (cpuSet.ContainsKey(key)) { Native.SetCpuSets(pid, 0, true); cpuSet.Remove(key); }
                    viaPs.Add(key);
                    denied.Remove(key);
                    managed[key] = target;
                    return;
                }
            }
            // CPU Sets need fewer rights; the affinity itself is tried again every poll
            ulong soft;
            if (cpuSet.TryGetValue(key, out soft) && soft == target) return;
            if (Native.SetCpuSets(pid, target, false))
            {
                Note(label, pid, Where(rule) +" (через CPU Sets, пока ядра закрыты)");
                cpuSet[key] = target;
                managed[key] = target;
                denied.Remove(key);
                return;
            }
            // CoD (sp26-cod) refuses for its first seconds and allows it later: keep trying every poll
            if (denied.Add(key)) Store.Log(name + " (" + pid + "): пока не даёт поменять ядра, пробую каждые 3 с");
        }

        static string Where(Rule r) { return r.Group + (r.NoSmt ? " без SMT" : ""); }

        void Note(string name, int pid, string where)
        {
            Store.Log(name + " (" + pid + ") -> " + where);
            lock (sync) lastAction = DateTime.Now.ToString("HH:mm") + "  " + name + " → " + where;
        }

        Rule Match(string name, string path, List<Rule> rules, Settings s)
        {
            if (path != null && path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) return null; // Windows itself
            foreach (var r in rules) if (Hit(r, name, path)) return r;
            if (s.StoreGames && path != null) foreach (var r in storeRulesInLaunchers) if (Hit(r, name, path)) return r;
            if (s.SkipLaunchers && IsLauncher(name, path)) return SkipRule;
            if (s.StoreGames && path != null) foreach (var r in storeRules) if (Hit(r, name, path)) return r;
            return null;
        }

        public static bool Hit(Rule r, string name, string path)
        {
            if (r.ByPath) return path != null && path.IndexOf(r.Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            var pat = r.Pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? r.Pattern.Substring(0, r.Pattern.Length - 4) : r.Pattern;
            return string.Equals(name, pat, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsLauncher(string name, string path)
        {
            if (LauncherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return true;
            return path != null && LauncherPaths.Any(p => path.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // --dist-check <file>: where made-up programs land (the store folders, the launchers, what a launcher starts) and
        // what the hints offer. Nothing is started or moved; the chips are this computer's (RT_FAKE_L3 plays an X3D).
        public static string Check()
        {
            var sb = new System.Text.StringBuilder();
            var e = new Engine();
            var s = new Settings();
            Action<bool, string> line = (ok, t) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + t);
            sb.AppendLine("chips: " + (e.TwoChips ? "two" : "one") + ", L3 " + (e.L3First >> 20) + " / " + (e.L3Second >> 20) + " MB, X3D " + e.X3D + ", game " + e.GameGroup + ", background " + e.BackGroup);
            Func<string, Rule> at = p => e.Match(System.IO.Path.GetFileNameWithoutExtension(p), p, new List<Rule>(), s);
            Func<Rule, string> show = r => r == null ? "none" : r == SkipRule ? "SKIP (launcher)" : r.Group + " «" + r.Name + "»";
            var cases = new[]
            {
                new[] { @"D:\SteamLibrary\steamapps\common\Game\game.exe", "game" },
                new[] { @"C:\XboxGames\Halo Infinite\Content\HaloInfinite.exe", "game" },
                new[] { @"C:\Riot Games\VALORANT\live\VALORANT.exe", "game" },
                new[] { @"C:\Riot Games\Riot Client\RiotClientServices.exe", "skip" },
                new[] { @"C:\Program Files\EA Games\Battlefield 2042\BF2042.exe", "game" },
                new[] { @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Far Cry 6\bin\FarCry6.exe", "game" },
                new[] { @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\upc.exe", "skip" },
                new[] { @"C:\Program Files (x86)\GOG Galaxy\Games\Witcher 3\bin\x64\witcher3.exe", "game" },
                new[] { @"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe", "skip" },
                new[] { @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", "skip" },
                new[] { @"C:\Tools\notepad2.exe", "none" },
            };
            foreach (var c in cases)
            {
                var r = at(c[0]);
                bool ok = c[1] == "game" ? r != null && r != SkipRule && r.Group == e.GameGroup : c[1] == "skip" ? r == SkipRule : r == null;
                line(ok, c[0] + " -> " + show(r) + " (" + c[1] + " expected)");
            }
            s.StoreGames = false;
            line(at(@"C:\XboxGames\Halo Infinite\Content\HaloInfinite.exe") == null, "«Новые игры» off: an Xbox game gets nothing");
            s.StoreGames = true;
            // what a launcher starts
            const string bnet = @"C:\Program Files (x86)\Battle.net\Battle.net.exe", steam = @"C:\Program Files (x86)\Steam\steam.exe";
            line(e.FromLauncher("Overwatch", @"D:\Games\Overwatch\_retail_\Overwatch.exe", bnet), "Battle.net starts D:\\Games\\Overwatch: a game");
            line(!e.FromLauncher("Agent", @"C:\ProgramData\Battle.net\Agent\Agent.9000\Agent.exe", bnet), "Battle.net starts its agent: not a game");
            line(!e.FromLauncher("gameoverlayui", @"C:\Program Files (x86)\Steam\gameoverlayui.exe", steam), "Steam starts its overlay from its own folder: not a game");
            line(e.FromLauncher("mygame", @"E:\Emu\mygame.exe", steam), "Steam starts a game added from E:\\: a game");
            line(!e.FromLauncher("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", steam), "Steam opens a link in Edge: not a game");
            line(!e.FromLauncher("tool", @"C:\Tools\tool.exe", @"C:\Windows\explorer.exe"), "Explorer starts a program: not from a launcher");
            // hints
            e.FakeHint(@"D:\Games\G\g.exe", 300, 280, 3.5);
            e.FakeHint(@"C:\Tools\sync.exe", 120, 0, 1.2);
            e.FakeHint(@"C:\Render\render.exe", 600, 500, e.CoreCount);
            e.FakeHint(@"C:\Tools\short.exe", 30, 30, 2);
            var hs = e.Hints();
            Func<string, Hint> h = n => hs.FirstOrDefault(x => x.Name == n);
            line(hs.Count == 3 && h("short") == null, "hints: " + hs.Count + " (3; 30 s busy is not enough)");
            line(h("g") != null && e.Suggest(h("g")) == e.GameGroup, "busy in front -> " + (h("g") != null ? e.Suggest(h("g")).ToString() : "-") + " (the game chip)");
            line(h("sync") != null && e.Suggest(h("sync")) == e.BackGroup, "busy in the background -> " + (h("sync") != null ? e.Suggest(h("sync")).ToString() : "-") + " (the background chip)");
            line(h("render") != null && e.Suggest(h("render")) == Group.ALL, "more cores than a chip has -> " + (h("render") != null ? e.Suggest(h("render")).ToString() : "-") + " (ALL)");
            lock (e.sync) e.ordered = new List<Rule> { new Rule { Group = Group.CCD2, Pattern = @"\G\" } }; // not Apply: that would scan and move the real programs
            line(e.Hints().All(x => x.Name != "g"), "a rule for it: the hint goes");
            return sb.ToString();
        }

        // --dist-live-check <file> <seconds>: the hints' measuring on the real programs: scans every 5 s with no rules, the
        // store folders off and the launchers left alone, so nothing is moved; then what it counted. Start a busy program first.
        public static string LiveCheck(int seconds)
        {
            var e = new Engine();
            lock (e.sync) { e.ordered = new List<Rule>(); e.settings = new Settings { StoreGames = false, SkipLaunchers = true }; }
            var until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until) { e.Tick(); Thread.Sleep(5000); }
            e.Tick();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("managed after the scans: " + e.managed.Count + " (0: nothing was moved)");
            lock (e.sync)
                foreach (var h in e.hints.Values.OrderByDescending(x => x.Busy))
                    sb.AppendLine(h.Name + "  busy " + h.Busy.ToString("0") + " s, in front " + h.Fg.ToString("0") + " s, cores " + h.Load.ToString("0.00") + " -> " + e.Suggest(h) + (h.Busy >= HintBusySeconds ? "  HINT" : "") + "  " + h.Path);
            return sb.ToString();
        }

        // on exit: everything this app pinned gets all cores back
        public void ReleaseAll()
        {
            lock (work) ReleaseAllLocked();
        }

        void ReleaseAllLocked()
        {
            foreach (var key in managed.Keys.ToList())
            {
                int pid;
                if (!int.TryParse(key.Split('|')[0], out pid)) continue;
                if (cpuSet.ContainsKey(key)) Native.SetCpuSets(pid, 0, true);
                else if (!Native.SetMask(pid, MaskAll) && viaPs.Contains(key)) Native.SetMaskViaPowerShell(pid, MaskAll);
            }
            managed.Clear();
            Store.Log("выход: все программы снова на всех ядрах");
        }
    }
}
