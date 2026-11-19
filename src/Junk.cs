using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RedTune
{
    // «Очистка» → files: categories of things Windows and programs rebuild by themselves. The scan only measures;
    // only the categories the user ticks are deleted. Files in use are skipped quietly, folders themselves stay,
    // personal folders (Downloads, Documents…) are never looked at.
    class JunkCategory
    {
        public string Key, Name, Sub;
        public bool DefaultOn = true;
        public bool InfoOnly;                    // only shown with its size (Windows.old)
        public bool Recycle;                     // the Recycle Bin, emptied through the shell
        public int MinAgeHours;                  // temp folders: only files older than this (fresh ones may be in use)
        public Func<IEnumerable<string>> Folders = () => new string[0];
        public Func<IEnumerable<string>> Files = () => new string[0];
        public Func<string, bool> Skip = path => false;

        public long Bytes; public int Count;     // the last scan
    }

    static class Junk
    {
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        static string ProgramData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }

        // browser profiles: Default, Profile 1… under «User Data»
        static IEnumerable<string> BrowserCaches(string userData)
        {
            if (!Directory.Exists(userData)) yield break;
            foreach (var profile in SafeDirs(userData).Where(d => { var n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile ") || n == "Guest Profile"; }))
                foreach (var sub in new[] { @"Cache", @"Code Cache", @"GPUCache", @"Service Worker\CacheStorage" })
                    yield return Path.Combine(profile, sub);
            foreach (var sub in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" }) yield return Path.Combine(userData, sub);
        }

        public static List<JunkCategory> Categories()
        {
            var temp = Path.GetTempPath().TrimEnd('\\');
            return new List<JunkCategory>
            {
                new JunkCategory { Key = "temp", Name = "Временные файлы", Sub = "Ваша папка Temp — файлы старше суток", MinAgeHours = 24,
                    Folders = () => new[] { temp }, Skip = p => p.StartsWith(Path.Combine(temp, "claude") + "\\", StringComparison.OrdinalIgnoreCase) }, // Claude's working files
                new JunkCategory { Key = "wintemp", Name = "Временные файлы Windows", Sub = "C:\\Windows\\Temp — файлы старше суток", MinAgeHours = 24,
                    Folders = () => new[] { Path.Combine(Win, "Temp") } },
                new JunkCategory { Key = "recycle", Name = "Корзина", Sub = "Всё, что в корзине на всех дисках", Recycle = true },
                new JunkCategory { Key = "update", Name = "Кэш обновлений Windows", Sub = "Скачанные установщики уже установленных обновлений",
                    Folders = () => new[] { Path.Combine(Win, @"SoftwareDistribution\Download"), Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") } },
                new JunkCategory { Key = "browser", Name = "Кэш браузеров", Sub = "Chrome и Edge: только кэш, без паролей, куков и истории. Закройте браузер — открытый не отдаст часть файлов",
                    Folders = () => BrowserCaches(Path.Combine(Local, @"Google\Chrome\User Data")).Concat(BrowserCaches(Path.Combine(Local, @"Microsoft\Edge\User Data"))) },
                new JunkCategory { Key = "wer", Name = "Отчёты об ошибках и логи", Sub = "Отчёты Windows об ошибках программ и старые журналы установки",
                    Folders = () => new[] { Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(Local, @"Microsoft\Windows\WER"), Path.Combine(Win, @"Logs\CBS") },
                    MinAgeHours = 24 * 7 },
                new JunkCategory { Key = "thumbs", Name = "Кэш миниатюр", Sub = "Картинки-превью в проводнике, пересоздаются сами",
                    Files = () => SafeFiles(Path.Combine(Local, @"Microsoft\Windows\Explorer")).Where(f => Path.GetFileName(f).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase)) },
                new JunkCategory { Key = "shaders", Name = "Кэш шейдеров", Sub = "NVIDIA, AMD, DirectX. После удаления игры первый раз чуть подлагивают, пока соберут заново", DefaultOn = false,
                    Folders = () => new[] { Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"D3DSCache"), Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\VkCache") } },
                new JunkCategory { Key = "dumps", Name = "Дампы падений", Sub = "Minidump и MEMORY.DMP — по ним разбирают синие экраны. Удаляйте, если разбирать нечего", DefaultOn = false,
                    Folders = () => new[] { Path.Combine(Win, "Minidump"), Path.Combine(Local, "CrashDumps"), Path.Combine(Win, "LiveKernelReports") },
                    Files = () => new[] { Path.Combine(Win, "MEMORY.DMP") }.Where(File.Exists) },
                new JunkCategory { Key = "winold", Name = "Прошлая версия Windows", Sub = "Папка Windows.old после большого обновления. Удаляется штатной «Очисткой диска»", InfoOnly = true, DefaultOn = false,
                    Folders = () => new[] { Path.Combine(Path.GetPathRoot(Win), "Windows.old") } },
            };
        }

        // ---------- walking the folders ----------

        static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }

        static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }

        // every file under the folder, not following junctions/symlinks (they point elsewhere)
        static IEnumerable<FileInfo> Walk(string root)
        {
            if (!Directory.Exists(root)) yield break;
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                FileInfo[] files = null; DirectoryInfo[] dirs = null;
                try { var di = new DirectoryInfo(dir); files = di.GetFiles(); dirs = di.GetDirectories(); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (dirs != null) foreach (var d in dirs) if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d.FullName);
            }
        }

        static IEnumerable<FileInfo> Items(JunkCategory c)
        {
            var cut = DateTime.Now.AddHours(-c.MinAgeHours);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in c.Folders().Where(Directory.Exists))
                foreach (var f in Walk(root))
                    if (seen.Add(f.FullName) && !c.Skip(f.FullName) && (c.MinAgeHours <= 0 || f.LastWriteTime < cut)) yield return f;
            foreach (var path in c.Files())
            {
                FileInfo f = null; try { f = new FileInfo(path); } catch { }
                if (f != null && f.Exists && seen.Add(f.FullName) && !c.Skip(f.FullName)) yield return f;
            }
        }

        // ---------- scan and clean ----------

        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct RecycleInfo { public int Size; public long Bytes, Items; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref RecycleInfo info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        public static void Scan(JunkCategory c)
        {
            c.Bytes = 0; c.Count = 0;
            if (c.Recycle)
            {
                var info = new RecycleInfo { Size = Marshal.SizeOf(typeof(RecycleInfo)) };
                if (SHQueryRecycleBin(null, ref info) == 0) { c.Bytes = info.Bytes; c.Count = (int)Math.Min(int.MaxValue, info.Items); }
                return;
            }
            foreach (var f in Items(c)) { try { c.Bytes += f.Length; c.Count++; } catch { } }
        }

        // deletes what it can; returns the bytes really freed and how many files were in use
        public static long Clean(JunkCategory c, out int skipped)
        {
            skipped = 0;
            if (c.InfoOnly) return 0;
            if (c.Recycle)
            {
                Scan(c); long before = c.Bytes;
                SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirmation, no progress, no sound
                Scan(c); return Math.Max(0, before - c.Bytes);
            }
            long freed = 0;
            foreach (var f in Items(c).ToList())
            {
                try
                {
                    long len = f.Length;
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete(); freed += len;
                }
                catch { skipped++; }
            }
            // the empty folders left inside (never the category's own folders)
            foreach (var root in c.Folders().Where(Directory.Exists))
                foreach (var d in SafeDirs(root)) RemoveEmpty(d, c);
            Store.Log("очистка: " + c.Name + " — освобождено " + (freed / 1048576) + " МБ" + (skipped > 0 ? ", занято программами: " + skipped : ""));
            return freed;
        }

        static void RemoveEmpty(string dir, JunkCategory c)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || c.Skip(dir + "\\")) return;
                foreach (var sub in di.GetDirectories()) RemoveEmpty(sub.FullName, c);
                if (!di.EnumerateFileSystemInfos().Any() && (c.MinAgeHours <= 0 || di.LastWriteTime < DateTime.Now.AddHours(-c.MinAgeHours))) di.Delete();
            }
            catch { }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / 1073741824.0).ToString("0.0") + " ГБ";
            if (bytes >= 1L << 20) return (bytes / 1048576.0).ToString("0") + " МБ";
            if (bytes > 0) return Math.Max(1, bytes / 1024) + " КБ";
            return "0";
        }
    }
}
