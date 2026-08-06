using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Media;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class WifiSetupWindow : Window
{
    const string PiUsbBase = "http://10.55.0.1:8080";   // fixed USB-gadget address
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    readonly WifiProvisioner _wifi;
    readonly PairingClient _pairing;
    readonly CredentialVault _vault = new(new DpapiSecretProtector());
    readonly CancellationTokenSource _lifetime = new();
    readonly DetectionRunGate _detectionRuns = new();
    Task? _detectionTask;
    PairStatus? _cachedPairStatus;
    string _controllerId = "";
    bool _cachedHasCredential;
    bool _detected;

    // Set on success so MainWindow can auto-connect to the freshly set-up Pi.
    public string? NewDeviceIp { get; private set; }
    public string? NewDeviceHostname { get; private set; }
    public string? NewDeviceId { get; private set; }

    public WifiSetupWindow()
    {
        InitializeComponent();
        _wifi = new WifiProvisioner(_http);
        _pairing = new PairingClient(_http);
        // editable ComboBox has no TextChanged attribute — hook the routed event
        CmbSsid.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler((s, e) => Field_Changed(s, e)));
        Closing += (_, _) =>
        {
            _detectionRuns.Cancel();
            _lifetime.Cancel();
        };
        Closed += (_, _) => _detectionRuns.Dispose();
        Loaded += async (_, _) => await StartDetectionAsync();
    }

    void Window_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) Close();
    }

    void MarkDone(System.Windows.Controls.TextBlock head)
    {
        if (!head.Text.StartsWith("✓")) head.Text = "✓ " + head.Text;
        head.Foreground = (Brush)FindResource("Success");
    }

    async Task StartDetectionAsync()
    {
        await CancelActiveDetectionAsync();
        if (_lifetime.IsCancellationRequested ||
            !_detectionRuns.TryBegin(_lifetime.Token, out var runToken))
        {
            return;
        }

        var task = RunDetectionAsync(runToken);
        _detectionTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_detectionTask, task))
                _detectionTask = null;
        }
    }

    async Task RunDetectionAsync(CancellationToken runToken)
    {
        try
        {
            await DetectLoop(runToken);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ShowDetectionFailure(ex);
        }
        finally
        {
            _detectionRuns.Complete(runToken);
        }
    }

    async Task CancelActiveDetectionAsync()
    {
        _detectionRuns.Cancel();
        var active = _detectionTask;
        if (active is null) return;
        try { await active; }
        catch (OperationCanceledException) { }
    }

    async Task DetectLoop(CancellationToken cancellationToken)
    {
        var blocker = OnboardingPolicy.UsbSetupBlocker(Environment.OSVersion.Version);
        if (blocker is not null)
        {
            DetectStatus.Text = blocker;
            Detecting.Visibility = Visibility.Collapsed;
            BtnCancelDetect.Visibility = Visibility.Collapsed;
            return;
        }
        for (int i = 0; i < 60 && !_detected; i++)   // ~60s of polling
        {
            if (await _wifi.DetectAsync(PiUsbBase, cancellationToken))
            {
                await RefreshPairingStateAsync(cancellationToken);
                await LoadNearbyNetworks(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _detected = true;
                Detecting.Visibility = Visibility.Collapsed;
                BtnCancelDetect.Visibility = Visibility.Collapsed;
                DetectStatus.Text = "Pi found over USB.";
                MarkDone(Step1Head);
                Form.IsEnabled = true;
                CmbSsid.Focus();
                return;
            }
            DetectStatus.Text = i >= 15
                ? $"Still looking ({i}s)… make sure the cable is in the Pi's USB-C port and is a data cable (not charge-only)."
                : $"Looking for the Pi over USB… ({i}s)";
            await Task.Delay(1000, cancellationToken);
        }
        if (_detected) return;
        DetectStatus.Text = "No Pi found. Check the cable is a DATA USB-C cable and the Pi has booted, then click Retry.";
        Detecting.Visibility = Visibility.Collapsed;
        BtnCancelDetect.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = Visibility.Visible;   // let the user try again without reopening
    }

    async void CancelDetect_Click(object s, RoutedEventArgs e)
    {
        BtnCancelDetect.IsEnabled = false;
        await CancelActiveDetectionAsync();
        if (_lifetime.IsCancellationRequested) return;
        _detected = false;
        Detecting.Visibility = Visibility.Collapsed;
        BtnCancelDetect.Visibility = Visibility.Collapsed;
        BtnCancelDetect.IsEnabled = true;
        DetectStatus.Text = "Stopped looking. Click Retry when the Pi is plugged in.";
        BtnRetry.Visibility = Visibility.Visible;
    }

    async void Retry_Click(object s, RoutedEventArgs e)
    {
        BtnRetry.Visibility = Visibility.Collapsed;
        Detecting.Visibility = Visibility.Visible;
        BtnCancelDetect.Visibility = Visibility.Visible;
        DetectStatus.Text = "Looking for the Pi over USB…";
        _detected = false;
        await StartDetectionAsync();
    }

    void ShowDetectionFailure(Exception ex)
    {
        if (_lifetime.IsCancellationRequested) return;
        _detected = false;
        DetectStatus.Text = "The Pi was found, but setup status could not be read: " +
            ex.Message;
        Detecting.Visibility = Visibility.Collapsed;
        BtnCancelDetect.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = Visibility.Visible;
    }

    // Pre-fill the SSID picker from Windows' visible networks so the client can
    // pick by name instead of typing it exactly. Best-effort: failure -> empty
    // list, typing still works.
    async Task LoadNearbyNetworks(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", "wlan show networks")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // lines look like "SSID 1 : MyNetwork" (label is locale-dependent,
            // but the "SSID <n> :" shape holds on common locales; parse defensively)
            var ssids = output.Split('\n')
                .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\s*SSID\s+\d+\s*:\s*(.+?)\s*$"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();
            CmbSsid.ItemsSource = ssids;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { /* no list — the user can still type the name */ }
    }

    // Effective password from whichever field is visible (masked vs shown).
    string EffectivePassword => ChkShow.IsChecked == true ? TxtPassPlain.Text : TxtPass.Password;
    string Ssid => CmbSsid.Text.Trim();

    void ChkShow_Changed(object s, RoutedEventArgs e)
    {
        if (ChkShow.IsChecked == true)
        {
            TxtPassPlain.Text = TxtPass.Password;
            TxtPassPlain.Visibility = Visibility.Visible;
            TxtPass.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtPass.Password = TxtPassPlain.Text;
            TxtPass.Visibility = Visibility.Visible;
            TxtPassPlain.Visibility = Visibility.Collapsed;
        }
        Field_Changed(s, e);
    }

    void Field_Changed(object s, RoutedEventArgs e)
        => BtnConnect.IsEnabled = OnboardingPolicy.CanSubmit(
            Ssid,
            EffectivePassword,
            TxtRecoveryPin.Password,
            _cachedPairStatus,
            _controllerId,
            _cachedHasCredential);

    async Task RefreshPairingStateAsync(CancellationToken cancellationToken)
    {
        var status = await _pairing.GetStatusAsync(
            PiUsbBase,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var controllerId = _vault.Load().ControllerId;
        var hasCredential =
            status.Paired &&
            string.Equals(
                status.ControllerId,
                controllerId,
                StringComparison.Ordinal) &&
            _vault.TryGet(status.DeviceId) is not null;
        cancellationToken.ThrowIfCancellationRequested();
        _cachedPairStatus = status;
        _controllerId = controllerId;
        _cachedHasCredential = hasCredential;
        Field_Changed(this, new RoutedEventArgs());
    }

    async void Connect_Click(object s, RoutedEventArgs e)
    {
        Form.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        Result.Foreground = (Brush)FindResource("TextMuted");
        Result.Text = $"Sending your WiFi details to the Pi…";
        var cancellationToken = _lifetime.Token;
        try
        {
            await RefreshPairingStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var pairStatus = _cachedPairStatus
                ?? throw new InvalidDataException("The Pi returned no pairing status.");
            var controllerId = _controllerId;
            if (!OnboardingPolicy.CanSubmit(
                    Ssid,
                    EffectivePassword,
                    TxtRecoveryPin.Password,
                    pairStatus,
                    controllerId,
                    _cachedHasCredential))
            {
                Result.Foreground = (Brush)FindResource("Error");
                Result.Text = "Enter the Pi's 8-digit PIN before continuing.";
                Form.IsEnabled = true;
                return;
            }
            var replacementConfirmed = true;
            if (OnboardingPolicy.RequiresReplacement(pairStatus, controllerId))
            {
                var replace = MessageBox.Show(
                    this,
                    "Pair this Pi to this laptop?\n\n" +
                    "The previous laptop will lose access. Continue only if you are setting up a replacement store laptop.",
                    "Replace paired laptop",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                replacementConfirmed = replace == MessageBoxResult.Yes;
                cancellationToken.ThrowIfCancellationRequested();
                if (!OnboardingPolicy.CanProceedWithPairing(
                        pairStatus,
                        controllerId,
                        replacementConfirmed))
                {
                    Result.Text = "Setup cancelled. The Pi is still paired to the previous laptop.";
                    Form.IsEnabled = true;
                    return;
                }
            }

            string deviceId;
            if (OnboardingPolicy.CanReuseCredential(
                    pairStatus,
                    controllerId,
                    _cachedHasCredential))
            {
                // A prior Wi-Fi attempt may have failed after pairing. Reuse the
                // retained credential instead of consuming the PIN again.
                deviceId = pairStatus.DeviceId;
            }
            else
            {
                Result.Text = "Pairing this Pi to this laptop over USB…";
                var paired = await _pairing.PairAsync(
                    PiUsbBase,
                    TxtRecoveryPin.Password,
                    controllerId,
                    cancellationToken);
                OnboardingPolicy.ValidatePairResult(pairStatus, paired);
                _vault.Put(paired.DeviceId, paired.Secret);
                deviceId = paired.DeviceId;
                _cachedPairStatus =
                    new PairStatus(deviceId, true, controllerId);
                _cachedHasCredential = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Result.Text = "Sending your WiFi details to the paired Pi…";
            var r = await _wifi.ConnectAsync(
                PiUsbBase,
                Ssid,
                EffectivePassword,
                ControlContext(deviceId),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Result.Text = "Checking the Pi got online…";
            var wifiStatus = await _wifi.GetStatusAsync(
                PiUsbBase,
                cancellationToken);
            if (!wifiStatus.Connected)
            {
                await Task.Delay(3000, cancellationToken);
                wifiStatus = await _wifi.GetStatusAsync(
                    PiUsbBase,
                    cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var ok = wifiStatus.Connected;
            if (ok)
            {
                var status = await _http.GetFromJsonAsync<StatusInfo>(
                    PiUsbBase + "/api/status",
                    cancellationToken)
                    ?? throw new InvalidDataException("The Pi returned an empty status.");
                if (!string.Equals(status.DeviceId, deviceId, StringComparison.Ordinal))
                    throw new InvalidDataException("The Pi identity changed during setup.");
                var lanIp = wifiStatus.Ip ?? r.Ip;
                if (string.IsNullOrWhiteSpace(lanIp))
                    throw new InvalidDataException("The Pi connected but did not report its WiFi address.");

                var piName = string.IsNullOrWhiteSpace(status.Name) ? "New Pi" : status.Name;
                var store = new PiSignage.Signage.DeviceStore();
                var list = PiSignage.Signage.DeviceStore.Upsert(store.Load(),
                    new PiSignage.Signage.SavedDevice {
                        DeviceId = deviceId,
                        Name = piName,
                        Hostname = piName,
                        Ip = lanIp,
                        Port = new Uri(PiUsbBase).Port,
                    });
                store.Save(list);
                cancellationToken.ThrowIfCancellationRequested();
                NewDeviceIp = lanIp;
                NewDeviceHostname = piName;
                NewDeviceId = deviceId;
                MarkDone(Step2Head);
                MarkDone(Step3Head);
                Result.Foreground = (Brush)FindResource("Success");
                Result.Text = $"✓ Your Pi is online at {lanIp}. You can unplug the cable — it's in your device list now.";
            }
            else
            {
                Result.Foreground = (Brush)FindResource("Error");
                Result.Text = "Couldn't connect: " +
                    (r.Error ?? "check the network name and password") +
                    " Pairing is saved — correct the WiFi details and try again.";
                Form.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Result.Foreground = (Brush)FindResource("Error");
            Result.Text = "Setup failed: " + ex.Message +
                " If pairing succeeded, it is saved and the WiFi step can be retried.";
            Form.IsEnabled = true;
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
                Working.Visibility = Visibility.Collapsed;
        }
    }

    ControlContext ControlContext(string deviceId)
    {
        var credential = _vault.TryGet(deviceId)
            ?? throw new KeyNotFoundException(
                $"No controller credential exists for device '{deviceId}'.");
        var stableDeviceId = deviceId;
        return new ControlContext(
            stableDeviceId,
            _vault.Load().ControllerId,
            credential.Secret,
            () => _vault.TakeNextCounter(stableDeviceId),
            _vault.Path);
    }
}
