using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    class PowerPage : Page
    {
        readonly PowerService power;
        readonly Row presetRow;
        // 18.11: the page in tabs under the preset (the page had grown to forty rows in one column). A row belongs to the tab
        // it was made in (tabOf); rows that come and go for reasons of their own (the currents, the overclocking rows) say
        // so through Want, and show only while their tab is the one on screen.
        readonly PageTabs tabs = new PageTabs(0, "Процессор", "Видеокарта", "Вентиляторы", "Схемы питания", "Кривые");
        readonly System.Collections.Generic.Dictionary<Control, int> tabOf = new System.Collections.Generic.Dictionary<Control, int>();
        readonly System.Collections.Generic.Dictionary<Control, bool> wish = new System.Collections.Generic.Dictionary<Control, bool>();
        int tab, tabMark;
        readonly FooterBar bar;
        readonly FlatButton presetBtn = new FlatButton("…"), saveBtn = new FlatButton("Сохранить"), applyBtn = new FlatButton("Применить", true), resetBtn = new FlatButton("Сбросить  ▾");
        readonly Slider cpuLong, cpuShort, cpuPeak, cpuCross, cpuTemp, gpuTemp, gpuTotal;
        readonly DropButton gpuBoost = new DropButton(), gpuTgp = new DropButton(), gpuToCpu = new DropButton(), planBtn = new DropButton();
        readonly DropButton[] modePlans = { new DropButton(), new DropButton(), new DropButton() };
        // 18.11: the processor settings of a Windows plan («Схемы питания»): the clock cap, the boost mode, the energy
        // preference, the minimum state and core parking, on mains power and on battery apart. They are read from the plan
        // picked in cfgPlan and written into it at once: they belong to Windows' plan, not to the preset («Сохранить» is
        // for the preset). A slider writes when it has stood still for a moment.
        readonly DropButton cfgPlan = new DropButton(), cfgBoost = new DropButton();
        // the stronger choice stands on the right in both switches (the user's order): battery | mains, saving | balance | performance
        readonly Segmented cfgSrc = new Segmented("От батареи", "От сети"), cfgEpp = new Segmented("Экономия", "Баланс", "Производительность");
        readonly Toggle cfgPark = new Toggle();
        Slider cfgFreq, cfgMin;
        Row cfgPlanRow, cfgBoostRow, cfgEppRow, cfgParkRow;
        // 18.11: «Схема для автотюна» shows only for the tuning preset and only while Windows has no tuning plan yet
        Row tuneRow;
        FlatButton tuneBtn;
        // 18.11: the plan «Тюнинг» shown in the plan block has its settings grey and locked: Red Tune sets them. Windows can
        // still change the plan, so a change is noticed (WinPower.TuningDrift) and the row «Схема для автотюна» comes back
        // with a button that writes the values again.
        internal static bool PretendTuneDrift; // the off-screen render: as if the plan had been changed outside
        const string TuneRowSub = "Без потолка частоты и с минимумом 5 %: процессор может опускаться в самый низ и подниматься на самый верх", TuneRowDrift = "Значения схемы «Тюнинг» изменены вне Red Tune: кнопка запишет заданные для автотюна";
        internal static bool PretendNoTunePlan; // the off-screen render: the row as on a computer without the plan
        Guid cfgId;
        bool cfgLoading;
        int cfgNoCap = 6100; // the top of the clock slider: no cap
        // 18.11, later the same day: the block writes nothing by itself. A change waits in cfgEdits, per plan and power
        // source, until «Записать в схему»; «Отменить» drops what waits. (Written at once, a slider moved by accident
        // changed Windows' setting on the spot, and nothing remembered the old value.)
        // 19.11: the switches of the plan block stand in line. «От батареи | От сети» with the plan's list beside them was
        // 12 px narrower than the three-way switch of the row under it, so their left edges did not meet. The list keeps
        // its width (it stands under the lists of the rows above, the user's order); the two choices of the switch are
        // made as wide as it takes for the group to begin where the three-way switches begin. The two three-way switches
        // (Windows' power mode, the energy preference) get one width of a choice as well.
        protected override void BeforeLayout(int width)
        {
            int three = Math.Max(Math.Max(cfgMode.ItemWidth, cfgEpp.ItemWidth), cfgKind.ItemWidth);
            cfgMode.MinItemWidth = three; cfgEpp.MinItemWidth = three; cfgKind.MinItemWidth = three;
            int room = cfgMode.Width - Theme.S(8) - cfgPlan.Width;       // what is left for the two choices beside the list
            cfgSrc.MinItemWidth = Math.Max(0, (room - Theme.S(4)) / 2);  // their own names decide when that is too little
        }

        class CfgEdit { public uint Value; public uint?[] Classes; }
        class CfgTarget { public Guid Id; public bool Dc; public readonly System.Collections.Generic.Dictionary<WinPower.Cpu, CfgEdit> Edits = new System.Collections.Generic.Dictionary<WinPower.Cpu, CfgEdit>(); }
        readonly System.Collections.Generic.Dictionary<string, CfgTarget> cfgEdits = new System.Collections.Generic.Dictionary<string, CfgTarget>();
        readonly FlatButton cfgWrite = new FlatButton("Записать в схему", true), cfgUndo = new FlatButton("Отменить");
        Row cfgWriteRow;
        // 18.11: Windows' power mode in the block: it is laid over the plan's values, so it stands beside them. One for
        // mains power and one for battery (the block's own switch), shared by all plans; changed like the rest, by
        // «Записать в схему» (modeEdits: what waits, by the power source).
        readonly Segmented cfgMode = new Segmented("Эффективность", "Сбалансированный", "Производительность");
        Row cfgModeRow;
        readonly System.Collections.Generic.Dictionary<bool, Guid> modeEdits = new System.Collections.Generic.Dictionary<bool, Guid>();
        // 19.11 (the user's wish): the kind of the plan in the block («Тип схемы электропитания» of Windows, hidden in its
        // own settings). Windows lays its power mode only over plans of the balanced kind, so the mode's row under this
        // one follows it. Changed like the rest, by «Записать в схему» (kindEdits: what waits, by the plan).
        readonly Segmented cfgKind = new Segmented("Экономия", "Сбалансированная", "Производительность");
        Row cfgKindRow;
        static readonly uint[] KindValues = { 0, 2, 1 }; // Windows' numbers of the three kinds, in the buttons' order
        readonly System.Collections.Generic.Dictionary<Guid, uint> kindEdits = new System.Collections.Generic.Dictionary<Guid, uint>();
        void KindEdit(uint want)
        {
            if (cfgId == Guid.Empty) return;
            uint now;
            if (WinPower.ReadKind(cfgId, out now) && now == want) kindEdits.Remove(cfgId); else kindEdits[cfgId] = want;
            LoadCfg();
        }
        static readonly Guid[] ModeIds = { WinPower.ModeEfficiency, WinPower.ModeBalanced, WinPower.ModePerformance };
        const string CfgWriteSub = "Изменения выше попадают в схему Windows по этой кнопке или по «Сохранить» внизу";
        readonly System.Collections.Generic.Dictionary<string, uint?[]> cfgParkWas = new System.Collections.Generic.Dictionary<string, uint?[]>();
        static readonly string[] BoostNames = { "Отключён", "Включён", "Агрессивный", "Эффективный", "Эффективный агрессивный", "Агрессивный, гарант. частота", "Эфф. агрессивный, гарант. частота" };
        static readonly uint[] EppValues = { 70, 33, 0 }; // what the three buttons write, in their order
        const string CfgSub = "Запись — кнопкой ниже", CfgNone = "В этой схеме такой настройки нет";
        readonly DropButton curveBtn = new DropButton();
        readonly DropButton[] modeCurves = { new DropButton(), new DropButton(), new DropButton() };
        readonly DropButton gpuPresetBtn = new DropButton();
        readonly DropButton[] modeGpu = { new DropButton(), new DropButton(), new DropButton() };
        string[] gpuNames = new string[0];
        readonly CurveService curve;
        string[] curveNames = new string[0];
        readonly TempFanCurve fan = new TempFanCurve();
        readonly Segmented fanSeg = new Segmented("Вентилятор ЦП", "Вентилятор ГП", "Дополнительно");
        readonly FanCard fanCard;
        readonly Toggle fanMax = new Toggle(), fanOn = new Toggle();
        readonly Row fanOnRow;
        int[] editCpu = (int[])FanControl.DefaultCurve.Clone(), editGpu = (int[])FanControl.DefaultCurve.Clone(), editBoth = (int[])FanControl.DefaultCurve.Clone();
        // a curve per fan, or one for both with its temperature (ЦП / ГП / the hotter one)
        readonly Segmented fanKind = new Segmented("Две кривые", "Одна на оба"), fanSrc = new Segmented("ЦП", "ГП", "Горячее");
        static readonly string[] TabsTwo = { "Вентилятор ЦП", "Вентилятор ГП", "Дополнительно" }, TabsOne = { "Оба вентилятора", "Дополнительно" };
        bool tabsShared; // which set of tabs fanSeg shows now
        bool Shared { get { return fanKind.Selected == 1; } }
        bool OptionsTab { get { return fanSeg.Selected == (tabsShared ? 1 : 2); } }
        readonly FanControl fans;
        readonly Settings settings;
        readonly Timer fanTimer = new Timer { Interval = 1000 };
        // 16.11: TDC and EDC, the processor's two current limits, as numbers to read: "drawn now / limit". Nothing here
        // is set; the rows appear when the processor's table gives them (CoreCurve.Amps)
        readonly ValueText tdcVal = new ValueText(), edcVal = new ValueText();
        Row tdcRow, edcRow;
        bool ampsBusy;
        // advanced overclocking (LLT's «Расширенные параметры разгона»), only with the BIOS overclocking mode on
        readonly Toggle ocOn = new Toggle(), allCoreOn = new Toggle();
        readonly Slider pboScalar, pboBoost, allCore;
        readonly Row ocRow, allCoreRow;
        bool biosOc, biosOcChecked;
        const string FanOnSub = "Red Tune сам задаёт обороты по температуре раз в 0,5 с (в режиме «Свой»). Выключено — по таблице прошивки";
        PowerPreset selected, edit;
        bool loading;
        System.Collections.Generic.List<PowerPlan> plans = new System.Collections.Generic.List<PowerPlan>();

        public PowerPage(PowerService power, CurveService curve, FanControl fans, Settings settings)
        {
            this.power = power; this.curve = curve; this.fans = fans; this.settings = settings;
            presetRow = Row("Пресет своего режима", "", presetBtn);
            Add(tabs); tabs.Picked += ShowTab;
            Footer = bar = new FooterBar(resetBtn, saveBtn, applyBtn);
            gestureEnd.Tick += (o, e) => { if (Control.MouseButtons != MouseButtons.None) return; gestureEnd.Stop(); gesture = false; }; // a run of changes ends
            Disposed += (o, e) => gestureEnd.Dispose();

            tabMark = Controls.Count; // the rows from here on go to the tabs, in the order of EndTab
            cpuLong = SliderRow("Долгосрочное ограничение мощности", "Непрерывная мощность процессора", Limits.CpuLong, "W");
            cpuShort = SliderRow("Краткосрочное ограничение мощности", "Пиковая мощность на короткое время", Limits.CpuShort, "W");
            cpuPeak = SliderRow("Предельная пиковая мощность", "Максимум на мгновенных нагрузках", Limits.CpuPeak, "W");
            cpuCross = SliderRow("Долгосрочное ограничение при нагрузке ЦП+ГП", "Мощность процессора, когда работает и видеокарта", Limits.CpuCross, "W");
            cpuTemp = SliderRow("Ограничение температуры ЦП", "Выше — процессор сбрасывает частоту", Limits.CpuTemp, "°C");
            tdcRow = Row("TDC — длительный ток", "Сейчас / предел, который процессор держит долго · только для сведения", tdcVal);
            edcRow = Row("EDC — пиковый ток", "Сейчас / предел на коротких пиках · только для сведения", edcVal);
            Want(tdcRow, false); Want(edcRow, false);
            ocRow = Row("Расширенные параметры разгона", "Неверные значения могут привести к нестабильности, сбоям или к тому, что ноутбук не загрузится", ocOn);
            pboScalar = SliderRow("Множитель Precision Boost Overdrive", "Насколько PBO может выйти за заводские пределы, 1–10", new[] { 1, 10 }, "x");
            pboBoost = SliderRow("Частота ускорения Precision Boost Overdrive", "Прибавка к максимальной частоте ускорения", new[] { 0, 200 }, "МГц");
            allCoreRow = Row("Регулировка кривой ядер", "Одно смещение Curve Optimizer сразу для всех ядер, через прошивку", allCoreOn);
            allCore = SliderRow("Оптимизатор кривой все ядра", "Смещение для всех ядер, 0–20 (как в LLT): снижает напряжение и нагрев, нужна проверка стабильности", new[] { 0, 20 }, "");
            foreach (var r in new Control[] { ocRow, pboScalar.Parent, pboBoost.Parent, allCoreRow, allCore.Parent }) Want(r, false);
            ocOn.CheckedChanged += (s, e) => { if (loading) return; UpdateOc(true); Edited(); };
            allCoreOn.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                if (allCoreOn.Checked && !ConfirmAllCore()) allCoreOn.Set(false);
                UpdateOc(true); Edited();
            };
            foreach (var sl in new[] { pboScalar, pboBoost, allCore }) sl.Changed += (s, e) => Edited();

            EndTab(0); // «Процессор» ends, «Видеокарта»: its limits (the GPU presets join them below)
            Row("Dynamic Boost", "Добавка к видеокарте по загрузке процессора", gpuBoost);
            Row("Настраиваемый TGP", "Мощность видеокарты сверх базовой", gpuTgp);
            gpuTemp = SliderRow("Ограничение температуры ГП", "Выше — видеокарта сбрасывает частоту", Limits.GpuTemp, "°C");
            gpuTotal = SliderRow("Общая целевая мощность процессора от сети", "Порог, с которого мощность перераспределяется на видеокарту", Limits.GpuTotal, "W");
            Row("Dynamic Boost ГП на ЦП", "Сколько видеокарта может забрать у процессора", gpuToCpu);

            EndTab(1);
            fanOnRow = Row("Своя кривая вентиляторов", FanOnSub, fanOn);
            fanKind.Selected = 0; fanSrc.Selected = 2;
            Row("Кривые вентиляторов", "Две — у каждого вентилятора своя, по его температуре. Одна — оба крутятся одинаково", fanKind);
            fanSeg.Selected = 0;
            fanCard = Add(new FanCard(fanSeg, fan, fanSrc));
            fanCard.Default.Click += (s, e) => { editCpu = (int[])FanControl.DefaultCurve.Clone(); editGpu = (int[])FanControl.DefaultCurve.Clone(); editBoth = (int[])FanControl.DefaultCurve.Clone(); ShowFanCurve(); fanMax.Set(false); Edited(); };
            // the last tab: the extras, shared by all presets, in place of the curve
            fanSeg.Changed += (s, e) => { fanCard.ShowOptions(OptionsTab); ShowFanCurve(); ShowFanLive(); Relayout(); };
            fanKind.Changed += (s, e) => { SetFanTabs(); Edited(); };
            fanSrc.Changed += (s, e) => { ShowFanLive(); Edited(); };
            Row("Максимальная скорость", "Все вентиляторы на полную. Долго так держать — изнашивает вентиляторы", fanMax);
            FanOption("Страховка от перегрева", "С 75 °C обороты не ниже растущего минимума, к 90 °C — не ниже 80 %", () => settings.FanSafety, v => settings.FanSafety = v);
            FanOption("Аварийный режим", "От 95 °C на ЦП или ГП — оба вентилятора на максимум", () => settings.FanCritical, v => settings.FanCritical = v);
            FanOption("Гистерезис", "Раскрученный вентилятор держит обороты на 3 °C дольше — не дёргается туда-сюда", () => settings.FanHyst, v => settings.FanHyst = v);
            FanOption("Сглаживание температуры", "Не реагировать на короткие скачки (скользящее среднее)", () => settings.FanEma, v => settings.FanEma = v);
            FanOption("Плавный спад", "Обороты снижаются не быстрее 400 об/мин в секунду", () => settings.FanGlide, v => settings.FanGlide = v);
            FanOption("Разгон при старте", "Короткий толчок 3000 об/мин, когда вентилятор трогается с места", () => settings.FanBoost, v => settings.FanBoost = v);
            FanOption("Синхронизация", "Оба вентилятора на обороты того, кому нужнее", () => settings.FanSync, v => settings.FanSync = v);
            FanOption("Упреждение", "Температура растёт быстрее 5 °C/с — раскрутить заранее, как через 2 с", () => settings.FanPredict, v => settings.FanPredict = v);

            EndTab(2);
            Row("Схема для этого пресета", "Включается вместе с пресетом", planBtn);
            Section("Схемы для других режимов");
            var names = new[] { "Тихий", "Баланс", "Макс" };
            for (int i = 0; i < 3; i++) { Row(names[i], null, modePlans[i]); int k = i; modePlans[i].Changed += (s, e) => SaveModePlan(k); }
            Section("Настройки процессора в схеме");
            cfgSrc.Selected = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline ? 0 : 1;
            cfgPlanRow = Row("Схема", CfgSub, cfgSrc, cfgPlan); // the plan on the right, under the buttons of the rows above (the user's order)
            cfgKindRow = Row("Тип схемы", "Режим питания Windows действует только на сбалансированные схемы", cfgKind);
            cfgModeRow = Row("Режим питания Windows", "Общий для всех сбалансированных схем; от сети и от батареи — свой", cfgMode);
            cfgFreq = SliderRow("Потолок частоты", "Выше процессор не поднимается. Справа до упора — без потолка", new[] { 1000, cfgNoCap }, "МГц");
            cfgFreq.Step = 100; cfgFreq.Caption = v => v >= cfgNoCap ? "нет" : null;
            cfgBoostRow = Row("Буст", "Как процессор поднимает частоту выше базовой", cfgBoost);
            cfgEppRow = Row("Предпочтение энергии", CfgNone, cfgEpp);
            cfgMin = SliderRow("Минимальное состояние процессора", "Ниже этой доли своей частоты процессор не опускается", new[] { 0, 100 }, "%");
            cfgParkRow = Row("Запретить парковку ядер", CfgNone, cfgPark);
            cfgWriteRow = Row("Запись в схему", CfgWriteSub, cfgUndo, cfgWrite);
            cfgWrite.Enabled = cfgUndo.Enabled = false;
            // the two rows about plans as a whole stand under the settings of a plan (the user's order, 18.11)
            var open = new FlatButton("Открыть «Электропитание»");
            Row("Схемы Windows", "Создать или настроить схемы — в панели управления Windows", open);
            // 17.11: a plan for the auto-tune and for checks of the curve, made by the program on this button (WinPower.MakeTuningPlan)
            tuneBtn = new FlatButton("Создать «Тюнинг»");
            tuneRow = Row("Схема для автотюна", TuneRowSub, tuneBtn);
            tuneBtn.Click += (s, e) => { if (HaveTunePlan()) RestoreTuningPlan(); else MakeTuningPlan(); };
            cfgBoost.Set(BoostNames, -1);
            cfgPlan.Changed += (s, e) => { if (cfgPlan.Index >= 0 && cfgPlan.Index < plans.Count) cfgId = plans[cfgPlan.Index].Id; LoadCfg(); };
            cfgSrc.Changed += (s, e) => LoadCfg();
            cfgFreq.Changed += (s, e) => SetEdit(WinPower.Cpu.FreqMax, (uint)(cfgFreq.Value >= cfgNoCap ? 0 : cfgFreq.Value), null, false);
            cfgMin.Changed += (s, e) => SetEdit(WinPower.Cpu.MinState, (uint)cfgMin.Value, null, false);
            cfgBoost.Changed += (s, e) => { if (cfgBoost.Index >= 0) SetEdit(WinPower.Cpu.Boost, (uint)cfgBoost.Index, null, true); };
            // the button the plan's own value lights is no change: «Баланс» pressed again over a plan with 25 leaves the 25
            cfgEpp.Changed += (s, e) => { if (cfgEpp.Selected < 0) return; uint now; SetEdit(WinPower.Cpu.Epp, Cfg(WinPower.Cpu.Epp, out now) && EppButton(now) == cfgEpp.Selected ? now : EppValues[cfgEpp.Selected], null, true); };
            cfgPark.CheckedChanged += (s, e) => { if (!cfgLoading) ParkEdit(cfgPark.Checked); };
            cfgKind.Changed += (s, e) => { if (!cfgLoading && cfgKind.Selected >= 0) KindEdit(KindValues[cfgKind.Selected]); };
            cfgMode.Changed += (s, e) =>
            {
                if (cfgLoading || cfgMode.Selected < 0) return;
                Guid now, want = ModeIds[cfgMode.Selected]; bool dc = CfgDc;
                if (WinPower.ReadMode(dc, out now) && now == want) modeEdits.Remove(dc); else modeEdits[dc] = want;
                LoadCfg();
            };
            cfgWrite.Click += (s, e) => WriteCfgEdits();
            cfgUndo.Click += (s, e) => UndoCfgEdits();
            planBtn.Changed += (s, e) => FollowCfg();

            EndTab(3);
            Row("Кривая для этого пресета", "Включается вместе с пресетом и становится активной (вкладка «Кривая»)", curveBtn);
            Section("Кривые для других режимов");
            for (int i = 0; i < 3; i++) { Row(names[i], "Включается при переходе в этот режим (Fn+Q, главная)", modeCurves[i]); int k = i; modeCurves[i].Changed += (s, e) => SaveModeCurve(k); }

            // the «Видеокарта» presets (13.11): a GPU curve and memory clock per power preset and per mode
            EndTab(4);
            if (Hw.Nvidia)
            {
                Section("Пресет видеокарты");
                Row("Пресет видеокарты для этого пресета", "Кривая и частота памяти из раздела «Видеокарта» — применяются вместе с пресетом", gpuPresetBtn);
                Section("Пресеты видеокарты для других режимов");
                for (int i = 0; i < 3; i++) { Row(names[i], "Применяется при переходе в этот режим (Fn+Q, главная)", modeGpu[i]); int k = i; modeGpu[i].Changed += (s, e) => SaveModeGpu(k); }
            }

            EndTab(1); // the GPU presets, made last, belong to «Видеокарта»
            ShowTab(0);

            gpuBoost.Set(Limits.GpuBoostSteps.Select(v => v + " W").ToArray(), 0);
            gpuTgp.Set(Limits.GpuTgpSteps.Select(v => v + " W").ToArray(), 0);
            gpuToCpu.Set(Limits.GpuToCpuSteps.Select(v => v + " W").ToArray(), 0);
            foreach (var sl in new[] { cpuLong, cpuShort, cpuPeak, cpuCross, cpuTemp, gpuTemp, gpuTotal }) sl.Changed += (s, e) => Edited();

            // 14.11: the processor's power limits keep their order while one of them is moved:
            //   ЦП+ГП (долгий при нагрузке на видеокарту) ≤ долгий ≤ краткий ≤ пиковый.
            // A limit moved up takes the larger ones with it once it reaches them; moved down, the smaller ones. They are
            // measured from where they stood when the slider was taken (held), so going back lets them return.
            var chain = new[] { cpuCross, cpuLong, cpuShort, cpuPeak };
            int[] held = null;
            foreach (var sl in chain)
            {
                var me = sl;
                me.Grabbed += (s, e) => held = chain.Select(c => c.Value).ToArray();
                me.Changed += (s, e) =>
                {
                    if (loading) return;
                    if (held == null) held = chain.Select(c => c.Value).ToArray();
                    int k = Array.IndexOf(chain, me), v = me.Value;
                    for (int j = 0; j < chain.Length; j++)
                        if (j > k) chain[j].Value = Math.Max(held[j], v);
                        else if (j < k) chain[j].Value = Math.Min(held[j], v);
                };
            }
            foreach (var d in new[] { gpuBoost, gpuTgp, gpuToCpu, planBtn, curveBtn, gpuPresetBtn }) d.Changed += (s, e) => Edited();
            fan.Changed += (s, e) => Edited();
            fanMax.CheckedChanged += (s, e) => Edited();
            fanOn.CheckedChanged += (s, e) => Edited();
            fan.MaxRpm = fans.MaxRpm; fan.MinRpm = fans.MinRpm;
            fanTimer.Tick += (s, e) => { ShowFanLive(); ShowAmps(); };

            presetBtn.Click += (s, e) => PresetMenu();
            saveBtn.Click += (s, e) => { Collect(); SaveEdit(); SaveCfg(); };
            applyBtn.Click += (s, e) => Apply();
            resetBtn.Click += (s, e) => ResetMenu();
            open.Click += (s, e) => { try { WinPower.OpenControlPanel(); } catch { } };

            LoadPlans();
            Select(power.Active);
            VisibleChanged += (s, e) => { if (Visible) { LoadPlans(); FollowActive(); Edited(); ShowFanLive(); ShowAmps(); fanTimer.Start(); CheckBiosOc(); } else fanTimer.Stop(); };
            // another preset became active while the page is open: follow it (a mode change alone doesn't move the editor)
            power.Changed += () => { if (Visible && power.Data.Active != followed) FollowActive(); };
            followed = power.Data.Active;
        }

        // ---------- the tabs ----------
        void EndTab(int i) { for (int k = tabMark; k < Controls.Count; k++) tabOf[Controls[k]] = i; tabMark = Controls.Count; }
        bool Wanted(Control c) { bool w; return !wish.TryGetValue(c, out w) || w; }
        void Want(Control c, bool on) { wish[c] = on; int t; c.Visible = on && (!tabOf.TryGetValue(c, out t) || t == tab); }

        // the off-screen render (RT_FAN_ON=1): the custom fan curve switched on in the editor, as if clicked; nothing is saved
        internal void PretendFanCurveOn() { fanOn.Checked = true; }

        public void ShowTab(int i)
        {
            if (i < 0 || i > 4) return;
            tab = i; tabs.Selected = i;
            SuspendLayout();
            foreach (var kv in tabOf) kv.Key.Visible = kv.Value == i && Wanted(kv.Key);
            ResumeLayout();
            Relayout();
            AutoScrollPosition = new System.Drawing.Point(0, 0);
        }

        string followed; // the active preset the editor last followed

        // the page is made once and kept: each time it shows, the editor follows the preset that is active now
        // (switched on the home page meanwhile), unless it holds unsaved changes of the one it shows
        void FollowActive()
        {
            followed = power.Data.Active;
            if (loading || edit == null || selected == null) return;
            var a = power.Active;
            if (a == null || a == selected) return;
            Collect();
            if (!edit.SameAs(selected)) return;
            Select(a);
        }

        // the overclocking rows exist only when the BIOS allows it (GetBIOSOCMode = 3)
        void CheckBiosOc()
        {
            if (biosOcChecked) return;
            biosOcChecked = true;
            Task.Run(() =>
            {
                bool on = Lenovo.BiosOcOn;
                try { BeginInvoke((Action)(() => { biosOc = on; UpdateOc(); })); } catch { }
            });
        }

        // slide: the change was made by hand on a switch, so the rows slide out or back (14.11); when the page loads they
        // are simply there
        void UpdateOc(bool slide = false)
        {
            Want(ocRow, biosOc);
            bool on = biosOc && ocOn.Checked, core = on && allCoreOn.Checked;
            var want = new[] { pboScalar.Parent, pboBoost.Parent, allCoreRow, allCore.Parent };
            var to = new[] { on, on, on, core };
            if (slide)
            {
                var show = want.Where((c, i) => to[i] && !Wanted(c)).ToArray();
                var hide = want.Where((c, i) => !to[i] && Wanted(c)).ToArray();
                if (show.Length > 0) { foreach (var c in show) wish[c] = true; if (tab == 0) Slide(show, true); return; }
                if (hide.Length > 0) { foreach (var c in hide) wish[c] = false; if (tab == 0) Slide(hide, false); return; }
            }
            for (int i = 0; i < want.Length; i++) Want(want[i], to[i]);
            Relayout();
        }

        // the per-core curve from «Кривая» and the firmware's all-core offset write the same thing: warn before both run
        bool ConfirmAllCore()
        {
            var cp = curve.Active;
            bool tuned = cp != null && !cp.BuiltIn && cp.Values.Any(v => v != 0);
            bool live = curve.Live != null && curve.Live.Any(v => v != 0);
            if (!tuned && !live) return true;
            var v = tuned ? cp.Values : curve.Live;
            var text = "У тебя уже настроена кривая по ядрам" + (tuned ? " — «" + cp.Name + "»" : "") + ": от " + v.Min() + " до " + v.Max() + ". " +
                "Red Tune ставит её при запуске, от зарядки и после сна.\n\n" +
                "Оптимизатор кривой для всех ядер задаёт одно значение сразу всем ядрам через прошивку и перебьёт твою кривую при каждом применении этого пресета. " +
                "Потом Red Tune вернёт свою — они будут спорить, и что окажется на ядрах, зависит от того, кто записал последним.\n\n" +
                "Точно включить?";
            // the safe answer is the highlighted one (Enter keeps the curve)
            return ConfirmDialog.AskSecond(FindForm(), "Кривая уже настроена", text, "Оставить мою кривую", "Всё равно включить");
        }

        void FanOption(string title, string sub, Func<bool> get, Action<bool> set)
        {
            var t = new Toggle();
            fanCard.AddOption(new Row(title, sub, t) { BackColor = Theme.Card });
            t.Set(get());
            t.CheckedChanged += (s, e) => { set(t.Checked); Store.SaveSettings(settings); };
        }

        void ShowFanCurve() { fan.Points = tabsShared ? editBoth : fanSeg.Selected == 1 ? editGpu : editCpu; fan.Invalidate(); }

        // one curve or two: the tabs over the graph follow («Оба вентилятора» | «Дополнительно» or ЦП | ГП | «Дополнительно»),
        // the extras stay open if they were
        void SetFanTabs()
        {
            bool wasOptions = OptionsTab;
            tabsShared = Shared;
            fanSeg.SetItems(tabsShared ? TabsOne : TabsTwo);
            fanSeg.Selected = wasOptions ? (tabsShared ? 1 : 2) : 0;
            fanCard.SetShared(tabsShared);
            fanCard.ShowOptions(OptionsTab);
            ShowFanCurve(); ShowFanLive(); Relayout();
        }

        // TDC and EDC once a second while the page is open: read off the UI thread (the table goes through the driver),
        // shown as "now / limit". The rows stay hidden where the table does not give them.
        void ShowAmps()
        {
            if (ampsBusy || !Visible) return;
            ampsBusy = true;
            Task.Run(() =>
            {
                CoreCurve.AmpsNow a = null;
                try { a = CoreCurve.Amps(); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        ampsBusy = false;
                        if (a == null) return; // a read that failed now and then: the last numbers stay
                        tdcVal.Set(Math.Round(a.Tdc) + " / " + Math.Round(a.TdcLimit) + " A");
                        edcVal.Set(Math.Round(a.Edc) + " / " + Math.Round(a.EdcLimit) + " A");
                        if (!Wanted(tdcRow)) { Want(tdcRow, true); Want(edcRow, true); Relayout(); }
                    }));
                }
                catch { ampsBusy = false; }
            });
        }

        // the fan of the open tab: its temperature and RPM (one curve: the chosen temperature, the faster fan); the row says who drives the fans now
        void ShowFanLive()
        {
            int i = fanSeg.Selected == 1 ? 1 : 0;
            if (tabsShared) { fan.LiveTemp = FanControl.SharedTemp(Math.Max(0, fanSrc.Selected), fans.CpuTemp, fans.GpuTemp); fan.LiveRpm = Math.Max(fans.Rpm[0], fans.Rpm[1]); }
            else { fan.LiveTemp = i == 0 ? fans.CpuTemp : fans.GpuTemp; fan.LiveRpm = fans.Rpm[i]; }
            fan.MaxRpm = fans.MaxRpm; fan.MinRpm = fans.MinRpm;
            fan.Invalidate();
            var p = power.Active;
            string who = fans.Active ? "Сейчас кривая Red Tune: ЦП " + fans.Rpm[0] + ", ГП " + fans.Rpm[1] + " об/мин"
                : p != null && p.FanCurveOn && p.FanMax ? "Сейчас полная скорость"
                : p != null && p.FanCurveOn && power.Mode != Lenovo.Custom ? "Включится в режиме «Свой»; сейчас вентиляторами управляет прошивка" : null;
            fanOnRow.SetSub(who ?? FanOnSub);
        }

        Slider SliderRow(string title, string sub, int[] range, string unit)
        {
            var s = new Slider(range[0], range[1], range[0], unit) { Width = Theme.S(330), WarpToKnob = true }; // a press puts the pointer onto the knob (20.11)
            Row(title, sub, s);
            return s;
        }

        // ---------- the processor settings of a plan ----------
        bool CfgDc { get { return cfgSrc.Selected == 0; } }
        public void ShowCfgSource(bool battery) { cfgSrc.Selected = battery ? 0 : 1; LoadCfg(); } // for the off-screen render
        public void ShowCfgPlan(string name) { int i = plans.FindIndex(x => x.Name == name); if (i < 0) return; cfgId = plans[i].Id; ShowCfgPlans(); LoadCfg(); } // the same: the plan block on that plan

        // the plan the block shows: the one of the preset on the page; the active one when the preset changes no plan
        void FollowCfg()
        {
            Guid g;
            cfgId = Guid.TryParse(PlanId(planBtn.Index), out g) ? g : WinPower.Active();
            ShowCfgPlans(); LoadCfg();
        }

        void ShowCfgPlans()
        {
            if (!plans.Exists(p => p.Id == cfgId)) cfgId = WinPower.Active();
            cfgPlan.Set(plans.Select(p => p.Name + (p.Active ? " · активная" : "")).ToArray(), plans.FindIndex(p => p.Id == cfgId));
        }

        bool Cfg(WinPower.Cpu what, out uint v) { v = 0; return cfgId != Guid.Empty && WinPower.ReadCpu(cfgId, what, CfgDc, out v); }

        string CfgKey(Guid id, bool dc) { return id + (dc ? "|d" : "|a"); }
        static int EppButton(uint v) { return v <= 12 ? 2 : v < 50 ? 1 : 0; }

        // a change that waits for «Записать в схему», for the plan and the power source on screen
        bool Pending(WinPower.Cpu what, out CfgEdit e)
        {
            e = null; CfgTarget t;
            return cfgEdits.TryGetValue(CfgKey(cfgId, CfgDc), out t) && t.Edits.TryGetValue(what, out e);
        }

        // the plan's values into the controls, and over them the changes that wait; nothing is written
        void LoadCfg()
        {
            cfgLoading = true;
            try
            {
                uint v; CfgEdit pe;
                // the plan's kind, and over it the change that waits
                uint kind = 0; bool kindRead = cfgId != Guid.Empty && WinPower.ReadKind(cfgId, out kind);
                {
                    uint kw; if (kindRead && kindEdits.TryGetValue(cfgId, out kw)) kind = kw;
                    cfgKind.Selected = kindRead ? Array.IndexOf(KindValues, kind) : -1; cfgKind.Enabled = kindRead;
                }
                // Windows' power mode for this power source, and what it holds over a plan
                {
                    bool dc = CfgDc; Guid mode, waits;
                    // 19.11 (the user's wish): the row is there only for a plan Windows lays its mode over — one of the
                    // balanced kind; for the others («Max», «Тюнинг») the mode changes nothing and the row only confused
                    // (the kind picked in the row above counts at once, before it is written: the row follows the button)
                    bool takes = cfgId == Guid.Empty || (kindRead ? kind == 2 : WinPower.TakesMode(cfgId, dc));
                    if (Wanted(cfgModeRow) != takes) { Want(cfgModeRow, takes); Relayout(); }
                    bool read = WinPower.ReadMode(dc, out mode);
                    if (modeEdits.TryGetValue(dc, out waits)) mode = waits;
                    cfgMode.Selected = read ? Array.IndexOf(ModeIds, mode) : -1; cfgMode.Enabled = read;
                    string holds = null;
                    if (read && mode != Guid.Empty)
                    {
                        uint f, e, mn;
                        bool hf = WinPower.ReadModeValue(mode, WinPower.Cpu.FreqMax, dc, out f), he = WinPower.ReadModeValue(mode, WinPower.Cpu.Epp, dc, out e), hm = WinPower.ReadModeValue(mode, WinPower.Cpu.MinState, dc, out mn);
                        if (hf && he && hm) holds = "Держит поверх «Сбалансированных» схем: " + (f == 0 ? "без потолка частоты, предпочтение энергии " : "потолок частоты " + f + " МГц, предпочтение энергии ") + e + ", минимальное состояние " + mn + " %";
                    }
                    if (holds != cfgModeRow.Extra) { cfgModeRow.SetExtra(holds); Relayout(); }
                }
                if (Cfg(WinPower.Cpu.FreqMax, out v))
                {
                    if (Pending(WinPower.Cpu.FreqMax, out pe)) v = pe.Value;
                    cfgNoCap = Math.Max(6100, (int)Math.Min(20000, v + 199) / 100 * 100);
                    cfgFreq.SetRange(v > 0 && v < 1000 ? (int)v : 1000, cfgNoCap);
                    cfgFreq.Value = v == 0 ? cfgNoCap : (int)Math.Min(20000, v); cfgFreq.Enabled = true; cfgFreq.Invalidate();
                }
                else cfgFreq.Enabled = false;
                if (Cfg(WinPower.Cpu.Boost, out v))
                {
                    if (Pending(WinPower.Cpu.Boost, out pe)) v = pe.Value;
                    cfgBoost.Set(BoostNames, v < BoostNames.Length ? (int)v : -1); cfgBoost.Enabled = true;
                }
                else { cfgBoost.Set(BoostNames, -1); cfgBoost.Enabled = false; }
                if (Cfg(WinPower.Cpu.Epp, out v))
                {
                    cfgEppRow.SetSub("В схеме сейчас: " + v + " (0 — скорость, 100 — экономия)");
                    if (Pending(WinPower.Cpu.Epp, out pe)) v = pe.Value;
                    cfgEpp.Selected = EppButton(v); cfgEpp.Enabled = true;
                }
                else { cfgEpp.Selected = -1; cfgEpp.Enabled = false; cfgEppRow.SetSub(CfgNone); }
                if (Cfg(WinPower.Cpu.MinState, out v))
                {
                    if (Pending(WinPower.Cpu.MinState, out pe)) v = pe.Value;
                    cfgMin.Value = (int)Math.Min(100, v); cfgMin.Enabled = true;
                }
                else cfgMin.Enabled = false;
                if (Cfg(WinPower.Cpu.MinCores, out v))
                {
                    bool waits = Pending(WinPower.Cpu.MinCores, out pe);
                    if (waits) v = pe.Value;
                    cfgPark.Set(v >= 100); cfgPark.Enabled = true;
                    cfgParkRow.SetSub(waits ? (v >= 100 ? "Не записано: после записи Windows не будет парковать ядра" : "Не записано: после записи Windows сможет парковать ядра")
                        : v >= 100 ? "Все ядра всегда в работе: Windows их не паркует" : "Сейчас Windows может парковать ядра; минимум ядер без парковки, %: " + v);
                }
                else { cfgPark.Set(false); cfgPark.Enabled = false; cfgParkRow.SetSub(CfgNone); }
                // the plan «Тюнинг»: its values are Red Tune's, the rows are grey and locked
                bool tunePlan = CfgIsTunePlan();
                foreach (var r in new Control[] { cfgFreq.Parent, cfgBoostRow, cfgEppRow, cfgMin.Parent, cfgParkRow }) if (r.Enabled == tunePlan) r.Enabled = !tunePlan;
                cfgPlanRow.SetSub(tunePlan ? "Схема автотюна: значения задаёт Red Tune" : CfgSub);
                // a line of its own under the row, as wide as the row: beside the two controls it did not fit
                var extra = tunePlan && TuneDrift() ? "Значения отличаются от заданных для автотюна: строка «Схема для автотюна» ниже запишет их заново" : null;
                if (extra != cfgPlanRow.Extra) { cfgPlanRow.SetExtra(extra); Relayout(); }
            }
            finally { cfgLoading = false; }
            ShowCfgDirty();
            ShowTuneRow();
        }

        void WriteError(uint rc) { cfgPlanRow.SetSub("Windows не дала записать значение, код " + rc); }

        // A change made in the block: it waits. The value the plan already has is no change and waits for nothing.
        // classes: the values class by class when they differ («разрешить парковку» puts back what the plan had).
        // reload: the controls are read again (not while a slider is being dragged).
        void SetEdit(WinPower.Cpu what, uint value, uint?[] classes, bool reload)
        {
            if (cfgLoading || cfgId == Guid.Empty) return;
            bool dc = CfgDc; string key = CfgKey(cfgId, dc);
            CfgTarget t;
            if (!cfgEdits.TryGetValue(key, out t)) cfgEdits[key] = t = new CfgTarget { Id = cfgId, Dc = dc };
            uint now;
            if (classes == null && WinPower.ReadCpu(cfgId, what, dc, out now) && now == value) t.Edits.Remove(what);
            else t.Edits[what] = new CfgEdit { Value = value, Classes = classes };
            if (t.Edits.Count == 0) cfgEdits.Remove(key);
            if (reload) LoadCfg(); else ShowCfgDirty();
        }

        // «Запретить парковку»: 100 % of the cores as the minimum that stays unparked, for every class of cores. Switched
        // off, the plan gets back what it had when the ban was written from here; when that is not known, what Windows' own
        // «Balanced» plan has out of the box; and half of the cores when that forbids parking too.
        void ParkEdit(bool forbid)
        {
            if (cfgId == Guid.Empty) return;
            bool dc = CfgDc;
            var now = WinPower.ReadCpuClasses(cfgId, WinPower.Cpu.MinCores, dc);
            if (now[0] == null) return;
            if (forbid == (now[0] >= 100)) { SetEdit(WinPower.Cpu.MinCores, now[0].Value, null, true); return; } // as the plan is: nothing waits
            if (forbid) { SetEdit(WinPower.Cpu.MinCores, 100, null, true); return; }
            uint?[] back;
            if (!cfgParkWas.TryGetValue(CfgKey(cfgId, dc), out back) || back[0] == null || back[0] >= 100) back = WinPower.DefaultCpuClasses(WinPower.Cpu.MinCores, dc);
            if (back[0] == null || back[0] >= 100) { back = (uint?[])back.Clone(); back[0] = 50; }
            SetEdit(WinPower.Cpu.MinCores, back[0].Value, back, true);
        }

        // «Записать в схему»: everything that waits goes to its plan and power source
        void WriteCfgEdits()
        {
            uint rc = 0;
            foreach (var kv in cfgEdits.ToList())
            {
                var t = kv.Value;
                if (!plans.Exists(p => p.Id == t.Id)) continue; // the plan is gone
                foreach (var e in t.Edits)
                {
                    if (e.Key == WinPower.Cpu.MinCores && e.Value.Value >= 100) cfgParkWas[kv.Key] = WinPower.ReadCpuClasses(t.Id, e.Key, t.Dc); // what the plan had before the ban
                    uint r = e.Value.Classes != null ? WinPower.WriteCpuClasses(t.Id, e.Key, t.Dc, e.Value.Classes) : WinPower.WriteCpu(t.Id, e.Key, t.Dc, e.Value.Value);
                    if (r != 0) rc = r;
                }
            }
            cfgEdits.Clear();
            foreach (var m in modeEdits.ToList()) { uint r = WinPower.WriteMode(m.Key, m.Value); if (r != 0) rc = r; }
            modeEdits.Clear();
            foreach (var k in kindEdits.ToList()) { if (!plans.Exists(p => p.Id == k.Key)) continue; uint r = WinPower.WriteKind(k.Key, k.Value); if (r != 0) rc = r; }
            kindEdits.Clear();
            LoadCfg();
            if (rc != 0) WriteError(rc);
        }

        void UndoCfgEdits() { cfgEdits.Clear(); modeEdits.Clear(); kindEdits.Clear(); LoadCfg(); }

        // 19.11: what waits in the block is unsaved work of the page as well: «Сохранить» and «Применить» at the bottom
        // light up from it and write it too (the row's own button is below the fold, the user looked for the bottom one)
        bool CfgDirty { get { return cfgEdits.Count > 0 || modeEdits.Count > 0 || kindEdits.Count > 0; } }
        void SaveCfg() { if (CfgDirty) WriteCfgEdits(); }

        void ShowCfgDirty()
        {
            if (cfgWriteRow == null) return;
            Edited(); // the bottom bar follows the block
            bool dirty = CfgDirty;
            if (cfgWrite.Enabled != dirty) { cfgWrite.Enabled = dirty; cfgUndo.Enabled = dirty; }
            cfgWriteRow.SetSub(dirty ? "Не записано: " + string.Join("; ", cfgEdits.Values.Select(t => { var p = plans.Find(x => x.Id == t.Id); return (p != null ? p.Name : t.Id.ToString()) + (t.Dc ? " — от батареи" : " — от сети"); }).Concat(modeEdits.Keys.Select(dc => "режим питания Windows" + (dc ? " — от батареи" : " — от сети"))).Concat(kindEdits.Keys.Select(id => { var p = plans.Find(x => x.Id == id); return (p != null ? p.Name : id.ToString()) + " — тип схемы"; }))) : CfgWriteSub);
        }

        // for --plan-ui-check: the block's own path on a plan made for the check. Changes are made as the controls make
        // them; the plan must stay as it was until «Записать в схему», take them then, and stay as it is after «Отменить».
        public string TestCfg(Guid plan)
        {
            var sb = new System.Text.StringBuilder(); int bad = 0;
            var all = (WinPower.Cpu[])Enum.GetValues(typeof(WinPower.Cpu));
            Func<bool, string> read = dc => string.Join(" ", all.Select(c => { uint v; return WinPower.ReadCpu(plan, c, dc, out v) ? v.ToString() : "-"; }));
            Action<string, bool> say = (what, ok) => { if (!ok) bad++; sb.AppendLine((ok ? "ok      " : "FAILED  ") + what); };
            LoadPlans();
            // 19.11, the bottom bar: «Сохранить» follows the plan picked for the preset and what waits in the block
            say("at the start the bottom «Сохранить» is off", !saveBtn.Enabled);
            int planWas = planBtn.Index;
            planBtn.Pick(planWas == 0 ? 1 : 0);
            say("another plan picked for the preset: «Сохранить» is on", saveBtn.Enabled);
            planBtn.Pick(planWas);
            say("the plan picked back: off again", !saveBtn.Enabled);
            cfgId = plan; ShowCfgPlans();
            string ac0 = read(false), dc0 = read(true);
            sb.AppendLine("the plan at the start (clock cap, boost, energy preference, minimum state, minimum cores): mains " + ac0 + " | battery " + dc0);
            cfgSrc.Selected = 1; LoadCfg(); // mains
            SetEdit(WinPower.Cpu.FreqMax, 3300, null, false); SetEdit(WinPower.Cpu.Boost, 4, null, true); SetEdit(WinPower.Cpu.Epp, 70, null, true); SetEdit(WinPower.Cpu.MinState, 37, null, false);
            cfgSrc.Selected = 0; LoadCfg(); // battery
            SetEdit(WinPower.Cpu.FreqMax, 2700, null, false); SetEdit(WinPower.Cpu.MinState, 12, null, false); ParkEdit(true);
            say("changes made on both sides, the plan is as it was: mains " + read(false) + " | battery " + read(true), read(false) == ac0 && read(true) == dc0);
            say("two sides wait, the buttons are on: «" + cfgWriteRow.Sub + "»", cfgEdits.Count == 2 && cfgWrite.Enabled && cfgUndo.Enabled);
            say("the bottom «Сохранить» is on as well", saveBtn.Enabled);
            cfgSrc.Selected = 1; LoadCfg();
            say("back on mains the controls show what waits: " + cfgFreq.Value + " МГц, boost " + cfgBoost.Index + ", preference button " + cfgEpp.Selected + ", minimum " + cfgMin.Value, cfgFreq.Value == 3300 && cfgBoost.Index == 4 && cfgEpp.Selected == 0 && cfgMin.Value == 37);
            // a value put back to what the plan has waits for nothing
            uint min0; WinPower.ReadCpu(plan, WinPower.Cpu.MinState, false, out min0);
            SetEdit(WinPower.Cpu.MinState, min0, null, false);
            CfgEdit gone; say("minimum state put back to the plan's " + min0 + ": it no longer waits", !Pending(WinPower.Cpu.MinState, out gone));
            SetEdit(WinPower.Cpu.MinState, 37, null, false);
            var a0 = ac0.Split(' '); var d0 = dc0.Split(' ');
            WriteCfgEdits();
            string ac1 = read(false), dc1 = read(true);
            say("«Записать в схему»: mains " + ac1 + " | battery " + dc1, ac1 == "3300 4 70 37 " + a0[4] && dc1 == "2700 " + d0[1] + " " + d0[2] + " 12 100");
            say("nothing waits, the buttons are off, the bottom «Сохранить» too", cfgEdits.Count == 0 && !cfgWrite.Enabled && !cfgUndo.Enabled && !saveBtn.Enabled);
            cfgSrc.Selected = 0; LoadCfg();
            SetEdit(WinPower.Cpu.MinState, 55, null, false); SetEdit(WinPower.Cpu.Boost, 0, null, true);
            say("a change waits again: the bottom «Сохранить» is on", saveBtn.Enabled);
            UndoCfgEdits();
            say("«Отменить»: the bottom «Сохранить» is off", !saveBtn.Enabled);
            say("«Отменить»: the plan is as written (" + read(true) + "), the slider is back at " + cfgMin.Value, read(true) == dc1 && cfgEdits.Count == 0 && cfgMin.Value == 12);
            ParkEdit(false);
            say("parking allowed again waits: «" + cfgParkRow.Sub + "»", Pending(WinPower.Cpu.MinCores, out gone) && read(true) == dc1);
            WriteCfgEdits();
            var back = WinPower.ReadCpuClasses(plan, WinPower.Cpu.MinCores, true);
            say("written: the minimum of cores is what the plan had before the ban, " + string.Join("/", back.Select(x => x == null ? "-" : x.ToString())) + " (was " + d0[4] + ")", back[0] != null && back[0].ToString() == d0[4]);
            // 19.11: the kind of the plan — waits like the rest, the mode's row follows the button at once, both sides are written
            uint k0; bool kr = WinPower.ReadKind(plan, out k0);
            say("the plan's kind is read: " + k0 + " (0 saving, 1 high performance, 2 balanced); the button shown is " + cfgKind.Selected, kr && cfgKind.Selected == Array.IndexOf(KindValues, k0));
            uint other = k0 == 2 ? 1u : 2u, kNow;
            KindEdit(other);
            say("another kind picked: it waits (the plan still has " + (WinPower.ReadKind(plan, out kNow) ? kNow.ToString() : "?") + "), «" + cfgWriteRow.Sub + "», the bottom «Сохранить» is on", kNow == k0 && kindEdits.Count == 1 && cfgWrite.Enabled && saveBtn.Enabled);
            say("the row of Windows' power mode follows the button at once: " + (Wanted(cfgModeRow) ? "shown" : "hidden"), Wanted(cfgModeRow) == (other == 2));
            UndoCfgEdits();
            say("«Отменить»: nothing waits, the row is back " + (Wanted(cfgModeRow) ? "shown" : "hidden"), kindEdits.Count == 0 && Wanted(cfgModeRow) == (k0 == 2) && cfgKind.Selected == Array.IndexOf(KindValues, k0));
            KindEdit(other); WriteCfgEdits();
            say("«Записать в схему»: the plan's kind is " + (WinPower.ReadKind(plan, out kNow) ? kNow.ToString() : "?") + ", on mains and on battery alike", kNow == other && WinPower.TakesMode(plan, false) == (other == 2) && WinPower.TakesMode(plan, true) == (other == 2) && kindEdits.Count == 0 && !saveBtn.Enabled);
            say("the other settings of the plan are as they were: mains " + read(false), read(false) == ac1);
            KindEdit(k0); WriteCfgEdits();
            say("written back: " + (WinPower.ReadKind(plan, out kNow) ? kNow.ToString() : "?"), kNow == k0);
            sb.AppendLine(bad == 0 ? "all as expected" : bad + " FAILED");
            return sb.ToString();
        }

        // The tuning plan is there: the one the button made (its id is kept in the settings), or a plan with its name
        // (made on another install of the program, or by hand).
        string TunePlanId()
        {
            Guid id;
            if (Guid.TryParse(settings.TunePlan ?? "", out id) && plans.Exists(p => p.Id == id)) return id.ToString();
            var named = plans.FirstOrDefault(p => p.Name == "Тюнинг" || p.Name == L.T("Тюнинг"));
            return named != null ? named.Id.ToString() : "";
        }

        bool HaveTunePlan() { return !PretendNoTunePlan && TunePlanId() != ""; }
        bool CfgIsTunePlan() { Guid g; return cfgId != Guid.Empty && Guid.TryParse(TunePlanId(), out g) && g == cfgId; }
        bool TuneDrift()
        {
            if (!HaveTunePlan()) return false;
            if (PretendTuneDrift) return true;
            Guid g; return Guid.TryParse(TunePlanId(), out g) && WinPower.TuningDrift(g);
        }

        // the values of the plan «Тюнинг» written again (WinPower.MakeTuningPlan does only that when the plan is there);
        // the plan picked for the preset on the page is not touched
        void RestoreTuningPlan()
        {
            try
            {
                Guid id; if (!Guid.TryParse(TunePlanId(), out id)) return;
                string report;
                WinPower.MakeTuningPlan(L.T("Тюнинг"), L.T("Red Tune: для автотюна и проверки кривой. Без потолка частоты, минимум 5 %"), id, out report);
                settings.TunePlan = id.ToString(); Store.SaveSettings(settings);
                LoadPlans();
                Msg.Show(FindForm(), L.T("Значения схемы «Тюнинг» записаны заново.") + "\n\n" + string.Join("\n", report.Split('\n').Select(L.T)), "Red Tune");
            }
            catch (Exception ex) { Msg.Show(FindForm(), L.T("Схема не создана: ") + L.T(ex.Message), "Red Tune"); }
        }

        void ShowTuneRow()
        {
            if (tuneRow == null) return;
            // made for the tuning preset while Windows has no such plan; and again, as «Вернуть значения», when the plan
            // was changed outside: in the tuning preset and wherever the block shows that plan
            bool have = HaveTunePlan(), drift = have && TuneDrift(), tunePreset = selected != null && selected.Name == PowerService.TuneName;
            bool on = tunePreset && !have || drift && (tunePreset || CfgIsTunePlan());
            string text = drift ? "Вернуть значения" : "Создать «Тюнинг»";
            if (tuneBtn.Text != text) tuneBtn.Text = text;
            tuneRow.SetSub(drift ? TuneRowDrift : TuneRowSub);
            bool was = tuneRow.Visible;
            Want(tuneRow, on);
            if (tuneRow.Visible != was) Relayout();
        }

        public void ShowPreset(string name) { var p = power.Data.Presets.FirstOrDefault(x => x.Name.StartsWith(name)); if (p != null) Select(p); } // for the off-screen render: the preset whose name starts so

        // the plan for tuning: made, or its values written again, then picked for the preset on the page (the user saves the preset)
        void MakeTuningPlan()
        {
            try
            {
                string report; Guid old; Guid.TryParse(settings.TunePlan ?? "", out old);
                var id = WinPower.MakeTuningPlan(L.T("Тюнинг"), L.T("Red Tune: для автотюна и проверки кривой. Без потолка частоты, минимум 5 %"), old, out report);
                settings.TunePlan = id.ToString(); Store.SaveSettings(settings);
                BrightnessSync.Now(); // the new plan is a copy and has its source's brightness: with the switch on it gets the screen's
                LoadPlans();
                if (edit != null) { planBtn.Set(planBtn.Items, PlanIndex(id.ToString())); Edited(); }
                Msg.Show(FindForm(), L.T("Схема «Тюнинг» готова и выбрана для этого пресета. Сохраните пресет, чтобы она включалась вместе с ним.") + "\n\n" + string.Join("\n", report.Split('\n').Select(L.T)), "Red Tune");
            }
            catch (Exception ex) { Msg.Show(FindForm(), L.T("Схема не создана: ") + L.T(ex.Message), "Red Tune"); }
        }

        void LoadPlans()
        {
            try { plans = WinPower.List(); } catch { }
            loading = true;
            var items = new[] { "Не менять" }.Concat(plans.Select(p => p.Name)).ToArray();
            for (int i = 0; i < 3; i++)
            {
                var id = i == 0 ? power.Data.PlanQuiet : i == 1 ? power.Data.PlanBalance : power.Data.PlanPerformance;
                modePlans[i].Set(items, PlanIndex(id));
            }
            if (edit != null) planBtn.Set(items, PlanIndex(edit.Plan));
            if (cfgId != Guid.Empty) { ShowCfgPlans(); LoadCfg(); }
            ShowTuneRow();
            LoadCurves();
            LoadGpuPresets();
            loading = false;
        }

        // the curve presets (they can change on the «Кривая» page)
        void LoadCurves()
        {
            curveNames = curve.Presets.Select(c => c.Name).ToArray();
            var items = new[] { "Не менять" }.Concat(curveNames).ToArray();
            for (int i = 0; i < 3; i++)
                modeCurves[i].Set(items, CurveIndex(i == 0 ? power.Data.CurveQuiet : i == 1 ? power.Data.CurveBalance : power.Data.CurvePerformance));
            if (edit != null) curveBtn.Set(items, CurveIndex(edit.Curve));
        }

        // the «Видеокарта» presets (they can change on that page)
        void LoadGpuPresets()
        {
            if (!Hw.Nvidia) return;
            GpuStore.Load();
            gpuNames = GpuStore.Presets.Select(p => p.Name).ToArray();
            var items = new[] { "Не менять" }.Concat(gpuNames).ToArray();
            for (int i = 0; i < 3; i++)
                modeGpu[i].Set(items, GpuIndex(i == 0 ? power.Data.GpuQuiet : i == 1 ? power.Data.GpuBalance : power.Data.GpuPerformance));
            if (edit != null) gpuPresetBtn.Set(items, GpuIndex(edit.GpuPreset));
        }

        int GpuIndex(string name) { int i = Array.IndexOf(gpuNames, name ?? ""); return i < 0 ? 0 : i + 1; }
        string GpuName(int index) { return index <= 0 || index > gpuNames.Length ? "" : gpuNames[index - 1]; }

        void SaveModeGpu(int k)
        {
            if (loading) return;
            var n = GpuName(modeGpu[k].Index);
            if (k == 0) power.Data.GpuQuiet = n; else if (k == 1) power.Data.GpuBalance = n; else power.Data.GpuPerformance = n;
            power.Save();
        }

        int CurveIndex(string name) { int i = Array.IndexOf(curveNames, name ?? ""); return i < 0 ? 0 : i + 1; }
        string CurveName(int index) { return index <= 0 || index > curveNames.Length ? "" : curveNames[index - 1]; }

        void SaveModeCurve(int k)
        {
            if (loading) return;
            var n = CurveName(modeCurves[k].Index);
            if (k == 0) power.Data.CurveQuiet = n; else if (k == 1) power.Data.CurveBalance = n; else power.Data.CurvePerformance = n;
            power.Save();
        }

        int PlanIndex(string id)
        {
            Guid g;
            if (!Guid.TryParse(id ?? "", out g)) return 0;
            int i = plans.FindIndex(p => p.Id == g);
            return i < 0 ? 0 : i + 1;
        }

        string PlanId(int index) { return index <= 0 || index > plans.Count ? "" : plans[index - 1].Id.ToString(); }

        void SaveModePlan(int k)
        {
            if (loading) return;
            var id = PlanId(modePlans[k].Index);
            if (k == 0) power.Data.PlanQuiet = id; else if (k == 1) power.Data.PlanBalance = id; else if (k == 2) power.Data.PlanPerformance = id; else power.Data.PlanExtreme = id;
            power.Save();
        }

        void Select(PowerPreset p)
        {
            selected = p; edit = p.Clone();
            Baseline();
            ShowEdit();
        }

        // ---------- 20.11 (the user's wish): Ctrl+Z / Ctrl+Y, as «Звук» and «Видеокарта» have ----------
        // A step is the values of the preset being edited (the limits, the graphics card, the fans and their curves, the
        // overclocking, the plan, the curve and the GPU preset tied to it). A run of changes with less than half a
        // second between them, or made while a mouse button is held (a slider dragged), is one step. The steps belong to
        // the preset shown: another preset picked starts afresh; saving keeps them (a step back then makes the editor
        // differ from the saved preset again). The block «Настройки процессора в схеме» is not in them: it writes to the
        // Windows plan with its own «Записать» / «Отменить».
        readonly System.Collections.Generic.List<PowerPreset> back = new System.Collections.Generic.List<PowerPreset>(), forward = new System.Collections.Generic.List<PowerPreset>();
        PowerPreset seen; string seenName; bool gesture;
        readonly Timer gestureEnd = new Timer { Interval = 500 };

        void Baseline()
        {
            if (seen != null && seenName == selected.Name && seen.SameAs(edit)) return; // saved as it stood: the steps stay
            back.Clear(); forward.Clear(); gesture = false; gestureEnd.Stop();
            seen = edit.Clone(); seenName = selected.Name;
        }

        // a change was taken into the editor: the values before it are a step back (once per run of changes)
        void Record()
        {
            if (seen == null || edit.SameAs(seen)) return;
            if (!gesture) { back.Add(seen); if (back.Count > 200) back.RemoveAt(0); forward.Clear(); gesture = true; }
            seen = edit.Clone();
            gestureEnd.Stop(); gestureEnd.Start();
        }

        bool Step(System.Collections.Generic.List<PowerPreset> from, System.Collections.Generic.List<PowerPreset> to)
        {
            if (edit == null || from.Count == 0) return false;
            to.Add(edit.Clone());
            var snap = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
            edit = snap.Clone(); edit.Name = selected.Name;
            seen = edit.Clone(); gesture = false; gestureEnd.Stop();
            ShowEdit();
            return true;
        }

        // Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z from the main window while this page is shown (a number being typed keeps its own)
        public bool HandleKey(Keys keyData)
        {
            bool undo = keyData == (Keys.Control | Keys.Z), redo = keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z);
            if (!Visible || edit == null || (!undo && !redo)) return false;
            var form = FindForm();
            if (form != null && form.ActiveControl is TextBoxBase) return false;
            if (Control.MouseButtons != MouseButtons.None) return true; // something is being dragged: not under its hand
            if (undo) Step(back, forward); else Step(forward, back);
            return true;
        }

        // --power-undo-check: the steps back and forward in the editor, nothing applied or saved
        internal string UndoCheck()
        {
            var sb = new System.Text.StringBuilder();
            Action<bool, string> line = (ok, text) => sb.AppendLine((ok ? "ok    " : "FAIL  ") + text);
            var z = Keys.Control | Keys.Z; var y = Keys.Control | Keys.Y;
            int l0 = cpuLong.Value, s0 = cpuShort.Value; bool oc0 = ocOn.Checked;
            line(back.Count == 0 && HandleKey(z) && cpuLong.Value == l0, "nothing changed yet: Ctrl+Z is taken and changes nothing");
            // (a slider set from code reports nothing: the check calls Edited, as the slider's event does)
            cpuLong.Value = l0 == cpuLong.Value && l0 > 40 ? l0 - 10 : l0 + 10; Edited(); cpuShort.Value = s0 > 40 ? s0 - 10 : s0 + 10; Edited();
            int l1 = cpuLong.Value, s1 = cpuShort.Value;
            line(back.Count == 1 && edit.CpuLong == l1 && edit.CpuShort == s1, "two limits moved in one run: one step back (" + back.Count + ")");
            gesture = false; gestureEnd.Stop();
            bool fm0 = fanMax.Checked; fanMax.Checked = !fm0; // a toggle reports its click
            line(back.Count == 2, "after a pause a switch was clicked: a second step (" + back.Count + ")");
            HandleKey(z);
            line(fanMax.Checked == fm0 && edit.FanMax == fm0 && cpuLong.Value == l1 && back.Count == 1 && forward.Count == 1, "Ctrl+Z: the switch is back, the limits stay moved (" + cpuLong.Value + " W)");
            HandleKey(z);
            line(cpuLong.Value == l0 && cpuShort.Value == s0 && edit.SameAs(selected) == true && back.Count == 0 && forward.Count == 2, "Ctrl+Z again: the values of the preset, nothing unsaved (" + cpuLong.Value + " / " + cpuShort.Value + " W)");
            line(!saveBtn.Enabled, "«Сохранить» is grey again");
            HandleKey(z);
            line(cpuLong.Value == l0 && forward.Count == 2, "a third Ctrl+Z: nothing more to take back");
            HandleKey(y); HandleKey(Keys.Control | Keys.Shift | Keys.Z);
            line(cpuLong.Value == l1 && cpuShort.Value == s1 && fanMax.Checked == edit.FanMax && forward.Count == 0 && back.Count == 2 && saveBtn.Enabled, "Ctrl+Y and Ctrl+Shift+Z: both steps again, «Сохранить» lit");
            HandleKey(z); gesture = false; cpuTemp.Value = cpuTemp.Value > 80 ? cpuTemp.Value - 2 : cpuTemp.Value + 2; Edited();
            line(forward.Count == 0 && back.Count == 2, "a new change after a step back drops the steps forward");
            line(ocOn.Checked == oc0, "the overclocking switch was not touched");
            return sb.ToString();
        }

        // the values of edit into the controls
        void ShowEdit()
        {
            var p = selected;
            loading = true;
            cpuLong.Value = edit.CpuLong; cpuShort.Value = edit.CpuShort; cpuPeak.Value = edit.CpuPeak; cpuCross.Value = edit.CpuCross; cpuTemp.Value = edit.CpuTemp;
            gpuTemp.Value = edit.GpuTemp; gpuTotal.Value = edit.GpuTotal;
            gpuBoost.Set(gpuBoost.Items, Math.Max(0, Array.IndexOf(Limits.GpuBoostSteps, edit.GpuBoost)));
            gpuTgp.Set(gpuTgp.Items, Math.Max(0, Array.IndexOf(Limits.GpuTgpSteps, edit.GpuTgp)));
            gpuToCpu.Set(gpuToCpu.Items, Math.Max(0, Array.IndexOf(Limits.GpuToCpuSteps, edit.GpuToCpu)));
            editCpu = (int[])(edit.FanCpu ?? FanControl.DefaultCurve).Clone(); editGpu = (int[])(edit.FanGpu ?? FanControl.DefaultCurve).Clone();
            editBoth = (int[])(edit.FanBoth ?? FanControl.DefaultCurve).Clone();
            fanKind.Selected = edit.FanShared ? 1 : 0; fanSrc.Selected = edit.FanSource >= 0 && edit.FanSource <= 2 ? edit.FanSource : 2;
            SetFanTabs();
            fanMax.Set(edit.FanMax); fanOn.Set(edit.FanCurveOn);
            ocOn.Set(edit.OcOn); allCoreOn.Set(edit.AllCoreOn);
            pboScalar.Value = edit.PboScalar; pboBoost.Value = edit.PboBoost; allCore.Value = edit.AllCore;
            planBtn.Set(new[] { "Не менять" }.Concat(plans.Select(x => x.Name)).ToArray(), PlanIndex(edit.Plan));
            curveBtn.Set(new[] { "Не менять" }.Concat(curveNames).ToArray(), CurveIndex(edit.Curve));
            if (Hw.Nvidia) gpuPresetBtn.Set(new[] { "Не менять" }.Concat(gpuNames).ToArray(), GpuIndex(edit.GpuPreset));
            presetBtn.Text = p.Name + "  ▾";
            loading = false;
            FollowCfg();
            ShowTuneRow();
            if (biosOcChecked) UpdateOc();
            Edited();
        }

        void Collect()
        {
            edit.CpuLong = cpuLong.Value; edit.CpuShort = cpuShort.Value; edit.CpuPeak = cpuPeak.Value; edit.CpuCross = cpuCross.Value; edit.CpuTemp = cpuTemp.Value;
            edit.GpuTemp = gpuTemp.Value; edit.GpuTotal = gpuTotal.Value;
            edit.GpuBoost = Limits.GpuBoostSteps[Math.Max(0, gpuBoost.Index)];
            edit.GpuTgp = Limits.GpuTgpSteps[Math.Max(0, gpuTgp.Index)];
            edit.GpuToCpu = Limits.GpuToCpuSteps[Math.Max(0, gpuToCpu.Index)];
            edit.FanCpu = (int[])editCpu.Clone(); edit.FanGpu = (int[])editGpu.Clone(); edit.FanMax = fanMax.Checked; edit.FanCurveOn = fanOn.Checked;
            edit.FanBoth = (int[])editBoth.Clone(); edit.FanShared = Shared; edit.FanSource = Math.Max(0, fanSrc.Selected);
            edit.OcOn = ocOn.Checked; edit.PboScalar = pboScalar.Value; edit.PboBoost = pboBoost.Value; edit.AllCoreOn = allCoreOn.Checked; edit.AllCore = allCore.Value;
            edit.Plan = PlanId(planBtn.Index);
            edit.Curve = CurveName(curveBtn.Index);
            if (Hw.Nvidia) edit.GpuPreset = GpuName(gpuPresetBtn.Index);
        }

        void Edited()
        {
            if (loading || edit == null) return;
            Collect();
            bool dirty = !edit.SameAs(selected) || CfgDirty;
            bool active = selected.Name == power.Data.Active;
            presetRow.SetSub(active ? "Активный: включается в режиме «Свой» (Fn+Q или главная)" : "Выбран, но не активный: «Применить» сделает его активным");
            bar.SetStatus(dirty ? "Есть несохранённые изменения" : power.LastError != null ? "Ошибка: " + power.LastError : "Сохранено");
            saveBtn.Enabled = dirty;
            undoDirty = dirty;
            applyBtn.Enabled = dirty || !active; // gray when the active preset is already applied as it is
            // the curves and the extras act only while Red Tune drives the fans: grey and locked otherwise
            fanCard.OptionsActive(fanOn.Checked);
            if (fan.Enabled != fanOn.Checked) fan.Enabled = fanOn.Checked;
            fanCard.Default.Enabled = fanOn.Checked && (!editCpu.SequenceEqual(FanControl.DefaultCurve) || !editGpu.SequenceEqual(FanControl.DefaultCurve) || !editBoth.SequenceEqual(FanControl.DefaultCurve) || fanMax.Checked);
            if (fanKind.Enabled != fanOn.Checked) { fanKind.Enabled = fanOn.Checked; fanSrc.Enabled = fanOn.Checked; }
            Record();
        }

        void SaveEdit()
        {
            var name = selected.Name;
            int i = power.Data.Presets.IndexOf(selected);
            var copy = edit.Clone(); copy.Name = name;
            power.Data.Presets[i] = copy;
            power.Save();
            Select(copy);
        }

        void Apply()
        {
            Collect(); SaveEdit(); SaveCfg();
            applyBtn.Enabled = false; bar.SetStatus("Применяю…");
            power.Apply(selected, ok => { applyBtn.Enabled = true; bar.SetStatus(ok ? "Применено: режим «Свой», пресет «" + selected.Name + "»" : "Ошибка: " + power.LastError); Edited(); });
        }

        bool undoDirty;

        // "Сбросить": undo the edits, or fill the editor with the factory values of a mode (nothing is applied until "Применить")
        void ResetMenu()
        {
            var m = Page.Menu();
            var undo = m.Items.Add("Отменить изменения", null, (s, e) => { if (CfgDirty) UndoCfgEdits(); Select(selected); });
            undo.Enabled = undoDirty;
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Как в режиме «Тихий»", null, (s, e) => FromMode(Lenovo.Quiet));
            m.Items.Add("Как в режиме «Баланс»", null, (s, e) => FromMode(Lenovo.Balance));
            m.Items.Add("Как в режиме «Макс»", null, (s, e) => FromMode(Lenovo.Performance));
            if (selected.Name == PowerService.TuneName)
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Заполнить для автотюна", null, (s, e) => FillForTune());
            }
            ShowAt(m, resetBtn);
        }

        // 18.11, the tuning preset: the editor gets what PowerService.FillTune gives (the larger of the factory «Макс» mode
        // and the most powerful preset, overclocking off, no curve, no graphics preset) and the plan «Тюнинг» when Windows
        // has it. Nothing is saved or applied until «Сохранить» / «Применить».
        void FillForTune()
        {
            Collect();
            var t = edit.Clone();
            PowerService.FillTune(power.Data, t);
            var plan = TunePlanId(); if (plan != "") t.Plan = plan;
            loading = true;
            cpuLong.Value = t.CpuLong; cpuShort.Value = t.CpuShort; cpuPeak.Value = t.CpuPeak; cpuCross.Value = t.CpuCross; cpuTemp.Value = t.CpuTemp;
            gpuTemp.Value = t.GpuTemp; gpuTotal.Value = t.GpuTotal;
            gpuBoost.Set(gpuBoost.Items, Math.Max(0, Array.IndexOf(Limits.GpuBoostSteps, t.GpuBoost)));
            gpuTgp.Set(gpuTgp.Items, Math.Max(0, Array.IndexOf(Limits.GpuTgpSteps, t.GpuTgp)));
            gpuToCpu.Set(gpuToCpu.Items, Math.Max(0, Array.IndexOf(Limits.GpuToCpuSteps, t.GpuToCpu)));
            ocOn.Set(false); allCoreOn.Set(false); pboScalar.Value = 1; pboBoost.Value = 0; allCore.Value = 0;
            planBtn.Set(planBtn.Items, PlanIndex(t.Plan));
            curveBtn.Set(curveBtn.Items, 0);
            if (Hw.Nvidia) gpuPresetBtn.Set(gpuPresetBtn.Items, 0);
            loading = false;
            if (biosOcChecked) UpdateOc();
            Edited(); FollowCfg(); ShowTuneRow();
            bar.SetStatus("Для автотюна: " + t.CpuLong + " / " + t.CpuShort + " / " + t.CpuPeak + " W, " + t.CpuTemp + (edit.SameAs(selected) ? " °C — так уже и стоит" : " °C — сохраните пресет"));
        }

        void FromMode(int mode)
        {
            System.Collections.Generic.Dictionary<uint, int> d;
            try { d = Lenovo.ModeDefaults(mode); } catch (Exception ex) { bar.SetStatus("Не удалось прочитать заводские значения: " + ex.Message); return; }
            Func<uint, int, int> v = (id, keep) => d.ContainsKey(id) ? d[id] : keep;
            loading = true;
            cpuLong.Value = v(Lenovo.CpuLong, cpuLong.Value); cpuShort.Value = v(Lenovo.CpuShort, cpuShort.Value); cpuPeak.Value = v(Lenovo.CpuPeak, cpuPeak.Value);
            cpuCross.Value = v(Lenovo.CpuCross, cpuCross.Value); cpuTemp.Value = v(Lenovo.CpuTempLimit, cpuTemp.Value);
            gpuTemp.Value = v(Lenovo.GpuTempLimit, gpuTemp.Value); gpuTotal.Value = v(Lenovo.GpuTotal, gpuTotal.Value);
            Func<int[], int, int> idx = (steps, val) => { int i = Array.IndexOf(steps, val); return i >= 0 ? i : Array.IndexOf(steps, steps.OrderBy(x => Math.Abs(x - val)).First()); };
            gpuBoost.Set(gpuBoost.Items, idx(Limits.GpuBoostSteps, v(Lenovo.GpuBoost, Limits.GpuBoostSteps[Math.Max(0, gpuBoost.Index)])));
            gpuTgp.Set(gpuTgp.Items, idx(Limits.GpuTgpSteps, v(Lenovo.GpuTgp, Limits.GpuTgpSteps[Math.Max(0, gpuTgp.Index)])));
            gpuToCpu.Set(gpuToCpu.Items, idx(Limits.GpuToCpuSteps, v(Lenovo.GpuToCpu, Limits.GpuToCpuSteps[Math.Max(0, gpuToCpu.Index)])));
            editCpu = (int[])FanControl.DefaultCurve.Clone(); editGpu = (int[])FanControl.DefaultCurve.Clone(); editBoth = (int[])FanControl.DefaultCurve.Clone(); ShowFanCurve();
            fanMax.Set(false);
            loading = false;
            Edited();
        }

        void PresetMenu()
        {
            var m = Page.Menu();
            foreach (var p in power.Data.Presets)
            {
                var pp = p;
                var it = new ToolStripMenuItem(p.Name + (p.Name == power.Data.Active ? "   · активный" : "")) { Checked = p == selected };
                it.Click += (s, e) => Select(pp);
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Новый пресет из этих настроек…", null, (s, e) => NewPreset());
            // the auto-tune finds its preset by its name: «00 Тюнинг» is not renamed and not deleted (18.11)
            bool tune = selected.Name == PowerService.TuneName;
            var ren = m.Items.Add("Переименовать…", null, (s, e) => Rename());
            ren.Enabled = !tune;
            var del = m.Items.Add("Удалить", null, (s, e) => Delete());
            del.Enabled = power.Data.Presets.Count > 1 && !tune;
            ShowAt(m, presetBtn);
        }

        bool NameTaken(string n) { return power.Data.Presets.Any(p => p.Name == n); }

        void NewPreset()
        {
            var name = InputDialog.Ask(FindForm(), "Новый пресет", "Название", L.T("05 Новый"));
            if (name == null) return;
            if (NameTaken(name)) { Msg.Show(FindForm(), "Такое название уже есть", "Red Tune"); return; }
            Collect();
            var p = edit.Clone(); p.Name = name;
            power.Data.Presets.Add(p); power.Save();
            Select(p);
        }

        void Rename()
        {
            if (selected.Name == PowerService.TuneName) return;
            var name = InputDialog.Ask(FindForm(), "Переименовать пресет", "Название", selected.Name);
            if (name == null || name == selected.Name) return;
            if (NameTaken(name)) { Msg.Show(FindForm(), "Такое название уже есть", "Red Tune"); return; }
            if (power.Data.Active == selected.Name) power.Data.Active = name;
            selected.Name = name; power.Save();
            Select(selected);
        }

        void Delete()
        {
            if (selected.Name == PowerService.TuneName) return;
            if (Msg.Show(FindForm(), "Удалить пресет «" + selected.Name + "»?", "Red Tune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            power.Data.Presets.Remove(selected);
            if (power.Data.Active == selected.Name) power.Data.Active = power.Data.Presets[0].Name;
            power.Save();
            Select(power.Active);
        }

        class FanCard : Painted
        {
            readonly TempFanCurve curve;
            readonly Segmented tabs, source; // source: the temperature of the one shared curve, top right over the graph
            readonly System.Collections.Generic.List<Row> options = new System.Collections.Generic.List<Row>();
            bool showOptions, shared, active = true;
            public readonly FlatButton Default = new FlatButton("По умолчанию");

            public void AddOption(Row r) { r.Visible = showOptions; options.Add(r); Controls.Add(r); Fit(); }

            // the extras only act while Red Tune drives the fans: grey and locked otherwise
            string optionsNote = "Для всех пресетов, меняется сразу";
            string CurveNote
            {
                get
                {
                    if (!active) return "Кривые работают только со своей кривой вентиляторов — включите переключатель выше";
                    return shared ? "Оба вентилятора одинаково, по температуре справа: ЦП, ГП или горячей из двух. Своя кривая у каждого пресета"
                                  : "ЦП — по температуре ЦП, ГП — по температуре ГП. Своя кривая у каждого пресета";
                }
            }
            public void OptionsActive(bool on)
            {
                var note = on ? "Для всех пресетов, меняется сразу" : "Работают только со своей кривой вентиляторов — включите переключатель выше";
                foreach (var r in options) if (r.Enabled != on) r.Enabled = on;
                if (note != optionsNote || active != on) { optionsNote = note; active = on; Invalidate(); }
            }

            public void SetShared(bool on) { shared = on; source.Visible = shared && !showOptions; PerformLayout(); Invalidate(); }

            public void ShowOptions(bool on)
            {
                showOptions = on;
                curve.Visible = Default.Visible = !on;
                source.Visible = shared && !on;
                foreach (var r in options) r.Visible = on;
                // a row places its toggle only while visible: lay them out again now that they are shown
                Fit(); PerformLayout();
                foreach (var r in options) { r.PerformLayout(); r.Invalidate(true); }
                Fit(); PerformLayout(); Invalidate();
            }

            // the curve with its button, or the option rows and a note
            void Fit()
            {
                int body = showOptions ? options.Sum(r => r.Height + Theme.S(8)) + Theme.S(36) : curve.Height + Theme.S(48);
                Height = Theme.S(16) + tabs.Height + Theme.S(8) + body;
            }

            public FanCard(Segmented tabs, TempFanCurve curve, Segmented source)
            {
                this.curve = curve; this.tabs = tabs; this.source = source; BackColor = Theme.Window;
                tabs.BackColor = Theme.Card; Default.BackColor = Theme.Card; source.BackColor = Theme.Card; source.Visible = false;
                Controls.Add(tabs); Controls.Add(source); Controls.Add(curve); Controls.Add(Default);
                Height = Theme.S(16) + tabs.Height + Theme.S(8) + curve.Height + Theme.S(48);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (curve == null) return;
                tabs.Location = new Point(Theme.S(16), Theme.S(12));
                source.Location = new Point(Width - Theme.S(16) - source.Width, Theme.S(12));
                curve.Bounds = new Rectangle(Theme.S(8), tabs.Bottom + Theme.S(8), Width - Theme.S(16), curve.Height);
                Default.Location = new Point(Width - Theme.S(16) - Default.Width, curve.Bottom + Theme.S(8));
                int y = tabs.Bottom + Theme.S(36);
                foreach (var r in options) { r.Bounds = new Rectangle(Theme.S(16), y, Width - Theme.S(32), r.Height); y += r.Height + Theme.S(8); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                Theme.Fill(g, r, Theme.S(10), Theme.Card);
                Theme.Stroke(g, r, Theme.S(10), Theme.Border);
                if (showOptions)
                    Theme.Text1(g, optionsNote, Theme.Small, new Rectangle(Theme.S(16), tabs.Bottom + Theme.S(8), Width - Theme.S(32), Theme.S(22)), Theme.Text2);
                else
                    Theme.Text1(g, CurveNote, Theme.Small,
                        new Rectangle(Theme.S(16), curve.Bottom + Theme.S(8), Math.Max(0, Default.Left - Theme.S(28)), Default.Height), Theme.Text2);
            }
        }
    }
}
