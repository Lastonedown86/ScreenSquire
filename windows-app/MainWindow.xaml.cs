using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PiSignage.Control;

public partial class MainWindow : Window
{
    private ApiClient? _api;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly ObservableCollection<MediaFile> _media = new();
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private bool _dirty;
    private readonly PiSignage.Signage.DeviceStore _deviceStore = new();
    private System.Collections.Generic.List<PiSignage.Signage.SavedDevice> _devices = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => this.ExcludeFromCapture();   // control app never appears in a tournament screenshot
        LstMedia.ItemsSource = _media;
        LstPlaylist.ItemsSource = _playlist;
        _playlist.CollectionChanged += (_, _) => SetDirty(true);
        _poll.Tick += async (_, _) => await RefreshStatusAsync();
        ReloadDevices();
    }

    private void ReloadDevices()
    {
        var keepHost = (CmbAddress.SelectedItem as PiSignage.Signage.SavedDevice)?.Hostname;
        _devices = _deviceStore.Load();
        CmbAddress.ItemsSource = _devices;
        if (keepHost != null)
            CmbAddress.SelectedItem = _devices.FirstOrDefault(d =>
                string.Equals(d.Hostname, keepHost, System.StringComparison.OrdinalIgnoreCase));
    }

    private void SaveDevices()
    {
        try { _deviceStore.Save(_devices); }
        catch (System.Exception ex) { LblStatus.Text = "Could not save device list: " + ex.Message; }
    }

    // ---------------------------------------------------------- connect
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
        if (status == null)
        {
            MessageBox.Show(this, $"Could not reach the Pi at {addr}:{port}.",
                "Connection failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.IsNullOrWhiteSpace(status.Name))
        {
            _devices = PiSignage.Signage.DeviceStore.Upsert(_devices,
                new PiSignage.Signage.SavedDevice { Name = status.Name, Hostname = status.Name, Ip = addr });
            SaveDevices();
            ReloadDevices();
            CmbAddress.SelectedItem = _devices.FirstOrDefault(d =>
                string.Equals(d.Hostname, status.Name, System.StringComparison.OrdinalIgnoreCase));
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

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice dev) return;
        var name = TextPrompt.Ask(this, "New name for this Pi:", dev.Name);
        if (name == null) return;
        dev.Name = name;
        SaveDevices();
        ReloadDevices();
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

    private void OpenSignage_Click(object sender, RoutedEventArgs e)
        => new SignageWindow { Owner = this }.Show();

    void AddPi_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        new WifiSetupWindow { Owner = this }.ShowDialog();   // modal: no concurrent device-file writes
        Activate();
        ReloadDevices();   // show the newly-provisioned Pi
    }

    // Open a VNC session to the selected Pi so staff can drive the TV with kb/mouse.
    void BtnRemote_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice dev)
        {
            MessageBox.Show(this, "Pick a saved Pi from the list first.", "Remote control",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var viewer = PiSignage.Signage.VncLauncher.FindViewer(PiSignage.Signage.VncLauncher.DefaultViewerPaths());
        if (viewer == null)
        {
            if (MessageBox.Show(this,
                    "No VNC viewer is installed. Install RealVNC Viewer now? (a winget window will open)",
                    "Remote control", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "winget", "install --id RealVNC.VNCViewer -e") { UseShellExecute = true }); }
                catch (System.Exception ex) { MessageBox.Show(this, "Couldn't launch winget: " + ex.Message); }
            }
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                viewer, PiSignage.Signage.VncLauncher.Target(dev.Ip)) { UseShellExecute = false });
            LblStatus.Text = $"Opening remote control for {dev.Name} ({dev.Ip}:{PiSignage.Signage.VncLauncher.Port})…";
        }
        catch (System.Exception ex) { MessageBox.Show(this, "Couldn't open the viewer: " + ex.Message); }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false;
        BtnScan.Content = "Scanning…";
        try
        {
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
        }
        catch (System.Exception ex) { LblStatus.Text = "Scan failed: " + ex.Message; }
        finally
        {
            BtnScan.IsEnabled = true;
            BtnScan.Content = "Scan network";
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_api == null) return;
        try
        {
            var s = await _api.GetStatusAsync();
            if (s == null) return;
            LblStatus.Text = $"Connected: {s.Name}  •  {s.ScreensConnected} screen(s)"
                             + (s.OverrideActive ? "  •  OVERRIDE ACTIVE" : "");
            LblNow.Text = s.NowShowing == null ? "" : s.NowShowing.Type switch
            {
                "idle" => "Now: idle",
                "url" => $"Now: {s.NowShowing.Src}",
                _ => $"Now: {s.NowShowing.Type} {System.IO.Path.GetFileName(s.NowShowing.Src ?? "")}"
            };
        }
        catch
        {
            LblStatus.Text = "Connection lost — retrying…";
        }
    }

    // ---------------------------------------------------------- media
    private async Task ReloadMediaAsync()
    {
        if (_api == null) return;
        var files = await _api.GetMediaAsync();
        _media.Clear();
        foreach (var f in files) _media.Add(f);
    }

    private async void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.mp4;*.webm;*.mov;*.mkv|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        BtnUpload.IsEnabled = false;
        try
        {
            foreach (var path in dlg.FileNames)
            {
                LblStatus.Text = $"Uploading {System.IO.Path.GetFileName(path)}…";
                await _api.UploadMediaAsync(path);
            }
            await ReloadMediaAsync();
            LblStatus.Text = "Upload complete";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Upload failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnUpload.IsEnabled = true; }
    }

    private async void BtnDeleteMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null || LstMedia.SelectedItem is not MediaFile f) return;
        if (MessageBox.Show(this, $"Delete {f.Name} from the Pi?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _api.DeleteMediaAsync(f.Name);
            await ReloadMediaAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not delete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnAddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (LstMedia.SelectedItem is not MediaFile f) return;
        _playlist.Add(new PlaylistItem { Type = f.Type, Source = f.Name, Duration = 10 });
    }

    // ---------------------------------------------------------- playlist
    private async Task ReloadPlaylistAsync()
    {
        if (_api == null) return;
        var pl = await _api.GetPlaylistAsync() ?? new Playlist();
        _playlist.Clear();
        foreach (var i in pl.Items) _playlist.Add(i);
        SetDirty(false);
    }

    private void SetDirty(bool value)
    {
        _dirty = value;
        LblDirty.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnAddUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MessageBox.Show(this, "Enter a full web address, e.g. https://example.com",
                "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _playlist.Add(new PlaylistItem { Type = "url", Source = url, Duration = 15, Name = uri.Host });
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e) => MoveItem(sender, -1);
    private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => MoveItem(sender, +1);

    private void MoveItem(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistItem item) return;
        int i = _playlist.IndexOf(item);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _playlist.Count) return;
        _playlist.Move(i, j);
    }

    private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlaylistItem item)
            _playlist.Remove(item);
    }

    private async void BtnSavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        // TextBox duration edits don't raise CollectionChanged; grab current values
        foreach (var item in _playlist)
            if (item.Duration < 1) item.Duration = 1;

        try
        {
            await _api.PutPlaylistAsync(new Playlist { Items = _playlist.ToList(), Enabled = true });
            SetDirty(false);
            LblStatus.Text = "Playlist saved — TV updated";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save playlist",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnRevert_Click(object sender, RoutedEventArgs e)
    {
        try { await ReloadPlaylistAsync(); } catch { }
    }

    private async void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        try { await _api.NextAsync(); } catch { }
    }

    // ---------------------------------------------------------- show now
    private int ShowSeconds() =>
        int.TryParse(TxtShowSecs.Text.Trim(), out var s) && s > 0 ? s : 60;

    private async void BtnShowSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        if (LstMedia.SelectedItem is not MediaFile f)
        {
            MessageBox.Show(this, "Select a media file on the left first.", "Show now",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await _api.ShowNowAsync(new ShowNowRequest { Type = f.Type, Source = f.Name, Duration = ShowSeconds() });
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show now failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnShowUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        var url = TxtUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Enter a URL in the box above first.", "Show now",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await _api.ShowNowAsync(new ShowNowRequest { Type = "url", Source = url, Duration = ShowSeconds() });
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show now failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnClearShow_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        try
        {
            await _api.ClearShowNowAsync();
            await RefreshStatusAsync();
        }
        catch { }
    }
}
