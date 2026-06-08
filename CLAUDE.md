# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A Windows-only background service (.NET Framework 4.8, C#) that captures the system/virtual audio stream via WASAPI loopback and duplicates it in real time to **two** physical playback devices simultaneously. It runs entirely from the **system tray** — there is no GUI window and no web interface; all control is via the tray context menu. See `design.md` for the architectural rationale (why a user-mode duplicator + VB-Cable instead of a kernel driver).

## Build & Run

There is no project/solution file — compile directly with the .NET Framework C# compiler:

```powershell
# Fetch NAudio.dll into the project root (first-time only)
.\setup.ps1

# Compile
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /r:NAudio.dll /target:exe /out:AudioDuplicator.exe AudioDuplicator.cs

# Run
.\AudioDuplicator.exe
```

There are no tests, linters, or package manager — the only dependency is `NAudio.dll` (NAudio 1.10.0), referenced at compile time and loaded at runtime from the project root. Runtime prerequisite: a virtual audio device (VB-Cable) as the capture input.

To iterate during development, the build/restart cycle is: stop the running process, recompile, relaunch, then read `debug.log`:
```powershell
Stop-Process -Name AudioDuplicator -Force -ErrorAction SilentlyContinue
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /r:NAudio.dll /target:exe /out:AudioDuplicator.exe AudioDuplicator.cs
Remove-Item debug.log -ErrorAction SilentlyContinue; Start-Process .\AudioDuplicator.exe
```

## Architecture

Everything lives in one file: **`AudioDuplicator.cs`** (single `static class Program`). Three pieces run off `Main`:

1. **System tray UI** (`SetupSystemTray`) — the only control surface. Uses the legacy WinForms `ContextMenu`/`MenuItem` API. The `Input Device` / `Output 1` / `Output 2` submenus are **rebuilt on every `Popup`** by `PopulateDeviceMenu` so they reflect currently-active render endpoints and the saved selection (radio-checked). Selecting a device → `OnDeviceSelected` updates the in-memory id, calls `SaveSettings`, and live-restarts the pipeline if running. If the tray icon can't be created (headless session), `Main` falls back to a blocking wait and logs to console.
2. **Audio duplication core** (`StartDuplicationInternal` / `StopDuplicationInternal`). One `WasapiLoopbackCaptureEx` capture feeds **two** `BufferedWaveProvider`s, each driving its own `WasapiOut`.
3. **Stats loop** (`StatsLoop`, 1s tick) writes a per-second `STATS` line to `debug.log`.

Key behaviors to preserve when editing:
- **Resampling decision (`RequiresResampling`)**: a per-output `MediaFoundationResampler` is inserted **only** when `AudioClient.IsFormatSupported` says the device can't take the capture format directly. Do *not* revert to the naive `CompareFormats`-only gate: a 48k float capture and a 48k "Extensible" mix format are the same PCM data, and inserting an unnecessary same-rate resampler under-consumes the buffer and causes severe stutter (this was a real bug — see git history).
- **Latency control**: capture buffer 40ms, outputs target 50ms. `PruneBuffer` clears a provider's buffer when its `BufferedDuration` exceeds 300ms — a safety valve for drift. With the resampler fix it should rarely if ever fire; if `prunes*` in `debug.log` climbs continuously, something upstream is wrong.
- **Concurrency**: all audio start/stop/reconfig is guarded by `_audioLock`. Audio device fields (`_capture`, `_play1/2`, `_provider1/2`, etc.) are global mutable state — touch them only inside `_audioLock`. Diagnostic counters use `Interlocked`.
- **Custom loopback class**: `WasapiLoopbackCaptureEx` subclasses `WasapiCapture` to force `AudioClientStreamFlags.Loopback` while still allowing a custom buffer size (NAudio's stock `WasapiLoopbackCapture` doesn't expose this).

### Logging
`Log()` writes timestamped lines to both the console and `debug.log` (in the exe's base directory). This matters because in tray mode there is no visible console — `debug.log` is the only way to observe the running service. The per-second `STATS` line reports: `captured` (ms of audio delivered by the capture per second — should be ~1000 in steady state), both buffer depths, cumulative prune counts, and each `WasapiOut.PlaybackState`. This is the primary debugging tool for audio glitches.

### Settings & JSON
`settings.json` (in the exe's base directory) holds the three selected device IDs (`inputId`, `out1Id`, `out2Id`). Read on startup (`LoadSettings`), rewritten on every device change. If both outputs are set at launch, duplication auto-starts. `inputId` of `"default"`/empty means the system default render endpoint (loopback). There is no JSON library: `SaveSettings` uses `string.Format` with manual escaping and `ExtractJsonValue` is a regex over `"key":"value"` — keep the file flat and string-valued.
