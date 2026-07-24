using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class WifiSetupWindow : Window
{
    const string PiUsbBase = "http://10.55.0.1:8080";   // fixed USB-gadget address
    readonly WifiProvisioner _wifi = new(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
    bool _detected;
    bool _cancelDetect;

    // Set on success so MainWindow can auto-connect to the freshly set-up Pi.
    public string? NewDeviceIp { get; private set; }
    public string? NewDeviceHostname { get; private set; }

    public WifiSetupWindow()
    {
        InitializeComponent();
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
        => BtnConnect.IsEnabled = Ssid.Length > 0 && EffectivePassword.Length > 0;

    async void Connect_Click(object s, RoutedEventArgs e)
    {
        Form.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        Result.Foreground = (Brush)FindResource("TextMuted");
        Result.Text = $"Sending your WiFi details to the Pi…";
        try
        {
            var r = await _wifi.ConnectAsync(PiUsbBase, Ssid, EffectivePassword);
            bool ok = r.Ok && r.Connected;
            if (!ok)   // one confirming re-check in case connect returned before DHCP settled
            {
                Result.Text = "Checking the Pi got online…";
                await Task.Delay(3000);
                var st = await _wifi.GetStatusAsync(PiUsbBase);
                ok = st.Connected;
                if (ok) r = new WifiResult { Ok = true, Connected = true, Ip = st.Ip };
            }
            if (ok)
            {
                MarkDone(Step2Head);
                MarkDone(Step3Head);
                Result.Foreground = (Brush)FindResource("Success");
                Result.Text = $"✓ Your Pi is online at {r.Ip}. You can unplug the cable — it's in your device list now.";

                // Always save the device — fall back to a placeholder name if the
                // status call fails, so the new Pi never silently goes missing.
                var piName = "New Pi";
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var nameDoc = await http.GetStringAsync(PiUsbBase.TrimEnd('/') + "/api/status");
                    using var doc = System.Text.Json.JsonDocument.Parse(nameDoc);
                    piName = doc.RootElement.GetProperty("name").GetString() ?? piName;
                }
                catch { /* keep placeholder */ }
                try
                {
                    var store = new PiSignage.Signage.DeviceStore();
                    var list = PiSignage.Signage.DeviceStore.Upsert(store.Load(),
                        new PiSignage.Signage.SavedDevice { Name = piName, Hostname = piName, Ip = r.Ip! });
                    store.Save(list);
                    NewDeviceIp = r.Ip;
                    NewDeviceHostname = piName;
                }
                catch { /* WiFi connect already succeeded; worst case the user runs Find my Pi */ }
            }
            else
            {
                Result.Foreground = (Brush)FindResource("Error");
                Result.Text = "Couldn't connect: " + (r.Error ?? "check the network name and password") + "  — Try again.";
                Form.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Result.Foreground = (Brush)FindResource("Error");
            Result.Text = "Setup failed: " + ex.Message;
            Form.IsEnabled = true;
        }
        finally { Working.Visibility = Visibility.Collapsed; }
    }
}
