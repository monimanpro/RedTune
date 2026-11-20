using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // Battery, screen, keyboard and touchpad. Every control reads its state when the page opens and writes on change;
    // a failure shows in the row's gray line instead of a dialog.
    class DevicePage : Page
    {
        readonly List<Action> refreshers = new List<Action>();
        readonly Row lightRow;
        bool loading;

        public DevicePage(Settings settings)
        {
            PageGroups.AddTabs(this, "device");
            Section("Батарея");
            var battery = new Segmented("Консервация", "Обычный", "Быстрый");
            var batRow = Row("Режим зарядки", "Консервация держит заряд около 80 % и бережёт батарею", battery);
            BindChoice(battery, batRow, "Консервация держит заряд около 80 % и бережёт батарею",
                () => (int)Energy.GetBattery(), i => Energy.SetBattery((BatteryMode)i));
            BindToggle("Ночная зарядка", "80 % на ночь, 100 % к утру", () => Energy.GetNightCharge(), Energy.SetNightCharge);
            BindToggle("Включение при открытии крышки", null, () => Lenovo.GetFeature(Lenovo.FlipToStart) == 1, v => Lenovo.SetFeature(Lenovo.FlipToStart, v ? 1 : 0));
            BindToggle("Включение при подключении зарядки", null, () => Lenovo.GetFeature(Lenovo.InstantBootAc) == 1, v => Lenovo.SetFeature(Lenovo.InstantBootAc, v ? 1 : 0));
            BindToggle("Включение при зарядке через USB-C", null, () => Lenovo.GetFeature(Lenovo.InstantBootUsbPd) == 1, v => Lenovo.SetFeature(Lenovo.InstantBootUsbPd, v ? 1 : 0));
            var usb = new Segmented("Выкл", "Во сне", "Всегда");
            var usbRow = Row("Питание USB", "Заряжать устройства по USB, когда ноутбук спит или выключен", usb);
            BindChoice(usb, usbRow, "Заряжать устройства по USB, когда ноутбук спит или выключен", () => (int)Energy.GetUsbPower(), i => Energy.SetUsbPower((UsbPower)i));

            Section("Видеокарта");
            var gpuBtn = new DropButton();
            const string gpuSub = "Гибридный бережёт батарею, дискретный — всё через RTX";
            var gpuRow = Row("Режим работы видеокарты", gpuSub, gpuBtn);
            int gpuNow = -1;
            gpuBtn.Changed += (s, e) =>
            {
                if (loading) return;
                int want = gpuBtn.Index;
                if (want == gpuNow) return;
                bool gsync = (want == Lenovo.GpuDiscrete) != (gpuNow == Lenovo.GpuDiscrete);
                var text = "Переключить на «" + Lenovo.GpuModeNames[want] + "»?\n\n" + (gsync ? "Переход между гибридным и дискретным режимом включится только после перезагрузки." : "Режим переключится сразу; если что-то не подхватится, поможет перезагрузка.");
                if (!ConfirmDialog.Ask(FindForm(), "Режим видеокарты", text, "Переключить", "Отмена")) { loading = true; gpuBtn.Set(gpuBtn.Items, gpuNow); loading = false; return; }
                Task.Run(() =>
                {
                    string err = null; bool reboot = false;
                    try { reboot = Lenovo.SetGpuMode(want); } catch (Exception ex) { err = ex.Message; Store.Log("видеокарта: " + err); }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (err != null) { gpuRow.SetSub("Не удалось: " + err); foreach (var r in refreshers) r(); return; }
                            gpuNow = want;
                            gpuRow.SetSub(reboot ? "Выбрано «" + Lenovo.GpuModeNames[want] + "» — включится после перезагрузки" : gpuSub);
                            if (reboot && ConfirmDialog.Ask(FindForm(), "Перезагрузка", "Режим видеокарты сменится после перезагрузки. Перезагрузить сейчас? Сохраните открытые файлы.", "Перезагрузить", "Позже"))
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { UseShellExecute = false, CreateNoWindow = true });
                        }));
                    }
                    catch { }
                });
            };
            refreshers.Add(() => Read(() => Lenovo.IgpuModeSupported ? Lenovo.GetGpuMode() : (Lenovo.GSyncSupported ? Lenovo.GetGpuMode() : -1), v =>
            {
                gpuRow.Visible = v >= 0;
                if (v >= 0) { gpuNow = v; gpuBtn.Set(Lenovo.GpuModeNames, v); }
                Relayout();
            }, gpuRow));

            // 20.11: the screen («Экран») is a section of its own now, «Дисплей» (DisplayPage)

            Section("Клавиатура и ввод");
            var light = new Segmented("Выкл", "Низкая", "Высокая");
            lightRow = Row("Подсветка клавиатуры", "Fn+Пробел", light);
            BindChoice(light, lightRow, "Fn+Пробел", () => Energy.KeyLightSupported ? (int)Energy.GetKeyLight() : -1, i => Energy.SetKeyLight((KeyLight)i));
            BindToggle("Блокировка тачпада", "Выключить сенсорную панель", () => Lenovo.GameZoneGet("GetTPStatus") == 1, v => Lenovo.GameZoneSet("SetTPStatus", v ? 1 : 0));
            BindToggle("Блокировка Fn", "F1–F12 работают без Fn", () => Energy.GetFnLock(), Energy.SetFnLock);
            BindToggle("Блокировка клавиши Windows", "Только на встроенной клавиатуре", () => Lenovo.GameZoneGet("GetWinKeyStatus") == 1, v => Lenovo.GameZoneSet("SetWinKeyStatus", v ? 1 : 0));
            BindToggle("Микрофон", "Все микрофоны вкл. или выкл.", () => Audio.MicOn() ?? false, Audio.SetMic);

            VisibleChanged += (s, e) => { if (Visible) foreach (var r in refreshers) r(); };
        }

        // runs a setter off the UI thread, shows an error in the row
        void Run(Row row, string sub, Action set)
        {
            Task.Run(() =>
            {
                string err = null;
                try { set(); } catch (Exception ex) { err = ex.Message; Store.Log("устройство: " + row.Title + ": " + err); }
                try { BeginInvoke((Action)(() => { row.SetSub(err != null ? "Не удалось: " + err : sub); if (err != null) foreach (var r in refreshers) r(); })); } catch { }
            });
        }

        void Read<T>(Func<T> get, Action<T> show, Row row)
        {
            Task.Run(() =>
            {
                T v = default(T); string err = null;
                try { v = get(); } catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => { loading = true; if (err == null) show(v); else row.SetSub("Недоступно: " + err); loading = false; })); } catch { }
            });
        }

        void BindToggle(string title, string sub, Func<bool> get, Action<bool> set)
        {
            var t = new Toggle();
            var row = Row(title, sub, t);
            t.CheckedChanged += (s, e) => { if (!loading) { bool v = t.Checked; Run(row, sub, () => set(v)); } };
            refreshers.Add(() => Read(get, v => t.Set(v), row));
        }

        void BindChoice(Segmented seg, Row row, string sub, Func<int> get, Action<int> set)
        {
            seg.Changed += (s, e) => { if (!loading) { int i = seg.Selected; Run(row, sub, () => set(i)); } };
            refreshers.Add(() => Read(get, i => { seg.Selected = i; row.Visible = i >= 0; Relayout(); }, row));
        }
    }
}
