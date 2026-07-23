# Saved Devices List — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A persistent saved-devices dropdown in the control app: every Pi you connect to (or provision via the wizard) is remembered with an editable friendly name, selecting one reconnects, and it self-heals when a Pi's DHCP IP changes.

**Architecture:** `SavedDevice` + `DeviceStore` (JSON at `%APPDATA%\PiSignage\devices.json`) live in `signage-core` (net8.0, unit-tested). `MainWindow` binds a dropdown to the store, connects via a self-heal path (try IP → re-resolve by hostname over mDNS → update IP), and upserts on connect. `WifiSetupWindow` upserts the provisioned Pi on success; `MainWindow` reloads when the wizard closes.

**Tech Stack:** C# / .NET 8 (`signage-core` + WPF), System.Text.Json, xUnit.

## Global Constraints

- `SavedDevice` + `DeviceStore` live in `signage-core` (**net8.0, no WPF**), namespace `PiSignage.Signage`. WPF namespace `PiSignage.Control`.
- Persistence path: `%APPDATA%\PiSignage\devices.json`. `DeviceStore` takes an **injectable path** (tests pass a temp file).
- Atomic write (temp file + `File.Move(overwrite:true)`); corrupt/missing file → **empty list, never throw**.
- `Upsert` matches by **`Hostname` (case-insensitive)**: existing → update `Ip` (and `Name` only if a non-empty new name is given); else append. Pure function.
- The Pi's `/api/status` `name` **is its hostname** — used as the default friendly name and the match key for re-resolution.
- Solution file is **`PiSignage.slnx`** (not `.sln`).
- Branch: `feat/saved-devices`.

---

## File Structure

**Library (`signage-core/`):** `SavedDevice.cs`, `DeviceStore.cs`.
**Library tests:** `signage-core.Tests/DeviceStoreTests.cs`.
**WPF:** `TextPrompt.xaml` + `.cs` (tiny rename dialog); modify `MainWindow.xaml`, `MainWindow.xaml.cs`, `WifiSetupWindow.xaml.cs`.

---

## Task 1: signage-core SavedDevice + DeviceStore

**Files:**
- Create: `signage-core/SavedDevice.cs`, `signage-core/DeviceStore.cs`
- Test: `signage-core.Tests/DeviceStoreTests.cs`

**Interfaces:**
- Produces: `SavedDevice { string Name; string Hostname; string Ip; }`;
  `DeviceStore(string path)` with `List<SavedDevice> Load()`, `void Save(IEnumerable<SavedDevice>)`, static `List<SavedDevice> Upsert(List<SavedDevice> list, SavedDevice dev)`, static `string DefaultPath()`.

- [ ] **Step 1: Write the failing tests**

Create `signage-core.Tests/DeviceStoreTests.cs`:
```csharp
using PiSignage.Signage;
using Xunit;

public class DeviceStoreTests
{
    static string TempFile() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"dev-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var path = TempFile();
        try
        {
            var store = new DeviceStore(path);
            store.Save(new[] { new SavedDevice { Name = "Front TV", Hostname = "pisignage1", Ip = "192.168.0.58" } });
            var got = store.Load();
            Assert.Single(got);
            Assert.Equal("Front TV", got[0].Name);
            Assert.Equal("pisignage1", got[0].Hostname);
            Assert.Equal("192.168.0.58", got[0].Ip);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void MissingFileLoadsEmpty()
    {
        Assert.Empty(new DeviceStore(TempFile()).Load());
    }

    [Fact]
    public void CorruptFileLoadsEmpty()
    {
        var path = TempFile();
        try { System.IO.File.WriteAllText(path, "{not json"); Assert.Empty(new DeviceStore(path).Load()); }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void UpsertUpdatesIpKeepsEditedNameByHostname()
    {
        var list = new List<SavedDevice> { new() { Name = "Front TV", Hostname = "pisignage1", Ip = "192.168.0.58" } };
        // same hostname, new IP, no new name -> keep "Front TV", update IP
        list = DeviceStore.Upsert(list, new SavedDevice { Name = "", Hostname = "PISIGNAGE1", Ip = "192.168.0.99" });
        Assert.Single(list);
        Assert.Equal("Front TV", list[0].Name);
        Assert.Equal("192.168.0.99", list[0].Ip);
    }

    [Fact]
    public void UpsertAddsNewDevice()
    {
        var list = new List<SavedDevice> { new() { Name = "Front TV", Hostname = "pisignage1", Ip = "192.168.0.58" } };
        list = DeviceStore.Upsert(list, new SavedDevice { Name = "pisignage2", Hostname = "pisignage2", Ip = "192.168.0.71" });
        Assert.Equal(2, list.Count);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test signage-core.Tests --filter DeviceStoreTests
```
Expected: FAIL (types not found).

