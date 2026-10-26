namespace SomaFM.Windows;

public sealed record Station(
    string Id,
    string Title,
    string Description,
    string Genre,
    int Bitrate,
    string StreamUrl);