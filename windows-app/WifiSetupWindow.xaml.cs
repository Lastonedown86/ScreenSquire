using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Media;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class WifiSetupWindow : Window
{
    const string PiUsbBase = "http://10.55.0.1:8080";   // fixed USB-gadget address
    readonly WifiProvisioner _wifi = new(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
    bool _detected;

    public WifiSetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await DetectLoop();
    }

    async Task DetectLoop()
    {
        for (int i = 0; i < 60 && !_detected; i++)   // ~60s of polling
        {
            if (await _wifi.DetectAsync(PiUsbBase))
            {
                _detected = true;
                Detecting.Visibility = Visibility.Collapsed;
                DetectStatus.Text = "Pi found over USB.";
                Form.IsEnabled = true;
                return;
            }
            await Task.Delay(1000);
        }
        DetectStatus.Text = "No Pi found. Check the cable is a DATA USB-C cable and wait for the Pi to boot.";
    }

    void Field_Changed(object s, RoutedEventArgs e)
        => BtnConnect.IsEnabled = TxtSsid.Text.Trim().Length > 0 && TxtPass.Password.Length > 0;

    async void Connect_Click(object s, RoutedEventArgs e)
    {
        Form.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        Result.Foreground = Brushes.Gray;
        Result.Text = $"Connecting the Pi to {TxtSsid.Text.Trim()}…";
        try
        {
            var r = await _wifi.ConnectAsync(PiUsbBase, TxtSsid.Text.Trim(), TxtPass.Password);
            bool ok = r.Ok && r.Connected;
            if (!ok)   // one confirming re-check in case connect returned before DHCP settled
            {
                await Task.Delay(3000);
                var st = await _wifi.GetStatusAsync(PiUsbBase);
                ok = st.Connected;
                if (ok) r = new WifiResult { Ok = true, Connected = true, Ip = st.Ip };
            }
            if (ok)
            {
                Result.Foreground = Brushes.Green;
                Result.Text = $"Connected — this Pi is on {TxtSsid.Text.Trim()} at {r.Ip}. You can unplug the USB cable.";

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
            }
            else
            {
                Result.Foreground = Brushes.Firebrick;
                Result.Text = "Couldn't connect: " + (r.Error ?? "check the network name and password") + "  — Try again.";
                Form.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Result.Foreground = Brushes.Firebrick;
            Result.Text = "Setup failed: " + ex.Message;
            Form.IsEnabled = true;
        }
        finally { Working.Visibility = Visibility.Collapsed; }
    }
}