- [ ] **Step 3: Implement `SavedDevice.cs`**

```csharp
namespace PiSignage.Signage;

public sealed class SavedDevice
{
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Ip { get; set; } = "";
}
```

- [ ] **Step 4: Implement `DeviceStore.cs`**

```csharp
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
```

- [ ] **Step 5: Run to verify pass**

```bash
dotnet test signage-core.Tests --filter DeviceStoreTests
```
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add signage-core/SavedDevice.cs signage-core/DeviceStore.cs signage-core.Tests/DeviceStoreTests.cs
git commit -m "feat(core): SavedDevice + DeviceStore (persist/upsert by hostname) + tests"
```

---

## Task 2: MainWindow saved-devices dropdown + self-heal connect

**Files:**
- Create: `windows-app/TextPrompt.xaml` + `.cs`
- Modify: `windows-app/MainWindow.xaml`, `windows-app/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DeviceStore`, `SavedDevice` (core); existing `ApiClient(host,port)`, `MdnsDiscovery.ScanAsync`, `StatusInfo.Name`.

- [ ] **Step 1: Create the tiny rename dialog `TextPrompt.xaml`**

```xml
<Window x:Class="PiSignage.Control.TextPrompt"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Rename" Height="150" Width="340" WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize">
    <StackPanel Margin="14">
        <TextBlock x:Name="Prompt" Text="New name:" Margin="0,0,0,6"/>
        <TextBox x:Name="Input"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,14,0,0">
            <Button Content="OK" IsDefault="True" Width="70" Click="Ok_Click"/>
            <Button Content="Cancel" IsCancel="True" Width="70" Margin="8,0,0,0"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Create `TextPrompt.xaml.cs`**

```csharp
using System.Windows;

namespace PiSignage.Control;

public partial class TextPrompt : Window
{
    public string Value => Input.Text.Trim();

    public TextPrompt(string prompt, string initial)
    {
        InitializeComponent();
        Prompt.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.SelectAll(); Input.Focus(); };
    }

    void Ok_Click(object s, RoutedEventArgs e) => DialogResult = true;

    // Returns the entered value, or null if cancelled/empty.
    public static string? Ask(Window owner, string prompt, string initial)
    {
        var d = new TextPrompt(prompt, initial) { Owner = owner };
        return d.ShowDialog() == true && d.Value.Length > 0 ? d.Value : null;
    }
}
```

- [ ] **Step 3: Update the connect bar in `MainWindow.xaml`**

Replace the connect-bar `<DockPanel>...</DockPanel>` (the block inside the Row 0 Border) with:
```xml
            <DockPanel>
                <TextBlock Text="Device:" VerticalAlignment="Center" Margin="0,0,6,0" FontWeight="SemiBold"/>
                <Button Content="Tournament Signage" DockPanel.Dock="Right" Margin="8,0,0,0" Click="OpenSignage_Click"/>
                <Button Content="Add a Pi" DockPanel.Dock="Right" Margin="8,0,0,0" Click="AddPi_Click"/>
                <Button x:Name="BtnScan" Content="Scan network" DockPanel.Dock="Right" Click="BtnScan_Click"/>
                <Button x:Name="BtnForget" Content="Forget" DockPanel.Dock="Right" Margin="4,0,0,0" Click="BtnForget_Click"/>
                <Button x:Name="BtnRename" Content="Rename" DockPanel.Dock="Right" Margin="4,0,0,0" Click="BtnRename_Click"/>
                <Button x:Name="BtnConnect" Content="Connect" DockPanel.Dock="Right"
                        Style="{StaticResource PrimaryButton}" Click="BtnConnect_Click"/>
                <ComboBox x:Name="CmbAddress" IsEditable="True" MinWidth="260" Margin="0,0,8,0"
                          VerticalContentAlignment="Center" DisplayMemberPath="Name"
                          IsTextSearchEnabled="False"/>
            </DockPanel>
```

- [ ] **Step 4: Wire the store + dropdown in `MainWindow.xaml.cs`**

Add fields + load in the constructor. Add near the other fields:
```csharp
    private readonly PiSignage.Signage.DeviceStore _deviceStore = new();
    private System.Collections.Generic.List<PiSignage.Signage.SavedDevice> _devices = new();
```
At the end of the constructor body add:
```csharp
        ReloadDevices();
```
Add these methods to the class:
```csharp
    private void ReloadDevices()
    {
        _devices = _deviceStore.Load();
        var keep = CmbAddress.SelectedItem;
        CmbAddress.ItemsSource = _devices;
        if (keep != null && _devices.Contains(keep)) CmbAddress.SelectedItem = keep;
    }

    private void SaveDevices() => _deviceStore.Save(_devices);
```

