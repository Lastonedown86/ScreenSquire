namespace PiSignage.Signage;

public sealed class SavedDevice : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Ip { get; set; } = "";

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
