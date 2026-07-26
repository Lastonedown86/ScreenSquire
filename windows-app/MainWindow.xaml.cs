using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PiSignage.Control;

public partial class MainWindow : Window
{
    private ApiClient? _api;
    private string? _connectedHost;   // the Pi we're connected to (target for Kiosk/Remote)
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly ObservableCollection<MediaFile> _media = new();
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private bool _dirty;
    private readonly PiSignage.Signage.DeviceStore _deviceStore = new();
    private readonly PiSignage.Signage.CredentialVault _credentialVault =
        new(new DpapiSecretProtector());
    private PiSignage.Signage.ControlContext? _connectedControlContext;
    private System.Collections.Generic.List<PiSignage.Signage.SavedDevice> _devices = new();

    public MainWindow()
    {
        InitializeComponent();
        App.TrackPlacement(this, "Main");
        Loaded += (_, _) => this.ExcludeFromCapture();   // control app never appears in a tournament screenshot
        LstMedia.ItemsSource = _media;
        LstPlaylist.ItemsSource = _playlist;
        _playlist.CollectionChanged += (_, _) => SetDirty(true);
        _poll.Tick += async (_, _) => await RefreshStatusAsync();
        Closing += MainWindow_Closing;
        ReloadDevices();
        _ = ProbeDevicesAsync();   // light up the online dots

        // Reconnect to the Pi used last time, so the app opens ready to go.
        var last = _devices.FirstOrDefault(d => string.Equals(
            d.Hostname, App.Settings.LastDeviceHostname, System.StringComparison.OrdinalIgnoreCase));
        if (last != null)
        {
            CmbAddress.SelectedItem = last;
            Loaded += async (_, _) => await ConnectToDeviceAsync(last);
        }
    }

