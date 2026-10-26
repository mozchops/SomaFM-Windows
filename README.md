# SomaFM for Windows

An independent Windows client for SomaFM internet radio. It runs as a compact always-on-top player bar with station artwork, live track metadata, and an audio-spectrum visualizer.

Drag anywhere on the bar to reposition it; releasing it near the top or either side of the screen snaps it to that edge. Middle-click centres it horizontally, right-click toggles play/pause, and the edges resize it (elements drop away as it narrows). Left-click the visualizer to cycle colour palettes, and left-click the playtime to switch between session and all-time totals. The `×` button exits the app; `_` hides the bar to the notification area.

Click the station name to open the station picker: type to filter, Enter plays the top match, right-click a station to favourite it. Each station shows its stream type and bitrate (for example `MP3 128k`). The best-quality MP3 stream is used, since Windows Media Foundation decodes it at full quality. Windows decodes the stream and NAudio outputs the audio while the visualizer analyzes the decoded samples, so no separate audio engine is bundled.

Use Settings in the player bar or tray menu to sort favourite stations alphabetically or by listening time, toggle the visualizer, choose a colour palette, notify on track change, play the last station at startup, and remember the last size and location. Preferences, volume and placement are saved between launches.

Build and run on Windows with the .NET 8 SDK:

```powershell
dotnet run --project .\SomaFM.Windows\SomaFM.Windows.csproj
```
