using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RedTune
{
    // 23.11 (the user: «добавим правила поиска и удаления временных файлов»): what «Очистка → Файлы на диске» looks at, kept in
    // junk-rules.txt next to the settings. The age of temporary files and of logs, a «never touch» list, and folders of the
    // user's own cleaned like the temp folders. What is always protected is not a rule: Junk.Always and Refuse below.
    class JunkRules
    {
        public int TempHours = 24, LogDays = 7;
        public List<string> Never = new List<string>(), Extra = new List<string>();

        // the steps of the age slider, hours: fresh files are often still in use, so nothing below an hour
        public static readonly int[] AgeSteps = { 1, 2, 3, 6, 12, 24, 48, 72, 168, 336, 720 };

        public static string Age(int hours)
        {
            if (hours == 24) return L.T("суток");
            if (hours < 24) return hours + " " + L.T("ч");
            return hours / 24 + " " + L.T("дн");
        }

        static string FilePath { get { return Path.Combine(Store.DataDir, "junk-rules.txt"); } }

        static JunkRules current;
        public static JunkRules Current { get { if (current == null) current = Load(); return current; } }

        public JunkRules Copy() { return new JunkRules { TempHours = TempHours, LogDays = LogDays, Never = new List<string>(Never), Extra = new List<string>(Extra) }; }

        static JunkRules Load()
        {
            var r = new JunkRules();
            try
            {
                if (!File.Exists(FilePath)) return r;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim(); int n;
                    if (k == "temphours" && int.TryParse(v, out n)) r.TempHours = Math.Max(1, Math.Min(720, n));
                    else if (k == "logdays" && int.TryParse(v, out n)) r.LogDays = Math.Max(1, Math.Min(90, n));
                    else if (k == "never" && v.Length > 0) r.Never.Add(v);
                    else if (k == "extra" && v.Length > 0) r.Extra.Add(v);
                }
            }
            catch (Exception ex) { Store.Log("правила очистки: " + ex.Message); }
            return r;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("temphours=" + TempHours); sb.AppendLine("logdays=" + LogDays);
            foreach (var n in Never) sb.AppendLine("never=" + n);
            foreach (var e in Extra) sb.AppendLine("extra=" + e);
            Directory.CreateDirectory(Store.DataDir);
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
            current = this;
            Store.Log("правила очистки: сохранены (временные старше " + TempHours + " ч, логи старше " + LogDays + " дн, никогда " + Never.Count + ", свои папки " + Extra.Count + ")");
        }

        static string Expand(string s) { try { return Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')); } catch { return s.Trim(); } }

        // «никогда не трогать»: a path (C:\…, %TEMP%\…) takes what lies in it; a mask (*.iso) a file or folder of that name
        // anywhere; a word what has it in its path
        // asked for every file of a scan: the entries are turned into tests once (a rules object is not changed after Save:
        // the dialog edits a copy)
        List<Func<string, bool>> tests;
        public bool Blocked(string path)
        {
            if (tests == null)
            {
                var t = new List<Func<string, bool>>();
                foreach (var n in Never)
                {
                    var e = Expand(n).TrimEnd('\\'); if (e.Length < 2) continue;
                    if (e.IndexOf('*') >= 0 || e.IndexOf('?') >= 0)
                    {
                        var re = new Regex("^" + Regex.Escape(e).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        t.Add(p => p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Any(re.IsMatch));
                    }
                    else if (e.Length > 2 && (e[1] == ':' || e.StartsWith(@"\\")))
                        t.Add(p => { var q = p.TrimEnd('\\'); return q.Equals(e, StringComparison.OrdinalIgnoreCase) || q.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase); });
                    else t.Add(p => p.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                tests = t;
            }
            foreach (var test in tests) if (test(path)) return true;
            return false;
        }

        // the folders a list of the user's own may name, as cleaned folders: never a drive, the user's folder, a personal
        // folder itself (Documents, Downloads…), Windows, Program Files, ProgramData, AppData, Red Tune's own data, or a folder
        // above any of them. A folder inside a personal folder is the user's choice. null when it may be cleaned.
        public static string Refuse(string folder)
        {
            string d;
            try { d = Path.GetFullPath(Expand(folder)).TrimEnd('\\'); } catch { return L.T("не путь к папке"); }
            if (d.Length <= 3) return L.T("целый диск");
            Func<Environment.SpecialFolder, string> sf = f => { try { return Environment.GetFolderPath(f).TrimEnd('\\'); } catch { return ""; } };
            var guarded = new[]
            {
                sf(Environment.SpecialFolder.UserProfile), sf(Environment.SpecialFolder.Desktop), sf(Environment.SpecialFolder.MyDocuments),
                sf(Environment.SpecialFolder.MyPictures), sf(Environment.SpecialFolder.MyMusic), sf(Environment.SpecialFolder.MyVideos),
                Path.Combine(sf(Environment.SpecialFolder.UserProfile), "Downloads"), Path.Combine(sf(Environment.SpecialFolder.UserProfile), "OneDrive"),
                sf(Environment.SpecialFolder.Windows), sf(Environment.SpecialFolder.ProgramFiles), sf(Environment.SpecialFolder.ProgramFilesX86),
                sf(Environment.SpecialFolder.CommonApplicationData), sf(Environment.SpecialFolder.ApplicationData), sf(Environment.SpecialFolder.LocalApplicationData),
                Store.DataDir.TrimEnd('\\'),
            }.Where(x => x.Length > 3);
            foreach (var g in guarded)
                if (d.Equals(g, StringComparison.OrdinalIgnoreCase) || g.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) return L.T("защищённая папка");
            if (d.StartsWith(Store.DataDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return L.T("защищённая папка");
            return null;
        }

        // the user's own folders that may be cleaned, expanded
        public IEnumerable<string> ExtraFolders() { return Extra.Where(x => Refuse(x) == null).Select(x => Path.GetFullPath(Expand(x)).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase); }

        // the protections, as the clean-up keeps them: shown in the dialog
        public static readonly string[] Always =
        {
            "Личные папки — Рабочий стол, Документы, Загрузки, Изображения, Музыка, Видео, OneDrive — не просматриваются",
            "Файлы, открытые сейчас программами, пропускаются",
            "Сами папки остаются: удаляется только то, что в них, и пустые папки внутри",
            "Ссылки на другие папки (junction, symlink) не проходятся",
            "Данные Red Tune и рабочие файлы Claude",
            "Своей папкой для очистки нельзя сделать диск целиком, папку пользователя, личную папку, Windows, Program Files, ProgramData и AppData",
        };
    }

    // the dialog of the rules (a page of rows inside a window, the buttons under it), like the one of «Удаление программ»
    class JunkRulesDialog : DarkForm
    {
        readonly JunkRules r;
        Slider age, logs; Row neverRow, extraRow;
        DialogUndo undo;

        string Snap() { return DialogUndo.Join(new[] { age.Value.ToString(), logs.Value.ToString(), DialogUndo.List(r.Never), DialogUndo.List(r.Extra) }); }

        void LoadSnap(string s)
        {
            var p = DialogUndo.Split(s);
            age.Value = int.Parse(p[0]); logs.Value = int.Parse(p[1]); r.Never = DialogUndo.List(p[2]); r.Extra = DialogUndo.List(p[3]);
            ShowLists();
        }

        JunkRulesDialog(JunkRules rules)
        {
            r = rules.Copy();
            Text = L.T("Правила очистки файлов"); FormBorderStyle = FormBorderStyle.Sizable;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(900), wa.Width - 60), Math.Min(Theme.S(640), wa.Height - 60));
            MinimumSize = new Size(Theme.S(640), Theme.S(420));

            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(64), BackColor = Theme.Window };
            var ok = new FlatButton("Сохранить", true) { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cancel = new FlatButton("Отмена") { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var reset = new FlatButton("Как было сначала") { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            bar.Controls.AddRange(new Control[] { ok, cancel, reset });
            bar.Layout += (s, e) =>
            {
                ok.Location = new Point(bar.Width - Theme.S(20) - ok.Width, (bar.Height - ok.Height) / 2);
                cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, ok.Top);
                reset.Location = new Point(Theme.S(20), ok.Top);
            };
            ok.Click += (s, e) => { Take(); DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            reset.Click += (s, e) => undo.Batch(() => { var d = new JunkRules(); age.Value = StepOf(d.TempHours); logs.Value = d.LogDays; r.Never = new List<string>(); r.Extra = new List<string>(); ShowLists(); });

            var page = new Page { Dock = DockStyle.Fill };
            page.Section("Возраст файлов");
            // the caption counts: «24 ч», «2 дн» (Age is for a sentence: «старше суток»)
            age = new Slider(0, JunkRules.AgeSteps.Length - 1, StepOf(r.TempHours), "") { Width = Theme.S(260), WarpToKnob = true, ResetTo = StepOf(new JunkRules().TempHours), Caption = i => { int v = JunkRules.AgeSteps[i]; return v < 48 ? v + " " + L.T("ч") : v / 24 + " " + L.T("дн"); } };
            page.Row("Временные файлы", "Папки Temp и свои папки ниже: удаляются файлы, не менявшиеся дольше этого", age);
            logs = new Slider(1, 90, r.LogDays, L.T("дн")) { Width = Theme.S(260), WarpToKnob = true, ResetTo = new JunkRules().LogDays };
            page.Row("Отчёты об ошибках и логи", "Отчёты Windows об ошибках и журналы установки старше этого", logs);

            page.Section("Свои списки");
            var editNever = new FlatButton("Изменить…"); var editExtra = new FlatButton("Изменить…");
            neverRow = page.Row("Никогда не трогать", "", editNever);
            extraRow = page.Row("Свои папки для очистки", "", editExtra);
            editNever.Click += (s, e) =>
            {
                var v = ListDialog.Edit(this, "Никогда не трогать", "По одному на строку. Путь (C:\\Work\\cache, %TEMP%\\build) — ничего в этой папке. Маска (*.iso, *.blend) — файлы и папки с таким именем где угодно. Слово (Steam) — всё, где оно есть в пути.", r.Never);
                if (v != null) { undo.Before(); r.Never = v; ShowLists(); undo.Settled(); }
            };
            editExtra.Click += (s, e) =>
            {
                var v = ListDialog.Edit(this, "Свои папки для очистки", "Папки, которые тоже можно чистить, — по одной на строку (D:\\Downloads\\tmp, %LOCALAPPDATA%\\SomeApp\\cache). В них удаляются файлы старше возраста временных файлов, сама папка остаётся. Каждая папка — своя строка с переключателем.", r.Extra);
                if (v == null) return;
                var bad = v.Select(x => new { x, why = JunkRules.Refuse(x) }).Where(t => t.why != null).ToList();
                undo.Before(); r.Extra = v.Where(x => JunkRules.Refuse(x) == null).ToList();
                ShowLists(); undo.Settled();
                if (bad.Count > 0) Msg.Show(this, L.T("Не добавлены:") + "\n" + string.Join("\n", bad.Select(t => t.x + " — " + t.why)), "Red Tune", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            ShowLists();
            undo = new DialogUndo(Snap, LoadSnap);
            undo.Slider(age); undo.Slider(logs);

            page.Section("Всегда защищено — не меняется");
            foreach (var a in JunkRules.Always) page.Row(a, null);

            Controls.Add(page); Controls.Add(bar);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); else if (undo.Key(e, this)) e.Handled = e.SuppressKeyPress = true; };
        }

        static int StepOf(int hours)
        {
            int best = 0;
            for (int i = 0; i < JunkRules.AgeSteps.Length; i++) if (Math.Abs(JunkRules.AgeSteps[i] - hours) < Math.Abs(JunkRules.AgeSteps[best] - hours)) best = i;
            return best;
        }

        void ShowLists()
        {
            Func<List<string>, string, string> sum = (l, none) => l.Count == 0 ? L.T(none) : string.Join(" · ", l.Take(4)) + (l.Count > 4 ? L.T(" · и ещё ") + (l.Count - 4) : "");
            neverRow.SetSub(sum(r.Never, "Пусто — очищается всё, что подходит по возрасту"));
            extraRow.SetSub(sum(r.Extra, "Пусто — только папки из списка на странице"));
        }

        void Take()
        {
            r.TempHours = JunkRules.AgeSteps[Math.Max(0, Math.Min(JunkRules.AgeSteps.Length - 1, age.Value))];
            r.LogDays = logs.Value;
            r.Save();
        }

        // the check: the dialog drawn off screen into a png (scrolled by RT_SCROLL units)
        public static void Render(string file)
        {
            using (var d = new JunkRulesDialog(JunkRules.Current) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) })
            {
                d.Show(); Application.DoEvents();
                int sc; if (int.TryParse(Environment.GetEnvironmentVariable("RT_SCROLL") ?? "", out sc)) foreach (Control c in d.Controls) if (c is Page) { ((Page)c).AutoScrollPosition = new Point(0, Theme.S(sc)); }
                Application.DoEvents();
                using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(Point.Empty, d.Size)); bmp.Save(file); }
                d.Close();
            }
        }

        // --rules-undo-check: a slider moved and a list changed, then Ctrl+Z / Ctrl+Y (nothing is saved)
        public static string Check()
        {
            var o = new StringBuilder();
            using (var d = new JunkRulesDialog(new JunkRules()))
            {
                int v0 = d.age.Value;
                d.undo.Before(d.age); d.age.Value = v0 + 2; d.undo.Settled();
                d.undo.Before(); d.r.Never = new List<string> { "*.iso" }; d.undo.Settled();
                d.undo.Key(new KeyEventArgs(Keys.Control | Keys.Z), d);
                o.AppendLine((d.r.Never.Count == 0 && d.age.Value == v0 + 2 ? "ok" : "FAIL") + " junk: Ctrl+Z takes the list back, the slider stays");
                d.undo.Key(new KeyEventArgs(Keys.Control | Keys.Z), d);
                o.AppendLine((d.age.Value == v0 ? "ok" : "FAIL") + " junk: second Ctrl+Z takes the slider back (" + d.age.Value + ")");
                d.undo.Key(new KeyEventArgs(Keys.Control | Keys.Y), d); d.undo.Key(new KeyEventArgs(Keys.Control | Keys.Y), d);
                o.AppendLine((d.age.Value == v0 + 2 && d.r.Never.Count == 1 ? "ok" : "FAIL") + " junk: Ctrl+Y twice brings both again");
                d.undo.Before(d.logs); d.logs.Value = 30; d.undo.Before(d.logs); d.logs.Value = 31; d.undo.Settled();
                d.undo.Key(new KeyEventArgs(Keys.Control | Keys.Z), d);
                o.AppendLine((d.logs.Value == new JunkRules().LogDays ? "ok" : "FAIL") + " junk: quick steps on one slider are one step");
                o.AppendLine((d.age.WarpToKnob && d.logs.WarpToKnob && d.age.ResetTo.HasValue && d.logs.ResetTo == 7 ? "ok" : "FAIL") + " junk: sliders warp to the knob, middle button resets");
            }
            return o.ToString();
        }

        public static bool Edit(IWin32Window owner)
        {
            using (var d = new JunkRulesDialog(JunkRules.Current)) return d.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
