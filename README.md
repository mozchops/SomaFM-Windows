# SomaFM for Windows

A compact, always-on-top Windows player bar for [SomaFM](https://somafm.com) internet radio, with station artwork, live track info and an audio-spectrum visualizer.

> **Unofficial.** This is an independent app. It is not made, endorsed or supported by SomaFM.

## Download

Get the latest version from the [Releases page](../../releases/latest) if you don't want to build it yourself. Two builds are provided; they are the same app.

| File | Size | What you need |
|---|---|---|
| **SomaFM-Bundle.exe** | about 63 MB | Nothing. Includes everything it needs. Recommended if you are unsure. |
| **SomaFM-Lite.exe** | under 1 MB | The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) must be installed. Windows does not include it by default. If it is missing, the app will not start. |

Both are 64-bit Windows 10/11 programs. There is no installer: save the file anywhere and run it.

Windows SmartScreen may warn about an unrecognised app, because the files are not code-signed. Choose **More info**, then **Run anyway** if you are happy to proceed.

## Using the app

- **Choose a station:** click the station name to open the picker. Type to filter, press Enter to play the top match, and right-click a station to favourite it. Each station shows its stream type, bitrate and your total listening time.
- **Play / pause:** right-click anywhere on the bar.
- **Move:** drag anywhere on the bar. It snaps to screen edges. Middle-click centres it horizontally.
- **Resize:** drag the left or right edge. Elements drop away as the bar narrows.
- **Visualizer:** left-click it to cycle colour palettes.
- **Playtime:** left-click it to switch between this session and all time.
- **Settings (⚙) or tray menu:** favourite-station sorting (A-Z or most listened; the full list is always A-Z), visualizer on/off, colour palette, track-change notifications, play last station on startup, and remember size and location.
- **`_`** hides the bar to the notification area; **`×`** exits.

Settings, favourites, volume, playtime and placement are stored in `%APPDATA%\SomaFM-Windows`.

## How it works

The app reads SomaFM's public station catalog, chooses the highest-quality MP3 stream each station offers, and decodes it with Windows Media Foundation. [NAudio](https://github.com/naudio/NAudio) plays the audio and feeds the visualizer. No separate audio engine is bundled.

## Build from source

Requires Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project .\SomaFM.Windows\SomaFM.Windows.csproj
```

To publish the two release builds:

```powershell
# Lite: needs the .NET 8 Desktop Runtime installed
dotnet publish .\SomaFM.Windows\SomaFM.Windows.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:SatelliteResourceLanguages=en -p:DebugType=none

# Bundle: includes the runtime
dotnet publish .\SomaFM.Windows\SomaFM.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=en -p:DebugType=none
```

## Streams, content and fair use

This app plays SomaFM's public internet radio streams. All audio, station names, artwork and track information belong to SomaFM and the artists concerned.

**The author has no control over, and takes no responsibility for, the content of the streams.**

SomaFM is a listener-supported, non-commercial radio service. **SomaFM is likely to be very unhappy with anyone trying to make money from this app or from their streams.** The MIT license below does not give you any rights to SomaFM's streams, names, logos or content. Please support them at [somafm.com/support](https://somafm.com/support/).

## Disclaimer

The software is provided "as is", without warranty of any kind, and the author accepts no liability for any damage, loss or other issue arising from its use.

## License

[MIT](LICENSE)
