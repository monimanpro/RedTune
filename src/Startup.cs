using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RedTune
{
    // What starts with Windows, switched on and off the way Task Manager does it, so both show the same:
    // - Run keys (HKCU, HKLM, HKLM WOW6432Node) and the two Startup folders: the entry stays, its flag in
    //   Explorer\StartupApproved\{Run,Run32,StartupFolder} says on (02 …) or off (03 + the time it was switched off);
    // - scheduled tasks outside \Microsoft\ that start at logon or boot: the task's own Enabled.
    class StartupItem
    {
        public string Name, Command, Source, Kind; // Kind: run-hkcu, run-hklm, run-hklm32, folder-user, folder-all, task
        public string ValueName, FilePath, TaskPath;
        public bool Enabled;

        public string Exe { get { return Startup.ExePath(Command); } }
    }

    static class Startup
    {
        // the program out of a command line («"C:\x\a.exe" -arg», «C:\x\a.exe,0» from DisplayIcon, %vars%)
        public static string ExePath(string command)
        {
            var c = Environment.ExpandEnvironmentVariables(command ?? "").Trim();
            if (c.StartsWith("\"")) { int e = c.IndexOf('"', 1); return e > 1 ? c.Substring(1, e - 1) : c.Trim('"'); }
            int x = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return x > 0 ? c.Substring(0, x + 4) : c.Split(' ', ',')[0];
        }

        const string Cv = @"Software\Microsoft\Windows\CurrentVersion";
        const string Approved = Cv + @"\Explorer\StartupApproved\";

        static RegistryKey Hive(bool machine) { return RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64); }

        public static List<StartupItem> All()
        {
            var list = new List<StartupItem>();
            Run(list, false, Cv + @"\Run", "Run", "run-hkcu", "Реестр, этот пользователь");
            Run(list, true, Cv + @"\Run", "Run", "run-hklm", "Реестр, все пользователи");
            Run(list, true, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "run-hklm32", "Реестр, все пользователи (32-бит)");
            Folder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), false, "folder-user", "Папка «Автозагрузка»");
            Folder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true, "folder-all", "Папка «Автозагрузка» всех пользователей");
            try { Packaged(list); } catch (Exception ex) { Store.Log("автозагрузка: приложения-пакеты: " + ex.Message); }
            try { Tasks(list); } catch (Exception ex) { Store.Log("автозагрузка: задания: " + ex.Message); }
            return list;
        }

        // ---------- package apps (MSIX/Store) with a startup task: Claude, ChatGPT … ----------
        // Declared in the package's AppxManifest.xml (StartupTask TaskId, Enabled, the Extension's Executable);
        // the user's choice lives in AppModel\SystemAppData\<family>\<TaskId> State: 0 off, 1 off by the user,
        // 2 on, 3 off by policy, 4 on by policy (Task Manager writes the same value).
        const string AppModel = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\";

        static void Packaged(List<StartupItem> list)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var h = Hive(false))
            using (var rep = h.OpenSubKey(AppModel + @"Repository\Packages"))
            {
                if (rep == null) return;
                foreach (var full in rep.GetSubKeyNames().OrderByDescending(n => n))
                {
                    try
                    {
                        string root, disp;
                        using (var k = rep.OpenSubKey(full)) { root = k.GetValue("PackageRootFolder") as string; disp = k.GetValue("DisplayName") as string; }
                        var manifest = root == null ? null : Path.Combine(root, "AppxManifest.xml");
                        if (manifest == null || !File.Exists(manifest)) continue;
                        var text = File.ReadAllText(manifest);
                        if (text.IndexOf("startupTask", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var parts = full.Split('_');
                        var family = parts[0] + "_" + parts[parts.Length - 1];
                        var doc = new System.Xml.XmlDocument(); doc.LoadXml(text);
                        foreach (System.Xml.XmlElement t in doc.GetElementsByTagName("*").Cast<System.Xml.XmlNode>().OfType<System.Xml.XmlElement>().Where(e => e.LocalName == "StartupTask").ToList())
                        {
                            var id = t.GetAttribute("TaskId");
                            if (id.Length == 0 || !seen.Add(family + "|" + id)) continue;
                            var ext = t.ParentNode as System.Xml.XmlElement;
                            var exe = ext != null ? ext.GetAttribute("Executable") : "";
                            var name = t.GetAttribute("DisplayName");
                            if (name.Length == 0 || name.StartsWith("ms-resource:")) name = disp != null && !disp.StartsWith("@") && !disp.StartsWith("ms-resource:") ? disp : parts[0];
                            int state = TaskState(family, id, t.GetAttribute("Enabled").Equals("true", StringComparison.OrdinalIgnoreCase));
                            list.Add(new StartupItem
                            {
                                Name = name, Kind = "store", ValueName = id, TaskPath = family,
                                Command = exe.Length > 0 ? "\"" + Path.Combine(root, exe.Replace('/', '\\')) + "\"" : root,
                                Source = "Приложение из пакета (MSIX)" + (state >= 3 ? ", задано политикой" : ""),
                                Enabled = state == 2 || state == 4,
                            });
                        }
                    }
                    catch { }
                }
            }
        }

        static int TaskState(string family, string id, bool manifestOn)
        {
            using (var h = Hive(false))
            using (var k = h.OpenSubKey(AppModel + @"SystemAppData\" + family + "\\" + id))
            {
                var v = k == null ? null : k.GetValue("State");
                return v is int ? (int)v : manifestOn ? 2 : 0;
            }
        }

        static void SetPackaged(StartupItem it, bool on)
        {
            var family = it.TaskPath; var id = it.ValueName;
            if (TaskState(family, id, false) >= 3) throw new InvalidOperationException("это задано политикой, переключить нельзя");
            using (var h = Hive(false))
            using (var k = h.CreateSubKey(AppModel + @"SystemAppData\" + family + "\\" + id))
                k.SetValue("State", on ? 2 : 1, RegistryValueKind.DWord);
            it.Enabled = on;
            Store.Log("автозагрузка: «" + it.Name + "» (пакет " + family + ") " + (on ? "включена" : "выключена"));
        }

        static void Run(List<StartupItem> list, bool machine, string path, string approved, string kind, string source)
        {
            using (var h = Hive(machine))
            using (var k = h.OpenSubKey(path))
            {
                if (k == null) return;
                foreach (var n in k.GetValueNames())
                {
                    if (n.Length == 0) continue;
                    list.Add(new StartupItem { Name = n, ValueName = n, Command = Convert.ToString(k.GetValue(n)), Kind = kind, Source = source, Enabled = IsOn(machine, approved, n) });
                }
            }
        }

        static void Folder(List<StartupItem> list, string dir, bool machine, string kind, string source)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LinkTarget(f) : f;
                list.Add(new StartupItem { Name = Path.GetFileNameWithoutExtension(f), ValueName = name, FilePath = f, Command = target ?? f, Kind = kind, Source = source, Enabled = IsOn(machine, "StartupFolder", name) });
            }
        }

        // the flag's first byte: even = on (02, 06), odd = off (03, 07); no value = on
        static bool IsOn(bool machine, string approved, string name)
        {
            using (var h = Hive(machine))
            using (var k = h.OpenSubKey(Approved + approved))
            {
                var b = k == null ? null : k.GetValue(name) as byte[];
                return b == null || b.Length == 0 || (b[0] & 1) == 0;
            }
        }

        public static void Set(StartupItem it, bool on)
        {
            if (it.Kind == "task") { SetTask(it.TaskPath, on); it.Enabled = on; Store.Log("автозагрузка: задание " + it.TaskPath + (on ? " включено" : " выключено")); return; }
            if (it.Kind == "store") { SetPackaged(it, on); return; }
            bool machine = it.Kind != "run-hkcu" && it.Kind != "folder-user";
            string approved = it.Kind == "run-hklm32" ? "Run32" : it.Kind.StartsWith("folder") ? "StartupFolder" : "Run";
            var data = new byte[12];
            data[0] = (byte)(on ? 2 : 3);
            if (!on) Array.Copy(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()), 0, data, 4, 8);
            using (var h = Hive(machine))
            using (var k = h.CreateSubKey(Approved + approved))
                k.SetValue(it.ValueName, data, RegistryValueKind.Binary);
            it.Enabled = on;
            Store.Log("автозагрузка: «" + it.Name + "» " + (on ? "включена" : "выключена") + " (" + it.Source + ")");
        }

        // removes the entry for good (the command goes to the log, so it can be put back by hand)
        public static void Remove(StartupItem it)
        {
            if (it.Kind == "task") { RemoveTask(it); return; }
            if (it.Kind == "store") throw new InvalidOperationException("автозапуск приложения из пакета можно только выключить");
            Store.Log("автозагрузка: удалено «" + it.Name + "» (" + it.Source + "): " + it.Command);
            if (it.Kind.StartsWith("folder")) { Recycle(it.FilePath); }
            else
            {
                bool machine = it.Kind != "run-hkcu";
                var path = it.Kind == "run-hklm32" ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" : Cv + @"\Run";
                using (var h = Hive(machine)) using (var k = h.OpenSubKey(path, true)) if (k != null) k.DeleteValue(it.ValueName, false);
            }
            string approved = it.Kind == "run-hklm32" ? "Run32" : it.Kind.StartsWith("folder") ? "StartupFolder" : "Run";
            using (var h = Hive(it.Kind != "run-hkcu" && it.Kind != "folder-user"))
            using (var k = h.OpenSubKey(Approved + approved, true))
                if (k != null) k.DeleteValue(it.ValueName, false);
        }

        // ---------- scheduled tasks (Task Scheduler COM, late-bound) ----------

        static object Call(object o, string m, params object[] a) { return o.GetType().InvokeMember(m, BindingFlags.InvokeMethod, null, o, a); }
        static object Get(object o, string p, params object[] a) { return o.GetType().InvokeMember(p, BindingFlags.GetProperty, null, o, a); }

        static object Service()
        {
            var s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            Call(s, "Connect");
            return s;
        }

        static void Tasks(List<StartupItem> list)
        {
            var s = Service();
            Walk(Call(s, "GetFolder", "\\"), list);
        }

        static void Walk(object folder, List<StartupItem> list)
        {
            var path = (string)Get(folder, "Path");
            if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
            var tasks = Call(folder, "GetTasks", 1); // with hidden ones
            int n = (int)Get(tasks, "Count");
            for (int i = 1; i <= n; i++)
            {
                try
                {
                    var t = Get(tasks, "Item", i);
                    var def = Get(t, "Definition");
                    var triggers = Get(def, "Triggers");
                    bool start = false;
                    for (int j = 1, c = (int)Get(triggers, "Count"); j <= c; j++) { int type = (int)Get(Get(triggers, "Item", j), "Type"); if (type == 8 || type == 9) start = true; } // boot, logon
                    if (!start) continue;
                    string cmd = "";
                    var actions = Get(def, "Actions");
                    if ((int)Get(actions, "Count") > 0)
                    {
                        var a = Get(actions, "Item", 1);
                        if ((int)Get(a, "Type") == 0) { var exe = (string)Get(a, "Path") ?? ""; var args = (string)Get(a, "Arguments") ?? ""; cmd = (exe.Contains(" ") && !exe.StartsWith("\"") ? "\"" + exe + "\"" : exe) + (args.Length > 0 ? " " + args : ""); }
                    }
                    list.Add(new StartupItem { Name = (string)Get(t, "Name"), TaskPath = (string)Get(t, "Path"), Command = Environment.ExpandEnvironmentVariables(cmd), Kind = "task", Source = "Планировщик заданий", Enabled = (bool)Get(t, "Enabled") });
                }
                catch { }
            }
            var subs = Call(folder, "GetFolders", 0);
            for (int i = 1, c = (int)Get(subs, "Count"); i <= c; i++) Walk(Get(subs, "Item", i), list);
        }

        static void SetTask(string path, bool on)
        {
            var s = Service();
            var t = Call(Call(s, "GetFolder", "\\"), "GetTask", path);
            t.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, t, new object[] { on });
        }

        // a task whose program is gone: its XML goes to %APPDATA%\RedTune\deleted-tasks first (schtasks /Create /XML brings it back)
        static void RemoveTask(StartupItem it)
        {
            var s = Service();
            var t = Call(Call(s, "GetFolder", "\\"), "GetTask", it.TaskPath);
            var dir = Path.Combine(Store.DataDir, "deleted-tasks");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(it.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllText(Path.Combine(dir, safe + ".xml"), (string)Get(t, "Xml"), System.Text.Encoding.Unicode);
            int cut = it.TaskPath.LastIndexOf('\\');
            var folder = Call(s, "GetFolder", cut <= 0 ? "\\" : it.TaskPath.Substring(0, cut));
            Call(folder, "DeleteTask", it.TaskPath.Substring(cut + 1), 0);
            Store.Log("автозагрузка: удалено задание " + it.TaskPath + " (" + it.Command + "), копия в deleted-tasks");
        }

        // ---------- helpers ----------

        internal static string LinkTarget(string lnk)
        {
            try
            {
                var sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                var l = Call(sh, "CreateShortcut", lnk);
                var target = (string)Get(l, "TargetPath"); var args = (string)Get(l, "Arguments");
                if (string.IsNullOrEmpty(target)) return null;
                return (target.Contains(" ") ? "\"" + target + "\"" : target) + (string.IsNullOrEmpty(args) ? "" : " " + args);
            }
            catch { return null; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom, pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        // to the Recycle Bin, quietly
        internal static void Recycle(string file)
        {
            var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = file + "\0\0", fFlags = 0x40 | 0x10 | 0x400 | 0x4 }; // FO_DELETE; ALLOWUNDO, NOCONFIRMATION, NOERRORUI, SILENT
            int r = SHFileOperation(ref op);
            if (r != 0) throw new IOException("не удалось убрать в корзину (код " + r + ")");
        }
    }
}
