using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RedTune.Dist
{
    // Desktop shortcut that starts a program already on its cores: cmd "start /affinity" creates the process with the mask,
    // so a game protected by anti-cheat inherits it (CoD: cod.exe starts the protected sp26-cod.exe within a moment).
    // No admin rights and no UAC prompt: the game starts as usual, not elevated.
    static class Shortcuts
    {
        static string ListPath { get { return Path.Combine(Store.DataDir, "shortcuts.txt"); } } // lnk | exe | rule pattern

        public static string Create(Rule rule, string exe, ulong mask)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var name = new string(rule.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            var lnk = Path.Combine(desktop, (name.Length > 0 ? name : "Программа") + ".lnk");
            Write(lnk, exe, mask, rule.Group);
            var list = Load().Where(e => !e[0].Equals(lnk, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(new[] { lnk, exe, rule.Pattern });
            File.WriteAllLines(ListPath, list.Select(e => string.Join("|", e)), new UTF8Encoding(true));
            return lnk;
        }

        // after "Сохранить": shortcuts made earlier follow the new group of their rule
        public static void Refresh(IEnumerable<Rule> rules, Engine engine)
        {
            foreach (var e in Load())
            {
                if (!File.Exists(e[0]) || !File.Exists(e[1])) continue;
                var rule = rules.FirstOrDefault(r => r.Pattern.Equals(e[2], StringComparison.OrdinalIgnoreCase));
                if (rule == null || !rule.Enabled || rule.Group == Group.SKIP) continue;
                try { Write(e[0], e[1], engine.MaskOf(rule), rule.Group); } catch { }
            }
        }

        static List<string[]> Load()
        {
            if (!File.Exists(ListPath)) return new List<string[]>();
            return File.ReadAllLines(ListPath, Encoding.UTF8).Select(l => l.Split('|')).Where(p => p.Length == 3).ToList();
        }

        static void Write(string lnk, string exe, ulong mask, Group group)
        {
            var dir = Path.GetDirectoryName(exe);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType), sc = null;
            try
            {
                sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var t = sc.GetType();
                Action<string, object> set = (prop, value) => t.InvokeMember(prop, BindingFlags.SetProperty, null, sc, new[] { value });
                set("TargetPath", Path.Combine(Environment.SystemDirectory, "cmd.exe"));
                set("Arguments", "/c start \"\" /D \"" + dir + "\" /affinity " + mask.ToString("X") + " \"" + exe + "\"");
                set("WorkingDirectory", dir);
                set("IconLocation", exe + ",0");
                set("WindowStyle", 7); // minimized: cmd only flashes in the taskbar
                set("Description", "Распределение: запуск сразу на " + group);
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            finally
            {
                if (sc != null) Marshal.ReleaseComObject(sc);
                Marshal.ReleaseComObject(shell);
            }
        }
    }
}
