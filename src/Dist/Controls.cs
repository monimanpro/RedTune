using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace RedTune.Dist
{
    abstract class Painted : Control
    {
        protected Painted()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
        }
    }

    class Toggle : Painted
    {
        bool on = true;
        public event EventHandler CheckedChanged;

        readonly ToggleAnim anim;

        public Toggle() { Size = Theme.ToggleSize; Cursor = Cursors.Hand; anim = new ToggleAnim(this, on); }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; anim.Go(on); Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Theme.DrawToggle(e.Graphics, new RectangleF(0, 0, Width, Height), anim.Pos);
        }
    }

    class FlatButton : Painted
    {
        public bool Primary;
        public string Glyph; // "+" or "save"
        bool hover;

        public FlatButton(string text, bool primary = false, string glyph = null)
        {
            Primary = primary; Glyph = glyph; Cursor = Cursors.Hand; Height = Theme.S(32); Text = text;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Width = TextRenderer.MeasureText(Text, Font).Width + Theme.S(Glyph != null ? 44 : 28);
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (Primary) Theme.Fill(g, r, Theme.S(6), hover ? Theme.AccentHover : Theme.Accent);
            else
            {
                Theme.Fill(g, r, Theme.S(6), hover ? Theme.Raised : BackColor);
                Theme.Stroke(g, r, Theme.S(6), Theme.BorderStrong);
            }
            var fg = Primary ? Color.White : Theme.Text;
            int tx = Theme.S(14);
            if (Glyph != null)
            {
                float cx = Theme.S(19), cy = Height / 2f, k = Theme.S(5);
                using (var pen = new Pen(fg, Theme.S(1.6f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    if (Glyph == "+") { g.DrawLine(pen, cx - k, cy, cx + k, cy); g.DrawLine(pen, cx, cy - k, cx, cy + k); }
                    else
                    {
                        g.DrawLines(pen, new[] { new PointF(cx - k, cy), new PointF(cx - k * 0.2f, cy + k * 0.8f), new PointF(cx + k, cy - k * 0.8f) });
                    }
                }
                tx = Theme.S(30);
            }
            Theme.Text1(g, Text, Font, new Rectangle(tx, 0, Width - tx - Theme.S(8), Height), fg);
        }
    }

    class CheckOption : Painted
    {
        bool on;
        public event EventHandler CheckedChanged;

        public CheckOption(string text)
        {
            Text = text; Cursor = Cursors.Hand;
            Size = new Size(TextRenderer.MeasureText(text, Theme.Body).Width + Theme.S(30), Theme.S(28));
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Theme.S(16), y = (Height - k) / 2f;
            var box = new RectangleF(1, y, k, k);
            if (on)
            {
                Theme.Fill(g, box, Theme.S(4), Theme.Accent);
                using (var pen = new Pen(Color.White, Theme.S(1.8f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(box.X + k * 0.24f, box.Y + k * 0.52f), new PointF(box.X + k * 0.43f, box.Y + k * 0.72f), new PointF(box.X + k * 0.78f, box.Y + k * 0.3f) });
                }
            }
            else Theme.Stroke(g, box, Theme.S(4), Theme.BorderStrong);
            Theme.Text1(g, Text, Font, new Rectangle(Theme.S(24), 0, Width - Theme.S(24), Height), Theme.Text2);
        }
    }

    // One program: grip, icon, name with state, path pattern, group pill (menu), on/off switch. The whole row can be dragged.
    class RuleRow : Painted
    {
        public readonly Rule Rule;
        public RuleStatus Status;
        public Image Icon;
        public bool IsLast;
        public bool Shown = true; // search filter (Visible is false while the window is hidden)
        public string Folder; // where the program lives (null = not found yet): the folder button shows then
        public bool FolderGone; // the folder was known and has disappeared (deleted, or moved out of reach)
        readonly DistPage owner;
        Rectangle gripR, iconR, pillR, toggleR, folderR;
        ToggleAnim toggleAnim; // the rule's switch slides (14.11)
        Point downAt;
        bool armed, folderHot;
        static Font glyphFont;
        static Font GlyphFont
        {
            get
            {
                if (glyphFont == null)
                {
                    var family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                    glyphFont = new Font(family, 11f * Theme.Scale);
                }
                return glyphFont;
            }
        }

        public RuleRow(DistPage owner, Rule rule)
        {
            this.owner = owner; Rule = rule; Height = Theme.S(50); BackColor = Theme.Card; AllowDrop = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = Height;
            gripR = new Rectangle(0, 0, Theme.S(16), h);
            iconR = new Rectangle(Theme.S(18), (h - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
            toggleR = new Rectangle(Width - Theme.ToggleSize.Width, (h - Theme.ToggleSize.Height) / 2, Theme.ToggleSize.Width, Theme.ToggleSize.Height);
            pillR = new Rectangle(toggleR.Left - Theme.S(12) - Theme.S(96), (h - Theme.S(24)) / 2, Theme.S(96), Theme.S(24));
            folderR = new Rectangle(pillR.Left - Theme.S(8) - Theme.S(28), (h - Theme.S(28)) / 2, Theme.S(28), Theme.S(28));
        }

        bool Running { get { return Status != null && Status.SamplePath != null; } }
        bool HasFolder { get { return Folder != null || Running || FolderGone; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var b = new SolidBrush(Theme.Muted))
                for (int c = 0; c < 2; c++)
                    for (int r = 0; r < 3; r++)
                    {
                        float d = Theme.S(3);
                        g.FillEllipse(b, gripR.X + Theme.S(3) + c * Theme.S(5), Height / 2f - Theme.S(7) + r * Theme.S(5.5f), d, d);
                    }

            Theme.Fill(g, iconR, Theme.S(7), Theme.Raised);
            if (Icon != null)
            {
                int k = Theme.S(20);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Icon, iconR.X + (iconR.Width - k) / 2, iconR.Y + (iconR.Height - k) / 2, k, k);
            }
            else
            {
                var letter = Rule.Name.Length > 0 ? Rule.Name.Substring(0, 1).ToUpperInvariant() : "?";
                TextRenderer.DrawText(g, letter, Theme.Bold, iconR, DistColors.Fg(Rule.Group), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tx = iconR.Right + Theme.S(12), tw = folderR.Left - Theme.S(10) - tx;

            if (HasFolder)
            {
                if (folderHot) Theme.Fill(g, folderR, Theme.S(6), Theme.Raised);
                bool gone = FolderGone && !Running;
                TextRenderer.DrawText(g, "", GlyphFont, folderR, gone ? Theme.Warning : folderHot ? Theme.Text : Theme.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            int line1 = Height / 2 - Theme.S(10), line2 = Height / 2 + Theme.S(9);
            var nameColor = Rule.Enabled ? Theme.Text : Theme.Muted;
            var nameSize = TextRenderer.MeasureText(g, Rule.Name, Theme.Body, new Size(tw, Height), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int nameW = Math.Min(nameSize.Width, tw);
            Theme.Text1(g, Rule.Name, Theme.Body, new Rectangle(tx, line1 - Theme.S(10), nameW + Theme.S(4), Theme.S(20)), nameColor, TextFormatFlags.NoPadding);

            string state = null; Color stateColor = Theme.Success;
            if (!Rule.Enabled) { state = "выключено"; stateColor = Theme.Muted; }
            else if (Status != null && Status.Count > 0)
            {
                if (Status.Soft) { state = "● запущена · через CPU Sets"; stateColor = Theme.Success; }
                else if (Status.Protected) { state = "● пока не даёт поменять ядра, пробую ещё"; stateColor = Theme.Warning; }
                else { state = "● запущена"; stateColor = Theme.Success; }
            }
            else if (FolderGone) { state = "● папки больше нет"; stateColor = Theme.Warning; }
            var note = owner.PendingNote(Rule);
            int x = tx + nameW + Theme.S(8);
            if (state != null && x < tx + tw)
            {
                int w = TextRenderer.MeasureText(g, state, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                Theme.Text1(g, state, Theme.Small, new Rectangle(x, line1 - Theme.S(10), Math.Min(w + Theme.S(4), tx + tw - x), Theme.S(20)), stateColor, TextFormatFlags.NoPadding);
                x += w + Theme.S(8);
            }
            if (note != null && x < tx + tw)
                Theme.Text1(g, "· " + L.T(note), Theme.Small, new Rectangle(x, line1 - Theme.S(10), tx + tw - x, Theme.S(20)), Theme.Warning, TextFormatFlags.NoPadding);
            // a path rule shows its path; a name rule shows the exe name and the folder it was found in (it matches the name anywhere)
            var sub = Rule.Pattern;
            if (!Rule.ByPath)
            {
                var exeName = Rule.Pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Rule.Pattern : Rule.Pattern + ".exe";
                var where = Status != null && Status.SamplePath != null ? System.IO.Path.GetDirectoryName(Status.SamplePath) : Folder;
                sub = exeName + (where != null ? "  ·  " + where.TrimEnd('\\') : "");
            }
            Theme.Text1(g, sub, Theme.Small, new Rectangle(tx, line2 - Theme.S(10), tw, Theme.S(20)), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);

            Theme.Fill(g, pillR, Theme.S(6), DistColors.Bg(Rule.Group));
            var fg = DistColors.Fg(Rule.Group);
            Theme.Text1(g, Rule.Group + (Rule.NoSmt && Rule.Group != Group.SKIP ? " −SMT" : ""), Theme.Small, new Rectangle(pillR.X + Theme.S(10), pillR.Y, pillR.Width - Theme.S(24), pillR.Height), fg, TextFormatFlags.NoPadding);
            float ax = pillR.Right - Theme.S(14), ay = pillR.Y + pillR.Height / 2f;
            using (var pen = new Pen(fg, Theme.S(1.4f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(ax - Theme.S(3.5f), ay - Theme.S(1.5f)), new PointF(ax, ay + Theme.S(2)), new PointF(ax + Theme.S(3.5f), ay - Theme.S(1.5f)) });
            }

            if (toggleAnim == null) toggleAnim = new ToggleAnim(this, Rule.Enabled, () => toggleR);
            toggleAnim.Go(Rule.Enabled);
            Theme.DrawToggle(g, toggleR, toggleAnim.Pos);

            if (!IsLast) using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { owner.ShowRowMenu(this, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            if (pillR.Contains(e.Location)) owner.ShowGroupMenu(this, new Point(pillR.Left, pillR.Bottom + Theme.S(2)));
            else if (toggleR.Contains(e.Location)) owner.ToggleRule(Rule);
            else if (HasFolder && folderR.Contains(e.Location)) owner.OpenFolder(this);
            else { armed = true; downAt = e.Location; }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (folderHot) { folderHot = false; Invalidate(); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool fh = HasFolder && folderR.Contains(e.Location);
            if (fh != folderHot) { folderHot = fh; Invalidate(); }
            Cursor = pillR.Contains(e.Location) || toggleR.Contains(e.Location) || fh ? Cursors.Hand : gripR.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
            if (armed && (Math.Abs(e.X - downAt.X) > Theme.S(6) || Math.Abs(e.Y - downAt.Y) > Theme.S(6)))
            {
                armed = false;
                DoDragDrop(this, DragDropEffects.Move);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) { armed = false; base.OnMouseUp(e); }

        GroupCard Card { get { return Parent as GroupCard; } }
        protected override void OnDragEnter(DragEventArgs e) { if (Card != null) Card.DragHover(e); }
        protected override void OnDragOver(DragEventArgs e) { if (Card != null) Card.DragHover(e); }
        protected override void OnDragLeave(EventArgs e) { if (Card != null) Card.DragOut(); }
        protected override void OnDragDrop(DragEventArgs e) { if (Card != null) Card.Drop(e); }
    }

    // CCD1 / CCD2 / ALL card: header and its rows; programs can be dropped onto it
    class GroupCard : Painted
    {
        public readonly Group Group;
        public string Range = "";
        public string Note = ""; // line under the rows (SKIP: the built-in launcher list)
        readonly DistPage owner;
        bool hot;
        int noteY;

        public GroupCard(DistPage owner, Group group) { this.owner = owner; Group = group; AllowDrop = true; BackColor = Theme.Window; }

        public RuleRow[] Rows { get { return Controls.OfType<RuleRow>().ToArray(); } }

        public void Relayout(int width)
        {
            Width = width;
            int y = Theme.S(46);
            var rows = Rows.Where(r => r.Shown).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Bounds = new Rectangle(Theme.S(14), y, width - Theme.S(28), Theme.S(50));
                rows[i].IsLast = i == rows.Count - 1;
                y += rows[i].Height;
            }
            if (rows.Count == 0) y += Theme.S(44);
            noteY = y;
            if (Note.Length > 0) y += Theme.S(30);
            Height = y + Theme.S(8);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(g, r, Theme.S(12), Theme.Card);
            Theme.Stroke(g, r, Theme.S(12), hot ? Theme.Accent : Theme.Border);

            float d = Theme.S(10);
            using (var b = new SolidBrush(DistColors.Dot(Group))) g.FillRectangle(b, Theme.S(16), Theme.S(23) - d / 2, d, d);
            int x = Theme.S(34);
            var title = Group.ToString();
            int tw = TextRenderer.MeasureText(g, title, Theme.Bold, Size.Empty, TextFormatFlags.NoPadding).Width;
            Theme.Text1(g, title, Theme.Bold, new Rectangle(x, Theme.S(13), tw + Theme.S(4), Theme.S(20)), Theme.Text, TextFormatFlags.NoPadding);
            Theme.Text1(g, "· " + L.T(Range), Theme.Body, new Rectangle(x + tw + Theme.S(6), Theme.S(13), Math.Max(0, Width - x - tw - Theme.S(120)), Theme.S(20)), Theme.Muted, TextFormatFlags.NoPadding);
            int n = Rows.Count(row => row.Shown);
            Theme.Text1(g, Plural(n), Theme.Small, new Rectangle(0, Theme.S(13), Width - Theme.S(16), Theme.S(20)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Theme.S(14), Theme.S(45), Width - Theme.S(14), Theme.S(45));
            if (n == 0)
                Theme.Text1(g, "Перетащите сюда программу или выберите группу в её меню", Theme.Small, new Rectangle(0, Theme.S(46), Width, Theme.S(44)), Theme.Muted, TextFormatFlags.HorizontalCenter);
            if (Note.Length > 0)
                Theme.Text1(g, Note, Theme.Small, new Rectangle(Theme.S(16), noteY, Width - Theme.S(32), Theme.S(30)), Theme.Muted);
        }

        static string Plural(int n)
        {
            int m10 = n % 10, m100 = n % 100;
            var w = m10 == 1 && m100 != 11 ? "программа" : m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? "программы" : "программ";
            return n + " " + w;
        }

        public void DragHover(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(RuleRow))) return;
            e.Effect = DragDropEffects.Move;
            if (!hot) { hot = true; Invalidate(); }
        }

        public void DragOut() { if (hot) { hot = false; Invalidate(); } }

        public void Drop(DragEventArgs e)
        {
            DragOut();
            var row = e.Data.GetData(typeof(RuleRow)) as RuleRow;
            if (row != null) owner.MoveRule(row.Rule, Group);
        }

        protected override void OnDragEnter(DragEventArgs e) { DragHover(e); }
        protected override void OnDragOver(DragEventArgs e) { DragHover(e); }
        protected override void OnDragLeave(EventArgs e) { DragOut(); }
        protected override void OnDragDrop(DragEventArgs e) { Drop(e); }
    }

    // single-line text field on a rounded dark surface
    class SearchBox : Painted, ILang
    {
        public readonly TextBox Box = new TextBox();
        readonly string cue;

        // the grey hint follows the language switch
        public void Relang() { if (Box.IsHandleCreated) Native.CueBanner(Box.Handle, L.T(cue)); }

        public SearchBox(string cue)
        {
            this.cue = cue;
            BackColor = Theme.Window;
            Height = Theme.S(32);
            Box.BorderStyle = BorderStyle.None; Box.BackColor = Theme.Raised; Box.ForeColor = Theme.Text; Box.Font = Theme.Body;
            Controls.Add(Box);
            Box.HandleCreated += (s, e) => Native.CueBanner(Box.Handle, L.T(cue));
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Box.Bounds = new Rectangle(Theme.S(12), (Height - Box.PreferredHeight) / 2 + 1, Width - Theme.S(24), Box.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.Fill(e.Graphics, r, Theme.S(6), Theme.Raised);
            Theme.Stroke(e.Graphics, r, Theme.S(6), Box.Focused ? Theme.Accent : Theme.Border);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }
    }
}
