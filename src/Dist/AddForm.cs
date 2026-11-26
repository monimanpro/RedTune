using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RedTune.Dist
{
    // Pick a running program, an exe or a folder; the rule (pattern and name) is made from it, the group is chosen.
    // 27.11 (the user: the patterns are not clear to an ordinary person): «Выбрать папку…» makes the rule from a folder,
    // a line in plain words says what the rule will cover, the pattern itself waits behind «Точнее…».
    class AddForm : Form
    {
        class Candidate { public string Path, Name, Process; public Image Icon; }

        public Rule Result;
        public string ResultIconPath;
        readonly ListBox list = new ListBox();
        readonly SearchBox nameBox = new SearchBox("Название");
        readonly SearchBox patternBox = new SearchBox(@"\Папка\ или имя процесса");
        readonly FlatButton[] groupBtns;
        // translated at once: a button takes its width from its text
        readonly FlatButton browseBtn = new FlatButton(L.T("Выбрать exe…"));
        readonly FlatButton folderBtn = new FlatButton(L.T("Выбрать папку…"));
        readonly FlatButton moreBtn = new FlatButton(L.T("Точнее…"));
        readonly FlatButton okBtn = new FlatButton("Добавить", true, "+");
        readonly FlatButton cancelBtn = new FlatButton("Отмена");
        readonly Label summary = new Label();
        readonly Label hint = new Label();
        readonly Label patternLabel;
        Group group = Group.CCD1;
        string pickedPath;     // the exe picked, for the icon
        string pickedFolder;   // the folder the pattern stands for, when known
        bool settingPattern;   // the pattern is being set by the form, not typed
        int counting;          // the newest count of exe files; older ones are dropped when they come back

        static readonly HashSet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program Files", "Program Files (x86)", "WindowsApps", "Users", "AppData", "Local", "Roaming", "Programs", "ProgramData", "LocalLow",
        };

        // names too common to stand alone in a rule: the folder above joins them (\D-Games\Tools\)
        static readonly HashSet<string> Common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "games", "tools", "apps", "soft", "software", "programs", "utilities", "utils", "portable", "launcher", "data", "common", "editor", "hub",
            "downloads", "download", "desktop", "documents", "install", "installed", "setup", "new", "old", "temp", "tmp", "test",
        };

        // a person's own folders wherever they are (D:\Downloads): many unrelated programs, refused
        static readonly HashSet<string> Personal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Desktop", "Documents", "Downloads", "Рабочий стол", "Документы", "Загрузки",
        };

        public AddForm(Engine engine)
        {
            Text = L.T("Добавить программу");
            BackColor = Theme.Window; ForeColor = Theme.Text; Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(560), Theme.S(620));
            int pad = Theme.S(18), w = ClientSize.Width - 2 * pad, col = pad + Theme.S(110), cw = w - Theme.S(110);

            MakeLabel("Запущенные программы", pad, Theme.S(14));
            list.Bounds = new Rectangle(pad, Theme.S(40), w, Theme.S(250));
            list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Card; list.ForeColor = Theme.Text;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = Theme.S(40); list.IntegralHeight = false;
            list.DrawItem += DrawCandidate;
            list.SelectedIndexChanged += (s, e) => { var c = list.SelectedItem as Candidate; if (c != null) PickExe(c.Path, c.Name); };
            list.HandleCreated += (s, e) => Native.DarkScrollBars(list.Handle);

            browseBtn.BackColor = folderBtn.BackColor = Theme.Window;
            browseBtn.Location = new Point(pad, Theme.S(300));
            folderBtn.Location = new Point(browseBtn.Right + Theme.S(8), Theme.S(300));
            browseBtn.Click += (s, e) => Browse();
            folderBtn.Click += (s, e) => BrowseFolder();

            MakeLabel("Название", pad, Theme.S(350));
            nameBox.Bounds = new Rectangle(col, Theme.S(346), cw, Theme.S(32));

            MakeLabel("Что попадёт", pad, Theme.S(392));
            summary.ForeColor = Theme.Text2; summary.Font = Theme.Small; summary.AutoSize = false;
            summary.Bounds = new Rectangle(col, Theme.S(392), cw, Theme.S(40));
            summary.Text = L.T("Выберите программу в списке, exe или папку");

            // the pattern itself: hidden behind «Точнее…» until asked for
            moreBtn.BackColor = Theme.Window;
            moreBtn.Location = new Point(col, Theme.S(438));
            moreBtn.Click += (s, e) => ShowPattern();
            patternLabel = MakeLabel("Шаблон", pad, Theme.S(442));
            patternBox.Bounds = new Rectangle(col, Theme.S(438), cw, Theme.S(32));
            patternBox.Box.TextChanged += (s, e) => { if (!settingPattern) { pickedFolder = null; pickedPath = null; Summarize(); } };
            hint.Text = L.T(@"\Папка\ — все exe из папки. Имя без \ — один процесс.");
            hint.ForeColor = Theme.Muted; hint.Font = Theme.Small; hint.AutoSize = false;
            hint.Bounds = new Rectangle(col, Theme.S(474), cw, Theme.S(20));
            patternLabel.Visible = patternBox.Visible = hint.Visible = false;

            MakeLabel("Группа", pad, Theme.S(512));
            groupBtns = new[] { Group.CCD1, Group.CCD2, Group.ALL, Group.SKIP }.Select(g => new FlatButton(g.ToString())).ToArray();
            int gx = col;
            for (int i = 0; i < groupBtns.Length; i++)
            {
                var g = (Group)i;
                groupBtns[i].BackColor = Theme.Window;
                groupBtns[i].Location = new Point(gx, Theme.S(506));
                groupBtns[i].Click += (s, e) => SetGroup(g);
                gx += groupBtns[i].Width + Theme.S(8);
            }
            var range = new Label { Text = "CCD1 — " + L.T(engine.RangeText(Group.CCD1)) + ", CCD2 — " + L.T(engine.RangeText(Group.CCD2)) + ", ALL — " + L.T("все") + ", SKIP — " + L.T("не трогать"), ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true };
            range.Location = new Point(col, Theme.S(544));
            Controls.Add(range);

            okBtn.BackColor = cancelBtn.BackColor = Theme.Window;
            okBtn.Location = new Point(ClientSize.Width - pad - okBtn.Width, ClientSize.Height - pad - okBtn.Height);
            cancelBtn.Location = new Point(okBtn.Left - Theme.S(8) - cancelBtn.Width, okBtn.Top);
            okBtn.Click += (s, e) => Finish();
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { list, browseBtn, folderBtn, nameBox, summary, moreBtn, patternBox, hint, okBtn, cancelBtn });
            Controls.AddRange(groupBtns);
            SetGroup(Group.CCD1);
            foreach (var c in Candidates()) list.Items.Add(c);
        }

        Label MakeLabel(string text, int x, int y)
        {
            var l = new Label { Text = L.T(text), ForeColor = Theme.Text2, Font = Theme.Body, AutoSize = true, Location = new Point(x, y) };
            Controls.Add(l);
            return l;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }

        void SetGroup(Group g)
        {
            group = g;
            for (int i = 0; i < groupBtns.Length; i++) { groupBtns[i].Primary = (Group)i == g; groupBtns[i].Invalidate(); }
        }

        // the off-screen render (--page-png distadd): a folder as if picked, the pattern shown
        internal void Pretend(string folder, bool more) { if (folder != null) PickFolder(folder); if (more) ShowPattern(); }

        void ShowPattern() { moreBtn.Visible = false; patternLabel.Visible = patternBox.Visible = hint.Visible = true; patternBox.Box.Focus(); }

        IEnumerable<Candidate> Candidates()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            int self = Process.GetCurrentProcess().Id;
            var seen = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id <= 4 || p.Id == self) continue;
                    var path = Native.GetPath(p.Id);
                    if (path == null || path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || seen.ContainsKey(path)) continue;
                    seen[path] = new Candidate { Path = path, Process = p.ProcessName, Name = Describe(path, p.ProcessName) };
                }
                catch { }
                finally { p.Dispose(); }
            }
            return seen.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        static string Describe(string path, string fallback)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                var d = (vi.FileDescription ?? "").Trim();
                if (d.Length == 0) d = (vi.ProductName ?? "").Trim();
                if (d.Length > 0 && d.Length <= 60) return d;
            }
            catch { }
            return fallback;
        }

        void DrawCandidate(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var c = (Candidate)list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Raised : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            if (c.Icon == null) { try { using (var ic = Icon.ExtractAssociatedIcon(c.Path)) c.Icon = ic.ToBitmap(); } catch { } }
            int k = Theme.S(20);
            if (c.Icon != null) e.Graphics.DrawImage(c.Icon, e.Bounds.X + Theme.S(10), e.Bounds.Y + (e.Bounds.Height - k) / 2, k, k);
            int x = e.Bounds.X + Theme.S(40);
            Theme.Text1(e.Graphics, c.Name, Theme.Body, new Rectangle(x, e.Bounds.Y + Theme.S(2), e.Bounds.Width - x - Theme.S(8), Theme.S(20)), Theme.Text, TextFormatFlags.NoPadding);
            Theme.Text1(e.Graphics, c.Path, Theme.Small, new Rectangle(x, e.Bounds.Y + Theme.S(20), e.Bounds.Width - x - Theme.S(8), Theme.S(18)), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);
        }

        void Browse()
        {
            using (var d = new OpenFileDialog { Filter = L.T("Программы (*.exe)|*.exe"), Title = L.T("Выберите exe программы") })
                if (d.ShowDialog(this) == DialogResult.OK) PickExe(d.FileName, Describe(d.FileName, Path.GetFileNameWithoutExtension(d.FileName)));
        }

        void BrowseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = L.T("Выберите папку программы или игры"), ShowNewFolderButton = false })
            {
                if (pickedFolder != null && Directory.Exists(pickedFolder)) d.SelectedPath = pickedFolder;
                if (d.ShowDialog(this) == DialogResult.OK) PickFolder(d.SelectedPath);
            }
        }

        void PickExe(string path, string name)
        {
            pickedPath = path;
            nameBox.Box.Text = name;
            var pattern = SuggestPattern(path);
            SetPattern(pattern);
            pickedFolder = pattern.Contains("\\") ? Folders.FromExe(new Rule { Pattern = pattern }, path) : null;
            Summarize();
        }

        void PickFolder(string dir)
        {
            string refuse;
            var pattern = FolderPattern(dir, out refuse);
            if (pattern == null)
            {
                SetPattern(""); pickedFolder = null; pickedPath = null; nameBox.Box.Text = "";
                summary.Text = L.T(refuse); summary.ForeColor = Theme.Warning;
                return;
            }
            pickedPath = null;
            pickedFolder = dir.TrimEnd('\\');
            SetPattern(pattern);
            // a whole path (C:\Program Files\7-Zip\) would be named after Program Files: the folder's own name then
            var name = Rule.DefaultName(pattern);
            if (Roots.Contains(name) || name.EndsWith(":")) name = Path.GetFileName(pickedFolder);
            nameBox.Box.Text = name;
            Summarize();
        }

        void SetPattern(string pattern) { settingPattern = true; patternBox.Box.Text = pattern; settingPattern = false; }

        // what the rule will cover, in words; the exe files of its folder are counted off the UI thread
        void Summarize()
        {
            summary.ForeColor = Theme.Text2;
            var pattern = patternBox.Box.Text.Trim();
            int me = ++counting;
            if (pattern.Length == 0) { summary.Text = L.T("Выберите программу в списке, exe или папку"); return; }
            if (!pattern.Contains("\\")) { summary.Text = L.T("Только процесс") + " «" + Path.GetFileNameWithoutExtension(pattern) + "»"; return; }
            var folder = pickedFolder;
            if (folder == null && pattern.Contains(":") && Directory.Exists(pattern)) folder = pattern.TrimEnd('\\');
            if (folder == null) { summary.Text = L.T("Все программы, в пути которых есть") + " «" + pattern + "»"; return; }
            var head = L.T("Все программы из папки") + " «" + Path.GetFileName(folder.TrimEnd('\\')) + "»";
            summary.Text = head + "\n" + L.T("Считаю exe…");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var found = ExeFiles(folder, 2000);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (me != counting || IsDisposed) return;
                        if (pickedPath == null && found.Count > 0) pickedPath = found[0]; // the icon of the row
                        summary.Text = head + "\n" + (found.Count == 0 ? L.T("exe в ней пока нет — правило сработает, когда они появятся")
                            : L.T("сейчас в ней exe:") + " " + found.Count + (found.Count >= 2000 ? "+" : "") + " — " + string.Join(", ", found.Take(3).Select(Path.GetFileName)) + (found.Count > 3 ? "…" : ""));
                    }));
                }
                catch { }
            });
        }

        // the exe files in a folder and its subfolders, the shallow ones first; links to other places are not followed
        static List<string> ExeFiles(string root, int most)
        {
            var found = new List<string>();
            var level = new List<string> { root };
            int dirs = 0;
            while (level.Count > 0 && found.Count < most && dirs < 5000)
            {
                var next = new List<string>();
                foreach (var d in level)
                {
                    if (++dirs > 5000 || found.Count >= most) break;
                    try { found.AddRange(Directory.GetFiles(d, "*.exe").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(most - found.Count)); } catch { }
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(d))
                            try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) next.Add(sub); } catch { }
                    }
                    catch { }
                }
                level = next;
            }
            return found;
        }

        // the program's own folder: walk up past version folders (app-1.0.9) and generic ones (bin, x64, Application)
        public static string SuggestPattern(string exe)
        {
            var proc = Path.GetFileNameWithoutExtension(exe);
            var dir = Path.GetDirectoryName(exe) ?? "";
            var parts = dir.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                var p = parts[i];
                if (Rule.IsVersionFolder(p) || Rule.Generic.Contains(p)) continue;
                if (Roots.Contains(p)) break;
                return "\\" + p + "\\";
            }
            return proc;
        }

        // 27.11: a folder the person picked → the pattern of a rule for every exe in it. The folder's own name when it says
        // something (\Witcher 3\); with the generic and version folders under it kept (\Cyberpunk 2077\bin\x64\); a common name
        // gets the folder above it (\D-Games\Tools\); the whole path when nothing better is there. Places with many unrelated
        // programs (a drive, Windows, Program Files, the user's folders) are refused: null and the reason.
        public static string FolderPattern(string dir, out string refuse)
        {
            refuse = null;
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { refuse = "Эта папка не подходит"; return null; }
            var root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
            if (full.Length <= root.Length) { refuse = "Целый диск — слишком много. Выберите папку одной программы или игры"; return null; }
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            if (win.Length > 0 && (full.Equals(win, StringComparison.OrdinalIgnoreCase) || full.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)))
            { refuse = "Папки Windows не распределяются"; return null; }
            var parts = full.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (Shared().Contains(full) || Roots.Contains(parts[parts.Length - 1]) || Personal.Contains(parts[parts.Length - 1]))
            { refuse = "Это общая папка, в ней много разных программ. Выберите папку одной программы или игры"; return null; }

            int start = parts.Length - 1;
            while (start >= 0 && (Rule.IsVersionFolder(parts[start]) || Rule.Generic.Contains(parts[start]) || parts[start].Length < 3) && !Roots.Contains(parts[start])) start--;
            if (start >= 0 && Common.Contains(parts[start]))
            {
                if (start > 0 && !Roots.Contains(parts[start - 1])) start--;
                else start = -1;
            }
            if (start < 0 || Roots.Contains(parts[start])) return full + "\\";
            return "\\" + string.Join("\\", parts, start, parts.Length - start) + "\\";
        }

        // the folders that hold many unrelated programs or a person's own files
        static HashSet<string> Shared()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = p => { if (!string.IsNullOrEmpty(p)) set.Add(p.TrimEnd('\\')); };
            foreach (var f in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
                add(Environment.GetFolderPath(f));
            add(Environment.GetEnvironmentVariable("ProgramW6432"));
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                add(Path.Combine(profile, "Downloads"));
                add(Path.GetDirectoryName(profile.TrimEnd('\\'))); // C:\Users
            }
            add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            return set;
        }

        void Finish()
        {
            var pattern = patternBox.Box.Text.Trim();
            if (pattern.Length == 0) { summary.Text = L.T("Выберите программу в списке, exe или папку"); summary.ForeColor = Theme.Warning; return; }
            var name = nameBox.Box.Text.Trim();
            if (name.Length == 0) name = Rule.DefaultName(pattern);
            Result = new Rule { Group = group, Pattern = pattern, Name = name };
            ResultIconPath = pickedPath;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
