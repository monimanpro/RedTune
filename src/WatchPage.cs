using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace RedTune
{
    // 16.11: «Сторож», the third tab of «Кривая» (Watchdog): the switch, what it does, what it has seen and what it advises
    class WatchPage : Page
    {
        readonly Watchdog watch;
        readonly CurveService curve;
        readonly Settings settings;
        readonly Toggle onToggle = new Toggle(), deepToggle = new Toggle();
        static readonly int[] DayChoices = { 3, 5, 7 };
        readonly Segmented daysSeg = new Segmented("3 дня", "5 дней", "7 дней");
        readonly Row onRow, deepRow, daysRow, stateRow, deepenRow, boxRow;
        // 20.11: when the black box records (Settings.BlackBoxMode)
        readonly Segmented boxSeg = new Segmented("Всегда", "Только тесты", "Со сторожем");
        static readonly int[] BoxModes = { 1, 2, 0 }; // the buttons in that order → BlackBoxMode (the stored value stays as it was)
        readonly Pill statePill = new Pill();
        readonly Row[] coreRows;
        readonly SectionLabel adviceLabel;
        readonly TextCard help, journal;
        readonly FlatButton deepenBtn = new FlatButton("Опустить на 1", true), laterBtn = new FlatButton("Не сейчас"), forgetBtn = new FlatButton("Забыть ошибки");
        readonly Row forgetRow;
        readonly Timer timer = new Timer { Interval = 5000 };

        const string HelpText = "Что это. Кривая, прошедшая все тесты, может оказаться на шаг глубже, чем выдерживает какое-то ядро в обычной работе. Тогда ядро сначала ошибается тихо: процессор сам исправляет ошибку и записывает её в журнал Windows (WHEA). Ничего не падает, заметить это нельзя, а до настоящих вылетов остаётся несколько шагов. Сторож читает эти записи и говорит, какое ядро ошиблось.\n\nЧто он делает. Раз в минуту смотрит журнал Windows; после перезагрузки без завершения работы берёт из чёрного ящика ядра, которые работали в последний момент. Для каждого такого ядра появляется совет поднять значение на 3 шага (около 15 мВ). Сам сторож в процессор ничего не пишет: значение меняется, когда вы нажимаете «Поднять на 3»; пресет кривой сохраняется и применяется сразу. «Не трогать» убирает совет.\n\nЧего он не делает. Не нагружает процессор и ничего не тестирует. Не считает ошибки во время автотюна и замера кривых ядер: там они часть теста. Не обещает, что вылетов не будет: резкий сбой может случиться без предупреждающих ошибок.\n\nПостепенное углубление (по желанию). Если выбранное число дней прошло без ошибок, сторож предлагает опустить все ядра на 1 шаг. Ядро, которое после этого ошибётся, получит совет «поднять на 3» и больше не углубляется. Так за пару недель каждое ядро приходит к своему пределу, проверенному вашей обычной работой, а не синтетическим тестом. Шаг вниз тоже делается только по вашей кнопке. Исправленные ошибки обычно появляются раньше вылетов, но не всегда: отдельная перезагрузка на этом пути возможна.\n\n«Забыть ошибки» снова разрешает углублять ядра, которые ошибались: например после обновления BIOS или замены термопасты.";

        public WatchPage(Watchdog watch, CurveService curve, Settings settings, Action<int> openTab)
        {
            this.watch = watch; this.curve = curve; this.settings = settings;
            var tabs = Add(new PageTabs(2, "Кривая", "Автотюн", "Сторож"), 0);
            tabs.Picked += i => openTab(i);
            var helpBtn = new FlatButton("Как это работает");
            Row("Сторож кривой", "Следит за ошибками процессора в обычной работе и говорит, какое ядро поднять", helpBtn);
            help = Add(new TextCard(HelpText)); help.Visible = false;
            helpBtn.Click += (s, e) => { help.Visible = !help.Visible; Relayout(); };
            onRow = Row("Слежение", "", onToggle);
            stateRow = Row("Состояние", "", statePill);
            boxRow = Row("Чёрный ящик", "", boxSeg);
            boxSeg.Selected = Math.Max(0, Array.IndexOf(BoxModes, settings.BlackBoxMode));
            boxSeg.Changed += (s, e) =>
            {
                if (boxSeg.Selected < 0 || BoxModes[boxSeg.Selected] == settings.BlackBoxMode) return;
                settings.BlackBoxMode = BoxModes[boxSeg.Selected]; Store.SaveSettings(settings);
                Store.Log("чёрный ящик: режим " + (new[] { "со сторожем", "всегда", "только тесты" })[settings.BlackBoxMode]);
                ShowState();
            };

            adviceLabel = Section("Советы");
            coreRows = new Row[Hw.CurveCores];
            for (int c = 0; c < coreRows.Length; c++)
            {
                int core = c;
                var raise = new FlatButton("Поднять на 3", true); var skip = new FlatButton("Не трогать");
                var row = Row(Hw.CoreName(c), "", skip, raise);
                row.WarnStyle = true; row.Visible = false;
                raise.Click += (s, e) => { raise.Enabled = false; watch.Raise(core, ok => { raise.Enabled = true; if (!ok) Msg.Show(FindForm(), "Кривая не применилась: " + curve.LastError, "Red Tune"); ShowState(); }); };
                skip.Click += (s, e) => watch.Skip(core);
                coreRows[c] = row;
            }
            deepenRow = Row("Можно попробовать глубже", "", laterBtn, deepenBtn); deepenRow.Visible = false;
            deepenBtn.Click += (s, e) =>
            {
                if (Msg.Show(FindForm(), "Опустить все ядра, которые ещё не ошибались, на 1 шаг? Пресет «" + curve.Active.Name + "» будет изменён и применён.", "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                deepenBtn.Enabled = false;
                watch.Deepen(ok => { deepenBtn.Enabled = true; if (!ok) Msg.Show(FindForm(), "Кривая не применилась: " + curve.LastError, "Red Tune"); ShowState(); });
            };
            laterBtn.Click += (s, e) => watch.SkipDeepen();

            Section("Постепенное углубление");
            deepRow = Row("Предлагать шаг глубже", "После нескольких дней без ошибок — совет опустить все ядра на 1 шаг", deepToggle);
            daysRow = Row("Сколько дней без ошибок", "Чем дольше срок, тем надёжнее каждый шаг", daysSeg);
            forgetRow = Row("Ядра, которые ошибались", "", forgetBtn);
            forgetBtn.Click += (s, e) =>
            {
                if (Msg.Show(FindForm(), "Забыть, какие ядра ошибались? Их снова можно будет углублять.", "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                watch.Forget();
            };

            Section("Журнал");
            journal = Add(new TextCard("")); journal.Mono = true;

            onToggle.Set(settings.WatchOn); deepToggle.Set(settings.WatchDeepen);
            daysSeg.Selected = Math.Max(0, Array.IndexOf(DayChoices, settings.WatchDays));
            onToggle.CheckedChanged += (s, e) =>
            {
                settings.WatchOn = onToggle.Checked; Store.SaveSettings(settings);
                Store.Log("сторож: " + (settings.WatchOn ? "включён" : "выключен"));
                if (settings.WatchOn) watch.Restart();
                ShowState();
            };
            deepToggle.CheckedChanged += (s, e) => { settings.WatchDeepen = deepToggle.Checked; Store.SaveSettings(settings); if (settings.WatchDeepen) watch.SkipDeepen(); ShowState(); };
            daysSeg.Changed += (s, e) => { if (daysSeg.Selected >= 0) { settings.WatchDays = DayChoices[daysSeg.Selected]; Store.SaveSettings(settings); ShowState(); } };
            watch.Changed += () => { if (!IsDisposed && Visible) ShowState(); };
            timer.Tick += (s, e) => ShowState();
            VisibleChanged += (s, e) => { if (Visible) { ShowState(); timer.Start(); } else timer.Stop(); };
            ShowState();
        }

        static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "только что";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " мин назад";
            return (int)t.TotalHours + " ч назад";
        }

        static string Quiet(TimeSpan t)
        {
            return t.TotalDays >= 1 ? (int)t.TotalDays + " дн. " + t.Hours + " ч" : t.TotalHours >= 1 ? (int)t.TotalHours + " ч " + t.Minutes + " мин" : Math.Max(0, (int)t.TotalMinutes) + " мин";
        }

        void ShowState()
        {
            var st = watch.State;
            bool on = settings.WatchOn;
            // the black box follows within 2 s; the line says what the choice means and whether it records now
            int bm = settings.BlackBoxMode;
            bool rec = BlackBox.Wanted();
            boxRow.SetSub((rec ? "Сейчас пишет · " : "Сейчас не пишет · ") + (bm == 1 ? "всегда, каждые 2 секунды" : bm == 2 ? "только во время автотюна и замера кривых ядер"
                : "пока включено слежение и во время тестов"));
            var preset = curve.Active;
            onRow.SetSub(!on ? "Выключено: журнал ошибок никто не читает"
                : "Включено · проверка раз в минуту" + (watch.LastCheck > DateTime.MinValue ? " · последняя " + Ago(DateTime.Now - watch.LastCheck) : ""));
            if (!on) { stateRow.SetSub("Включите слежение, чтобы сторож начал считать"); statePill.Set("выключен", PillKind.Muted); }
            else
            {
                stateRow.SetSub("Без ошибок: " + Quiet(DateTime.Now - watch.Since) + " · за всё время: ошибок WHEA " + st.Wheas + ", перезагрузок " + st.Resets
                    + (preset != null ? " · пресет «" + preset.Name + "»" : ""));
                statePill.Set(st.Pending.Count > 0 ? "есть совет" : "спокойно", st.Pending.Count > 0 ? PillKind.Warn : PillKind.Ok);
            }

            bool any = false;
            for (int c = 0; c < coreRows.Length; c++)
            {
                bool show = on && st.Pending.Contains(c);
                if (show && preset != null && c < preset.Values.Length)
                {
                    int v = preset.Values[c];
                    var last = st.Events.LastOrDefault(e => e.Core == c && (e.Kind == "whea" || e.Kind == "reset"));
                    string why = last == null ? "работало перед перезагрузкой" : last.Kind == "whea" ? "исправленных ошибок " + last.Count : "работало перед перезагрузкой";
                    coreRows[c].SetSub(why + " · сейчас " + v + " → станет " + Math.Min(0, v + Watchdog.RaiseStep));
                }
                if (coreRows[c].Visible != show) coreRows[c].Visible = show;
                any |= show;
            }
            bool deepen = on && settings.WatchDeepen && st.DeepenOffer;
            if (deepen) deepenRow.SetSub(Quiet(DateTime.Now - watch.Since) + " без ошибок. Все ядра, которые ещё не ошибались, станут на 1 шаг глубже");
            if (deepenRow.Visible != deepen) deepenRow.Visible = deepen;
            adviceLabel.Visible = any || deepen;

            daysRow.Visible = settings.WatchDeepen;
            deepRow.SetSub(!settings.WatchDeepen ? "После нескольких дней без ошибок — совет опустить все ядра на 1 шаг"
                : !on ? "Работает только при включённом слежении"
                : "Следующий совет через " + Quiet(TimeSpan.FromDays(Math.Max(1, settings.WatchDays)) - (DateTime.Now - watch.Since)) + ", если не будет ошибок");
            forgetRow.SetSub(st.Frozen.Count == 0 ? "Таких нет" : string.Join(", ", st.Frozen.OrderBy(c => c).Select(Hw.CoreName)) + " — больше не углубляются");
            forgetBtn.Enabled = st.Frozen.Count > 0;

            var lines = st.Events.Skip(Math.Max(0, st.Events.Count - 14)).Reverse().Select(e =>
            {
                DateTime t; var when = DateTime.TryParse(e.Time, out t) ? t.ToString("dd.MM HH:mm") : "";
                return when + "  " + e.Text;
            }).ToList();
            journal.SetText(lines.Count == 0 ? "Пока пусто" : string.Join("\n", lines));
            Relayout();
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
