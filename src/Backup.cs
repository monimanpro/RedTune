using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RedTune
{
    // 21.11: the data folder in one zip and back (Settings → «Резервная копия»): before reinstalling Windows, or to move to
    // another computer. Left out: journals, pictures, older copies (.bak), the Prime95 folder, saved registry keys of
    // removed programs, crash records. A copy is loaded at the next start, before anything reads the settings: the
    // current ones go into backups\ first. From another computer (the model in the copy differs) the curve, the memory
    // timings and the graphics card's curve stay out: they were found for that silicon.
    static class Backup
    {
        const string ManifestName = "redtune-backup.txt";
        static readonly string[] SkipDirs = { "p95", "removed-registry", "backups" };
        static readonly string[] SkipNames = { "blackbox.txt", "crash.txt", "applying.flag", "import-pending.zip", "import-pending.txt" };
        // what is tied to this machine's processor, memory and graphics card
        static readonly string[] HardwareNames = { "curves.txt", "corecurves.txt", "curves_mode.txt", "tune.json", "watch.json", "history.txt", "resets.txt", "gpu.txt", "gpu_stock.txt" };
        static string Pending { get { return Path.Combine(Store.DataDir, "import-pending.zip"); } }

        static bool Skipped(string rel)
        {
            var parts = rel.Split('\\', '/');
            if (parts.Length > 1 && SkipDirs.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
            var name = parts[parts.Length - 1].ToLowerInvariant();
            return SkipNames.Contains(name) || name.EndsWith(".log") || name.EndsWith(".png") || name.EndsWith(".tmp") || name.Contains(".bak");
        }

        static bool Hardware(string rel)
        {
            var parts = rel.Split('\\', '/');
            return parts.Length > 1 && parts[0].Equals("Ram", StringComparison.OrdinalIgnoreCase) || HardwareNames.Contains(parts[parts.Length - 1], StringComparer.OrdinalIgnoreCase);
        }

        public static string DefaultName { get { return "RedTune-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip"; } }

        // the copy; returns how many files went in
        public static int Export(string zipPath)
        {
            int n = 0;
            var root = Store.DataDir.TrimEnd('\\') + "\\";
            var tmp = zipPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry(ManifestName).Open(), new UTF8Encoding(false)))
                {
                    string machine; try { machine = Hw.Short(); } catch { machine = ""; }
                    w.Write("Red Tune " + Updater.Current + "\n" + machine + "\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n");
                }
                if (Directory.Exists(root))
                    foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var rel = f.Substring(root.Length);
                        if (Skipped(rel)) continue;
                        try
                        {
                            using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var dst = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal).Open()) src.CopyTo(dst);
                            n++;
                        }
                        catch { } // a file another part of the program holds right now: left out
                    }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return n;
        }

        // the copy's first two lines (version, machine), or null when the zip is not a Red Tune copy
        public static string[] ManifestOf(string zipPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var e = zip.GetEntry(ManifestName); if (e == null) return null;
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd().Split('\n').Select(l => l.Trim()).ToArray();
                }
            }
            catch { return null; }
        }

        public static bool SameMachine(string[] manifest) { return manifest != null && manifest.Length > 1 && manifest[1] == Hw.Short(); }

        // the copy waits for the next start (MainForm.RestartApp right after)
        public static void Stage(string zipPath, bool withHardware)
        {
            Directory.CreateDirectory(Store.DataDir);
            File.Copy(zipPath, Pending, true);
            File.WriteAllText(Path.ChangeExtension(Pending, ".txt"), withHardware ? "all" : "nohw");
        }

        // at the start, before the settings are read: the current data into backups\, then the copy over it; a line for the log
        public static string ApplyPending()
        {
            if (!File.Exists(Pending)) return null;
            var mode = Path.ChangeExtension(Pending, ".txt");
            bool withHw = !File.Exists(mode) || File.ReadAllText(mode).Trim() != "nohw";
            try
            {
                var dir = Path.Combine(Store.DataDir, "backups"); Directory.CreateDirectory(dir);
                var before = Path.Combine(dir, "before-import-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".zip");
                Export(before);
                var root = Path.GetFullPath(Store.DataDir).TrimEnd('\\') + "\\";
                int n = 0, kept = 0;
                using (var zip = ZipFile.OpenRead(Pending))
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName == ManifestName || e.FullName.EndsWith("/")) continue;
                        var rel = e.FullName.Replace('/', '\\');
                        if (Skipped(rel)) continue;
                        if (!withHw && Hardware(rel)) { kept++; continue; }
                        var target = Path.GetFullPath(Path.Combine(root, rel));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // nothing outside the data folder
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        e.ExtractToFile(target, true); n++;
                    }
                return "резервная копия загружена: " + n + " файлов" + (kept > 0 ? ", без кривой, памяти и видеокарты (" + kept + ")" : "") + "; прежние настройки в " + Path.GetFileName(before);
            }
            catch (Exception ex) { return "резервная копия не загрузилась: " + ex.Message; }
            finally { try { File.Delete(Pending); File.Delete(mode); } catch { } }
        }
    }
}
