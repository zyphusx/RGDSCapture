# Changelog

All notable changes to RGDSCapture are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

How to maintain this file: add entries under **[Unreleased]** as you work
(one bullet per user-visible change, grouped under Added / Changed / Fixed /
Removed). When cutting a release, rename **[Unreleased]** to the new version
number with the date, and start a fresh empty **[Unreleased]** section above it.

## [Unreleased]

### Added
- **15 built-in themes** with a visual picker (View → Theme Picker…, or
  **Ctrl+T**). Twelve dark — Midnight, **Gengar**, Nocturne, Matrix, Ember,
  Crimson, Cyberpunk, Ocean, Forest, Amber, Sakura, Mono — and three light:
  Daylight, Parchment, Mint. A theme tints every surface, not just the accent,
  so the whole app takes on its colour. Presets are also listed under
  View → Theme, and apply instantly with no restart.
- **Custom accent colour.** The picker's RGB sliders (or a typed `#RRGGBB`)
  regenerate the entire palette around any colour you choose, on top of
  whichever preset is active. "Use Preset Accent" drops back to the
  preset's own colour.
- The left panel gains a theme button showing the current theme and accent.

### Changed
- **Themes are now generated rather than hand-written.** A theme is defined by
  three seeds — light/dark ramp, accent, and how strongly the accent hue tints
  the greys — and `PaletteBuilder` derives all 101 brushes from them. Adding a
  theme is one line in `ThemeCatalog` instead of a 200-line resource
  dictionary, and it is what makes an arbitrary user-picked accent possible.
  `Themes/Light.xaml` is gone (now generated); `Themes/Dark.xaml` remains only
  as the designer/startup fallback and as the documented key contract.
- Settings: `Theme` now stores a preset id and a new `CustomAccent` holds the
  accent override. Older files containing `"Dark"`/`"Light"` are mapped
  forward automatically, so existing settings keep working.
- **Redesigned interface — two-column layout.** The menu bar plus three
  stacked toolbars (~180 px of chrome above the picture) are gone. Controls
  now live in two collapsible columns either side of the video: a **device**
  column on the left (connection, streams, display, console power) and a
  **capture** column on the right (recording, instant replay, audio, run
  timer). Video is the hero and fills everything between them.
  - Collapse either column with **Ctrl+B** / **Ctrl+J**, the title-bar
    buttons, or View → Show Device/Capture Panel. The open/closed state is
    remembered between sessions.
  - The menu bar moves into a slim custom title bar that also shows the
    connection dot and the current device and address. Every menu item and
    every existing keyboard shortcut is unchanged.
  - Radio-style segmented controls replace the nested submenus for quality,
    layout, rotation, screen gap, scaling and replay length — the current
    value is now visible without opening a menu.
- **Darker, higher-contrast theme.** New surface ramp (`#0E0F12` →
  `#16181D` → `#1E2127`), a distinct `#4C8DFF` accent in place of the stock
  Windows blue, and semantic live/record colors. The light theme was rebuilt
  key-for-key to match.
- The event log drawer now overlays only the video stage, so opening it no
  longer resizes the picture.
- Video panels get rounded corners, glass status chips, and a fullscreen
  button that appears on hover instead of sitting permanently over the frame.

### Fixed
- Stream health badges no longer render a doubled status dot (the badge text
  already carries its own `●`/`○` glyph).
- Replaced button glyphs that fell back to empty boxes on Windows because
  they resolved to Segoe UI Emoji rather than the UI font.

### Added
- **RG353V device support** — connect to a single-screen Anbernic RG353V
  (stock firmware) in addition to the RG DS. Pick the device type in the
  Connection panel before connecting. Stock RG353V firmware has no GStreamer, so
  video is captured via ffmpeg's `fbdev` input and encoded with software
  x264 instead of the DS's GStreamer/MPP pipeline. Dual-screen-only features
  (Combined Recording, Instant Replay, GIF export, alternate layouts) are
  disabled for this device for now — full single-screen recording/screenshot
  support is in.
- **Hybrid layout** — one screen large, the other small in the corner
  (melonDS-style), alongside the existing four layouts.
- **Screen rotation** (View → Rotation: 0° / 90° / 180° / 270°) for games
  played with the console held sideways (Brain Age, Hotel Dusk, …).
- **Swap Screens** — exchange which screen takes the top/left/large position
  in any layout.
- **Screen Gap** presets (None / Small / Normal / Wide) for the spacing
  around each screen.
- **Video Filter** toggle — Sharp (pixel-perfect, default) or Smooth scaling.
- **Recording indicator** — a red ● REC badge with elapsed time appears on
  screens that are being recorded (per-screen or combined).
- **GIF export (F10)** — save the last 10 seconds of both screens as a
  shareable animated GIF (stacked, DS-native 256 px wide, ~15 fps), saved to
  `My Pictures\RGDSCapture`.

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
