using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace RedTune
{
    // «Очистка» → «Удаление программ»: the program's own uninstaller first, then what it left behind
    // (folders, registry keys, shortcuts, startup entries), and leftovers of programs removed long ago.
    // Nothing goes without the user's tick: files go to the Recycle Bin, registry keys are exported to a .reg first.
    class InstalledApp
    {
        public string Name, Publisher, Version, Location, Uninstall, Icon, KeyPath; // KeyPath: "HKLM64|SOFTWARE\...\Uninstall\X"
        public long SizeKb; public string Date;
    }

    class Leftover
    {
        public string Kind, Path, What;  // Kind: folder, file, reg, link, startup
        public long Bytes;
        public bool Checked = true;
        public StartupItem Item;         // for startup entries
        public override string ToString() { return L.T(What); }
    }

    static class Uninst
    {
        const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        static RegistryKey Open(string hive, bool write = false)
        {
            switch (hive)
            {
                case "HKLM64": return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                case "HKLM32": return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                default: return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            }
        }

        // what «Программы и компоненты» shows: entries with a name and an uninstaller, without system parts and updates
        public static List<InstalledApp> List()
        {
            var list = new List<InstalledApp>();
            foreach (var hive in new[] { "HKLM64", "HKLM32", "HKCU" })
                using (var root = Open(hive))
                using (var k = root.OpenSubKey(Key))
                {
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                        using (var e = k.OpenSubKey(sub))
                        {
                            if (e == null) continue;
                            var name = e.GetValue("DisplayName") as string;
                            var un = e.GetValue("UninstallString") as string;
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(un)) continue;
                            if (Convert.ToInt32(e.GetValue("SystemComponent") ?? 0) == 1 || e.GetValue("ParentKeyName") != null) continue;
                            var rel = e.GetValue("ReleaseType") as string ?? "";
                            if (rel.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 || rel.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            list.Add(new InstalledApp
                            {
                                Name = name.Trim(), Publisher = (e.GetValue("Publisher") as string ?? "").Trim(), Version = e.GetValue("DisplayVersion") as string ?? "",
                                Location = (e.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'), Uninstall = un.Trim(),
                                Icon = e.GetValue("DisplayIcon") as string ?? "", KeyPath = hive + "|" + Key + "\\" + sub,
                                SizeKb = Convert.ToInt64(e.GetValue("EstimatedSize") ?? 0L), Date = e.GetValue("InstallDate") as string ?? "",
                            });
                        }
                }
            // the same program registered twice (32+64 bit keys): one row
            return list.GroupBy(a => a.Name + "|" + a.Version, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static bool StillInstalled(InstalledApp a)
        {
            var p = a.KeyPath.Split('|');
            using (var root = Open(p[0])) using (var k = root.OpenSubKey(p[1])) return k != null;
        }

        // ---------- the program's own uninstaller ----------

        // starts it and waits until it and everything it started have exited (NSIS copies itself to Temp and exits at once)
        public static void RunUninstaller(InstalledApp a, Func<bool> cancelled)
        {
            string exe, args;
            var u = a.Uninstall;
            var msi = Regex.Match(u, @"msiexec(\.exe)?\s+/[IX]\s*(\{[0-9A-Fa-f\-]+\})", RegexOptions.IgnoreCase);
            if (msi.Success) { exe = "msiexec.exe"; args = "/x " + msi.Groups[2].Value; }
            else if (u.StartsWith("\"")) { int e = u.IndexOf('"', 1); exe = u.Substring(1, e - 1); args = u.Substring(e + 1).Trim(); }
            else
            {
                int x = u.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (x > 0) { exe = u.Substring(0, x + 4); args = u.Substring(x + 4).Trim(); } else { exe = u; args = ""; }
            }
            var started = DateTime.Now.AddSeconds(-1);
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            Store.Log("удаление программ: «" + a.Name + "» — " + exe + " " + args);
            if (p == null) return;
            var watched = new HashSet<int> { p.Id };
            var until = DateTime.Now.AddMinutes(30);
            while (DateTime.Now < until && !cancelled())
            {
                Thread.Sleep(1000);
                bool alive = false;
                try
                {
                    using (var q = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process"))
                        foreach (ManagementObject o in q.Get())
                        {
                            int pid = Convert.ToInt32(o["ProcessId"]), parent = Convert.ToInt32(o["ParentProcessId"]);
                            var created = o["CreationDate"] is string ? ManagementDateTimeConverter.ToDateTime((string)o["CreationDate"]) : DateTime.MinValue;
                            if (!watched.Contains(pid) && watched.Contains(parent) && created >= started) watched.Add(pid);
                            if (watched.Contains(pid)) alive = true;
                        }
                }
                catch { alive = !p.HasExited; }
                if (!alive) break;
            }
        }

        // ---------- leftovers of one program ----------

        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "windows", "common files", "microsoft shared", "nvidia corporation", "nvidia", "intel", "amd", "google", "packages", "programs",
            "temp", "system32", "program files", "program files (x86)", "appdata", "local", "roaming", "locallow", "users", "public", "default", "inc", "llc",
            "corporation", "software", "classes", "policies", "wow6432node", "app", "apps", "bin", "data", "update", "updater", "setup", "installer",
        };

        // «Telegram Desktop 5.6.3 (x64)» → «Telegram Desktop», «TelegramDesktop»; the install folder's name; the publisher's first word
        public static List<string> Names(InstalledApp a)
        {
            var r = new List<string>();
            var n = Regex.Replace(a.Name, @"\(.*?\)|\b[vV]?\d+(\.\d+)+\b|\b(x64|x86|64-bit|32-bit)\b", "").Trim(' ', '-', '_', '.');
            if (n.Length > 0) { r.Add(n); r.Add(n.Replace(" ", "")); }
            if (a.Location.Length > 3) { var leaf = Path.GetFileName(a.Location.TrimEnd('\\')); if (!string.IsNullOrEmpty(leaf)) r.Add(leaf); }
            return r.Where(x => x.Length >= 3 && !Generic.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static List<string> Publishers(InstalledApp a)
        {
            var r = new List<string>();
            if (a.Publisher.Length > 0)
            {
                r.Add(a.Publisher);
                var clean = Regex.Replace(a.Publisher, @"[,.]?\s*(Inc|LLC|Ltd|GmbH|Corporation|Corp|Co|S\.A|AB|Limited)\.?$", "", RegexOptions.IgnoreCase).Trim();
                r.Add(clean);
            }
            return r.Where(x => x.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string LocalLow { get { return Path.Combine(Path.GetDirectoryName(Local), "LocalLow"); } }
        static string ProgramData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        static string Pf { get { return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); } }
        static string Pf86 { get { return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); } }

        // never offered, whatever matched
        static bool Protected(string path)
        {
            var p = path.TrimEnd('\\');
            var bad = new[] { Pf, Pf86, ProgramData, Roaming, Local, LocalLow, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Pf, "Common Files"), Path.Combine(Pf86, "Common Files"), Path.GetPathRoot(Pf) };
            return p.Length < 8 || bad.Any(b => string.Equals(p, b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                || p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                || p.IndexOf("RedTune", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("\\Claude", StringComparison.OrdinalIgnoreCase) >= 0
                || OwnData.Any(d => Under(d, p));
        }

        // everything of Red Tune sits in its two folders (the program and %APPDATA%\RedTune, whose paths say RedTune);
        // a folder that holds one of them is not a leftover of anything either
        static IEnumerable<string> OwnData { get { return new[] { Store.DataDir, AppDomain.CurrentDomain.BaseDirectory }; } }

        static bool Under(string path, string dir)
        {
            var a = path.TrimEnd('\\'); var b = (dir ?? "").TrimEnd('\\');
            return b.Length > 0 && (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase));
        }

        // forgottenEntry: the uninstaller finished but left its entry in «Программы и компоненты»
        public static List<Leftover> Leftovers(InstalledApp a, bool forgottenEntry)
        {
            var found = new List<Leftover>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = Names(a); var pubs = Publishers(a);
            Action<string> folder = d =>
            {
                try
                {
                    if (string.IsNullOrEmpty(d)) return;
                    d = Path.GetFullPath(d).TrimEnd('\\');
                    if (!Directory.Exists(d) || Protected(d) || !folders.Add(d)) return;
                }
                catch { }
            };
            // where it was installed, and its exe's folder if that is not the root of Program Files
            if (a.Location.Length > 3) folder(a.Location);
            foreach (var p in new[] { a.Icon, a.Uninstall })
            {
                var exe = Startup.ExePath(p);
                if (!string.IsNullOrEmpty(exe) && (exe.StartsWith(Pf, StringComparison.OrdinalIgnoreCase) || exe.StartsWith(Pf86, StringComparison.OrdinalIgnoreCase)))
                    folder(Path.GetDirectoryName(exe));
            }
            // name folders in the usual places, also under the publisher's folder
            foreach (var root in new[] { Pf, Pf86, ProgramData, Roaming, Local, LocalLow, Path.Combine(Local, "Programs") })
                foreach (var n in names)
                {
                    folder(Path.Combine(root, n));
                    foreach (var pub in pubs) folder(Path.Combine(root, pub, n));
                }
            // a publisher folder that only held this program and is empty now goes too (checked again before deleting)
            foreach (var d in folders.ToList())
            {
                long bytes; DateTime newest;
                Measure(d, out bytes, out newest);
                found.Add(new Leftover { Kind = "folder", Path = d, Bytes = bytes, What = "Папка  " + d + "  · " + Junk.Size(bytes) });
            }

            // registry
            var keys = new List<string>();
            foreach (var hive in new[] { "HKCU", "HKLM64", "HKLM32" })
                foreach (var n in names)
                {
                    keys.Add(hive + "|SOFTWARE\\" + n);
                    foreach (var pub in pubs) keys.Add(hive + "|SOFTWARE\\" + pub + "\\" + n);
                }
            if (forgottenEntry && StillInstalled(a)) keys.Add(a.KeyPath); // the uninstall entry the uninstaller forgot
            foreach (var k in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var p = k.Split('|');
                using (var root = Open(p[0])) using (var x = root.OpenSubKey(p[1]))
                    if (x != null) found.Add(new Leftover { Kind = "reg", Path = k, What = "Реестр  " + RegName(k) });
            }

            // shortcuts: in Start and on the desktop, pointing into its folders or to a file that is gone and named like it
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                if (!Directory.Exists(dir)) continue;
                string[] links; try { links = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); } catch { continue; }
                foreach (var l in links)
                {
                    var target = Startup.ExePath(Startup.LinkTarget(l));
                    bool inside = !string.IsNullOrEmpty(target) && folders.Any(f => target.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
                    bool dead = !string.IsNullOrEmpty(target) && !File.Exists(target) && names.Any(n => Path.GetFileNameWithoutExtension(l).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (inside || dead) found.Add(new Leftover { Kind = "file", Path = l, What = "Ярлык  " + l });
                }
                foreach (var n in names) { var sub = Path.Combine(dir, n); if (Directory.Exists(sub) && !folders.Contains(sub)) found.Add(new Leftover { Kind = "folder", Path = sub, What = "Папка в «Пуске»  " + sub }); }
            }

            // startup entries and tasks that start something from its folders (or a file that is gone and named like it)
            try
            {
                foreach (var it in Startup.All())
                {
                    var exe = it.Exe;
                    if (string.IsNullOrEmpty(exe)) continue;
                    bool inside = folders.Any(f => exe.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
                    bool dead = !File.Exists(exe) && names.Any(n => it.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || exe.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (inside || dead) found.Add(new Leftover { Kind = "startup", Path = it.Kind + "|" + it.Name, Item = it, What = "Автозагрузка  " + it.Name + " (" + it.Source + ")" });
                }
            }
            catch { }
            // a shortcut or folder inside another listed folder goes with it: one line, and no "failed" after the parent is gone
            var dirs = found.Where(f => f.Kind == "folder").Select(f => f.Path).ToList();
            return found.Where(f => (f.Kind != "folder" && f.Kind != "file") || !dirs.Any(d => f.Path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)))
                        .GroupBy(f => f.Kind + "|" + f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        static string RegName(string k)
        {
            var p = k.Split('|');
            return (p[0] == "HKCU" ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE") + "\\" + (p[0] == "HKLM32" && !p[1].StartsWith("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", StringComparison.OrdinalIgnoreCase) ? p[1].Replace("SOFTWARE\\", "SOFTWARE\\WOW6432Node\\") : p[1]);
        }

        // for reg.exe: the key as the given view sees it (/reg:32 maps SOFTWARE to WOW6432Node itself)
        static string RegExport(string k) { var p = k.Split('|'); return (p[0] == "HKCU" ? "HKCU" : "HKLM") + "\\" + p[1]; }

        static void Measure(string dir, out long bytes, out DateTime newest)
        {
            bytes = 0; newest = DateTime.MinValue;
            var stack = new Stack<string>(); stack.Push(dir);
            int guard = 0;
            while (stack.Count > 0 && guard++ < 200000)
            {
                var d = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(d);
                    if (di.LastWriteTime > newest) newest = di.LastWriteTime;
                    foreach (var f in di.GetFiles()) { bytes += f.Length; if (f.LastWriteTime > newest) newest = f.LastWriteTime; }
                    foreach (var s in di.GetDirectories()) if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s.FullName);
                }
                catch { }
            }
        }

        // ---------- leftovers of programs removed earlier (a hint: nothing ticked by default) ----------

        static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Packages", "Temp", "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache", "NVIDIA", "NVIDIA Corporation", "AMD", "Intel", "Comms",
            "PeerDistRepub", "PlaceholderTileLogoFolder", "Programs", "Publishers", "VirtualStore", "Package Cache", "Microsoft OneDrive", "OneDrive", "Windows",
            "Adobe", "Google", "Mozilla", "Apple", "Apple Computer", "Sun", "Oracle", "regid.1991-06.com.microsoft", "SoftwareDistribution", "ssh", "USOPrivate",
            "USOShared", "WindowsHolographicDevices", "Desktop", "Documents", "Downloads", "History", "IconCache", "Application Data", "Start Menu", "Templates",
            "RedTune", "Claude", "Claude-Data", "AnthropicClaude", "claude-cli-nodejs", "npm", "npm-cache", "node-gyp", "pip", "NuGet", ".NET",
            "dotnet", "Package", "Lenovo", "LenovoLegionToolkit", "Realtek", "Dolby", "DTS", "Nahimic", "A-Volute", "Waves", "Kingston", "Samsung",
        };

        static string Norm(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        public static List<Leftover> Orphans(List<InstalledApp> apps)
        {
            // what the installed programs could own: names, publishers, install folders, their longer words
            var tokens = new HashSet<string>();
            var words = new HashSet<string>(); // single words of names: only the whole folder name or its start ("Epic" → EpicGamesLauncher, but "Games" ≠ Rockstar Games)
            foreach (var a in apps)
            {
                foreach (var t in new[] { a.Name, a.Publisher, Path.GetFileName((a.Location ?? "").TrimEnd('\\')) }.Concat(Names(a)).Concat(Publishers(a)))
                    if (Norm(t).Length >= 3) tokens.Add(Norm(t));
                foreach (var w in Regex.Split(a.Name + " " + a.Publisher, @"[^\p{L}\p{N}]+")) if (w.Length >= 4) words.Add(Norm(w));
            }
            words.ExceptWith(new[] { "games", "game", "software", "systems", "corporation", "technologies", "technology", "audio", "driver", "drivers", "runtime", "tools", "studio", "client", "services", "service", "version", "microsoft", "update" });
            var running = new List<string>();
            try { using (var q = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process")) foreach (ManagementObject o in q.Get()) { var p = o["ExecutablePath"] as string; if (p != null) running.Add(p); } } catch { }

            var found = new List<Leftover>();
            var old = DateTime.Now.AddDays(-60);
            foreach (var root in new[] { Roaming, Local, LocalLow, ProgramData })
            {
                string[] dirs; try { dirs = Directory.GetDirectories(root); } catch { continue; }
                foreach (var d in dirs)
                {
                    var name = Path.GetFileName(d); var n = Norm(name);
                    if (n.Length < 3 || Known.Contains(name) || name.StartsWith(".") || Protected(d)) continue;
                    if (tokens.Any(t => t == n || (t.Length >= 4 && n.Contains(t)) || (n.Length >= 4 && t.Contains(n)))) continue;
                    if (words.Any(w => n.StartsWith(w))) continue;
                    if (running.Any(p => p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                    try { if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    long bytes; DateTime newest;
                    Measure(d, out bytes, out newest);
                    if (bytes < 1048576 || newest > old) continue; // small or still in use
                    found.Add(new Leftover { Kind = "folder", Path = d, Bytes = bytes, Checked = false,
                        What = name + "  · " + Junk.Size(bytes) + " · не менялась с " + newest.ToString("dd.MM.yyyy") + "  · " + d });
                }
            }
            return found.OrderByDescending(f => f.Bytes).ToList();
        }

        // ---------- deleting the ticked ones ----------

        public static string Remove(IEnumerable<Leftover> items, string label)
        {
            int ok = 0, failed = 0; long bytes = 0;
            var backupDir = Path.Combine(Store.DataDir, "removed-registry");
            foreach (var it in items)
            {
                try
                {
                    switch (it.Kind)
                    {
                        case "folder":
                        case "file":
                            if (Protected(it.Path)) { failed++; continue; }
                            if (!File.Exists(it.Path) && !Directory.Exists(it.Path)) { ok++; continue; } // already gone
                            Startup.Recycle(it.Path); bytes += it.Bytes; break;
                        case "reg":
                            Directory.CreateDirectory(backupDir);
                            var file = Path.Combine(backupDir, string.Concat((label + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + " " + ok).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".reg");
                            var p = it.Path.Split('|');
                            var exp = Process.Start(new ProcessStartInfo("reg.exe", "export \"" + RegExport(it.Path) + "\" \"" + file + "\" /y" + (p[0] == "HKLM32" ? " /reg:32" : "")) { UseShellExecute = false, CreateNoWindow = true });
                            exp.WaitForExit(15000);
                            if (exp.ExitCode != 0) { failed++; continue; } // no backup — no delete
                            using (var root = Open(p[0], true)) root.DeleteSubKeyTree(p[1], false);
                            break;
                        case "startup":
                            Startup.Remove(it.Item); break;
                    }
                    ok++;
                    Store.Log("удаление программ: " + label + " — убрано " + it.What);
                }
                catch (Exception ex) { failed++; Store.Log("удаление программ: " + label + " — не убрано " + it.What + ": " + ex.Message); }
            }
            return "Убрано " + ok + (bytes > 0 ? " · в корзине " + Junk.Size(bytes) : "") + (failed > 0 ? " · не получилось: " + failed : "");
        }
    }
}
