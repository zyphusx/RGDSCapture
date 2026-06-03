# RGDSCapture

RGDSCapture is an open-source Windows application that acts as a software capture card for the Anbernic RG Dual Screen (RG DS) handheld.

The application connects to the device over SSH, launches RTP streaming services, receives video and audio streams, and displays them on a Windows PC in a native desktop interface.

## Features

* Dual-screen video viewing
* Low-latency RTP streaming
* Audio streaming and playback
* SSH-based device control
* Light and Dark themes
* Native Windows desktop application
* Open-source development

## Requirements

### Device

* Anbernic RG Dual Screen (RG DS)
* Compatible Linux firmware
* Network connectivity between device and PC

### PC

* Windows 10 or Windows 11
* .NET 8 Runtime

## Building

Clone the repository:

git clone https://github.com/zyphusx/RGDSCapture.git

Open the solution in Visual Studio 2022 or later and build in Release mode.

## License

RGDSCapture is licensed under the MIT License.

Third-party components are licensed separately. See:

* DEPENDENCIES.md
* ThirdPartyLicenses/

## Disclaimer

RGDSCapture is an independent community project and is not affiliated with, endorsed by, or supported by Anbernic.
