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

    void Window_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) Close();
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
        DetectStatus.Text = "No Pi found. Check the cable is a DATA USB-C cable and the Pi has booted.";
        Detecting.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = Visibility.Visible;   // let the user try again without reopening
    }

    async void Retry_Click(object s, RoutedEventArgs e)
    {
        BtnRetry.Visibility = Visibility.Collapsed;
        Detecting.Visibility = Visibility.Visible;
        DetectStatus.Text = "Looking for the Pi over USB…";
        await DetectLoop();
    }

    // Effective password from whichever field is visible (masked vs shown).
    string EffectivePassword => ChkShow.IsChecked == true ? TxtPassPlain.Text : TxtPass.Password;

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
        => BtnConnect.IsEnabled = TxtSsid.Text.Trim().Length > 0 && EffectivePassword.Length > 0;

    async void Connect_Click(object s, RoutedEventArgs e)
    {
        Form.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        Result.Foreground = (Brush)FindResource("TextMuted");
        Result.Text = $"Connecting the Pi to {TxtSsid.Text.Trim()}…";
        try
        {
            var r = await _wifi.ConnectAsync(PiUsbBase, TxtSsid.Text.Trim(), EffectivePassword);
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
                Result.Foreground = (Brush)FindResource("Success");
                Result.Text = $"Connected — this Pi is on {TxtSsid.Text.Trim()} at {r.Ip}. You can unplug the USB cable.";

                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
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
