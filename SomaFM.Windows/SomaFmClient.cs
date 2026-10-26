using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SomaFM.Windows;

public sealed class SomaFmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex ChannelIdPattern = new("^[a-z0-9][a-z0-9-]{0,47}$", RegexOptions.Compiled);
    private static readonly Regex StreamHostPattern = new("^ice[0-9]+\\.somafm\\.com$", RegexOptions.Compiled);
    private static readonly Regex BitratePattern = new("(?:-|/)([0-9]{2,3})-(?:mp3|aacp?|ogg|opus)$|([0-9]{2,3})\\.(?:mp3|aacp?|ogg|opus)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImagePathPattern = new("^/logos/120/[A-Za-z0-9-]+120\\.(png|jpe?g|gif)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync("https://api.somafm.com/channels.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The SomaFM channel catalog was not in the expected format.");

        var stations = new List<Station>();
        foreach (var channel in channels.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetString(channel, "id");
            if (!ChannelIdPattern.IsMatch(id) || !channel.TryGetProperty("playlists", out var playlists))
                continue;

            var playlistUrls = playlists.EnumerateArray()
                .Select(item => (Url: GetString(item, "url"), Quality: GetString(item, "quality")))
                .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out var url)
                    && url.Scheme == Uri.UriSchemeHttps && url.Host == "api.somafm.com")
                .OrderByDescending(item => item.Quality.Equals("highest", StringComparison.OrdinalIgnoreCase));
            var streamUrl = await FindStreamAsync(id, playlistUrls, cancellationToken);
            if (streamUrl is null)
                continue;

            var title = Clean(GetString(channel, "title"), 100);
            stations.Add(new Station(id, title.Length > 0 ? title : id,
                Clean(GetString(channel, "description"), 280), Clean(GetString(channel, "genre"), 60),
                ValidImageUrl(GetString(channel, "image")), ParseBitrate(streamUrl), streamUrl));
        }
        return stations.OrderBy(station => station.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<(string Title, string Artist)> GetTrackAsync(string stationId, CancellationToken cancellationToken = default)
    {
        if (!ChannelIdPattern.IsMatch(stationId))
            return ("", "");
        using var response = await Http.GetAsync($"https://somafm.com/songs/{stationId}.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var song = document.RootElement.TryGetProperty("songs", out var songs) && songs.GetArrayLength() > 0
            ? songs[0]
            : default;
        return (Clean(GetString(song, "title"), 160), Clean(GetString(song, "artist"), 120));
    }

    private static async Task<string?> FindStreamAsync(string id,
        IEnumerable<(string Url, string Quality)> playlists, CancellationToken cancellationToken)
    {
        foreach (var playlist in playlists)
        {
            try
            {
                var text = await Http.GetStringAsync(playlist.Url, cancellationToken);
                var streamUrl = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line.Split('=', 2).Last().Trim())
                    .Where(url => IsValidStreamUrl(url, id))
                    .OrderByDescending(ParseBitrate)
                    .FirstOrDefault();
                if (streamUrl is not null)
                    return streamUrl;
            }
            catch (HttpRequestException)
            {
            }
        }
        return null;
    }

    private static bool IsValidStreamUrl(string value, string id)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !StreamHostPattern.IsMatch(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        return Regex.IsMatch(uri.AbsolutePath, $"^/{Regex.Escape(id)}(?:-[a-z0-9]+)+$", RegexOptions.IgnoreCase);
    }

    private static int ParseBitrate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return 0;
        var match = BitratePattern.Match(uri.AbsolutePath);
        return int.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, out var rate) ? rate : 0;
    }

    private static string ValidImageUrl(string value)
    {
        if (value.Length > 300 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.somafm.com" || uri.Port != 443
            || !ImagePathPattern.IsMatch(uri.AbsolutePath))
            return "";
        return uri.AbsoluteUri;
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";

    private static string Clean(string value, int limit) =>
        new string(value.Where(character => !char.IsControl(character)).Take(limit).ToArray()).Trim();
}