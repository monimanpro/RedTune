using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RedTune.Dist
{
    // The Distribution app (programs on CCD1 / CCD2 / all cores) as a page. The engine runs from the app's start
    // (MainForm), this page edits a draft of the rules: "Сохранить" applies it, "Отменить" drops it.
    class DistPage : Page
    {
        readonly Engine engine;
        List<Rule> saved = new List<Rule>(), draft = new List<Rule>();
        Settings savedOpts, draftOpts;
        readonly GroupCard[] cards;
        readonly Row headRow;
        readonly RedTune.Toggle runToggle = new RedTune.Toggle();
        readonly RedTune.Toggle optStore = new RedTune.Toggle(), optLaunchers = new RedTune.Toggle();
        readonly SearchBox search = new SearchBox("Найти программу");
        readonly RedTune.FlatButton addBtn = new RedTune.FlatButton("+  Добавить программу", true);
        readonly RedTune.FlatButton undoBtn = new RedTune.FlatButton("Отменить"), saveBtn = new RedTune.FlatButton("Сохранить", true);
        readonly FooterBar bar;
        readonly Timer timer = new Timer { Interval = 1500 };
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // rule key -> folder (null = not found)
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase), gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool finding;

        // folders of the programs that aren't running, looked up on a background thread (disk lookups)
        void FindFolders()
        {
            if (finding) return;
            List<Rule> todo;
            lock (folders) todo = draft.Where(r => !folders.ContainsKey(r.Key)).Select(r => r.Clone()).ToList();
            if (todo.Count == 0) return;
            finding = true;
            var wasKnown = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var results = new List<Tuple<string, string, bool>>();
                foreach (var r in todo)
                {
                    var f = Folders.Find(r);
                    lock (folders) folders[r.Key] = f;
                    results.Add(Tuple.Create(r.Key, f, f == null && (wasKnown.Contains(r.Key) || Folders.WasSeen(r.Key))));
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        finding = false;
                        foreach (var t in results) SetFolder(t.Item1, t.Item2, t.Item3);
                        FindFolders(); // rules added meanwhile
                    }));
                }
                catch { finding = false; }
            });
        }

        // a folder moved while the page was hidden: forget it and look again
        void RecheckFolders()
        {
            List<string> moved;
            lock (folders) moved = folders.Where(kv => kv.Value != null && !Directory.Exists(kv.Value)).Select(kv => kv.Key).ToList();
            if (moved.Count == 0) return;
            lock (folders) foreach (var k in moved) folders.Remove(k);
            foreach (var c in cards) foreach (var row in c.Rows)
                if (moved.Contains(row.Rule.Key)) { row.Folder = null; row.Invalidate(); }
            FindFolders();
        }

        // the result of a lookup: a folder, or "gone" when it had been known (found before, or the program ran from there)
        void SetFolder(string key, string folder, bool isGone)
        {
            if (folder != null) known.Add(key);
            if (isGone) gone.Add(key); else gone.Remove(key);
            foreach (var c in cards) foreach (var row in c.Rows)
                if (row.Rule.Key == key && (row.Folder != folder || row.FolderGone != isGone)) { row.Folder = folder; row.FolderGone = isGone; row.Invalidate(); }
        }

        public void OpenFolder(RuleRow row)
        {
            var exe = row.Status != null ? row.Status.SamplePath : null;
            var folder = exe != null ? Folders.FromExe(row.Rule, exe) : null;
            if (folder == null) folder = row.Folder;
            if (folder != null && Directory.Exists(folder)) { Open(folder, exe); return; }

            // moved or deleted since it was found: look again (off the UI thread, it walks the disks)
            var rule = row.Rule;
            var copy = rule.Clone();
            lock (folders) folders.Remove(rule.Key);
            bar.SetStatus("Папки «" + rule.Name + "» нет на старом месте, ищу…");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var found = Folders.Find(copy);
                lock (folders) folders[copy.Key] = found;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        SetFolder(copy.Key, found, found == null);
                        if (found != null) { bar.SetStatus("Новое место «" + rule.Name + "»: " + found); Open(found, null); return; }
                        UpdateDirty();
                        AskRemove(rule);
                    }));
                }
                catch { }
            });
        }

        void AskRemove(Rule rule)
        {
            if (!draft.Contains(rule)) return;
            var text = "Папки программы «" + rule.Name + "» больше нет: её удалили или перенесли туда, где Red Tune её не находит.\n\n" +
                       "Убрать «" + rule.Name + "» из распределения?\n(если программу просто перенесли, оставьте: папка найдётся при её первом запуске)";
            if (!ConfirmDialog.Ask(FindForm(), "Папка не найдена", text, "Убрать из распределения", "Оставить")) return;
            draft.Remove(rule);
            BuildRows();
            bar.SetStatus("«" + rule.Name + "» убрана из списка · нажмите «Сохранить», чтобы применить");
        }

        void Open(string folder, string exe)
        {
            try { Folders.Open(folder, exe); }
            catch (Exception ex) { bar.SetStatus("Не открылась папка: " + ex.Message); }
        }
        bool loading;
        string lastNote = "";

        public DistPage(Engine engine)
        {
            this.engine = engine;
            PageGroups.AddTabs(this, "dist");
            runToggle.Set(!engine.Paused);
            headRow = Row("Распределение программ по чипам", "", runToggle);
            runToggle.CheckedChanged += (s, e) => { engine.Paused = !runToggle.Checked; Store.Log(engine.Paused ? "пауза: все программы снова на всех ядрах" : "работает"); RefreshStatus(); };
            search.Width = Theme.S(260);
            Row("Программы", "Строку можно перетащить в другую группу", search, addBtn);
            // 20.11: the game chip is the one with 3D V-Cache on an X3D; more stores than Steam and Epic, and what launchers start
            Row("Новые игры → " + engine.GameName, "Игры из папок Steam, Epic, Xbox, EA, Ubisoft, GOG и Riot и то, что запускают лаунчеры, — сразу на этот чип, даже без своего правила", optStore);
            Row("Не трогать лаунчеры", "Steam, Epic, Battle.net и другие остаются на всех ядрах: их игры сами получают нужные ядра", optLaunchers);
            cards = new[] { new GroupCard(this, Group.CCD1), new GroupCard(this, Group.CCD2), new GroupCard(this, Group.ALL), new GroupCard(this, Group.SKIP) };
            foreach (var c in cards) { c.Range = engine.RangeText(c.Group); Add(c, 12); }
            Footer = bar = new FooterBar(undoBtn, saveBtn);

            optStore.CheckedChanged += (s, e) => { if (!loading) { draftOpts.StoreGames = optStore.Checked; UpdateDirty(); } };
            optLaunchers.CheckedChanged += (s, e) => { if (!loading) { draftOpts.SkipLaunchers = optLaunchers.Checked; cards[3].Note = draftOpts.SkipLaunchers ? LaunchersNote : ""; Relayout(); UpdateDirty(); } };
            search.Box.TextChanged += (s, e) => BuildRows();
            addBtn.Click += (s, e) => AddProgram();
            undoBtn.Click += (s, e) => { ResetDraft(); bar.SetStatus("Изменения отменены"); };
            saveBtn.Click += (s, e) => Save();
            timer.Tick += (s, e) => RefreshStatus();
            VisibleChanged += (s, e) => { engine.SetFastScan(Visible); if (Visible) { RefreshStatus(); RecheckFolders(); timer.Start(); } else timer.Stop(); };

            saved = Store.LoadRules(engine.BackGroup);
            savedOpts = Store.LoadSettings();
            ResetDraft();
        }

        const string LaunchersNote = "Ещё автоматически: лаунчеры Steam, Epic, Battle.net, Riot, EA, Ubisoft, GOG (игры получают ядра сами)";

        protected override void BeforeLayout(int width) { if (cards != null) foreach (var c in cards) c.Relayout(width); }

        void ResetDraft()
        {
            draft = saved.Select(r => { var c = r.Clone(); c.Origin = r; return c; }).ToList();
            draftOpts = savedOpts.Clone();
            loading = true;
            optStore.Set(draftOpts.StoreGames); optLaunchers.Set(draftOpts.SkipLaunchers);
            cards[3].Note = draftOpts.SkipLaunchers ? LaunchersNote : "";
            loading = false;
            BuildRows();
        }

        void BuildRows()
        {
            var q = search.Box.Text.Trim();
            foreach (var c in cards)
            {
                var old = c.Rows;
                c.Controls.Clear();
                foreach (var r in old) r.Dispose();
                foreach (var rule in draft.Where(r => r.Group == c.Group))
                {
                    var row = new RuleRow(this, rule);
                    row.Shown = q.Length == 0 || rule.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || rule.Pattern.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    row.Visible = row.Shown;
                    string f;
                    lock (folders) if (folders.TryGetValue(rule.Key, out f)) row.Folder = f;
                    row.FolderGone = gone.Contains(rule.Key);
                    c.Controls.Add(row);
                }
            }
            FindFolders();
            Relayout();
            RefreshStatus();
            UpdateDirty();
        }

        public string PendingNote(Rule r)
        {
            if (r.Origin == null) return "новое, не сохранено";
            var parts = new List<string>();
            if (r.Group != r.Origin.Group) parts.Add("было " + r.Origin.Group);
            if (r.Enabled != r.Origin.Enabled) parts.Add(r.Enabled ? "включено" : "будет выключено");
            if (r.NoSmt != r.Origin.NoSmt) parts.Add(r.NoSmt ? "станет без SMT" : "вернётся SMT");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        int DirtyCount()
        {
            if (draft == null || savedOpts == null) return 0;
            int n = 0;
            foreach (var r in draft)
            {
                if (r.Origin == null) { n++; continue; }
                if (r.Group != r.Origin.Group) n++;
                if (r.Enabled != r.Origin.Enabled) n++;
                if (r.NoSmt != r.Origin.NoSmt) n++;
            }
            n += saved.Count(s => !draft.Any(d => d.Origin == s));
            if (draftOpts.StoreGames != savedOpts.StoreGames) n++;
            if (draftOpts.SkipLaunchers != savedOpts.SkipLaunchers) n++;
            return n;
        }

        void UpdateDirty()
        {
            int n = DirtyCount();
            undoBtn.Enabled = saveBtn.Enabled = n > 0;
            bar.SetStatus(n > 0 ? "Не сохранено: " + n + " " + Word(n, "изменение", "изменения", "изменений") : lastNote);
        }

        static string Word(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? few : many;
        }

        void Save()
        {
            int n = DirtyCount();
            if (n == 0) return;
            var newSaved = new List<Rule>();
            foreach (var d in draft) { var c = d.Clone(); newSaved.Add(c); d.Origin = c; }
            saved = newSaved;
            savedOpts = draftOpts.Clone();
            try { Store.SaveRules(saved); Store.SaveSettings(savedOpts); }
            catch (Exception ex) { Msg.Show(FindForm(), "Не получилось сохранить правила: " + ex.Message, "Red Tune"); return; }
            engine.Apply(saved, savedOpts);
            try { Shortcuts.Refresh(saved, engine); } catch { }
            Store.Log("сохранено изменений: " + n);
            lastNote = "Сохранено: " + n + " " + Word(n, "изменение", "изменения", "изменений") + " · запущенные программы переехали сразу";
            BuildRows();
        }

        // ---------- row actions (called by the rows and cards) ----------

        public void MoveRule(Rule r, Group g) { if (r.Group == g) return; r.Group = g; BuildRows(); }

        public void ToggleRule(Rule r)
        {
            r.Enabled = !r.Enabled;
            foreach (var c in cards) foreach (var row in c.Rows) if (row.Rule == r) row.Invalidate();
            UpdateDirty();
        }

        void ToggleSmt(Rule r)
        {
            r.NoSmt = !r.NoSmt;
            foreach (var c in cards) foreach (var row in c.Rows) if (row.Rule == r) row.Invalidate();
            UpdateDirty();
        }

        static Image Dot(Group g)
        {
            int k = Theme.S(16), d = Theme.S(9);
            var bmp = new Bitmap(k, k);
            using (var gr = Graphics.FromImage(bmp))
            {
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(DistColors.Dot(g))) gr.FillRectangle(b, (k - d) / 2, (k - d) / 2, d, d);
            }
            return bmp;
        }

        void AddGroupItems(ToolStripItemCollection items, Rule r)
        {
            foreach (var g in new[] { Group.CCD1, Group.CCD2, Group.ALL, Group.SKIP })
            {
                var target = g;
                var item = new ToolStripMenuItem(g + " · " + engine.RangeText(g), Dot(g), (s, e) => MoveRule(r, target));
                if (r.Group == g) item.Font = Theme.Bold;
                items.Add(item);
            }
            if (r.Group == Group.SKIP) return;
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("Только ядра, без SMT (один поток на ядро)", null, (s, e) => ToggleSmt(r)) { Checked = r.NoSmt });
        }

        public void ShowGroupMenu(RuleRow row, Point at)
        {
            var m = Page.Menu();
            AddGroupItems(m.Items, row.Rule);
            m.Show(row, at);
        }

        public void ShowRowMenu(RuleRow row, Point at)
        {
            var r = row.Rule;
            var m = Page.Menu();
            var move = new ToolStripMenuItem("Перенести в");
            AddGroupItems(move.DropDownItems, r);
            move.DropDown.BackColor = Theme.Card;
            m.Items.Add(move);
            m.Items.Add(new ToolStripMenuItem(r.Enabled ? "Выключить правило" : "Включить правило", null, (s, e) => ToggleRule(r)));
            var path = row.Status != null ? row.Status.SamplePath : null;
            if (r.Group != Group.SKIP) m.Items.Add(new ToolStripMenuItem("Ярлык запуска на рабочем столе…", null, (s, e) => CreateLauncher(r, path)));
            if (path != null || row.Folder != null) m.Items.Add(new ToolStripMenuItem("Открыть папку", null, (s, e) => OpenFolder(row)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Удалить", null, (s, e) => { draft.Remove(r); BuildRows(); }));
            m.Show(row, at);
        }

        // a game protected by anti-cheat can't be moved once running, but a process created on the cores keeps them
        void CreateLauncher(Rule r, string runningPath)
        {
            string dir = runningPath != null ? Path.GetDirectoryName(runningPath) : null;
            using (var d = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Какой exe вы запускаете для «" + r.Name + "»? (обычно стартер в папке игры)", InitialDirectory = dir ?? "" })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    var lnk = Shortcuts.Create(r, d.FileName, engine.MaskOf(r));
                    lastNote = "Ярлык «" + Path.GetFileNameWithoutExtension(lnk) + "» на рабочем столе: запускает сразу на " + r.Group;
                    UpdateDirty();
                    Store.Log("ярлык запуска: " + lnk + " -> " + d.FileName + " на " + r.Group);
                }
                catch (Exception ex) { Msg.Show(FindForm(), "Не получилось создать ярлык: " + ex.Message, "Red Tune"); }
            }
        }

        void AddProgram()
        {
            using (var f = new AddForm(engine))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK || f.Result == null) return;
                var existing = draft.FirstOrDefault(r => string.Equals(r.Pattern, f.Result.Pattern, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { existing.Group = f.Result.Group; existing.Enabled = true; }
                else draft.Add(f.Result);
                if (f.ResultIconPath != null) GetIcon(f.ResultIconPath);
                search.Box.Text = "";
                BuildRows();
            }
        }

        // ---------- live status ----------

        Image GetIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Image img;
            if (icons.TryGetValue(path, out img)) return img;
            try { using (var ic = Icon.ExtractAssociatedIcon(path)) img = ic.ToBitmap(); } catch { img = null; }
            icons[path] = img;
            return img;
        }

        void RefreshStatus()
        {
            var st = engine.Status;
            foreach (var c in cards)
                foreach (var row in c.Rows)
                {
                    RuleStatus rs;
                    st.TryGetValue(row.Rule.Key, out rs);
                    bool changed = (rs == null) != (row.Status == null) || (rs != null && (rs.Count != row.Status.Count || rs.Protected != row.Status.Protected));
                    row.Status = rs;
                    if (row.Icon == null && rs != null && rs.SamplePath != null) { row.Icon = GetIcon(rs.SamplePath); changed = true; }
                    if (changed) row.Invalidate();
                }
            int managed = engine.ManagedCount;
            headRow.SetSub(engine.Paused ? "Пауза · ядрами управляет Windows" :
                "Работает · " + managed + " " + Word(managed, "процесс", "процесса", "процессов") + " на своих ядрах" + (engine.TwoChips ? " · 2 чипа × " + engine.CoreCount / 2 + " ядер" : "")
                + (engine.X3D ? " · 3D V-Cache на " + engine.GameGroup : ""));
            ShowHints();
            if (DirtyCount() == 0)
            {
                var last = engine.LastAction;
                if (last.Length > 0 && lastNote.Length == 0) bar.SetStatus(last);
            }
        }

        // ---------- 20.11: hints ----------
        // Programs without a rule that keep the processor busy (Engine.Hints), above the groups: the button with the chip
        // that suits it puts a rule into the draft (saved with «Сохранить», as any other change here), «…» offers the
        // other groups, «Скрыть» drops the hint for good. A program the draft already has a rule for is not shown.
        SectionLabel hintHead;
        readonly List<RedTune.Row> hintRows = new List<RedTune.Row>();
        string hintsShown = "";

        void ShowHints()
        {
            if (cards == null) return;
            var list = engine.Hints().Where(h => !draft.Any(r => r.Enabled && Engine.Hit(r, h.Name, h.Path))).ToList();
            var sig = string.Join("|", list.Select(h => h.Path + ">" + engine.Suggest(h)));
            if (sig == hintsShown)
            {
                for (int i = 0; i < list.Count && i < hintRows.Count; i++) hintRows[i].SetSub(HintSub(list[i]));
                return;
            }
            hintsShown = sig;
            SuspendLayout();
            foreach (var r in hintRows) Remove(r);
            hintRows.Clear();
            if (hintHead != null) { Remove(hintHead); hintHead = null; }
            if (list.Count > 0)
            {
                hintHead = InsertBefore(cards[0], new SectionLabel("Подсказки"), 10);
                foreach (var h in list) hintRows.Add(InsertBefore(cards[0], HintRow(h)));
            }
            ResumeLayout();
            Relayout();
        }

        string HintSub(Hint h)
        {
            var g = engine.Suggest(h);
            string why = g == Group.ALL ? "Больше ядер, чем на одном чипе: лучше оставить на всех"
                : h.Foreground ? "Нагружал процессор, пока был на экране — похоже на игру" : "Нагружает процессор в фоне";
            int pct = (int)Math.Round(h.Load * 100 / Math.Max(1, Environment.ProcessorCount));
            return why + " · " + Math.Max(1, pct) + " % процессора, " + (int)Math.Max(1, Math.Round(h.Busy / 60)) + " мин";
        }

        static string Title(string path)
        {
            try { var d = FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrWhiteSpace(d) && d.Trim().Length <= 60) return d.Trim(); } catch { }
            return Path.GetFileNameWithoutExtension(path);
        }

        RedTune.Row HintRow(Hint h)
        {
            var g = engine.Suggest(h);
            var go = new RedTune.FlatButton("→ " + g, true);
            var more = new RedTune.FlatButton("…");
            var hide = new RedTune.FlatButton("Скрыть");
            var name = Title(h.Path);
            var row = new RedTune.Row(name, HintSub(h), go, more, hide);
            go.Click += (s, e) => TakeHint(h, name, g);
            more.Click += (s, e) =>
            {
                var m = Page.Menu();
                foreach (var x in new[] { Group.CCD1, Group.CCD2, Group.ALL, Group.SKIP })
                {
                    var target = x;
                    var item = new ToolStripMenuItem(x + " · " + engine.RangeText(x), Dot(x), (o, a) => TakeHint(h, name, target));
                    if (x == g) item.Font = Theme.Bold;
                    m.Items.Add(item);
                }
                m.Show(more, new Point(0, more.Height));
            };
            hide.Click += (s, e) => { engine.Hide(h.Path); hintsShown = ""; ShowHints(); };
            return row;
        }

        void TakeHint(Hint h, string name, Group g)
        {
            var pattern = AddForm.SuggestPattern(h.Path);
            var existing = draft.FirstOrDefault(r => string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.Group = g; existing.Enabled = true; }
            else draft.Add(new Rule { Group = g, Pattern = pattern, Name = name });
            GetIcon(h.Path);
            Store.Log("подсказка: " + name + " -> " + g + " (" + pattern + ")");
            BuildRows();
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
