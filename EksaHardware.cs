using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace EksaCompanion
{
    public class EksaStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; set; }
        public string DeviceUid { get; set; }
        public string StatusSummary { get; set; }
    }

    public class EksaHardware : IDisposable
    {
        private const ushort VID = 0x3206;
        private const ushort PID = 0x0F06;

        [StructLayout(LayoutKind.Sequential)]
        private struct HidDeviceInfo
        {
            [MarshalAs(UnmanagedType.LPStr)]
            public string path;
            public ushort vendor_id;
            public ushort product_id;
            public IntPtr serial_number;
            public ushort release_number;
            public IntPtr manufacturer_string;
            public IntPtr product_string;
            public ushort usage_page;
            public ushort usage;
            public int interface_number;
            public IntPtr next;
        }

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_init();

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr hid_enumerate(ushort vendor_id, ushort product_id);

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void hid_free_enumeration(IntPtr devs);

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr hid_open_path([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_write(IntPtr device, byte[] data, UIntPtr length);

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_read_timeout(IntPtr device, byte[] data, UIntPtr length, int milliseconds);

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void hid_close(IntPtr device);

        [DllImport("MyHidApi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hid_exit();

        private IntPtr _deviceHandle = IntPtr.Zero;
        private readonly object _lock = new object();
        private Thread _pollThread;
        private volatile bool _isRunning = false;
        private bool _surroundEnabled = false;
        private bool _micEncEnabled = false;
        private sbyte[] _lastGains = null;
        private string _lastPresetName = "ZXG";

        public event EventHandler<EksaStatusEventArgs> StatusUpdated;
        public bool IsConnected
        {
            get { return _deviceHandle != IntPtr.Zero; }
        }

        public EksaHardware()
        {
            hid_init();
            Connect();
            _isRunning = true;
            _pollThread = new Thread(PollWorker);
            _pollThread.IsBackground = true;
            _pollThread.Name = "EksaPollWorker";
            _pollThread.Start();
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, out int RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid HidGuid);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        private static string FindTargetCol01Path()
        {
            try
            {
                Guid hidGuid;
                HidD_GetHidGuid(out hidGuid);

                IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x10 | 0x02);
                if (devInfo == (IntPtr)(-1)) return null;

                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);

                int index = 0;
                string found = null;
                int cbDetail = IntPtr.Size == 8 ? 8 : 6;

                while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index++, ref ifData))
                {
                    int reqSize;
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    IntPtr detailData = Marshal.AllocHGlobal(reqSize);
                    Marshal.WriteInt32(detailData, cbDetail);

                    if (SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailData, reqSize, out reqSize, IntPtr.Zero))
                    {
                        IntPtr pPath = new IntPtr(detailData.ToInt64() + 4);
                        string path = Marshal.PtrToStringAuto(pPath);
                        if (path != null && path.IndexOf("vid_3206&pid_0f06", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            path.IndexOf("col01", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = path;
                            Marshal.FreeHGlobal(detailData);
                            break;
                        }
                    }
                    Marshal.FreeHGlobal(detailData);
                }

                SetupDiDestroyDeviceInfoList(devInfo);
                return found;
            }
            catch (Exception ex)
            {
                App.Log("FindTargetCol01Path exception: " + ex.Message);
                return null;
            }
        }

        public bool Connect()
        {
            lock (_lock)
            {
                if (_deviceHandle != IntPtr.Zero)
                    return true;

                // Win32 SetupAPI direct resolution guarantees finding COL01 (Vendor Interface)
                string col01Path = FindTargetCol01Path();

                // Fallback to hid_enumerate if SetupAPI returned null
                if (string.IsNullOrEmpty(col01Path))
                {
                    IntPtr devs = hid_enumerate(VID, PID);
                    if (devs != IntPtr.Zero)
                    {
                        IntPtr curr = devs;
                        while (curr != IntPtr.Zero)
                        {
                            HidDeviceInfo info = (HidDeviceInfo)Marshal.PtrToStructure(curr, typeof(HidDeviceInfo));
                            if (!string.IsNullOrEmpty(info.path) && info.path.IndexOf("col01", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                col01Path = info.path;
                                break;
                            }
                            curr = info.next;
                        }
                        hid_free_enumeration(devs);
                    }
                }

                if (!string.IsNullOrEmpty(col01Path))
                {
                    _deviceHandle = hid_open_path(col01Path);
                    if (_deviceHandle != IntPtr.Zero)
                    {
                        App.Log("Connected to EKSA Hardware on COL01: " + col01Path);
                        return true;
                    }
                }
                return false;
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                if (_deviceHandle != IntPtr.Zero)
                {
                    try { hid_close(_deviceHandle); } catch { }
                    _deviceHandle = IntPtr.Zero;
                }
            }
        }

        private byte CalcChecksum(byte[] buf, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++)
                sum = (sum + buf[i]) & 0xFF;
            return (byte)sum;
        }

        private bool WritePacket(byte[] cmdBytes)
        {
            lock (_lock)
            {
                if (_deviceHandle == IntPtr.Zero)
                {
                    if (!Connect())
                        return false;
                }

                byte[] packet = new byte[64];
                Array.Copy(cmdBytes, packet, cmdBytes.Length);
                packet[cmdBytes.Length] = CalcChecksum(cmdBytes, cmdBytes.Length);

                int written = hid_write(_deviceHandle, packet, new UIntPtr(64));
                if (written != 64)
                {
                    Disconnect();
                    return false;
                }

                return true;
            }
        }

        private bool SendPacket(byte[] cmdBytes, out byte[] response)
        {
            response = new byte[64];
            lock (_lock)
            {
                if (_deviceHandle == IntPtr.Zero)
                {
                    if (!Connect())
                        return false;
                }

                // Drain any pending write-only ACKs before sending a query command
                byte[] drain = new byte[64];
                while (hid_read_timeout(_deviceHandle, drain, new UIntPtr(64), 0) > 0) { }

                byte[] packet = new byte[64];
                Array.Copy(cmdBytes, packet, cmdBytes.Length);
                packet[cmdBytes.Length] = CalcChecksum(cmdBytes, cmdBytes.Length);

                int written = hid_write(_deviceHandle, packet, new UIntPtr(64));
                if (written != 64)
                {
                    Disconnect();
                    return false;
                }

                byte expectedOpcode = cmdBytes.Length > 4 ? cmdBytes[4] : (byte)0;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    int read = hid_read_timeout(_deviceHandle, response, new UIntPtr(64), 150);
                    if (read <= 0)
                        return false;
                    if (expectedOpcode == 0 || response[4] == expectedOpcode)
                        return true;
                }
                return false;
            }
        }

        public bool SetAlgorithmSwitch(byte arg1, byte arg2, byte arg3, byte arg4)
        {
            byte[] cmd = new byte[] {
                0x4F, 0x30, 0x80, 0x18, 0x31, 0x06,
                arg1, 0x00, arg2, 0x00, arg3, arg4
            };
            bool ok = WritePacket(cmd);
            App.Log(string.Format("SetAlgorithmSwitch({0}, {1}, {2}, {3}): ok={4}", arg1, arg2, arg3, arg4, ok));
            return ok;
        }

        public bool SetMicEnc(bool enable)
        {
            _micEncEnabled = enable;
            byte arg3 = enable ? (byte)1 : (byte)2;
            return SetAlgorithmSwitch(1, 0, arg3, 0);
        }

        public bool SetAudioMode(bool surround)
        {
            _surroundEnabled = surround;
            if (_lastGains != null)
            {
                return SetEqualizer(_lastGains, _lastPresetName);
            }
            if (surround)
            {
                return SetAlgorithmSwitch(1, 1, 0, 2) && SetAlgorithmSwitch(1, 1, 0, 0);
            }
            else
            {
                return SetAlgorithmSwitch(1, 2, 0, 1) && SetAlgorithmSwitch(1, 0, 0, 1);
            }
        }

        private static byte GetOemCustomizeType(string presetName, bool surround)
        {
            string p = presetName ?? "";
            if (surround)
            {
                if (string.Equals(p, "Deep Bass", StringComparison.OrdinalIgnoreCase)) return 0x10;
                if (string.Equals(p, "EKSA Standard", StringComparison.OrdinalIgnoreCase)) return 0x11;
                if (string.Equals(p, "FPS", StringComparison.OrdinalIgnoreCase)) return 0x12;
                if (string.Equals(p, "MOBA", StringComparison.OrdinalIgnoreCase)) return 0x13;
                return 0x16; // Surround Custom 1 slot (applies custom 10-band gains)
            }
            else
            {
                if (string.Equals(p, "Classic", StringComparison.OrdinalIgnoreCase)) return 0x00;
                if (string.Equals(p, "Deep Bass", StringComparison.OrdinalIgnoreCase)) return 0x01;
                if (string.Equals(p, "EKSA Standard", StringComparison.OrdinalIgnoreCase)) return 0x02;
                if (string.Equals(p, "Jazz", StringComparison.OrdinalIgnoreCase)) return 0x03;
                if (string.Equals(p, "Pack&Roll", StringComparison.OrdinalIgnoreCase)) return 0x04;
                if (string.Equals(p, "Pop", StringComparison.OrdinalIgnoreCase)) return 0x05;
                return 0x06; // Stereo Custom 1 slot (applies custom 10-band gains)
            }
        }

        private bool SendPeqBank(sbyte[] gains, byte bankType, byte customizeType)
        {
            // Packet 1: Bands 0..4 (frequencies 30, 60, 120, 250, 500 Hz, Q=2000)
            byte[] pkt1 = new byte[44];
            pkt1[0] = 0x4F; pkt1[1] = 0x30; pkt1[2] = 0x80; pkt1[3] = 0x18; pkt1[4] = 0x21; pkt1[5] = 0x26;
            pkt1[6] = bankType; pkt1[7] = 0x00; pkt1[8] = 0x04; pkt1[9] = 0x00;
            // Band 0: 30Hz, Q=2000
            pkt1[10] = 0x00; pkt1[11] = 0x1E; pkt1[12] = 0x07; pkt1[13] = 0xD0; pkt1[14] = (byte)gains[0];
            // Band 1: 60Hz, Q=2000
            pkt1[15] = 0x00; pkt1[16] = 0x3C; pkt1[17] = 0x07; pkt1[18] = 0xD0; pkt1[19] = (byte)gains[1];
            // Band 2: 120Hz, Q=2000
            pkt1[20] = 0x00; pkt1[21] = 0x78; pkt1[22] = 0x07; pkt1[23] = 0xD0; pkt1[24] = (byte)gains[2];
            // Band 3: 250Hz, Q=2000
            pkt1[25] = 0x00; pkt1[26] = 0xFA; pkt1[27] = 0x07; pkt1[28] = 0xD0; pkt1[29] = (byte)gains[3];
            // Band 4: 500Hz, Q=2000
            pkt1[30] = 0x01; pkt1[31] = 0xF4; pkt1[32] = 0x07; pkt1[33] = 0xD0; pkt1[34] = (byte)gains[4];
            // 8 zeroes: indices 35..42
            pkt1[43] = customizeType;

            // Packet 2: Bands 5..9 (frequencies 1000, 2000, 4000, 8000, 16000 Hz)
            // Verified OEM: Q=2000 for bands 5-6, Q=4000 (0x0FA0) for bands 7-9
            byte[] pkt2 = new byte[44];
            pkt2[0] = 0x4F; pkt2[1] = 0x30; pkt2[2] = 0x80; pkt2[3] = 0x18; pkt2[4] = 0x21; pkt2[5] = 0x26;
            pkt2[6] = bankType; pkt2[7] = 0x05; pkt2[8] = 0x09; pkt2[9] = 0x00;
            // Band 5: 1000Hz, Q=2000
            pkt2[10] = 0x03; pkt2[11] = 0xE8; pkt2[12] = 0x07; pkt2[13] = 0xD0; pkt2[14] = (byte)gains[5];
            // Band 6: 2000Hz, Q=2000
            pkt2[15] = 0x07; pkt2[16] = 0xD0; pkt2[17] = 0x07; pkt2[18] = 0xD0; pkt2[19] = (byte)gains[6];
            // Band 7: 4000Hz, Q=4000 (0x0FA0)
            pkt2[20] = 0x0F; pkt2[21] = 0xA0; pkt2[22] = 0x0F; pkt2[23] = 0xA0; pkt2[24] = (byte)gains[7];
            // Band 8: 8000Hz, Q=4000 (0x0FA0)
            pkt2[25] = 0x1F; pkt2[26] = 0x40; pkt2[27] = 0x0F; pkt2[28] = 0xA0; pkt2[29] = (byte)gains[8];
            // Band 9: 16000Hz, Q=4000 (0x0FA0)
            pkt2[30] = 0x3E; pkt2[31] = 0x80; pkt2[32] = 0x0F; pkt2[33] = 0xA0; pkt2[34] = (byte)gains[9];
            // 8 zeroes: indices 35..42
            pkt2[43] = customizeType;

            bool ok1 = WritePacket(pkt1);
            Thread.Sleep(5);
            bool ok2 = WritePacket(pkt2);
            Thread.Sleep(5);
            return ok1 && ok2;
        }

        public bool SetEqualizer(sbyte[] gains, string presetName = null)
        {
            if (gains == null || gains.Length < 10)
                return false;

            _lastGains = (sbyte[])gains.Clone();
            if (presetName != null)
                _lastPresetName = presetName;

            byte customizeType = GetOemCustomizeType(_lastPresetName, _surroundEnabled);

            // Send PEQ packets to bank 0x00 and active OEM slot on bank 0x01 without overwriting active slot
            bool okType0 = SendPeqBank(gains, 0x00, customizeType);
            bool okActiveSlot = SendPeqBank(gains, 0x01, customizeType);

            // Trigger algorithm switch (0x31) to activate the filter curve in the hardware DSP
            bool okSwitch;
            if (_surroundEnabled)
            {
                okSwitch = SetAlgorithmSwitch(1, 1, 0, 0);
            }
            else
            {
                okSwitch = SetAlgorithmSwitch(1, 0, 0, 1);
            }

            App.Log(string.Format("SetEqualizer(preset={0}, slot=0x{1:X2}): type0={2}, activeSlot={3}, switch={4}",
                _lastPresetName, customizeType, okType0, okActiveSlot, okSwitch));

            return (okType0 || okActiveSlot) && okSwitch;
        }

        public bool QueryStatus(out bool isConnected, out string deviceUid)
        {
            isConnected = false;
            deviceUid = null;

            byte[] cmd = new byte[] { 0x4F, 0x30, 0x80, 0x18, 0x32, 0x01, 0x00 };
            byte[] resp;
            if (!SendPacket(cmd, out resp))
                return false;

            // Validate header: 0x24/0x23, 0x30, 0x18, 0x80, 0x32
            if ((resp[0] == 0x24 || resp[0] == 0x23) && resp[1] == 0x30 && resp[4] == 0x32)
            {
                if (resp[6] == 0) // No error
                {
                    isConnected = true;
                    deviceUid = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}", resp[9], resp[10], resp[11], resp[12]);
                    return true;
                }
            }

            return false;
        }

        private void PollWorker()
        {
            while (_isRunning)
            {
                try
                {
                    bool connected = Connect();
                    bool statusConnected = false;
                    string uid = null;

                    if (connected && QueryStatus(out statusConnected, out uid))
                    {
                        var handler = StatusUpdated;
                        if (handler != null)
                        {
                            handler(this, new EksaStatusEventArgs
                            {
                                IsConnected = true,
                                DeviceUid = uid,
                                StatusSummary = "Headset Connected (5.8GHz Link)"
                            });
                        }
                    }
                    else
                    {
                        var handler = StatusUpdated;
                        if (handler != null)
                        {
                            handler(this, new EksaStatusEventArgs
                            {
                                IsConnected = false,
                                DeviceUid = null,
                                StatusSummary = "Headset Disconnected / Standby"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Log("PollWorker exception: " + ex);
                }

                Thread.Sleep(3000);
            }
        }

        public void Dispose()
        {
            _isRunning = false;
            try
            {
                if (_pollThread != null)
                    _pollThread.Join(500);
            }
            catch { }
            Disconnect();
            hid_exit();
        }
    }
}
