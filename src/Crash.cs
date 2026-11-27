using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RedTune
{
    // 21.11 (beta): what went wrong, kept for a report. An exception nothing caught is written to crash.txt in the data
    // folder: the version, Windows, the machine in short, the exception with its stack and the end of the journal, with
    // the user's and the computer's names taken out (Diag.Clean). «О программе» shows the last one with «Отправить на
    // GitHub» (the Bug form filled in, without the journal: ForForm); after a crash that ended the program the next
    // start says so from the tray.
    static class Crash
    {
        public static string FilePath { get { return Path.Combine(Store.DataDir, "crash.txt"); } }

        public class Info { public DateTime Time; public bool Fatal; public string Kind, Text; }

        public static void Record(Exception ex, bool fatal, string where)
        {
            try
            {
                var text = Build(ex, fatal, where);
                Directory.CreateDirectory(Store.DataDir);
                File.WriteAllText(FilePath, text, new UTF8Encoding(true));
            }
            catch { }
        }

        // the record's text (also what --crash-check shows without writing anything)
        public static string Build(Exception ex, bool fatal, string where)
        {
            {
                var sb = new StringBuilder();
                sb.AppendLine("Red Tune " + Updater.Current + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + (fatal ? "fatal" : "caught") + " · " + where);
                sb.AppendLine("Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " x64" : "") + " · .NET " + Environment.Version);
                try { sb.AppendLine(Hw.Short()); } catch { }
                sb.AppendLine();
                sb.AppendLine(ex != null ? ex.ToString() : "(no exception object)");
                sb.AppendLine();
                sb.AppendLine("--- journal ---");
                try
                {
                    var log = Path.Combine(Store.DataDir, "redtune.log");
                    if (File.Exists(log)) using (var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var r = new StreamReader(f, Encoding.UTF8))
                        foreach (var l in r.ReadToEnd().Split('\n').Reverse().Take(40).Reverse()) sb.AppendLine(l.TrimEnd('\r'));
                }
                catch { }
                return Diag.Clean(sb.ToString());
            }
        }

        // the last record, or null
        public static Info Last()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var text = File.ReadAllText(FilePath, Encoding.UTF8);
                var first = text.Split('\n')[0];
                var lines = text.Split('\n');
                // the exception's first line: "System.Xxx: message"
                var kind = lines.Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Windows ") && l.Contains("Exception")) ?? "";
                if (kind.Length > 160) kind = kind.Substring(0, 160) + "…";
                return new Info { Time = File.GetLastWriteTime(FilePath), Fatal = first.Contains(" · fatal · "), Kind = kind, Text = text };
            }
            catch { return null; }
        }

        // the Bug form of the issue templates with its fields filled in from the address (ids: version, what, report, log);
        // what does not fit an address is cut there (the whole record goes to the clipboard)
        public static string BugUrl(Info c, string report)
        {
            string head = "https://github.com/" + Updater.Repo + "/issues/new?template=bug.yml&title=" + Uri.EscapeDataString("[Crash] " + Short(c.Kind, 90))
                + "&version=" + Uri.EscapeDataString(Updater.Current.ToString())
                + "&what=" + Uri.EscapeDataString((c.Fatal ? "Red Tune crashed" : "Red Tune caught an error") + " (" + c.Time.ToString("yyyy-MM-dd HH:mm") + "): " + c.Kind + "\n\nWhat I was doing: ")
                + "&report=" + Uri.EscapeDataString(report ?? "");
            var log = ForForm(c.Text);
            for (int i = 0; i < 12; i++)
            {
                var url = head + "&log=" + Uri.EscapeDataString(log);
                if (url.Length <= 7500 || log.Length < 200) return url;
                log = log.Substring(0, log.Length * 3 / 4) + "\n… (cut; the whole record is in crash.txt)";
            }
            return head;
        }

        // 27.11: the record as it goes to GitHub (the form's field and the clipboard): without the journal. An issue is
        // public, and the journal's lines name the user's presets and may name programs (the user: «убрать из формы»).
        // The version, the machine in short, the error and where it happened stay; crash.txt keeps everything.
        public static string ForForm(string text)
        {
            text = text ?? "";
            int j = text.IndexOf("--- journal ---", StringComparison.Ordinal);
            return j < 0 ? text : text.Substring(0, j).TrimEnd() + "\n\n(the journal is not sent; it stays in crash.txt on the computer)\n";
        }

        static string Short(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n) + "…"; }
    }
}
