# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

**RGDSCapture** is a software capture card for the Anbernic RG Dual Screen handheld. It is a WPF (.NET 8) Windows app that:
- Connects to the RG DS over SSH and starts GStreamer H.264 pipelines on the device
- Receives both screens as RTP/UDP streams (ports **5000** top, **5001** bottom) and decodes them with FFmpeg
- Passes audio through from the PC's Line-In jack (physical 3.5mm cable — audio never touches the network)
- Records each screen to MP4 with zero re-encoding, takes screenshots, and provides a speedrun timer

## Build & Run

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project RGDSCapture.csproj
dotnet publish -c Release        # output consumed by RGDSCaptureSetup.iss (Inno Setup)
```

There are no automated tests; verification is manual. A useful smoke test: launch the built exe, confirm it stays alive, and check `%APPDATA%\RGDSCapture\crash.log` does not appear.

**Changelog discipline:** every user-visible change must add a bullet to the `[Unreleased]` section of `CHANGELOG.md` (Keep a Changelog format: Added / Changed / Fixed / Removed). At release time, `[Unreleased]` is renamed to the version + date, and `AppVersion` in `RGDSCaptureSetup.iss` plus `AssemblyVersion`/`FileVersion` in the csproj are bumped to match.

## Architecture (MVVM, no external MVVM package)

```
Core/         Enums (ScreenId, LayoutMode, AppTheme, ConnectionState, StreamHealth),
              AppPaths, AppSettings + SettingsService (JSON in %APPDATA%\RGDSCapture)
