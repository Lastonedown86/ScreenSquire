namespace PiSignage.Signage;

// Parses Spotify links/URIs into (type, id) for the kiosk's Embed player.
// Exact host match only (open.spotify.com.evil.example is rejected). IDs are
// exactly 22 base62 chars — note: NO '_' or '-', unlike YouTube ids.
public static class SpotifyUrl
{
    const int MaxUrlLength = 4096;

    static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "track", "album", "playlist", "episode", "show", "artist" };

    public static (string Type, string Id)? TryParse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxUrlLength) return null;
        input = input.Trim();

        // spotify:track:<id> URI form
        if (input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var p = input.Split(':');
            return p.Length == 3 && Types.Contains(p[1].ToLowerInvariant()) && IsId(p[2])
                ? (p[1].ToLowerInvariant(), p[2]) : null;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var i = 0;
        // locale prefix like /intl-de/track/<id>
        if (parts.Length > 0 && parts[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase)) i = 1;
        if (parts.Length != i + 2) return null;
        var type = parts[i].ToLowerInvariant();
        var id = parts[i + 1];
        return Types.Contains(type) && IsId(id) ? (type, id) : null;
    }

    static bool IsId(string value)
    {
        if (value.Length != 22) return false;
        foreach (var ch in value)
            if (!char.IsAsciiLetterOrDigit(ch)) return false;
        return true;
    }

    public static string CanonicalUri(string type, string id) => $"spotify:{type}:{id}";
    public static string OpenUrl(string type, string id) => $"https://open.spotify.com/{type}/{id}";
    public static string EmbedUrl(string type, string id) => $"https://open.spotify.com/embed/{type}/{id}";
}
