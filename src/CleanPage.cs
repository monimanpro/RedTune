using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // «Очистка»: programs (the «Как после загрузки» button with its white («Белый список») / black («Чёрный список») lists, moved here
    // from «Главная» and «Настройки») and files (a scan of what can be deleted, by category; only ticked ones are deleted).
    class CleanPage : Page
    {
        readonly Settings s;
        readonly TabbedRows clean;
        readonly Row filesRow;
        readonly FlatButton scanBtn = new FlatButton("Сканировать"), deleteBtn = new FlatButton("Удалить выбранное", true);
        readonly List<Tuple<JunkCategory, Row, Toggle>> cats = new List<Tuple<JunkCategory, Row, Toggle>>();
        readonly Dictionary<JunkCategory, Pill> sizes = new Dictionary<JunkCategory, Pill>(); // the size found, on the right
        bool scanning, scanned;

        public CleanPage(Settings s)
        {
            this.s = s;
            tabs = Add(new PageTabs(tab, "Оперативная память", "Файлы на диске", "Удаление программ"), 0);
            tabs.Picked += i => { tabs.Selected = i; ShowTab(i); };
            int first = Controls.Count;

            var cleanBtn = new FlatButton("Очистить");
            cleanRow = Row("Очистка памяти", CleanSub, cleanBtn); // 16.11: was «Как после загрузки», a name that said little
            cleanBtn.Click += (o, e) => CleanUp(cleanBtn);
            clean = Add(new TabbedRows("Белый список", "Чёрный список"));
            clean.Resized += Relayout;
            FillClean(0); FillClean(1);
            Group(0, first); first = Controls.Count;

            filesRow = Row("Ненужные файлы", FilesSub, scanBtn, deleteBtn);
            deleteBtn.Enabled = false;
            scanBtn.Click += (o, e) => ScanFiles();
            deleteBtn.Click += (o, e) => DeleteFiles();
            foreach (var c in Junk.Categories())
            {
                var t = new Toggle(); t.Set(c.DefaultOn && !c.InfoOnly); t.Enabled = false;
                var size = new Pill(); size.Set("…", PillKind.Muted); sizes[c] = size;
                var row = Row(c.Name, c.Sub, size, t);
                if (c.InfoOnly) t.Visible = false;
                t.CheckedChanged += (o, e) => UpdateTotal();
                cats.Add(Tuple.Create(c, row, t));
            }
            Group(1, first); first = Controls.Count;

            // removing programs: the uninstaller, then what it left; leftovers of programs removed earlier
            search.Width = Theme.S(260);
            var orphansBtn = new FlatButton("Хвосты удалённых");
            appsRow = Row("Установленные программы", "Загружаю список…", search, orphansBtn);
            search.Box.TextChanged += (o, e) => Filter();
            orphansBtn.Click += (o, e) => FindOrphans(orphansBtn);
            Group(2, first); // the program rows join it as they load (Filter shows them only on this tab)

            ShowTab(tab);
            VisibleChanged += (o, e) => { if (Visible) Load(tab); };
        }

        // ---------- tabs: «Оперативная память» | «Файлы на диске» | «Удаление программ» ----------

        static int tab; // the last one open, kept while the app runs
        readonly PageTabs tabs;
        readonly List<Control>[] groups = { new List<Control>(), new List<Control>(), new List<Control>() };

        void Group(int g, int from) { for (int i = from; i < Controls.Count; i++) groups[g].Add(Controls[i]); }

        void ShowTab(int i)
        {
            tab = i;
            SuspendLayout();
            for (int g = 0; g < groups.Length; g++) foreach (var c in groups[g]) c.Visible = g == i;
            AutoScrollPosition = new System.Drawing.Point(0, 0);
            ResumeLayout();
            Filter(); // the program rows, then the layout
            if (Visible) Load(i);
        }

        // the slow parts start when their tab is first opened: the disk scan, the list of programs
        void Load(int i)
        {
            if (i == 1 && !scanned && !scanning) ScanFiles();
            if (i == 2 && apps == null && !loadingApps) { loadingApps = true; LoadApps(); }
        }
        bool loadingApps;

        // ---------- removing programs ----------

        readonly Dist.SearchBox search = new Dist.SearchBox("Найти программу");
        readonly Row appsRow;
        List<InstalledApp> apps;
        readonly List<Tuple<InstalledApp, Row>> appRows = new List<Tuple<InstalledApp, Row>>();
        bool busyApps;

        void LoadApps()
        {
            Task.Run(() =>
            {
                List<InstalledApp> list = null;
                try { list = Uninst.List(); } catch (Exception ex) { Store.Log("удаление программ: список: " + ex.Message); list = new List<InstalledApp>(); }
                try { BeginInvoke((Action)(() => ShowApps(list))); } catch { }
            });
        }

        void ShowApps(List<InstalledApp> list)
        {
            apps = list;
            SuspendLayout();
            foreach (var t in appRows) Remove(t.Item2);
            appRows.Clear();
            foreach (var a in list)
            {
                var btn = new FlatButton("Удалить…");
                var app = a;
                var where = AppFolder(a);
                Row row;
                if (where != null)
                {
                    var open = FolderButton();
                    open.Click += (o, e) => Reveal(where, null);
                    row = Row(a.Name, AppSub(a), open, btn);
                }
                else row = Row(a.Name, AppSub(a), btn);
                btn.Click += (o, e) => UninstallApp(app, row, btn);
                appRows.Add(Tuple.Create(a, row));
            }
            ResumeLayout();
            appsRow.SetSub(list.Count + " " + Plural(list.Count, "программа", "программы", "программ") + " · удаляются вместе с хвостами");
            Filter();
        }

        static string AppSub(InstalledApp a)
        {
            var parts = new List<string>();
            if (a.Publisher.Length > 0) parts.Add(a.Publisher);
            if (a.Version.Length > 0) parts.Add(a.Version);
            if (a.SizeKb > 0) parts.Add(Junk.Size(a.SizeKb * 1024));
            DateTime d;
            if (a.Date.Length == 8 && DateTime.TryParseExact(a.Date, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out d)) parts.Add(d.ToString("dd.MM.yyyy"));
            return string.Join(" · ", parts);
        }

        void Filter()
        {
            var q = search.Box.Text.Trim();
            foreach (var t in appRows)
                t.Item2.Visible = tab == 2 && (q.Length == 0 || t.Item1.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || t.Item1.Publisher.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            Relayout();
        }

        void UninstallApp(InstalledApp a, Row row, FlatButton btn)
        {
            if (busyApps) return;
            if (!ConfirmDialog.Ask(FindForm(), "Удаление программы", "Удалить «" + a.Name + "»?\n\nСначала откроется её собственный деинсталлятор — пройдите его как обычно. " +
                "Потом Red Tune найдёт, что осталось (папки, записи реестра, ярлыки, автозапуск), и покажет список — удалится только отмеченное.", "Удалить", "Отмена")) return;
            busyApps = true; btn.Enabled = false; row.SetSub("Идёт удаление — пройдите окно деинсталлятора…");
            Task.Run(() =>
            {
                string err = null;
                try { Uninst.RunUninstaller(a, () => false); } catch (Exception ex) { err = ex.Message; }
                bool still = Uninst.StillInstalled(a);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (err != null) { busyApps = false; btn.Enabled = true; row.SetSub("Не удалось запустить деинсталлятор: " + err); return; }
                        bool forgotten = false;
                        if (still)
                        {
                            forgotten = ConfirmDialog.Ask(FindForm(), "Удаление программы", "«" + a.Name + "» всё ещё в списке установленных. Удаление прошло, но запись осталась, или его отменили?",
                                "Прошло — искать хвосты", "Отменили");
                            if (!forgotten) { busyApps = false; btn.Enabled = true; row.SetSub(AppSub(a)); return; }
                        }
                        row.SetSub("Ищу хвосты…");
                        Task.Run(() =>
                        {
                            List<Leftover> left;
                            try { left = Uninst.Leftovers(a, forgotten); } catch (Exception ex) { left = new List<Leftover>(); Store.Log("удаление программ: хвосты: " + ex.Message); }
                            try { BeginInvoke((Action)(() => OfferLeftovers(a, row, left, () => { busyApps = false; btn.Enabled = true; LoadApps(); }))); } catch { busyApps = false; }
                        });
                    }));
                }
                catch { busyApps = false; }
            });
        }

        // the list with ticks, then the removal in the background; done() refreshes the page
        void OfferLeftovers(InstalledApp a, Row row, List<Leftover> left, Action done)
        {
            if (left.Count == 0) { row.SetSub("Удалена, хвостов не найдено"); done(); return; }
            var picked = PickDialog.Pick(FindForm(), "Хвосты «" + a.Name + "»", "Что осталось после удаления. Файлы и папки уйдут в корзину, записи реестра перед удалением сохраняются в .reg-файл " +
                "(папка Red Tune → removed-registry). Снимите галочку с того, что нужно оставить.", left, "Удалить отмеченное");
            if (picked == null || picked.Count == 0) { row.SetSub("Удалена · хвосты оставлены"); done(); return; }
            row.SetSub("Убираю хвосты…");
            Task.Run(() =>
            {
                var result = Uninst.Remove(picked, a.Name);
                try { BeginInvoke((Action)(() => { appsRow.SetSub("«" + a.Name + "»: " + result); done(); })); } catch { }
            });
        }

        void FindOrphans(FlatButton btn)
        {
            if (apps == null || busyApps) return;
            busyApps = true; btn.Enabled = false; appsRow.SetSub("Ищу хвосты удалённых программ…");
            Task.Run(() =>
            {
                List<Leftover> found;
                try { found = Uninst.Orphans(apps); } catch (Exception ex) { found = new List<Leftover>(); Store.Log("удаление программ: старые хвосты: " + ex.Message); }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        busyApps = false; btn.Enabled = true;
                        if (found.Count == 0) { appsRow.SetSub("Хвостов удалённых программ не найдено"); return; }
                        var picked = PickDialog.Pick(FindForm(), "Хвосты удалённых программ", "Папки в AppData и ProgramData, которым не нашлась ни одна установленная программа и которые не менялись 2 месяца. " +
                            "Это подсказка, а не точный ответ: ничего не отмечено — отметьте то, что узнаёте. Удаляется в корзину.", found, "Удалить отмеченное");
                        if (picked == null || picked.Count == 0) { appsRow.SetSub("Найдено " + found.Count + " · ничего не удалено"); return; }
                        appsRow.SetSub("Удаляю…");
                        Task.Run(() =>
                        {
                            var result = Uninst.Remove(picked, "старые хвосты");
                            try { BeginInvoke((Action)(() => appsRow.SetSub(result))); } catch { }
                        });
                    }));
                }
                catch { busyApps = false; }
            });
        }

        const string FilesSub = "Временные файлы, кэши и отчёты, которые Windows и программы создадут заново. Удаляется только отмеченное";

        void ScanFiles()
        {
            if (scanning || !IsHandleCreated) return;
            scanning = true; scanBtn.Enabled = deleteBtn.Enabled = false;
            filesRow.SetSub("Сканирую…");
            foreach (var t in cats) { t.Item3.Enabled = false; sizes[t.Item1].Set("…", PillKind.Muted); }
            Task.Run(() =>
            {
                foreach (var t in cats)
                {
                    try { Junk.Scan(t.Item1); } catch { }
                    var item = t;
                    try { BeginInvoke((Action)(() => ShowCategory(item))); } catch { }
                }
                try { BeginInvoke((Action)(() => { scanning = false; scanned = true; scanBtn.Enabled = true; UpdateTotal(); if (lastResult != null) { filesRow.SetSub(lastResult); lastResult = null; } })); } catch { scanning = false; }
            });
        }

        void ShowCategory(Tuple<JunkCategory, Row, Toggle> t)
        {
            var c = t.Item1;
            sizes[c].Set(c.Bytes > 0 ? Junk.Size(c.Bytes) : "пусто", c.Bytes > 0 && !c.InfoOnly ? PillKind.Warn : PillKind.Muted);
            t.Item3.Enabled = !c.InfoOnly && c.Bytes > 0;
            t.Item2.Enabled = c.Bytes > 0 || c.InfoOnly;
        }

        IEnumerable<Tuple<JunkCategory, Row, Toggle>> Picked { get { return cats.Where(t => !t.Item1.InfoOnly && t.Item3.Checked && t.Item1.Bytes > 0); } }

        void UpdateTotal()
        {
            if (!scanned || scanning) return;
            long all = cats.Where(t => !t.Item1.InfoOnly).Sum(t => t.Item1.Bytes), picked = Picked.Sum(t => t.Item1.Bytes);
            filesRow.SetSub(all == 0 ? "Удалять нечего" : "Найдено " + Junk.Size(all) + " · отмечено " + Junk.Size(picked));
            deleteBtn.Enabled = picked > 0;
        }

        void DeleteFiles()
        {
            var picked = Picked.ToList();
            if (picked.Count == 0) return;
            var text = "Удалить " + Junk.Size(picked.Sum(t => t.Item1.Bytes)) + "?\n\n" + string.Join("\n", picked.Select(t => "• " + t.Item1.Name + " — " + Junk.Size(t.Item1.Bytes))) +
                "\n\nФайлы, занятые программами, пропустятся. Корзину вернуть будет нельзя.";
            if (!ConfirmDialog.Ask(FindForm(), "Очистка", text, "Удалить", "Отмена")) return;
            scanning = true; scanBtn.Enabled = deleteBtn.Enabled = false;
            filesRow.SetSub("Удаляю…");
            foreach (var t in cats) t.Item3.Enabled = false;
            Task.Run(() =>
            {
                long freed = 0; int busy = 0;
                foreach (var t in picked)
                {
                    int skipped = 0;
                    try { freed += Junk.Clean(t.Item1, out skipped); } catch { }
                    busy += skipped;
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        scanning = false; scanned = false;
                        lastResult = "Освобождено " + Junk.Size(freed) + (busy > 0 ? " · " + busy + " " + Plural(busy, "файл занят", "файла заняты", "файлов заняты") + " программами" : "");
                        ScanFiles(); // the rescan shows what is left; the result stays on the row
                    }));
                }
                catch { scanning = false; }
            });
        }

        string lastResult;

        readonly Row cleanRow;
        internal const string CleanSub = "Закрыть всё, что открыто после входа в Windows";

        // find → confirm with the list → close politely → ask before forcing what did not close → report freed memory
        void CleanUp(FlatButton btn) { RunClean(this, t => cleanRow.SetSub(t ?? CleanSub), busy => btn.Enabled = !busy); }

        // 16.11: the run itself, apart from the page, so the mini panel at the tray icon starts the very same one.
        // owner: the control whose window the questions belong to; say: the line of status (null: the row's own text
        // again); busy: the button off while it works
        public static void RunClean(Control owner, Action<string> say, Action<bool> busy)
        {
            busy(true); say("Ищу лишнее…");
            Task.Run(() =>
            {
                System.Collections.Generic.List<CleanSlate.Proc> list = null; string err = null;
                var svcs = new System.Collections.Generic.List<CleanSlate.Svc>();
                try { list = CleanSlate.Find(); } catch (Exception ex) { err = ex.Message; }
                try { svcs = CleanSlate.Services(Comp.S); } catch (Exception ex) { Store.Log("как после загрузки: службы: " + ex.Message); }
                try
                {
                    owner.BeginInvoke((Action)(() =>
                    {
                        busy(false);
                        if (err != null) { say("Не удалось: " + err); return; }
                        if (list.Count == 0 && svcs.Count == 0) { say("Лишнего нет — всё как после загрузки"); return; }
                        var names = list.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        var svcText = svcs.Count > 0 ? "Остановить до перезагрузки " + Plural(svcs.Count, "службу", "службы", "служб") + ": " + string.Join(", ", svcs.Select(x => x.Display)) : "";
                        var text = list.Count > 0
                            ? "Закрыть " + names + " " + Plural(names, "программу", "программы", "программ") + " (" + list.Count + " " + Plural(list.Count, "процесс", "процесса", "процессов") + ")?\n\n" +
                              CleanSlate.Summary(list) + (svcs.Count > 0 ? "\n\n" + svcText : "") + "\n\nНесохранённое в них может пропасть: программы с окнами сначала попросят закрыться сами."
                            : svcText + "?";
                        if (!ConfirmDialog.Ask(owner.FindForm(), "Очистка памяти", text, list.Count > 0 ? "Закрыть" : "Остановить", "Отмена")) { say(null); return; }
                        busy(true); say("Закрываю…");
                        ulong before = FreeMemory();
                        Task.Run(() =>
                        {
                            var left = list.Count > 0 ? CleanSlate.Close(list) : new System.Collections.Generic.List<CleanSlate.Proc>();
                            var svcLeft = CleanSlate.StopServices(svcs); // after their programs: a window may still talk to its service while closing
                            try
                            {
                                owner.BeginInvoke((Action)(() =>
                                {
                                    if (left.Count > 0 && ConfirmDialog.Ask(owner.FindForm(), "Не закрылись", "Эти программы не закрылись — возможно, ждут сохранения:\n\n" + CleanSlate.Summary(left) + "\n\nЗакрыть принудительно? Несохранённое в них пропадёт.", "Закрыть принудительно", "Оставить"))
                                    { CleanSlate.Force(left); left.Clear(); }
                                    var freed = (long)FreeMemory() - (long)before;
                                    var parts = new System.Collections.Generic.List<string>();
                                    if (list.Count > 0) parts.Add("Закрыто " + (list.Count - left.Count) + " из " + list.Count);
                                    if (svcs.Count > 0) parts.Add((list.Count > 0 ? "служб остановлено " : "Служб остановлено ") + (svcs.Count - svcLeft.Count) + " из " + svcs.Count);
                                    if (freed > 0) parts.Add("освобождено " + (freed / 1073741824.0).ToString("0.0") + " ГБ памяти");
                                    if (left.Count > 0) parts.Add("остались: " + CleanSlate.Summary(left, 5));
                                    if (svcLeft.Count > 0) parts.Add("не остановились: " + string.Join(", ", svcLeft.Select(x => x.Display)));
                                    say(string.Join(" · ", parts));
                                    busy(false);
                                }));
                            }
                            catch { }
                        });
                    }));
                }
                catch { }
            });
        }

        static ulong FreeMemory() { ulong total, avail; return Native.Memory(out total, out avail) ? avail : 0; }

        // ---------- «открыть расположение» ----------

        static string iconFamily;
        static FlatButton FolderButton()
        {
            if (iconFamily == null)
                iconFamily = System.Drawing.FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            var b = new FlatButton(""); // an open folder; one symbol = a square button
            b.Font = new System.Drawing.Font(iconFamily, Theme.Body.Size * 1.1f); // follows the app's scale like the text fonts
            return b;
        }

        static void Reveal(string folder, string file)
        {
            try
            {
                if (file != null && System.IO.File.Exists(file)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\"");
                else System.Diagnostics.Process.Start("explorer.exe", "\"" + folder.TrimEnd('\\') + "\"");
            }
            catch { }
        }

        // a list entry: a folder opens (a Store app's installed version), an exe is shown selected where it runs from or lives
        void OpenEntry(string entry, FlatButton btn)
        {
            btn.Enabled = false;
            Task.Run(() =>
            {
                string folder = null, file = null;
                try
                {
                    if (CleanSlate.IsFolder(entry)) folder = CleanSlate.ResolveFolder(entry);
                    else
                    {
                        string path;
                        if (CleanSlate.Running().TryGetValue(entry, out path)) { file = path; folder = System.IO.Path.GetDirectoryName(path); }
                        else { folder = Dist.Folders.FindExeFolder(entry); if (folder != null) file = System.IO.Path.Combine(folder, entry); }
                    }
                }
                catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        btn.Enabled = true;
                        if (folder == null) { Msg.Show(FindForm(), L.T("Не нашёл, где лежит «") + entry + "»" + (CleanSlate.IsFolder(entry) ? "" : L.T(": запустите программу, и её место станет известно.")), "Red Tune"); return; }
                        Reveal(folder, file);
                    }));
                }
                catch { }
            });
        }

        // an installed program: its folder, else the folder of its uninstaller (not msiexec in Windows); null = unknown
        static string AppFolder(InstalledApp a)
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var d in new[] { a.Location, System.IO.Path.GetDirectoryName(Startup.ExePath(a.Uninstall) ?? "") })
            {
                if (string.IsNullOrEmpty(d)) continue;
                var dir = d.Trim().Trim('"').TrimEnd('\\');
                if (dir.Length > 3 && !dir.StartsWith(win, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(dir)) return dir;
            }
            return null;
        }

        static string Plural(int n, string one, string few, string many)
        {
            int a = n % 100, b = n % 10;
            return a >= 11 && a <= 14 ? many : b == 1 ? one : b >= 2 && b <= 4 ? few : many;
        }

        string CleanList(int tab) { return tab == 0 ? s.CleanKeep : s.CleanClose; }
        void SetCleanList(int tab, IEnumerable<string> names)
        {
            var v = string.Join("|", names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            if (tab == 0) s.CleanKeep = v; else s.CleanClose = v;
            Store.SaveSettings(s);
        }

        // one tab: a note, a row per program with «Убрать», and «Добавить…» (running programs or an exe)
        void FillClean(int tab)
        {
            clean.Clear(tab);
            Func<string, string> title = n => CleanSlate.IsFolder(n) ? CleanSlate.FolderTitle(n) : System.IO.Path.GetFileNameWithoutExtension(n);
            var names = CleanSlate.Names(CleanList(tab)).OrderBy(title, StringComparer.OrdinalIgnoreCase).ToList();
            clean.Add(tab, new Row(tab == 0 ? "Белый список: никогда не закрываются" : "Чёрный список: закрываются всегда, даже если запустились сами при загрузке",
                tab == 0 ? "Exe — эта программа со всем, что она запускает; папка — всё, что из неё запущено"
                         : "Exe — только эта программа; папка — всё, что из неё запущено, и её службы до перезагрузки · Windows и Red Tune не закрываются никогда"));
            // the two that are never closed (built into CleanSlate): shown, but without «Убрать»
            if (tab == 0)
                foreach (var b in new[] { Tuple.Create("Red Tune", "RedTune.exe · сама программа") })
                {
                    var pill = new Pill(); pill.Set("всегда", PillKind.Ok);
                    clean.Add(tab, new Row(b.Item1, b.Item2, pill));
                }
            foreach (var n in names)
            {
                var remove = new FlatButton("Убрать");
                var name = n;
                remove.Click += (o, e) => { SetCleanList(tab, CleanSlate.Names(CleanList(tab)).Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase))); FillClean(tab); };
                var sub = CleanSlate.IsFolder(n) ? n + (n.IndexOf('*') >= 0 ? " · любая версия" : "") + (tab == 1 ? " · папка целиком, со службами" : " · папка целиком") : n;
                var open = FolderButton();
                open.Click += (o, e) => OpenEntry(name, open);
                clean.Add(tab, new Row(title(n), sub, open, remove));
            }
            if (names.Count == 0 && tab == 1) clean.Add(tab, new Row("Список пуст"));
            var add = new FlatButton("Добавить…");
            add.Click += (o, e) => AddClean(tab, add);
            clean.Add(tab, new Row("Добавить программу", "Из запущенных сейчас, exe-файлом или папкой", add));
            clean.Relist();
        }

        void AddClean(int tab, FlatButton anchor)
        {
            System.Collections.Generic.SortedDictionary<string, string> running;
            try { running = CleanSlate.Running(); } catch { running = new System.Collections.Generic.SortedDictionary<string, string>(); }
            var have = CleanSlate.Names(CleanList(tab));
            var other = CleanSlate.Names(CleanList(1 - tab));
            Action<string> add = name =>
            {
                SetCleanList(tab, have.Concat(new[] { name }));
                if (other.Contains(name)) { SetCleanList(1 - tab, other.Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase))); FillClean(1 - tab); } // one list at a time
                FillClean(tab);
            };
            var m = Page.Menu();
            foreach (var kv in running.Where(kv => !have.Contains(kv.Key)))
            {
                var name = kv.Key;
                m.Items.Add(name, null, (o, e) => add(name)).ToolTipText = kv.Value;
            }
            if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Выбрать exe…", null, (o, e) =>
            {
                using (var d = new OpenFileDialog { Filter = L.T("Программы (*.exe)|*.exe"), Title = L.T("Выберите exe программы") })
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) add(System.IO.Path.GetFileName(d.FileName));
            });
            m.Items.Add("Выбрать папку…", null, (o, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = L.T(tab == 1 ? "Папка программы: закроется всё, что из неё запущено, и остановятся её службы" : "Папка программы: всё, что из неё запущено, останется"), ShowNewFolderButton = false })
                {
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    var dir = d.SelectedPath.TrimEnd('\\');
                    var bad = CleanSlate.BadFolder(dir);
                    if (bad != null) { Msg.Show(FindForm(), L.T(bad) + ": " + dir + "\n\n" + L.T("Выберите папку одной программы, например C:\\Program Files\\Cloudflare\\Cloudflare WARP."), "Red Tune"); return; }
                    dir = CleanSlate.AnyVersion(dir); // a Store app: every version, not just the installed one
                    if (!have.Contains(dir)) add(dir);
                }
            });
            ShowAt(m, anchor);
        }

    }
}
