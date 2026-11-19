using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    class PlaceholderPage : Page
    {
        public PlaceholderPage(string title, string stage, string what)
        {
            Row(title + " — " + stage, what);
        }
    }

    // what the curve, the memory page and the auto-tune need: found or not, where it is, where it comes from;
    // every row can point to another folder ("Указать папку") and go back to the default ("Сбросить")
    class ComponentsPage : Page
    {
        readonly Settings s;
        readonly System.Collections.Generic.List<Action> checks = new System.Collections.Generic.List<Action>();

        public ComponentsPage(Settings s)
        {
            this.s = s;
            // this computer and which sections fit it (Hw); the report goes to whoever adds support for another laptop
            Section("Это устройство");
            var copy = new FlatButton("Скопировать отчёт");
            // 16.11: the full report into a file on the desktop (Diag): for a laptop the program was never tried on, to send
            // to whoever adds support for it. It only reads, and carries no names.
            var full = new FlatButton("Полный отчёт в файл");
            var hwRow = Row(Hw.Short(), Hw.Details(), copy, full);
            full.Click += (o, e) =>
            {
                full.Enabled = false;
                hwRow.SetSub("Собираю полный отчёт: это только чтение, около полуминуты…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    string path = null, err = null;
                    try { path = Diag.DefaultPath(); System.IO.File.WriteAllText(path, Diag.Full(s), new System.Text.UTF8Encoding(true)); }
                    catch (Exception ex) { err = ex.Message; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            full.Enabled = true;
                            if (err != null) { hwRow.SetSub("Отчёт не сохранился: " + err); return; }
                            hwRow.SetSub("Отчёт на рабочем столе: " + System.IO.Path.GetFileName(path) + " — его можно отправить");
                            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                        }));
                    }
                    catch { }
                });
            };
            // the report, with what the processor's SMU says (AMD limits: family, mailbox, limits now), read off the UI thread
            copy.Click += (o, e) =>
            {
                hwRow.SetSub("Собираю отчёт…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    var text = Hw.Report();
                    if (Hw.Amd)
                        try
                        {
                            var i = AmdPower.Check();
                            text += Environment.NewLine + "SMU: " + (i.Error ?? (i.Codename + " · " + (i.Apu ? "MP1 (STAPM/fast/slow)" : "RSMU (PPT/TDC/EDC)") + " · " + i.Path
                                + (i.Limits != null ? " · сейчас " + string.Join("/", i.Limits) + " W" : " · лимиты не прочитались"))) + Environment.NewLine;
                        }
                        catch { }
                    try { BeginInvoke((Action)(() => { try { Clipboard.SetText(text); hwRow.SetSub("Отчёт о системе скопирован — его можно вставить в сообщение"); } catch { hwRow.SetSub("Не удалось скопировать отчёт"); } })); } catch { }
                });
            };
            Row(Hw.VerifiedModel ? "Эта модель проверена" : "Эта модель ещё не проверена",
                Hw.VerifiedModel ? "Всё, что есть в программе, проверялось на этом ноутбуке"
                : "Red Tune проверялся на Legion R7000P. Здесь он работает по тем же правилам, но части с пометкой «не проверено» на такой модели ещё не видели в работе. Отчёт выше поможет добавить её");;
            foreach (var p in new[] { Tuple.Create("power", "Питание"), Tuple.Create("curve", "Кривая"), Tuple.Create("gpu", "Видеокарта"), Tuple.Create("ram", "Память"), Tuple.Create("dist", "Распределение"), Tuple.Create("device", "Устройство") })
            {
                var st = Hw.Status(p.Item1);
                var pill = new Pill(); pill.Set(st == "ok" ? "работает" : st == "unverified" ? "не проверено" : "скрыт", st == "ok" ? PillKind.Ok : st == "unverified" ? PillKind.Warn : PillKind.Muted);
                Row(p.Item2, st == "ok" ? "Поддерживается этим устройством" : st == "unverified" ? Hw.WhyUnverified(p.Item1) : Hw.WhyNot(p.Item1), pill);
            }
            Section("Компоненты");
            Row("Что нужно программе", Stress.Available ? "С кривой Red Tune работает сам, через драйвер PawnIO. Автотюну нужна нагрузка: встроенный Red Tune Test или Prime95" : "С кривой Red Tune работает сам, через драйвер PawnIO. Автотюну ещё нужен Prime95");
            // 17.11: the driver the curve, the AMD limits and the processor temperature go through; it has its own installer
            {
                var pill = new Pill();
                var web = new FlatButton("Скачать");
                var row = Row("PawnIO", "Драйвер доступа к процессору: кривая, лимиты AMD, температура. Ставится отдельно", pill, web);
                row.SetExtra("Откуда: автор namazso, pawnio.eu — подписанный драйвер со своим установщиком");
                var lib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
                Action check = () =>
                {
                    bool have = File.Exists(lib);
                    pill.Set(have ? "найден" : "не найден", have ? PillKind.Ok : PillKind.Warn);
                    web.Visible = !have;
                    row.PerformLayout(); row.Invalidate();
                };
                checks.Add(check);
                check();
                web.Click += (o, e) => { try { Process.Start("https://pawnio.eu/"); } catch { } };
            }
            Item("Prime95", "Нагрузка для автотюна. Подойдёт папка из CoreCycler\\test_programs\\p95 или с сайта",
                () => Load.P95Dir(s),
                dir => File.Exists(Path.Combine(dir, "prime95.exe")) ? null : "В этой папке нет prime95.exe",
                dir => s.P95Path = dir, () => s.P95Path,
                "https://www.mersenne.org/download/",
                "Откуда: mersenne.org (GIMPS); по умолчанию — копия из CoreCycler");
            // 17.11: the program's own test on trial beside Prime95 (Stress): which of the two the auto-tune runs
            if (Stress.Available)
            {
                var load = new DropButton();
                load.Set(new[] { "Prime95", "Red Tune Test" }, s.TuneLoad == "own" ? 1 : 0);
                Row("Нагрузка автотюна", "Red Tune Test встроен в программу: вычисления с самопроверкой, ошибка называет ядро", load);
                load.Changed += (o, e) => { s.TuneLoad = load.Index == 1 ? "own" : "p95"; Store.SaveSettings(s); };
            }
            VisibleChanged += (o, e) => { if (Visible) foreach (var c in checks) c(); };
        }

        // find: what is used now (null = not found); bad: why a picked folder does not fit (null = fits);
        // set: remember or apply the folder; custom: the remembered folder (null/empty = default, no "Сбросить")
        void Item(string name, string what, Func<string> find, Func<string, string> bad, Action<string> set, Func<string> custom, string url, string from)
        {
            var pill = new Pill();
            var pick = new FlatButton("Указать папку");
            var reset = new FlatButton("Сбросить");
            var web = new FlatButton("Скачать");
            var row = Row(name, what, pill, pick, reset, web);
            row.SetExtra(from);
            Action check = () =>
            {
                var where = find();
                bool own = custom != null && !string.IsNullOrEmpty(custom());
                pill.Set(where != null ? "найден" : "не найден", where != null ? PillKind.Ok : PillKind.Warn);
                row.SetSub(where == null ? what : own ? where + "  · указано вручную" : where);
                reset.Visible = own;
                row.PerformLayout(); row.Invalidate();
            };
            checks.Add(check);
            check();
            pick.Click += (o, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = L.T("Папка, где лежит") + " " + name })
                {
                    var cur = find();
                    if (cur != null) d.SelectedPath = File.Exists(cur) ? Path.GetDirectoryName(cur) : cur;
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    var why = bad(d.SelectedPath);
                    if (why != null) { Msg.Show(FindForm(), why, "Red Tune"); return; }
                    try { set(d.SelectedPath); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Msg.Show(FindForm(), "Не удалось: " + ex.Message, "Red Tune"); return; }
                    Store.SaveSettings(s); check();
                }
            };
            reset.Click += (o, e) => { set(""); Store.SaveSettings(s); check(); };
            web.Click += (o, e) => { try { Process.Start(url); } catch { } };
        }
    }
    class AboutPage : Page
    {
        public AboutPage()
        {
            Add(new AboutCard());
            var data = new FlatButton("Открыть");
            data.Click += (o, e) => { Directory.CreateDirectory(Store.DataDir); Process.Start("explorer.exe", "\"" + Store.DataDir + "\""); };
            Row("Папка с настройками", Store.DataDir, data);
        }

        class AboutCard : Painted
        {
            public AboutCard() { BackColor = Theme.Window; Height = Theme.S(390); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                Theme.Fill(g, r, Theme.S(10), Theme.Card);
                Theme.Stroke(g, r, Theme.S(10), Theme.Border);
                int k = Theme.S(190);
                Logo.Draw(g, new RectangleF((Width - k) / 2f, Theme.S(20), k, k), Theme.Text, Theme.Card);
                int y = Theme.S(20) + k + Theme.S(10);
                var center = TextFormatFlags.HorizontalCenter;
                Theme.Text1(g, "Red Tune", Theme.Big, new Rectangle(0, y, Width, Theme.S(30)), Theme.Text, center);
                Theme.Text1(g, "Версия " + Application.ProductVersion + " · " + Hw.Short(), Theme.Small, new Rectangle(0, y + Theme.S(30), Width, Theme.S(20)), Theme.Text2, center);
                int ly = y + Theme.S(62), lx = Width / 2 - Theme.S(190);
                Theme.Text1(g, "Инициатор", Theme.Body, new Rectangle(lx, ly, Theme.S(100), Theme.S(22)), Theme.Text2);
                Theme.Text1(g, "Кожаный — идеи, тесты, перезагрузки и терпение", Theme.Body, new Rectangle(lx + Theme.S(100), ly, Theme.S(330), Theme.S(22)), Theme.Text);
                Theme.Text1(g, "Исполнитель", Theme.Body, new Rectangle(lx, ly + Theme.S(26), Theme.S(100), Theme.S(22)), Theme.Text2);
                Theme.Text1(g, "Claude — код, кривые и 40+ раундов Prime95", Theme.Body, new Rectangle(lx + Theme.S(100), ly + Theme.S(26), Theme.S(330), Theme.S(22)), Theme.Text);
                Theme.Text1(g, "Ни одно ядро не пострадало. Ну, почти.", Theme.Small, new Rectangle(0, ly + Theme.S(62), Width, Theme.S(20)), Theme.Muted, center);
            }
        }
    }
}
