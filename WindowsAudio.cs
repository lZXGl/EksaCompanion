using System;
using System.Runtime.InteropServices;

namespace EksaCompanion
{
    public static class WindowsAudio
    {
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
            int GetMeteringChannelCount(out int channelCount);
            int GetChannelsPeakValues(int channelCount, float[] peakValues);
            int QueryHardwareSupport(out int hardwareSupportMask);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out int channelCount);
            int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);
            int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            int GetMasterVolumeLevel(out float levelDB);
            int GetMasterVolumeLevelScalar(out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            int GetVolumeStepInfo(out uint step, out uint stepCount);
            int VolumeStepUp(ref Guid eventContext);
            int VolumeStepDown(ref Guid eventContext);
            int QueryHardwareSupport(out uint hardwareSupportMask);
            int GetVolumeRange(out float volumeMindB, out float volumeMaxdB, out float volumeIncrementdB);
        }

        public static float GetMasterPeakAudio()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice device;
                // eRender = 0, eConsole = 0
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
                {
                    Guid meterGuid = typeof(IAudioMeterInformation).GUID;
                    object meterObj;
                    if (device.Activate(ref meterGuid, 1, IntPtr.Zero, out meterObj) == 0 && meterObj != null)
                    {
                        var meter = (IAudioMeterInformation)meterObj;
                        float peak = 0;
                        meter.GetPeakValue(out peak);
                        return peak;
                    }
                }
            }
            catch { }
            return 0f;
        }

        public static float GetMasterVolume()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
                {
                    Guid volGuid = typeof(IAudioEndpointVolume).GUID;
                    object volObj;
                    if (device.Activate(ref volGuid, 1, IntPtr.Zero, out volObj) == 0 && volObj != null)
                    {
                        var vol = (IAudioEndpointVolume)volObj;
                        float level = 0;
                        vol.GetMasterVolumeLevelScalar(out level);
                        return level;
                    }
                }
            }
            catch { }
            return 1.0f;
        }

        public static bool SetMasterVolume(float level)
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
                {
                    Guid volGuid = typeof(IAudioEndpointVolume).GUID;
                    object volObj;
                    if (device.Activate(ref volGuid, 1, IntPtr.Zero, out volObj) == 0 && volObj != null)
                    {
                        var vol = (IAudioEndpointVolume)volObj;
                        Guid empty = Guid.Empty;
                        vol.SetMasterVolumeLevelScalar(level, ref empty);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
