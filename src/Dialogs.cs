using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    class DarkForm : Form
    {
        public DarkForm()
        {
            BackColor = Theme.Window; ForeColor = Theme.Text; Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
            Icon = Program.AppIcon;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.DarkTitleBar(Handle); }
    }

    // one-line text question; null when cancelled or empty
    class InputDialog : DarkForm
    {
        readonly TextBox box = new TextBox();

        InputDialog(string title, string label, string value)
        {
            Text = L.T(title); FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(Theme.S(380), Theme.S(150));
            var l = new Label { Text = L.T(label), ForeColor = Theme.Text2, AutoSize = true, Location = new Point(Theme.S(18), Theme.S(16)) };
            box.Text = value; box.BorderStyle = BorderStyle.FixedSingle; box.BackColor = Theme.Raised; box.ForeColor = Theme.Text; box.Font = Theme.Body;
            box.Bounds = new Rectangle(Theme.S(18), Theme.S(42), ClientSize.Width - Theme.S(36), box.PreferredHeight);
            var ok = new FlatButton("Готово", true) { BackColor = Theme.Window };
            var cancel = new FlatButton("Отмена") { BackColor = Theme.Window };
            ok.Location = new Point(ClientSize.Width - Theme.S(18) - ok.Width, ClientSize.Height - Theme.S(16) - ok.Height);
            cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, ok.Top);
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); } else if (e.KeyCode == Keys.Escape) Close(); };
            Controls.AddRange(new Control[] { l, box, ok, cancel });
            Shown += (s, e) => { box.Focus(); box.SelectAll(); };
        }

        public static string Ask(IWin32Window owner, string title, string label, string value)
        {
            using (var d = new InputDialog(title, label, value))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                var t = d.box.Text.Trim().Replace("|", "/");
                return t.Length > 0 ? t : null;
            }
        }
    }

    // a yes/no question in the app's look; true = the first (primary) button
    class ConfirmDialog : DarkForm
    {
        ConfirmDialog(string title, string text, string yes, string no)
        {
            Text = L.T(title); FormBorderStyle = FormBorderStyle.FixedDialog;
            int w = Theme.S(440);
            var l = new Label { Text = L.T(text), ForeColor = Theme.Text, AutoSize = true, MaximumSize = new Size(w - Theme.S(40), 0), Location = new Point(Theme.S(20), Theme.S(20)) };
            Controls.Add(l);
            int textH = l.PreferredHeight;
            ClientSize = new Size(w, Theme.S(20) + textH + Theme.S(76));
            var ok = new FlatButton(yes, true) { BackColor = Theme.Window };
            var cancel = new FlatButton(no) { BackColor = Theme.Window };
            ok.Location = new Point(ClientSize.Width - Theme.S(20) - ok.Width, ClientSize.Height - Theme.S(18) - ok.Height);
            cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, ok.Top);
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.No; Close(); }; // No = this button; Esc or the cross = Cancel
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Controls.AddRange(new Control[] { ok, cancel });
        }

        public static bool Ask(IWin32Window owner, string title, string text, string yes, string no)
        {
            using (var d = new ConfirmDialog(title, text, yes, no)) return d.ShowDialog(owner) == DialogResult.OK;
        }

        // true only when the second (not highlighted) button was clicked; Esc, the cross and the first button give false
        public static bool AskSecond(IWin32Window owner, string title, string text, string first, string second)
        {
            using (var d = new ConfirmDialog(title, text, first, second)) return d.ShowDialog(owner) == DialogResult.No;
        }
    }

    // a list with checkboxes (leftovers of a removed program): returns the ticked ones, null when cancelled
    class PickDialog : DarkForm
    {
        readonly CheckedListBox list;

        PickDialog(string title, string text, IList<Leftover> items, string yes)
        {
            Text = L.T(title); FormBorderStyle = FormBorderStyle.Sizable; MinimizeBox = false;
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(820), wa.Width - 80), Math.Min(Theme.S(560), wa.Height - 80));
            var l = new Label { Text = L.T(text), ForeColor = Theme.Text, AutoSize = true, MaximumSize = new Size(ClientSize.Width - Theme.S(40), 0), Location = new Point(Theme.S(20), Theme.S(16)) };
            Controls.Add(l);
            list = new CheckedListBox
            {
                BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true,
                Location = new Point(Theme.S(20), l.Bottom + Theme.S(12)), Font = Theme.Body,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            list.Size = new Size(ClientSize.Width - Theme.S(40), ClientSize.Height - list.Top - Theme.S(72));
            foreach (var it in items) list.Items.Add(it, it.Checked);
            Controls.Add(list);
            var ok = new FlatButton(yes, true) { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cancel = new FlatButton("Отмена") { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var all = new FlatButton("Отметить всё") { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var none = new FlatButton("Снять всё") { BackColor = Theme.Window, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            ok.Location = new Point(ClientSize.Width - Theme.S(20) - ok.Width, ClientSize.Height - Theme.S(18) - ok.Height);
            cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, ok.Top);
            all.Location = new Point(Theme.S(20), ok.Top); none.Location = new Point(all.Right + Theme.S(8), ok.Top);
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            all.Click += (s, e) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
            none.Click += (s, e) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Controls.AddRange(new Control[] { ok, cancel, all, none });
        }

        public static List<Leftover> Pick(IWin32Window owner, string title, string text, IList<Leftover> items, string yes)
        {
            using (var d = new PickDialog(title, text, items, yes))
                return d.ShowDialog(owner) == DialogResult.OK ? d.list.CheckedItems.Cast<Leftover>().ToList() : null;
        }
    }

    // past applies, newest first; "В редактор" puts a set into the grid without applying it
    class HistoryForm : DarkForm
    {
        public HistoryEntry Picked;

        public HistoryForm()
        {
            Text = L.T("История изменений кривой");
            FormBorderStyle = FormBorderStyle.Sizable; MinimizeBox = false;
            ClientSize = new Size(Theme.S(720), Theme.S(560));
            var page = new Page();
            var list = Store.LoadHistory(200);
            if (list.Count == 0) page.Row("Пока пусто", "Здесь появится каждое применение кривой");
            foreach (var h in list)
            {
                var btn = new FlatButton("В редактор");
                var hh = h;
                btn.Click += (s, e) => { Picked = hh; DialogResult = DialogResult.OK; Close(); };
                bool ok = h.Result == "ok";
                page.Row(h.Time.ToString("dd.MM HH:mm:ss") + " · " + h.Source + " · " + h.Preset,
                    Hw.Describe(h.Values) + (ok ? "" : "   · " + h.Result), btn);
            }
            Controls.Add(page);
        }
    }
}

namespace RedTune
{
    static class Msg
    {
        public static System.Windows.Forms.DialogResult Show(System.Windows.Forms.IWin32Window owner, string text, string title,
            System.Windows.Forms.MessageBoxButtons buttons = System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon icon = System.Windows.Forms.MessageBoxIcon.None)
        {
            return System.Windows.Forms.MessageBox.Show(owner, L.T(text), title, buttons, icon);
        }
    }
}
