using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace RedTune
{
    // «Как после загрузки»: closes what was started after signing in to Windows, as if the PC had just booted.
    // Kept: other sessions and services; anything in the Windows folder (the shell, Explorer windows, system tools);
    // what started by itself (not from Explorer) in the first minute after sign-in or is on in «Автозагрузка», with everything
    // those start later (Steam's helpers…); Red Tune and Claude with all their child processes.
    // The user lists in «Очистка»: the white list («Не закрывать» before 13.11) stays with its children, the black list
    // names close even when they started at boot (never Windows, Red Tune or Claude).
    // An entry is an exe name (that program) or a folder (everything started from it); services inside a «Закрывать всегда»
    // folder are stopped until the next boot.
    // Closing: a window gets a polite close first (it may ask to save); windowless background processes are ended.
    // What does not close in a few seconds is reported back, the page asks before forcing it.
    static class CleanSlate
    {
        public class Proc
        {
            public int Pid, Parent;
            public string Name, Path;
            public DateTime Start;
            public override string ToString() { return Name + " (" + Pid + ")"; }
        }

        const int BootWindowSeconds = 60;

        public static HashSet<string> Names(string list)
        {
            return new HashSet<string>((list ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        }

        // a list entry is a folder when it has a backslash in it, else an exe name
        public static bool IsFolder(string entry) { return entry.IndexOf('\\') >= 0; }

        // one list split into exe names and folders: a process matches by its name or by the folder it runs from
        class Entries
        {
            public readonly HashSet<string> Exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Folders = new List<string>();
            readonly List<System.Text.RegularExpressions.Regex> any = new List<System.Text.RegularExpressions.Regex>(); // "…\Claude_*__pzs8sxrjxfjjc": every version
            public Entries(string list)
            {
                foreach (var e in Names(list))
                {
                    if (!IsFolder(e)) { Exes.Add(e); continue; }
                    if (e.IndexOf('*') < 0) { Folders.Add(Norm(e)); continue; }
                    Folders.Add(e);
                    any.Add(new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(e.TrimEnd('\\')).Replace("\\*", @"[^\\]*") + @"(\\|$)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                }
            }
            public bool InFolder(string path)
            {
                if (path == null) return false;
                var p = Norm(path);
                return Folders.Any(f => f.IndexOf('*') < 0 && Under(p, f)) || any.Any(r => r.IsMatch(p));
            }
            public bool Match(Proc p) { return Exes.Contains(p.Name) || InFolder(p.Path); }
        }

        // a Store app's folder carries its version (WindowsApps\Claude_5.1231.2.0_x64__pzs8sxrjxfjjc) and a new one comes with every
        // update: the entry keeps the name and the publisher only (Claude_*__pzs8sxrjxfjjc), so it matches every version
        static readonly System.Text.RegularExpressions.Regex Package = new System.Text.RegularExpressions.Regex(
            @"^(?<dir>.+\\WindowsApps\\)(?<name>[^_\\]+)_[\d.]+_[^_\\]*_[^_\\]*_(?<pub>[a-z0-9]+)\\?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static string AnyVersion(string entry)
        {
            if (!IsFolder(entry)) return entry;
            var m = Package.Match(entry.Trim());
            return m.Success ? m.Groups["dir"].Value + m.Groups["name"].Value + "_*__" + m.Groups["pub"].Value : entry;
        }

        // the list with every versioned Store-app folder turned into its any-version form
        public static string AnyVersionList(string list)
        {
            return string.Join("|", Names(list).Select(AnyVersion).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        // the real folder of a folder entry: as is, or for "…\Claude_*__pzs…" the installed version (WindowsApps can't be listed,
        // so a running process from it tells); null when it isn't there
        public static string ResolveFolder(string entry)
        {
            var e = entry.TrimEnd('\\');
            if (e.IndexOf('*') < 0) return Directory.Exists(e) ? e : null;
            var parent = Path.GetDirectoryName(e); var leaf = Path.GetFileName(e);
            try { var d = Directory.GetDirectories(parent, leaf).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); if (d != null) return d; } catch { }
            var match = new Entries(entry);
            using (var q = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process"))
                foreach (ManagementObject o in q.Get())
                {
                    var p = o["ExecutablePath"] as string;
                    if (p == null || !match.InFolder(p)) continue;
                    var rest = Norm(p).Substring(parent.Length).TrimStart('\\');
                    int slash = rest.IndexOf('\\');
                    return Path.Combine(parent, slash < 0 ? rest : rest.Substring(0, slash));
                }
            return null;
        }

        // «Claude» for "…\WindowsApps\Claude_*__pzs8sxrjxfjjc", the folder name otherwise
        public static string FolderTitle(string entry)
        {
            var leaf = Path.GetFileName(entry.TrimEnd('\\'));
            int star = leaf.IndexOf("_*_");
            return star > 0 ? leaf.Substring(0, star) : leaf;
        }

        static bool Under(string path, string folder)
        {
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // a folder too wide to be one program: why not, or null when it is fine
        public static string BadFolder(string dir)
        {
            var d = Norm(dir);
            if (string.IsNullOrEmpty(d) || Path.GetPathRoot(d).TrimEnd('\\').Equals(d, StringComparison.OrdinalIgnoreCase)) return "Это корень диска";
            var windows = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (Under(d, windows)) return "Это папка Windows";
            var me = Norm(Path.GetDirectoryName(typeof(CleanSlate).Assembly.Location)); // the exe's own folder, however it was started
            if (Under(me, d) || Under(d, me) || Under(Norm(Store.DataDir), d) || Under(d, Norm(Store.DataDir))) return "В ней Red Tune";
            Func<Environment.SpecialFolder, string> sf = Environment.GetFolderPath;
            var local = sf(Environment.SpecialFolder.LocalApplicationData);
            var wide = new[] { sf(Environment.SpecialFolder.ProgramFiles), sf(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432"),
                sf(Environment.SpecialFolder.CommonApplicationData), sf(Environment.SpecialFolder.UserProfile), Path.GetDirectoryName(sf(Environment.SpecialFolder.UserProfile)),
                sf(Environment.SpecialFolder.ApplicationData), local, Path.Combine(Path.GetDirectoryName(local), "LocalLow"), Path.Combine(local, "Programs"), Path.GetDirectoryName(local),
                sf(Environment.SpecialFolder.DesktopDirectory), sf(Environment.SpecialFolder.MyDocuments), Path.Combine(sf(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(sf(Environment.SpecialFolder.ProgramFiles), "Common Files"), Path.Combine(sf(Environment.SpecialFolder.ProgramFilesX86), "Common Files"),
                Path.Combine(sf(Environment.SpecialFolder.ProgramFiles), "WindowsApps") };
            if (wide.Where(w => !string.IsNullOrEmpty(w)).Any(w => Norm(w).Equals(d, StringComparison.OrdinalIgnoreCase))) return "Это общая папка, в ней много программ";
            return null;
        }

        // settings before folders: an exe in «Закрывать всегда» also stopped the services next to it. Such an entry becomes
        // its folder (Cloudflare WARP.exe → C:\Program Files\Cloudflare\Cloudflare WARP), so it keeps doing what it did.
        // CleanVer 4 (13.11): Claude on the white list and off the black one (it was there as a folder entry)
        public static void Upgrade4(Settings s)
        {
            var black = Names(s.CleanClose).Where(e => e.IndexOf("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) < 0 && !e.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            s.CleanClose = string.Join("|", black);
            var white = Names(s.CleanKeep).ToList();
            if (!white.Contains("claude.exe", StringComparer.OrdinalIgnoreCase)) white.Insert(0, "claude.exe");
            s.CleanKeep = string.Join("|", white);
        }

        // CleanVer 5 (13.11): version 4 also put the browsers and Telegram on the black list, to cover the wrong boot rule;
        // the rule is fixed (what Explorer starts is the user's), so those entries come off again
        static readonly string[] Version4Black = { "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe", "Telegram.exe" };
        public static void Upgrade5(Settings s)
        {
            s.CleanClose = string.Join("|", Names(s.CleanClose).Where(e => !Version4Black.Contains(e, StringComparer.OrdinalIgnoreCase)));
        }

        public static void Upgrade(Settings s)
        {
            var exes = Names(s.CleanClose).Where(e => !IsFolder(e)).ToList();
            if (exes.Count == 0) return;
            var dirs = new List<string>();
            using (var q = new ManagementObjectSearcher("SELECT PathName FROM Win32_Service"))
                foreach (ManagementObject o in q.Get())
                {
                    var exe = Startup.ExePath(o["PathName"] as string);
                    if (string.IsNullOrEmpty(exe)) continue;
                    var dir = Path.GetDirectoryName(Norm(exe));
                    if (!string.IsNullOrEmpty(dir)) { dirs.Add(dir); var up = Path.GetDirectoryName(dir); if (!string.IsNullOrEmpty(up)) dirs.Add(up); }
                }
            var list = Names(s.CleanClose).ToList();
            foreach (var e in exes)
            {
                var where = dirs.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(d => File.Exists(Path.Combine(d, e)) && BadFolder(d) == null);
                if (where == null) continue;
                list.Remove(e); list.Add(where);
                Store.Log("как после загрузки: «" + e + "» стала папкой " + where);
            }
            s.CleanClose = string.Join("|", list.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        public static List<Proc> Find() { return Find(Comp.S); }

        public static List<Proc> Find(Settings s)
        {
            Entries keepNames = new Entries(s != null ? s.CleanKeep : ""), closeNames = new Entries(s != null ? s.CleanClose : "");
            int session = Process.GetCurrentProcess().SessionId, me = Process.GetCurrentProcess().Id;
            var all = new List<Proc>();
            var sessionOf = new Dictionary<int, int>();
            using (var q = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate, SessionId FROM Win32_Process"))
                foreach (ManagementObject o in q.Get())
                {
                    var p = new Proc
                    {
                        Pid = Convert.ToInt32(o["ProcessId"]), Parent = Convert.ToInt32(o["ParentProcessId"]),
                        Name = o["Name"] as string ?? "", Path = o["ExecutablePath"] as string,
                        Start = o["CreationDate"] is string ? ManagementDateTimeConverter.ToDateTime((string)o["CreationDate"]) : DateTime.MinValue,
                    };
                    sessionOf[p.Pid] = Convert.ToInt32(o["SessionId"]);
                    all.Add(p);
                }
            var byPid = all.ToDictionary(p => p.Pid);
            var mine = all.Where(p => sessionOf[p.Pid] == session).ToList();

            // sign-in time: the session's shell host started then (sihost), else the first Explorer
            var shell = mine.Where(p => p.Name.Equals("sihost.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Start).DefaultIfEmpty(DateTime.MinValue).Min();
            if (shell == DateTime.MinValue) shell = mine.Where(p => p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Start).DefaultIfEmpty(DateTime.MinValue).Min();
            var bootEnd = shell == DateTime.MinValue ? DateTime.MinValue : shell.AddSeconds(BootWindowSeconds);

            // what «Автозагрузка» starts (enabled entries): it would be running after a fresh boot too
            var startupExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var it in Startup.All().Where(x => x.Enabled)) { var e = it.Exe; if (!string.IsNullOrEmpty(e)) startupExes.Add(Norm(e)); } } catch { }

            var windows = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "\\";
            Func<Proc, bool> isWindows = p => p.Path == null || Norm(p.Path).StartsWith(windows, StringComparison.OrdinalIgnoreCase);

            // roots whose whole process tree stays
            var keepTree = new HashSet<int>();
            var protect = new HashSet<int>(); // Red Tune: its tree stays whatever the lists say (Claude is an ordinary «Не закрывать» entry)
            foreach (var p in mine)
            {
                if (p.Pid == me || p.Name.Equals("RedTune.exe", StringComparison.OrdinalIgnoreCase)) { keepTree.Add(p.Pid); protect.Add(p.Pid); continue; }
                if (isWindows(p)) continue;
                if (keepNames.Match(p)) { keepTree.Add(p.Pid); continue; }
                if (closeNames.Match(p)) continue; // closes even if it started at boot // kept itself, but Explorer's children are what the user launched
                // 13.11: what Explorer started is what the user opened (taskbar, Start, a shortcut) — even in the first minute
                // after sign-in (Telegram opened 52 s after sign-in was kept as «started at boot»). Explorer's own startup
                // entries (Run keys, the Startup folder) are in «Автозагрузка», so they still stay through startupExes.
                Proc parent;
                bool byExplorer = byPid.TryGetValue(p.Parent, out parent) && parent.Start <= p.Start && parent.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
                if ((bootEnd != DateTime.MinValue && p.Start <= bootEnd && !byExplorer) || (p.Path != null && startupExes.Contains(Norm(p.Path)))) keepTree.Add(p.Pid);
            }

            // 14.11: what Red Tune itself was started from stays, with its whole tree, whatever the lists say. A program that
            // starts others often takes them down with it when it is ended (a job object, or it ends its own children):
            // Red Tune started from Claude's console went down each time the clean-up ended Claude, which is on the user's
            // «Чёрный список». Started the usual way (the logon task, Explorer) its parents are Windows' own, and nothing
            // changes.
            Proc self;
            if (byPid.TryGetValue(me, out self))
            {
                var cur = self;
                for (int depth = 0; depth < 64; depth++)
                {
                    Proc parent;
                    if (!byPid.TryGetValue(cur.Parent, out parent) || parent.Start > cur.Start) break; // a reused PID is not the parent
                    if (!isWindows(parent)) protect.Add(parent.Pid);
                    cur = parent;
                }
            }

            Func<Proc, HashSet<int>, bool> under = (p, roots) =>
            {
                var cur = p;
                for (int depth = 0; depth < 64; depth++)
                {
                    if (roots.Contains(cur.Pid)) return true;
                    Proc parent;
                    if (!byPid.TryGetValue(cur.Parent, out parent) || parent.Start > cur.Start) return false; // a reused PID is not the parent
                    cur = parent;
                }
                return false;
            };

            return mine.Where(p => !isWindows(p) && !under(p, protect) && !keepNames.Match(p) && (closeNames.Match(p) || !under(p, keepTree)))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public class Svc
        {
            public string Name, Display, Exe;
            public override string ToString() { return Display; }
        }

        // services of the «Закрывать всегда» folders: a running service whose exe is inside one of them (an exe entry is only
        // that program, no services). Never Windows' own, Red Tune's or one a «Не закрывать» entry covers.
        // They are only stopped: Windows starts them again at the next boot, as their start type stays.
        public static List<Svc> Services(Settings s)
        {
            var r = new List<Svc>();
            Entries close = new Entries(s != null ? s.CleanClose : ""), keep = new Entries(s != null ? s.CleanKeep : "");
            if (close.Folders.Count == 0) return r;
            var windows = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "\\";
            using (var q = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName, AcceptStop FROM Win32_Service WHERE State = 'Running'"))
                foreach (ManagementObject o in q.Get())
                {
                    var exe = Startup.ExePath(o["PathName"] as string);
                    if (string.IsNullOrEmpty(exe) || !(o["AcceptStop"] is bool && (bool)o["AcceptStop"])) continue;
                    exe = Norm(exe);
                    if (exe.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || exe.IndexOf("RedTune", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (keep.Exes.Contains(Path.GetFileName(exe)) || keep.InFolder(exe)) continue;
                    if (close.InFolder(exe))
                        r.Add(new Svc { Name = o["Name"] as string, Display = o["DisplayName"] as string ?? o["Name"] as string, Exe = exe });
                }
            return r.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // stops them (until the next boot); returns the ones that did not stop
        public static List<Svc> StopServices(List<Svc> list)
        {
            var left = new List<Svc>();
            foreach (var sv in list)
            {
                try
                {
                    using (var sc = new System.ServiceProcess.ServiceController(sv.Name))
                    {
                        if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped) sc.Stop();
                        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                }
                catch (Exception ex) { left.Add(sv); Store.Log("как после загрузки: служба " + sv.Name + " не остановилась: " + ex.Message); }
            }
            if (list.Count > 0) Store.Log("как после загрузки: остановлено служб " + (list.Count - left.Count) + " из " + list.Count + " (" + string.Join(", ", list.Select(x => x.Name)) + ")");
            return left;
        }

        // programs running now outside Windows (for the «Добавить…» menu): exe name → path
        public static SortedDictionary<string, string> Running()
        {
            int session = Process.GetCurrentProcess().SessionId;
            var windows = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "\\";
            var r = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var q = new ManagementObjectSearcher("SELECT Name, ExecutablePath, SessionId FROM Win32_Process"))
                foreach (ManagementObject o in q.Get())
                {
                    var path = o["ExecutablePath"] as string; var name = o["Name"] as string;
                    if (path == null || name == null || Convert.ToInt32(o["SessionId"]) != session || Norm(path).StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("RedTune.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!r.ContainsKey(name)) r[name] = path;
                }
            return r;
        }

        static string Norm(string path)
        {
            try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\'); } catch { return path.Trim().Trim('"'); }
        }

        // polite close for windows, end for background processes; returns what is still running after the wait
        public static List<Proc> Close(List<Proc> list, int waitMs = 6000)
        {
            var windowed = new List<Process>();
            foreach (var p in list)
            {
                try
                {
                    var pr = Process.GetProcessById(p.Pid);
                    if (pr.StartTime != p.Start && Math.Abs((pr.StartTime - p.Start).TotalSeconds) > 1) continue; // not the same process any more
                    if (pr.MainWindowHandle != IntPtr.Zero && pr.CloseMainWindow()) windowed.Add(pr);
                    else pr.Kill();
                }
                catch { }
            }
            var until = DateTime.Now.AddMilliseconds(waitMs);
            while (DateTime.Now < until && windowed.Any(w => { try { return !w.HasExited; } catch { return false; } })) Thread.Sleep(200);
            var left = new List<Proc>();
            foreach (var w in windowed)
                try { if (!w.HasExited) left.Add(list.First(x => x.Pid == w.Id)); } catch { }
            Store.Log("как после загрузки: закрыто " + (list.Count - left.Count) + " из " + list.Count + (left.Count > 0 ? ", не закрылись: " + string.Join(", ", left.Select(x => x.Name)) : ""));
            return left;
        }

        public static void Force(List<Proc> list)
        {
            foreach (var p in list) try { Process.GetProcessById(p.Pid).Kill(); } catch { }
            Store.Log("как после загрузки: принудительно " + string.Join(", ", list.Select(x => x.Name)));
        }

        // «chrome ×12, Telegram, Steam ×3»
        public static string Summary(List<Proc> list, int max = 18)
        {
            var groups = list.GroupBy(p => Path.GetFileNameWithoutExtension(p.Name), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count() : g.Key).ToList();
            var shown = groups.Take(max).ToList();
            return string.Join(", ", shown) + (groups.Count > max ? " и ещё " + (groups.Count - max) : "");
        }
    }
}
