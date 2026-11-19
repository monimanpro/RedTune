using System;
using System.Runtime.InteropServices;

namespace RedTune
{
    // Microphones through Core Audio: all active capture devices muted or not (like LLT's microphone switch)
    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            int GetCount(out int count);
            int Item(int index, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr n);
            int UnregisterControlChangeNotify(IntPtr n);
            int GetChannelCount(out int c);
            int SetMasterVolumeLevel(float level, ref Guid ctx);
            int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            int GetMasterVolumeLevel(out float level);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
            int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            int GetChannelVolumeLevel(uint ch, out float level);
            int GetChannelVolumeLevelScalar(uint ch, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static void ForEach(Action<IAudioEndpointVolume> a)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection col;
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(1 /* capture */, 1 /* active */, out col));
            int n; col.GetCount(out n);
            var iid = typeof(IAudioEndpointVolume).GUID;
            for (int i = 0; i < n; i++)
            {
                IMMDevice d; col.Item(i, out d);
                object o;
                if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) a((IAudioEndpointVolume)o);
            }
        }

        // true = at least one microphone is on; null = no microphones
        public static bool? MicOn()
        {
            bool any = false, on = false;
            ForEach(v => { bool m; v.GetMute(out m); any = true; on |= !m; });
            return any ? on : (bool?)null;
        }

        public static void SetMic(bool on)
        {
            var ctx = Guid.Empty;
            ForEach(v => v.SetMute(!on, ref ctx));
        }
    }
}
