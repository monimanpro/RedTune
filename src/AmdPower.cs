using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // CPU power limits straight in the SMU for AMD laptops without Lenovo's firmware interface: what UXTU does through
    // RyzenAdj. Smu sends them through PawnIO's RyzenSMU module (17.11: the app's own code; before, a PowerShell
    // script with ZenStates-Core). Two kinds of processor:
    //   rsmu — Dragon Range (HX) and other desktop-silicon chips: PPT in W, TDC and EDC in A through the RSMU mailbox
    //          with ZenStates-Core's message numbers (verified on the R7000P, 13.11);
    //   apu  — the laptop APUs, Renoir .. Strix (U, HS, H, Ryzen AI): STAPM, fast and slow PPT in W through the MP1
    //          mailbox with RyzenAdj's messages (not tried on a real APU yet).
    // The SMU forgets the limits on reboot and the firmware may reset them on sleep, so Red Tune applies the saved ones
    // again at start and after sleep (AmdPowerOn). Nothing is written while simulating (Hw).
    static class AmdPower
    {
        // the six limits, in this order everywhere (settings, script arguments, the page)
        public static readonly string[] Keys = { "PPT", "TDC", "EDC", "STAPM", "FAST", "SLOW" };
        public static readonly string[] Units = { "W", "A", "A", "W", "W", "W" };
        public static bool IsApuLimit(int k) { return k >= 3; }

        public class Info
        {
            public string Codename = "", Path = "", Error;
            public bool Apu;                 // the MP1 mailbox (STAPM / fast / slow) instead of PPT / TDC / EDC
            public bool[] Can = new bool[6]; // which of Keys this processor takes
            public int[] Limits;             // what it enforces now: STAPM, fast, slow (W), or null when unread
        }

        // ---------- the processor's class: what the sliders may reach ----------
        // From the model name (13.11, for Ryzen 5000..9000 laptops): U thin and light (15..28 W), H/HS performance
        // (35..54 W), HX desktop silicon (55 W and up), Ryzen AI 300 (15..54 W), Ryzen AI Max (45..120 W). Mid is only
        // where an unset slider stands; nothing is written from it. STAPM, fast and slow use the power span.
        public class Span { public int Min, Max, Mid; public Span(int a, int b, int m) { Min = a; Max = b; Mid = m; } }
        // Hx: desktop silicon (HX laptops and desktop Ryzen), its limits are PPT / TDC / EDC through the RSMU mailbox
        public class Range { public string Name; public bool Hx; public Span Ppt, Tdc, Edc; }

        public static Range ClassOf(string cpu)
        {
            var u = (cpu ?? "").ToUpperInvariant();
            Func<string, Span, Span, Span, Range> R = (n, p, t, e) => new Range { Name = n, Ppt = p, Tdc = t, Edc = e };
            bool ai = u.Contains("RYZEN AI") || System.Text.RegularExpressions.Regex.IsMatch(u, @"\bAI\s+\d");
            if (ai && u.Contains("MAX")) return R("класс Ryzen AI Max (45–120 W)", new Span(25, 140, 70), new Span(30, 160, 90), new Span(40, 220, 130));
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\d{3,4}HX\b|\bHX\s*\d{3}\b"))
            { var r = R("класс HX — игровые (55 W и выше)", new Span(25, 200, 55), new Span(30, 200, 100), new Span(40, 250, 150)); r.Hx = true; return r; }
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\d{4}(HS|H)\b")) return R("класс H/HS — производительные (35–54 W)", new Span(15, 90, 45), new Span(20, 110, 60), new Span(30, 160, 90));
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\d{4}U\b")) return R("класс U — тонкие (15–28 W)", new Span(6, 45, 25), new Span(10, 60, 35), new Span(15, 90, 50));
            if (ai) return R("класс Ryzen AI (15–54 W)", new Span(10, 60, 30), new Span(15, 80, 45), new Span(20, 120, 70));
            // 16.11: desktop Ryzen (AM4 / AM5: no suffix, X, X3D, XT, F, G, GE, GT, T, E). The limits are the PBO ones;
            // an unset slider stands at the 65 W class's factory values (PPT 88 W, TDC 75 A, EDC 150 A on AM5)
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"RYZEN\s+\d\s+(PRO\s+)?\d{4}(X3D|XT|X|F|GE|GT|G|T|E)?\b"))
            { var r = R("настольный Ryzen (AM4 / AM5)", new Span(30, 300, 88), new Span(20, 250, 75), new Span(30, 300, 150)); r.Hx = true; return r; }
            return R("класс не определён", new Span(8, 120, 35), new Span(10, 150, 60), new Span(15, 200, 90));
        }

        public static Span SpanOf(Range r, int k) { return k == 1 ? r.Tdc : k == 2 ? r.Edc : r.Ppt; }

        // families where writing was tried on a real machine (13.11: only the R7000P's)
        public static bool Verified(string codename) { return codename == "DragonRange"; }

        // the saved value of limit k (0 = not saved: left alone)
        public static int Saved(Settings s, int k) { return k == 0 ? s.AmdPpt : k == 1 ? s.AmdTdc : k == 2 ? s.AmdEdc : k == 3 ? s.AmdStapm : k == 4 ? s.AmdFast : s.AmdSlow; }
        public static void Save(Settings s, int k, int v)
        {
            switch (k) { case 0: s.AmdPpt = v; break; case 1: s.AmdTdc = v; break; case 2: s.AmdEdc = v; break; case 3: s.AmdStapm = v; break; case 4: s.AmdFast = v; break; default: s.AmdSlow = v; break; }
        }

        // what this processor's SMU accepts and the limits it enforces now; nothing is written
        public static Info Check()
        {
            var i = new Info();
            if (Hw.SimCpu)
            {
                // another chip: its limits are unknown; the kind follows its class (HX = desktop silicon, else an APU)
                i.Codename = "(симуляция)"; i.Apu = !ClassOf(Hw.Cpu).Hx;
                for (int k = 0; k < 6; k++) i.Can[k] = IsApuLimit(k) == i.Apu;
                return i;
            }
            try
            {
                i.Codename = Smu.Code;
                if (i.Codename.Length == 0) { i.Error = "Этого процессора нет в таблице программы"; return i; }
                if (!Tctl.Available) { i.Error = "Нужен PawnIO (вкладка «Компоненты»)"; return i; }
                i.Apu = Smu.Apu;
                i.Can = Smu.Can();
                i.Path = "pawnio";
                i.Limits = Smu.Limits();
            }
            catch (Exception ex) { i.Error = ex.Message; }
            return i;
        }

        // writes the non-zero ones (values in Keys order); returns «STAPM 25 W ✓ · FAST 35 W ✗ TIMEOUT …» or why not
        public static string Apply(int[] v, string why)
        {
            if (Hw.Simulated) return "Симуляция другого процессора: в процессор ничего не пишется";
            if (v.All(x => x <= 0)) return "Нечего применять";
            try
            {
                // «PPT 45 W ✓», or with the SMU's answer when it did not take it (13.11 on the R7000P: PPT 50 W → OK)
                var said = new System.Collections.Generic.List<string>(); var set = new System.Collections.Generic.List<string>();
                for (int k = 0; k < 6; k++)
                {
                    if (v[k] <= 0) continue;
                    var st = Smu.SetLimit(k, v[k]);
                    set.Add(Keys[k] + "=" + st);
                    said.Add(Keys[k] + " " + v[k] + " " + Units[k] + (st == "OK" ? " ✓" : " ✗ " + st));
                }
                var res = string.Join(" · ", said);
                Store.Log("лимиты AMD (" + why + "): " + res + " [" + string.Join(",", set) + "]");
                return res;
            }
            catch (Exception ex) { Store.Log("лимиты AMD: " + ex.Message); return "Не удалось: " + ex.Message; }
        }

        // the saved limits again (start, after sleep), only where this page is the power page
        public static void ApplySaved(Settings s, string why)
        {
            if (Hw.LenovoPower || !Hw.Amd || !s.AmdPowerOn || !s.Expert || Safety.SafeStart) return; // safe start: «Применить сейчас» brings them
            var v = Enumerable.Range(0, 6).Select(k => Saved(s, k)).ToArray();
            if (v.All(x => x <= 0)) return;
            Task.Run(() => Apply(v, why));
        }
    }

    // «Питание» on an AMD laptop without Lenovo's interface: the processor's limits, applied on demand and, if wanted,
    // at every start and after sleep; the Windows power plan
    class AmdPowerPage : Page
    {
        static readonly string[] Titles = { "PPT — мощность процессора", "TDC — длительный ток", "EDC — пиковый ток", "STAPM — средняя мощность", "Быстрый предел (fast PPT)", "Медленный предел (slow PPT)" };
        static readonly string[] Subs =
        {
            "Предел мощности (на Dragon Range — быстрый)", "Ток ядер при долгой нагрузке", "Ток ядер на коротких всплесках",
            "Средняя мощность под долгой нагрузкой",
            "Первые секунды нагрузки", "Следующие минуты нагрузки, пока не вступит STAPM",
        };

        readonly Settings s;
        readonly Row head, infoRow, autoRow;
        readonly Row[] rows = new Row[6];
        readonly Slider[] sliders = new Slider[6];
        readonly Toggle auto = new Toggle();
        readonly FlatButton apply = new FlatButton("Применить", true), reset = new FlatButton("Сбросить");
        readonly DropButton planBtn = new DropButton();
        readonly FooterBar bar;
        readonly AmdPower.Range range;
        System.Collections.Generic.List<PowerPlan> plans = new System.Collections.Generic.List<PowerPlan>();
        AmdPower.Info info;           // what the processor said (null until it answered)
        bool[] touched = new bool[6];  // moved by the user since the page opened: only these and the saved ones are written
        bool checkedOnce;

        // 13.11: the page for every AMD laptop without Lenovo's interface. It opens for everyone and only reads: the
        // processor's class gives the slider ranges, its power table the limits it enforces now; a limit it did not
        // report stays «не менять» and is never written until the user moves it. Writing (Применить, at start, after
        // sleep) needs «Экспертные функции».
        public AmdPowerPage(Settings s)
        {
            this.s = s;
            range = AmdPower.ClassOf(Hw.Cpu);
            head = Row("Ограничения мощности процессора", "");
            infoRow = Row("Процессор", "Проверяю, что он принимает…");
            autoRow = Row("Применять при запуске и после сна", "Red Tune сам вернёт эти лимиты, когда Windows или прошивка их сбросят", auto);
            Section("Лимиты");
            for (int k = 0; k < 6; k++)
            {
                var span = AmdPower.SpanOf(range, k);
                int saved = AmdPower.Saved(s, k);
                sliders[k] = new Slider(span.Min, Math.Max(span.Max, saved), saved > 0 ? saved : span.Mid, AmdPower.Units[k]) { Width = Theme.S(330), Unset = saved <= 0 };
                rows[k] = Row(Titles[k], "", sliders[k]);
                int kk = k;
                sliders[k].Changed += (o, e) => { touched[kk] = true; UpdateState(); };
            }

            Section("Схема питания Windows");
            Row("Активная схема", "Сбалансированная, экономия, высокая производительность или схемы производителя ноутбука", planBtn);
            var open = new FlatButton("Открыть «Электропитание»");
            Row("Схемы Windows", "Создать или настроить схемы — в панели управления Windows", open);
            open.Click += (o, e) => { try { WinPower.OpenControlPanel(); } catch { } };
            planBtn.Changed += (o, e) =>
            {
                if (planBtn.Index < 0 || planBtn.Index >= plans.Count) return;
                try { WinPower.SetActive(plans[planBtn.Index].Id); Store.Log("схема питания: " + plans[planBtn.Index].Name); }
                catch (Exception ex) { Msg.Show(FindForm(), "Не удалось включить схему: " + ex.Message, "Red Tune"); }
                LoadPlans();
            };

            auto.Set(s.AmdPowerOn);
            auto.CheckedChanged += (o, e) => { s.AmdPowerOn = auto.Checked; Store.SaveSettings(s); };
            Footer = bar = new FooterBar(reset, apply);
            apply.Click += (o, e) => Apply();
            reset.Click += (o, e) => Reset();
            Hw.ExpertChanged += OnExpert;
            Disposed += (o, e) => Hw.ExpertChanged -= OnExpert;
            VisibleChanged += (o, e) =>
            {
                if (!Visible) return;
                LoadPlans();
                if (!checkedOnce) { checkedOnce = true; CheckCpu(); }
            };
            UpdateState();
        }

        void OnExpert() { try { BeginInvoke((Action)UpdateState); } catch { } }

        // an APU (STAPM / fast / slow) or desktop silicon (PPT / TDC / EDC): from the answer, before it from the class
        bool Apu { get { return info != null && info.Error == null ? info.Apu : !range.Hx; } }

        void LoadPlans()
        {
            try { plans = WinPower.List(); } catch { plans = new System.Collections.Generic.List<PowerPlan>(); }
            planBtn.Set(plans.Select(p => p.Name).ToArray(), plans.FindIndex(p => p.Active));
        }

        void CheckCpu()
        {
            Task.Run(() =>
            {
                var i = AmdPower.Check();
                try { BeginInvoke((Action)(() => { info = i; StartFromCurrent(); UpdateState(); })); } catch { }
            });
        }

        // what the processor enforces now: the sliders start there and their range grows to hold it; still nothing is
        // written until a slider is moved. An APU reports all three of its limits; on Dragon Range PPT moved the fast
        // limit (13.11), so PPT starts from it. TDC and EDC are not in the table: they stay «не менять».
        void StartFromCurrent()
        {
            if (info == null || info.Limits == null) return;
            var from = Apu ? new[] { -1, -1, -1, 0, 1, 2 } : new[] { 1, -1, -1, -1, -1, -1 };
            for (int k = 0; k < 6; k++)
            {
                if (from[k] < 0 || AmdPower.Saved(s, k) > 0 || touched[k]) continue;
                var span = AmdPower.SpanOf(range, k);
                sliders[k].SetRange(span.Min, Math.Max(span.Max, info.Limits.Max() + 10));
                sliders[k].Value = info.Limits[from[k]]; sliders[k].Unset = false;
            }
        }

        // one place decides what is shown, what is enabled and what every line says
        void UpdateState()
        {
            bool expert = s.Expert, failed = info != null && info.Error != null, apu = Apu;
            bool any = info != null && !failed && Enumerable.Range(0, 6).Any(k => info.Can[k]);

            head.SetSub(failed ? "Не удалось связаться с процессором: показываю, но не пишу"
                : info != null && !any ? "Этот процессор не принимает лимиты через SMU"
                : expert ? "Пишутся прямо в процессор (SMU), как в UXTU. Сбрасываются при перезагрузке"
                : "Сейчас только просмотр. Запись в процессор — экспертная функция: включите «Экспертные функции» в «Настройках»");

            var sb = new System.Text.StringBuilder(L.T(range.Name) + " · ");
            if (info == null) sb.Append(L.T("Проверяю, что он принимает…"));
            else if (failed) sb.Append(L.T("Не ответил: ") + info.Error);
            else
            {
                sb.Append(info.Codename + (info.Path == "pawnio" ? " · " + L.T("через PawnIO") : ""));
                sb.Append(info.Limits != null
                    ? " · " + L.T("сейчас") + ": STAPM " + info.Limits[0] + " W, " + L.T("быстрый") + " " + info.Limits[1] + " W, " + L.T("медленный") + " " + info.Limits[2] + " W"
                    : " · " + L.T("текущие лимиты не прочитались"));
                if (!AmdPower.Verified(info.Codename)) sb.Append(" · " + L.T("на этом семействе запись не проверена"));
            }
            infoRow.SetSub(sb.ToString());

            bool pending = false;
            for (int k = 0; k < 6; k++)
            {
                bool mine = AmdPower.IsApuLimit(k) == apu;
                rows[k].Visible = mine;
                bool can = info == null || failed || info.Can[k];
                sliders[k].Enabled = mine && expert && can && !failed;
                rows[k].SetSub(L.T(Subs[k]) + " · " + L.T(State(k)));
                if (mine && Wanted(k) > 0) pending = true;
            }
            auto.Enabled = expert;
            apply.Enabled = expert && any && pending;
            reset.Enabled = expert && (pending || Enumerable.Range(0, 6).Any(k => AmdPower.Saved(s, k) > 0));
            bar.SetStatus(!expert ? "Только просмотр" : !pending ? "Нечего применять: ни один лимит не сдвинут" : "Значения не применены");
            PerformLayout(); // rows shown or hidden
        }

        string State(int k)
        {
            return touched[k] ? "будет записано" : AmdPower.Saved(s, k) > 0 ? "сохранено, пишется при «Применить» и при запуске"
                : sliders[k].Unset ? "не меняется, пока не сдвинете" : "текущее значение";
        }

        // the value to write for one limit: moved now, or saved before; 0 = leave it alone (or not this kind of chip)
        int Wanted(int k)
        {
            if (AmdPower.IsApuLimit(k) != Apu) return 0;
            if (info != null && info.Error == null && !info.Can[k]) return 0;
            return touched[k] || AmdPower.Saved(s, k) > 0 ? sliders[k].Value : 0;
        }

        void Apply()
        {
            if (!s.Expert) return;
            if (!s.AmdPowerAsked)
            {
                if (!ConfirmDialog.Ask(FindForm(), "Лимиты мощности", "Лимиты пишутся прямо в процессор. Слишком высокие значения нагреют ноутбук сильнее, чем рассчитывал производитель; слишком низкие сделают его медленным.\n\nПосле перезагрузки процессор вернётся к заводским лимитам.", "Применить", "Отмена")) return;
                s.AmdPowerAsked = true;
            }
            var v = Enumerable.Range(0, 6).Select(Wanted).ToArray();
            for (int k = 0; k < 6; k++) AmdPower.Save(s, k, v[k]);
            Store.SaveSettings(s);
            touched = new bool[6];
            apply.Enabled = false; bar.SetStatus("Применяю…");
            Task.Run(() =>
            {
                var r = AmdPower.Apply(v, "вручную");
                try { BeginInvoke((Action)(() => { UpdateState(); bar.SetStatus(r); })); } catch { }
            });
        }

        // nothing saved any more, every slider back to «не менять» (or what the processor reported); the processor keeps
        // what it has until a reboot
        void Reset()
        {
            for (int k = 0; k < 6; k++) { AmdPower.Save(s, k, 0); sliders[k].Unset = true; }
            s.AmdPowerOn = false; auto.Set(false);
            Store.SaveSettings(s);
            touched = new bool[6];
            StartFromCurrent();
            UpdateState();
            bar.SetStatus("Сохранённые лимиты убраны. Заводские вернутся после перезагрузки");
        }
    }
}