- [ ] **Step 5: Replace `BtnConnect_Click` with device-aware connect + upsert**

Replace the existing `BtnConnect_Click` method with:
```csharp
    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is PiSignage.Signage.SavedDevice dev)
        {
            await ConnectToDeviceAsync(dev);
            return;
        }
        var addr = CmbAddress.Text.Trim();
        if (addr.Length == 0) return;
        int port = 8080;
        var parts = addr.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var p)) { addr = parts[0]; port = p; }

        var status = await ConnectHostAsync(addr, port);
        if (status != null && !string.IsNullOrWhiteSpace(status.Name))
        {
            _devices = PiSignage.Signage.DeviceStore.Upsert(_devices,
                new PiSignage.Signage.SavedDevice { Name = status.Name, Hostname = status.Name, Ip = addr });
            SaveDevices();
            ReloadDevices();
        }
    }

    // Try dev.Ip; on failure, re-resolve by hostname over mDNS, update Ip, retry.
    private async Task ConnectToDeviceAsync(PiSignage.Signage.SavedDevice dev)
    {
        var status = await ConnectHostAsync(dev.Ip, 8080);
        if (status == null)
        {
            LblStatus.Text = $"{dev.Name} not at {dev.Ip} — searching…";
            var newIp = await ResolveByHostnameAsync(dev.Hostname);
            if (newIp != null)
            {
                status = await ConnectHostAsync(newIp, 8080);
                if (status != null) { dev.Ip = newIp; SaveDevices(); }
            }
        }
        if (status == null)
            MessageBox.Show(this, $"Couldn't reach {dev.Name}. Try Scan network.",
                "Not found", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Core connect: returns StatusInfo on success (and wires up the UI), else null.
    private async Task<StatusInfo?> ConnectHostAsync(string host, int port)
    {
        BtnConnect.IsEnabled = false;
        LblStatus.Text = $"Connecting to {host}…";
        try
        {
            _api?.Dispose();
            _api = new ApiClient(host, port);
            var status = await _api.GetStatusAsync() ?? throw new HttpRequestException("Empty response");
            LblStatus.Text = $"Connected: {status.Name}";
            MainArea.IsEnabled = true;
            await ReloadMediaAsync();
            await ReloadPlaylistAsync();
            await RefreshStatusAsync();
            _poll.Start();
            return status;
        }
        catch
        {
            _poll.Stop();
            MainArea.IsEnabled = false;
            LblStatus.Text = "Not connected";
            return null;
        }
        finally { BtnConnect.IsEnabled = true; }
    }

    // Scan mDNS, GET /api/status on each, return the IP whose Pi name matches.
    private async Task<string?> ResolveByHostnameAsync(string hostname)
    {
        try
        {
            var devices = await MdnsDiscovery.ScanAsync(TimeSpan.FromSeconds(3));
            foreach (var d in devices)
            {
                try
                {
                    using var probe = new ApiClient(d.Address, d.Port);
                    var s = await probe.GetStatusAsync();
                    if (s != null && string.Equals(s.Name, hostname, StringComparison.OrdinalIgnoreCase))
                        return d.Address;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }
```

- [ ] **Step 6: Rename + Forget handlers; merge Scan results**

Add:
```csharp
    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice dev) return;
        var name = TextPrompt.Ask(this, "New name for this Pi:", dev.Name);
        if (name == null) return;
        dev.Name = name;
        SaveDevices();
        ReloadDevices();
        CmbAddress.SelectedItem = dev;
    }

    private void BtnForget_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice dev) return;
        if (MessageBox.Show(this, $"Forget {dev.Name}?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _devices.Remove(dev);
        SaveDevices();
        ReloadDevices();
    }
```
Then replace the body of `BtnScan_Click` so discoveries merge into the store instead of filling a string list. Replace its `try { ... }` block contents with:
```csharp
            var found = await MdnsDiscovery.ScanAsync(TimeSpan.FromSeconds(3));
            foreach (var d in found)
            {
                try
                {
                    using var probe = new ApiClient(d.Address, d.Port);
                    var s = await probe.GetStatusAsync();
                    if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                        _devices = PiSignage.Signage.DeviceStore.Upsert(_devices,
                            new PiSignage.Signage.SavedDevice { Name = s.Name, Hostname = s.Name, Ip = d.Address });
                }
                catch { }
            }
            SaveDevices();
            ReloadDevices();
            if (_devices.Count == 0) LblStatus.Text = "No devices found — type the Pi's address manually";
```

- [ ] **Step 7: Build**

```bash
taskkill //F //IM PiSignageControl.exe 2>/dev/null
dotnet build PiSignage.slnx -v q --nologo
```
Expected: 0 errors.

