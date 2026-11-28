# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A Windows-only background service (.NET Framework 4.8, C#) that captures the system/virtual audio stream via WASAPI loopback and duplicates it in real time to **two** physical playback devices simultaneously. It runs from the **system tray** — all control is via the tray context menu, plus one small **Filters and Sine Tone** window opened from it. There is no web interface. See `design.md` for the architectural rationale (why a user-mode duplicator + VB-Cable instead of a kernel driver).

## Build & Run

There is no project/solution file — compile directly with the .NET Framework C# compiler:

```powershell
# Fetch NAudio.dll into the project root (first-time only)
.\setup.ps1

# Compile
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /r:NAudio.dll /target:winexe /out:AudioDuplicator.exe AudioDuplicator.cs

# Run
.\AudioDuplicator.exe
```

There are no tests, linters, or package manager — the only dependency is `NAudio.dll` (NAudio 1.10.0), referenced at compile time and loaded at runtime from the project root. Runtime prerequisite: a virtual audio device (VB-Cable) as the capture input.

To iterate during development, the build/restart cycle is: stop the running process, recompile, relaunch, then read `debug.log`:
```powershell
Stop-Process -Name AudioDuplicator -Force -ErrorAction SilentlyContinue
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /r:NAudio.dll /target:winexe /out:AudioDuplicator.exe AudioDuplicator.cs
Remove-Item debug.log -ErrorAction SilentlyContinue; Start-Process .\AudioDuplicator.exe
```

## Architecture

Everything lives in one file: **`AudioDuplicator.cs`** (single `static class Program`). Three pieces run off `Main`:

