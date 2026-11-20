using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RedTune.Dist
{
    // Where a rule's program lives, for "Открыть папку" even when it isn't running:
    // 1) the exe last seen running for the rule (remembered in paths.txt), 2) the pattern looked up in the usual install places.
    static class Folders
    {
        static readonly object gate = new object();
        static Dictionary<string, string> seen; // rule key -> exe path
        static string FilePath { get { return Path.Combine(Store.DataDir, "paths.txt"); } }

        static void Load()
        {
            if (seen != null) return;
            seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int t = line.IndexOf('\t');
                        if (t > 0) seen[line.Substring(0, t)] = line.Substring(t + 1);
                    }
            }
            catch { }
        }

        // called by the engine for every process it matched (background thread)
        public static void Remember(string ruleKey, string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            lock (gate)
            {
                Load();
                string old;
                if (seen.TryGetValue(ruleKey, out old) && string.Equals(old, exe, StringComparison.OrdinalIgnoreCase)) return;
                seen[ruleKey] = exe;
                try
                {
                    Directory.CreateDirectory(Store.DataDir);
                    File.WriteAllLines(FilePath, seen.Select(kv => kv.Key + "\t" + kv.Value), Encoding.UTF8);
                }
                catch { }
            }
        }

        // the program ran at some point (its exe path is remembered)
        public static bool WasSeen(string ruleKey) { lock (gate) { Load(); return seen.ContainsKey(ruleKey); } }

        // the folder the rule stands for: for "\Epic Games\" the Epic Games folder itself, for an exe or a process name the exe's folder
        public static string FromExe(Rule r, string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            if (r.ByPath && !r.Pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                int i = exe.IndexOf(r.Pattern, StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    int end = i + r.Pattern.Length;
                    if (!r.Pattern.EndsWith("\\")) { int slash = exe.IndexOf('\\', end); end = slash < 0 ? exe.Length : slash + 1; }
                    var dir = exe.Substring(0, end);
                    if (Directory.Exists(dir)) return dir;
                }
            }
            var d = Path.GetDirectoryName(exe);
            return Directory.Exists(d) ? d : null;
        }

        // slow (disk lookups): call it off the UI thread
        public static string Find(Rule r)
        {
            string exe;
            lock (gate) { Load(); seen.TryGetValue(r.Key, out exe); }
            if (exe != null && File.Exists(exe)) { var f = FromExe(r, exe); if (f != null) return f; }
            try { return r.ByPath ? FindByPath(r.Pattern) : FindByName(r.Pattern); } catch { return null; }
        }

        static IEnumerable<string> Roots()
        {
            var list = new List<string>();
            Func<Environment.SpecialFolder, string> sf = Environment.GetFolderPath;
            string local = sf(Environment.SpecialFolder.LocalApplicationData);
            list.Add(sf(Environment.SpecialFolder.ProgramFiles));
            list.Add(Environment.GetEnvironmentVariable("ProgramW6432"));
            list.Add(sf(Environment.SpecialFolder.ProgramFilesX86));
            list.Add(Path.Combine(local, "Programs"));
            list.Add(local);
            list.Add(sf(Environment.SpecialFolder.ApplicationData));
            list.Add(sf(Environment.SpecialFolder.CommonApplicationData));
            list.Add(sf(Environment.SpecialFolder.DesktopDirectory));
            list.Add(sf(Environment.SpecialFolder.UserProfile));
            foreach (var d in DriveInfo.GetDrives()) if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.RootDirectory.FullName);
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrEmpty(steam)) { steam = steam.Replace('/', '\\'); list.Add(steam); list.Add(Path.GetDirectoryName(steam.TrimEnd('\\'))); }
            return list.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // root + pattern, then one folder deeper (D:\Games\ + \Epic Games\)
        static string FindByPath(string pattern)
        {
            var roots = Roots().ToList();
            foreach (var root in roots) { var hit = Probe(root, pattern); if (hit != null) return hit; }
            foreach (var root in roots)
            {
                string[] subs;
                try { subs = Directory.GetDirectories(root + "\\"); } catch { continue; }
                foreach (var sub in subs.Take(400)) { var hit = Probe(sub.TrimEnd('\\'), pattern); if (hit != null) return hit; }
            }
            return null;
        }

        static string Probe(string root, string pattern)
        {
            var full = root + (pattern.StartsWith("\\") ? "" : "\\") + pattern;
            if (pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return File.Exists(full) ? Path.GetDirectoryName(full) : null;
            if (pattern.EndsWith("\\")) return Directory.Exists(full) ? full : null;
            // a name prefix: \WindowsApps\Claude_ -> the first folder starting with it
            int slash = full.LastIndexOf('\\');
            var parent = full.Substring(0, slash + 1);
            var prefix = full.Substring(slash + 1);
            if (!Directory.Exists(parent)) return null;
            try { var d = Directory.GetDirectories(parent, prefix + "*").FirstOrDefault(); return d == null ? null : d + "\\"; } catch { return null; }
        }

        // the folder of an exe by its name (App Paths, the usual install places, the installed programs): slow, off the UI thread
        public static string FindExeFolder(string exeName) { try { return FindByName(exeName); } catch { return null; } }

        static string FindByName(string pattern)
        {
            var exe = pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pattern : pattern + ".exe";
            foreach (var hive in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
            {
                var p = Registry.GetValue(hive + @"\Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe, "", null) as string;
                if (!string.IsNullOrEmpty(p)) { p = p.Trim('"'); if (File.Exists(p)) return Path.GetDirectoryName(p); }
            }
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;
            if (!string.IsNullOrEmpty(steam) && string.Equals(Path.GetFileName(steam), exe, StringComparison.OrdinalIgnoreCase) && File.Exists(steam)) return Path.GetDirectoryName(steam.Replace('/', '\\'));
            foreach (var root in Roots())
            {
                string[] subs;
                try { subs = Directory.GetDirectories(root + "\\"); } catch { continue; }
                foreach (var sub in subs.Take(400))
                {
                    var f = Path.Combine(sub, exe);
                    if (File.Exists(f)) return sub;
                    f = Path.Combine(sub, "bin", exe);
                    if (File.Exists(f)) return Path.GetDirectoryName(f);
                }
            }
            return FindInInstalled(exe);
        }

        // helpers sit deep inside their program (Steam\bin\cef\cef.win64\steamwebhelper.exe, Epic Games\Launcher\Engine\Binaries\Win64\EpicWebHelper.exe):
        // the folders of the installed programs, the ones named like the exe first, a few levels down, shallow levels first
        static string FindInInstalled(string exe)
        {
            var want = Norm(Path.GetFileNameWithoutExtension(exe));
            var roots = new List<Tuple<string, bool>>(); // folder, named like the exe
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var a in Uninst.List())
            {
                var like = Regex.Split(a.Name + " " + a.Publisher, @"[^\p{L}\p{N}]+").Any(w => w.Length >= 4 && want.Contains(Norm(w)));
                foreach (var d in new[] { a.Location, Path.GetDirectoryName(Startup.ExePath(a.Uninstall) ?? "") })
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    var dir = d.Trim().Trim('"').TrimEnd('\\');
                    if (dir.Length < 4 || dir.StartsWith(win, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir)) continue;
                    roots.Add(Tuple.Create(dir, like));
                }
            }
            foreach (var root in roots.OrderByDescending(r => r.Item2).Select(r => r.Item1).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var level = new List<string> { root };
                int seen = 0;
                for (int depth = 0; depth <= 5 && level.Count > 0 && seen < 20000; depth++)
                {
                    var next = new List<string>();
                    foreach (var d in level)
                    {
                        if (File.Exists(Path.Combine(d, exe))) return d;
                        if (++seen >= 20000) break;
                        try
                        {
                            foreach (var sub in Directory.GetDirectories(d))
                            {
                                if (Skip.Contains(Path.GetFileName(sub))) continue; // game libraries and the like: huge, and no helpers of the program inside
                                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                                next.Add(sub);
                            }
                        }
                        catch { }
                    }
                    level = next;
                }
            }
            return null;
        }

        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steamapps", "node_modules", ".git", "cache", "logs", "dumps", "userdata", "depotcache", "appcache" };
        static string Norm(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        // the exe itself is selected when it's known, otherwise the folder opens
        public static void Open(string folder, string exe)
        {
            if (exe != null && File.Exists(exe) && folder != null && exe.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(exe), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Process.Start("explorer.exe", "/select,\"" + exe + "\"");
            else Process.Start("explorer.exe", "\"" + folder.TrimEnd('\\') + "\"");
        }
    }
}
