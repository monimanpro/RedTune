using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RedTune
{
    // 21.11: «Звук → Микрофон». Windows runs the effects of an input only while some program records from it, so while the
    // tab shows a microphone with the effect on, the page records from it itself: shared mode, every packet taken at once.
    // That keeps the effect working, so its meters move and the gate's threshold can be set against the real level.
    // Windows shows its microphone sign meanwhile.
    // 21.11 (the user's wish): Monitor — the recorded sound (as Windows hands it to programs: after the effect, the gate,
    // the equalizer, the compressor) goes straight on to the default output, to hear the settings while making them.
    // Mono, at the output's own rate (a linear resample when the two differ), kept under ~60 ms behind. Nothing is stored.
    static class MicListen
    {
        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
        }

        static Thread thread;
        static volatile bool stop;
        static Guid now;
        public static Guid Device { get { return thread != null ? now : Guid.Empty; } }
        // what goes to the output while listening; set by the page, read by the recording thread
        public static volatile bool Monitor;
        public static volatile string MonitorError; // the last reason the sound could not go out, or null
        public static long Frames; public static string Format = ""; // what was recorded, for --mic-listen-check

        // starts recording from this input (and stops another one); nothing when it already records from it
        public static void Start(Guid id)
        {
            if (thread != null && now == id) return;
            Stop();
            now = id; stop = false;
            var t = new Thread(() => Run(id)) { IsBackground = true, Name = "mic listen" };
            t.SetApartmentState(ApartmentState.MTA);
            thread = t;
            t.Start();
        }

        public static void Stop()
        {
            var t = thread; if (t == null) return;
            stop = true; thread = null;
            t.Join(1500);
        }

        // a shared-mode format: float samples (32 bit) are the only kind handled, as Windows mixes in them
        struct Fmt { public int Channels, Rate; public bool Float; }
        static Fmt Read(IntPtr wf)
        {
            var f = new Fmt();
            int tag = Marshal.ReadInt16(wf); f.Channels = Marshal.ReadInt16(wf, 2); f.Rate = Marshal.ReadInt32(wf, 4); int bits = Marshal.ReadInt16(wf, 14);
            if (tag == 3) f.Float = bits == 32;
            else if (tag == unchecked((short)0xFFFE) || tag == 0xFFFE)
            {
                var sub = new byte[16]; Marshal.Copy(wf + 24, sub, 0, 16);
                f.Float = bits == 32 && new Guid(sub) == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }
            return f;
        }

        static void Run(Guid id)
        {
            IAudioClient ac = null; IAudioCaptureClient cc = null; IntPtr fmt = IntPtr.Zero; bool started = false;
            Output o = null;
            try
            {
                ac = AudioFx.Activate(id, typeof(IAudioClient).GUID) as IAudioClient;
                if (ac == null) { Store.Log("микрофон: не открылся для прослушивания"); return; }
                int hr = ac.GetMixFormat(out fmt);
                if (hr == 0) hr = ac.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero); // shared, a 200 ms buffer
                object svc = null; var iid = typeof(IAudioCaptureClient).GUID;
                if (hr == 0) hr = ac.GetService(ref iid, out svc);
                cc = svc as IAudioCaptureClient;
                if (hr == 0 && cc != null) hr = ac.Start();
                if (hr != 0 || cc == null) { Store.Log("микрофон: прослушивание не началось: 0x" + hr.ToString("X8")); return; }
                started = true;
                var inf = Read(fmt); Format = inf.Rate + " Hz, " + inf.Channels + " ch, " + (inf.Float ? "float" : "not float");
                var mono = new float[48000];
                while (!stop)
                {
                    Thread.Sleep(5);
                    // the output follows the switch: made when it is turned on, let go when it is turned off
                    if (Monitor && o == null) { o = Output.Open(); if (o != null && !inf.Float) { MonitorError = "формат микрофона не поддерживается"; o.Dispose(); o = null; Monitor = false; } }
                    else if (!Monitor && o != null) { o.Dispose(); o = null; }
                    uint n;
                    while (!stop && cc.GetNextPacketSize(out n) == 0 && n > 0)
                    {
                        IntPtr data; uint frames, flags; ulong dp, qp;
                        if (cc.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;
                        if (o != null && frames > 0)
                        {
                            int k = (int)Math.Min(frames, (uint)mono.Length);
                            if ((flags & 2) != 0 || inf.Channels < 1) Array.Clear(mono, 0, k); // AUDCLNT_BUFFERFLAGS_SILENT
                            else unsafe
                            {
                                float* s = (float*)data; int ch = inf.Channels;
                                for (int i = 0; i < k; i++) { float sum = 0; for (int c = 0; c < ch; c++) sum += s[i * ch + c]; mono[i] = sum / ch; }
                            }
                            o.Push(mono, k, inf.Rate);
                        }
                        Interlocked.Add(ref Frames, frames);
                        cc.ReleaseBuffer(frames); // the sound is not kept
                    }
                    if (o != null && !o.Pump()) { o.Dispose(); o = null; Monitor = false; }
                }
            }
            catch (Exception ex) { Store.Quiet("микрофон: прослушивание", ex); }
            finally
            {
                if (o != null) o.Dispose();
                try { if (started && ac != null) ac.Stop(); } catch { }
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (cc != null) try { Marshal.ReleaseComObject(cc); } catch { }
                if (ac != null) try { Marshal.ReleaseComObject(ac); } catch { }
            }
        }

        // the default output, shared mode, fed from a small ring of mono samples at its own rate
        sealed class Output : IDisposable
        {
            IAudioClient ac; IAudioRenderClient rc; IntPtr fmt; uint size; int ch, rate; bool ok;
            readonly float[] ring = new float[96000]; int head, count; // mono samples at the output's rate
            double phase; float last; // the resample: position between two input samples, the previous input sample

            public static Output Open()
            {
                var o = new Output();
                try
                {
                    var dev = AudioFx.DefaultDevice();
                    o.ac = dev == Guid.Empty ? null : AudioFx.Activate(dev, typeof(IAudioClient).GUID) as IAudioClient;
                    if (o.ac == null) { MonitorError = "нет устройства вывода"; Monitor = false; return null; }
                    int hr = o.ac.GetMixFormat(out o.fmt);
                    var f = hr == 0 ? Read(o.fmt) : new Fmt();
                    if (hr == 0 && !f.Float) { MonitorError = "формат устройства вывода не поддерживается"; o.Dispose(); Monitor = false; return null; }
                    if (hr == 0) hr = o.ac.Initialize(0, 0, 400000, 0, o.fmt, IntPtr.Zero); // a 40 ms buffer
                    if (hr == 0) hr = o.ac.GetBufferSize(out o.size);
                    object svc = null; var iid = typeof(IAudioRenderClient).GUID;
                    if (hr == 0) hr = o.ac.GetService(ref iid, out svc);
                    o.rc = svc as IAudioRenderClient;
                    if (hr == 0 && o.rc != null) hr = o.ac.Start();
                    if (hr != 0 || o.rc == null) { MonitorError = "вывод не открылся: 0x" + hr.ToString("X8"); o.Dispose(); Monitor = false; return null; }
                    o.ch = f.Channels; o.rate = f.Rate; o.ok = true; MonitorError = null;
                    return o;
                }
                catch (Exception ex) { MonitorError = ex.Message; o.Dispose(); Monitor = false; return null; }
            }

            // input samples at their rate into the ring at the output's rate (linear between two samples)
            public void Push(float[] x, int n, int inRate)
            {
                if (!ok) return;
                double step = (double)inRate / rate;
                for (int i = 0; i < n; i++)
                {
                    float cur = x[i];
                    while (phase < 1.0)
                    {
                        float v = last + (cur - last) * (float)phase;
                        ring[(head + count) % ring.Length] = v;
                        if (count < ring.Length) count++; else head = (head + 1) % ring.Length;
                        phase += step;
                    }
                    phase -= 1.0; last = cur;
                }
                // never more than ~60 ms behind: the oldest samples go
                int keep = rate * 60 / 1000;
                if (count > keep) { int drop = count - keep; head = (head + drop) % ring.Length; count = keep; }
            }

            // as much of the ring as the output has room for; false when the output went away
            public unsafe bool Pump()
            {
                if (!ok) return false;
                uint pad;
                if (ac.GetCurrentPadding(out pad) != 0) { MonitorError = "устройство вывода пропало"; return false; }
                int room = (int)(size - pad), k = Math.Min(room, count);
                if (k <= 0) return true;
                IntPtr buf;
                if (rc.GetBuffer((uint)k, out buf) != 0) return true;
                float* d = (float*)buf;
                for (int i = 0; i < k; i++) { float v = ring[head]; head = (head + 1) % ring.Length; for (int c = 0; c < ch; c++) d[i * ch + c] = v; }
                count -= k;
                rc.ReleaseBuffer((uint)k, 0);
                return true;
            }

            public void Dispose()
            {
                ok = false;
                try { if (ac != null) ac.Stop(); } catch { }
                if (fmt != IntPtr.Zero) { Marshal.FreeCoTaskMem(fmt); fmt = IntPtr.Zero; }
                if (rc != null) try { Marshal.ReleaseComObject(rc); } catch { }
                if (ac != null) try { Marshal.ReleaseComObject(ac); } catch { }
                rc = null; ac = null;
            }
        }
    }
}
