# Audio Duplicator

A lightweight Windows background service that captures the system/virtual audio stream via **WASAPI loopback** and duplicates it in real time to **two physical playback devices simultaneously** (e.g. speakers *and* headphones). It runs entirely from the **system tray** — no window, no browser.

See [`design.md`](design.md) for the full architecture and rationale.

## Prerequisites

- Windows with **.NET Framework 4.8**
- A virtual audio device for the input — [VB-Cable](https://vb-audio.com/Cable/) is recommended. Set **CABLE Input** as your default playback device; this tool then fans its output out to your two physical devices.

## Build & Run

```powershell
# 1. Fetch the NAudio dependency into the project root (first time only)
.\setup.ps1

# 2. Compile
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /r:NAudio.dll /target:exe /out:AudioDuplicator.exe AudioDuplicator.cs

# 3. Run
.\AudioDuplicator.exe
```

## Usage

Right-click the tray icon to:

- **Input Device** — pick the capture source (defaults to *System Default Render Loopback*; choose your virtual cable here).
- **Output 1 / Output 2** — pick the two physical devices to play to. The current selection is shown next to each menu and check-marked in the submenu.
- **Start / Stop Duplication** — toggle the pipeline (also available by double-clicking the tray icon).

Selections are saved to `settings.json` and re-applied on the next launch; if both outputs are set, duplication auto-starts. Changing a device while running restarts the pipeline live.

## Diagnostics

Runtime diagnostics are written to `debug.log` in the app folder: negotiated audio formats, per-second capture throughput, buffer depth, buffer-flush counts, and playback state. Tail it with `Get-Content .\debug.log -Wait`.

## How it works

One WASAPI loopback capture feeds two buffered providers, each driving its own `WasapiOut`. A resampler is inserted per-output only when the device cannot accept the capture format natively (checked via `AudioClient.IsFormatSupported`), which avoids an unnecessary same-rate resampler that would otherwise starve the buffer and cause stuttering.
