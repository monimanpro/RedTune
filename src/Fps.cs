using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace RedTune
{
    // 16.11: frames per second of any program, counted the way PresentMon does it. Windows reports every frame a program
    // hands over for showing as an event (ETW); a real-time session of our own («RedTuneFps») listens to three sources:
    //   Microsoft-Windows-DXGI, event 42     a Direct3D 10 / 11 / 12 program calls Present
    //   Microsoft-Windows-D3D9, event 1      the same for Direct3D 9
    //   Microsoft-Windows-DxgKrnl, event 184 the graphics kernel takes a frame from a program, whatever it draws with
    //                                        (Vulkan, OpenGL): used for a program the first two say nothing about
    // An event carries the program's process id and its time; the frame rate of a program is the number of its frames
    // in the last second of its own events, by their times (the events come in batches, about once a second, so
    // counting arrivals would jump). Nothing is injected into the game, nothing is read from it. Needs administrator
    // rights, which Red Tune has. The session costs next to nothing while nobody draws and is stopped with the overlay.
    static class Fps
    {
        const string Session = "RedTuneFps";
        static readonly Guid Dxgi = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3D9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        static readonly Guid DxgKrnl = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");
        const int DxgiId = unchecked((int)0xCA11C036), D3D9Id = 0x783ACA0A, KrnlId = unchecked((int)0x802EC45A);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

        delegate void RecordCallback(IntPtr record);
        static RecordCallback callback; // kept: Windows calls it for as long as the session runs
        static ulong session, trace;
        static Thread reader;
        static IntPtr logfile, nameMem;
        static readonly object gate = new object();
        public static bool Running { get; private set; }
        public static string Error { get; private set; }

        // the times (QPC ticks) of a program's frames in its last second: by the runtime (DXGI, D3D9) and by the kernel
        class Stat { public readonly Queue<long> Api = new Queue<long>(), Kernel = new Queue<long>(); }
        static readonly Dictionary<int, Stat> stats = new Dictionary<int, Stat>();
        // --fps-probe: how many events of each kind came from each process
        public static Dictionary<string, int> Histogram;
        static readonly long Second = System.Diagnostics.Stopwatch.Frequency;

        const int PropsSize = 120, NameBytes = 1024;

        static IntPtr Props()
        {
            int total = PropsSize + 2 * NameBytes;
            var p = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i += 8) Marshal.WriteInt64(p, i, 0);
            Marshal.WriteInt32(p, 0, total);           // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);              // Wnode.ClientContext: event times in QPC ticks
            Marshal.WriteInt32(p, 44, 0x00020000);     // Wnode.Flags: WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);             // BufferSize, KB
            Marshal.WriteInt32(p, 64, 0x100);          // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);              // FlushTimer, s
            Marshal.WriteInt32(p, 116, PropsSize);     // LoggerNameOffset
            return p;
        }

        // null, or why it did not start. krnlAll: every event of the graphics kernel for the probe (heavy, seconds only)
        public static string Start(bool krnlAll = false)
        {
            lock (gate)
            {
                if (Running) return null;
                Error = null;
                var props = Props();
                try
                {
                    uint r = StartTraceW(out session, Session, props);
                    if (r == 183) // left by a copy of the app that did not close it
                    {
                        var old = Props();
                        ControlTraceW(0, Session, old, 1); Marshal.FreeHGlobal(old);
                        Marshal.FreeHGlobal(props); props = Props();
                        r = StartTraceW(out session, Session, props);
                    }
                    if (r != 0) return Error = "сеанс событий не открылся (" + r + ")";
                }
                finally { Marshal.FreeHGlobal(props); }

                var g = Dxgi; uint e1 = EnableTraceEx2(session, ref g, 1, 4, 0, 0, 0, IntPtr.Zero);
                g = D3D9; EnableTraceEx2(session, ref g, 1, 4, 0, 0, 0, IntPtr.Zero);
                g = DxgKrnl; uint e3 = krnlAll ? EnableTraceEx2(session, ref g, 1, 4, 0, 0, 0, IntPtr.Zero) : EnableOnly(DxgKrnl, 184);
                if (e1 != 0) { StopSession(); return Error = "события DXGI не включились (" + e1 + ")"; }
                if (e3 != 0) Store.Log("FPS: события графического ядра не включились (" + e3 + "): Vulkan и OpenGL без FPS");

                // EVENT_TRACE_LOGFILEW, 448 bytes on x64: LoggerName at 8, ProcessTraceMode at 28, EventRecordCallback at 424
                logfile = Marshal.AllocHGlobal(448);
                for (int i = 0; i < 448; i += 8) Marshal.WriteInt64(logfile, i, 0);
                nameMem = Marshal.StringToHGlobalUni(Session);
                Marshal.WriteIntPtr(logfile, 8, nameMem);
                Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000); // real time, EVENT_RECORD callbacks
                callback = OnEvent;
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));
                trace = OpenTraceW(logfile);
                if (trace == ulong.MaxValue || trace == 0xFFFFFFFF)
                {
                    int err = Marshal.GetLastWin32Error();
                    StopSession(); Free();
                    return Error = "сеанс событий не читается (" + err + ")";
                }
                Running = true;
                reader = new Thread(() => { try { ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } }) { IsBackground = true, Name = "fps" };
                reader.Start();
                return null;
            }
        }

        // one event of a provider, the rest of it filtered out before it reaches the session (EVENT_FILTER_TYPE_EVENT_ID)
        static uint EnableOnly(Guid provider, ushort id)
        {
            IntPtr ids = Marshal.AllocHGlobal(8), desc = Marshal.AllocHGlobal(16), par = Marshal.AllocHGlobal(48);
            try
            {
                Marshal.WriteByte(ids, 0, 1); Marshal.WriteByte(ids, 1, 0); Marshal.WriteInt16(ids, 2, 1); Marshal.WriteInt16(ids, 4, (short)id); // FilterIn, Count, Events[0]
                Marshal.WriteInt64(desc, 0, ids.ToInt64()); Marshal.WriteInt32(desc, 8, 6); Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200));
                for (int i = 0; i < 48; i += 8) Marshal.WriteInt64(par, i, 0);
                Marshal.WriteInt32(par, 0, 2);                 // ENABLE_TRACE_PARAMETERS_VERSION_2
                Marshal.WriteIntPtr(par, 32, desc); Marshal.WriteInt32(par, 40, 1);
                return EnableTraceEx2(session, ref provider, 1, 4, 0, 0, 0, par);
            }
            finally { Marshal.FreeHGlobal(ids); Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(par); }
        }

        static void StopSession()
        {
            var p = Props();
            try { ControlTraceW(session, null, p, 1); } catch { } // EVENT_TRACE_CONTROL_STOP
            Marshal.FreeHGlobal(p);
            session = 0;
        }

        static void Free()
        {
            if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
            if (nameMem != IntPtr.Zero) { Marshal.FreeHGlobal(nameMem); nameMem = IntPtr.Zero; }
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (!Running) return;
                Running = false;
                StopSession();                                  // ProcessTrace returns once the session is gone
                try { if (reader != null) reader.Join(2000); } catch { }
                try { CloseTrace(trace); } catch { }
                Free();
                lock (stats) stats.Clear();
            }
        }

        // EVENT_RECORD: ProcessId at 12, TimeStamp at 16, ProviderId at 24, the event's Id at 40
        static void OnEvent(IntPtr rec)
        {
            try
            {
                int provider = Marshal.ReadInt32(rec, 24);
                int id = (ushort)Marshal.ReadInt16(rec, 40);
                int pid = Marshal.ReadInt32(rec, 12);
                var h = Histogram;
                if (h != null) lock (h) { string key = (provider == DxgiId ? "DXGI" : provider == D3D9Id ? "D3D9" : provider == KrnlId ? "DxgKrnl" : provider.ToString("X8")) + " " + id + " pid " + pid; int n; h.TryGetValue(key, out n); h[key] = n + 1; }
                bool api = provider == DxgiId && id == 42 || provider == D3D9Id && id == 1;
                bool kernel = provider == KrnlId && id == 184;
                if (!api && !kernel) return;
                long t = Marshal.ReadInt64(rec, 16);
                lock (stats)
                {
                    Stat s;
                    if (!stats.TryGetValue(pid, out s)) stats[pid] = s = new Stat();
                    var q = api ? s.Api : s.Kernel;
                    q.Enqueue(t);
                    while (q.Count > 2 && t - q.Peek() > Second) q.Dequeue();
                }
            }
            catch { }
        }

        static double Rate(Queue<long> q, long now)
        {
            if (q.Count < 3) return double.NaN;
            long first = q.Peek(), last = first;
            foreach (var t in q) last = t;
            if (now - last > 2 * Second || last <= first) return double.NaN; // it stopped drawing
            return (q.Count - 1) * (double)Second / (last - first);
        }

        // the frame rate of a process now; NaN when it draws nothing (or the session does not run)
        public static double Of(int pid)
        {
            if (!Running) return double.NaN;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (stats)
            {
                Stat s;
                if (!stats.TryGetValue(pid, out s)) return double.NaN;
                double r = Rate(s.Api, now);
                return double.IsNaN(r) ? Rate(s.Kernel, now) : r;
            }
        }

        // every process that draws now, for the probe: pid, by the runtime, by the kernel
        public static List<Tuple<int, double, double>> All()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (stats) return stats.Select(kv => Tuple.Create(kv.Key, Rate(kv.Value.Api, now), Rate(kv.Value.Kernel, now))).Where(x => !double.IsNaN(x.Item2) || !double.IsNaN(x.Item3)).ToList();
        }
    }
}
