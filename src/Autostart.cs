using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace RedTune
{
    static class Tasks
    {
        public static string Run(string args, out int code)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(866),
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
                return output;
            }
        }
    }

    // Logon task with highest rights: writing the curve and Lenovo settings needs admin rights, a task avoids the UAC prompt
    static class Autostart
    {
        const string TaskName = "RedTune";

        public static bool IsOn()
        {
            try
            {
                int code;
                var xml = Tasks.Run("/Query /TN \"" + TaskName + "\" /XML", out code);
                return code == 0 && xml.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            int code;
            if (!on)
            {
                Tasks.Run("/Delete /TN \"" + TaskName + "\" /F", out code);
                return;
            }
            var user = WindowsIdentity.GetCurrent().Name;
            var xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Red Tune: кривая, питание и настройки ноутбука</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) + "</Command><Arguments>--tray</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            var file = Path.Combine(Path.GetTempPath(), "redtune_task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                var output = Tasks.Run("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out code);
                if (code != 0) throw new Exception(output.Trim());
            }
            finally { File.Delete(file); }
        }
    }
}
