# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

**RGDSCapture** is a software capture card for the Anbernic RG Dual Screen handheld gaming console. It runs on Windows and:
- Connects to the RG DS device over SSH
- Receives both screen feeds as H.264-encoded video via RTP/UDP at 30 fps each
- Monitors audio from the device's headphone jack via a 3.5mm cable connected to the PC's Line-In
- Supports multiple layout modes, recording, screenshots, and a speedrun timer overlay
- Includes auto-recovery for frozen streams and real-time FPS monitoring

## Project Structure

The project is a **WPF (.NET 8 / C#)** desktop application with unsafe code enabled.

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | Main UI orchestrator; manages all state (connection, streams, recording, audio, timer, logs, fullscreen) |
| `RtpStreamReceiver.cs` | Receives H.264-over-RTP on a UDP port, reassembles FU-A NAL units, decodes via FFmpeg.AutoGen, fires FrameReady events with BGRA pixel buffers; tracks FPS and freeze state |
| `AudioMonitor.cs` | Real-time Line-In passthrough using NAudio; 48 kHz 16-bit stereo PCM with drift-corrected ring buffer; exposes L/R VU meter levels |
| `SshManager.cs` | SSH client wrapper; connects to device and manages GStreamer pipeline lifecycle (start/stop/restart both screens) |
| `ThemeManager.cs` | Persistence layer for dark/light theme selection |
| `FullScreenOverlay.cs` | Dedicated overlay window for fullscreen playback |
| `FFmpegBinariesHelper.cs` | FFmpeg binary registration and path resolution |
| `ConnectDialog.xaml.cs` | SSH connection dialog (IP, port, username, password) |

### Supporting Files

- `App.xaml` / `App.xaml.cs` – Application entry point and initialization
- `MainWindow.xaml` – Main UI layout
- `Themes/*.xaml` – Dark/light theme resource dictionaries
- `RGDSCapture.csproj` – NuGet dependencies and build configuration

## Build & Development Commands

### Restore, Build, Run

```powershell
# Restore NuGet packages
dotnet restore

# Build Release configuration
dotnet build -c Release

# Build Debug configuration
dotnet build -c Debug

# Run the application
dotnet run --project RGDSCapture

# Run with debugger (VS Code)
# Press F5 (or use Debug → Start Debugging)
```

### Common Workflows

**Running in the debugger:**
```powershell
# Set breakpoints in VS Code, then press F5
# (pre-configured in .vscode/launch.json)
```

**Testing a specific component:**
- No automated tests are present; testing is manual via the app UI
- Test connections with a local `ssh localhost` to verify SSH.NET behavior
- Test RTP reception with `ffplay -protocol_whitelist file,udp,rtp udp://<local-ip>:5000` on the PC

**Hot reload during development:**
```powershell
dotnet watch run
```

## Architecture & Key Concepts

### Dual Stream Processing

The app manages **two independent RTP video streams** (top and bottom screens):
- Each stream has its own `RtpStreamReceiver` instance listening on ports **5000** (top) and **5001** (bottom)
- Each receiver independently decodes H.264 NAL units via FFmpeg and fires `FrameReady` events
- A single `DispatcherTimer` on the main thread polls the most recent frame from each stream and composites them onto the UI

### Freeze Detection & Auto-Recovery

- `RtpStreamReceiver.IsFrozen` triggers if no frame arrives within `FreezeThresholdSeconds` (5 seconds)
- MainWindow's `_freezeCheckTimer` polls both streams every ~1 second
- If frozen, the app automatically restarts the GStreamer pipeline (via SSH) up to **3 retries**
- After 3 retries, it requires manual intervention (user clicks "Restart")

### Audio Pipeline

Audio is **not** routed over the network — it comes directly from a 3.5mm cable connected to the PC's Line-In:
1. **Capture:** NAudio `WaveInEvent` captures at 48 kHz, 16-bit, stereo
2. **Ring Buffer:** Custom `RingBufferProvider` maintains an ~80 ms buffer with drift correction (drops old data if buffer is too full, inserts silence if too empty)
3. **Playback:** NAudio `WaveOutEvent` plays back to the selected output device
4. **Volume:** Applied in-place during capture, before the ring buffer

### Fullscreen Overlay

- `FullScreenOverlay` is a borderless, topmost overlay window
- When activated, it renders the selected screen (or both stacked/side-by-side) in fullscreen
- Falls back to a `WriteableBitmap` if the selected screen hasn't received a frame yet

### Recording

- Top and bottom screens are recorded independently using `ffmpeg.exe` subprocesses
- Each subprocess reads H.264 packets directly from the UDP stream (via a local listening socket) and remuxes into MP4 without re-encoding
- Files are saved to `My Videos\RGDSCapture\` with ISO 8601 timestamps

### Speedrun Timer

- Built on a `Stopwatch` with manual offset tracking
- A `DispatcherTimer` updates the timer display every frame
- Lap splits are logged with timestamps to the event log

### Theming

- Dark/light theme selection is persisted to disk (likely in `AppData\Local` or similar)
- Theme is loaded at startup via `ThemeManager.Load()` and applied to the `Application.Current.Resources` dictionary

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `FFmpeg.AutoGen` | 6.x | P/Invoke bindings for FFmpeg native libraries (decoding H.264) |
| `SSH.NET` | 2024.x | SSH client for device control |
| `NAudio` | 2.x | Audio device enumeration, capture, and playback |
| `BouncyCastle.Cryptography` | (transitive) | Cryptographic support for SSH.NET |

**FFmpeg native DLLs** (LGPL 2.1, included in repo root):
- `avcodec-60.dll`, `avformat-60.dll`, `avutil-58.dll`, `swscale-7.dll`, etc.
- These are copied to the build output directory automatically

## Safety & Concurrency

- **Thread safety:** Frame buffers for each stream are protected by `_topLock` and `_bottomLock` (simple object monitors)
- **Unsafe code:** `RtpStreamReceiver` uses unsafe pointers for FFmpeg context, codec context, frames, and swscale context; this is required for FFmpeg.AutoGen interop
- **Cancellation:** Both streaming and audio use `CancellationTokenSource` for clean shutdown
- **UI updates:** All UI updates happen on the dispatcher thread via `Invoke` or `BeginInvoke`

## Common Patterns

### Adding a New Stream Control

1. Declare new `RtpStreamReceiver?` field
2. In the Start handler, instantiate and call `.Start()`
3. Subscribe to `FrameReady` with a lock-protected delegate
4. In the Render loop, lock and check for pending frames
5. In the Stop handler, cancel and dispose

### Adding a New Audio Output Device Option

1. Call `AudioMonitor.GetOutputDevices()` to enumerate
2. Store the selected device index
3. Call `_audioMonitor.Start(inputIndex, outputIndex)` with the index (use `-1` for system default)

### Adding a New Layout Mode

1. Add an enum value
2. In the Render handler, adjust the composite rect logic
3. Update the UI menu to select the new mode

## Testing Notes

- No unit test framework is present; all testing is manual via the UI
- To manually test stream reception, use `ffplay` with an RTP URL
- To manually test SSH, use `ssh -v user@device` from PowerShell/cmd
- To manually test audio, connect a test signal to Line-In and check the VU meters

## Key Files to Read First

1. **RtpStreamReceiver.cs** — Understand the RTP/FFmpeg decoding loop and freeze detection
2. **MainWindow.xaml.cs** — Understand the overall orchestration and state machine
3. **AudioMonitor.cs** — Understand the ring buffer and drift correction logic
4. **SshManager.cs** — Understand the GStreamer pipeline commands and device control

## Recent Work

Per git log:
- Dark mode XAML fixes
- Fullscreen streaming improvements
- FPS counter debugging
- Recording debug work

Keep an eye on the freeze-detection threshold and auto-recovery logic if working on streaming reliability.
