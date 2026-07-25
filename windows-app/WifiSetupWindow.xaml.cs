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
    bool _detected;
    bool _cancelDetect;

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
        Loaded += async (_, _) => await DetectLoop();
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

    async Task DetectLoop()
    {
        _cancelDetect = false;
        for (int i = 0; i < 60 && !_detected && !_cancelDetect; i++)   // ~60s of polling
        {
            if (await _wifi.DetectAsync(PiUsbBase))
            {
                _detected = true;
                Detecting.Visibility = Visibility.Collapsed;
                BtnCancelDetect.Visibility = Visibility.Collapsed;
                DetectStatus.Text = "Pi found over USB.";
                MarkDone(Step1Head);
                Form.IsEnabled = true;
                await LoadNearbyNetworks();
                CmbSsid.Focus();
                return;
            }
            DetectStatus.Text = i >= 15
                ? $"Still looking ({i}s)… make sure the cable is in the Pi's USB-C port and is a data cable (not charge-only)."
                : $"Looking for the Pi over USB… ({i}s)";
            await Task.Delay(1000);
        }
        if (_detected || _cancelDetect) return;
        DetectStatus.Text = "No Pi found. Check the cable is a DATA USB-C cable and the Pi has booted, then click Retry.";
        Detecting.Visibility = Visibility.Collapsed;
        BtnCancelDetect.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = Visibility.Visible;   // let the user try again without reopening
    }

    void CancelDetect_Click(object s, RoutedEventArgs e)
    {
        _cancelDetect = true;
        Detecting.Visibility = Visibility.Collapsed;
        BtnCancelDetect.Visibility = Visibility.Collapsed;
        DetectStatus.Text = "Stopped looking. Click Retry when the Pi is plugged in.";
        BtnRetry.Visibility = Visibility.Visible;
    }

    async void Retry_Click(object s, RoutedEventArgs e)
    {
        BtnRetry.Visibility = Visibility.Collapsed;
        Detecting.Visibility = Visibility.Visible;
        BtnCancelDetect.Visibility = Visibility.Visible;
        DetectStatus.Text = "Looking for the Pi over USB…";
        await DetectLoop();
    }

    // Pre-fill the SSID picker from Windows' visible networks so the client can
    // pick by name instead of typing it exactly. Best-effort: failure -> empty
    // list, typing still works.
    async Task LoadNearbyNetworks()
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
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

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
        => BtnConnect.IsEnabled =
            TxtRecoveryPin.Password.Length == 8 &&
            TxtRecoveryPin.Password.All(char.IsDigit) &&
            Ssid.Length > 0 &&
            EffectivePassword.Length > 0;

    async void Connect_Click(object s, RoutedEventArgs e)
    {
        Form.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        Result.Foreground = (Brush)FindResource("TextMuted");
        Result.Text = $"Sending your WiFi details to the Pi…";
        try
        {
            var vaultData = _vault.Load();
            var controllerId = vaultData.ControllerId;
            var pairStatus = await _pairing.GetStatusAsync(PiUsbBase);
            if (pairStatus.Paired &&
                !string.Equals(
                    pairStatus.ControllerId,
                    controllerId,
                    StringComparison.Ordinal))
            {
                var replace = MessageBox.Show(
                    this,
                    "Pair this Pi to this laptop?\n\n" +
                    "The previous laptop will lose access. Continue only if you are setting up a replacement store laptop.",
                    "Replace paired laptop",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (replace != MessageBoxResult.Yes)
                {
                    Result.Text = "Setup cancelled. The Pi is still paired to the previous laptop.";
                    Form.IsEnabled = true;
                    return;
                }
            }

            string deviceId;
            if (pairStatus.Paired &&
                string.Equals(pairStatus.ControllerId, controllerId, StringComparison.Ordinal) &&
                _vault.TryGet(pairStatus.DeviceId) is not null)
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
                    controllerId);
                if (!string.Equals(paired.ControllerId, controllerId, StringComparison.Ordinal))
                    throw new InvalidDataException("The Pi returned the wrong controller identity.");
                _vault.Put(paired.DeviceId, paired.Secret);
                deviceId = paired.DeviceId;
            }

            Result.Text = "Sending your WiFi details to the paired Pi…";
            var r = await _wifi.ConnectAsync(
                PiUsbBase,
                Ssid,
                EffectivePassword,
                _vault,
                deviceId);

            Result.Text = "Checking the Pi got online…";
            var wifiStatus = await _wifi.GetStatusAsync(PiUsbBase);
            if (!wifiStatus.Connected)
            {
                await Task.Delay(3000);
                wifiStatus = await _wifi.GetStatusAsync(PiUsbBase);
            }
            var ok = wifiStatus.Connected;
            if (ok)
            {
                var status = await _http.GetFromJsonAsync<StatusInfo>(
                    PiUsbBase + "/api/status")
                    ?? throw new InvalidDataException("The Pi returned an empty status.");
                if (!string.Equals(status.DeviceId, deviceId, StringComparison.Ordinal))
                    throw new InvalidDataException("The Pi identity changed during setup.");
                var lanIp = wifiStatus.Ip ?? r.Ip;
                if (string.IsNullOrWhiteSpace(lanIp))
                    throw new InvalidDataException("The Pi connected but did not report its WiFi address.");

                MarkDone(Step2Head);
                MarkDone(Step3Head);
                Result.Foreground = (Brush)FindResource("Success");
                Result.Text = $"✓ Your Pi is online at {lanIp}. You can unplug the cable — it's in your device list now.";

                var piName = string.IsNullOrWhiteSpace(status.Name) ? "New Pi" : status.Name;
                try
                {
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
                    NewDeviceIp = lanIp;
                    NewDeviceHostname = piName;
                    NewDeviceId = deviceId;
                }
                catch { /* WiFi connect already succeeded; worst case the user runs Find my Pi */ }
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
        catch (Exception ex)
        {
            Result.Foreground = (Brush)FindResource("Error");
            Result.Text = "Setup failed: " + ex.Message +
                " If pairing succeeded, it is saved and the WiFi step can be retried.";
            Form.IsEnabled = true;
        }
        finally { Working.Visibility = Visibility.Collapsed; }
    }
}
