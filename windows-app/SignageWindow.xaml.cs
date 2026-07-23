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
        Loaded += (_, _) =>
        {
            this.ExcludeFromCapture();   // our window never appears in the screenshot
            CmbAgent.ItemsSource = new PiSignage.Signage.DeviceStore().Load();  // saved Pis to pick from
            HydrateFromDevice();         // restore what's already on the device
        };
        // WPF sometimes sends the owner behind other apps when an owned window
        // closes — pull the main window back to the front.
        Closed += (_, _) => Owner?.Activate();
    }

    // On (re)open, pull the device's current boards + timer so the app reflects
    // what the TV is still showing after the window was closed.
    void SetBusy(bool on) => Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    async void HydrateFromDevice()
    {
        SetBusy(true);
        Status.Text = "Loading from device…";
        try
        {
            var snap = await _client.GetDashboardAsync(Base);
            if (snap is null) { Status.Text = "Nothing on the device yet"; return; }

            _state.Boards.Clear();
            foreach (var kv in snap.view_data.boards) _state.Boards[kv.Key] = kv.Value;

            if (snap.timer.state == "running" && snap.timer.endsAt is { } ends)
            {
                _timer.RestoreRunning(snap.timer.remaining ?? 0, snap.timer.round, snap.timer.label);
                _endsAtLocal = DateTimeOffset.FromUnixTimeMilliseconds(ends).UtcDateTime;
                if (snap.timer.round is { } rd) TxtRound.Text = rd.ToString();
            }
            else { _timer.Stop(); _endsAtLocal = null; }
            UpdateClock();

            await PreviewSlot();   // show the currently-selected slot's board

            int n = _state.Boards.Count;
            Status.Text = (n > 0 || _endsAtLocal is not null)
                ? $"Restored from device ({n} board{(n == 1 ? "" : "s")}{(_endsAtLocal is not null ? ", timer running" : "")})"
                : "Nothing on the device yet";
        }
        catch { Status.Text = "Device not reachable — nothing restored"; }
        finally { SetBusy(false); }
    }

    async Task PreviewSlot()
    {
        if (_state.Boards.TryGetValue(Slot, out var path))
        {
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PreviewBusy.Visibility = Visibility.Visible;     // spinner while fetching this slot's image
            try
            {
                var fetch = _client.GetMediaAsync(Base, path);
                await Task.WhenAll(fetch, Task.Delay(300));  // keep the spinner visible even on an instant (localhost) fetch
                ShowPreview(await fetch);
                return;
            }
            catch { }
            finally { PreviewBusy.Visibility = Visibility.Collapsed; }
        }
        Preview.Source = null;
        PreviewEmpty.Visibility = Visibility.Visible;
    }

    async void Slot_Changed(object s, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await PreviewSlot();   // show the newly-selected slot's board
    }

    void UpdateClock()
    {
        if (_endsAtLocal is not { } end) { ClockDisplay.Text = "—:—"; return; }
        var rem = (int)Math.Round((end - DateTime.UtcNow).TotalSeconds);
        ClockDisplay.Text = rem <= 0 ? "TIME" : $"{rem / 60:00}:{rem % 60:00}";
    }

    string Slot => ((ComboBoxItem)CmbSlot.SelectedItem).Content!.ToString()!;
    // Target from the picked saved device (Ip:8080), else the typed host[:port].
    string Base => CmbAgent.SelectedItem is PiSignage.Signage.SavedDevice d
        ? "http://" + d.Ip + ":8080"
        : "http://" + CmbAgent.Text.Trim();

    string TargetLabel => CmbAgent.SelectedItem is PiSignage.Signage.SavedDevice d ? d.Name : CmbAgent.Text.Trim();

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
        // No hide/opacity/delay: the window is excluded from capture, so we grab
        // instantly and the app stays put (no blink, no z-order change).
        SetBusy(true);
        try
        {
            var png = ScreenCapture.CaptureRegion(r.x, r.y, r.w, r.h);
            ShowPreview(png);            // let the operator see exactly what was grabbed
            var name = $"{Slot}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";   // globally-unique -> cache-bust
            Status.Text = $"Pushing {Slot}…";
            var path = await _client.UploadMediaAsync(Base, name, png);
            _state.Boards[Slot] = path;
            await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer));
            Status.Text = $"Pushed {Slot} → {TargetLabel}";
        }
        catch (Exception ex) { Status.Text = "Push failed: " + ex.Message; }
        finally { SetBusy(false); }
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