- [ ] **Step 8: Manual check (no unit test for WPF UI)**

Connect to the live Pi by typing `192.168.0.58` → it should appear in the dropdown by its name. Rename it → reselect → persists. Restart the app → still there. (Self-heal + wizard auto-add verified in Task 3 / end-to-end.)

- [ ] **Step 9: Commit**

```bash
git add windows-app/TextPrompt.xaml windows-app/TextPrompt.xaml.cs windows-app/MainWindow.xaml windows-app/MainWindow.xaml.cs
git commit -m "feat(wpf): saved-devices dropdown — connect/self-heal, rename, forget, scan-merge"
```

---

## Task 3: Wizard auto-adds the provisioned Pi

**Files:**
- Modify: `windows-app/WifiSetupWindow.xaml.cs`, `windows-app/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DeviceStore`, `SavedDevice`; the wizard already has the Pi's WiFi `Ip` (from `WifiStatus`) and can `GET /api/status` over USB for the name.

- [ ] **Step 1: Add the Pi to the store on wizard success**

In `WifiSetupWindow.xaml.cs`, in the success branch of `Connect_Click` (where `ok` is true and `r.Ip` is known), before/after setting the success text, add:
```csharp
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var st = await http.GetFromJsonAsync<PiSignage.Signage.WifiStatus>(PiUsbBase.TrimEnd('/') + "/api/wifi/status");
                    // get the Pi's name from /api/status over the USB link
                    var nameDoc = await http.GetStringAsync(PiUsbBase.TrimEnd('/') + "/api/status");
                    using var doc = System.Text.Json.JsonDocument.Parse(nameDoc);
                    var piName = doc.RootElement.GetProperty("name").GetString() ?? "pi";
                    var store = new PiSignage.Signage.DeviceStore();
                    var list = PiSignage.Signage.DeviceStore.Upsert(store.Load(),
                        new PiSignage.Signage.SavedDevice { Name = piName, Hostname = piName, Ip = r.Ip! });
                    store.Save(list);
                }
                catch { /* saving to the list is best-effort; the WiFi connect already succeeded */ }
```
(Requires `using System.Net.Http.Json;` at the top of the file for `GetFromJsonAsync`.)

- [ ] **Step 2: MainWindow refreshes the dropdown when the wizard closes**

In `MainWindow.xaml.cs`, update `AddPi_Click` so it reloads devices on close:
```csharp
    void AddPi_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var w = new WifiSetupWindow { Owner = this };
        w.Closed += (_, _) => { Activate(); ReloadDevices(); };   // show the newly-provisioned Pi
        w.Show();
    }
```

- [ ] **Step 3: Build**

```bash
taskkill //F //IM PiSignageControl.exe 2>/dev/null
dotnet build PiSignage.slnx -v q --nologo
```
Expected: 0 errors.

- [ ] **Step 4: End-to-end (real Pi)**

With the `pisignage1` unit reachable: run **Add a Pi** → connect its WiFi → close the wizard → it appears in the dropdown with its name + IP. Rename to "Front Counter TV", restart the app → remembered. Point its IP wrong (edit devices.json) then select it → self-heal re-resolves via Scan and reconnects.

- [ ] **Step 5: Commit**

```bash
git add windows-app/WifiSetupWindow.xaml.cs windows-app/MainWindow.xaml.cs
git commit -m "feat(wpf): wizard auto-adds provisioned Pi to saved devices; main refreshes on close"
```

---

## Self-Review

- **Spec coverage:** SavedDevice+DeviceStore JSON persistence ✓ (Task 1); dropdown by friendly name ✓ (Task 2 step 3); connect via selection ✓ (step 5); self-heal try-IP→mDNS-re-resolve-by-hostname→update ✓ (step 5 `ConnectToDeviceAsync`/`ResolveByHostnameAsync`); editable rename ✓ (step 6 + TextPrompt); forget ✓; scan-merge ✓; upsert on manual connect ✓; wizard auto-add + main refresh ✓ (Task 3); `%APPDATA%\PiSignage\devices.json` ✓.
- **Placeholder scan:** none — full code in every step.
- **Type/contract consistency:** `SavedDevice{Name,Hostname,Ip}`, `DeviceStore.Load/Save/Upsert/DefaultPath`, `StatusInfo.Name`, `MdnsDiscovery.ScanAsync`→`DiscoveredDevice{Address,Port}`, `ApiClient(host,port)` used consistently across tasks. `WifiStatus` (from the earlier WiFi feature) reused for the wizard's IP.
- **Hardware/UI caveat:** the dropdown, self-heal, rename, and wizard-add are WPF/network — `dotnet build` is the automated gate; behavior is verified manually against the live `pisignage1`. `DeviceStore` (the persistence + upsert logic) is fully unit-tested.
