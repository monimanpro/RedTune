using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RedTune
{
    // The processor's own temperature (Tctl) on any Ryzen, for laptops without Lenovo's firmware readings (13.11).
    // It is the THM_TCON_CUR_TMP register (SMN 0x00059800, the same since Zen 1): bits 31..21 = temperature × 8, bit 19
    // set = the reading is offset by 49 °C. Read through PawnIO's RyzenSMU module (ram\RyzenSMU.bin, the one the AMD
    // limits use) with PawnIOLib.dll from PawnIO's own folder: only reads, in this process, under the PCI mutex that
    // ZenStates, HWiNFO and LibreHardwareMonitor share. Without PawnIO it stays off and Home falls back to ACPI.
    static class Tctl
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
        [DllImport("PawnIOLib.dll")] static extern int pawnio_open(out IntPtr handle);
        [DllImport("PawnIOLib.dll")] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
        [DllImport("PawnIOLib.dll", CharSet = CharSet.Ansi)] static extern int pawnio_execute(IntPtr handle, string name, ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
        [DllImport("PawnIOLib.dll")] static extern int pawnio_close(IntPtr handle);

        static IntPtr handle = IntPtr.Zero;
        static bool tried, failed;
        static readonly object gate = new object();

        static bool Open()
        {
            if (tried) return !failed;
            tried = true; failed = true;
            try
            {
                if (!Hw.Amd) return false;
                var lib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
                var blob = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ram", "RyzenSMU.bin");
                if (!File.Exists(lib) || !File.Exists(blob) || LoadLibrary(lib) == IntPtr.Zero) return false;
                IntPtr h;
                if (pawnio_open(out h) < 0) return false;
                var bytes = File.ReadAllBytes(blob);
                if (pawnio_load(h, bytes, (UIntPtr)bytes.Length) < 0) { pawnio_close(h); return false; }
                handle = h; failed = false;
                return true;
            }
            catch (Exception ex) { Store.Log("температура ЦП (PawnIO): " + ex.Message); return false; }
        }

        public static bool Available { get { lock (gate) return Open(); } }

        // any call of the RyzenSMU module, under the same PCI mutex (14.11: the telemetry table for a core's curve);
        // null when it fails or the bus is held by another program
        public static ulong[] Execute(string fn, ulong[] input, int outCount)
        {
            lock (gate)
            {
                if (!Open()) return null;
                Mutex m = null;
                try
                {
                    m = new Mutex(false, "Global\\Access_PCI");
                    bool got;
                    try { got = m.WaitOne(300); } catch (AbandonedMutexException) { got = true; }
                    if (!got) return null;
                    try
                    {
                        var output = new ulong[outCount]; UIntPtr n;
                        return pawnio_execute(handle, fn, input, (UIntPtr)input.Length, output, (UIntPtr)outCount, out n) < 0 ? null : output;
                    }
                    finally { m.ReleaseMutex(); }
                }
                catch { return null; }
                finally { if (m != null) m.Dispose(); }
            }
        }

        // 17.11: several calls of the module as one piece under the PCI mutex (an SMU mailbox exchange: Smu). The body
        // gets false when PawnIO is missing or another program held the bus for longer than waitMs, and is not run then
        public static bool Hold(int waitMs, Action body)
        {
            lock (gate)
            {
                if (!Open()) return false;
                using (var m = new Mutex(false, "Global\\Access_PCI"))
                {
                    bool got;
                    try { got = m.WaitOne(waitMs); } catch (AbandonedMutexException) { got = true; }
                    if (!got) return false;
                    try { body(); } finally { m.ReleaseMutex(); }
                    return true;
                }
            }
        }

        // one SMN register through the module; only inside Hold (no mutex of their own)
        public static bool ReadReg(uint addr, out uint value)
        {
            var output = new ulong[1]; UIntPtr n;
            bool ok = pawnio_execute(handle, "ioctl_read_smu_register", new ulong[] { addr }, (UIntPtr)1, output, (UIntPtr)1, out n) >= 0;
            value = ok ? (uint)output[0] : 0;
            return ok;
        }

        public static bool WriteReg(uint addr, uint value)
        {
            UIntPtr n;
            return pawnio_execute(handle, "ioctl_write_smu_register", new ulong[] { addr, value }, (UIntPtr)2, new ulong[0], (UIntPtr)0, out n) >= 0;
        }

        // 17.11: another PawnIO module (a file in ram\), in a handle of its own: a handle holds one module. Zero when
        // PawnIO is missing or the module does not load (its own check of the hardware failed); the handle stays open
        // for the life of the program
        public static IntPtr LoadModule(string file)
        {
            lock (gate)
            {
                if (!Open()) return IntPtr.Zero;
                try
                {
                    var blob = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ram", file);
                    IntPtr h;
                    if (!File.Exists(blob) || pawnio_open(out h) < 0) return IntPtr.Zero;
                    var bytes = File.ReadAllBytes(blob);
                    if (pawnio_load(h, bytes, (UIntPtr)bytes.Length) >= 0) return h;
                    pawnio_close(h);
                }
                catch (Exception ex) { Store.Log("PawnIO, модуль " + file + ": " + ex.Message); }
                return IntPtr.Zero;
            }
        }

        // one call of such a module; false when it refused. The caller takes whatever mutex the bus needs
        public static bool Call(IntPtr module, string fn, ulong[] input, ulong[] output)
        {
            UIntPtr n;
            return module != IntPtr.Zero && pawnio_execute(module, fn, input, (UIntPtr)input.Length, output, (UIntPtr)output.Length, out n) >= 0;
        }

        // any SMN register, read only (the memory controller's: Ram\LiveTimings). The RyzenSMU module reads only the
        // SMU's own registers, so this goes through PawnIO's AMDFamily17 module (ram\AMDFamily17.bin, the one
        // ZenStates-Core carries). Only inside Hold
        static IntPtr smn = IntPtr.Zero;
        static bool smnTried;

        public static bool ReadSmn(uint addr, out uint value)
        {
            value = 0;
            if (!smnTried)
            {
                smnTried = true;
                smn = LoadModule("AMDFamily17.bin");
                if (smn == IntPtr.Zero) Store.Log("регистры процессора (PawnIO): модуль AMDFamily17 не загрузился");
            }
            var output = new ulong[1];
            if (!Call(smn, "ioctl_read_smn", new ulong[] { addr }, output)) return false;
            value = (uint)output[0];
            return true;
        }

        // °C, or -1 when it cannot be read now (no PawnIO, or another program holds the PCI bus for over 100 ms)
        public static int Read()
        {
            lock (gate)
            {
                if (!Open()) return -1;
                Mutex m = null;
                try
                {
                    m = new Mutex(false, "Global\\Access_PCI");
                    bool got;
                    try { got = m.WaitOne(100); } catch (AbandonedMutexException) { got = true; }
                    if (!got) return -1;
                    try
                    {
                        var output = new ulong[1]; UIntPtr n;
                        if (pawnio_execute(handle, "ioctl_read_smu_register", new ulong[] { 0x59800 }, (UIntPtr)1, output, (UIntPtr)1, out n) < 0) return -1;
                        uint v = (uint)output[0];
                        double t = (v >> 21) * 0.125 - ((v & 0x80000) != 0 ? 49 : 0);
                        return t > 0 && t < 125 ? (int)Math.Round(t) : -1;
                    }
                    finally { m.ReleaseMutex(); }
                }
                catch { return -1; }
                finally { if (m != null) m.Dispose(); }
            }
        }
    }
}
