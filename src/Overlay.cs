using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedTune
{
    // 16.11: a small overlay over games: «144 FPS · CPU 68° 4,3 ГГц · GPU 61° 2310 МГц» in a corner of the screen.
    // One line on a dark plate, a window of its own that takes no clicks and no focus and stays on top (the way the
    // notifications are shown: a layered window drawn in real pixels on its own thread). It shows over windowed and
    // borderless games; a game in the old exclusive full-screen mode covers every window, this one too.
    // FPS comes from Fps (Windows' own events about frames, nothing is injected into the game), the rest from Sensors:
    // the processor's temperature and clock as on «Главная», the graphics card's from NVIDIA's driver. The card is asked
    // only while the overlay is on screen (asking keeps a sleeping discrete card awake), and by default the overlay is
    // on screen only over a program that covers the whole monitor.
    static class Overlay
    {
        static Settings settings;
        static SynchronizationContext ui;
        static OverlayForm form;
        static readonly object gate = new object();

        // the places in the order of the list; CornerValues: what is kept in the settings for each (0..3 the corners as
        // before, 4 the middle of the top edge, added 16.11)
        public static readonly string[] CornerNames = { "Слева сверху", "Сверху по центру", "Справа сверху", "Слева снизу", "Снизу по центру", "Справа снизу" };
        public static readonly int[] CornerValues = { 0, 4, 1, 2, 5, 3 }; // 5: the middle of the bottom edge (27.11, the user's wish)
        public static readonly string[] WhenNames = { "В полноэкранных программах", "Всегда" };
        public static readonly string[] SizeNames = { "Мелкий", "Средний", "Крупный", "Очень крупный", "Огромный" };
        static readonly float[] SizePx = { 12f, 15f, 19f, 24f, 30f }; // the text height at 100 % scaling
        public static readonly string[] LayoutNames = { "В строку", "Столбиком" };

        // 27.11: what the overlay shows is picked in the settings (asked for on the forum: «больше параметров для
        // контроля»). Each value has a key kept in Settings.OverlayItems; the ones a new install shows are the five the
        // overlay always had. Everything but the two frame values and the battery was read every second already (Sensors).
        public class Item { public string Key, Name, Sub; public Func<bool> Fits; }
        static readonly Func<bool> HasFans = () => Hw.Features || Hw.AsusFw, Always = () => true;
        public static readonly Item[] Items =
        {
            new Item { Key = "fps", Name = "FPS", Sub = "Кадров в секунду у программы на экране", Fits = Always },
            new Item { Key = "low", Name = "Просадки FPS (1% low)", Sub = "Частота кадров в худшем 1 % кадров за последние 30 секунд", Fits = Always },
            new Item { Key = "low01", Name = "Редкие просадки FPS (0.1% low)", Sub = "То же по худшему 0,1 % кадров: одиночные рывки", Fits = Always },
            new Item { Key = "ftg", Name = "График времени кадра", Sub = "Последние 10 секунд: ровная линия — плавно, зубцы — рывки", Fits = Always },
            new Item { Key = "ct", Name = "Процессор: температура", Fits = Always },
            new Item { Key = "cc", Name = "Процессор: частота", Fits = Always },
            new Item { Key = "cl", Name = "Процессор: загрузка", Fits = Always },
            new Item { Key = "cw", Name = "Процессор: мощность", Fits = Always },
            new Item { Key = "cf", Name = "Процессор: вентилятор", Fits = HasFans },
            new Item { Key = "gt", Name = "Видеокарта: температура", Fits = () => Hw.Nvidia },
            new Item { Key = "gc", Name = "Видеокарта: частота", Fits = () => Hw.Nvidia },
            new Item { Key = "gl", Name = "Видеокарта: загрузка", Fits = () => Hw.Nvidia },
            new Item { Key = "gw", Name = "Видеокарта: мощность", Fits = () => Hw.Nvidia },
            new Item { Key = "gv", Name = "Видеокарта: видеопамять", Sub = "Сколько занято", Fits = () => Hw.Nvidia },
            new Item { Key = "gf", Name = "Видеокарта: вентилятор", Fits = () => Hw.Nvidia && HasFans() },
            new Item { Key = "ram", Name = "Оперативная память", Sub = "Сколько занято", Fits = Always },
            new Item { Key = "bat", Name = "Батарея", Sub = "Заряд в процентах", Fits = () => Hw.Battery },
            new Item { Key = "time", Name = "Время", Fits = Always },
        };
        public const string DefaultItems = "fps,ct,cc,gt,gc";

        public static bool Has(Settings s, string key) { return (s.OverlayItems ?? DefaultItems).Split(',').Contains(key); }

        public static void SetItem(Settings s, string key, bool on)
        {
            var have = new HashSet<string>((s.OverlayItems ?? DefaultItems).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            if (on) have.Add(key); else have.Remove(key);
            s.OverlayItems = string.Join(",", Items.Select(i => i.Key).Where(have.Contains)); // kept in the order of the list
            Store.SaveSettings(s);
            Preview();
        }

        // a change in the settings shows the overlay for a few seconds whatever is on screen, so the change is seen
        static DateTime previewUntil = DateTime.MinValue;
        public static void Preview()
        {
            if (settings == null || !settings.OverlayOn) return;
            previewUntil = DateTime.Now.AddSeconds(6);
            Apply();
        }

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

        public static void Init(Settings s)
        {
            settings = s;
            Application.ApplicationExit += (o, e) => { try { Fps.Stop(); } catch { } };
            Apply();
        }

        // the switch, the corner or «когда» changed
        public static void Apply()
        {
            if (settings == null) return;
            if (!settings.OverlayOn)
            {
                Fps.Stop();
                Post(f => f.Off());
                return;
            }
            lock (gate)
            {
                if (ui == null)
                {
                    var ready = new ManualResetEvent(false);
                    var t = new Thread(() =>
                    {
                        // real pixels, like the notifications: on a monitor of another scaling Windows would move and
                        // stretch a window of the program's own (system) DPI awareness
                        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
                        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                        ui = SynchronizationContext.Current;
                        form = new OverlayForm(settings);
                        ready.Set();
                        Application.Run();
                    }) { IsBackground = true, Name = "overlay" };
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    ready.WaitOne(3000);
                }
            }
            Fps.KeepLong = Has(settings, "low") || Has(settings, "low01") || Has(settings, "ftg");
            Task.Run(() => { var why = Fps.Start(); if (why != null) Store.Log("оверлей: FPS не считается: " + why); });
            Post(f => f.On());
        }

        // ---------- the picture ----------
        // what is shown now; NaN or a negative number = not known
        public class Vals
        {
            public float[] Frames = new float[0]; // how long each frame of the last seconds took, ms (the graph)
            public double Fps = double.NaN, Low = double.NaN, Low01 = double.NaN, CpuMhz = double.NaN, CpuLoad = double.NaN, CpuW = double.NaN, GpuW = double.NaN, RamGb = double.NaN, VramGb = double.NaN;
            public int CpuTemp = -1, GpuTemp = -1, GpuMhz = -1, GpuLoad = -1, CpuFan = -1, GpuFan = -1, Battery = -1;
            public bool Gpu;
            public string Time = "";
        }

        static readonly Color Label = Color.FromArgb(0xff, 0x6e, 0x73), Value = Color.White, Unit = Color.FromArgb(0xc4, 0xc0, 0xbc), Hot = Color.FromArgb(0xff, 0xa2, 0x40);

        // a piece of the overlay's text. Glued: it belongs to the piece before it (a unit after its number, the first
        // value after a row's name) and stands close to it; a piece that is not glued starts a new reading and keeps
        // a wider gap, so a long row still reads as separate numbers
        // Graph: the piece is a small graph of these numbers instead of text (its Text only tells one picture from the next)
        // Kind: the piece starts a reading of this kind (t temperature, c clock, l load, w power, m memory, f fan); in a
        // column the readings of one kind stand under each other (the user, 27.11: «не искать их глазами»)
        sealed class Piece
        {
            public readonly string Text; public readonly Color Color; public bool Glued; public float[] Graph; public string Kind;
            public Piece(string text, Color color, bool glued) { Text = text; Color = color; Glued = glued; }
        }

        // the groups of the line (frames, processor, graphics card, memory, battery, time), each a list of pieces.
        // In a line the frame rate reads «144 FPS» as it always did; in a column every row starts with its name
        static List<List<Piece>> Groups(Vals v, Func<string, bool> on, bool column)
        {
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            bool en = L.En;
            var num = en ? CultureInfo.InvariantCulture : ru;
            var all = new List<List<Piece>>();
            Func<List<Piece>> open = () => new List<Piece>();
            Action<List<Piece>, string, Color> add = (l, t, c) => l.Add(new Piece(t, c, false));
            Action<List<Piece>, string, Color> glue = (l, t, c) => l.Add(new Piece(t, c, true));
            Action<List<Piece>, string> close = (l, name) => { if (l.Count == 0) return; if (name != null) { l[0].Glued = true; l.Insert(0, new Piece(name, Label, false)); } all.Add(l); };
            Action<List<Piece>, string, string, Color> cell = (l, kind, t, c) => l.Add(new Piece(t, c, false) { Kind = kind });
            string rpm = en ? "RPM" : "об/мин", watt = en ? "W" : "Вт", gb = en ? "GB" : "ГБ";

            // the frames: first in a line, as it always was; in a column they go last, with the graph under them (the
            // user, 27.11: «сначала цпу потом гпу рам и фпс», «фпс с его графиком снизу»)
            var frames = new List<List<Piece>>();
            var g = open();
            if (on("fps"))
            {
                string f = double.IsNaN(v.Fps) ? "—" : Math.Round(v.Fps).ToString("0");
                if (column) { add(g, "FPS", Label); glue(g, f, Value); } else { add(g, f, Value); glue(g, "FPS", Label); }
            }
            Action<string, double> lowPart = (name, val) =>
            {
                if (column && g.Count == 0) { add(g, "FPS", Label); glue(g, name, Label); } else add(g, name, Label);
                glue(g, double.IsNaN(val) ? "—" : Math.Round(val).ToString("0"), Value);
            };
            if (on("low")) lowPart("1%", v.Low);
            if (on("low01")) lowPart("0.1%", v.Low01);
            // the frame-time graph: in a line a piece of the frames' group, in a column a row of its own under it.
            // Without frames (the desktop, a program that draws nothing) there is no graph, not an empty place for it
            Piece graph = null;
            if (on("ftg") && v.Frames != null && v.Frames.Length >= 2)
            {
                var f = v.Frames;
                graph = new Piece("~" + f.Length + ":" + f.Sum().ToString("0", CultureInfo.InvariantCulture), Value, false) { Graph = f };
                if (!column) g.Add(graph);
            }
            if (g.Count > 0) frames.Add(g);
            if (graph != null && column) frames.Add(new List<Piece> { graph });
            if (!column) all.AddRange(frames);

            g = open();
            if (on("ct")) cell(g, "t", v.CpuTemp > 0 ? v.CpuTemp + "°" : "—", v.CpuTemp >= 90 ? Hot : Value);
            if (on("cc") && !double.IsNaN(v.CpuMhz) && v.CpuMhz > 0) { cell(g, "c", (v.CpuMhz / 1000).ToString("0.0", num), Value); glue(g, en ? "GHz" : "ГГц", Unit); }
            if (on("cl") && !double.IsNaN(v.CpuLoad)) cell(g, "l", Math.Round(Math.Max(0, Math.Min(100, v.CpuLoad))).ToString("0") + "%", Value);
            if (on("cw") && !double.IsNaN(v.CpuW) && v.CpuW > 0) { cell(g, "w", Math.Round(v.CpuW).ToString("0"), Value); glue(g, watt, Unit); }
            if (on("cf") && v.CpuFan >= 0) { cell(g, "f", v.CpuFan.ToString(), Value); glue(g, rpm, Unit); }
            close(g, "CPU");

            if (v.Gpu)
            {
                g = open();
                if (on("gt")) cell(g, "t", v.GpuTemp > 0 ? v.GpuTemp + "°" : "—", v.GpuTemp >= 85 ? Hot : Value);
                if (on("gc") && v.GpuMhz > 0) { cell(g, "c", v.GpuMhz.ToString(), Value); glue(g, en ? "MHz" : "МГц", Unit); }
                if (on("gl") && v.GpuLoad >= 0) cell(g, "l", v.GpuLoad + "%", Value);
                if (on("gw") && !double.IsNaN(v.GpuW) && v.GpuW > 0) { cell(g, "w", Math.Round(v.GpuW).ToString("0"), Value); glue(g, watt, Unit); }
                if (on("gv") && !double.IsNaN(v.VramGb)) { cell(g, "m", v.VramGb.ToString("0.0", num), Value); glue(g, gb, Unit); }
                if (on("gf") && v.GpuFan >= 0) { cell(g, "f", v.GpuFan.ToString(), Value); glue(g, rpm, Unit); }
                close(g, "GPU");
            }

            g = open();
            if (on("ram") && !double.IsNaN(v.RamGb)) { cell(g, "m", v.RamGb.ToString("0.0", num), Value); glue(g, gb, Unit); }
            close(g, "RAM");

            g = open();
            if (on("bat") && v.Battery >= 0) add(g, v.Battery + "%", v.Battery <= 15 ? Hot : Value);
            // in a column the battery and the time share a row (the user, 27.11: one value each, no need for a row each)
            bool timeOn = on("time") && v.Time.Length > 0;
            if (column && g.Count > 0 && timeOn) { add(g, v.Time, Value); timeOn = false; }
            close(g, "BAT");

            g = open();
            if (timeOn) add(g, v.Time, Value);
            close(g, null);
            if (column) all.AddRange(frames);
            return all;
        }

        // the plate with the groups on it: a pill with one line, or a rounded card with a row per group. px: the text's
        // height in pixels, k: the monitor's scaling. edge: how far the plate keeps from the edge of the screen
        static Bitmap Render(List<List<Piece>> groups, bool column, Font font, float px, float k, out int edge)
        {
            int padX = (int)Math.Round(10 * k), padY = (int)Math.Round(5 * k), gap = (int)Math.Round(5 * k), group = (int)Math.Round(14 * k);
            var fmt = StringFormat.GenericTypographic;
            var fam = font.FontFamily;
            float ascent = fam.GetCellAscent(font.Style) * px / fam.GetEmHeight(font.Style);
            var widths = groups.Select(l => new float[l.Count]).ToArray();
            int textH;
            using (var probe = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(probe))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int a = 0; a < groups.Count; a++)
                    for (int i = 0; i < groups[a].Count; i++) widths[a][i] = groups[a][i].Graph != null ? (float)Math.Round(px * 8) : g.MeasureString(groups[a][i].Text, font, 10000, fmt).Width;
                textH = (int)Math.Ceiling(font.GetHeight(g));
            }
            int wide = (int)Math.Round(gap * 1.9f); // between two readings; a glued piece keeps the small gap
            Func<int, int, int> before = (a, i) => groups[a][i].Glued ? gap : wide;
            Func<int, int, float> rowWidth = (a, from) => { float t = 0; for (int i = from; i < groups[a].Count; i++) t += widths[a][i] + (i > from ? before(a, i) : 0); return t; };

            if (!column)
            {
                // 16.11: the plate is half as tall again as the text with its margins needs (the user asked for a bigger
                // plate around the same text); the text stands in the middle of it. The plate is a pill, its ends are
                // half circles; the text keeps clear of the round ends
                int h = (int)Math.Round((textH + 2 * padY) * 1.5f);
                padX = Math.Max(padX, (int)Math.Round(h * 0.5f));
                float total = 0;
                for (int a = 0; a < groups.Count; a++) total += rowWidth(a, 0) + (a > 0 ? group : 0);
                int w = (int)Math.Ceiling(total) + 2 * padX;
                float ty = (h - textH) / 2f - textH * 0.05f; // a line of this font has more room under the letters than over them
                // 16.11: the plate stands from the edge of the screen by the distance its letters keep from its own top
                // edge: where the line of text starts, down to its baseline, back up by the height of a capital letter
                // (0.70 of the size in Segoe UI)
                edge = Math.Max(0, (int)Math.Round(ty + ascent - 0.70f * px));
                var bmp = new Bitmap(Math.Max(1, w), h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var path = new GraphicsPath())
                    {
                        float d = h - 1;
                        path.AddArc(0, 0, d, d, 90, 180); path.AddArc(w - 1 - d, 0, d, d, 270, 180);
                        path.CloseFigure();
                        using (var b = new SolidBrush(Color.FromArgb(165, 12, 12, 12))) g.FillPath(b, path);
                    }
                    float x = padX;
                    for (int a = 0; a < groups.Count; a++)
                    {
                        if (a > 0) x += group;
                        for (int i = 0; i < groups[a].Count; i++)
                        {
                            if (i > 0) x += before(a, i);
                            if (groups[a][i].Graph != null) DrawGraph(g, groups[a][i].Graph, new RectangleF(x, (h - textH * 0.95f) / 2f, widths[a][i], textH * 0.95f), k);
                            else using (var b = new SolidBrush(groups[a][i].Color)) g.DrawString(groups[a][i].Text, font, b, x, ty, fmt);
                            x += widths[a][i];
                        }
                    }
                }
                return bmp;
            }
            else
            {
                // a row per group; the rows' names (the first piece of a row that has one) stand in a column of their
                // own, the values start on one vertical
                Func<int, bool> named = a => groups[a].Count > 1 && groups[a][0].Color == Label;
                float nameW = 0;
                for (int a = 0; a < groups.Count; a++) if (named(a)) nameW = Math.Max(nameW, widths[a][0]);
                int nameGap = (int)Math.Round(9 * k), rowH = textH + (int)Math.Round(3 * k), padV = (int)Math.Round(8 * k);
                padX = (int)Math.Round(13 * k);
                // 27.11: the rows with readings by kind (processor, graphics card, memory) make a table: a column per
                // kind, in one order, as wide as its widest reading; a reading stands right-aligned in its column, so
                // numbers stand under numbers and units under units, and a kind a row lacks leaves its place empty.
                // A row whose only reading is of a kind no other row has (memory without the card's memory) is not
                // part of the table: its value stands right after its name
                const string kinds = "tclwmf";
                Func<int, List<int>> starts = a => Enumerable.Range(0, groups[a].Count).Where(i => groups[a][i].Kind != null).ToList();
                Func<int, int, int> cellEnd = (a, i) => { int j = i + 1; while (j < groups[a].Count && groups[a][j].Kind == null && groups[a][j].Glued) j++; return j; };
                Func<int, int, float> cellWidth = (a, i) => { float t = 0; for (int j = i, e = cellEnd(a, i); j < e; j++) t += widths[a][j] + (j > i ? gap : 0); return t; };
                var kindRows = new Dictionary<string, int>();
                for (int a = 0; a < groups.Count; a++) foreach (int i in starts(a)) { int n; kindRows.TryGetValue(groups[a][i].Kind, out n); kindRows[groups[a][i].Kind] = n + 1; }
                Func<int, bool> tabled = a => { var st = starts(a); return st.Count > 1 || st.Count == 1 && kindRows[groups[a][st[0]].Kind] > 1; };
                var colW = new Dictionary<string, float>(); var colX = new Dictionary<string, float>();
                for (int a = 0; a < groups.Count; a++)
                    if (tabled(a)) foreach (int i in starts(a)) { float was; colW.TryGetValue(groups[a][i].Kind, out was); colW[groups[a][i].Kind] = Math.Max(was, cellWidth(a, i)); }
                int colGap = (int)Math.Round(gap * 2.8f);
                float cx = nameW + nameGap;
                foreach (char c in kinds) { string kd = c.ToString(); if (!colW.ContainsKey(kd)) continue; colX[kd] = cx; cx += colW[kd] + colGap; }
                float tableW = colX.Count > 0 ? cx - colGap : 0;
                float widest = 0;
                for (int a = 0; a < groups.Count; a++) widest = Math.Max(widest, tabled(a) ? tableW : named(a) ? nameW + nameGap + rowWidth(a, 1) : rowWidth(a, 0));
                // the graph is a row of its own, half as tall again, as wide as the card
                Func<int, bool> graphRow = a => groups[a].Count == 1 && groups[a][0].Graph != null;
                var top = new int[groups.Count + 1]; top[0] = padV;
                for (int a = 0; a < groups.Count; a++) top[a + 1] = top[a] + (graphRow(a) ? (int)Math.Round(rowH * 1.5f) : rowH);
                int w = (int)Math.Ceiling(widest) + 2 * padX, h = top[groups.Count] + padV;
                edge = (int)Math.Round(12 * k);
                var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var path = new GraphicsPath())
                    {
                        float d = Math.Min(h - 1, Math.Min(w - 1, (int)Math.Round(rowH * 0.9f)));
                        path.AddArc(0, 0, d, d, 180, 90); path.AddArc(w - 1 - d, 0, d, d, 270, 90);
                        path.AddArc(w - 1 - d, h - 1 - d, d, d, 0, 90); path.AddArc(0, h - 1 - d, d, d, 90, 90);
                        path.CloseFigure();
                        using (var b = new SolidBrush(Color.FromArgb(165, 12, 12, 12))) g.FillPath(b, path);
                    }
                    for (int a = 0; a < groups.Count; a++)
                    {
                        if (graphRow(a)) { int gh = top[a + 1] - top[a]; DrawGraph(g, groups[a][0].Graph, new RectangleF(padX, top[a] + gh * 0.12f, w - 2 * padX, gh * 0.76f), k); continue; }
                        float x = padX, y = top[a] + (rowH - textH) / 2f - textH * 0.05f;
                        if (tabled(a))
                        {
                            using (var b = new SolidBrush(groups[a][0].Color)) g.DrawString(groups[a][0].Text, font, b, x, y, fmt); // the row's name
                            foreach (int i in starts(a))
                            {
                                string kd = groups[a][i].Kind;
                                x = padX + colX[kd] + colW[kd] - cellWidth(a, i);
                                for (int j = i, e = cellEnd(a, i); j < e; j++)
                                {
                                    if (j > i) x += gap;
                                    using (var b = new SolidBrush(groups[a][j].Color)) g.DrawString(groups[a][j].Text, font, b, x, y, fmt);
                                    x += widths[a][j];
                                }
                            }
                            continue;
                        }
                        for (int i = 0; i < groups[a].Count; i++)
                        {
                            if (i == 1 && named(a)) x = padX + nameW + nameGap; else if (i > 0) x += before(a, i);
                            using (var b = new SolidBrush(groups[a][i].Color)) g.DrawString(groups[a][i].Text, font, b, x, y, fmt);
                            x += widths[a][i];
                        }
                    }
                }
                return bmp;
            }
        }

        // the frame times as a line, oldest on the left: the bottom is 0 ms, a typical frame (the middle one by length)
        // stands at two fifths of the height, the top is two and a half typical frames. Where a frame took more than two
        // typical ones the tooth is in the warning colour. More frames than pixels: each pixel shows its slowest frame
        static void DrawGraph(Graphics g, float[] ms, RectangleF r, float k)
        {
            using (var floor = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, k))) g.DrawLine(floor, r.Left, r.Bottom - 0.5f, r.Right, r.Bottom - 0.5f);
            if (ms == null || ms.Length < 2 || r.Width < 4) return;
            var sorted = (float[])ms.Clone(); Array.Sort(sorted);
            float typical = Math.Max(0.1f, sorted[sorted.Length / 2]), most = typical * 2.5f;
            int cols = Math.Max(2, Math.Min(ms.Length, (int)r.Width));
            var pts = new PointF[cols]; var worst = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                int from = (int)((long)c * ms.Length / cols), to = Math.Max(from + 1, (int)((long)(c + 1) * ms.Length / cols));
                float m = 0; for (int i = from; i < to && i < ms.Length; i++) m = Math.Max(m, ms[i]);
                worst[c] = m;
                pts[c] = new PointF(r.Left + c * (r.Width - 1) / (cols - 1), r.Bottom - 1 - Math.Min(m, most) / most * (r.Height - 2));
            }
            float width = Math.Max(1f, 1.2f * k), usual = r.Bottom - 1 - typical / most * (r.Height - 2);
            using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), width) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
            using (var hot = new Pen(Hot, width))
                for (int c = 0; c < cols; c++) if (worst[c] > typical * 2) g.DrawLine(hot, pts[c].X, usual, pts[c].X, pts[c].Y);
        }

        static Font MakeFont(float px) { return new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel); }

        // --overlay-png <out.png> [items] [--column] [--size n]: the overlay with made-up numbers over a grey picture (a check)
        public static void RenderTo(string file, string items, bool column, int size)
        {
            // made-up frames: about 7 ms with a little unrest, a few slow ones
            var rnd = new Random(7);
            var frames = Enumerable.Range(0, 1400).Select(i => (float)(6.9 + rnd.NextDouble() * 1.2 + (i % 233 == 232 ? 9 + rnd.NextDouble() * 14 : 0) + (i > 900 && i < 940 ? 2.5 : 0))).ToArray();
            var v = new Vals { Frames = frames, Low01 = 61.4, Fps = 143.6, Low = 97.2, CpuTemp = 68, CpuMhz = 4310, CpuLoad = 34.6, CpuW = 54.2, CpuFan = 2600, Gpu = true, GpuTemp = 61, GpuMhz = 2310, GpuLoad = 98, GpuW = 79.6, VramGb = 5.13, GpuFan = 2400, RamGb = 14.24, Battery = 87, Time = "21:47" };
            var keys = (items ?? DefaultItems).Split(',');
            float px = SizePx[Math.Max(0, Math.Min(SizePx.Length - 1, size))];
            int edge;
            using (var font = MakeFont(px))
            using (var plate = Render(Groups(v, k => keys.Contains(k), column), column, font, px, 1f, out edge))
            using (var pic = new Bitmap(plate.Width + 2 * (12 + edge), plate.Height + 2 * (12 + edge)))
            {
                using (var g = Graphics.FromImage(pic))
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, pic.Width, pic.Height), Color.FromArgb(70, 96, 120), Color.FromArgb(150, 130, 100), 20f))
                {
                    g.FillRectangle(b, 0, 0, pic.Width, pic.Height);
                    g.DrawImage(plate, 12 + edge, 12 + edge);
                }
                pic.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        static void Post(Action<OverlayForm> a)
        {
            var u = ui; var f = form;
            if (u == null || f == null) return;
            u.Post(_ => { try { a(f); } catch (Exception ex) { Store.Log("оверлей: " + ex.Message); } }, null);
        }

        class OverlayForm : Form
        {
            [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref Blend pblend, int flags);
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
            [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
            [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

            readonly Settings s;
            readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
            Sensors sensors;
            volatile bool reading;
            DateTime read = DateTime.MinValue;
            // the last readings, written by the worker (a new object each time, so the picture never sees half of one)
            volatile Vals vals = new Vals();
            double low = double.NaN, low01 = double.NaN; float[] frames = new float[0]; DateTime lowAt = DateTime.MinValue;
            Font font; float fontPx;
            string shownKey = "";

            public OverlayForm(Settings s)
            {
                this.s = s;
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000); Size = new Size(10, 10);
                timer.Tick += (o, e) => Tick();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000 | 0x8; return cp; } // tool window, no activate, click-through, layered, topmost
            }

            public void On() { shownKey = ""; timer.Start(); Tick(); }
            public void Off() { timer.Stop(); if (Visible) Hide(); }

            // the monitor to show on, or null: nothing to show over now
            Osd.Mon Target()
            {
                var mons = Osd.Monitors();
                var active = Osd.ActiveMonitor();
                var m = mons.FirstOrDefault(x => x.Handle == active) ?? mons.FirstOrDefault(x => x.Primary) ?? mons.FirstOrDefault();
                if (m == null) return null;
                return s.OverlayAlways || Osd.FullscreenOn(m) || DateTime.Now < previewUntil ? m : null;
            }

            void Tick()
            {
                if (!s.OverlayOn) { Off(); return; }
                Osd.Mon m = null;
                try { m = Target(); } catch { }
                if (m == null) { if (Visible) Hide(); return; }
                Poll();
                uint pid = 0;
                try { GetWindowThreadProcessId(GetForegroundWindow(), out pid); } catch { }
                int drawer;
                double fps = FpsOf(pid, out drawer);
                // the slow frames are counted once a second (a sort of the last half minute of frames)
                if (Fps.KeepLong && (DateTime.Now - lowAt).TotalMilliseconds >= 950)
                {
                    lowAt = DateTime.Now;
                    bool draws = !double.IsNaN(fps);
                    low = draws && Has(s, "low") ? Fps.Low(drawer) : double.NaN;
                    low01 = draws && Has(s, "low01") ? Fps.Low(drawer, true) : double.NaN;
                    frames = draws && Has(s, "ftg") ? Fps.FrameTimes(drawer, 10) : new float[0];
                }
                Draw(m, fps);
            }

            // the frame rate of the program in front, and the process it draws from. A game draws from the process its
            // window belongs to; a program built on a browser engine (Chrome, Electron) draws from a helper process of
            // the same name, so when the window's own process draws nothing, its namesakes are looked at (their list is
            // kept for 5 s)
            uint famPid; DateTime famAt = DateTime.MinValue; int[] famIds = new int[0];
            double FpsOf(uint pid, out int drawer)
            {
                drawer = (int)pid;
                double f = Fps.Of((int)pid);
                if (!double.IsNaN(f) || pid == 0) return f;
                try
                {
                    if (pid != famPid || (DateTime.Now - famAt).TotalSeconds > 5)
                    {
                        famPid = pid; famAt = DateTime.Now;
                        string name; using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName;
                        famIds = System.Diagnostics.Process.GetProcessesByName(name).Select(x => { int id = x.Id; x.Dispose(); return id; }).Where(id => id != pid).ToArray();
                    }
                    foreach (var id in famIds) { double v = Fps.Of(id); if (!double.IsNaN(v) && (double.IsNaN(f) || v > f)) { f = v; drawer = id; } }
                }
                catch { }
                return f;
            }

            // the sensors once a second, off this thread (the driver calls take a few milliseconds each)
            void Poll()
            {
                if (reading || (DateTime.Now - read).TotalMilliseconds < 950) return;
                reading = true; read = DateTime.Now;
                Task.Run(() =>
                {
                    try
                    {
                        if (sensors == null) sensors = new Sensors { SkipRam = true };
                        sensors.Update(true);
                        var c = sensors.Cpu; var g = sensors.Gpu; var r = sensors.Ram;
                        vals = new Vals
                        {
                            CpuTemp = c.TempC, CpuMhz = c.ClockMHz, CpuLoad = c.LoadPct, CpuW = c.PowerW, CpuFan = c.FanRpm,
                            Gpu = Hw.Nvidia, GpuTemp = g.Ok && g.TempC > 0 ? (int)g.TempC : -1, GpuMhz = g.Ok ? (int)g.ClockMHz : -1, GpuLoad = g.Ok ? (int)g.LoadPct : -1,
                            GpuW = g.Ok ? g.PowerW : double.NaN, VramGb = g.Ok && g.VramTotal > 0 ? g.VramUsed / 1073741824.0 : double.NaN, GpuFan = g.FanRpm,
                            RamGb = r.Total > 0 ? r.Used / 1073741824.0 : double.NaN,
                        };
                    }
                    catch { }
                    finally { reading = false; }
                });
            }

            void Draw(Osd.Mon m, double fps)
            {
                // the readings of the worker, with what is known at once: the frames, the battery, the clock
                var r = vals;
                var v = new Vals
                {
                    Fps = fps, Low = low, Low01 = low01, Frames = frames, CpuTemp = r.CpuTemp, CpuMhz = r.CpuMhz, CpuLoad = r.CpuLoad, CpuW = r.CpuW, CpuFan = r.CpuFan, Gpu = r.Gpu, GpuTemp = r.GpuTemp, GpuMhz = r.GpuMhz,
                    GpuLoad = r.GpuLoad, GpuW = r.GpuW, VramGb = r.VramGb, GpuFan = r.GpuFan, RamGb = r.RamGb, Time = DateTime.Now.ToString("HH:mm"),
                };
                try { float b = SystemInformation.PowerStatus.BatteryLifePercent; v.Battery = b >= 0 && b <= 1 ? (int)Math.Round(b * 100) : -1; } catch { }
                var groups = Groups(v, key => Has(s, key), s.OverlayColumn);
                if (groups.Count == 0) { if (Visible) Hide(); return; } // everything is switched off in the settings

                // 16.11: the size follows Windows' scaling of the monitor it is shown on (150 % on the laptop's own screen,
                // 100 % on the monitor beside it), like everything else on that screen. For an hour it followed the
                // screen's resolution instead, after the user found the line too big on the laptop's screen; he then
                // asked for the scaling back: the screen is small and dense, and its scaling is there for that reason.
                // It never follows the scale of Red Tune's own window (set to 150 % here, that came on top at first).
                float k = 1f;
                try { uint dx, dy; if (m.Handle != IntPtr.Zero && GetDpiForMonitor(m.Handle, 0, out dx, out dy) == 0 && dx >= 96) k = dx / 96f; } catch { }
                string key = string.Join("|", groups.Select(l => string.Join(" ", l.Select(p => p.Text)))) + "|" + m.Bounds + "|" + s.OverlayCorner + "|" + s.OverlaySize + "|" + s.OverlayColumn + "|" + k + "|" + Osd.FullscreenOn(m);
                if (key == shownKey && Visible) { OnTop(); return; } // nothing changed: the picture stays
                shownKey = key;

                float px = SizePx[Math.Max(0, Math.Min(SizePx.Length - 1, s.OverlaySize))] * k;
                if (font == null || Math.Abs(fontPx - px) > 0.1f) { if (font != null) font.Dispose(); font = MakeFont(px); fontPx = px; }
                int margin = (int)Math.Round(12 * k), edge;
                using (var bmp = Render(groups, s.OverlayColumn, font, px, k, out edge))
                {
                    int w = bmp.Width, h = bmp.Height;
                    // over a full-screen program the corner of the screen; over the desktop («Всегда») the corner of the work area, clear of the taskbar
                    var area = Osd.FullscreenOn(m) ? m.Bounds : m.Work;
                    // 16.11: the plate stands from the edge of the screen by the distance its letters keep from its own
                    // top edge (asked for after the first game: the plate sat too close to the top; Render gives that
                    // distance). At the bottom places it is kept from the bottom edge the same way.
                    var at = s.OverlayCorner == 4 ? new Point(area.Left + (area.Width - w) / 2, area.Top + edge)
                        : s.OverlayCorner == 5 ? new Point(area.Left + (area.Width - w) / 2, area.Bottom - edge - h)
                        : new Point(s.OverlayCorner % 2 == 0 ? area.Left + margin : area.Right - margin - w, s.OverlayCorner < 2 ? area.Top + edge : area.Bottom - edge - h);
                    Push(bmp, at);
                }
                if (!Visible) Show();
                OnTop();
            }

            void Push(Bitmap pic, Point at)
            {
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hbmp = pic.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(mem, hbmp);
                    var size = pic.Size; var src = Point.Empty;
                    var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screen);
                    if (hbmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hbmp); }
                    DeleteDC(mem);
                }
            }

            // above the game's own window, which is topmost too in borderless mode
            void OnTop() { try { SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } catch { } } // HWND_TOPMOST; NOSIZE, NOMOVE, NOACTIVATE
        }
    }
}
