using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // 20.11 (the user's wishes): pages that left the menu and became tabs of another item, as «Автотюн» and «Сторож» are
    // tabs of «Кривая»: «Устройство» and «Компоненты» under «Настройки»; «Распределение» and «Автозагрузка» under the new
    // item «Программы». Each stays its own page with its own scrolling, the tabs on top of each switch between them, and
    // the menu shows the item lit for all of them. A page this computer cannot run (Hw.PageFits) is not a tab; a group
    // left with one page shows no tabs. The keys stay the same, so ShowPage("comp") from the home page still lands on its tab.
    static class PageGroups
    {
        public static Action<string> Show; // MainForm's ShowPage; null in the off-screen renders

        // the menu item a page is shown under
        public static string Parent(string key)
        {
            switch (key)
            {
                case "tune": case "watch": return "curve";
                case "device": case "comp": return "settings";
                case "dist": case "startup": return "apps";
                default: return key;
            }
        }

        // the pages of a menu item that this computer runs, in the order of the tabs
        public static string[] Keys(string parent)
        {
            var all = parent == "settings" ? new[] { "settings", "device", "comp" } : parent == "apps" ? new[] { "dist", "startup" } : new[] { parent };
            return all.Where(Hw.PageFits).ToArray();
        }

        static string Name(string key)
        {
            switch (key)
            {
                case "settings": return "Основное";
                case "device": return "Устройство";
                case "comp": return "Компоненты";
                case "dist": return "Распределение";
                default: return "Автозагрузка";
            }
        }

        public static void AddTabs(Page page, string key)
        {
            var keys = Keys(Parent(key));
            if (keys.Length < 2) return;
            var tabs = new PageTabs(Math.Max(0, Array.IndexOf(keys, key)), keys.Select(Name).ToArray());
            tabs.Picked += i => { var h = Show; if (h != null) h(keys[i]); };
            page.Add(tabs, 0);
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
            PageGroups.AddTabs(this, "comp");
            // this computer and which sections fit it (Hw); the report goes to whoever adds support for another laptop
            Section("Это устройство");
            var copy = new FlatButton("Скопировать отчёт");
            // 16.11: the full report into a file on the desktop (Diag): for a laptop the program was never tried on, to send
            // to whoever adds support for it. It only reads, and carries no names.
            var full = new FlatButton("Полный отчёт в файл");
            // 21.11: the report straight into the GitHub form (the browser opens it filled in; Red Tune itself sends nothing)
            var send = new FlatButton("Отправить на GitHub", true);
            var hwRow = Row(Hw.Short(), Hw.Details(), send, copy, full);
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
            Func<string> report = () =>
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
                return text;
            };
            copy.Click += (o, e) =>
            {
                hwRow.SetSub("Собираю отчёт…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    var text = report();
                    try { BeginInvoke((Action)(() => { try { Clipboard.SetText(text); hwRow.SetSub("Отчёт о системе скопирован — его можно вставить в сообщение"); } catch { hwRow.SetSub("Не удалось скопировать отчёт"); } })); } catch { }
                });
            };
            // the form «Hardware report» of the issue templates, its fields filled from the address (ids: report, version);
            // a report too long for an address goes to the clipboard and the form opens without it
            send.Click += (o, e) =>
            {
                hwRow.SetSub("Собираю отчёт…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    var text = report();
                    var url = "https://github.com/" + Updater.Repo + "/issues/new?template=hardware-report.yml&title=" + Uri.EscapeDataString("[Hardware] " + Hw.Short())
                        + "&version=" + Uri.EscapeDataString(Updater.Current.ToString());
                    var withReport = url + "&report=" + Uri.EscapeDataString(text);
                    bool fits = withReport.Length <= 7500;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            try { Clipboard.SetText(text); } catch { }
                            try { Process.Start(fits ? withReport : url); }
                            catch (Exception ex) { hwRow.SetSub(L.T("Браузер не открылся: ") + ex.Message); return; }
                            hwRow.SetSub(fits ? "Форма открылась в браузере с отчётом внутри: отметь, что работает, и нажми «Create»" : "Форма открылась в браузере, отчёт скопирован: вставь его в поле «System report»");
                        }));
                    }
                    catch { }
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
                // 21.11: its installer comes with Red Tune (pawnio\PawnIO_setup.exe, the author's, signed, unchanged): one
                // press installs it silently; without that file (a copy without it) the button opens the author's page
                var setup = Path.Combine(Application.StartupPath, "pawnio", "PawnIO_setup.exe");
                bool bundled = File.Exists(setup);
                var web = new FlatButton(bundled ? "Установить" : "Скачать", bundled);
                const string sub = "Драйвер доступа к процессору: кривая, лимиты AMD, температура";
                var row = Row("PawnIO", sub, pill, web);
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
                web.Click += (o, e) =>
                {
                    if (!File.Exists(setup)) { try { Process.Start("https://pawnio.eu/"); } catch { } return; }
                    if (!ConfirmDialog.Ask(FindForm(), "PawnIO", "Red Tune установит драйвер PawnIO его собственным установщиком (автор namazso, драйвер подписан Microsoft). Это займёт несколько секунд.\n\nПотом Red Tune перезапустится, чтобы начать работать через драйвер.", "Установить", "Отмена")) return;
                    web.Enabled = false; row.SetSub("Устанавливаю PawnIO…");
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        int code = -1; string err = null;
                        try
                        {
                            using (var p = Process.Start(new ProcessStartInfo(setup, "-install -silent") { UseShellExecute = false, CreateNoWindow = true }))
                                code = p.WaitForExit(120000) ? p.ExitCode : -2;
                        }
                        catch (Exception ex) { err = ex.Message; }
                        Store.Log("PawnIO: установка " + (err ?? "код " + code) + ", файл драйвера " + (File.Exists(lib) ? "есть" : "нет"));
                        try
                        {
                            BeginInvoke((Action)(() =>
                            {
                                web.Enabled = true; check();
                                if (!File.Exists(lib)) { row.SetSub(err != null ? "Не установился: " + err : code == -2 ? "Не установился: установщик не ответил" : "Не установился: код " + code); return; }
                                row.SetSub(sub);
                                var main = FindForm() as MainForm;
                                if (main != null && ConfirmDialog.Ask(main, "PawnIO", "PawnIO установлен. Перезапустить Red Tune сейчас, чтобы кривая, лимиты и температура заработали?", "Перезапустить", "Позже"))
                                    main.RestartApp();
                            }));
                        }
                        catch { }
                    });
                };
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
        public AboutPage(Settings s)
        {
            Add(new AboutCard());
            var data = new FlatButton("Открыть");
            data.Click += (o, e) => { Directory.CreateDirectory(Store.DataDir); Process.Start("explorer.exe", "\"" + Store.DataDir + "\""); };
            Row("Папка с настройками", Store.DataDir, data);

            // 21.11: the last error nothing caught (Crash), with the Bug form filled in; shown only when there is one
            var crash = Crash.Last();
            if (crash != null)
            {
                Section("Ошибки");
                var send = new FlatButton("Отправить на GitHub", true);
                var open = new FlatButton("Открыть");
                var clear = new FlatButton("Убрать");
                var crow = Row((crash.Fatal ? L.T("Red Tune завершился с ошибкой · ") : L.T("Ошибка, программа продолжила работу · ")) + crash.Time.ToString("dd.MM HH:mm"), crash.Kind, send, open, clear);
                open.Click += (o, e) => { try { Process.Start("notepad.exe", "\"" + Crash.FilePath + "\""); } catch { } };
                clear.Click += (o, e) => { try { File.Delete(Crash.FilePath); } catch { } crow.SetSub("Убрано"); send.Enabled = open.Enabled = clear.Enabled = false; };
                send.Click += (o, e) =>
                {
                    crow.SetSub("Собираю отчёт…");
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        string report = ""; try { report = Hw.Report(); } catch { }
                        var url = Crash.BugUrl(crash, report);
                        try
                        {
                            BeginInvoke((Action)(() =>
                            {
                                try { Clipboard.SetText(crash.Text); } catch { }
                                try { Process.Start(url); } catch (Exception ex) { crow.SetSub(L.T("Браузер не открылся: ") + ex.Message); return; }
                                crow.SetSub("Форма открылась в браузере: допиши, что ты делал перед ошибкой, и нажми «Create». Вся запись ещё и в буфере");
                            }));
                        }
                        catch { }
                    });
                };
            }

            // 21.11: new versions from GitHub (Updater): look now, install (only a copy the setup installed), or the page
            Section("Обновления");
            var check = new FlatButton("Проверить");
            var install = new FlatButton("Установить", true);
            var web = new FlatButton("Страница выпусков");
            var row = Row("Red Tune " + Updater.Current, null, install, web, check);
            Action show = () =>
            {
                var f = Updater.Found;
                install.Visible = f != null && Updater.Installed;
                if (f != null) row.SetSub(L.T("Доступна версия ") + f.Version + (Updater.Installed ? "" : L.T(" · эта копия запущена не из установки: новую версию можно скачать со страницы выпусков")));
                else row.SetSub(string.IsNullOrEmpty(s.UpdateLast) ? "Ещё не проверялось" : L.T("Последняя проверка: ") + s.UpdateLast);
            };
            show();
            web.Click += (o, e) => { try { Process.Start(Updater.Found != null && Updater.Found.Page != null ? Updater.Found.Page : Updater.Releases); } catch { } };
            check.Click += (o, e) =>
            {
                check.Enabled = false; row.SetSub("Проверяю…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    string err = null; Updater.Info i = null;
                    try { i = Updater.Check(s); } catch (Exception ex) { err = ex.Message; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            check.Enabled = true;
                            if (err != null) { row.SetSub(L.T("Не удалось проверить: ") + err); return; }
                            if (Updater.Newer(i)) show(); else row.SetSub(L.T("Это последняя версия · проверено ") + s.UpdateLast);
                        }));
                    }
                    catch { }
                });
            };
            // 21.11: the way back after an update: the version that was there before, from its own release (the same checks)
            var prev = s.UpdatePrev;
            var back = new FlatButton(L.T("Вернуть ") + prev);
            back.Visible = Updater.Installed && !string.IsNullOrEmpty(prev) && prev != Updater.Current.ToString();
            if (back.Visible) Row("Прошлая версия", "Если новая версия работает хуже — вернёт ту, что стояла до обновления. Настройки, пресеты и кривые останутся", back);
            // download (the newest release, or the one of the version to go back to), check, run the setup, close
            Action<Func<Updater.Info>> go = pick =>
            {
                install.Enabled = check.Enabled = back.Enabled = false; row.SetSub("Скачиваю…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    string path = null, err = null;
                    try { path = Updater.Download(pick(), p => { try { BeginInvoke((Action)(() => row.SetSub(L.T("Скачиваю… ") + p + " %"))); } catch { } }); }
                    catch (Exception ex) { err = ex.Message; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (err != null) { install.Enabled = check.Enabled = back.Enabled = true; row.SetSub(L.T("Не удалось обновить: ") + err); return; }
                            try { Updater.Install(path, s); }
                            catch (Exception ex) { install.Enabled = check.Enabled = back.Enabled = true; row.SetSub(L.T("Не удалось обновить: ") + ex.Message); return; }
                            var m = FindForm() as MainForm; if (m != null) m.ExitApp(); else Application.Exit();
                        }));
                    }
                    catch { }
                });
            };
            install.Click += (o, e) =>
            {
                var f = Updater.Found; if (f == null) return;
                if (!ConfirmDialog.Ask(FindForm(), "Обновление", L.T("Red Tune закроется, установщик обновит его до версии ") + f.Version + L.T(" и запустит снова. Настройки, пресеты и кривые останутся."), "Обновить", "Отмена")) return;
                go(() => f);
            };
            back.Click += (o, e) =>
            {
                if (!ConfirmDialog.Ask(FindForm(), "Прошлая версия", L.T("Red Tune закроется, установщик вернёт версию ") + prev + L.T(" и запустит её. Настройки, пресеты и кривые останутся."), "Вернуть", "Отмена")) return;
                go(() => Updater.ForVersion(prev));
            };
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
