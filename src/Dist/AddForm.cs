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
    // Pick a running program (or an exe), then the pattern, name and group
    class AddForm : Form
    {
        class Candidate { public string Path, Name, Process; public Image Icon; }

        public Rule Result;
        public string ResultIconPath;
        readonly ListBox list = new ListBox();
        readonly SearchBox nameBox = new SearchBox("Название");
        readonly SearchBox patternBox = new SearchBox(@"\Папка\ или имя процесса");
        readonly FlatButton[] groupBtns;
        readonly FlatButton browseBtn = new FlatButton("Выбрать exe…");
        readonly FlatButton okBtn = new FlatButton("Добавить", true, "+");
        readonly FlatButton cancelBtn = new FlatButton("Отмена");
        readonly Label hint = new Label();
        Group group = Group.CCD1;
        string pickedPath;

        static readonly HashSet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program Files", "Program Files (x86)", "WindowsApps", "Users", "AppData", "Local", "Roaming", "Programs", "ProgramData", "LocalLow",
        };

        public AddForm(Engine engine)
        {
            Text = "Добавить программу";
            BackColor = Theme.Window; ForeColor = Theme.Text; Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(560), Theme.S(560));
            int pad = Theme.S(18), w = ClientSize.Width - 2 * pad;

            var l1 = MakeLabel("Запущенные программы", pad, Theme.S(14));
            list.Bounds = new Rectangle(pad, Theme.S(40), w, Theme.S(250));
            list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Card; list.ForeColor = Theme.Text;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = Theme.S(40); list.IntegralHeight = false;
            list.DrawItem += DrawCandidate;
            list.SelectedIndexChanged += (s, e) => { var c = list.SelectedItem as Candidate; if (c != null) Pick(c.Path, c.Name); };
            list.HandleCreated += (s, e) => Native.DarkScrollBars(list.Handle);

            browseBtn.BackColor = Theme.Window;
            browseBtn.Location = new Point(pad, Theme.S(300));
            browseBtn.Click += (s, e) => Browse();

            MakeLabel("Название", pad, Theme.S(346));
            nameBox.Bounds = new Rectangle(pad + Theme.S(110), Theme.S(342), w - Theme.S(110), Theme.S(32));
            MakeLabel("Шаблон", pad, Theme.S(388));
            patternBox.Bounds = new Rectangle(pad + Theme.S(110), Theme.S(384), w - Theme.S(110), Theme.S(32));
            hint.Text = @"\Папка\ — все exe из папки. Имя без \ — один процесс.";
            hint.ForeColor = Theme.Muted; hint.Font = Theme.Small; hint.AutoSize = false;
            hint.Bounds = new Rectangle(pad + Theme.S(110), Theme.S(420), w - Theme.S(110), Theme.S(20));

            MakeLabel("Группа", pad, Theme.S(456));
            groupBtns = new[] { Group.CCD1, Group.CCD2, Group.ALL, Group.SKIP }.Select(g => new FlatButton(g.ToString())).ToArray();
            int gx = pad + Theme.S(110);
            for (int i = 0; i < groupBtns.Length; i++)
            {
                var g = (Group)i;
                groupBtns[i].BackColor = Theme.Window;
                groupBtns[i].Location = new Point(gx, Theme.S(450));
                groupBtns[i].Click += (s, e) => SetGroup(g);
                gx += groupBtns[i].Width + Theme.S(8);
            }
            var range = new Label { Text = "CCD1 — " + engine.RangeText(Group.CCD1) + ", CCD2 — " + engine.RangeText(Group.CCD2) + ", ALL — все, SKIP — не трогать", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true };
            range.Location = new Point(pad + Theme.S(110), Theme.S(488));
            Controls.Add(range);

            okBtn.BackColor = cancelBtn.BackColor = Theme.Window;
            okBtn.Location = new Point(ClientSize.Width - pad - okBtn.Width, ClientSize.Height - pad - okBtn.Height);
            cancelBtn.Location = new Point(okBtn.Left - Theme.S(8) - cancelBtn.Width, okBtn.Top);
            okBtn.Click += (s, e) => Finish();
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { list, browseBtn, nameBox, patternBox, hint, okBtn, cancelBtn });
            Controls.AddRange(groupBtns);
            SetGroup(Group.CCD1);
            foreach (var c in Candidates()) list.Items.Add(c);
        }

        Label MakeLabel(string text, int x, int y)
        {
            var l = new Label { Text = text, ForeColor = Theme.Text2, Font = Theme.Body, AutoSize = true, Location = new Point(x, y) };
            Controls.Add(l);
            return l;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }

        void SetGroup(Group g)
        {
            group = g;
            for (int i = 0; i < groupBtns.Length; i++) { groupBtns[i].Primary = (Group)i == g; groupBtns[i].Invalidate(); }
        }

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
            using (var d = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите exe программы" })
                if (d.ShowDialog(this) == DialogResult.OK) Pick(d.FileName, Describe(d.FileName, Path.GetFileNameWithoutExtension(d.FileName)));
        }

        void Pick(string path, string name)
        {
            pickedPath = path;
            nameBox.Box.Text = name;
            patternBox.Box.Text = SuggestPattern(path);
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

        void Finish()
        {
            var pattern = patternBox.Box.Text.Trim();
            if (pattern.Length == 0) { hint.Text = "Выберите программу в списке или укажите шаблон."; hint.ForeColor = Theme.Warning; return; }
            var name = nameBox.Box.Text.Trim();
            if (name.Length == 0) name = Rule.DefaultName(pattern);
            Result = new Rule { Group = group, Pattern = pattern, Name = name };
            ResultIconPath = pickedPath;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
