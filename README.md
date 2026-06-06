<div align="center">

# RGDSCapture

**A software capture card for the Anbernic RG Dual Screen**

Stream, record, and monitor both screens of your RG DS on Windows — no hardware capture card required.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)]()


</div>

---

## What It Does

RGDSCapture connects to your Anbernic RG DS over SSH, starts GStreamer H.264 video pipelines on the device, and receives both screens as low-latency RTP streams on your Windows PC. Audio is monitored directly from the console's headphone jack via your PC's Line-In input, keeping the network path video-only for maximum stability.

<div align="center">

```
RG DS                              Windows PC
──────                             ──────────
Top Screen ──── RTP/UDP:5000 ────► Screen 1 Display
Bot Screen ──── RTP/UDP:5001 ────► Screen 2 Display
Audio Out  ──── 3.5mm Cable  ────► Line-In → Speakers
```

</div>

## Features

### Video
- Dual-screen H.264 video at up to 30 fps per screen
- Four layout modes: Vertical Stack, Side by Side, Top Only, Bottom Only
- Live FPS counter per screen
- Stream health badges: **● LIVE** / **● FROZEN** / **● RECOVERING** / **○ WAITING**
- Auto-recovery: frozen streams automatically restart (up to 3 attempts)
- Manual restart per screen or all at once
- Error concealment — damaged macroblocks are interpolated rather than shown as corruption

### Audio
- Real-time Line-In passthrough via NAudio (3.5mm cable: console → PC Line-In)
- Input and output device selection
- Volume control with live L/R VU meters
- Drift-corrected ring buffer — no latency creep, no starve-clicks
- ~80 ms target latency

