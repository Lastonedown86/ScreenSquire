namespace PiSignage.Signage;

public sealed class SavedDevice : System.ComponentModel.INotifyPropertyChanged
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 8080;

    // UI-only reachability flag (null = not probed yet); never persisted
    bool? _online;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? Online
    {
        get => _online;
        set { _online = value; PropertyChanged?.Invoke(this, new(nameof(Online))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
