using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class SignageWindow : Window
{
    readonly DashboardState _state = new();
    readonly RoundTimer _timer = new();
    readonly PushClient _client = new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    (int x, int y, int w, int h)? _lastRegion;
    DateTime? _endsAtLocal;                 // app-side countdown target (mirrors the TV)
    readonly DispatcherTimer _clockTick;

    public SignageWindow()
    {
        InitializeComponent();
        _clockTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTick.Tick += (_, _) => UpdateClock();
        _clockTick.Start();
    }

    void UpdateClock()
    {
        if (_endsAtLocal is not { } end) { ClockDisplay.Text = "—:—"; return; }
        var rem = (int)Math.Round((end - DateTime.UtcNow).TotalSeconds);
        ClockDisplay.Text = rem <= 0 ? "TIME" : $"{rem / 60:00}:{rem % 60:00}";
    }

    string Slot => ((ComboBoxItem)CmbSlot.SelectedItem).Content!.ToString()!;
    string Base => "http://" + TxtAgent.Text.Trim();

    async void Capture_Click(object s, RoutedEventArgs e)
    {
        var sel = new RegionSelectorWindow { Owner = this };
        var ok = sel.ShowDialog();
        if (ok != true || sel.Result is null) return;
        _lastRegion = sel.Result;
        BtnRecapture.IsEnabled = true;
        await CaptureAndPush(_lastRegion.Value);
    }

    async void Recapture_Click(object s, RoutedEventArgs e)
    {
        if (_lastRegion is { } r) await CaptureAndPush(r);
    }

    async Task CaptureAndPush((int x, int y, int w, int h) r)
    {
        try
        {
            // Go transparent (not Hide()) so we vanish from the screenshot without
            // the whole window blinking off-screen; reappear right after the grab.
            Opacity = 0;
            byte[] png;
            try
            {
                await Task.Delay(150);   // let the compositor drop our window before grabbing
                png = ScreenCapture.CaptureRegion(r.x, r.y, r.w, r.h);
            }
            finally { Opacity = 1; }      // always reappear, even if the grab throws

            ShowPreview(png);            // let the operator see exactly what was grabbed
            var name = $"{Slot}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";   // globally-unique -> cache-bust
            var path = await _client.UploadMediaAsync(Base, name, png);
            _state.Boards[Slot] = path;
            await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer));
            Status.Text = $"Pushed {Slot} → {TxtAgent.Text}";
        }
        catch (Exception ex) { Opacity = 1; Status.Text = "Push failed: " + ex.Message; }
    }

    void ShowPreview(byte[] png)
    {
        var img = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;   // decode now so the stream can close
            img.StreamSource = ms;
            img.EndInit();
        }
        img.Freeze();
        Preview.Source = img;
        PreviewEmpty.Visibility = Visibility.Collapsed;
    }

    async void StartTimer_Click(object s, RoutedEventArgs e)
    {
        int min = int.TryParse(TxtMinutes.Text, out var m) ? m : 25;
        int round = int.TryParse(TxtRound.Text, out var rd) ? rd : 1;
        _timer.Start(min, $"Round {round}", round);
        _endsAtLocal = DateTime.UtcNow.AddSeconds(min * 60);
        UpdateClock();
        await Post("Timer started");
    }

    async void StopTimer_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        _endsAtLocal = null;
        UpdateClock();
        await Post("Timer stopped");
    }

    async Task Post(string msg)
    {
        try { await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer)); Status.Text = msg; }
        catch (Exception ex) { Status.Text = "Push failed: " + ex.Message; }
    }
}
