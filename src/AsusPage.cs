using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RedTune
{
    // «Устройство» on an ASUS laptop (19.11): what its firmware has, each row only where the firmware says so (Asus.Has).
    // The Lenovo page (DevicePage) is another class: the two firmwares have nothing in common but the page's name.
    // The firmware cannot be asked for the charge limit, and what it answers for the mode is not relied on: what was
    // picked here is kept in the settings and shown from there; a row says «не задано» until something is picked.
    // After every change the row says what the firmware answered.
    class AsusDevicePage : Page
    {
        readonly Settings s;
        bool loading;
        const string ModeSub = "Тихий — меньше шума, производительный — больше мощности";
        const string ChargeSub = "Батарея заряжается до этого уровня и дольше живёт. 100 % — без предела";
        const string LightSub = "Яркость подсветки клавиш";
        const string FnSub = "Включено: F1–F12 работают без Fn, значки — с Fn";

        public AsusDevicePage(Settings settings)
        {
            s = settings;
            var note = Row("Функции ASUS ещё не проверены на настоящем ноутбуке", "Написаны по отчёту одной модели. Если переключатель не сработал, пришлите отчёт из «Компонентов»: ответ прошивки записан в журнал");
            note.WarnStyle = true;

            if (Asus.HasModes)
            {
                Section("Производительность");
                var mode = new Segmented("Тихий", "Баланс", "Производительный");
                var row = Row("Режим производительности", ModeSub, mode);
                mode.Selected = s.AsusMode >= 0 && s.AsusMode <= 2 ? s.AsusMode : -1;
                mode.Changed += (o, e) =>
                {
                    if (loading || mode.Selected < 0) return;
                    int r = Asus.SetMode(mode.Selected);
                    if (r == 1) { s.AsusMode = mode.Selected; Store.SaveSettings(s); }
                    row.SetSub(Answer(r, ModeSub));
                };
            }

            if (Asus.Has(Asus.BatteryLimit))
            {
                Section("Батарея");
                int[] levels = { 60, 80, 100 };
                var charge = new Segmented("60 %", "80 %", "100 %");
                var row = Row("Предел заряда", ChargeSub, charge);
                charge.Selected = Array.IndexOf(levels, s.AsusCharge);
                charge.Changed += (o, e) =>
                {
                    if (loading || charge.Selected < 0) return;
                    int want = levels[charge.Selected];
                    int r = Asus.SetChargeLimit(want);
                    if (r == 1) { s.AsusCharge = want; Store.SaveSettings(s); }
                    row.SetSub(Answer(r, ChargeSub));
                };
            }

            bool light = Asus.Has(Asus.KbdLight), fn = Asus.Has(Asus.FnLock);
            if (light || fn) Section("Клавиатура");
            if (light)
            {
                var level = new Segmented("Выкл", "1", "2", "3");
                var row = Row("Подсветка клавиатуры", LightSub, level);
                level.Selected = s.AsusLight >= 0 && s.AsusLight <= 3 ? s.AsusLight : -1;
                level.Changed += (o, e) =>
                {
                    if (loading || level.Selected < 0) return;
                    int r = Asus.SetKeyboardLight(level.Selected);
                    if (r == 1) { s.AsusLight = level.Selected; Store.SaveSettings(s); }
                    row.SetSub(Answer(r, LightSub));
                };
            }
            if (fn)
            {
                var lockT = new Toggle();
                var row = Row("Блокировка Fn", FnSub, lockT);
                loading = true; lockT.Set(Asus.Get(Asus.FnLock) == 1); loading = false;
                lockT.CheckedChanged += (o, e) =>
                {
                    if (loading) return;
                    int r = Asus.SetFnLock(lockT.Checked);
                    if (r != 1) { loading = true; lockT.Set(!lockT.Checked); loading = false; }
                    row.SetSub(Answer(r, FnSub));
                };
            }

            int cpuFan = Asus.FanRpm(Asus.CpuFan), gpuFan = Asus.FanRpm(Asus.GpuFan);
            if (cpuFan >= 0 || gpuFan >= 0)
            {
                Section("Вентиляторы");
                var fans = Row("Обороты сейчас", FanText());
                var t = new Timer { Interval = 2000 };
                t.Tick += (o, e) => { if (Visible) fans.SetSub(FanText()); };
                VisibleChanged += (o, e) => { if (Visible) { fans.SetSub(FanText()); t.Start(); } else t.Stop(); };
                Disposed += (o, e) => t.Dispose();
            }
        }

        static string FanText()
        {
            var parts = new List<string>();
            int c = Asus.FanRpm(Asus.CpuFan), g = Asus.FanRpm(Asus.GpuFan);
            if (c >= 0) parts.Add("Процессорный вентилятор: " + c + " об/мин");
            if (g >= 0) parts.Add("второй: " + g + " об/мин");
            return parts.Count > 0 ? string.Join(" · ", parts) + " · кривую вентиляторов прошивка этой модели задавать не даёт" : "Прошивка не ответила";
        }

        static string Answer(int r, string sub)
        {
            return r == 1 ? L.T("Прошивка приняла") + " · " + L.T(sub)
                : r == Asus.Failed ? L.T("Не удалось: драйвер ASUS не ответил")
                : L.T("Прошивка не приняла, ответ ") + r;
        }
    }
}
