# SomaFM for Windows

An independent Windows client for SomaFM internet radio. It runs as a compact always-on-top player bar with station artwork, live track metadata, and an audio-spectrum visualizer.

Drag the SomaFM label to reposition the bar. Releasing it near the top or either side of the current screen snaps it to that edge. The close control hides the bar to the notification area; use the tray menu to restore or exit.

The station picker loads SomaFM's current station catalog, including artwork. LibVLC decodes the stream, and NAudio outputs the audio while the visualizer analyzes the decoded samples.

Build and run on Windows with the .NET 8 SDK:

```powershell
dotnet run --project .\SomaFM.Windows\SomaFM.Windows.csproj
```
