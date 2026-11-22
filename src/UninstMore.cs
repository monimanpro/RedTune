using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace RedTune
{
    // 22.11 (the user: add services and drivers, context menu entries, PATH entries ahead of time; and can the program tell
    // what is safe to remove). The rules, the same for every kind:
    //   - only what is tied to the program by evidence: its file lives in the program's own folder, or the file is gone;
    //   - never Windows' own: a service run by svchost, anything whose file exists under the Windows folder (drivers a
    //     program put there among Windows' ones too), a driver in a device's filter chain (a filter that is gone stops the
    //     device: keyboards and drives have been lost that way);
    //   - 22.11, later (the user: «при чистке всё удалять без всяких копий … где есть риски — просто не трогать»): no
    //     copies and no restore points; what carries a risk is not offered at all — certificates (one removed by mistake
    //     breaks HTTPS), anything found only through the publisher's folder (Google's folder holds more than Chrome);
    //   - certain links are ticked after a removal, programs removed long ago are a hint without ticks.
    static partial class Uninst
    {
        static string WinDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }

        static bool UnderAny(string path, IEnumerable<string> dirs)
        {
            return !string.IsNullOrEmpty(path) && dirs.Any(d => !string.IsNullOrEmpty(d) && d.Length > 3 && (path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
        }

        // a file that is gone for sure: a full path on a drive that is there (not a USB disk unplugged), not a pattern
        static bool Gone(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.IndexOfAny(new[] { '%', '*', '?' }) >= 0) return false;
                var root = Path.GetPathRoot(path); if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch { return false; }
        }

        // ---------- services and drivers ----------

        internal class Svc { public string Name, Display, Image; public bool Driver, Shared, InFilters; public int Start; }

        // the file a service's ImagePath names: «\SystemRoot\…», «System32\drivers\x.sys», «\??\C:\…», quoted with arguments
        static string ImageFile(string image)
        {
            var s = Environment.ExpandEnvironmentVariables((image ?? "").Trim());
            if (s.StartsWith(@"\??\")) s = s.Substring(4);
            if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(WinDir, s.Substring(12));
            else if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || s.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(WinDir, s);
            if (s.StartsWith("\"")) return Startup.ExePath(s);
            // unquoted with spaces («C:\Program Files\BlueStacks_nxt\BstkDrv_nxt.sys»): up to the first .exe / .sys
            foreach (var ext in new[] { ".exe", ".sys" })
            {
                int x = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (x > 0 && (x + 4 == s.Length || s[x + 4] == ' ' || s[x + 4] == '"')) return s.Substring(0, x + 4);
            }
            return Startup.ExePath(s);
        }

        static HashSet<string> Filters()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<RegistryKey> take = k => { foreach (var v in new[] { "UpperFilters", "LowerFilters" }) { var m = k.GetValue(v) as string[]; if (m != null) foreach (var x in m) set.Add(x.Trim()); } };
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var cls = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class"))
                    if (cls != null) foreach (var g in cls.GetSubKeyNames()) using (var k = cls.OpenSubKey(g)) if (k != null) take(k);
                // and every device's own (two levels under Enum\<bus>\<id>\<instance>)
                using (var en = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
                    if (en != null)
                        foreach (var bus in en.GetSubKeyNames()) try { using (var b = en.OpenSubKey(bus)) foreach (var id in b.GetSubKeyNames()) using (var i = b.OpenSubKey(id)) foreach (var inst in i.GetSubKeyNames()) using (var d = i.OpenSubKey(inst)) if (d != null) take(d); } catch { }
            }
            return set;
        }

        internal static List<Svc> Services()
        {
            var list = new List<Svc>(); var filters = Filters();
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var all = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                foreach (var name in all.GetSubKeyNames())
                    try
                    {
                        using (var k = all.OpenSubKey(name))
                        {
                            var img = k.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                            if (string.IsNullOrWhiteSpace(img)) continue;
                            int type = Convert.ToInt32(k.GetValue("Type") ?? 0), start = Convert.ToInt32(k.GetValue("Start") ?? 3);
                            var disp = k.GetValue("DisplayName") as string ?? name; if (disp.StartsWith("@")) disp = name;
                            list.Add(new Svc { Name = name, Display = disp, Image = ImageFile(img), Driver = (type & 0x3) != 0, Start = start,
                                Shared = img.IndexOf("svchost.exe", StringComparison.OrdinalIgnoreCase) >= 0, InFilters = filters.Contains(name) });
                        }
                    }
                    catch { }
            return list;
        }

        static bool Running(string name)
        {
            try { using (var sc = new ServiceController(name)) return sc.Status != ServiceControllerStatus.Stopped; } catch { return false; }
        }

        static Leftover SvcLeft(Svc s, bool gone, bool ticked)
        {
            string what = (s.Driver ? "Драйвер  " : "Служба  ") + (s.Display != s.Name ? "«" + s.Display + "» (" + s.Name + ")" : s.Name) + " · " + s.Image + (gone ? " (файла уже нет)" : "");
            if (!gone && Running(s.Name)) what += " · сейчас работает: будет остановлена" + (s.Driver ? ", уйдёт после перезагрузки" : "");
            return new Leftover { Kind = "svc", Path = s.Name, Checked = ticked, What = what };
        }

        // a service is never offered when Windows or a device leans on it
        static bool SvcSafe(Svc s) { return !s.Shared && !s.InFilters && !(UnderAny(s.Image, new[] { WinDir }) && File.Exists(s.Image)); }

        // ---------- context menu (static verbs and shell extensions, for files, folders, the folder's background, drives) ----------

        static readonly string[] MenuPlaces = { "*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive" };

        internal class MenuItem { public string Key, Name, Target; } // Key: "HKLM64|SOFTWARE\Classes\…" (the way Remove takes it)

        static string PlaceText(string place)
        {
            switch (place) { case "*": return " (на файлах)"; case "AllFilesystemObjects": return " (на файлах и папках)"; case "Directory": case "Folder": return " (на папках)"; case @"Directory\Background": return " (на фоне папки)"; case "Drive": return " (на дисках)"; default: return ""; }
        }

        static string ClsidDll(RegistryKey hklm, RegistryKey hkcu, string clsid)
        {
            foreach (var root in new[] { hkcu.OpenSubKey(@"Software\Classes\CLSID\" + clsid + @"\InprocServer32"), hklm.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + clsid + @"\InprocServer32") })
                using (root) if (root != null) { var v = root.GetValue("") as string; if (!string.IsNullOrEmpty(v)) return Environment.ExpandEnvironmentVariables(v.Trim('"')); }
            return null;
        }

        internal static List<MenuItem> MenuItems()
        {
            var list = new List<MenuItem>();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                foreach (var hive in new[] { "HKLM64", "HKCU" })
                {
                    var root = hive == "HKCU" ? hkcu : hklm; var cls = hive == "HKCU" ? @"Software\Classes\" : @"SOFTWARE\Classes\";
                    foreach (var place in MenuPlaces)
                    {
                        // a verb with its own command line
                        using (var sh = root.OpenSubKey(cls + place + @"\shell"))
                            if (sh != null) foreach (var v in sh.GetSubKeyNames()) try
                            {
                                using (var c = sh.OpenSubKey(v + @"\command"))
                                {
                                    var cmd = c == null ? null : c.GetValue("") as string; if (string.IsNullOrWhiteSpace(cmd)) continue;
                                    using (var vk = sh.OpenSubKey(v)) { var label = vk.GetValue("MUIVerb") as string ?? vk.GetValue("") as string ?? v; if (label.StartsWith("@")) label = v;
                                        list.Add(new MenuItem { Key = hive + "|" + cls + place + @"\shell\" + v, Name = label.Replace("&", "") + PlaceText(place), Target = Startup.ExePath(cmd) }); }
                                }
                            }
                            catch { }
                        // a shell extension: its CLSID and the dll behind it
                        using (var hs = root.OpenSubKey(cls + place + @"\shellex\ContextMenuHandlers"))
                            if (hs != null) foreach (var h in hs.GetSubKeyNames()) try
                            {
                                using (var hk = hs.OpenSubKey(h))
                                {
                                    var clsid = hk.GetValue("") as string; if (string.IsNullOrEmpty(clsid) || !clsid.StartsWith("{")) clsid = h.StartsWith("{") ? h : null;
                                    if (clsid == null) continue;
                                    var dll = ClsidDll(hklm, hkcu, clsid); if (dll == null) continue;
                                    list.Add(new MenuItem { Key = hive + "|" + cls + place + @"\shellex\ContextMenuHandlers\" + h, Name = h + PlaceText(place), Target = dll });
                                }
                            }
                            catch { }
                    }
                }
            return list;
        }

        // ---------- PATH ----------

        internal class PathEntry { public string Scope, Entry, Expanded; } // Scope: HKLM (for everyone) or HKCU (this user)

        static RegistryKey EnvKey(string scope, bool write)
        {
            var root = RegistryKey.OpenBaseKey(scope == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            return root.OpenSubKey(scope == "HKLM" ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : "Environment", write);
        }

        internal static List<PathEntry> PathEntries()
        {
            var list = new List<PathEntry>();
            foreach (var scope in new[] { "HKLM", "HKCU" })
                using (var k = EnvKey(scope, false))
                {
                    var raw = k == null ? null : k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; if (raw == null) continue;
                    foreach (var e in raw.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                        list.Add(new PathEntry { Scope = scope, Entry = e, Expanded = Environment.ExpandEnvironmentVariables(e).Trim('"').TrimEnd('\\') });
                }
            return list;
        }

        static Leftover PathLeft(PathEntry p, bool gone, bool ticked)
        {
            return new Leftover { Kind = "path", Path = p.Scope + "|" + p.Entry, Checked = ticked,
                What = "PATH " + (p.Scope == "HKLM" ? "для всех" : "твой") + "  " + p.Entry + (gone ? " (папки уже нет)" : "") };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);

        static void RemovePathEntry(string scopeAndEntry)
        {
            int bar = scopeAndEntry.IndexOf('|'); string scope = scopeAndEntry.Substring(0, bar), entry = scopeAndEntry.Substring(bar + 1);
            using (var k = EnvKey(scope, true))
            {
                var raw = k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var kind = k.GetValueKind("Path");
                Store.Log("удаление программ: PATH " + scope + " без «" + entry + "», был: " + raw); // the old value in the journal, not a copy to restore
                var kept = raw.Split(';').Where(x => x.Trim().Length > 0 && !string.Equals(x.Trim().TrimEnd('\\'), entry.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                k.SetValue("Path", string.Join(";", kept), kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
            IntPtr res; SendMessageTimeout((IntPtr)0xffff, 0x1A, IntPtr.Zero, "Environment", 2, 3000, out res); // programs started after this see the new PATH
        }

        // ---------- what one program left (called from Leftovers) ----------

        static void MoreLeftovers(InstalledApp a, List<string> names, List<string> pubs, ICollection<string> folders, List<Leftover> found)
        {
            // where it lived, whether the folder is still there or not (an uninstaller usually takes it); its own folders only,
            // never the publisher's (Program Files\Google holds more than Chrome)
            var dirs = new List<string>(folders);
            if (a.Location.Length > 3) dirs.Add(a.Location.TrimEnd('\\'));
            foreach (var p in new[] { a.Icon, a.Uninstall }) { var exe = Startup.ExePath(p); if (!string.IsNullOrEmpty(exe) && !UnderAny(exe, new[] { WinDir }) && Path.IsPathRooted(exe)) try { dirs.Add(Path.GetDirectoryName(exe)); } catch { } }
            foreach (var root in new[] { Pf, Pf86 }) foreach (var n in names) { dirs.Add(Path.Combine(root, n)); foreach (var pub in pubs) dirs.Add(Path.Combine(root, pub, n)); }
            dirs = dirs.Where(d => d.Length > 3 && !Protected(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                foreach (var s in Services())
                {
                    if (!SvcSafe(s)) continue;
                    bool inside = UnderAny(s.Image, dirs), named = names.Any(n => NamedAfter(s.Name, n) || NamedAfter(s.Display, n)), gone = Gone(s.Image);
                    if (inside || (named && gone)) found.Add(SvcLeft(s, gone, true));
                }
            }
            catch (Exception ex) { Store.Log("удаление программ: службы: " + ex.Message); }
            try
            {
                foreach (var m in MenuItems())
                    if (UnderAny(m.Target, dirs)) found.Add(new Leftover { Kind = "menu", Path = m.Key, What = "Контекстное меню  «" + m.Name + "» · " + m.Target + (Gone(m.Target) ? " (файла уже нет)" : "") });
            }
            catch (Exception ex) { Store.Log("удаление программ: контекстное меню: " + ex.Message); }
            try
            {
                foreach (var p in PathEntries())
                    if (dirs.Any(d => string.Equals(p.Expanded, d, StringComparison.OrdinalIgnoreCase)) || UnderAny(p.Expanded, dirs)) found.Add(PathLeft(p, Gone(p.Expanded), true));
            }
            catch (Exception ex) { Store.Log("удаление программ: PATH: " + ex.Message); }
        }

        // ---------- what programs removed earlier left (called from Orphans; nothing ticked) ----------

        static List<Leftover> MoreOrphans()
        {
            var found = new List<Leftover>();
            try
            {
                foreach (var s in Services())
                    if (SvcSafe(s) && Gone(s.Image)) found.Add(SvcLeft(s, true, false));
            }
            catch (Exception ex) { Store.Log("удаление программ: службы: " + ex.Message); }
            try
            {
                foreach (var m in MenuItems())
                    if (Gone(m.Target) && !UnderAny(m.Target, new[] { WinDir })) found.Add(new Leftover { Kind = "menu", Path = m.Key, Checked = false, What = "Контекстное меню  «" + m.Name + "» · " + m.Target + " (файла уже нет)" });
            }
            catch (Exception ex) { Store.Log("удаление программ: контекстное меню: " + ex.Message); }
            try
            {
                foreach (var p in PathEntries())
                    if (Gone(p.Expanded) && !UnderAny(p.Expanded + "\\", new[] { WinDir })) found.Add(PathLeft(p, true, false));
            }
            catch (Exception ex) { Store.Log("удаление программ: PATH: " + ex.Message); }
            return found;
        }

        // ---------- COM servers: what Windows loads from a folder ----------

        // 22.11 (the user's «WinClean» in Program Files was Icaros, the video thumbnails of Explorer, registered as a
        // thumbnail provider: no uninstall entry, no service, no shortcut — only its CLSID): every InprocServer32 and
        // LocalServer32 file, 64 and 32 bit, the machine's and the user's; a folder holding one is in use
        internal static List<string> ComServers()
        {
            var list = new List<string>();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                foreach (var pair in new[] { Tuple.Create(hklm, @"SOFTWARE\Classes\CLSID"), Tuple.Create(hklm, @"SOFTWARE\WOW6432Node\Classes\CLSID"), Tuple.Create(hkcu, @"Software\Classes\CLSID") })
                    using (var root = pair.Item1.OpenSubKey(pair.Item2))
                    {
                        if (root == null) continue;
                        foreach (var id in root.GetSubKeyNames())
                            foreach (var srv in new[] { "InprocServer32", "LocalServer32" })
                                try
                                {
                                    using (var k = root.OpenSubKey(id + "\\" + srv))
                                    {
                                        var v = k == null ? null : k.GetValue("") as string; if (string.IsNullOrWhiteSpace(v)) continue;
                                        // InprocServer32 is a bare path to a dll, spaces and all («C:\Program Files\WinClean\Preview\…dll»);
                                        // LocalServer32 a command line
                                        var raw = Environment.ExpandEnvironmentVariables(v.Trim());
                                        var p = srv == "InprocServer32" ? raw.Trim('"') : ImageFile(raw);
                                        if (p.Length > 3 && p[1] == ':') list.Add(p);
                                    }
                                }
                                catch { }
                    }
            return list;
        }

        // ---------- Program Files: folders no program owns ----------

        // Windows' own and the developer kits keep folders there that no entry in «Программы и компоненты» names
        static readonly HashSet<string> PfKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Common Files", "Windows Kits", "Reference Assemblies", "MSBuild", "dotnet", "Internet Explorer", "ModifiableWindowsApps", "Uninstall Information",
            "WindowsApps", "WindowsPowerShell", "PowerShell", "Package Cache", "IIS", "IIS Express", "Microsoft SDKs", "Microsoft SQL Server", "Microsoft Update Health Tools",
            "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player", "Windows Multimedia Platform", "Windows NT",
            "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "Windows Journal", "RedTune", "Claude", "PawnIO",
        };

        // a folder is left alone when anything still leans on it: a service or driver, a startup entry or task, a running
        // program, a COM server Windows loads (extensions of Explorer: thumbnails, properties, menus — ComServers, passed in
        // with the running ones), PATH, the context menu, a firewall rule whose program is there; when Windows owns it (TrustedInstaller);
        // when its programs are Microsoft's or come from the publisher of a program that is installed
        static void ProgramFilesOrphans(HashSet<string> tokens, HashSet<string> words, List<string> running, HashSet<string> sysNorm, DateTime old, long minBytes, List<Leftover> found)
        {
            var used = new List<string>(running);
            try { used.AddRange(Services().Select(s => s.Image).Where(x => !string.IsNullOrEmpty(x))); } catch { }
            try { used.AddRange(Startup.All().Select(s => s.Exe).Where(x => !string.IsNullOrEmpty(x))); } catch { }
            try { used.AddRange(PathEntries().Select(p => p.Expanded + "\\")); } catch { }
            try { used.AddRange(MenuItems().Select(m => m.Target).Where(x => !string.IsNullOrEmpty(x))); } catch { }
            try { used.AddRange(FirewallRules().Select(r => Environment.ExpandEnvironmentVariables(r.App ?? "")).Where(x => x.Length > 3 && File.Exists(x))); } catch { }
            foreach (var root in new[] { Pf, Pf86 }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] dirs; try { dirs = Directory.GetDirectories(root); } catch { continue; }
                foreach (var d in dirs)
                {
                    var name = Path.GetFileName(d); var n = Norm(name);
                    if (n.Length < 3 || PfKnown.Contains(name) || name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || Protected(d)) continue;
                    if (tokens.Any(t => t == n || (t.Length >= 4 && n.Contains(t)) || (n.Length >= 4 && t.Contains(n)))) continue;
                    if (words.Any(w => n.StartsWith(w))) continue;
                    if (used.Any(p => p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                    try
                    {
                        var di = new DirectoryInfo(d);
                        if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var owner = di.GetAccessControl().GetOwner(typeof(System.Security.Principal.NTAccount)).Value;
                        if (owner.IndexOf("TrustedInstaller", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    }
                    catch { continue; }
                    // whose programs these are: Microsoft's, or a publisher of an installed program (PACE's iLok beside «PACE License Support»)
                    bool known = false;
                    try
                    {
                        foreach (var exe in Directory.EnumerateFiles(d, "*.exe", SearchOption.AllDirectories).Take(20))
                        {
                            var co = Norm(FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "");
                            if (co.Contains("microsoft") || (co.Length >= 4 && tokens.Any(t => t.Length >= 4 && (co.StartsWith(t) || t.StartsWith(co))))) { known = true; break; }
                        }
                    }
                    catch { known = true; }
                    if (known) continue;
                    long bytes; DateTime newest;
                    Measure(d, out bytes, out newest);
                    bool empty = bytes == 0 && !Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any();
                    if (!empty && !sysNorm.Contains(n) && (bytes < minBytes || newest > old)) continue;
                    found.Add(new Leftover { Kind = "folder", Path = d, Bytes = bytes, Checked = false,
                        What = name + "  · " + (empty ? "пустая" : Junk.Size(bytes) + " · не менялась с " + newest.ToString("dd.MM.yyyy")) + "  · " + d });
                }
            }
        }

        // ---------- removing ----------

        static void RemoveSvc(string name)
        {
            int exit; string err;
            Ps("Stop-Service -Name '" + name.Replace("'", "''") + "' -Force -ErrorAction SilentlyContinue; & sc.exe delete '" + name.Replace("'", "''") + "' | Out-Null; exit $LASTEXITCODE", 60000, out exit, out err);
            if (exit != 0 && exit != 1072) throw new Exception("sc delete: " + exit); // 1072: marked, goes at the next start
        }
    }
}
