using System.Text.Json.Serialization;

namespace SomaFM.Windows;

public sealed record Station(
    string Id,
    string Title,
    string Description,
    string Genre,
    string ImageUrl,
    int Bitrate,
    string StreamUrl)
{
    [JsonIgnore]
    public bool IsFavorite { get; set; }

    [JsonIgnore]
    public string PlayTime { get; set; } = "";

    [JsonIgnore]
    public string StreamInfo
    {
        get
        {
            var path = Uri.TryCreate(StreamUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "";
            var type = path.Contains("mp3", StringComparison.OrdinalIgnoreCase) ? "MP3"
                : path.Contains("aac", StringComparison.OrdinalIgnoreCase) ? "AAC"
                : path.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? "OGG" : "";
            return type.Length == 0 ? "" : Bitrate > 0 ? $"{type} {Bitrate}k" : type;
        }
    }
}