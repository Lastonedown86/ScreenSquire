using System.Text.Json;

namespace PiSignage.Signage;

public sealed class DeviceStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    readonly string _path;

    public DeviceStore(string path) => _path = path;
    public DeviceStore() : this(DefaultPath()) { }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiSignage");
        return Path.Combine(dir, "devices.json");
    }

    public List<SavedDevice> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<SavedDevice>>(File.ReadAllText(_path)) ?? new();
        }
        catch { return new(); }   // corrupt/unreadable -> empty, never crash
    }

    public void Save(IEnumerable<SavedDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(devices, Opts));
        File.Move(tmp, _path, overwrite: true);
    }

    // Pure: match by hostname (case-insensitive); update Ip, keep existing Name
    // unless a non-empty new Name is supplied; otherwise append.
    public static List<SavedDevice> Upsert(List<SavedDevice> list, SavedDevice dev)
    {
        var existing = list.FirstOrDefault(d =>
            string.Equals(d.Hostname, dev.Hostname, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            list.Add(new SavedDevice
            {
                Name = string.IsNullOrWhiteSpace(dev.Name) ? dev.Hostname : dev.Name,
                Hostname = dev.Hostname,
                Ip = dev.Ip,
            });
        }
        else
        {
            existing.Ip = dev.Ip;
            if (!string.IsNullOrWhiteSpace(dev.Name)) existing.Name = dev.Name;
        }
        return list;
    }
}
