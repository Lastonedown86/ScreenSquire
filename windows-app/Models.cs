using System.Text.Json.Serialization;

namespace PiSignage.Control;

// These mirror the agent's API (agent/main.py). Property names must match
// the JSON keys exactly, hence the attributes.

public class StatusInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("screens_connected")] public int ScreensConnected { get; set; }
    [JsonPropertyName("playlist_items")] public int PlaylistItems { get; set; }
    [JsonPropertyName("playlist_enabled")] public bool PlaylistEnabled { get; set; }
    [JsonPropertyName("override_active")] public bool OverrideActive { get; set; }
    [JsonPropertyName("now_showing")] public NowShowing? NowShowing { get; set; }
}

public class NowShowing
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("src")] public string? Src { get; set; }
}

public class PlaylistItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "image"; // image | video | url
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("duration")] public int Duration { get; set; } = 10;
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? Source : Name!;
}

public class Playlist
{
    [JsonPropertyName("items")] public List<PlaylistItem> Items { get; set; } = new();
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class MediaFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }

    [JsonIgnore]
    public string SizeText => Bytes >= 1_048_576
        ? $"{Bytes / 1_048_576.0:0.0} MB"
        : $"{Bytes / 1024.0:0} KB";
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
}

public class DiscoveredDevice
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 8080;
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
