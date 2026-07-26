using System.Text.Json.Serialization;

namespace PiSignage.Control;

// These mirror the agent's API (agent/main.py). Property names must match
// the JSON keys exactly, hence the attributes.

public class StatusInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("paired")] public bool Paired { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("screens_connected")] public int ScreensConnected { get; set; }
    [JsonPropertyName("playlist_items")] public int PlaylistItems { get; set; }
    [JsonPropertyName("playlist_enabled")] public bool PlaylistEnabled { get; set; }
    [JsonPropertyName("override_active")] public bool OverrideActive { get; set; }
    [JsonPropertyName("now_showing")] public NowShowing? NowShowing { get; set; }
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("youtube_state")] public YouTubeState? YouTubeState { get; set; }
    [JsonPropertyName("spotify_state")] public SpotifyState? SpotifyState { get; set; }
}

// Last player state a kiosk reported: playing | paused | ended | error.
public class YouTubeState
{
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("videoId")] public string? VideoId { get; set; }
}

// Last Spotify embed state a kiosk reported: playing | paused | ended.
public class SpotifyState
{
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("uri")] public string? Uri { get; set; }
}

public class NowShowing
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("src")] public string? Src { get; set; }
}

public class PlaylistItem : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "image"; // image | video | url

    // notifies so rows refresh when a media rename rewrites sources in place
    string _source = "";
    [JsonPropertyName("source")]
    public string Source
    {
        get => _source;
        set
        {
            _source = value;
            PropertyChanged?.Invoke(this, new(nameof(Source)));
            PropertyChanged?.Invoke(this, new(nameof(Display)));
        }
    }
    [JsonPropertyName("duration")] public int Duration { get; set; } = 10;
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? Source : Name!;

    // UI-only: thumbnail arrives async after the list renders
    System.Windows.Media.ImageSource? _thumb;
    [JsonIgnore]
    public System.Windows.Media.ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); }
    }
    [JsonIgnore] public string Glyph => Type == "video" ? "🎬" : Type == "url" ? "🌐" : "🖼";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class Playlist
{
    [JsonPropertyName("items")] public List<PlaylistItem> Items { get; set; } = new();
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class MediaFile : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }

    [JsonIgnore]
    public string SizeText => Bytes >= 1_048_576
        ? $"{Bytes / 1_048_576.0:0.0} MB"
        : $"{Bytes / 1024.0:0} KB";

    // UI-only: thumbnail arrives async after the list renders
    System.Windows.Media.ImageSource? _thumb;
    [JsonIgnore]
    public System.Windows.Media.ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); }
    }
    [JsonIgnore] public string Glyph => Type == "video" ? "🎬" : "🖼";

    // UI-only: multi-select checkbox state
    bool _isChecked;
    [JsonIgnore]
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class MediaListResponse
{
    [JsonPropertyName("files")] public List<MediaFile> Files { get; set; } = new();
}

public class ShowNowRequest
{
    [JsonPropertyName("type")] public string Type { get; set; } = "url";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    // youtube only: starting volume 0-100 (agents before 2026.07.26.3 ignore it)
    [JsonPropertyName("volume")] public int? Volume { get; set; }
}

public class DiscoveredDevice
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public override string ToString() => $"{Name} ({Address}:{Port})";
}

public class KioskState
{
    [JsonPropertyName("running")] public bool Running { get; set; }
}

public class KioskResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("running")] public bool? Running { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public record RemoteDesktopSession(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password);
