# Changelog

All notable changes to RGDSCapture are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

How to maintain this file: add entries under **[Unreleased]** as you work
(one bullet per user-visible change, grouped under Added / Changed / Fixed /
Removed). When cutting a release, rename **[Unreleased]** to the new version
number with the date, and start a fresh empty **[Unreleased]** section above it.

## [Unreleased]

## [2.2.0] - 2026-06-10

### Changed
- Updated to .NET 10 (LTS, supported through November 2028) — .NET 8 leaves
  support in November 2026. The installer now checks for the .NET 10 Desktop
  Runtime.
- Combined recording now produces a vertically stacked composite — one video
  track with both screens visible (top over bottom, like the DS) — so the
  file plays correctly in any player. Previously it wrote two separate video
  tracks, which most players can't display together. Instant replay still
  uses separate lossless tracks.

### Fixed
- Combined recording audio could start out of sync by the audio device's
  startup latency.

## [2.1.0] - 2026-06-10

### Added
- **Multi-track combined recording** — record both screens *and* Line-In
  audio into a single MP4 with synced tracks. Video is copied without
  re-encoding; audio is encoded to AAC. Tracks are labeled
  "Top Screen" / "Bottom Screen".
- **Instant replay (F9)** — the last 15/30/60/120 seconds of both screens
  and Line-In audio are always buffered while connected; press F9 to save
  them retroactively as one MP4. No pre-arming needed. Buffer length is
  configurable under Streams → Instant Replay Length.
- **Remember credentials** — opt-in checkbox on the connect dialog. The SSH
  password is encrypted with Windows DPAPI (only your Windows account on
  this PC can read it). Clear it anytime via File → Forget Saved Credentials.
- **Auto-reconnect** — if the SSH connection drops (WiFi blip, device sleep),
  the app reconnects automatically with increasing backoff (5 attempts).
  Video keeps displaying between attempts when the device is still streaming.
- **Stream quality presets** — Streams → Quality: Low (1 Mbps), Medium
  (2 Mbps), High (4 Mbps) per screen. Switching while connected applies
  immediately.
- **Network stats overlay** — View → Show Stream Stats displays live
  bitrate and packet-loss percentage on each screen.

### Fixed
- Closing the app sometimes required clicking X twice and logged an
  exception.
- Random `ObjectDisposedException` crash-log entries from SSH.NET when a
  command timed out or the connection dropped.
- Recordings now force exact 30 fps timestamps, protecting against encoders
  that declare misleading timing in the H.264 stream.

## [2.0.0] - 2026-06-10

### Changed
- **Complete architectural rewrite ("the rebase").** The app is now MVVM:
  backend services (`Services/`), view-models (`ViewModels/`), thin views
  (`Views/`), and a proper theme system (colors in `Themes/Dark.xaml` /
  `Light.xaml`, all control styles shared in `Themes/Controls.xaml`).
- Settings now persist to `%APPDATA%\RGDSCapture\settings.json` (theme,
  layout, device IP/port/username, volume, audio devices) — previously the
  theme was written to the install folder, which silently failed under
  Program Files.
- Audio devices are remembered by name instead of index, so they restore
  correctly when Windows reorders devices.
- Crash diagnostics now go to the in-app event log and
  `%APPDATA%\RGDSCapture\crash.log`.

### Added
- Per-screen FPS readout next to the stream health badge.
- F2 / F3 fullscreen shortcuts documented; F8 toggles the event log.

### Fixed
- **Recording now works.** It previously launched a second ffmpeg that tried
  to bind the same UDP port the viewer already owned (impossible), and
  re-encoded with libx264. Recording now taps the already-received H.264
  stream in-process and remuxes to MP4 with zero re-encoding.
- Dark theme menus rendered black text on a dark background.
- Stream auto-recovery burned all 3 retries within seconds because there was
  no grace period after a restart; each attempt now gets a 10-second window.
- SSH commands (restart, disconnect, power) blocked the UI thread for up to
  8 seconds; everything is now asynchronous.
- Packet loss corrupted H.264 fragments fed to the decoder; RTP sequence
  numbers are now tracked and damaged fragments dropped.
- VU meters now fill vertically as level meters should.
- The speedrun timer no longer ticks its display timer while paused.
- A failed audio start no longer leaks a playing output device.

## [1.7.1] and earlier

Pre-rewrite releases — see the git history.
