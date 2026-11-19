using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RedTune
{
    enum BatteryMode { Conservation, Normal, Rapid }
    enum UsbPower { Off, Sleep, Always }
    enum KeyLight { Off, Low, High }

    // Lenovo energy driver (\\.\EnergyDrv): battery, always-on USB, Fn lock, keyboard light. Codes and bit meanings are
    // LLT's (System/Drivers.cs, Features/*); read back on the R7000P on 2026-11-11 and matching what LLT showed.
    static class Energy
    {
        const uint BatteryCode = 0x831020F8, SettingsCode = 0x831020E8, NightCode = 0x83102150, KeyboardCode = 0x83102144;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr overlapped);

        static SafeFileHandle handle;
        static readonly object gate = new object();

        static uint Send(uint code, uint arg)
        {
            lock (gate)
            {
                if (handle == null || handle.IsInvalid)
                {
                    handle = CreateFile(@"\\.\EnergyDrv", 0x1 | 0x2, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (handle.IsInvalid) throw new InvalidOperationException("Драйвер Lenovo Energy не найден (" + Marshal.GetLastWin32Error() + ")");
                }
                uint o; int r;
                if (!DeviceIoControl(handle, code, ref arg, 4, out o, 4, out r, IntPtr.Zero)) throw new InvalidOperationException("Драйвер Lenovo отказал (" + Marshal.GetLastWin32Error() + ")");
                return o;
            }
        }

        static uint Swap(uint v) { return (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24); }
        static bool Bit(uint v, int n) { return ((v >> n) & 1) == 1; }

        // ---------- battery ----------

        public static BatteryMode GetBattery()
        {
            var s = Swap(Send(BatteryCode, 0xFF));
            if (Bit(s, 17)) return Bit(s, 26) ? BatteryMode.Rapid : BatteryMode.Normal;
            if (Bit(s, 29)) return BatteryMode.Conservation;
            throw new InvalidOperationException("Неизвестный режим батареи");
        }

        public static void SetBattery(BatteryMode mode)
        {
            var last = GetBattery();
            uint[] codes = mode == BatteryMode.Conservation ? (last == BatteryMode.Rapid ? new uint[] { 0x8, 0x3 } : new uint[] { 0x3 })
                         : mode == BatteryMode.Normal ? (last == BatteryMode.Conservation ? new uint[] { 0x5 } : new uint[] { 0x8 })
                         : (last == BatteryMode.Conservation ? new uint[] { 0x5, 0x7 } : new uint[] { 0x7 });
            foreach (var c in codes) Send(BatteryCode, c);
            // Lenovo's own service reads the mode from here after a restart
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin", "BatteryChargeMode",
                mode == BatteryMode.Conservation ? "Storage" : mode == BatteryMode.Rapid ? "Quick" : "Normal");
        }

        public static bool GetNightCharge()
        {
            var s = Send(NightCode, 0x11);
            if (!Bit(s, 0)) throw new InvalidOperationException("Ночная зарядка не поддерживается");
            return Bit(s, 4);
        }

        public static void SetNightCharge(bool on) { Send(NightCode, on ? 0x80000012u : 0x12u); }

        // ---------- always-on USB, Fn lock ----------

        public static UsbPower GetUsbPower()
        {
            var s = Swap(Send(SettingsCode, 0x2));
            return !Bit(s, 31) ? UsbPower.Off : Bit(s, 23) ? UsbPower.Always : UsbPower.Sleep;
        }

        public static void SetUsbPower(UsbPower p)
        {
            var codes = p == UsbPower.Off ? new uint[] { 0xB, 0x12 } : p == UsbPower.Sleep ? new uint[] { 0xA, 0x12 } : new uint[] { 0xA, 0x13 };
            foreach (var c in codes) Send(SettingsCode, c);
        }

        public static bool GetFnLock() { return Bit(Send(SettingsCode, 0x2), 10); }
        public static void SetFnLock(bool on) { Send(SettingsCode, on ? 0xEu : 0xFu); }

        // ---------- white keyboard light ----------

        public static bool KeyLightSupported { get { try { return (Send(KeyboardCode, 0x1) >> 1) == 0x2; } catch { return false; } } }

        public static KeyLight GetKeyLight()
        {
            var s = Send(KeyboardCode, 0x22);
            return s == 0x5 ? KeyLight.High : s == 0x3 ? KeyLight.Low : KeyLight.Off;
        }

        public static void SetKeyLight(KeyLight k) { Send(KeyboardCode, k == KeyLight.High ? 0x20023u : k == KeyLight.Low ? 0x10023u : 0x23u); }
    
        // ---------- Fn keys from the driver (LLT's DriverKeyListener): Fn+F10 = 32, Fn+F4 = 256, Fn+Space = 4096, Fn+F8 = 8192 ----------

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

        // the driver signals the event on every key; Wait returns the key value or 0
        public static void BindKeyEvent(System.Threading.WaitHandle ev)
        {
            lock (gate)
            {
                if (handle == null || handle.IsInvalid) Send(SettingsCode, 0x2);
                var b = new byte[16];
                BitConverter.GetBytes((uint)ev.SafeWaitHandle.DangerousGetHandle().ToInt64()).CopyTo(b, 0);
                int r;
                if (!DeviceIoControl(handle, 0x831020D8, b, 16, IntPtr.Zero, 0, out r, IntPtr.Zero)) throw new InvalidOperationException("Клавиши Fn недоступны (" + Marshal.GetLastWin32Error() + ")");
            }
        }

        public static uint KeyValue() { return Send(0x831020CC, 0); }
    }
}
