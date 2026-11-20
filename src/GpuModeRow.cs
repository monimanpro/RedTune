using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // 20.11 (the user's wish): «Режим работы видеокарты» (Lenovo: hybrid, hybrid iGPU, Hybrid-Auto, discrete) lives on
    // «Видеокарта» now, first on the page and for everyone (the overclocking under it is an expert function). «Устройство»
    // keeps it only where there is no «Видеокарта» page. The row reads the mode when its page opens and asks before a
    // switch; a switch between hybrid and discrete takes a restart, which it offers.
    static class GpuModeRow
    {
        const string Sub = "Гибридный бережёт батарею, дискретный — всё через RTX";

        static bool? can;
        // the firmware can switch the mode (asked once; the menu needs it before any page is made)
        public static bool Available
        {
            get
            {
                if (can == null) { try { can = Hw.GameZone && (Lenovo.IgpuModeSupported || Lenovo.GSyncSupported); } catch { can = false; } }
                return can.Value;
            }
        }

        public static Row Add(Page page)
        {
            var btn = new DropButton();
            var row = page.Row("Режим работы видеокарты", Sub, btn);
            row.Visible = false;
            int now = -1; bool loading = false;
            Action read = () => Task.Run(() =>
            {
                int v = -1; string err = null;
                try { v = Lenovo.IgpuModeSupported || Lenovo.GSyncSupported ? Lenovo.GetGpuMode() : -1; } catch (Exception ex) { err = ex.Message; }
                try
                {
                    page.BeginInvoke((Action)(() =>
                    {
                        loading = true;
                        if (err != null) { row.SetSub("Недоступно: " + err); row.Visible = true; }
                        else { row.Visible = v >= 0; if (v >= 0) { now = v; btn.Set(Lenovo.GpuModeNames, v); } }
                        loading = false;
                        page.Relayout();
                    }));
                }
                catch { }
            });
            btn.Changed += (s, e) =>
            {
                if (loading) return;
                int want = btn.Index;
                if (want == now) return;
                bool gsync = (want == Lenovo.GpuDiscrete) != (now == Lenovo.GpuDiscrete);
                var text = "Переключить на «" + Lenovo.GpuModeNames[want] + "»?\n\n" + (gsync ? "Переход между гибридным и дискретным режимом включится только после перезагрузки." : "Режим переключится сразу; если что-то не подхватится, поможет перезагрузка.");
                if (!ConfirmDialog.Ask(page.FindForm(), "Режим видеокарты", text, "Переключить", "Отмена")) { loading = true; btn.Set(btn.Items, now); loading = false; return; }
                Task.Run(() =>
                {
                    string err = null; bool reboot = false;
                    try { reboot = Lenovo.SetGpuMode(want); } catch (Exception ex) { err = ex.Message; Store.Log("видеокарта: " + err); }
                    try
                    {
                        page.BeginInvoke((Action)(() =>
                        {
                            if (err != null) { row.SetSub("Не удалось: " + err); read(); return; }
                            now = want;
                            row.SetSub(reboot ? "Выбрано «" + Lenovo.GpuModeNames[want] + "» — включится после перезагрузки" : Sub);
                            if (reboot && ConfirmDialog.Ask(page.FindForm(), "Перезагрузка", "Режим видеокарты сменится после перезагрузки. Перезагрузить сейчас? Сохраните открытые файлы.", "Перезагрузить", "Позже"))
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { UseShellExecute = false, CreateNoWindow = true });
                        }));
                    }
                    catch { }
                });
            };
            page.VisibleChanged += (s, e) => { if (page.Visible) read(); };
            return row;
        }
    }
}
