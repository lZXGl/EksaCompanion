using System;
using System.Runtime.InteropServices;

namespace EksaCompanion
{
    public static class WindowsAudio
    {
        private const int CLSCTX_ALL = 23;

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out uint count);
            [PreserveSig]
            int Item(uint index, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetState(out int state);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig]
            int GetPeakValue(out float peak);
            [PreserveSig]
            int GetMeteringChannelCount(out int channelCount);
            [PreserveSig]
            int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);
            [PreserveSig]
            int QueryHardwareSupport(out int hardwareSupportMask);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig]
            int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
            [PreserveSig]
            int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
            [PreserveSig]
            int RegisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig]
            int UnregisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig]
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
            [PreserveSig]
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int SessionCount);
            [PreserveSig]
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig]
            int GetState(out int pRetVal);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig]
            int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig]
            int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig]
            int GetChannelCount(out int channelCount);
            [PreserveSig]
            int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);
            [PreserveSig]
            int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig]
            int GetMasterVolumeLevel(out float levelDB);
            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig]
            int GetVolumeStepInfo(out uint step, out uint stepCount);
            [PreserveSig]
            int VolumeStepUp(ref Guid eventContext);
            [PreserveSig]
            int VolumeStepDown(ref Guid eventContext);
            [PreserveSig]
            int QueryHardwareSupport(out uint hardwareSupportMask);
            [PreserveSig]
            int GetVolumeRange(out float volumeMindB, out float volumeMaxdB, out float volumeIncrementdB);
        }

        private static float CheckDeviceAudioActivity(IMMDevice dev, ref Guid meterGuid, ref Guid sessionMgrGuid)
        {
            float maxPeak = 0f;
            if (dev == null) return 0f;

            try
            {
                object meterObj;
                if (dev.Activate(ref meterGuid, CLSCTX_ALL, IntPtr.Zero, out meterObj) == 0 && meterObj != null)
                {
                    var meter = (IAudioMeterInformation)meterObj;
                    float peak = 0f;
                    if (meter.GetPeakValue(out peak) == 0 && peak > maxPeak)
                        maxPeak = peak;
                }
            }
            catch { }

            if (maxPeak > 0.0005f)
                return maxPeak;

            try
            {
                object mgrObj;
                if (dev.Activate(ref sessionMgrGuid, CLSCTX_ALL, IntPtr.Zero, out mgrObj) == 0 && mgrObj != null)
                {
                    var mgr = (IAudioSessionManager2)mgrObj;
                    IAudioSessionEnumerator sessionEnum;
                    if (mgr.GetSessionEnumerator(out sessionEnum) == 0 && sessionEnum != null)
                    {
                        int count = 0;
                        sessionEnum.GetCount(out count);
                        for (int i = 0; i < count; i++)
                        {
                            IAudioSessionControl ctrl;
                            if (sessionEnum.GetSession(i, out ctrl) == 0 && ctrl != null)
                            {
                                var sessionMeter = ctrl as IAudioMeterInformation;
                                if (sessionMeter != null)
                                {
                                    float sPeak = 0f;
                                    if (sessionMeter.GetPeakValue(out sPeak) == 0 && sPeak > maxPeak)
                                        maxPeak = sPeak;
                                }

                                int state = 0;
                                // AudioSessionStateActive == 1
                                if (ctrl.GetState(out state) == 0 && state == 1 && maxPeak < 0.01f)
                                {
                                    maxPeak = 0.01f;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return maxPeak;
        }

        public static float GetMasterPeakAudio()
        {
            float maxPeak = 0f;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Guid meterGuid = typeof(IAudioMeterInformation).GUID;
                Guid sessionMgrGuid = typeof(IAudioSessionManager2).GUID;

                // 1. Check default console & multimedia endpoints
                for (int role = 0; role <= 1; role++)
                {
                    IMMDevice device;
                    if (enumerator.GetDefaultAudioEndpoint(0, role, out device) == 0 && device != null)
                    {
                        float p = CheckDeviceAudioActivity(device, ref meterGuid, ref sessionMgrGuid);
                        if (p > maxPeak) maxPeak = p;
                    }
                }

                if (maxPeak > 0.0005f)
                    return maxPeak;

                // 2. Check all active render endpoints
                IMMDeviceCollection collection;
                if (enumerator.EnumAudioEndpoints(0, 1, out collection) == 0 && collection != null)
                {
                    uint count = 0;
                    collection.GetCount(out count);
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice dev;
                        if (collection.Item(i, out dev) == 0 && dev != null)
                        {
                            float p = CheckDeviceAudioActivity(dev, ref meterGuid, ref sessionMgrGuid);
                            if (p > maxPeak) maxPeak = p;
                            if (maxPeak > 0.0005f)
                                return maxPeak;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("GetMasterPeakAudio error: " + ex.Message);
            }
            return maxPeak;
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
                    if (device.Activate(ref volGuid, CLSCTX_ALL, IntPtr.Zero, out volObj) == 0 && volObj != null)
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
                    if (device.Activate(ref volGuid, CLSCTX_ALL, IntPtr.Zero, out volObj) == 0 && volObj != null)
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
