using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RedTune.Dist
{
    enum Group { CCD1, CCD2, ALL, SKIP }

    class Rule
    {
        public Group Group;
        public string Pattern = "";
        public string Name = "";
        public bool Enabled = true;
        public bool NoSmt; // one thread per core: the second SMT thread of each core is left out
        public Rule Origin; // in the window's draft: the saved rule this one was copied from (null = new)

        // a pattern with "\" is a piece of the exe path, without it the exact process name
        public bool ByPath { get { return Pattern.Contains("\\"); } }
        public string Key { get { return Pattern.ToLowerInvariant(); } }
        public Rule Clone() { return new Rule { Group = Group, Pattern = Pattern, Name = Name, Enabled = Enabled, NoSmt = NoSmt }; }

        // folders that say nothing about the program: skipped when a pattern or a name is made from a path
        public static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "bin32", "x64", "x86", "win64", "win32", "application", "app", "binaries", "current", "program", "release", "client", "retail", "game",
        };
        static readonly HashSet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "editor", "hub", "common", "steamapps" };

        public static bool IsVersionFolder(string s) { return Regex.IsMatch(s, @"^(app-)?v?\d"); }

        public static string DefaultName(string pattern)
        {
            var trimmed = pattern.Trim('\\');
            if (trimmed.Equals(@"steamapps\common", StringComparison.OrdinalIgnoreCase)) return "Игры Steam";
            if (trimmed.Equals("Epic Games", StringComparison.OrdinalIgnoreCase)) return "Игры Epic";
            var parts = pattern.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return pattern;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                var p = parts[i];
                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
                if (Generic.Contains(p) || GenericNames.Contains(p) || IsVersionFolder(p)) continue;
                return p.TrimEnd('_', '.', ' ');
            }
            return parts[parts.Length - 1];
        }
    }

    class Settings
    {
        public bool StoreGames = true, SkipLaunchers = true, TrayHintShown;
        public int UiScale = 100; // window scale on top of the Windows one: 100, 125, 150, 175
        public Settings Clone() { return (Settings)MemberwiseClone(); }
    }

    // rules.txt keeps the format of the old Distribute.ps1 script, plus an optional "| name" and "#off " for disabled rules
    static class Store
    {
        // inside Red Tune's own folder, like everything else of the app (%APPDATA%\RedTune\Distribution)
        public static string DataDir = Path.Combine(RedTune.Store.DataDir, "Distribution");
        static readonly string OldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Distribution");

        // once: the folder of the old separate app (%APPDATA%\Distribution) moves in here, then the old one is gone.
        // A file already here wins; the old folder is deleted only when every file of it is here.
        public static void MoveOld()
        {
            try
            {
                if (!Directory.Exists(OldDir)) return;
                Directory.CreateDirectory(RedTune.Store.DataDir);
                if (!Directory.Exists(DataDir)) { Directory.Move(OldDir, DataDir); RedTune.Store.Log("распределение: данные перенесены из " + OldDir); return; }
                bool all = Directory.GetDirectories(OldDir).Length == 0; // nothing of ours lives in subfolders: leave such a folder alone
                foreach (var f in Directory.GetFiles(OldDir))
                {
                    var dst = Path.Combine(DataDir, Path.GetFileName(f));
                    try { if (!File.Exists(dst)) File.Copy(f, dst); } catch { all = false; }
                }
                if (all) { Directory.Delete(OldDir, true); RedTune.Store.Log("распределение: данные перенесены из " + OldDir); }
                else RedTune.Store.Log("распределение: старая папка " + OldDir + " оставлена — не всё скопировалось");
            }
            catch (Exception ex) { RedTune.Store.Log("распределение: перенос из " + OldDir + " не удался: " + ex.Message); }
        }
        public static string RulesPath { get { return Path.Combine(DataDir, "rules.txt"); } }
        static string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }
        static string LogPath { get { return Path.Combine(DataDir, "distribution.log"); } }
        static readonly Regex Line = new Regex(@"^(CCD1|CCD2|ALL|SKIP)\s+(.+?)(?:\s+\|\s*(.*))?$", RegexOptions.IgnoreCase);
        static readonly object logLock = new object();

        // back: the background chip (20.11: CCD1 on an X3D whose cache is on CCD2), where the first start's rules go
        public static List<Rule> LoadRules(Group back = Group.CCD2)
        {
            Directory.CreateDirectory(DataDir);
            string src = RulesPath;
            if (!File.Exists(src))
            {
                // first start: take rules.txt shipped next to the exe (the installer copies the old script's rules there)
                var near = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.txt");
                if (!File.Exists(near)) return Defaults(back);
                src = near;
            }
            var list = new List<Rule>();
            foreach (var raw in File.ReadAllLines(src, Encoding.UTF8))
            {
                var line = raw.Trim();
                bool enabled = true;
                if (line.StartsWith("#off ", StringComparison.OrdinalIgnoreCase)) { enabled = false; line = line.Substring(5).Trim(); }
                else if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Line.Match(line);
                if (!m.Success) continue;
                Group g;
                if (!Enum.TryParse(m.Groups[1].Value, true, out g)) continue;
                var pattern = m.Groups[2].Value.Trim();
                // "| name | nosmt": flags after a second bar
                var tail = m.Groups[3].Success ? m.Groups[3].Value.Split('|') : new string[0];
                var name = tail.Length > 0 && tail[0].Trim().Length > 0 ? tail[0].Trim() : Rule.DefaultName(pattern);
                bool noSmt = tail.Skip(1).Any(f => f.Trim().Equals("nosmt", StringComparison.OrdinalIgnoreCase));
                list.Add(new Rule { Group = g, Pattern = pattern, Name = name, Enabled = enabled, NoSmt = noSmt });
            }
            return list;
        }

        static List<Rule> Defaults(Group back)
        {
            return new List<Rule>
            {
                new Rule { Group = back, Pattern = @"\Google\Chrome\Application\", Name = "Chrome" },
                new Rule { Group = back, Pattern = @"\Discord\", Name = "Discord" },
                new Rule { Group = back, Pattern = @"\Telegram Desktop\", Name = "Telegram" },
            };
        }

        // 20.11: the hints the user hid («Скрыть»), one exe path a line, lower case
        static string HiddenPath { get { return Path.Combine(DataDir, "hints-hidden.txt"); } }

        public static HashSet<string> LoadHidden()
        {
            try { if (File.Exists(HiddenPath)) return new HashSet<string>(File.ReadAllLines(HiddenPath, Encoding.UTF8).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0)); }
            catch { }
            return new HashSet<string>();
        }

        public static void SaveHidden(IEnumerable<string> paths)
        {
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(HiddenPath, paths.OrderBy(p => p), Encoding.UTF8); } catch { }
        }

        public static void SaveRules(IEnumerable<Rule> rules)
        {
            Directory.CreateDirectory(DataDir);
            var sb = new StringBuilder();
            sb.AppendLine("# Распределение: какие программы на каких ядрах.");
            sb.AppendLine("# ГРУППА  шаблон  | название      CCD1, CCD2, ALL; SKIP = не трогать; \"#off \" = правило выключено");
            sb.AppendLine("# Шаблон с \"\\\" ищется в пути к exe, без \"\\\" — точное имя процесса без .exe; \"| nosmt\" в конце = один поток на ядро");
            sb.AppendLine();
            foreach (var r in rules)
                sb.AppendLine((r.Enabled ? "" : "#off ") + r.Group.ToString().PadRight(5) + " " + r.Pattern + "  | " + r.Name + (r.NoSmt ? " | nosmt" : ""));
            File.WriteAllText(RulesPath, sb.ToString(), new UTF8Encoding(true));
        }

        public static Settings LoadSettings()
        {
            var s = new Settings();
            if (!File.Exists(SettingsPath)) return s;
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                bool v = kv[1].Trim() == "1";
                switch (kv[0].Trim())
                {
                    case "StoreGames": s.StoreGames = v; break;
                    case "SkipLaunchers": s.SkipLaunchers = v; break;
                    case "TrayHintShown": s.TrayHintShown = v; break;
                    case "UiScale":
                        int z;
                        if (int.TryParse(kv[1].Trim(), out z) && z >= 100 && z <= 175) s.UiScale = z;
                        break;
                }
            }
            return s;
        }

        public static void SaveSettings(Settings s)
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(SettingsPath, new[]
            {
                "StoreGames=" + (s.StoreGames ? 1 : 0),
                "SkipLaunchers=" + (s.SkipLaunchers ? 1 : 0),
                "TrayHintShown=" + (s.TrayHintShown ? 1 : 0),
                "UiScale=" + s.UiScale,
            });
        }

        public static void Log(string msg)
        {
            lock (logLock)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Copy(LogPath, LogPath + ".old", true);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(LogPath);
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