Services/     All backend logic — no UI dependencies except ThemeService/ScreenshotService
ViewModels/   Mvvm.cs (ObservableObject, RelayCommand, AsyncRelayCommand) + one VM per feature
Views/        MainWindow (thin shell) + UserControls in Views/Controls + dialogs
Themes/       Dark.xaml / Light.xaml are COLORS ONLY; Controls.xaml holds ALL control styles
```

**Composition root** is `App.xaml.cs OnStartup` (no StartupUri): loads settings, applies theme, builds `MainViewModel`, shows `MainWindow`. Crash logging goes to the in-app event log plus `%APPDATA%\RGDSCapture\crash.log`.

### Data flow for video

1. `SshService` starts GStreamer pipelines on the device (pipeline strings are constants in that file — they are device-specific, treat as canonical).
2. `RtpStreamReceiver` (one per screen) owns the UDP port, tracks RTP sequence numbers (drops corrupt FU-A fragments on packet loss), reassembles Annex-B NALs, decodes via FFmpeg.AutoGen into a **reused** BGRA buffer, and raises `FrameReady`.
3. `ScreenViewModel.OnFrameReady` copies into its own reused pending buffer (decode thread).
4. A 33 ms `DispatcherTimer` in `MainViewModel` calls `RenderPendingFrame()` which writes into a `WriteableBitmap` bound to the views. Zero allocation at steady state.

### Recording (important — do not regress this)

Recording must **never** open a second socket on the RTP ports — the receiver already owns them. `RecordingService`/`RecordingSession` taps `RtpStreamReceiver.NalUnitReceived` (every reassembled Annex-B NAL, fresh array each) and pipes the elementary stream into `ffmpeg.exe -f h264 -i pipe:0 -c:v copy` for an MP4 remux. It waits for an SPS NAL before writing (the device re-sends SPS/PPS every keyframe via `config-interval=-1`). Output goes to `My Videos\RGDSCapture\`.

### Multi-input muxing (combined recording, instant replay)

`FfmpegPipeMuxer` runs one ffmpeg with N inputs over **Windows named pipes** (`\\.\pipe\…`) because stdin can only carry one stream. Hard-won rules, all verified against the shipped ffmpeg:

- **`WaitForPipeDrain()` before disposing a pipe server** — disposal discards unread bytes and silently truncates the recording tail.
- **Force CFR timestamps with `-bsf:v setts=ts=N/(30*TB)`** — the raw h264 demuxer trusts SPS VUI timing over `-framerate`, which can corrupt track timing. All MP4 outputs use `FfmpegArgs.CfrSetts`.
- **`setts` overrides `-itsoffset`**, so video track-alignment offsets are folded into the setts expression; only the audio input uses `-itsoffset`.
- MP4 track labels are `handler_name` metadata (`title` only works for MOV).
- Each input gets its own queue + writer task; bulk feeds (replay save) must run one task per input or ffmpeg's interleaving stalls can deadlock.

`CombinedRecordingSession` (screens + Line-In in one MP4) measures wall-clock first-SPS times per stream, then launches ffmpeg with computed offsets; audio before the video base time is trimmed to the sample. Audio records via an independent `AudioRecordingTap` at unity gain (works whether or not monitoring runs). `ReplayBuffer` (one per screen, always armed while receivers run) keeps a rolling timestamped NAL window, and `AudioReplayBuffer` keeps the matching PCM window (fed by a tap started on connect); `ReplayService.SaveAsync` trims to the last N seconds starting at an SPS and remuxes video + audio into one MP4. Known limitation: a mid-recording stream freeze compresses that video track's CFR timeline relative to audio.

### Credentials, auto-reconnect, quality, stats

- **Saved credentials**: opt-in checkbox in `ConnectDialog`; the password is DPAPI-encrypted by `Core/CredentialStore` (raw crypt32 P/Invoke — deliberately no extra NuGet package) and stored in settings.json. Cleared automatically when the device rejects them (`SshService.LastFailureWasAuth`).
- **Auto-reconnect**: on `ConnectionLost`, `MainViewModel` retries with 3/6/12/24/30 s backoff using the session's in-memory credentials. Receivers are kept running between attempts (the device may still be streaming), and a successful reconnect calls `NotifyManualRestart()` on both trackers so the pipeline restart isn't misread as a freeze.
- **Quality presets** (Streams → Quality): substitute `{BPS}` in the pipeline template (1/2/4 Mbps). GOP stays fixed at 10 — recovery and recording logic depend on the ~333 ms keyframe cadence.
- **Network stats** (View → Show Stream Stats): the receiver counts packets/lost/bytes (sequence-gap based; gaps ≥ 200 are treated as a device pipeline restart, not loss). `MainViewModel.UpdateStats` computes 1 s deltas; an empty `ScreenViewModel.StatsText` hides the overlay.

### SSH.NET hazard (do not regress)

Never dispose an `SshCommand` that did not complete (timeout or dropped connection) — SSH.NET's socket thread races the disposed object and throws `ObjectDisposedException` on a background thread. `RunCommandAsync` only disposes completed commands and swallows command failures into status messages. Similarly, `MainWindow.OnClosingAsync` must `await Task.Yield()` before calling `Close()` — closing from inside the `Closing` event throws.

### Stream health / auto-recovery

`StreamHealthTracker` (one per screen, ticked at 1 Hz by `MainViewModel`) implements: Waiting → Live → Frozen (no frames ≥ 5 s) → auto-restart via SSH (max 3 retries) with a **10-second grace window** after each restart. The grace window is essential — without it all retries burn in seconds because frames can't arrive until the device pipeline respawns. Retries reset once frames flow again. Manual restarts call `NotifyManualRestart()`.

### Threading rules

- Receive/decode happens on a dedicated long-running task per receiver; UI work happens via `DispatcherTimer`s.
- `LogViewModel.Append` and `MainViewModel` status updates marshal to the dispatcher themselves — safe to call from any thread.
- All SSH commands go through `SshService.RunCommandAsync` (serialized by a semaphore, wrapped in `Task.Run`) — **nothing in SshService may block the UI thread**.
- `FrameReady` hands out a reused buffer: handlers must copy synchronously and not keep the reference.

### Views

- `MainWindow.xaml` is a thin shell (menu, toolbars as UserControls, status bar, video grid). Its code-behind only handles: credential/confirm dialogs (via delegates the VM exposes), layout grid spans (`ApplyLayout`), fullscreen window creation, the Space shortcut, and async-safe close sequencing.
- `ScreenView` is the single per-screen control reused for both screens and all four layouts (layouts are just row/column spans on a 2×2 grid).
- Fullscreen (`FullScreenWindow`) binds to the **same** `WriteableBitmap` as the main view — no extra copies.
- Keyboard shortcuts are `Window.InputBindings` (F2/F3 fullscreen, F5/F6/F7 restarts, F8 log, F12 screenshot); Space is handled in `PreviewKeyDown` so typing in text boxes isn't hijacked.

### Theming

`Themes/Dark.xaml` and `Light.xaml` contain only `SolidColorBrush` resources with identical key sets. `Themes/Controls.xaml` contains every control style/template and references colors via `DynamicResource`, so `ThemeService.Apply` (which swaps merged dictionary **index 0** — Controls.xaml is index 1) restyles live. When adding a color, add it to **both** theme files. Menu styling uses role-based `MenuItem` templates (TopLevelHeader / SubmenuHeader / SubmenuItem) — never hardcode menu foregrounds.

## Settings & Paths

`AppSettings` (theme, layout, device IP/port/username, volume, audio device **names**) persists to `%APPDATA%\RGDSCapture\settings.json` — never to the install directory, which is unwritable under Program Files. Audio devices are matched by name on restore because indices shift. Recordings → `My Videos\RGDSCapture`, screenshots → `My Pictures\RGDSCapture` (see `AppPaths`).

## Packaging Constraints

`RGDSCaptureSetup.iss` copies the publish output with wildcards (`*.dll`, `Themes\*.xaml`, the ff*.exe tools). Keep the exe name `RGDSCapture.exe`, keep FFmpeg DLLs/exes as `Content` items in the csproj, and keep theme XAML in `Themes\`. FFmpeg is the LGPL shared build — dynamic linking only; do not switch to static linking or GPL components (license obligation, see DEPENDENCIES.md).

## Dependencies

| Package | Purpose |
|---------|---------|
| `FFmpeg.AutoGen` 6.x | P/Invoke bindings to the shipped FFmpeg 6 DLLs (H.264 decode, swscale) |
| `SSH.NET` 2024.x | Device control: pipeline start/stop, power commands |
| `NAudio` 2.x | Line-In capture/playback (48 kHz 16-bit stereo passthrough, drift-corrected ring buffer) |

`AllowUnsafeBlocks` is enabled solely for FFmpeg interop in `RtpStreamReceiver`.
