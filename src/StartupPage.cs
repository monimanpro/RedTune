using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // «Автозагрузка»: programs and scheduled tasks that start with Windows; a switch turns one off the same way
    // Task Manager does (the entry stays and can be switched back on), "…" opens its folder or removes it.
    class StartupPage : Page
    {
        readonly Row head;
        readonly FlatButton refresh = new FlatButton("Обновить"), folder = new FlatButton("Папка автозагрузки");
        readonly List<Control> listed = new List<Control>();
        bool loading;

        public StartupPage()
        {
            head = Row("Что запускается вместе с Windows", "Выключенное остаётся в списке — его можно включить обратно. То же видно в «Диспетчере задач» → «Автозагрузка»", folder, refresh);
            refresh.Click += (s, e) => Reload();
            folder.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\""); } catch { } };
            VisibleChanged += (s, e) => { if (Visible) Reload(); };
        }

        void Reload()
        {
            if (loading || !IsHandleCreated) return;
            loading = true; refresh.Enabled = false;
            Task.Run(() =>
            {
                List<StartupItem> items = null; string err = null;
                try { items = Startup.All(); } catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => { loading = false; refresh.Enabled = true; Show(items, err); })); } catch { loading = false; }
            });
        }

        void Show(List<StartupItem> items, string err)
        {
            int scroll = -AutoScrollPosition.Y;
            SuspendLayout();
            foreach (var c in listed) Remove(c);
            listed.Clear();
            if (err != null) { listed.Add(Row("Не удалось прочитать список", err)); }
            else
            {
                var apps = items.Where(i => i.Kind != "task").OrderBy(i => !i.Enabled).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                var tasks = items.Where(i => i.Kind == "task").OrderBy(i => !i.Enabled).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                head.SetSub("Включено " + items.Count(i => i.Enabled) + " из " + items.Count + ". Выключенное остаётся в списке — его можно включить обратно. То же видно в «Диспетчере задач»");
                listed.Add(Section("Программы"));
                if (apps.Count == 0) listed.Add(Row("Нет программ в автозагрузке"));
                foreach (var it in apps) listed.Add(Item(it));
                listed.Add(Section("Задания планировщика"));
                listed.Add(Row("Запускаются при входе или при старте Windows", "Задания Microsoft не показаны. Выключенное задание не удаляется; задание, чьей программы уже нет, можно удалить в «…»"));
                foreach (var it in tasks) listed.Add(Item(it));
            }
            ResumeLayout();
            Relayout();
            AutoScrollPosition = new Point(0, scroll);
        }

        // a full path, as far as can be told: a command with characters no path may have («Путь содержит недопустимые знаки»,
        // seen on another laptop, where it took the whole page down) is simply not one
        static bool Rooted(string p) { try { return !string.IsNullOrEmpty(p) && p.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(p); } catch { return false; } }

        Row Item(StartupItem it)
        {
            var on = new Toggle(); on.Set(it.Enabled);
            var more = new FlatButton("…");
            var exe = it.Exe;
            bool missing = Rooted(exe) && !File.Exists(exe);
            Func<string> sub = () => (missing ? "файла нет: " : "") + (string.IsNullOrEmpty(it.Command) ? "" : it.Command + " · ") + it.Source;
            var row = Row(it.Name, sub(), more, on);
            on.CheckedChanged += (s, e) =>
            {
                bool want = on.Checked;
                Task.Run(() =>
                {
                    string err = null;
                    try { Startup.Set(it, want); } catch (Exception ex) { err = ex.Message; Store.Log("автозагрузка: «" + it.Name + "»: " + err); }
                    try { BeginInvoke((Action)(() => { if (err != null) { on.Set(!want); row.SetSub("Не удалось: " + err); } else row.SetSub(sub()); })); } catch { }
                });
            };
            more.Click += (s, e) =>
            {
                var m = Page.Menu();
                // a missing file (an uninstalled program left its task behind): the nearest folder that still exists
                var open = m.Items.Add(missing ? "Открыть ближайшую папку" : "Открыть расположение", null, (o, a) => Reveal(it));
                open.Enabled = Nearest(exe) != null || it.FilePath != null;
                // tasks only when their program is gone (a working task is switched off, not deleted)
                if (it.Kind == "task" ? missing : it.Kind != "store") // package apps: only the switch
                    m.Items.Add(it.Kind == "task" ? "Удалить задание…" : "Удалить из автозагрузки…", null, (o, a) => Delete(it));
                ShowAt(m, more);
            };
            return row;
        }

        static void Reveal(StartupItem it)
        {
            var target = it.Exe;
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) target = it.FilePath != null && File.Exists(it.FilePath) ? it.FilePath : null;
            try
            {
                if (target != null) { Process.Start("explorer.exe", "/select,\"" + target + "\""); return; }
                var dir = Nearest(it.Exe);
                if (dir != null) Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        // the file if it exists, else the closest parent folder that does
        static string Nearest(string path)
        {
            try
            {
                if (!Rooted(path)) return null;
                if (File.Exists(path)) return path;
                for (var d = Path.GetDirectoryName(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                    if (Directory.Exists(d)) return d;
            }
            catch { }
            return null;
        }

        void Delete(StartupItem it)
        {
            var what = it.Kind == "task" ? "Программы этого задания уже нет. Задание удалится из планировщика; его копия (XML) сохранится в папке Red Tune → deleted-tasks, вернуть: schtasks /Create /XML."
                : it.Kind.StartsWith("folder") ? "Ярлык уйдёт в корзину.\nЧтобы просто не запускать, хватит выключить переключатель."
                : "Запись удалится из реестра; её команда останется в журнале Red Tune.\nЧтобы просто не запускать, хватит выключить переключатель.";
            if (!ConfirmDialog.Ask(FindForm(), "Автозагрузка", "Удалить «" + it.Name + "»?\n\n" + what, "Удалить", "Отмена")) return;
            try { Startup.Remove(it); } catch (Exception ex) { Msg.Show(FindForm(), "Не удалось: " + ex.Message, "Red Tune"); }
            Reload();
        }
    }
}
