using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace RedTune
{
    // 21.11: new versions from the GitHub releases of this repository. The only place Red Tune goes to the network:
    // a GET of api.github.com/repos/<Repo>/releases/latest (once a day if «Проверять обновления» is on, or on a press),
    // and the installer from that release when the user presses «Установить». The download is checked against the
    // SHA-256 GitHub keeps for the release file; without one nothing is installed. Nothing about the computer is sent.
    static class Updater
    {
        public const string Repo = "monimanpro/RedTune";
        public const string Releases = "https://github.com/" + Repo + "/releases";

        public class Info
        {
            public Version Version; public string Tag, Page, AssetUrl, AssetName, Sha256; public long Size;
        }

        // the newest release seen this run (from the daily check or a press); null = not checked or nothing newer
        public static Info Found;
        public static event Action Changed;

        public static Version Current
        {
            get { var v = new Version(Application.ProductVersion); return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        public static bool Newer(Info i) { return i != null && i.Version != null && i.Version > Current; }

        static string Get(string url, string accept)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "RedTune/" + Current; rq.Accept = accept; rq.Timeout = 15000; rq.ReadWriteTimeout = 15000;
            using (var rs = rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream(), System.Text.Encoding.UTF8)) return sr.ReadToEnd();
        }

        // the latest published release (drafts and pre-releases are not «latest»); throws on a network or format error
        public static Info Latest() { return Parse(Get("https://api.github.com/repos/" + Repo + "/releases/latest", "application/vnd.github+json")); }

        // 21.11: the release of one version (the way back: «Вернуть …» installs the version that was there before an update)
        public static Info ForVersion(string version) { return Parse(Get("https://api.github.com/repos/" + Repo + "/releases/tags/v" + version, "application/vnd.github+json")); }

        static Info Parse(string json)
        {
            var d =(System.Collections.Generic.Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json);
            var i = new Info { Tag = d["tag_name"] as string, Page = d["html_url"] as string };
            Version v; if (i.Tag != null && Version.TryParse(i.Tag.TrimStart('v', 'V'), out v)) i.Version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            foreach (System.Collections.Generic.Dictionary<string, object> a in ((object[])d["assets"]).Cast<System.Collections.Generic.Dictionary<string, object>>())
            {
                var name = a["name"] as string;
                if (name == null || !name.StartsWith("RedTune-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                i.AssetName = name; i.AssetUrl = a["browser_download_url"] as string; i.Size = Convert.ToInt64(a["size"]);
                object dg; if (a.TryGetValue("digest", out dg) && dg is string && ((string)dg).StartsWith("sha256:")) i.Sha256 = ((string)dg).Substring(7).ToLowerInvariant();
                break;
            }
            return i;
        }

        // the check behind the page's button and the daily one; remembers what it found
        public static Info Check(Settings s)
        {
            var i = Latest();
            s.UpdateLast = DateTime.Now.ToString("yyyy-MM-dd HH:mm"); Store.SaveSettings(s);
            Found = Newer(i) ? i : null;
            var h = Changed; if (h != null) try { h(); } catch { }
            return i;
        }

        // once a day if the switch is on: a minute after the start, then looked at every hour while the program runs (23.11: it
        // used to be the start only, so a laptop left on for a week in the tray never heard of a new version); a check is due 20 h
        // after the last good one, a failed one (no network is normal) is tried again an hour later; quiet on any error.
        // found: once per version per run, so the tray note does not come back every day for the same release
        static System.Threading.Timer daily;
        static string told;
        public static void StartDaily(Settings s, Action<Info> found)
        {
            if (daily != null) return;
            System.Threading.TimerCallback tick = _ =>
            {
                try
                {
                    if (!s.UpdateCheck) return;
                    DateTime last; if (DateTime.TryParse(s.UpdateLast, out last) && (DateTime.Now - last).TotalHours < 20) return;
                    var i = Check(s);
                    if (Newer(i) && found != null && i.Version.ToString() != told) { told = i.Version.ToString(); found(i); }
                }
                catch (Exception ex) { Store.Log("update check: " + ex.Message); }
            };
            daily = new System.Threading.Timer(tick, null, 60000, 3600000);
        }

        // installed by the setup (Program Files) rather than run from a build folder: only then can the setup update it
        public static bool Installed
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F3B2D51-8C4E-4A7B-9E2F-1D5C7A9B3E40}_is1"))
                    {
                        var loc = k == null ? null : k.GetValue("InstallLocation") as string;
                        return loc != null && string.Equals(loc.TrimEnd('\\'), Application.StartupPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { return false; }
            }
        }

        // the setup into %TEMP%\RedTune-Update, checked against GitHub's SHA-256 (and size); returns its path
        public static string Download(Info i, Action<int> percent)
        {
            if (i == null || i.AssetUrl == null) throw new Exception(L.T("в выпуске нет установщика"));
            if (string.IsNullOrEmpty(i.Sha256)) throw new Exception(L.T("GitHub не дал контрольную сумму установщика"));
            var dir = Path.Combine(Path.GetTempPath(), "RedTune-Update"); Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir)) try { File.Delete(old); } catch { }
            var path = Path.Combine(dir, Path.GetFileName(i.AssetName));
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var rq = (HttpWebRequest)WebRequest.Create(i.AssetUrl);
            rq.UserAgent = "RedTune/" + Current; rq.Timeout = 30000; rq.ReadWriteTimeout = 30000; rq.AllowAutoRedirect = true;
            using (var rs = rq.GetResponse())
            using (var src = rs.GetResponseStream())
            using (var dst = File.Create(path))
            {
                long total = rs.ContentLength > 0 ? rs.ContentLength : i.Size, got = 0; var buf = new byte[81920]; int n, last = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n); got += n;
                    int p = total > 0 ? (int)(got * 100 / total) : 0;
                    if (p != last && percent != null) { last = p; percent(p); }
                }
            }
            string hash;
            using (var f = File.OpenRead(path)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            if (hash != i.Sha256 || (i.Size > 0 && new FileInfo(path).Length != i.Size))
            {
                try { File.Delete(path); } catch { }
                throw new Exception(L.T("контрольная сумма не совпала, файл удалён"));
            }
            return path;
        }

        // the setup runs with its progress window only; the tasks chosen at the first install stay; /rtupdate=1 makes it
        // start Red Tune again at the end (RedTune.iss). The caller exits right after.
        public static void Install(string setup, Settings s)
        {
            // the version being left, for «Вернуть …» after the update (the same for a step back: then the way back is forward)
            if (s != null) { s.UpdatePrev = Current.ToString(); Store.SaveSettings(s); }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /rtupdate=1") { UseShellExecute = true });
        }
    }
}
