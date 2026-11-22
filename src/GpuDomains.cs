using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RedTune
{
    // 22.11 (the user: show the XBAR / SYS / Video clocks mVolt+ tunes): the GPU's per-domain V/F curves, READ ONLY. The
    // driver keeps a curve for each clock domain besides the core: XBAR (the crossbar), SYS (the system domain) and Video
    // (the video engine). They are read through private NVAPI "domain V/F" functions; their ids, sizes, versions and the
    // layout were taken from mVolt+ 0.50.13 by static analysis (never run) and checked on the R7000P's RTX 50 laptop card,
    // driver 621.14 (RedTune-devtools/gpu-domain-vf-notes.md):
    //   A 0x8895B510 points INFO   (0x68604, v1): mask of 2048 points at +4; point i at 0x304+i*0x68: +0 type, +0x28 µV
    //   C 0x5A769461 programs      (0x12EE28, v0): mask at +4; program p at 0x128+p*0x1300: +0 type (8 = curve), +0xC u16 first/last point
    //   D 0x57B5A5DF clock domains (0x86AC, v4): mask at +8; domain k at 0xAC+k*0x430: +4 key, +0x3C/+0x3D byte first/last program
    //   E 0x7FEE9032 points STATUS (0x1E8604, v0, A's mask at +4): point i at 0x304+i*0x1E8: +36 the MHz the driver runs there
    //   B 0xDA025C3E points CONTROL (0x424604, v5, A's mask at +4): point i at 0x304+i*0x424: +0 type, +0x24 non-zero = % encoding, +0x38 offset kHz
    // Keys as mVolt names them: 0 core, 1 XBAR, 2 SYS, 21 Video. Read() only reads; SetOffset (setter 0xFEC00D04) is used
    // only by the --gpu-domain-set check until it is tried on the card with the user's word.
    static class GpuDomains
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] static extern IntPtr Q(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out] IntPtr[] gpus, out int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CallFn(IntPtr gpu, IntPtr buf);

        public class Curve { public int Key; public string Name; public int[] Mv, Mhz, OffsetKhz; }

        public static readonly int[] Keys = { 0, 1, 2, 21 };
        public static string NameOf(int key) { return key == 0 ? L.T("Ядро, заводская") : key == 1 ? "XBAR" : key == 2 ? "SYS" : key == 21 ? "Video" : key.ToString(); }

        public static string Error;
        static readonly object gate = new object();

        static byte[] Call(IntPtr g, uint id, int size, int ver, byte[] mask)
        {
            var p = Q(id); if (p == IntPtr.Zero) return null;
            var f = (CallFn)Marshal.GetDelegateForFunctionPointer(p, typeof(CallFn));
            // mVolt+ gives the driver a buffer 4 KB longer than the struct; done the same here
            var buf = Marshal.AllocHGlobal(size + 0x1000);
            try
            {
                var data = new byte[size + 0x1000];
                BitConverter.GetBytes(size + (ver << 16)).CopyTo(data, 0);
                if (mask != null) Array.Copy(mask, 0, data, 4, mask.Length);
                Marshal.Copy(data, 0, buf, data.Length);
                if (f(g, buf) != 0) return null;
                var res = new byte[size]; Marshal.Copy(buf, res, 0, size);
                return BitConverter.ToInt32(res, 0) == size + (ver << 16) ? res : null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static uint U(byte[] b, long o) { return o >= 0 && o + 4 <= b.Length ? BitConverter.ToUInt32(b, (int)o) : 0; }
        static bool Bit(byte[] b, int at, int i) { return (U(b, at + (i >> 5) * 4) >> (i & 31) & 1) != 0; }

        // one reading of the whole layout: A (info), B (control), C (programs), D (domains), E (status), and for each of
        // Keys the indices of its voltage-defined points
        class Snap { public IntPtr Gpu; public byte[] A, B, C, D, E; public Dictionary<int, List<int>> Points = new Dictionary<int, List<int>>(); }

        const int ControlSize = 0x424604;
        static int OffsetAt(Snap s, int i) { return (int)U(s.B, 0x304 + i * 0x424L + 0x38); } // kHz
        static bool Percent(Snap s, int i) { return U(s.B, 0x304 + i * 0x424L + 0x24) != 0; } // that point's offset is in %, not kHz

        static Snap Take()
        {
            var init = Q(0x0150E828); var en = Q(0xE5AC921F);
            if (init == IntPtr.Zero || en == IntPtr.Zero) { Error = L.T("драйвер NVIDIA не ответил"); return null; }
            if (((InitFn)Marshal.GetDelegateForFunctionPointer(init, typeof(InitFn)))() != 0) { Error = L.T("драйвер NVIDIA не ответил"); return null; }
            var list = new IntPtr[64]; int n;
            if (((EnumFn)Marshal.GetDelegateForFunctionPointer(en, typeof(EnumFn)))(list, out n) != 0 || n < 1) { Error = L.T("видеокарта NVIDIA не найдена"); return null; }
            var s = new Snap { Gpu = list[0] };
            s.A = Call(s.Gpu, 0x8895B510, 0x68604, 1, null);
            byte[] mask = null;
            if (s.A != null) { mask = new byte[256]; Array.Copy(s.A, 4, mask, 0, 256); }
            s.B = mask == null ? null : Call(s.Gpu, 0xDA025C3E, ControlSize, 5, mask);
            s.C = s.B == null ? null : Call(s.Gpu, 0x5A769461, 0x12EE28, 0, null);
            s.D = s.C == null ? null : Call(s.Gpu, 0x57B5A5DF, 0x86AC, 4, null);
            s.E = s.D == null ? null : Call(s.Gpu, 0x7FEE9032, 0x1E8604, 0, mask);
            if (s.E == null) { Error = L.T("драйвер не отдаёт кривые доменов"); return null; }
            // CONTROL must select no secondary points (mVolt+ does not go on otherwise)
            for (int i = 0; i < 256; i++) if (s.B[0x212304 + i] != 0) { Error = L.T("драйвер не отдаёт кривые доменов"); return null; }
            uint domMask = U(s.D, 8);
            foreach (int key in Keys)
                for (int k = 0; k < 32; k++)
                {
                    if ((domMask >> k & 1) == 0) continue;
                    long o = 0xAC + k * 0x430L;
                    if (o + 0x40 > s.D.Length || U(s.D, o + 4) != key) continue;
                    int p0 = s.D[o + 0x3C], p1 = s.D[o + 0x3D];
                    if (p0 == 0xFF || p1 < p0 || p1 >= 255) break;
                    var pts = new List<int>();
                    for (int p = p0; p <= p1; p++)
                    {
                        if (!Bit(s.C, 4, p)) continue;
                        long po = 0x128 + p * 0x1300L;
                        if (U(s.C, po) != 8) continue; // a voltage curve; type 7 is a table of fixed clocks
                        uint span = U(s.C, po + 0xC); int i0 = (int)(span & 0xFFFF), i1 = (int)(span >> 16);
                        for (int i = i0; i <= i1 && i < 2048; i++)
                        {
                            if (!Bit(s.A, 4, i)) continue;
                            uint ta = U(s.A, 0x304 + i * 0x68L);
                            // a voltage-defined point (13 here, 8 on other cards) that CONTROL describes the same way
                            if ((ta != 13 && ta != 8) || U(s.B, 0x304 + i * 0x424L) != ta) continue;
                            if (U(s.A, 0x304 + i * 0x68L + 0x28) == 0) continue;
                            pts.Add(i);
                        }
                    }
                    if (pts.Count > 1) s.Points[key] = pts;
                    break;
                }
            return s;
        }

        // the core, XBAR, SYS and Video curves the driver runs now (those it has), or null with the reason in Error
        public static List<Curve> Read()
        {
            lock (gate)
            {
                try
                {
                    var s = Take(); if (s == null) return null;
                    var res = new List<Curve>();
                    foreach (int key in Keys)
                    {
                        List<int> pts; if (!s.Points.TryGetValue(key, out pts)) continue;
                        var mv = new List<int>(); var mhz = new List<int>(); var off = new List<int>();
                        foreach (int i in pts)
                        {
                            int uv = (int)U(s.A, 0x304 + i * 0x68L + 0x28), f = (int)U(s.E, 0x304 + i * 0x1E8L + 36);
                            if (f <= 0) continue;
                            mv.Add((int)Math.Round(uv / 1000.0)); mhz.Add(f); off.Add(Percent(s, i) ? 0 : OffsetAt(s, i));
                        }
                        if (mv.Count > 1) res.Add(new Curve { Key = key, Name = NameOf(key), Mv = mv.ToArray(), Mhz = mhz.ToArray(), OffsetKhz = off.ToArray() });
                    }
                    if (res.Count(x => x.Key != 0) == 0) { Error = L.T("на этой видеокарте нет кривых XBAR, SYS и Video"); return null; }
                    Error = null;
                    return res;
                }
                catch (DllNotFoundException) { Error = L.T("драйвер NVIDIA не найден"); return null; }
                catch (Exception ex) { Error = ex.Message; return null; }
            }
        }

        // 22.11: the clock a domain runs at now, as mVolt+ measures it (FUN_140250110; id 0x527FC458 decoded from its table,
        // called with the domain key: 0 core, 4 memory, 1 XBAR, 2 SYS, 21 Video). Struct v1 of 12 bytes (0x1000C) with 4 KB of guard after it; +4 the key, +8
        // the answer (-1 = none); the rest is filled with 0xA5 and must come back so. Read only. -1 when there is no answer.
        public static int LastStatus;
        public static int Measured(int key)
        {
            lock (gate)
            {
                try
                {
                    var p = Q(0x527FC458); LastStatus = -7777; if (p == IntPtr.Zero) return -1;
                    var init = Q(0x0150E828); var en = Q(0xE5AC921F);
                    if (init == IntPtr.Zero || en == IntPtr.Zero || ((InitFn)Marshal.GetDelegateForFunctionPointer(init, typeof(InitFn)))() != 0) return -1;
                    var list = new IntPtr[64]; int n;
                    if (((EnumFn)Marshal.GetDelegateForFunctionPointer(en, typeof(EnumFn)))(list, out n) != 0 || n < 1) return -1;
                    var f = (CallFn)Marshal.GetDelegateForFunctionPointer(p, typeof(CallFn));
                    const int size = 0x100C;
                    var data = new byte[size];
                    for (int i = 0; i < size; i++) data[i] = 0xA5;
                    BitConverter.GetBytes(0x1000C).CopyTo(data, 0); // v1, 12 bytes: the 4 KB after it is a guard
                    BitConverter.GetBytes(key).CopyTo(data, 4);
                    BitConverter.GetBytes(0).CopyTo(data, 8);
                    var buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.Copy(data, 0, buf, size);
                        LastStatus = f(list[0], buf); if (LastStatus != 0) return -1;
                        var res = new byte[size]; Marshal.Copy(buf, res, 0, size);
                        for (int i = 12; i < size; i++) if (res[i] != 0xA5) { LastStatus = -8888; return -1; }
                        return BitConverter.ToInt32(res, 8);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { return -1; }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetFn(IntPtr gpu, IntPtr buf);
        public static int MaxOffsetMhz = 300, MinOffsetMhz = -100; // 22.11: the user asked for −100…+300 (XBAR +15 checked under load, more not tried yet)

        // 22.11, worked out from mVolt+ (FUN_140274860 / FUN_14025fbd0 / FUN_1402738d0), NOT run yet: every point of one
        // domain (XBAR 1, SYS 2, Video 21; never the core) gets the same offset, in kHz at +0x38 of its CONTROL entry. The
        // buffer sent is CONTROL as read, with the mask at +4 holding only the points changed and the secondary mask at
        // 0x212304 zero; the setter 0xFEC00D04 gets 4 KB of 0xCD past the struct, which must come back untouched. Before:
        // the layout is read twice and must match; after: read again, every point must hold the new offset, else the old
        // offsets are written back. The driver forgets it on a reboot. Returns null when done, else the reason.
        public static string SetOffset(int key, int mhz)
        {
            if (key != 1 && key != 2 && key != 21) return "only XBAR, SYS and Video";
            if (mhz > MaxOffsetMhz || mhz < MinOffsetMhz) return "offset outside " + MinOffsetMhz + "…+" + MaxOffsetMhz + " MHz";
            lock (gate)
            {
                try
                {
                    var s = Take(); if (s == null) return Error;
                    List<int> pts; if (!s.Points.TryGetValue(key, out pts)) return "no such curve";
                    if (pts.Any(i => Percent(s, i))) return "the curve uses percentages";
                    var old = pts.ToDictionary(i => i, i => OffsetAt(s, i));
                    var again = Take(); List<int> pts2;
                    if (again == null || !again.Points.TryGetValue(key, out pts2) || !pts2.SequenceEqual(pts)) return "the layout changed while reading; nothing written";
                    int st = Write(s, pts.ToDictionary(i => i, i => mhz * 1000));
                    var after = Take();
                    if (st == 0 && after != null && pts.All(i => OffsetAt(after, i) == mhz * 1000)) return null;
                    int back = Write(after ?? s, old);
                    return "setter status " + st + (back == 0 ? "; the old offsets are back" : "; restoring failed too (" + back + "), a reboot clears it");
                }
                catch (Exception ex) { return ex.Message; }
            }
        }

        // the sliders' order: XBAR, SYS, Video
        public static readonly int[] Tunable = { 1, 2, 21 };

        // the offsets on the card now, MHz in Tunable's order (0 where a point has none), or null when there are no curves
        public static int[] Offsets(List<Curve> dc)
        {
            if (dc == null) return null;
            return Tunable.Select(k => { var c = dc.FirstOrDefault(x => x.Key == k); return c == null || c.OffsetKhz.Length == 0 ? 0 : c.OffsetKhz[0] / 1000; }).ToArray();
        }

        // three offsets (MHz, Tunable's order) onto the card: only the domains whose offset differs are written. null when
        // done, else the reasons
        public static string Apply(int[] mhz)
        {
            if (mhz == null || mhz.Length != Tunable.Length) return null;
            var now = Offsets(Read());
            if (now == null) return mhz.All(x => x == 0) ? null : Error;
            var errs = new List<string>();
            for (int k = 0; k < Tunable.Length; k++)
            {
                if (now[k] == mhz[k]) continue;
                var e = SetOffset(Tunable[k], mhz[k]);
                if (e != null) errs.Add(NameOf(Tunable[k]) + ": " + e);
            }
            return errs.Count == 0 ? null : string.Join("; ", errs);
        }

        static int Write(Snap s, Dictionary<int, int> khz)
        {
            var p = Q(0xFEC00D04); if (p == IntPtr.Zero) return -1;
            var f = (SetFn)Marshal.GetDelegateForFunctionPointer(p, typeof(SetFn));
            var data = new byte[ControlSize + 0x1000];
            Array.Copy(s.B, data, ControlSize);
            for (int i = ControlSize; i < data.Length; i++) data[i] = 0xCD;
            Array.Clear(data, 4, 256); Array.Clear(data, 0x212304, 256);
            foreach (var kv in khz)
            {
                data[4 + (kv.Key >> 3)] |= (byte)(1 << (kv.Key & 7));
                BitConverter.GetBytes(kv.Value).CopyTo(data, 0x304 + kv.Key * 0x424 + 0x38);
            }
            var buf = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, buf, data.Length);
                int st = f(s.Gpu, buf);
                var tail = new byte[0x1000]; Marshal.Copy(buf + ControlSize, tail, 0, tail.Length);
                if (st == 0 && tail.Any(b => b != 0xCD)) return -1000; // the driver wrote past the struct (mVolt+ answers -1000 too)
                return st;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static Color ColorOf(int key)
        {
            return key == 0 ? Theme.Muted : key == 1 ? Theme.Accent : key == 2 ? Theme.Warning : Color.FromArgb(0x6f, 0xa8, 0xe6);
        }
    }

    // the domain curves on one graph, voltage across, MHz up; the core dotted for comparison; hover shows every curve at
    // that voltage. Read only.
    class DomainGraph : Painted
    {
        List<GpuDomains.Curve> curves = new List<GpuDomains.Curve>();
        int hotMv = -1;

        public DomainGraph() { Height = Theme.S(260); BackColor = Theme.Card; DoubleBuffered = true; }

        public void Set(List<GpuDomains.Curve> c) { curves = c ?? new List<GpuDomains.Curve>(); hotMv = -1; Invalidate(); }

        RectangleF Plot { get { return new RectangleF(Theme.S(58), Theme.S(30), Width - Theme.S(76), Height - Theme.S(64)); } }
        int MinMv { get { return curves.Count > 0 ? curves.Min(c => c.Mv[0]) - 10 : 400; } }
        int MaxMv { get { return curves.Count > 0 ? curves.Max(c => c.Mv[c.Mv.Length - 1]) + 10 : 1250; } }
        int MaxMhz { get { return curves.Count > 0 ? (int)(Math.Ceiling(curves.Max(c => c.Mhz.Max()) / 500.0) * 500) : 3000; } }
        float X(int mv) { var p = Plot; return p.X + p.Width * (mv - MinMv) / (float)Math.Max(1, MaxMv - MinMv); }
        float Y(int mhz) { var p = Plot; return p.Bottom - p.Height * mhz / (float)Math.Max(1, MaxMhz); }

        static int At(GpuDomains.Curve c, int mv)
        {
            int best = 0; for (int k = 1; k < c.Mv.Length; k++) if (Math.Abs(c.Mv[k] - mv) < Math.Abs(c.Mv[best] - mv)) best = k;
            return best;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = Plot; int mv = -1;
            if (curves.Count > 0 && p.Contains(e.Location)) { var c = curves[0]; mv = c.Mv[At(c, (int)Math.Round(MinMv + (e.X - p.X) / p.Width * (MaxMv - MinMv)))]; }
            if (mv != hotMv) { hotMv = mv; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hotMv >= 0) { hotMv = -1; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            using (var grid = new Pen(Color.FromArgb(40, 255, 255, 255)))
            {
                for (int f = 0; f <= MaxMhz; f += 250)
                {
                    g.DrawLine(grid, p.X, Y(f), p.Right, Y(f));
                    if (f % 500 == 0) TextRenderer.DrawText(g, f.ToString(), Theme.Small, new Rectangle(0, (int)Y(f) - Theme.S(8), (int)p.X - Theme.S(6), Theme.S(16)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                for (int v = (MinMv + 49) / 50 * 50; v <= MaxMv; v += 50)
                {
                    g.DrawLine(grid, X(v), p.Y, X(v), p.Bottom);
                    if (v % 100 == 0) TextRenderer.DrawText(g, v.ToString(), Theme.Small, new Rectangle((int)X(v) - Theme.S(20), (int)p.Bottom + Theme.S(4), Theme.S(40), Theme.S(16)), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }
            TextRenderer.DrawText(g, L.T("МГц"), Theme.Small, new Rectangle((int)p.X + Theme.S(6), (int)p.Y + Theme.S(2), Theme.S(60), Theme.S(14)), Theme.Muted, TextFormatFlags.Left);
            TextRenderer.DrawText(g, L.T("мВ"), Theme.Small, new Rectangle((int)p.Right - Theme.S(30), (int)p.Bottom + Theme.S(18), Theme.S(40), Theme.S(16)), Theme.Muted, TextFormatFlags.Right);

            // the legend along the top
            int lx = (int)p.X;
            foreach (var c in curves)
            {
                var col = GpuDomains.ColorOf(c.Key);
                using (var pen = new Pen(col, Theme.S(2)) { DashStyle = c.Key == 0 ? DashStyle.Dot : DashStyle.Solid }) g.DrawLine(pen, lx, Theme.S(14), lx + Theme.S(18), Theme.S(14));
                var sz = TextRenderer.MeasureText(c.Name, Theme.Small);
                TextRenderer.DrawText(g, c.Name, Theme.Small, new Rectangle(lx + Theme.S(22), Theme.S(6), sz.Width + Theme.S(4), Theme.S(16)), Theme.Text2, TextFormatFlags.Left);
                lx += Theme.S(22) + sz.Width + Theme.S(18);
            }

            foreach (var c in curves.OrderBy(x => x.Key == 0 ? 0 : 1))
            {
                var pts = Enumerable.Range(0, c.Mv.Length).Select(k => new PointF(X(c.Mv[k]), Y(c.Mhz[k]))).ToArray();
                using (var pen = new Pen(GpuDomains.ColorOf(c.Key), c.Key == 0 ? 1.4f : Math.Max(1.5f, Theme.S(2))) { DashStyle = c.Key == 0 ? DashStyle.Dot : DashStyle.Solid })
                    g.DrawLines(pen, pts);
            }

            if (hotMv >= 0 && curves.Count > 0)
            {
                float x = X(hotMv);
                using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255)) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, x, p.Y, x, p.Bottom);
                var lines = new List<string> { hotMv + " " + L.T("мВ") };
                foreach (var c in curves)
                {
                    int k = At(c, hotMv);
                    if (Math.Abs(c.Mv[k] - hotMv) > 10) continue;
                    lines.Add(c.Name + ": " + c.Mhz[k] + " " + L.T("МГц"));
                    using (var b = new SolidBrush(GpuDomains.ColorOf(c.Key))) { float r = Theme.S(3); g.FillEllipse(b, x - r, Y(c.Mhz[k]) - r, 2 * r, 2 * r); }
                }
                var t = string.Join("\n", lines);
                var sz = TextRenderer.MeasureText(t, Theme.Body);
                var box = new Rectangle((int)(x + Theme.S(10) + sz.Width + Theme.S(8) > p.Right ? x - Theme.S(10) - sz.Width - Theme.S(8) : x + Theme.S(10)), (int)p.Y + Theme.S(4), sz.Width + Theme.S(8), sz.Height + Theme.S(6));
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Raised))) g.FillRectangle(b, box);
                TextRenderer.DrawText(g, t, Theme.Body, box, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