    // Ask before losing unsaved playlist edits. Save is async, so the first
    // pass cancels the close, saves, then closes for real.
    private bool _closeConfirmed;
    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || !_dirty || _api == null) return;
        var r = MessageBox.Show(this,
            "You changed the playlist but didn't save it to the Pi.\n\nSave before closing?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            if (!await SavePlaylistAsync()) return;   // save failed — stay open
            _closeConfirmed = true;
            Close();
        }
        // No -> close without saving
    }

    // Probe every saved Pi in parallel and set the dropdown's online dots.
    private async Task ProbeDevicesAsync()
    {
        var devs = _devices.ToList();
        await Task.WhenAll(devs.Select(async d =>
        {
            try
            {
                using var probe = new ApiClient(d.Ip, d.Port);
                var status = await probe.GetStatusAsync();
                d.Online = status is not null &&
                    PiSignage.Signage.DeviceIdentityPolicy.IsMatch(
                        d, status.DeviceId, status.Name);
            }
            catch { d.Online = false; }
        }));
    }

    private void ReloadDevices()
    {
        var selected = CmbAddress.SelectedItem as PiSignage.Signage.SavedDevice;
        var keepId = selected?.DeviceId;
        var keepHost = selected?.Hostname;
        _devices = _deviceStore.Load();
        CmbAddress.ItemsSource = _devices;
        if (!string.IsNullOrWhiteSpace(keepId))
            CmbAddress.SelectedItem = _devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, keepId, System.StringComparison.Ordinal));
        else if (keepHost != null)
            CmbAddress.SelectedItem = _devices.FirstOrDefault(d =>
                string.IsNullOrWhiteSpace(d.DeviceId) &&
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
        Toaster.Show("Pick your Pi from the list first — or click 'Find my Pi' to search for it.");
    }

    // Try dev.Ip; on failure, re-resolve by hostname over mDNS, update Ip, retry.
    private async Task ConnectToDeviceAsync(PiSignage.Signage.SavedDevice dev)
    {
        var endpointHost = dev.Ip;
        var endpointPort = dev.Port;
        var status = await ConnectHostAsync(endpointHost, endpointPort, dev);
        if (status == null)
        {
            LblStatus.Text = $"{dev.Name} not at {dev.Ip} — searching…";
            var discovered = await ResolveDeviceAsync(dev);
            if (discovered != null)
            {
                endpointHost = discovered.Address;
                endpointPort = discovered.Port;
                status = await ConnectHostAsync(endpointHost, endpointPort, dev);
            }
        }
        if (status != null)
        {
            PiSignage.Signage.DeviceIdentityPolicy.ApplyVerifiedEndpoint(
                dev,
                status.DeviceId,
                status.Name,
                endpointHost,
                endpointPort);
            SaveDevices();
        }
        if (status == null)
            Toaster.Show($"Couldn't reach {dev.Name}. Check it's powered on, then click Find my Pi.", ToastKind.Error);
    }

    // Core connect: returns StatusInfo on success (and wires up the UI), else null.
    private async Task<StatusInfo?> ConnectHostAsync(
        string host,
        int port,
        PiSignage.Signage.SavedDevice expected)
    {
        BtnConnect.IsEnabled = false;
        LblStatus.Text = $"Connecting to {host}…";
        try
        {
            _api?.Dispose();
            _connectedControlContext = null;
            _api = new ApiClient(host, port);
            var status = await _api.GetStatusAsync() ?? throw new HttpRequestException("Empty response");
            if (!PiSignage.Signage.DeviceIdentityPolicy.IsMatch(
                    expected, status.DeviceId, status.Name))
            {
                throw new InvalidDataException(
                    "The endpoint reported a different device identity.");
            }
            _connectedControlContext = TryControlContext(status.DeviceId);
            LblStatus.Text = $"Connected to {DisplayName(status)}";
            MainArea.IsEnabled = true;
            GettingStarted.Visibility = Visibility.Collapsed;
            _connectedHost = host;
            BtnKiosk.IsEnabled = true;   // device actions target this Pi
            // ponytail: ControlContext has no legacy/unsigned variant in this codebase —
            // TryControlContext only ever returns non-null for a paired, signed credential.
            BtnRemote.IsEnabled = _connectedControlContext is not null &&
                                  !_connectedControlContext.IsLegacyUnsigned;
            await ReloadMediaAsync();
            await ReloadPlaylistAsync();
            await RefreshStatusAsync();
            await RefreshKioskLabelAsync();
            _poll.Start();
            if (!string.IsNullOrWhiteSpace(status.Name))
            {
                App.Settings.LastDeviceHostname = status.Name;
                App.SaveSettings();
            }
            return status;
        }
        catch
        {
            _poll.Stop();
            MainArea.IsEnabled = false;
            GettingStarted.Visibility = Visibility.Visible;
            _connectedHost = null;
            _connectedControlContext = null;
            BtnKiosk.IsEnabled = false;
            BtnRemote.IsEnabled = false;
            BtnKiosk.Content = "_TV display on/off";
            LblStatus.Text = "Not connected — follow the steps above";
            return null;
        }
        finally { BtnConnect.IsEnabled = true; }
    }

    // Rename/Forget only make sense for a saved device that's actually selected.
    private void CmbAddress_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool isSaved = CmbAddress.SelectedItem is PiSignage.Signage.SavedDevice;
        if (BtnRename != null) BtnRename.IsEnabled = isSaved;
        if (BtnForget != null) BtnForget.IsEnabled = isSaved;
        if (BtnPrepareDelivery != null)
            BtnPrepareDelivery.IsEnabled = CanPrepareSelectedDevice();
    }

    private bool CanPrepareSelectedDevice() =>
        CmbAddress.SelectedItem is PiSignage.Signage.SavedDevice device &&
        DeliveryPreparation.CanPrepare(
            device,
            deviceId => _credentialVault.TryGet(deviceId) is not null);

    private void CmbAddress_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) BtnConnect_Click(sender, e);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.S &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
            _dirty && MainArea.IsEnabled)
        {
            BtnSavePlaylist_Click(sender, e);
            e.Handled = true;
        }
    }

    // Reject non-digit typing in numeric fields (durations/seconds/minutes).
    private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    // Duration edits update the bound item silently (no CollectionChanged);
    // the focus guard skips the events fired while a reload populates the list.
    private void Duration_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (((TextBox)sender).IsKeyboardFocused) SetDirty(true);
    }

    private async Task RefreshKioskLabelAsync()
    {
        if (_api == null) { BtnKiosk.Content = "_TV display on/off"; return; }
        try { var st = await _api.GetKioskAsync(); BtnKiosk.Content = (st?.Running ?? false) ? "_TV display: On" : "_TV display: Off"; }
        catch { BtnKiosk.Content = "_TV display on/off"; }
    }

    // Scan mDNS and restore by immutable device ID. Hostname is used only for
    // a legacy saved entry that does not have an ID yet.
    private async Task<DiscoveredDevice?> ResolveDeviceAsync(
        PiSignage.Signage.SavedDevice saved)
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
                    var matches = !string.IsNullOrWhiteSpace(saved.DeviceId)
                        ? s != null && string.Equals(
                            s.DeviceId, saved.DeviceId, StringComparison.Ordinal)
                        : s != null && string.Equals(
                            s.Name, saved.Hostname, StringComparison.OrdinalIgnoreCase);
                    if (matches) return d;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // Friendly name for a Pi: the saved device's name if we have one, else the
    // hostname the Pi reports. Keeps renames visible everywhere a name shows.
    private string DisplayName(StatusInfo status)
    {
        PiSignage.Signage.SavedDevice? dev = null;
        if (!string.IsNullOrWhiteSpace(status.DeviceId))
        {
            dev = _devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, status.DeviceId, StringComparison.Ordinal));
        }
        dev ??= _devices.FirstOrDefault(d =>
            string.IsNullOrWhiteSpace(d.DeviceId) &&
            string.Equals(d.Hostname, status.Name, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(dev?.Name) ? status.Name : dev!.Name;
    }

    private async void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice dev) return;
        var name = TextPrompt.Ask(this, "New name for this Pi:", dev.Name);
        if (name == null) return;

        var oldHostname = dev.Hostname;
        dev.Name = name;

        // Push the rename to the Pi itself so its TV splash (and the name it
        // reports to scans) match. If it's unreachable, the rename stays local.
        try
        {
            using var api = new ApiClient(dev.Ip, dev.Port);
            await api.SetNameAsync(name, ControlContext(dev.DeviceId));
            dev.Hostname = name;   // scans key devices by the Pi-reported name
            if (string.Equals(App.Settings.LastDeviceHostname, oldHostname, StringComparison.OrdinalIgnoreCase))
                App.Settings.LastDeviceHostname = name;
            if (string.Equals(App.Settings.SignageTarget, oldHostname, StringComparison.OrdinalIgnoreCase))
                App.Settings.SignageTarget = name;
            App.SaveSettings();
        }
        catch
        {
            Toaster.Show("Name changed in your list, but the Pi couldn't be reached — its TV keeps the old name until it's back online.", ToastKind.Warning);
        }

        SaveDevices();
        ReloadDevices();
        // trickle the new name everywhere it's already on screen
        if (_api != null) await RefreshStatusAsync();
        foreach (var w in OwnedWindows.OfType<SignageWindow>()) w.RefreshDevices();
    }

    private void BtnForget_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice dev) return;
        if (MessageBox.Show(this, $"Remove {dev.Name} from your device list?\n\nYou can always add it back with 'Find my Pi'.", "Remove device",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _devices.Remove(dev);
        SaveDevices();
        ReloadDevices();
    }

    private void OpenSignage_Click(object sender, RoutedEventArgs e)
        => new SignageWindow { Owner = this }.Show();

    async void AddPi_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var wizard = new WifiSetupWindow { Owner = this };
        wizard.ShowDialog();   // modal: no concurrent device-file writes
        Activate();
        ReloadDevices();   // show the newly-provisioned Pi
        foreach (var window in OwnedWindows.OfType<SignageWindow>())
            window.RefreshDevices();

        // Close the loop: land the client connected to the Pi they just set up.
        if (wizard.NewDeviceId != null)
        {
            var dev = _devices.FirstOrDefault(d => string.Equals(
                d.DeviceId, wizard.NewDeviceId, System.StringComparison.Ordinal));
            if (dev != null)
            {
                CmbAddress.SelectedItem = dev;
                await ConnectToDeviceAsync(dev);
            }
        }
    }

    private async void BtnPrepareDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (CmbAddress.SelectedItem is not PiSignage.Signage.SavedDevice device)
        {
            Toaster.Show("Pick the saved Pi you are preparing for delivery first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(device.DeviceId))
        {
            Toaster.Show(
                "This saved Pi has no stable identity. Pair it over USB before preparing it for delivery.",
                ToastKind.Warning);
            return;
        }

        const string warning =
            "Prepare this Pi for delivery?\n\n" +
            "This permanently removes your control access, Wi-Fi, media, playlists,\n" +
            "tournament screens, timer, and temporary name. The client will need the\n" +
            "8-digit PIN sticker to set it up.";
        if (MessageBox.Show(
                this,
                warning,
                "Prepare Pi for delivery",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var confirmation = TextPrompt.Ask(
            this,
            "Type PREPARE to permanently erase this Pi:",
            "",
            "Prepare Pi for delivery");
        if (!string.Equals(confirmation, "PREPARE", StringComparison.Ordinal))
        {
            if (confirmation is not null)
            {
                Toaster.Show(
                    "Nothing was erased. Type PREPARE exactly to continue.",
                    ToastKind.Warning);
            }
            return;
        }

        BtnPrepareDelivery.IsEnabled = false;
        SetBusy(true);
        try
        {
            DeliveryPreparationOutcome outcome;
            try
            {
                var context = ControlContext(device.DeviceId);
                using var http = new HttpClient
                {
                    // Reset may enumerate and delete several NetworkManager
                    // profiles before replying.
                    Timeout = TimeSpan.FromSeconds(90),
                };
                var operations = new WindowsDeliveryPreparationOperations(
                    http,
                    _credentialVault,
                    _deviceStore,
                    new PiSignage.Signage.SettingsStore(),
                    App.Settings,
                    ThumbnailCache.ClearDevice);
                outcome = await new DeliveryPreparation(operations)
                    .RunAsync(device, context);
            }
            catch (Exception ex)
            {
                Toaster.Show(
                    "The Pi was not prepared for delivery: " + ex.Message,
                    ToastKind.Error);
                return;
            }

            // A returned outcome means the Pi reset succeeded. Always leave the
            // UI in delivery-ready state even if local residue was reported.
            var residue = outcome.CleanupErrors.ToList();
            _poll.Stop();
            var oldApi = _api;
            _api = null;
            _connectedControlContext = null;
            _connectedHost = null;
            MainArea.IsEnabled = false;
            GettingStarted.Visibility = Visibility.Visible;
            BtnKiosk.IsEnabled = false;
            BtnRemote.IsEnabled = false;
            BtnKiosk.Content = "_TV display on/off";
            LblStatus.Text = "Ready for delivery — unplug the USB cable.";
            try
            {
                oldApi?.Dispose();
            }
            catch (Exception ex)
            {
                residue.Add(new DeliveryCleanupError("connection", ex.Message));
            }
            try
            {
                ReloadDevices();
            }
            catch (Exception ex)
            {
                residue.Add(new DeliveryCleanupError("device view", ex.Message));
            }
            foreach (var window in OwnedWindows.OfType<SignageWindow>())
            {
                try
                {
                    window.RefreshDevices();
                }
                catch (Exception ex)
                {
                    residue.Add(new DeliveryCleanupError(
                        "tournament device view",
                        ex.Message));
                }
            }

            if (residue.Count == 0 &&
                !outcome.ResetWasConfirmedAfterAmbiguousFailure)
            {
                Toaster.Show(
                    "Ready for delivery — unplug the USB cable.",
                    ToastKind.Success);
            }
            else
            {
                var confirmationWarning =
                    outcome.ResetWasConfirmedAfterAmbiguousFailure
                        ? "The reset response was lost, but the same Pi confirmed over USB that it is now unpaired. "
                        : "";
                var residueWarning = residue.Count == 0
                    ? ""
                    : "Some local builder records remain: " +
                      string.Join(
                          ", ",
                          residue.Select(error => error.Operation).Distinct()) +
                      ".";
                Toaster.Show(
                    "Ready for delivery — unplug the USB cable. " +
                    confirmationWarning +
                    residueWarning,
                    ToastKind.Warning);
            }
        }
        finally
        {
            SetBusy(false);
            BtnPrepareDelivery.IsEnabled = CanPrepareSelectedDevice();
        }
    }

    // Stop or restart the signage kiosk on the Pi.
    async void BtnKiosk_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null)
        {
            Toaster.Show("Connect to your Pi first — pick it at the top and click Connect.");
            return;
        }
        try
        {
            var st = await _api.GetKioskAsync();
            bool running = st?.Running ?? false;
            if (running &&   // turning the display OFF stops the live TV — confirm
                MessageBox.Show(this, "This turns off the signage on the TV and shows the Pi's desktop instead.\n\nTurn the TV display off?",
                    "TV display off", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            bool ok = await _api.SetKioskAsync(
                !running,
                ConnectedControlContext());
            if (!ok) { Toaster.Show("Couldn't switch the TV display — try again.", ToastKind.Error); return; }
            Toaster.Show(running
                ? "TV display is off — the Pi desktop is visible locally."
                : "TV display is on — your signage is playing again.", ToastKind.Success);
            await RefreshKioskLabelAsync();
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't switch the TV display: " + ex.Message, ToastKind.Error);
        }
    }

    // Launch the bundled remote viewer against a paired connection.
    private async void BtnRemote_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null || _connectedHost == null) return;
        var viewer = RemoteViewerLauncher.BundledViewerPath();
        if (!System.IO.File.Exists(viewer))
        {
            Toaster.Show("Remote viewer is missing from this install.", ToastKind.Error);
            return;
        }
        BtnRemote.IsEnabled = false;
        PiSignage.Signage.ControlContext? ctx = null;
        try
        {
            ctx = ConnectedControlContext();
            var session = await _api.StartRemoteDesktopAsync(ctx);
            if (session == null)
            {
                Toaster.Show("The Pi did not start remote control.", ToastKind.Error);
                BtnRemote.IsEnabled = true;
                return;
            }
            Toaster.Show("Opening remote control — the viewer window will appear.", ToastKind.Success);
            var proc = RemoteViewerLauncher.Launch(viewer, _connectedHost, session);
            _ = Task.Run(async () =>
            {
                await proc.WaitForExitAsync();
                try { await _api.StopRemoteDesktopAsync(ctx); } catch { /* idle timeout is the backstop */ }
                Dispatcher.Invoke(() => BtnRemote.IsEnabled = true);
            });
            return;
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't start remote control: " + ex.Message, ToastKind.Error);
            try { await _api.StopRemoteDesktopAsync(ctx); } catch { }
        }
        BtnRemote.IsEnabled = true;
    }

    // Push the agent software bundled inside this exe to every saved, reachable,
    // out-of-date Pi (not just the one currently connected).
    private async void BtnUpdatePi_Click(object sender, RoutedEventArgs e)
    {
        var bundled = AgentBundle.Version();
        if (bundled == null || _api == null) return;
        BtnUpdatePi.IsEnabled = false;
        try
        {
            Toaster.Show("Updating your Pi — the TV will blink once. This takes about half a minute…");
            var zip = PiSignage.Signage.AgentUpdater.BuildZip(AgentBundle.Files());
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // update every saved Pi that's reachable and out of date; connected Pi included
            var targets = _devices.Where(d => !string.IsNullOrEmpty(d.Ip)).ToList();
            int ok = 0, skipped = 0; var failedNames = new List<string>();
            foreach (var dev in targets)
            {
                var baseUrl = $"http://{dev.Ip}:{dev.Port}";
                try
                {
                    string? current = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(
                            await http.GetStringAsync($"{baseUrl}/api/status"));
                        if (doc.RootElement.TryGetProperty("agent_version", out var v))
                            current = v.GetString();
                    }
                    catch (Exception) { skipped++; continue; }   // off / unreachable — leave it alone
                    if (current == bundled) { skipped++; continue; }  // already up to date

                    await PiSignage.Signage.AgentUpdater.PushAsync(
                        http,
                        baseUrl,
                        zip,
                        bundled,
                        ControlContext(dev.DeviceId));
                    ok++;
                }
                catch (HttpRequestException) { failedNames.Add($"{dev.Name} (needs a one-time manual update)"); }
                catch (Exception) { failedNames.Add(dev.Name); }
            }

            if (failedNames.Count == 0)
                Toaster.Show(ok > 0 ? $"Done — {ok} Pi{(ok == 1 ? "" : "s")} updated." : "Everything was already up to date.",
                             ToastKind.Success);
            else
                Toaster.Show($"Updated {ok}, but these didn't finish: {string.Join(", ", failedNames)}. " +
                             "Check they're powered on and try again.", ToastKind.Warning);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't update your Pis: " + ex.Message, ToastKind.Error);
        }
        finally { BtnUpdatePi.IsEnabled = true; }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false;
        BtnScan.Content = "Looking…";
        try
        {
            int foundCount = 0;
            var found = await MdnsDiscovery.ScanAsync(TimeSpan.FromSeconds(3));
            foreach (var d in found)
            {
                try
                {
                    using var probe = new ApiClient(d.Address, d.Port);
                    var s = await probe.GetStatusAsync();
                    if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                    {
                        _devices = PiSignage.Signage.DeviceStore.Upsert(_devices,
                            new PiSignage.Signage.SavedDevice {
                                DeviceId = s.DeviceId,
                                Name = s.Name,
                                Hostname = s.Name,
                                Ip = d.Address,
                                Port = d.Port,
                            });
                        foundCount++;
                    }
                }
                catch { }
            }
            SaveDevices();
            ReloadDevices();
            _ = ProbeDevicesAsync();
            if (foundCount == 0)
                Toaster.Show("No Pis found. Check the Pi is powered on and on the same WiFi, then try again — or use 'Set up a new Pi'.", ToastKind.Warning);
            else
                Toaster.Show($"Found {foundCount} Pi{(foundCount == 1 ? "" : "s")} — pick one and click Connect.", ToastKind.Success);
        }
        catch (System.Exception ex) { Toaster.Show("Search failed: " + ex.Message, ToastKind.Error); }
        finally
        {
            BtnScan.IsEnabled = true;
            BtnScan.Content = "_Find my Pi";
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_api == null) return;
        try
        {
            var s = await _api.GetStatusAsync();
            if (s == null) return;
            if (CmbAddress.SelectedItem is PiSignage.Signage.SavedDevice on) on.Online = true;
            LblStatus.Text = $"Connected to {DisplayName(s)}  •  {s.ScreensConnected} screen(s)"
                             + (s.OverrideActive ? "  •  showing a one-off item (not the playlist)" : "");
            LblNow.Text = s.NowShowing == null ? "" : s.NowShowing.Type switch
            {
                "idle" => "Now: idle",
                "url" => $"Now: {FriendlyUrlLabel(s.NowShowing.Src)}",
                _ => $"Now: {s.NowShowing.Type} {System.IO.Path.GetFileName(s.NowShowing.Src ?? "")}"
            };
            BtnUpdatePi.Visibility =
                AgentBundle.Version() is string bundled && s.AgentVersion != bundled
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            LblStatus.Text = "Lost connection to the Pi — trying to reconnect…";
            if (CmbAddress.SelectedItem is PiSignage.Signage.SavedDevice off) off.Online = false;
            BtnUpdatePi.Visibility = Visibility.Collapsed;
        }
    }

    // The Pi's kiosk browser loads board/timer pages from its own agent via localhost,
    // so the raw src reads "http://localhost:8080/…" — translate to plain language.
    static string FriendlyUrlLabel(string? src)
    {
        if (string.IsNullOrEmpty(src)) return "";
        if (!System.Uri.TryCreate(src, System.UriKind.Absolute, out var u)) return src;
        if (u.AbsolutePath.StartsWith("/dashboard"))
        {
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            if (q["view"] == "timer") return "round timer";
            var name = q["name"];
            if (!string.IsNullOrEmpty(name)) return $"{name} board on the TV";
        }
        return $"webpage ({u.Host})";
    }

    private void SetBusy(bool on) => Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------- media
    private async Task ReloadMediaAsync()
    {
        if (_api == null) return;
        SetBusy(true);
        try
        {
            var files = await _api.GetMediaAsync();
            _media.Clear();
            foreach (var f in files)
            {
                f.PropertyChanged += MediaChecked_Changed;
                _media.Add(f);
            }
            UpdateDeleteSelectedButton();
        }
        finally { SetBusy(false); }
        _ = LoadThumbsAsync();   // fill in thumbnails after the list renders
    }

    // Fetch/cache image thumbnails one by one and mirror them onto playlist rows.
    private async Task LoadThumbsAsync()
    {
        var api = _api;
        if (api == null) return;
        foreach (var f in _media.Where(m => m.Type == "image" && m.Thumb == null).ToList())
        {
            if (_api != api) return;   // disconnected or switched Pi mid-load
            f.Thumb = await ThumbnailCache.GetAsync(api.BaseUrl, f.Name, f.Bytes);
        }
        foreach (var f in _media) ApplyThumbToPlaylist(f);
    }

    private void ApplyThumbToPlaylist(MediaFile f)
    {
        if (f.Thumb == null) return;
        foreach (var it in _playlist.Where(p => p.Type == "image" && p.Source == f.Name))
            it.Thumb = f.Thumb;
    }

    private static readonly string[] MediaExts =
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".mp4", ".webm", ".mov", ".mkv" };

    private async void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.mp4;*.webm;*.mov;*.mkv|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        await UploadFilesAsync(dlg.FileNames);
    }

    // Shared by the Upload button and drag-and-drop.
    private async Task<bool> UploadFilesAsync(string[] paths)
    {
        if (_api == null) return false;
        BtnUpload.IsEnabled = false;
        try
        {
            foreach (var path in paths)
            {
                LblStatus.Text = $"Uploading {System.IO.Path.GetFileName(path)}…";
                await _api.UploadMediaAsync(path, ConnectedControlContext());
            }
            await ReloadMediaAsync();
            Toaster.Show("Upload finished — your files are on the Pi.", ToastKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            Toaster.Show("Upload failed: " + ex.Message, ToastKind.Error);
            return false;
        }
        finally { BtnUpload.IsEnabled = true; }
    }

    // ---------------------------------------------------------- drag & drop
    private static string[] DroppedMediaFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => MediaExts.Contains(
                  System.IO.Path.GetExtension(f).ToLowerInvariant())).ToArray()
            : Array.Empty<string>();

    private void FileDrag_Over(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
                  : e.Data.GetDataPresent(typeof(PlaylistItem)) ? DragDropEffects.Move
                  : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LstMedia_Drop(object sender, DragEventArgs e)
    {
        var files = DroppedMediaFiles(e);
        if (files.Length == 0)
        {
            Toaster.Show("Drop pictures or videos (jpg, png, mp4, …) here to upload them.");
            return;
        }
        await UploadFilesAsync(files);
    }

    private async void LstPlaylist_Drop(object sender, DragEventArgs e)
    {
        // dropping files = upload AND add to the playlist in one gesture
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = DroppedMediaFiles(e);
            if (files.Length == 0)
            {
                Toaster.Show("Drop pictures or videos (jpg, png, mp4, …) here to put them on the TV.");
                return;
            }
            if (!await UploadFilesAsync(files)) return;
            foreach (var path in files)
            {
                var name = System.IO.Path.GetFileName(path);
                var f = _media.FirstOrDefault(m => m.Name == name);
                if (f != null) _playlist.Add(new PlaylistItem { Type = f.Type, Source = f.Name, Duration = 10, Thumb = f.Thumb });
            }
            return;
        }

        // internal drag = reorder
        if (e.Data.GetData(typeof(PlaylistItem)) is not PlaylistItem dragged) return;
        int from = _playlist.IndexOf(dragged);
        if (from < 0) return;
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PlaylistItem;
        int to = target != null ? _playlist.IndexOf(target) : _playlist.Count - 1;
        if (to >= 0 && to != from) _playlist.Move(from, to);
    }

    private Point _dragStart;

    private void LstPlaylist_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(null);

    private void LstPlaylist_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var src = e.OriginalSource as DependencyObject;
        // clicking the row's buttons or duration box must not start a drag
        if (FindAncestor<Button>(src) != null || FindAncestor<TextBox>(src) != null) return;
        if (FindAncestor<ListBoxItem>(src)?.DataContext is not PlaylistItem item) return;
        DragDrop.DoDragDrop(LstPlaylist, new DataObject(typeof(PlaylistItem), item), DragDropEffects.Move);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private async void MediaDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null || (sender as FrameworkElement)?.DataContext is not MediaFile f) return;
        await DeleteFilesAsync(new System.Collections.Generic.List<MediaFile> { f });
    }

    // Shared by the row ✕ and 'Delete selected'. Files still being shown on
    // the TV get a follow-up prompt offering to take them off first.
    private async Task DeleteFilesAsync(System.Collections.Generic.List<MediaFile> picked)
    {
        if (_api == null || picked.Count == 0) return;
        var what = picked.Count == 1 ? picked[0].Name : $"{picked.Count} files";
        if (MessageBox.Show(this, $"Delete {what} from the Pi?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var inUse = new System.Collections.Generic.List<MediaFile>();
        var failed = new System.Collections.Generic.List<string>();
        var deleted = new System.Collections.Generic.List<string>();
        SetBusy(true);
        try
        {
            foreach (var f in picked)
            {
                try
                {
                    await _api.DeleteMediaAsync(
                        f.Name,
                        ConnectedControlContext());
                    deleted.Add(f.Name);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { inUse.Add(f); }
                catch (Exception ex) { failed.Add($"{f.Name} — {ex.Message}"); }
            }
        }
        finally { SetBusy(false); }

        if (inUse.Count > 0)
        {
            var q = inUse.Count == 1
                ? $"{inUse[0].Name} is currently being shown on the TV.\n\nTake it off the screen and delete it?"
                : "These files are currently being shown on the TV:\n"
                  + string.Join("\n", inUse.Select(f => "  • " + f.Name))
                  + "\n\nTake them off the screen and delete them?";
            if (MessageBox.Show(this, q, "Still on the TV",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                SetBusy(true);
                try
                {
                    foreach (var f in inUse)
                    {
                        try
                        {
                            await _api.DetachMediaAsync(
                                f.Name,
                                ConnectedControlContext());
                            await _api.DeleteMediaAsync(
                                f.Name,
                                ConnectedControlContext());
                            deleted.Add(f.Name);
                        }
                        catch (Exception ex) { failed.Add($"{f.Name} — {ex.Message}"); }
                    }
                }
                finally { SetBusy(false); }
            }
        }

        await ReloadMediaAsync();
        // Detach rewrote the Pi's playlist — mirror locally without clobbering
        // unsaved edits.
        if (!_dirty) await ReloadPlaylistAsync();
        else
            foreach (var it in _playlist.Where(p => deleted.Contains(p.Source)).ToList())
                _playlist.Remove(it);
        if (failed.Count > 0)
            Toaster.Show("Some files couldn't be deleted:\n" + string.Join("\n", failed), ToastKind.Warning);
    }

    private void MediaChecked_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaFile.IsChecked)) UpdateDeleteSelectedButton();
    }

    private void UpdateDeleteSelectedButton()
    {
        int n = _media.Count(m => m.IsChecked);
        BtnDeleteSelected.IsEnabled = n > 0;
        BtnDeleteSelected.Content = n > 0 ? $"Delete selected ({n})" : "Delete selected";
        ChkSelectAll.IsEnabled = _media.Count > 0;
        // programmatic set — Click only fires on user clicks, so no feedback loop
        ChkSelectAll.IsChecked = n == 0 ? false : n == _media.Count ? true : (bool?)null;
    }

    private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = ChkSelectAll.IsChecked == true;
        foreach (var m in _media) m.IsChecked = on;
        UpdateDeleteSelectedButton();   // covers the empty-list click
    }

    private async void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        await DeleteFilesAsync(_media.Where(m => m.IsChecked).ToList());
    }

    private async void MediaRename_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null || (sender as FrameworkElement)?.DataContext is not MediaFile f) return;
        var entered = TextPrompt.Ask(this, "New name for this file:",
            System.IO.Path.GetFileNameWithoutExtension(f.Name), "Rename");
        if (string.IsNullOrWhiteSpace(entered)) return;
        var oldName = f.Name;
        try
        {
            var newName = await _api.RenameMediaAsync(
                oldName,
                entered.Trim(),
                ConnectedControlContext());
            await ReloadMediaAsync();
            // The Pi rewrote its playlist to the new name. Mirror that locally —
            // but never clobber unsaved edits with a server reload.
            if (!_dirty) await ReloadPlaylistAsync();
            else
                foreach (var it in _playlist.Where(p => p.Type != "url" && p.Source == oldName))
                    it.Source = newName;
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't rename the file: " + ex.Message, ToastKind.Error);
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
        SetBusy(true);
        try
        {
            var pl = await _api.GetPlaylistAsync() ?? new Playlist();
            _playlist.Clear();
            foreach (var i in pl.Items) _playlist.Add(i);
            SetDirty(false);
            foreach (var f in _media) ApplyThumbToPlaylist(f);
        }
        finally { SetBusy(false); }
    }

    private void SetDirty(bool value)
    {
        _dirty = value;
        LblDirty.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // Prompt for a full web address; returns null if cancelled or invalid.
    private Uri? AskForUrl(string title)
    {
        var url = TextPrompt.Ask(this, "Web address (e.g. https://example.com):", "https://", title);
        if (url == null) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Toaster.Show("That doesn't look like a full web address — it should start with https://", ToastKind.Warning);
            return null;
        }
        return uri;
    }

    private void BtnAddUrl_Click(object sender, RoutedEventArgs e)
    {
        if (AskForUrl("Add web page") is not { } uri) return;
        _playlist.Add(new PlaylistItem { Type = "url", Source = uri.ToString(), Duration = 15, Name = uri.Host });
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
        if (await SavePlaylistAsync())
            Toaster.Show("Saved — the TV is now playing your updated playlist.", ToastKind.Success);
    }

    private async Task<bool> SavePlaylistAsync()
    {
        if (_api == null) return false;
        // TextBox duration edits don't raise CollectionChanged; grab current values
        foreach (var item in _playlist)
            if (item.Duration < 1) item.Duration = 1;

        try
        {
            await _api.PutPlaylistAsync(
                new Playlist { Items = _playlist.ToList(), Enabled = true },
                ConnectedControlContext());
            SetDirty(false);
            return true;
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't save the playlist: " + ex.Message, ToastKind.Error);
            return false;
        }
    }

    private async void BtnRevert_Click(object sender, RoutedEventArgs e)
    {
        try { await ReloadPlaylistAsync(); }
        catch (Exception ex) { Toaster.Show("Couldn't reload the playlist from the Pi: " + ex.Message, ToastKind.Error); }
    }

    private async void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        try { await _api.NextAsync(ConnectedControlContext()); }
        catch (Exception ex) { Toaster.Show("Couldn't skip to the next item: " + ex.Message, ToastKind.Error); }
    }

    // ---------------------------------------------------------- show now
    private int ShowSeconds() =>
        int.TryParse(TxtShowSecs.Text.Trim(), out var s) && s > 0 ? s : 60;

    private async void BtnShowSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        if (LstMedia.SelectedItem is not MediaFile f)
        {
            Toaster.Show("Click a file in the list on the left first, then try again.");
            return;
        }
        try
        {
            await _api.ShowNowAsync(
                new ShowNowRequest
                {
                    Type = f.Type,
                    Source = f.Name,
                    Duration = ShowSeconds(),
                },
                ConnectedControlContext());
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't put that on the TV: " + ex.Message, ToastKind.Error);
        }
    }

    private async void BtnShowUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        if (AskForUrl("Show a web page") is not { } uri) return;
        try
        {
            await _api.ShowNowAsync(
                new ShowNowRequest
                {
                    Type = "url",
                    Source = uri.ToString(),
                    Duration = ShowSeconds(),
                },
                ConnectedControlContext());
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't put that on the TV: " + ex.Message, ToastKind.Error);
        }
    }

    private async void BtnClearShow_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        try
        {
            await _api.ClearShowNowAsync(ConnectedControlContext());
            await RefreshStatusAsync();
        }
        catch (Exception ex) { Toaster.Show("Couldn't switch back to the playlist: " + ex.Message, ToastKind.Error); }
    }

    private PiSignage.Signage.ControlContext? TryControlContext(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return PiSignage.Signage.ControlContext.LegacyUnsigned();
        var credential = _credentialVault.TryGet(deviceId);
        if (credential is null)
            return null;
        var stableDeviceId = deviceId;
        return new PiSignage.Signage.ControlContext(
            stableDeviceId,
            _credentialVault.Load().ControllerId,
            credential.Secret,
            () => _credentialVault.TakeNextCounter(stableDeviceId),
            _credentialVault.Path);
    }

    private PiSignage.Signage.ControlContext ControlContext(string deviceId) =>
        TryControlContext(deviceId)
        ?? throw new KeyNotFoundException(
            $"No controller credential exists for device '{deviceId}'.");

    private PiSignage.Signage.ControlContext ConnectedControlContext() =>
        _connectedControlContext
        ?? throw new InvalidOperationException(
            "This Pi is not paired with this controller.");
}
