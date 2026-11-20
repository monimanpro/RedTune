using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    class TunePage : Page
    {
        readonly Tuner tuner;
        readonly CurveService curve;
        readonly Settings settings;
        readonly Row condRow, resultRow, redoRow;
        readonly FlatButton redoBtn = new FlatButton("Фазы 2–3 заново");
        // 18.11: the two modes of the quick tune
        readonly Segmented modeSeg = new Segmented("Повседневный", "Строгий");
        Row headRow, modeRow, row1, row2;
        readonly Pill pill1 = new Pill(), pill2 = new Pill();
        double redoHours;
        readonly Pill condPill = new Pill();
        readonly TextCard help, progress;
        readonly FlatButton checkBtn = new FlatButton("Проверить текущую кривую"), startBtn = new FlatButton("Начать", true);
        readonly FlatButton pauseBtn = new FlatButton("Пауза"), resetBtn = new FlatButton("Сбросить"), applyBtn = new FlatButton("Применить результат", true);
        readonly Row controlRow;
        readonly Timer timer = new Timer { Interval = 1000 };
        bool conditionsOk;
        double checkHours, fullHours; // for the questions before a start

        // «~4,5 ч» / «~4.5 h», rounded to half an hour
        static string Hours(double h)
        {
            double r = Math.Max(0.5, Math.Round(h * 2) / 2);
            return "~" + (L.En ? r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " h" : r.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " ч");
        }

        // 14.11: the full description, with the light background (like a browser video) and clock-step stages
        // 16.11: the quick tune (halving by 13, 6, 3, 2, 1 on Prime95 and clock steps, then all cores), the long check on request
        internal const string HelpText = "Что это. Подбор Curve Optimizer для каждого ядра: насколько можно снизить напряжение, чтобы ядро осталось стабильным. Чем отрицательнее значение, тем ниже напряжение и нагрев и тем выше частоты при тех же лимитах, но слишком отрицательное значение роняет систему.\n\nДва режима. Повседневный: глубокая кривая, своя у каждого ядра. Сначала для каждого ядра по одному ищется край — значение, глубже которого оно сдаёт в одиночку: деление пополам от −24 шагами 12, 6, 3, остальные ядра на 0. Потом весь набор стартует с «край +22», но не глубже −12, и идёт вглубь шагами по 3 под нагрузкой на все ядра; каждое ядро — до своей границы. Ядро с ошибкой отступает на шаг и остаётся в нагрузке; после второй ошибки оно больше не спускается. Если сбой никого не называет, набор отступает на шаг и дальше идут чиплеты по отдельности, потом ядра по одному. Ближе чем на 6 шагов к своему краю ядро не подходит. Такая кривая рассчитана на то, что тяжёлая нагрузка идёт на все ядра сразу: глубокие ядра держатся, пока рядом работают более мелкие. Если заняты почти все ядра, а именно мелкие простаивают, возможны ошибки; сам по себе такой расклад редок, но его можно создать привязкой программы к части ядер. Строгий: держит при любом наборе занятых ядер, но заметно мельче; как он идёт, описано ниже.\n\nСтрогий режим. Ядра процессора делят одно напряжение и под нагрузкой влияют друг на друга: мелкое нагруженное ядро поднимает напряжение остальным, глубокое на нём «едет», но не даром. Поэтому каждый шаг проверяется цепочкой нагрузок. Все ядра стартуют с −6 и вместе идут вглубь шагами по 3. На каждом шаге сначала нагружены все ядра: простой, старт нагрузки рывками, 40 секунд непрерывной нагрузки. Ядро, на котором нагрузка или WHEA показали ошибку, возвращается на последнее пройденное значение и дальше не спускается, но в нагрузке на все ядра участвует по-прежнему. Затем те же значения проверяются без остановившихся ядер: без одного самого мелкого, без двух, без четырёх и, наконец, только на тех, кто ещё спускается. Так видно, держат ли глубокие ядра сами, без подпорки. Шаг засчитан, только когда прошла вся цепочка. Если сбой никого не называет (перезагрузка), все ядра, которые ещё спускались, встают на последнее пройденное значение, и спуск окончен. Глубже −45 подбор не идёт.\n\nИтоговая проверка. Весь набор: простой, старт нагрузки, шторм всплесков, лёгкий фон как видео в браузере на каждом чиплете, нагрузка на всех ядрах, в строгом режиме ещё нагрузка без самых мелких ядер (без 1, 2, 4, 8 и 12 из 16) и каждое ядро по очереди (нагрузка, ступени частоты, пробуждения). Названное ядро получает +3. После сбоя без имени под нагрузкой +3 получают ядра на самом глубоком уровне. Часть повторяется. К прошедшему набору прибавляется запас +3.\n\nПолная проверка — по желанию, кнопкой «Проверить текущую кривую»: длинные раунды на активном пресете (один чиплет в простое, другой под нагрузкой, по 25 минут; все ядра 25 минут; шторм 20 минут; фон по 12 минут; ступени по всем ядрам 10 минут). Ничего не подбирает, а если находит слабое ядро, поднимает его на +3. Её можно запустить когда угодно, например на ночь.\n\nТяжёлая нагрузка — Prime95 или Red Tune Test, выбирается на странице «Компоненты». Red Tune Test встроен в программу: на каждом ядре идут вычисления, которые проверяют сами себя, — возведение больших чисел в квадрат через преобразование Фурье у самого предела точности, суммы через блоки FMA, проходы через кэш ядра и резкие переходы от лёгкой работы к тяжёлой. Любой неверный результат — это ошибка ядра, и видно, какого именно.\n\nСбой — ошибка WHEA (аппаратная ошибка процессора в журнале Windows), ошибка нагрузки или перезагрузка. Ошибки WHEA 47 из собственных блоков ядра тоже считаются, хотя Windows подписывает их «память». После перезагрузки подбор продолжается сам через 45 секунд после входа в Windows, поэтому программа включает свой автозапуск; её окно при этом открывается сразу на этой вкладке.\n\nЕсли ноутбук завис. Изредка слишком глубокое значение не перезагружает ноутбук, а намертво его останавливает: экран замер, ничего не происходит дольше пары минут. Сам он из этого не выйдет: выключите его долгим нажатием кнопки питания и включите снова, подбор продолжится.\n\nПотолок частоты. На всё время подбора программа снимает потолок частоты Windows («Максимальная частота процессора» в действующей схеме питания), чтобы ядра проверялись до полного буста; в ступенях и под фоном она двигает его сама. Прежнее значение записывается на диск и возвращается, когда подбор остановлен, а после перезагрузки — при следующем запуске программы.\n\nЧёрный ящик. Каждые 2 секунды программа пишет на диск загрузку, частоту и напряжение каждого ядра. После перезагрузки без завершения работы она показывает, какие ядра работали в последний момент, а автотюн записывает это в ход подбора.\n\nУсловия. Подбирать нужно в самых широких условиях, в которых ноутбук работает: для этого есть пресет питания «00 Тюнинг» (лимиты вашего самого мощного пресета, план Windows, в котором ядра могут засыпать). Включите режим «Свой» с этим пресетом перед стартом.\n\nРезультат сохраняется новым пресетом кривой, применяете его вы.";

        public TunePage(Tuner tuner, CurveService curve, Settings settings, Action back, Action openWatch)
        {
            this.tuner = tuner; this.curve = curve; this.settings = settings;
            var helpBtn = new FlatButton("Как это работает");
            var tabs = Add(new PageTabs(1, "Кривая", "Автотюн", "Сторож"), 0);
            tabs.Picked += i => { if (i == 2) openWatch(); else back(); };
            // 13.11: the times follow this processor (Tuner: steps per core count, the stages per chiplet count)
            // 16.11: the same count as Tuner.Estimate (4 steps, the confirmation, the pauses between rounds and 2 h for retries);
            // the page said ~16 h while the progress said ~19.5 h
            // 16.11: «Начать» is the quick tune: the search per core and the all-core stages; the long check is its own button
            double p1 = Tuner.UniMinutes() / 60.0, p2 = Tuner.FinalMinutes() / 60.0;
            double p3 = Tuner.Phase3.Sum(Tuner.StageMinutes) / 60.0;
            checkHours = p3; fullHours = p1 + p2; redoHours = Tuner.Phase2.Length * 0.5 + p3;
            headRow = Row("Автоматический подбор кривой", "", helpBtn);
            help = Add(new TextCard(HelpText)); help.Visible = false;
            // fewer cores: longer steps, said under the description
            var note = Add(new TextCard(Hw.CurveCores <= 8 ? "На этом процессоре 8 ядер или меньше, поэтому шаги поиска длиннее: 6 минут вместо 4."
                : "На этом процессоре 9–12 ядер, поэтому шаги поиска длиннее: 5 минут вместо 4."));
            note.Visible = false;
            helpBtn.Click += (s, e) => { help.Visible = !help.Visible; Relayout(); }; // the note about longer steps belonged to the old search
            condRow = Row("Условия", "Проверяю…", condPill);
            modeRow = Row("Режим подбора", "", modeSeg);
            modeSeg.Selected = settings.TuneMode == "strict" ? 1 : 0;
            modeSeg.Changed += (s, e) => { if (modeSeg.Selected < 0 || tuner.Running) return; settings.TuneMode = modeSeg.Selected == 1 ? "strict" : "daily"; Store.SaveSettings(settings); ShowMode(); ShowState(); };
            row1 = Row("", "", pill1);
            row2 = Row("", "", pill2);
            Row("3. Полная проверка — по желанию", "Кнопка «Проверить текущую кривую»: длинные раунды, шторм, фон и ступени на активном пресете", P(Hours(p3)));
            controlRow = Row("Запуск", "«Проверить» — полная проверка активного пресета, ничего не подбирает" + " (" + Hours(p3) + ")", checkBtn, startBtn);
            // 16.11: phases 2-3 again from the set phase 1 found, without the 15 hours of phase 1
            redoRow = Row("Повтор фаз 2–3", "", redoBtn); redoRow.Visible = false;
            progress = Add(new TextCard("")); progress.Mono = true; progress.Visible = false;
            Add(new Row("Ход тюнинга", null, pauseBtn, resetBtn) { Visible = false }, 8).Name = "runRow";
            resultRow = Row("Результат", "", applyBtn); resultRow.Visible = false;
            ShowMode();

            startBtn.Click += (s, e) => StartRun("quick");
            checkBtn.Click += (s, e) => StartRun("check");
            redoBtn.Click += (s, e) => StartRun(Tuner.Edges(tuner.State) != null ? "set2" : "p23");
            pauseBtn.Click += (s, e) => { if (tuner.Running) tuner.Pause(); else tuner.Resume(false); ShowState(); };
            resetBtn.Click += (s, e) =>
            {
                if (Msg.Show(FindForm(), "Сбросить тюнинг? Ход и промежуточные значения будут удалены.", "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                tuner.Discard(); ShowState();
            };
            applyBtn.Click += (s, e) => ApplyResult();
            tuner.Changed += ShowState;
            timer.Tick += (s, e) => { if (tuner.Running) ShowState(); };
            VisibleChanged += (s, e) => { if (Visible) { CheckConditions(); ShowState(); timer.Start(); } else timer.Stop(); };
        }

        static Pill P(string text) { var p = new Pill(); p.Set(text, PillKind.Muted); return p; }

        // the rows that say what the chosen mode does, and its time
        void ShowMode()
        {
            bool strict = settings.TuneMode == "strict";
            double h1 = (strict ? Tuner.UniMinutes() : Tuner.EdgeMinutes()) / 60.0, h2 = (strict ? Tuner.FinalMinutes() : Tuner.DailyMinutes()) / 60.0;
            fullHours = h1 + h2;
            headRow.SetSub("Нужен один раз (повторить — после смены BIOS или драйверов)" + " · " + Hours(fullHours) + ", " + "ноутбуком в это время не пользоваться");
            modeRow.SetSub(strict ? "Держит при любом наборе занятых ядер, но мельче: общий уровень и отдельно слабые ядра" : "Глубокая кривая, своя у каждого ядра. Рассчитана на то, что тяжёлая нагрузка идёт на все ядра сразу");
            row1.Title = strict ? "1. Спуск с проверкой каждого шага" : "1. Край каждого ядра";
            row1.SetSub(strict ? "Все ядра от −6 вглубь шагами по 3. Шаг проверяется под нагрузкой на все ядра и без остановившихся; ядро с ошибкой возвращается на шаг и дальше не спускается" : "Каждое ядро по одному, остальные на 0: деление пополам от −24 шагами 12, 6, 3. Края задают форму кривой");
            row2.Title = strict ? "2. Итоговая проверка" : "2. Спуск всем набором";
            row2.SetSub(strict ? "Весь набор: простой, шторм, фон, нагрузка на все ядра и без самых мелких, каждое ядро отдельно; запас +3" : "От «край +22» (не глубже −12) вглубь шагами по 3 под нагрузкой на все ядра; ядро с ошибкой отступает на шаг и остаётся в нагрузке; итоговая проверка и запас +3");
            pill1.Set(Hours(h1), PillKind.Muted); pill2.Set(Hours(h2), PillKind.Muted);
            row1.Invalidate(); row2.Invalidate();
        }

        Row RunRow { get { return Controls.OfType<Row>().First(r => r.Name == "runRow"); } }

        void CheckConditions()
        {
            Task.Run(() =>
            {
                var notes = new System.Collections.Generic.List<string>(); bool ok = true;
                if (SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online) { notes.Add("подключите зарядку"); ok = false; }
                if (Smu.Missing() != null) { notes.Add(Smu.FamilyName == null ? "кривая этого процессора не известна" : "нет PawnIO"); ok = false; }
                if (!Load.OwnLoad(settings) && Load.P95Dir(settings) == null) { notes.Add("нет Prime95"); ok = false; }
                if (CurveService.UxtuRunning) { notes.Add("закройте UXTU (Настройки → Другие программы)"); ok = false; }
                if (PowerService.LltRunning) notes.Add("LLT запущен — лучше отключить");
                string plan = ""; try { var a = WinPower.Active(); plan = WinPower.List().First(p => p.Id == a).Name; } catch { }
                int mode = -1; try { mode = Lenovo.GetPowerMode(); } catch { }
                var modeName = mode == Lenovo.Quiet ? "Тихий" : mode == Lenovo.Balance ? "Баланс" : mode == Lenovo.Performance ? "Макс" : mode == Lenovo.Custom ? "Свой" : "Экстремальный";
                var sub = L.T("Режим «") + L.T(modeName) + L.T("» · схема «") + plan + "» · " + (notes.Count == 0 ? L.T("тяжёлые программы лучше закрыть") : string.Join(" · ", notes.Select(L.T)));
                try { BeginInvoke((Action)(() => { conditionsOk = ok; condRow.SetSub(sub); condPill.Set(ok ? "готово" : "есть замечания", ok ? PillKind.Ok : PillKind.Warn); ShowState(); })); } catch { }
            });
        }

        void StartRun(string mode)
        {
            if (!conditionsOk) { Msg.Show(FindForm(), "Сначала поправьте условия: " + condRow.Sub, "Red Tune"); return; }
            var p1set = Tuner.Phase1Set(tuner.State);
            if (mode == "p23" && p1set == null) return;
            var edges = Tuner.Edges(tuner.State);
            if (mode == "set2" && edges == null) return;
            var text = mode == "check"
                ? "Проверить активный пресет «" + curve.Active.Name + "»? " + Hours(checkHours) + ", возможны перезагрузки. Ноутбуком в это время пользоваться нельзя."
                : mode == "set2"
                ? "Запустить повседневный подбор от готовых краёв, без фазы 1? " + Hw.Describe(edges) + "\n\n" + Hours(Tuner.DailyMinutes() / 60.0) + ", будут перезагрузки. Ноутбуком в это время пользоваться нельзя. Текущий ход тюнинга будет заменён, готовые пресеты кривой останутся."
                : mode == "p23"
                ? "Прогнать фазы 2 и 3 заново от набора после фазы 1? " + Hw.Describe(p1set) + "\n\n" + Hours(redoHours) + ", возможны перезагрузки. Ноутбуком в это время пользоваться нельзя. Текущий ход тюнинга будет заменён, готовые пресеты кривой останутся."
                : "Начать подбор кривой? " + Hours(fullHours) + ", будут перезагрузки. Ноутбуком в это время пользоваться нельзя.\n\nПрограмма включит свой автозапуск, чтобы продолжить после перезагрузки." + "\n\n" + "Если ноутбук завис и дольше пары минут ничего не происходит, выключите его долгим нажатием кнопки питания и включите снова: подбор продолжится.";
            if (Msg.Show(FindForm(), text, "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { if (!Autostart.IsOn()) Autostart.Set(true); } catch (Exception ex) { Msg.Show(FindForm(), "Не удалось включить автозапуск: " + ex.Message, "Red Tune"); return; }
            tuner.Start(mode, mode == "p23" ? p1set : mode == "set2" ? edges : curve.Active.Values);
            ShowState();
        }

        void ApplyResult()
        {
            var name = curve.Settings.ActivePreset;
            var p = curve.Presets.FirstOrDefault(x => x.Name == LastResultName);
            if (p == null) return;
            curve.Settings.ActivePreset = p.Name; Store.SaveSettings(curve.Settings);
            curve.ApplyActive("автотюн", ok => resultRow.SetSub(ok ? "Применён и стал активным пресетом «" + p.Name + "»" : "Не применился: " + curve.LastError));
        }

        public static string LastResultName = "";

        void ShowState()
        {
            var st = tuner.State;
            bool has = st != null;
            progress.Visible = has; RunRow.Visible = has && !st.Done;
            checkBtn.Enabled = startBtn.Enabled = redoBtn.Enabled = modeSeg.Enabled = !tuner.Running;
            // offered once phase 1 is behind (a finished tune, or one stopped in phases 2-3)
            var p1set = has && st.Mode != "quick" && (st.Done || st.Phase >= 2) ? Tuner.Phase1Set(st) : null; // the old long tune only
            // 18.11: the quick tune: its second phase again from the edges it has, phase 1 is not repeated
            int[] edges = settings.TuneMode != "strict" && !tuner.Running ? Tuner.Edges(st) : null; // the daily mode: from the edges an earlier tune found, phase 1 is not repeated
            redoRow.Visible = (p1set != null || edges != null) && !tuner.Running;
            if (edges != null) { redoRow.Title = "Готовые края"; redoBtn.Text = "От готовых краёв"; redoRow.SetSub("Повседневный без фазы 1, от краёв: " + Hw.Describe(edges) + " · " + Hours(Tuner.DailyMinutes() / 60.0)); redoRow.Invalidate(); }
            else if (p1set != null) { redoRow.Title = "Повтор фаз 2–3"; redoBtn.Text = "Фазы 2–3 заново"; redoRow.SetSub("От набора после фазы 1: " + Hw.Describe(p1set) + " · " + Hours(redoHours)); redoRow.Invalidate(); }
            resultRow.Visible = has && st.Done && st.Failed == null;
            if (has)
            {
                var started = DateTime.TryParse(st.StartedAt, out var t0) ? DateTime.Now - t0 : TimeSpan.Zero;
                var left = tuner.SegmentEnd - DateTime.Now;
                var head = st.Done ? (st.Failed != null ? "Остановлено: " + st.Failed : "Готово")
                    : !tuner.Running ? (st.Paused ? "На паузе" : "Ждёт продолжения")
                    : tuner.Now + (left.TotalSeconds > 0 ? " · осталось " + left.ToString(@"m\:ss") : "");
                var lines = new System.Collections.Generic.List<string>
                {
                    head,
                    (st.Mode == "check" ? "Проверка кривой" : st.Mode == "quick" ? (st.Kind == "daily" ? "Повседневный" : "Строгий") + " · " + (st.Phase == 1 ? "края ядер" : st.Step == "final" ? "итоговая проверка" : "спуск") : "Фаза " + st.Phase + " из 3") + " · раундов " + st.Rounds + " · перезагрузок " + st.Resets + " · WHEA " + st.Wheas,
                    "Прошло " + (int)started.TotalHours + " ч " + started.Minutes + " мин" + (st.Done ? "" : " · осталось ~" + (int)tuner.Estimate().TotalHours + " ч " + tuner.Estimate().Minutes + " мин"),
                    "Набор: " + Hw.Describe(st.Cur),
                    "",
                };
                lines.AddRange(st.Log.Skip(Math.Max(0, st.Log.Count - 10)).Reverse());
                progress.SetText(string.Join("\n", lines));
                pauseBtn.Text = tuner.Running ? "Пауза" : "Продолжить";
                if (resultRow.Visible) resultRow.SetSub(Hw.Describe(st.Cur) + " · сохранён пресетом «" + LastResultName + "»");
            }
            Relayout();
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }

    // a card with wrapped text (help, progress log)
    class TextCard : Painted
    {
        string text;
        public bool Mono;

        public TextCard(string text) { this.text = text; BackColor = Theme.Window; Fit(); }

        public void SetText(string t) { if (t == text) return; text = t; Fit(); Drop(); Invalidate(); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }

        // 20.11: the card is drawn once into a picture and the picture is shown after that. The help of «Автотюн» is some
        // 5000 characters: laid out and drawn anew on every frame of the page's glide (the card being uncovered repaints
        // each frame) it took ~30 ms a frame, and the help came out in jerks of 30–45 ms. Made again when the size, the
        // text or the language changes.
        Bitmap pic; string picFor;
        void Drop() { if (pic != null) { pic.Dispose(); pic = null; } picFor = null; }
        protected override void Dispose(bool disposing) { if (disposing) Drop(); base.Dispose(disposing); }

        void Fit()
        {
            int w = Math.Max(Theme.S(200), Width - Theme.S(32));
            var size = TextRenderer.MeasureText(L.T(text ?? ""), Mono ? Theme.Mono : Theme.Small, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int h = size.Height + Theme.S(28);
            if (Height != h) Height = h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            string key = Width + "x" + Height + "|" + L.Code + "|" + Mono + "|" + (text ?? "").GetHashCode();
            if (pic == null || picFor != key)
            {
                Drop();
                pic = new Bitmap(Width, Height);
                using (var g = Graphics.FromImage(pic))
                {
                    g.Clear(BackColor);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                    Theme.Fill(g, r, Theme.S(10), Theme.Card);
                    Theme.Stroke(g, r, Theme.S(10), Theme.Border);
                    TextRenderer.DrawText(g, L.T(text ?? ""), Mono ? Theme.Mono : Theme.Small, new Rectangle(Theme.S(16), Theme.S(14), Width - Theme.S(32), Height - Theme.S(28)), Theme.Text2, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
                picFor = key;
            }
            e.Graphics.DrawImage(pic, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        }
    }
}
