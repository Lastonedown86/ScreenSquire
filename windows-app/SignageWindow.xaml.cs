using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class SignageWindow : Window
{
    readonly DashboardState _state = new();
    readonly RoundTimer _timer = new();
    readonly PushClient _client = new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    (int x, int y, int w, int h)? _lastRegion;
    int _counter;

    public SignageWindow() => InitializeComponent();

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
        Hide();
        try
        {
            await Task.Delay(200);   // let the desktop repaint with our window gone before grabbing
            var png = ScreenCapture.CaptureRegion(r.x, r.y, r.w, r.h);
            var name = $"{Slot}-{++_counter}.png";                 // unique -> cache-bust
            var path = await _client.UploadMediaAsync(Base, name, png);
            _state.Boards[Slot] = path;
            await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer));
            Status.Text = $"Pushed {Slot} → {TxtAgent.Text}";
        }
        catch (Exception ex) { Status.Text = "Push failed: " + ex.Message; }
        finally { Show(); }
    }

    async void StartTimer_Click(object s, RoutedEventArgs e)
    {
        int min = int.TryParse(TxtMinutes.Text, out var m) ? m : 25;
        int round = int.TryParse(TxtRound.Text, out var rd) ? rd : 1;
        _timer.Start(min, $"Round {round}", round);
        await Post("Timer started");
    }

    async void StopTimer_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        await Post("Timer stopped");
    }

    async Task Post(string msg)
    {
        try { await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer)); Status.Text = msg; }
        catch (Exception ex) { Status.Text = "Push failed: " + ex.Message; }
    }
}
