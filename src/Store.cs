using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RedTune
{
    class CurvePreset
    {
        public string Name;
        public int[] Values = new int[Hw.CurveCores]; // one per core of this processor (Hw)
        public bool BuiltIn; // "Заводской (0)": always there, can't be changed
        // 14.11: how the curve is set. 0 = a value per core (Values as they are); 1 = one value per chiplet (Chip);
        // 2 = one value for every core (All). Values is always what goes to the processor, so the rest of the app reads
        // it as before; Core keeps the per-core set while a group mode is on, to come back to.
        public int Mode;
        public int[] Chip; // null: never set
        public int? All;
        public int[] Core; // null: Values
        public CurvePreset Clone()
        {
            return new CurvePreset { Name = Name, Values = (int[])Values.Clone(), BuiltIn = BuiltIn, Mode = Mode, All = All,
                Chip = Chip == null ? null : (int[])Chip.Clone(), Core = Core == null ? null : (int[])Core.Clone() };
        }
    }

    class HistoryEntry
    {
        public DateTime Time;
        public string Source, Preset, Result;
        public int[] Values;
    }

    class Settings
    {
        public string ActivePreset = "";
        public bool ApplyOnStart = true; // 23.11: the curve page's one «Применять автоматически» (start, after sleep, another power source)
        public bool CloseToTray = true;
        public int UiScale = 100;
        public int AppsSort;                    // 23.11: «Удаление программ» order: 0 by name, 1 newest first, 2 oldest first
        public string Language = "ru";
        public string P95Path = "";
        public string TunePlan = "";            // the id of the Windows power plan made for tuning (WinPower.MakeTuningPlan), to find it again
        public string TuneLoad = "p95";         // the heavy load of the auto-tune: "p95" Prime95, "own" the program's own test (Stress), on trial
        public string TuneMode = "daily";       // 18.11: what the quick tune looks for: "daily" a deep curve for everyday use, "strict" one that holds with any set of busy cores
        // fan curve extras (FanControl): safety net and emergency on, the rest off
        public string AcmOff = ""; // 20.11: monitors (EDID «maker:product», hex) whose automatic colour management the user turned off: kept off (ColorKeep)
        public bool HzAuto; public int HzAc, HzDc; // 20.11: the built-in screen's refresh rate by the power source (DisplayAuto); 0 = the highest / 60
        public bool FanSafety = true, FanCritical = true, FanHyst, FanEma, FanGlide, FanBoost, FanSync, FanPredict; public bool SyncBrightness; // 18.11: one screen brightness for all power plans (BrightnessSync), off by default
        // on-screen notifications
        public bool OsdOn = true, OsdTopmost = false, OsdAllMonitors = false;
        // 16.11: the overlay over games (Overlay): on, over every program or only full-screen ones, the corner (0..3)
        public bool OverlayOn = false, OverlayAlways = false;
        public int OverlayCorner = 0, OverlaySize = 1; // the place: 0..3 the corners, 4 top centre; the size: 0 small .. 4 huge
        public int AsusMode = -1, AsusCharge = 0, AsusLight = -1; // ASUS laptops (19.11): the performance mode (0 quiet, 1 balanced, 2 performance), the charge limit in percent and the keyboard light picked here; -1 / 0 = not picked
        public bool CompByFreq = false;         // «Звук»: the compressor's picture shows the bands across the frequencies instead of the last seconds (19.11)
        public bool OsdFollow = false;          // on the monitor of the window in front instead of the main one (16.11)
        public string OsdPosition = "bottom";   // bottom, top, center, bottomleft, bottomright, topleft, topright
        public int OsdMs = 2000;
        public string OsdOff = "";              // comma-separated types switched off
        public string MiniOff = "";             // comma-separated blocks of the mini panel that are not shown (MiniBlocks.Kinds)
        // the glass look: blurred background on/off, blur 0 weak / 1 medium / 2 strong, darkening 0..100 %, top light + border
        public bool OsdGlass = true, OsdShine = true;
        // live glass: follows the screen behind, but notifications are left out of screenshots and screen recordings
        public bool OsdLive = true;
        // «Как после загрузки»: entries that always stay / always close, separated by |: an exe name ("Telegram.exe") = that program,
        // a folder ("C:\Program Files\Cloudflare\Cloudflare WARP") = everything started from it (and, to close, its services)
        public string CleanKeep = "", CleanClose = ""; // 19.11: a new install starts with empty lists (the white one carried a program of the PC this was made on)
        public int CleanVer = 5; // 2: Claude became an ordinary «Не закрывать» entry; 3: folders (exe entries that had services next to them became their folder); 4: Claude white; 5: the browsers and Telegram that 4 put on the black list come off
        public int OsdBlur = 1, OsdTint = 20;
        // power limits written straight to the SMU on AMD laptops without Lenovo's interface (AmdPower); 0 = leave it alone
        public bool AmdPowerOn, AmdPowerAsked; public int AmdPpt, AmdTdc, AmdEdc, AmdStapm, AmdFast, AmdSlow; // 0 = not saved: left alone
        // the sections that can make a PC unstable («Кривая», «Память», the AMD limits): off for a new install, on for settings
        // that existed before the switch (13.11), so nothing disappears for whoever already used them
        public bool Expert;
        public bool UnverifiedSeen; // «Эта модель ещё не проверена» on Home was hidden
        // 21.11: a look at GitHub for a new version once a day (Updater); the time of the last look
        public bool UpdateCheck = true; public string UpdateLast = "";
        public string CrashSeen = "";
        public string UpdatePrev = ""; // 21.11: the version an update from «О программе» left, for «Вернуть …» // 21.11: the crash record the tray note was already shown for (its time)
        // 16.11: «Сторож» (Watchdog): reads the processor's corrected errors in ordinary use; the offer to go a step deeper
        // after WatchDays quiet days is a switch of its own
        public bool WatchOn, WatchDeepen; public int WatchDays = 5;
        // 20.11: when the black box records: 0 while the watchdog is on, 1 always, 2 only during the tests (an auto-tune
        // and a core-curve measurement record in every mode: the auto-tune tells a reset by it)
        public int BlackBoxMode;
        // 21.11: Fn+Q goes round through «Свой» too (PowerService.ModeNow)
        public bool FnQCustom = true;
        public bool GpuAsked;       // the first «Применить» on «Видеокарта» was confirmed
        public bool NavCompact;     // the side menu shows only its icons (13.11)
        public int OsdLook = 2; // 2 = the clear glass (12.11): the old default darkening 47 % moves to 20 % once
    }

    // Everything lives in %APPDATA%\RedTune as plain text: settings.ini, curves.txt, history.txt, redtune.log
    static class Store
    {
        public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedTune");
        static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);
        public const string FactoryName = "Заводской (0)";

        static string P(string name) { Directory.CreateDirectory(DataDir); return Path.Combine(DataDir, name); }

        // 13.11: the program was called Legion Tune and kept everything in %APPDATA%\LegionTune. The folder is moved
        // whole, once, before anything reads or writes it (presets, curves, «Распределение», «Память», the log).
        // Nothing is merged: when the new folder already exists the old one is left alone.
        public static void MoveOld()
        {
            try
            {
                var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Legion" + "Tune");
                if (!Directory.Exists(old) || Directory.Exists(DataDir)) return;
                Directory.Move(old, DataDir);
                var log = Path.Combine(DataDir, "legion" + "tune.log");
                if (File.Exists(log) && !File.Exists(Path.Combine(DataDir, "redtune.log"))) File.Move(log, Path.Combine(DataDir, "redtune.log"));
                Log("настройки перенесены из " + old + " (программа переименована в Red Tune)");
            }
            catch { } // in use or no rights: the app starts with fresh settings, the old folder stays where it was
        }

        public static string Join(int[] v) { return string.Join(",", v.Select(x => x.ToString(CultureInfo.InvariantCulture))); }

        public static int[] Split(string s)
        {
            var parts = s.Split(',');
            if (parts.Length < 1) return null;
            var v = new int[parts.Length]; // any core count; Hw.Fit brings it to this processor
            for (int i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return null;
            return v;
        }

        // ---------- settings ----------

        internal static string FirstLanguage() { try { var two = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant(); return System.Array.IndexOf(L.Codes, two) >= 0 ? two : "en"; } catch { return "en"; } }
        public static Settings LoadSettings()
        {
            var s = new Settings();
            bool sawLook = false, sawCleanVer = false, sawExpert = false;
            var f = P("settings.ini");
            if (!File.Exists(f)) { s.Language = FirstLanguage(); return s; } // 19.11: a first start speaks the language of Windows where the program has it, English elsewhere (it was Russian everywhere)
            foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                bool b = v == "1";
                switch (k)
                {
                    case "ActivePreset": s.ActivePreset = v; break;
                    case "ApplyOnStart": s.ApplyOnStart = b; break;
                    case "AppsSort": int.TryParse(v, out s.AppsSort); break;
                    case "CloseToTray": s.CloseToTray = b; break;
                    case "UiScale": int z; if (int.TryParse(v, out z) && z >= 75 && z <= 200) s.UiScale = z; break;
                    case "P95Path": s.P95Path = v; break;
                    case "TuneLoad": if (v == "own" || v == "p95") s.TuneLoad = v; break;
                    case "TuneMode": if (v == "daily" || v == "strict") s.TuneMode = v; break;
                    case "TunePlan": s.TunePlan = v; break;
                    case "FanSafety": s.FanSafety = b; break;
                    case "FanCritical": s.FanCritical = b; break;
                    case "FanHyst": s.FanHyst = b; break;
                    case "FanEma": s.FanEma = b; break;
                    case "FanGlide": s.FanGlide = b; break;
                    case "FanBoost": s.FanBoost = b; break;
                    case "FanSync": s.FanSync = b; break;
                    case "FanPredict": s.FanPredict = b; break; case "SyncBrightness": s.SyncBrightness = b; break;
                    case "HzAuto": s.HzAuto = b; break;
                    case "AcmOff": s.AcmOff = v; break;
                    case "HzAc": int hza; if (int.TryParse(v, out hza) && hza >= 0 && hza <= 1000) s.HzAc = hza; break;
                    case "HzDc": int hzd; if (int.TryParse(v, out hzd) && hzd >= 0 && hzd <= 1000) s.HzDc = hzd; break;
                    case "Language": s.Language = System.Array.IndexOf(L.Codes, v) >= 0 ? v : "ru"; break;
                    case "OsdOn": s.OsdOn = b; break;
                    case "OsdTopmost": s.OsdTopmost = b; break;
                    case "OsdAllMonitors": s.OsdAllMonitors = b; break;
                    case "OsdFollow": s.OsdFollow = b; break;
                    case "CompByFreq": s.CompByFreq = b; break;
                    case "AsusMode": { int n; if (int.TryParse(v, out n) && n >= -1 && n <= 2) s.AsusMode = n; break; }
                    case "AsusCharge": { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 100) s.AsusCharge = n; break; }
                    case "AsusLight": { int n; if (int.TryParse(v, out n) && n >= -1 && n <= 3) s.AsusLight = n; break; }
                    case "OverlayOn": s.OverlayOn = b; break;
                    case "OverlaySize": int os; if (int.TryParse(v, out os) && os >= 0 && os <= 4) s.OverlaySize = os; break;
                    case "OverlayAlways": s.OverlayAlways = b; break;
                    case "OverlayCorner": int oc; if (int.TryParse(v, out oc) && oc >= 0 && oc <= 4) s.OverlayCorner = oc; break;
                    case "OsdPosition": s.OsdPosition = v; break;
                    case "OsdMs": int ms; if (int.TryParse(v, out ms) && ms >= 500 && ms <= 10000) s.OsdMs = ms; break;
                    case "OsdOff": s.OsdOff = v; break;
                    case "MiniOff": s.MiniOff = v; break;
                    case "OsdGlass": s.OsdGlass = b; break;
                    case "OsdShine": s.OsdShine = b; break;
                    case "OsdLive": s.OsdLive = b; break;
                    case "CleanKeep": s.CleanKeep = v; break;
                    case "CleanVer": int cv; if (int.TryParse(v, out cv)) s.CleanVer = cv; sawCleanVer = true; break;
                    case "CleanClose": s.CleanClose = v; break;
                    case "OsdBlur": int bl; if (int.TryParse(v, out bl) && bl >= 0 && bl <= 2) s.OsdBlur = bl; break;
                    case "OsdLook": int lk; if (int.TryParse(v, out lk)) s.OsdLook = lk; sawLook = true; break;
                    case "OsdTint": int ti; if (int.TryParse(v, out ti) && ti >= 0 && ti <= 100) s.OsdTint = ti; break;
                    case "AmdPowerOn": s.AmdPowerOn = v == "1"; break;
                    case "AmdPowerAsked": s.AmdPowerAsked = v == "1"; break;
                    case "Expert": s.Expert = v == "1"; sawExpert = true; break;
                    case "UnverifiedSeen": s.UnverifiedSeen = v == "1"; break;
                    case "UpdateCheck": s.UpdateCheck = v == "1"; break;
                    case "UpdateLast": s.UpdateLast = v; break;
                    case "CrashSeen": s.CrashSeen = v; break;
                    case "UpdatePrev": s.UpdatePrev = v; break;
                    case "WatchOn": s.WatchOn = v == "1"; break;
                    case "FnQCustom": s.FnQCustom = v == "1"; break;
                    case "WatchDeepen": s.WatchDeepen = v == "1"; break;
                    case "BlackBoxMode": int bm; if (int.TryParse(v, out bm) && bm >= 0 && bm <= 2) s.BlackBoxMode = bm; break;
                    case "WatchDays": int wd; if (int.TryParse(v, out wd) && wd > 0) s.WatchDays = wd; break;
                    case "GpuAsked": s.GpuAsked = v == "1"; break;
                    case "NavCompact": s.NavCompact = v == "1"; break;
                    case "AmdPpt": int ap; if (int.TryParse(v, out ap)) s.AmdPpt = ap; break;
                    case "AmdTdc": int at2; if (int.TryParse(v, out at2)) s.AmdTdc = at2; break;
                    case "AmdEdc": int ae; if (int.TryParse(v, out ae)) s.AmdEdc = ae; break;
                    case "AmdStapm": int ast; if (int.TryParse(v, out ast)) s.AmdStapm = ast; break;
                    case "AmdFast": int af; if (int.TryParse(v, out af)) s.AmdFast = af; break;
                    case "AmdSlow": int asl; if (int.TryParse(v, out asl)) s.AmdSlow = asl; break;
                }
            }
            if (!sawCleanVer && s.CleanKeep.IndexOf("claude.exe", StringComparison.OrdinalIgnoreCase) < 0) s.CleanKeep = s.CleanKeep.Length > 0 ? "claude.exe|" + s.CleanKeep : "claude.exe";
            if (!sawExpert) s.Expert = true; // settings from before the switch
            if (!sawLook && s.OsdTint == 47) s.OsdTint = 20; // the old default was for the dark frosted look
            return s;
        }

        public static void SaveSettings(Settings s)
        {
            Func<bool, string> b = x => x ? "1" : "0";
            File.WriteAllLines(P("settings.ini"), new[]
            {
                "ActivePreset=" + s.ActivePreset, "ApplyOnStart=" + b(s.ApplyOnStart), "CloseToTray=" + b(s.CloseToTray),
                "UiScale=" + s.UiScale, "AppsSort=" + s.AppsSort, "Language=" + s.Language, "P95Path=" + s.P95Path, "TuneLoad=" + s.TuneLoad, "TuneMode=" + s.TuneMode, "TunePlan=" + s.TunePlan,
                "FanSafety=" + b(s.FanSafety), "FanCritical=" + b(s.FanCritical), "FanHyst=" + b(s.FanHyst), "FanEma=" + b(s.FanEma),
                "FanGlide=" + b(s.FanGlide), "FanBoost=" + b(s.FanBoost), "FanSync=" + b(s.FanSync), "FanPredict=" + b(s.FanPredict), "SyncBrightness=" + b(s.SyncBrightness), "HzAuto=" + b(s.HzAuto), "AcmOff=" + s.AcmOff, "HzAc=" + s.HzAc, "HzDc=" + s.HzDc,
                "OsdOn=" + b(s.OsdOn), "OsdTopmost=" + b(s.OsdTopmost), "OsdAllMonitors=" + b(s.OsdAllMonitors), "OsdFollow=" + b(s.OsdFollow), "CompByFreq=" + b(s.CompByFreq), "AsusMode=" + s.AsusMode, "AsusCharge=" + s.AsusCharge, "AsusLight=" + s.AsusLight, "OverlayOn=" + b(s.OverlayOn), "OverlayAlways=" + b(s.OverlayAlways), "OverlayCorner=" + s.OverlayCorner, "OverlaySize=" + s.OverlaySize, "OsdPosition=" + s.OsdPosition, "OsdMs=" + s.OsdMs, "OsdOff=" + s.OsdOff, "MiniOff=" + s.MiniOff,
                "OsdGlass=" + b(s.OsdGlass), "OsdShine=" + b(s.OsdShine), "OsdLive=" + b(s.OsdLive), "CleanKeep=" + s.CleanKeep, "CleanClose=" + s.CleanClose, "CleanVer=" + s.CleanVer, "OsdBlur=" + s.OsdBlur, "OsdTint=" + s.OsdTint, "OsdLook=" + s.OsdLook, "AmdPowerOn=" + b(s.AmdPowerOn), "AmdPowerAsked=" + b(s.AmdPowerAsked), "Expert=" + b(s.Expert), "UnverifiedSeen=" + b(s.UnverifiedSeen), "UpdateCheck=" + b(s.UpdateCheck), "UpdateLast=" + s.UpdateLast, "CrashSeen=" + s.CrashSeen, "UpdatePrev=" + s.UpdatePrev,"WatchOn=" + b(s.WatchOn), "FnQCustom=" + b(s.FnQCustom), "WatchDeepen=" + b(s.WatchDeepen), "WatchDays=" + s.WatchDays, "BlackBoxMode=" + s.BlackBoxMode, "GpuAsked=" + b(s.GpuAsked), "NavCompact=" + b(s.NavCompact), "AmdPpt=" + s.AmdPpt, "AmdTdc=" + s.AmdTdc, "AmdEdc=" + s.AmdEdc, "AmdStapm=" + s.AmdStapm, "AmdFast=" + s.AmdFast, "AmdSlow=" + s.AmdSlow,
            }, Utf8Bom);
        }

        // ---------- curve presets: "name|v0,...,v15" ----------

        public static List<CurvePreset> LoadPresets()
        {
            var list = new List<CurvePreset>();
            var f = P("curves.txt");
            if (File.Exists(f))
                foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    int i = line.LastIndexOf('|');
                    if (i <= 0) continue;
                    var v = Split(line.Substring(i + 1));
                    var name = line.Substring(0, i).Trim();
                    if (v != null && name.Length > 0 && name != FactoryName) list.Add(new CurvePreset { Name = name, Values = Hw.Fit(v) });
                }
            list.Add(new CurvePreset { Name = FactoryName, BuiltIn = true });
            // the group modes, in a file of their own so curves.txt stays what it was: "name|mode|chip|all|core" ("-" = none)
            var fm = P("curves_mode.txt");
            if (File.Exists(fm))
                foreach (var line in File.ReadAllLines(fm, Encoding.UTF8))
                {
                    var p = line.Split('|');
                    if (p.Length < 5) continue;
                    var pr = list.FirstOrDefault(x => x.Name == p[0] && !x.BuiltIn);
                    int mode, all;
                    if (pr == null || !int.TryParse(p[1], out mode)) continue;
                    pr.Chip = p[2] == "-" ? null : Split(p[2]);
                    pr.All = p[3] != "-" && int.TryParse(p[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out all) ? all : (int?)null;
                    pr.Core = p[4] == "-" ? null : Split(p[4]);
                    if (pr.Core != null) pr.Core = Hw.Fit(pr.Core);
                    pr.Mode = mode == 1 && pr.Chip != null && pr.Chip.Length == Hw.CurveChips && Hw.CurveChips > 1 ? 1 : mode == 2 && pr.All.HasValue ? 2 : 0;
                }
            return list;
        }

        public static void SavePresets(IEnumerable<CurvePreset> presets)
        {
            if (Hw.Simulated) return; // a simulated smaller CPU must not cut the real curves
            File.WriteAllLines(P("curves.txt"), presets.Where(p => !p.BuiltIn).Select(p => p.Name.Replace("|", "/") + "|" + Join(p.Values)), Utf8Bom);
            File.WriteAllLines(P("curves_mode.txt"), presets.Where(p => !p.BuiltIn && (p.Mode != 0 || p.Chip != null || p.All.HasValue)).Select(p =>
                string.Join("|", p.Name.Replace("|", "/"), p.Mode.ToString(), p.Chip == null ? "-" : Join(p.Chip),
                    p.All.HasValue ? p.All.Value.ToString(CultureInfo.InvariantCulture) : "-", p.Core == null ? "-" : Join(p.Core))), Utf8Bom);
        }

        // ---------- history: "time|source|preset|values|result" ----------

        public static void AddHistory(HistoryEntry e)
        {
            var line = string.Join("|", e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Clean(e.Source), Clean(e.Preset), Join(e.Values), Clean(e.Result));
            File.AppendAllText(P("history.txt"), line + Environment.NewLine, Utf8Bom);
        }

        static string Clean(string s) { return (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " "); }

        public static List<HistoryEntry> LoadHistory(int max = 300)
        {
            var list = new List<HistoryEntry>();
            var f = P("history.txt");
            if (!File.Exists(f)) return list;
            foreach (var line in File.ReadAllLines(f, Encoding.UTF8).Reverse())
            {
                var p = line.Split('|');
                DateTime t;
                if (p.Length < 5 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                var v = Split(p[3]);
                if (v == null) continue;
                list.Add(new HistoryEntry { Time = t, Source = p[1], Preset = p[2], Values = Hw.Fit(v), Result = p[4] });
                if (list.Count >= max) break;
            }
            return list;
        }

        // 20.11: a failure that is caught and gone on from (the program keeps working) still goes to the log, once per
        // place and kind of error an hour: a sensor that fails every half second writes one line an hour, not seven thousand.
        // Never throws.
        static readonly Dictionary<string, DateTime> quietSeen = new Dictionary<string, DateTime>();
        public static void Quiet(string where, Exception ex)
        {
            try
            {
                string what = ex == null ? "?" : ex.GetType().Name + ": " + (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                string key = where + "|" + what;
                lock (quietSeen)
                {
                    DateTime last;
                    if (quietSeen.TryGetValue(key, out last) && (DateTime.Now - last).TotalHours < 1) return;
                    if (quietSeen.Count > 500) quietSeen.Clear();
                    quietSeen[key] = DateTime.Now;
                }
                Log("тихая ошибка (" + where + "): " + what);
            }
            catch { }
        }

        public static void Log(string text)
        {
            try
            {
                var f = P("redtune.log");
                if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024) { try { File.Delete(f + ".old"); File.Move(f, f + ".old"); } catch { File.Delete(f); } } // 18.11: kept as .old, a tune's notes were lost with it
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + text + Environment.NewLine, Utf8Bom);
            }
            catch { }
        }
    }
}
