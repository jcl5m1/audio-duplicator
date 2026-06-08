# Windows Virtual Audio Device & Duplicator Design

This project implements a virtual audio output device and duplicates its stream to two physical playback devices in Windows.

## Architectural Decision: Virtual Device + User-Mode Duplicator

Creating a raw kernel-mode audio driver from scratch is highly complex, requires the Windows Driver Kit (WDK), requires running Windows in "Test Signing Mode," and carries a significant risk of causing System Crashes (BSODs).

Instead, this project uses a hybrid solution:
1. **Virtual Device**: Recommend installing **VB-Audio Cable** (a safe, free, industry-standard virtual driver).
2. **Duplicator Backend**: A custom, lightweight, high-performance C# service (.NET Framework 4.8) that:
   - Runs in the background as a **system tray** application (or silently/headless if no interactive session).
   - Captures the audio stream from VB-Cable using WASAPI Loopback.
   - Duplicates the stream in real-time to two user-selected physical outputs (e.g. Speakers and Headphones).
   - Handles resampling automatically (only when a target device cannot accept the capture format natively).
3. **Control Surface**: All configuration is done from the **system tray context menu** — input device, the two outputs, and start/stop. There is no GUI window or web interface.

---

## Prerequisites

To establish the virtual audio device, you will need to download and install **VB-Cable** manually:
- **Download Link**: [VB-Cable Driver (Official Website)](https://vb-audio.com/Cable/)
- **Installation**: Extract the downloaded ZIP file, right-click `VBCABLE_Setup_x64.exe` and select **Run as administrator**. After installation, a reboot is required for Windows to fully load the driver.
- **Setup**: Once installed, you will set "CABLE Input" as your default playback device, and our duplicator tool will split its output to your two physical devices.

---

## File Structure

- `AudioDuplicator.cs` - C# background application source code (single file)
- `AudioDuplicator.exe` - Compiled executable (build output; not checked in)
- `NAudio.dll` - Audio helper library (restored by `setup.ps1`; not checked in)
- `setup.ps1` - Downloads NAudio and places `NAudio.dll` in the project root
- `settings.json` - Persisted device selections (machine-specific runtime state; not checked in)
- `debug.log` - Runtime diagnostics log (not checked in)

---

## Control Surface (System Tray)

Right-click the tray icon to access:
- **Input Device** — capture source, defaulting to *System Default Render Loopback*. Lists all active render endpoints; the current selection is shown in the label and check-marked.
- **Output 1** / **Output 2** — the two physical render devices to play to.
- **Start / Stop Duplication** — toggles the pipeline. Double-clicking the tray icon also toggles it.

Device menus are rebuilt each time the menu opens, so newly connected/removed devices appear automatically. Selecting a device saves `settings.json` and, if duplication is already running, restarts the pipeline live with the new selection.

---

## How to Compile & Run

1. Open a PowerShell terminal in this folder.
2. Restore the dependency (first time only): `.\setup.ps1`
3. Compile:
   ```powershell
   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /r:NAudio.dll /target:exe /out:AudioDuplicator.exe AudioDuplicator.cs
   ```
4. Run:
   ```powershell
   .\AudioDuplicator.exe
   ```
5. Configure input and the two outputs from the tray icon's right-click menu, then **Start Duplication**.
