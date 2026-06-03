# Dependencies

RGDSCapture is built on the following open-source libraries and tools. Each is included automatically via NuGet during the build process, except for FFmpeg which requires manual placement of native binaries.

---

## FFmpeg

| | |
|---|---|
| **Purpose** | H.264 video decoding, MP4 remuxing for recording |
| **Website** | https://ffmpeg.org |
| **License** | LGPL 2.1 (shared/dynamic build) |
| **Used as** | Native DLLs (`avcodec`, `avformat`, `avutil`, etc.) |
| **Notes** | Must be placed manually in the application directory. Use the **shared** build so the LGPL terms are satisfied by dynamic linking. See README for download instructions. |

---

## FFmpeg.AutoGen

| | |
|---|---|
| **Purpose** | Auto-generated .NET P/Invoke bindings for the FFmpeg native libraries |
| **Repository** | https://github.com/Ruslan-B/FFmpeg.AutoGen |
| **License** | MIT |
| **NuGet** | `FFmpeg.AutoGen` |
| **Version** | 6.x |

---

## SSH.NET

| | |
|---|---|
| **Purpose** | SSH connection and command execution on the RG DS device |
| **Repository** | https://github.com/sshnet/SSH.NET |
| **License** | MIT |
| **NuGet** | `SSH.NET` |
| **Version** | 2024.x |
| **Notes** | Used to connect to the device, start and stop GStreamer pipelines, and send power commands (shutdown/reboot). |

---

## NAudio

| | |
|---|---|
| **Purpose** | Audio device enumeration, Line-In capture, and audio playback |
| **Repository** | https://github.com/naudio/NAudio |
| **License** | MIT |
| **NuGet** | `NAudio` |
| **Version** | 2.x |
| **Notes** | Handles real-time 48 kHz 16-bit PCM passthrough from Line-In to speakers with drift-corrected ring buffer. |

---

## BouncyCastle.Cryptography

| | |
|---|---|
| **Purpose** | Cryptographic primitives required by SSH.NET for key exchange and encryption |
| **Repository** | https://github.com/bcgit/bc-csharp |
| **License** | MIT |
| **NuGet** | `BouncyCastle.Cryptography` |
| **Notes** | Pulled in automatically as a transitive dependency of SSH.NET. No direct usage in application code. |

---

## Notes

- All NuGet packages are restored automatically by `dotnet restore` or by Visual Studio on first build.
- FFmpeg native DLLs must be placed manually — see the [README](README.md#step-2--add-ffmpeg-binaries) for exact steps.
- Full license texts for FFmpeg (LGPL) and MIT-licensed packages are in the [THIRDPARTYLICENSES](THIRDPARTYLICENSES/) folder.
- All trademarks and product names remain the property of their respective owners.
