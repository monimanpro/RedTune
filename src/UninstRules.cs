using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RedTune
{
    // 22.11 (the user: let the rules of the search be seen and changed in «Удаление программ»). Three parts:
    //   - what is looked for: per kind of leftover «нет» / «искать» / «с галочкой» (the tick after a removal; the
    //     leftovers of programs removed long ago are never ticked);
    //   - numbers and lists: the age and size of a folder of a program removed long ago, «никогда не трогать» (names or
    //     paths), «искать ещё здесь» (folders where programs are installed too);
    //   - what is always protected: shown, not changeable (Windows, device drivers, Program Files as a whole, the profile).
    // Kept in %APPDATA%\RedTune\uninst-rules.txt, one «key=value» a line.
    class UninstRules
    {
        public static readonly string[] Kinds = { "folders", "registry", "links", "startup", "firewall", "accounts", "packages", "services", "menu", "path" };

        public static string KindName(string k)
        {
            switch (k)
            {
                case "folders": return "Папки"; case "registry": return "Реестр"; case "links": return "Ярлыки"; case "startup": return "Автозагрузка и задачи";
                case "firewall": return "Правила брандмауэра"; case "accounts": return "Учётные записи и группы Windows"; case "packages": return "Пакеты Microsoft Store";
                case "services": return "Службы и драйверы"; case "menu": return "Контекстное меню"; case "path": return "Переменная PATH"; default: return k;
            }
        }

        public static string KindHow(string k)
        {
            switch (k)
            {
                case "folders": return "Папки с её именем в Program Files, AppData, ProgramData, её папка установки, ~\\.имя";
                case "registry": return "Ключи SOFTWARE\\Имя и SOFTWARE\\Издатель\\Имя, забытая запись в списке программ";
                case "links": return "Ярлыки в «Пуске» и на рабочем столе, ведущие в её папку или на файл, которого нет";
                case "startup": return "Автозагрузка и задачи планировщика, запускающие что-то из её папки";
                case "firewall": return "Правила с её именем или путём в её папку; от удалённых — правила, чьей программы уже нет";
                case "accounts": return "Учётные записи и группы, названные по ней или созданные для её правил брандмауэра";
                case "packages": return "Пакеты, поставленные вместе с ней, и её пакет у других учётных записей";
                case "services": return "Службы и драйверы, чей файл лежит в её папке или уже удалён";
                case "menu": return "Пункты меню по правой кнопке, ведущие в её папку или на файл, которого нет";
                case "path": return "Записи, ведущие в её папку или в папку, которой нет";
                default: return "";
            }
        }

        // the kind of a leftover, as the rules name it
        public static string KindOf(Leftover l)
        {
            switch (l.Kind)
            {
                case "folder": return "folders"; case "file": return "links"; case "reg": return "registry"; case "menu": return "menu"; case "startup": return "startup";
                case "fw": return "firewall"; case "user": case "group": return "accounts"; case "appx": case "appxall": return "packages";
                case "svc": return "services"; case "path": return "path"; default: return "folders";
            }
        }

        public readonly Dictionary<string, int> Mode = Kinds.ToDictionary(k => k, k => 2); // 0 no, 1 look, 2 look and tick
        public int OrphanDays = 60, OrphanMinMb = 1;
        public List<string> Never = new List<string>(), Extra = new List<string>();

        static string FilePath { get { return Path.Combine(Store.DataDir, "uninst-rules.txt"); } }

        static UninstRules current;
        public static UninstRules Current { get { if (current == null) current = Load(); return current; } }

        public UninstRules Copy()
        {
            var c = new UninstRules { OrphanDays = OrphanDays, OrphanMinMb = OrphanMinMb, Never = new List<string>(Never), Extra = new List<string>(Extra) };
            foreach (var kv in Mode) c.Mode[kv.Key] = kv.Value;
            return c;
        }

        static UninstRules Load()
        {
            var r = new UninstRules();
            try
            {
                if (!File.Exists(FilePath)) return r;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim(); int n;
                    if (k == "days" && int.TryParse(v, out n)) r.OrphanDays = Math.Max(1, Math.Min(3650, n));
                    else if (k == "mb" && int.TryParse(v, out n)) r.OrphanMinMb = Math.Max(0, Math.Min(100000, n));
                    else if (k == "never" && v.Length > 0) r.Never.Add(v);
                    else if (k == "extra" && v.Length > 0) r.Extra.Add(v);
                    else if (r.Mode.ContainsKey(k) && int.TryParse(v, out n)) r.Mode[k] = Math.Max(0, Math.Min(2, n));
                }
            }
            catch (Exception ex) { Store.Log("правила удаления: " + ex.Message); }
            return r;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var k in Kinds) sb.AppendLine(k + "=" + Mode[k]);
            sb.AppendLine("days=" + OrphanDays); sb.AppendLine("mb=" + OrphanMinMb);
            foreach (var n in Never) sb.AppendLine("never=" + n);
            foreach (var e in Extra) sb.AppendLine("extra=" + e);
            Directory.CreateDirectory(Store.DataDir);
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
            current = this;
            Store.Log("правила удаления: сохранены (" + string.Join(", ", Kinds.Select(k => k + " " + Mode[k])) + ", " + OrphanDays + " дн, " + OrphanMinMb + " МБ, никогда " + Never.Count + ", ещё " + Extra.Count + ")");
        }

        // «никогда не трогать»: a path takes what lies in it (or is it), a name takes what mentions it
        public bool Blocked(Leftover l)
        {
            foreach (var n in Never)
            {
                var e = n.Trim().TrimEnd('\\'); if (e.Length < 2) continue;
                bool path = e.Length > 2 && e[1] == ':';
                if (path)
                {
                    foreach (var p in (l.Path ?? "").Split('\n'))
                    {
                        var x = p.Contains("|") ? p.Substring(p.IndexOf('|') + 1) : p;
                        if (x.Equals(e, StringComparison.OrdinalIgnoreCase) || x.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if ((l.What ?? "").IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                else if ((l.What ?? "").IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0 || (l.Path ?? "").IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // what the search found, by the rules: kinds switched off and the «никогда» ones go; after a removal a kind on
        // «искать» comes without its tick
        public List<Leftover> Apply(List<Leftover> found, bool afterRemoval)
        {
            var list = new List<Leftover>();
            foreach (var l in found)
            {
                int m; if (!Mode.TryGetValue(KindOf(l), out m)) m = 2;
                if (m == 0 || Blocked(l)) continue;
                if (afterRemoval && m == 1) l.Checked = false;
                list.Add(l);
            }
            return list;
        }

        // the protections, as the search keeps them (Uninst.Protected, SvcSafe and the rest): shown in the dialog
        public static readonly string[] Always =
        {
            "Папка Windows и всё в ней, что принадлежит Windows (в том числе драйверы, которые программы кладут туда)",
            "Program Files, Program Files (x86), ProgramData, AppData целиком и твоя папка пользователя — только папки отдельных программ внутри",
            "Службы Windows, работающие через svchost",
            "Драйверы, стоящие в цепочке какого-нибудь устройства (без них устройство перестаёт работать)",
            "Твоя учётная запись и встроенные учётные записи Windows",
            "Сертификаты — не предлагаются совсем: лишний удалённый ломает HTTPS",
            "Всё, что найдено только по папке издателя: там могут жить и другие его программы",
            "Red Tune и Claude, их папки и данные",
        };
    }

    // the dialog of the rules (a page of rows inside a window, the buttons under it)
    class UninstRulesDialog : DarkForm
    {
        readonly UninstRules r;
        readonly Dictionary<string, Segmented> modes = new Dictionary<string, Segmented>();
        Slider days, mb; Row neverRow, extraRow;

        UninstRulesDialog(UninstRules rules)
        {
            r = rules.Copy();
            Text = L.T("Правила поиска хвостов"); FormBorderStyle = FormBorderStyle.Sizable;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(900), wa.Width - 60), Math.Min(Theme.S(720), wa.Height - 60));
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
            reset.Click += (s, e) => { var d = new UninstRules(); foreach (var k in UninstRules.Kinds) modes[k].Selected = d.Mode[k]; days.Value = d.OrphanDays; mb.Value = d.OrphanMinMb; r.Never.Clear(); r.Extra.Clear(); ShowLists(); };

            var page = new Page { Dock = DockStyle.Fill };
            page.Section("Что искать после удаления программы");
            foreach (var k in UninstRules.Kinds)
            {
                var seg = new Segmented("Нет", "Искать", "С галочкой") { Selected = r.Mode[k] };
                modes[k] = seg;
                page.Row(UninstRules.KindName(k), UninstRules.KindHow(k), seg);
            }
            page.Row("Галочки", "«С галочкой» — найденное после удаления отмечено сразу, «Искать» — без галочки. У давно удалённых галочек нет");

            page.Section("Хвосты давно удалённых программ");
            days = new Slider(7, 365, r.OrphanDays, L.T("дн")) { Width = Theme.S(260) };
            page.Row("Папка не менялась", "Папка без своей программы (AppData, ProgramData, Program Files) показывается, если её не трогали столько дней; пустые — всегда", days);
            mb = new Slider(0, 500, r.OrphanMinMb, L.T("МБ")) { Width = Theme.S(260) };
            page.Row("Не меньше", "Папки меньше этого размера не показываются", mb);

            page.Section("Свои списки");
            var editNever = new FlatButton("Изменить…"); var editExtra = new FlatButton("Изменить…");
            neverRow = page.Row("Никогда не трогать", "", editNever);
            extraRow = page.Row("Искать ещё здесь", "", editExtra);
            editNever.Click += (s, e) =>
            {
                var v = ListDialog.Edit(this, "Никогда не трогать", "По одному на строку. Путь (C:\\Games\\…) — не трогать ничего в этой папке. Имя (Steam) — не трогать ничего, где оно встречается.", r.Never);
                if (v != null) { r.Never = v; ShowLists(); }
            };
            editExtra.Click += (s, e) =>
            {
                var v = ListDialog.Edit(this, "Искать ещё здесь", "Папки, куда ты тоже ставишь программы (D:\\Games, D:\\Programs) — по одной на строку. В них ищутся папки удалённых программ так же, как в Program Files.", r.Extra);
                if (v != null) { r.Extra = v.Where(x => x.Length > 3 && x[1] == ':').ToList(); ShowLists(); }
            };
            ShowLists();

            page.Section("Всегда защищено — не меняется");
            foreach (var a in UninstRules.Always) page.Row(a, null);

            Controls.Add(page); Controls.Add(bar);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        void ShowLists()
        {
            Func<List<string>, string, string> sum = (l, none) => l.Count == 0 ? none : string.Join(" · ", l.Take(4)) + (l.Count > 4 ? " · и ещё " + (l.Count - 4) : "");
            neverRow.SetSub(sum(r.Never, "Пусто — Red Tune предлагает всё, что нашёл по правилам выше"));
            extraRow.SetSub(sum(r.Extra, "Пусто — только Program Files, AppData и ProgramData"));
        }

        void Take()
        {
            foreach (var k in UninstRules.Kinds) r.Mode[k] = Math.Max(0, modes[k].Selected);
            r.OrphanDays = days.Value; r.OrphanMinMb = mb.Value;
            r.Save();
        }

        // the check: the dialog drawn off screen into a png (scrolled by RT_SCROLL units)
        public static void Render(string file)
        {
            using (var d = new UninstRulesDialog(UninstRules.Current) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) })
            {
                d.Show(); Application.DoEvents();
                int sc; if (int.TryParse(Environment.GetEnvironmentVariable("RT_SCROLL") ?? "", out sc)) foreach (Control c in d.Controls) if (c is Page) { ((Page)c).AutoScrollPosition = new Point(0, Theme.S(sc)); }
                Application.DoEvents();
                using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(Point.Empty, d.Size)); bmp.Save(file); }
                d.Close();
            }
        }

        public static bool Edit(IWin32Window owner)
        {
            using (var d = new UninstRulesDialog(UninstRules.Current)) return d.ShowDialog(owner) == DialogResult.OK;
        }
    }

    // a list to edit, one entry a line; null when cancelled
    class ListDialog : DarkForm
    {
        readonly TextBox box = new TextBox();

        ListDialog(string title, string text, IEnumerable<string> items)
        {
            Text = L.T(title); FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(Theme.S(560), Theme.S(380));
            var l = new Label { Text = L.T(text), ForeColor = Theme.Text2, AutoSize = true, MaximumSize = new Size(ClientSize.Width - Theme.S(40), 0), Location = new Point(Theme.S(20), Theme.S(16)) };
            Controls.Add(l);
            box.Multiline = true; box.ScrollBars = ScrollBars.Vertical; box.AcceptsReturn = true; box.WordWrap = false;
            box.BorderStyle = BorderStyle.FixedSingle; box.BackColor = Theme.Raised; box.ForeColor = Theme.Text; box.Font = Theme.Body;
            box.Text = string.Join("\r\n", items);
            box.Location = new Point(Theme.S(20), l.Bottom + Theme.S(10));
            box.Size = new Size(ClientSize.Width - Theme.S(40), ClientSize.Height - box.Top - Theme.S(70));
            box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            var ok = new FlatButton("Готово", true) { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cancel = new FlatButton("Отмена") { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            ok.Location = new Point(ClientSize.Width - Theme.S(20) - ok.Width, ClientSize.Height - Theme.S(18) - ok.Height);
            cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, ok.Top);
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { box, ok, cancel });
            Shown += (s, e) => box.Focus();
        }

        public static List<string> Edit(IWin32Window owner, string title, string text, IEnumerable<string> items)
        {
            using (var d = new ListDialog(title, text, items))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                return d.box.Lines.Select(x => x.Trim().Trim('"')).Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }
}
