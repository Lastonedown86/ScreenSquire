namespace PiSignage.Signage;

// C# port of the agent's _normalize_url video-id extraction (agent/main.py).
// Must stay in lockstep with it: same host allowlist (exact match — no suffix
// matching, so youtube.com.evil.example is rejected), same URL forms, same
// 11-char id alphabet. Tests mirror agent/test_normalize_url.py case-for-case.
public static class YouTubeUrl
{
    const int MaxUrlLength = 4096;

    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
        { "youtube.com", "www.youtube.com", "m.youtube.com" };

    public static string? TryGetVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            return AsId(uri.AbsolutePath.TrimStart('/').Split('/')[0]);

        if (!Hosts.Contains(host)) return null;
        if (uri.AbsolutePath == "/watch")
        {
            string? found = null;
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                if (!pair.StartsWith("v=", StringComparison.Ordinal)) continue;
                if (found != null) return null;   // ambiguous: two v= params
                found = pair[2..];
            }
            return found == null ? null : AsId(Uri.UnescapeDataString(found));
        }
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length == 2 && parts[0] is "shorts" or "live" or "embed")
            return AsId(parts[1]);
        return null;
    }

    static string? AsId(string value)
    {
        if (value.Length != 11) return null;
        foreach (var ch in value)
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-') return null;
        return value;
    }

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";
}
