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
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
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
