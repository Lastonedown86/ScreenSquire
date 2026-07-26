using System.Text.Json;

namespace PiSignage.Signage;

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class RegionRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public sealed class AppSettings
{
    public Dictionary<string, WindowPlacement> Windows { get; set; } = new();
    public string? LastDeviceHostname { get; set; }
    public string? SignageTarget { get; set; }
    public Dictionary<string, RegionRect> Regions { get; set; } = new();
    public int TimerMinutes { get; set; } = 25;
    public List<int> TimerPresets { get; set; } = new() { 30, 45, 50 };
    // Checked TVs in the Tournament Signage window (hostnames). Replaces the
    // single SignageTarget, which is kept for migration of old settings files.
    public List<string> SignageTargets { get; set; } = new();
    // Stable IDs are authoritative. SignageTargets remains only for migrating
    // the builder's existing hostname-based local settings.
    public List<string> SignageDeviceIds { get; set; } = new();
    // Capture boards shown in the board picker (display names as the client typed them).
    public List<string> Boards { get; set; } = new() { "pairings", "standings" };
    // Saved YouTube links for the bookmarks window; queue plays in list order.
    public List<YouTubeBookmark> YouTubeBookmarks { get; set; } = new();
    // Last volume set from the bookmarks window; sent with each play.
    public int YouTubeVolume { get; set; } = 100;
}

public sealed class YouTubeBookmark
{
    public string VideoId { get; set; } = "";
    public string Url { get; set; } = "";
    // Fetched via oEmbed at add time; null when the fetch failed (offline etc.)
    public string? Title { get; set; }
    public string? AuthorName { get; set; }
    public string? ThumbnailUrl { get; set; }
}

// Same shape as DeviceStore: JSON in %AppData%\PiSignage, corrupt -> defaults,
// atomic temp+move write.
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    readonly string _path;

    public SettingsStore(string path) => _path = path;
    public SettingsStore() : this(DefaultPath()) { }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiSignage");
        return Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
            // migrate: old files had one target; carry it into the checked-TVs list
            if (s.SignageTargets.Count == 0 && !string.IsNullOrWhiteSpace(s.SignageTarget))
                s.SignageTargets.Add(s.SignageTarget!);
            // the two default boards are permanent
            if (!s.Boards.Contains("standings")) s.Boards.Insert(0, "standings");
            if (!s.Boards.Contains("pairings")) s.Boards.Insert(0, "pairings");
            return s;
        }
        catch { return new(); }   // corrupt/unreadable -> defaults, never crash
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Opts));
        File.Move(tmp, _path, overwrite: true);
    }
}
