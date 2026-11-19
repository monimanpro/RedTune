using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;

namespace RedTune
{
    // Other Lenovo / tuning programs that write the same settings. Each one: is it here, does it start with Windows,
    // and switching that off (tasks disabled, services set to disabled, processes closed).
    class OtherApp
    {
        public string Name, Why;
        public string[] Processes = new string[0], TaskParts = new string[0], Services = new string[0];

        public bool Found, Running, Autostart;
        // whether it fights with Red Tune on this setup (Afterburner only once Red Tune applies its own GPU preset)
        public Func<bool> Relevant = () => true;

        public void Check()
        {
            Running = Processes.Any(p => Process.GetProcessesByName(p).Length > 0);
            var tasks = MatchingTasks();
            var services = Existing();
            Autostart = tasks.Any(t => t.Value) || services.Any(s => s.StartType == ServiceStartMode.Automatic);
            Found = Running || tasks.Count > 0 || services.Count > 0;
        }

        List<ServiceController> Existing()
        {
            var all = ServiceController.GetServices();
            return all.Where(s => Services.Any(n => n.Equals(s.ServiceName, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        // task name -> enabled
        Dictionary<string, bool> MatchingTasks()
        {
            var d = new Dictionary<string, bool>();
            if (TaskParts.Length == 0) return d;
            int code;
            var text = Tasks.Run("/Query /FO CSV /NH", out code);
            foreach (var line in text.Split('\n'))
            {
                var cells = line.Split(new[] { "\",\"" }, StringSplitOptions.None);
                if (cells.Length < 3) continue;
                var name = cells[0].Trim().Trim('"');
                if (!TaskParts.Any(p => name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) || d.ContainsKey(name)) continue;
                var status = cells[2].Trim().Trim('"', '\r');
                d[name] = status.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) < 0 && status.IndexOf("Отключ", StringComparison.OrdinalIgnoreCase) < 0;
            }
            return d;
        }

        public void Disable()
        {
            int code;
            foreach (var t in MatchingTasks().Where(t => t.Value)) Tasks.Run("/Change /TN \"" + t.Key + "\" /Disable", out code);
            foreach (var s in Existing())
            {
                Run("sc.exe", "config \"" + s.ServiceName + "\" start= disabled");
                try { if (s.Status != ServiceControllerStatus.Stopped) { s.Stop(); s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } } catch { }
            }
            foreach (var p in Processes) foreach (var pr in Process.GetProcessesByName(p)) try { pr.Kill(); pr.WaitForExit(3000); } catch { }
            Store.Log(Name + ": автозапуск выключен");
        }

        static void Run(string exe, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(15000);
        }

        public static List<OtherApp> All()
        {
            return new List<OtherApp>
            {
                new OtherApp { Name = "Lenovo Legion Toolkit", Why = "Режимы питания, лимиты, вентиляторы, батарея, Fn-клавиши",
                    Processes = new[] { "Lenovo Legion Toolkit" }, TaskParts = new[] { "LenovoLegionToolkit" } },
                new OtherApp { Name = "MSI Afterburner", Why = "Кривая и частота памяти видеокарты (применяет свой профиль при запуске)",
                    Processes = new[] { "MSIAfterburner" }, TaskParts = new[] { "MSIAfterburner" }, Relevant = () => GpuStore.ApplyOnStart && GpuStore.Presets.Count > 0 },
                new OtherApp { Name = "UXTU", Why = "Кривая (пресет при запуске, зарядке и после сна)",
                    Processes = new[] { "Universal x86 Tuning Utility" }, TaskParts = new[] { "\\UXTU" } },
                new OtherApp { Name = "Lenovo Hotkeys", Why = "Служба Fn-клавиш Lenovo: перехватывает их у программы",
                    Services = new[] { "LenovoFnAndFunctionKeys" } },
                new OtherApp { Name = "Lenovo Vantage", Why = "Службы Lenovo, меняют режимы и батарею",
                    Processes = new[] { "LenovoVantage", "LenovoVantageService", "Lenovo.Modern.ImController" }, Services = new[] { "LenovoVantageService", "ImControllerService" } },
                new OtherApp { Name = "Legion Zone / Legion Space", Why = "Фирменная программа Lenovo для игровых ноутбуков",
                    Processes = new[] { "LegionZone", "LegionSpace", "LegionSpaceService" }, Services = new[] { "LegionZoneService", "LegionSpaceService" } },
            };
        }
    }
}