### Recording & Screenshots
- Record each screen independently to MP4 (H.264, zero re-encode)
- Files saved to `My Videos\RGDSCapture\` with timestamps
- Screenshot both screens to PNG with a single button or F12
- Files saved to `My Pictures\RGDSCapture\`

### Speedrun Timer
- Millisecond-precision timer overlay: **Start / Pause / Reset / Lap**
- Lap splits logged to the event log with timestamps
- Space bar toggles the timer (when no text field has focus)

### Console Control
- **Shutdown** — sends `poweroff` with confirmation dialog
- **Reboot** — sends `restart` with confirmation dialog
- Disconnect confirmation prevents accidental stream drops
- Exit confirmation if streams are running

### App
- Dark and Light themes, persisted between sessions
- Event log panel with timestamped entries
- Keyboard shortcuts (see below)

---

## Keyboard Shortcuts

| Key   | Action                       |
|-------|------------------------------|
| F5    | Restart all streams          |
| F6    | Restart top stream           |
| F7    | Restart bottom stream        |
| F12   | Screenshot both screens      |
| Space | Start / pause speedrun timer |

---

## Requirements

### RG DS Device
- Anbernic RG Dual Screen running the latest release of Anbernic's Linux FW 1.0
- Connected to the same local network as your PC (Wi-Fi)

### Windows PC
- Windows 10 or Windows 11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (x64) — the installer will prompt you if this is missing
- A hardware Line-In audio jack, or a USB audio adapter with Line-In input
- A 3.5mm stereo cable (console headphone out → PC Line-In)

> **FFmpeg is already included.** The release package and installer ship with the required FFmpeg LGPL binaries — you do not need to download anything separately.

---

## Installation

### Option A — Installer (Recommended)

1. Go to the [Releases](https://github.com/zyphusx/RGDSCapture/releases) page
2. Download **`RGDSCaptureSetup.exe`**
3. Run the installer and follow the prompts
4. Launch RGDSCapture from the Start Menu or Desktop

If .NET 8 is not already installed, Windows will prompt you to install it before the app launches. Click the link in the prompt and run the Microsoft installer, then launch RGDSCapture again.

---

### Option B — Portable ZIP

1. Go to the [Releases](https://github.com/zyphusx/RGDSCapture/releases) page
2. Download **`RGDSCapture-v0.1.0.zip`**
3. Extract to any folder (e.g. `C:\RGDSCapture\`)
4. Launch `RGDSCapture.exe`

No installation required. To uninstall, delete the folder.

---

### Option C — Build from Source

See [Building from Source](#building-from-source) below.

---

## First Launch

1. Launch RGDSCapture
2. Enter your RG DS **IP address** in the top bar
   - Find this in your device's Wi-Fi settings, or check your router's connected devices list
3. Leave Port as `22` unless your firmware uses a different SSH port
4. Click **Connect**
5. Enter your SSH username and password when prompted

   If unchanged from default, it should be:
   Username: root
   Password: root
   
6. Both screens should appear within a few seconds

---

## Audio Setup

1. Connect a **3.5mm stereo cable** from the RG DS headphone jack to your PC's **Line-In port**
   - Line-In is typically the **blue** port on desktop PCs
   - If your PC only has a headset combo jack, use a **USB audio adapter** with a Line-In input
2. In the RGDSCapture toolbar, open the **🔊 Line-In** dropdown and select your Line-In device
3. Open the **Out** dropdown and choose your speakers or headphones
4. Click **▶ Audio**
5. Adjust the volume slider — the L/R VU meters confirm audio is flowing

**If you hear nothing:**
- Confirm the cable is in Line-In (blue), not Mic-In (pink)
- Go to `Control Panel → Sound → Recording` — right-click Line-In and choose **Enable** if it is disabled
- In Line-In Properties → Levels, set the level to around 80

---

## Troubleshooting

**Screens show WAITING and never display video**
- Confirm the device is reachable: `ping <device-ip>` in a terminal
- Confirm GStreamer is installed: `ssh user@<ip> which gst-launch-1.0`
- Check that UDP ports **5000** and **5001** are allowed through Windows Firewall
  - `Windows Defender Firewall → Advanced Settings → Inbound Rules → New Rule`
  - Rule type: Port → UDP → 5000, 5001 → Allow

**Stream freezes or pixellates**
- Press **F5** or click **↺ All** to restart both streams
- The app will auto-recover up to 3 times before requiring manual intervention

**Audio is choppy**
- Go to `Control Panel → Sound → Recording → Line-In → Properties → Advanced`
- Uncheck **"Allow applications to take exclusive control of this device"**
- Set the default format to **48000 Hz, 16-bit**

**App won't start — missing runtime**
- Download and install the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

**SSH connection refused**
- Confirm SSH is enabled in your device's firmware settings
- Test manually: `ssh user@<device-ip>` in a terminal

---

## Building from Source

### Prerequisites

| Tool | Version | Download |
|------|---------|----------|
| Visual Studio 2022 | 17.8 or later | [visualstudio.microsoft.com](https://visualstudio.microsoft.com/) |
| .NET 8 SDK | 8.0 or later | [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
| Git | Any recent version | [git-scm.com](https://git-scm.com/) |

During Visual Studio installation, select the **".NET desktop development"** workload.

---

### Build Steps

**1. Clone the repository**
```bash
git clone https://github.com/zyphusx/RGDSCapture.git
cd RGDSCapture
```

**2. Restore NuGet packages**

Visual Studio does this automatically on first build. From the command line:
```bash
dotnet restore
```

**3. Build**

In Visual Studio — open `RGDSCapture.sln`, select **Release**, press `Ctrl+Shift+B`.

From the command line:
```bash
dotnet build -c Release
```

The FFmpeg LGPL DLLs are included in the repository and are copied to the output directory automatically by the project file. No manual DLL placement is needed.

**4. Run**
```bash
dotnet run --project RGDSCapture
```
Or launch `bin\Release\net8.0-windows\RGDSCapture.exe` directly.

---

### Building with VS Code

**1. Install extensions** (`Ctrl+Shift+X`):
- **C# Dev Kit** (Microsoft)
- **C#** (Microsoft)

**2. Open folder:** `File → Open Folder → select the RGDSCapture folder`

**3. Build and run** (integrated terminal, `` Ctrl+` ``):
```bash
dotnet restore
dotnet build -c Release
dotnet run
```

Or press **F5** to launch with the debugger (VS Code will offer to create a launch config automatically if one doesn't exist).

---

## License

RGDSCapture is released under the [MIT License](LICENSE).

FFmpeg is licensed under the LGPL 2.1 and is used as a dynamically linked shared library. FFmpeg binaries are distributed with permission under these terms. See [DEPENDENCIES.md](DEPENDENCIES.md) and [THIRDPARTYLICENSES/](THIRDPARTYLICENSES/) for full license texts.

Other third-party components are MIT licensed — see [DEPENDENCIES.md](DEPENDENCIES.md).

---

## Disclaimer

RGDSCapture is an independent community project. It is not affiliated with, endorsed by, or supported by Anbernic. All product names and trademarks are the property of their respective owners.