1. **System tray UI** (`SetupSystemTray`) — the only control surface. Uses the legacy WinForms `ContextMenu`/`MenuItem` API. The `Input Device` / `Output 1` / `Output 2` submenus are **rebuilt on every `Popup`** by `PopulateDeviceMenu` so they reflect currently-active render endpoints and the saved selection (radio-checked). Selecting a device → `OnDeviceSelected` updates the in-memory id, calls `SaveSettings`, and live-restarts the pipeline if running. If the tray icon can't be created (headless session), `Main` falls back to a blocking wait and logs to console.
   - **Feedback-loop block**: `PopulateDeviceMenu` disables output devices equal to the (resolved) input, and input devices already used as an output. `StartOutput` also refuses a device equal to `_captureDeviceId`, because a "System Default" input can change to an output device later.
   - **Sine Tone and Equalizer window** (`ShowFilterWindow`): one tab per output (`BuildOutputPage`), each with independent sine-tone (`AddToneGroup`) and 3-band EQ (`AddEqGroup`) controls. There is no user low-pass control any more (the EQ's high cut replaced it). The tone is 10–250 Hz on a logarithmic slider. The tone also has −/+ buttons that step it by 1 Hz (they set the value directly, because a log slider step is more than 1 Hz near the top). A mouse press steps at once, then 5 times per second while held (a 200 ms WinForms timer, stopped on MouseUp / capture lost / disable); a mouse `Click` is ignored (its args are `MouseEventArgs`) so the press is not counted twice, and keyboard clicks step once. Values live in `OutputSlot` (volatile, written by the UI thread, read by the playback thread) and are saved 500 ms after the last change.
   - **Equalizer** (`AddEqGroup`): 3 points (`EqSettings.Bands`), 10–250 Hz (defaults 20/50/125 Hz; a saved frequency outside the range loads as the band default), −24…+12 dB in 0.5 dB steps, set only by dragging them on `EqPlot` (x = frequency, y = gain; double-click = 0 dB); a read-out label per point sits under the plot. The **curve passes through every point**: the `EqSettings` constructor turns points into filters — an always-on Butterworth **high cut** at the highest point (`CutoffHz`; `MaxHz` = 250 Hz when the EQ is off), one RBJ peaking band per point whose **width** is the octave distance to its nearest neighbour point (0.25–2.5 oct), and band **gains solved** by damped least squares (Levenberg–Marquardt, numeric Jacobian, band gains limited to ±36 dB) so total response (bands + high cut, at 48 kHz) equals each point's gain. Points at one frequency with different gains cannot all be met; the solver then gives the closest fit. `GainDb` = point (user) gain; `FilterDb`/`FilterQ` = solved band values used by `EqWaveProvider`. `EqSettings` is **immutable**; the UI replaces `slot.Eq` as a whole and `EqWaveProvider` recomputes coefficients when the reference changes (off, or no band needed = bypass of the peaking bands).
2. **Audio duplication core** (`StartDuplicationInternal` / `StopDuplicationInternal`). One `WasapiLoopbackCaptureEx` capture feeds **two** `OutputSlot`s, each with a `BufferedWaveProvider` → `ToneMixWaveProvider` (20% sine, mixed before filtering) → `EqWaveProvider` (3-band peaking EQ, bypassed when off or flat) → `LowPassWaveProvider` (the EQ's high cut: 2nd-order Butterworth, per audio channel, always on, corner = `slot.Eq.CutoffHz`; keeps the capture format, so it does not affect `RequiresResampling`) → optional resampler → its own `WasapiOut`.
3. **Stats loop** (`StatsLoop`, 1s tick) writes a per-second `STATS` line to `debug.log`.
4. **Reconnect loop** (`ReconnectLoop`, 5s tick, first check immediately) opens the saved devices and reopens lost devices while `_wantRunning` is true. At startup `Main` only sets `_wantRunning`; the loop does the opening, because after boot some devices appear late. The loop makes its `MMDeviceEnumerator` again after an exception (the audio service can be not ready at boot).

Key behaviors to preserve when editing:
- **No default fallback**: a saved device that is missing is waited for, never replaced by the system default. The default render endpoint is used only when the user explicitly selects "System Default" (`inputId` = `"default"`). An empty `inputId` means "not selected", and duplication does not start.
- **Reconnect**: `_wantRunning` is the user's intent; `_isRunning` only says the capture is up. A lost output is closed and reopened alone (`StopOutput` / `StartOutput`), so the other output is not interrupted. A lost capture restarts the whole pipeline, because the capture owns the buffers. Do not use `WasapiOut.PlaybackState` to detect a lost device: it still says `Playing` after the play thread dies with an exception. Use the `Faulted` flag, set from `PlaybackStopped`.
- **Resampling decision (`RequiresResampling`)**: a per-output `MediaFoundationResampler` is inserted **only** when `AudioClient.IsFormatSupported` says the device can't take the capture format directly. Do *not* revert to the naive `CompareFormats`-only gate: a 48k float capture and a 48k "Extensible" mix format are the same PCM data, and inserting an unnecessary same-rate resampler under-consumes the buffer and causes severe stutter (this was a real bug — see git history).
- **Latency control**: capture buffer 40ms, outputs target 50ms. `PruneBuffer` clears a provider's buffer when its `BufferedDuration` exceeds 300ms — a safety valve for drift. With the resampler fix it should rarely if ever fire; if `prunes*` in `debug.log` climbs continuously, something upstream is wrong.
- **Concurrency**: all audio start/stop/reconfig is guarded by `_audioLock`. Audio device fields (`_capture`, `_out1/2`, etc.) are global mutable state — touch them only inside `_audioLock`. Diagnostic counters use `Interlocked`.
- **Custom loopback class**: `WasapiLoopbackCaptureEx` subclasses `WasapiCapture` to force `AudioClientStreamFlags.Loopback` while still allowing a custom buffer size (NAudio's stock `WasapiLoopbackCapture` doesn't expose this).

### Logging
`Log()` writes timestamped lines to both the console and `debug.log` (in the exe's base directory). This matters because in tray mode there is no visible console — `debug.log` is the only way to observe the running service. The per-second `STATS` line reports: `captured` (ms of audio delivered by the capture per second — should be ~1000 in steady state), both buffer depths, cumulative prune counts, and each `WasapiOut.PlaybackState`. This is the primary debugging tool for audio glitches.

### Settings & JSON
`settings.json` (in the exe's base directory) holds the three selected device IDs (`inputId`, `out1Id`, `out2Id`) and tone settings (`tone1On`/`tone1Hz`, `tone2On`/`tone2Hz`) and EQ settings (`eq1On`, `eq1F1`…`eq1F3` in Hz, `eq1G1`…`eq1G3` in dB; same for `eq2`), all stored as strings. Old `lp1*`/`lp2*` low-pass keys are ignored and dropped on the next save. Read on startup (`LoadSettings`), rewritten on every device or filter change. If the input and both outputs are set at launch, duplication auto-starts (and waits for missing devices). `inputId` of `"default"` means the system default render endpoint (loopback); empty means not selected. There is no JSON library: `SaveSettings` uses `string.Format` with manual escaping and `ExtractJsonValue` is a regex over `"key":"value"` — keep the file flat and string-valued.
