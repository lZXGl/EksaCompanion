# EKSA E910 Companion - Project Handoff & Diagnostic Blueprint

## 1. Hardware & Environment
- **Headset**: EKSA E910 5.8GHz Wireless Gaming Headset
- **USB Audio Transmitter/Dongle**: Hongkong Nano IC USB Audio Bridge (`VID_3206`, `PID_0F06`)
- **HID Interfaces**:
  - `COL01` (`HID\VID_3206&PID_0F06&MI_03&COL01`): Vendor Hardware DSP Interface (PEQ, ENC, Surround, Algorithm Switches)
  - `COL02` (`HID\VID_3206&PID_0F06&MI_03&COL02`): Consumer Control (Hardware volume wheel)
- **OEM Binaries**:
  - Application: `C:\Program Files (x86)\EKSA 7.1 Surround Sound\ToneTuningTool.exe`
  - HID Library: `C:\Program Files (x86)\EKSA 7.1 Surround Sound\MyHidApi.dll` (32-bit x86 standard hidapi)
  - Presets: `C:\Program Files (x86)\EKSA 7.1 Surround Sound\DefaultFile\VID-3206&PID-0F06.json`
- **Companion App Workspace**: `EksaCompanion\`
- **Target Executable**: `bin\x86\Release\EksaTray.exe`
- **Desktop Shortcut**: `%USERPROFILE%\Desktop\EKSA E910 Companion.lnk`

---

## 2. What Is Fully Completed & Working
1. **Desktop Shortcut & Icon**:
   - Installed multi-resolution electric purple headset icon in `app.ico` and embedded into `EksaTray.exe`.
   - Desktop shortcut `%USERPROFILE%\Desktop\EKSA E910 Companion.lnk` works.
2. **UI Design & Palette**:
   - Palette completely switched from cyan to Obsidian Electric Purple (`#A855F7`, `#C084FC`, `#25123E`).
3. **Elimination of Fake Metrics**:
   - Fake "CONNECTED" badge and artificial battery countdown timer completely deleted.
   - Genuine hardware indicator: `5.8GHz DONGLE ACTIVE` verified on `COL01` with physical UID (`24:AE:13:27`).
4. **Idle Volume Reducer Removed**:
   - Removed the 10-minute 1% volume attenuator (digital silence already sends `0.0` samples over USB, and the dongle remains active when the headset is powered off).
   - Added startup safeguard in `MainWindow.xaml.cs` to restore volume to 50% if previously stuck at `<= 2%`.
5. **SetupAPI COL01 Resolution**:
   - Stable Win32 SetupAPI enumeration correctly binds directly to `COL01` across x86/x64 marshaling (`cbDetail = 6` on x86).

---

## 3. Remaining Problems & Discovered Root Causes

### Problem: EQ, Surround & Mic ENC Don't Change Sound on Headset
Through binary disassembly of `ToneTuningTool.exe` (`usbHidWriteDevice` at `0x0E3360`, `usbHidSend_SetPEQ` at `0x0E3EA2`, `usbHidSend_SetAlgorithmSwitch` at `0x0E447E`), three root causes were identified:

#### Root Cause 1: PEQ Filter Bank Mismatch (`type: 0` vs `type: 1`)
- In `VID-3206&PID-0F06.json`:
  - `Surround` mode uses `type = 0`
  - `Stereo` mode uses `type = 1`
- In `EksaHardware.cs` (line 282), byte 6 (`pkt[6]`) is hardcoded to `0x01` (Stereo only).
- When the dongle is running in Surround mode (`type: 0`), writing to `type: 1` updates an inactive filter bank.

#### Root Cause 2: Missing Algorithm Switch Trigger (`0x31`)
- The hardware DSP requires an explicit algorithm activation packet (`0x31` `usbHidSend_SetAlgorithmSwitch`) to apply filter changes to the audio pipeline.
- Verified packet structure:
  - Header: `4F 30 80 18 31 06`
  - Byte 6: `arg1`
  - Byte 7: `0x00`
  - Byte 8: `arg2`
  - Byte 9: `0x00`
  - Byte 10: `arg3` (Mic ENC: `1` = on, `2` = off)
  - Byte 11: `arg4` (Surround: `1` = 7.1, `2` = stereo)

#### Root Cause 3: False Failure from `hid_read_timeout`
- In `EksaHardware.cs`, `SendPacket` called `hid_read_timeout(..., 300)` and reported failure (`ok=False`) if the read timed out.
- `ToneTuningTool.exe` **never** calls `hid_read` after `SetPEQ` or `SetAlgorithmSwitch`; it only calls `hid_write`.

---

## 4. Exact Protocol Map Decoded from `ToneTuningTool.exe`

| Opcode | Command Name | Header Bytes | Length | Behavior / Purpose |
|---|---|---|---|---|
| `0x01` | `ReadVersion` | `4E 30 80 18 01 00` | 32 bytes (`0x20`) | Query dongle firmware version |
| `0x20` | `ReadAudio` | `4F 30 80 18 20 01` | 64 bytes (`0x40`) | Read current hardware audio routing |
| `0x30` | `SetVol` | `4E 30 80 18 30 06` | 32 bytes (`0x20`) | Set hardware volume endpoint |
| `0x21` | `SetPEQ` | `4F 30 80 18 21 26` | 64 bytes (`0x40`) | 10-band parametric EQ (2 packets of 45 bytes) |
| `0x31` | `SetAlgorithmSwitch` | `4F 30 80 18 31 06` | 64 bytes (`0x40`) | Activate DSP algorithms (Surround, Mic ENC) |
| `0x32` | `ReadStatus` | `4F 30 80 18 32 01` | 64 bytes (`0x40`) | Query DSP algorithm status |
| `0x99` | `SpectrumSwitch` | `4F 30 80 18 99 01` | 64 bytes (`0x40`) | Real-time audio spectrum telemetry toggle |

*Note on Packet Header prefix:*
- Commands starting with `0x4E` ('N') expect 32-byte write buffers.
- Commands starting with `0x4F` ('O') expect 64-byte write buffers.

---

## 5. Implementation Checklist (Completed)
1. **Updated `SetEqualizer()` in `EksaHardware.cs`**:
   - Sends PEQ packets to **both** `type: 0` (Surround bank) and `type: 1` (Stereo & Surround `0x10` customizeType slots) so EQ works in any audio mode.
   - Uses write-only `WritePacket()` verifying `hid_write(...) == 64` without blocking on `hid_read_timeout`, and drains pending ACKs before `QueryStatus()`.
2. **Updated `SetAlgorithmSwitch()` in `EksaHardware.cs`**:
   - Format: `4F 30 80 18 31 06 [arg1] 00 [arg2] 00 [arg3] [arg4]` + checksum.
   - Triggers `SetAlgorithmSwitch` (`0x31`) after `SetEqualizer()` (`SetAlgorithmSwitch(1, 1, 0, 0)` for Surround, `SetAlgorithmSwitch(1, 0, 0, 1)` for Stereo) to activate the filter curve in the hardware DSP.
3. **Built & Verified**:
   - Compiled `bin\x86\Release\EksaTray.exe` via `MSBuild EksaCompanion.csproj /p:Configuration=Release /p:Platform=x86`.
   - Verified in `app.log`: `SetEqualizer: type0=True, surrSlot=True, stereoSlot=True, switch=True`.
