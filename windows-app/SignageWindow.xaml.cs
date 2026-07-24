using System.IO;
using System.Linq;
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
        App.TrackPlacement(this, "Signage");
        _clockTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTick.Tick += (_, _) => UpdateClock();
        _clockTick.Start();
        Loaded += (_, _) =>
        {
            this.ExcludeFromCapture();   // our window never appears in the screenshot
            var saved = new PiSignage.Signage.DeviceStore().Load();
            CmbAgent.ItemsSource = saved;  // saved Pis to pick from
            RestoreSession(saved);
            HydrateFromDevice();         // restore what's already on the device
        };
        Closing += (_, _) => SaveSession();
        // WPF sometimes sends the owner behind other apps when an owned window
        // closes — pull the main window back to the front.
        Closed += (_, _) => Owner?.Activate();
    }

    // Called by MainWindow after a rename so the Target Pi list shows the new
    // name immediately, keeping the same Pi selected.
    public void RefreshDevices()
    {
        var keepHost = (CmbAgent.SelectedItem as PiSignage.Signage.SavedDevice)?.Hostname;
        var saved = new PiSignage.Signage.DeviceStore().Load();
        CmbAgent.ItemsSource = saved;
        if (keepHost != null)
            CmbAgent.SelectedItem = saved.FirstOrDefault(d =>
                string.Equals(d.Hostname, keepHost, StringComparison.OrdinalIgnoreCase));
    }

    // Reopen where the operator left off: same target Pi, timer default, and
    // capture region per slot (so Re-capture works without re-dragging).
    void RestoreSession(System.Collections.Generic.List<PiSignage.Signage.SavedDevice> saved)
    {
        // last-used target > the Pi the main window is connected to > first saved Pi
        var target = App.Settings.SignageTarget;
        if (string.IsNullOrWhiteSpace(target)) target = App.Settings.LastDeviceHostname;
        var dev = saved.FirstOrDefault(d => string.Equals(d.Hostname, target, StringComparison.OrdinalIgnoreCase))
                  ?? saved.FirstOrDefault();
        if (dev != null) CmbAgent.SelectedItem = dev;
        else if (!string.IsNullOrWhiteSpace(target)) CmbAgent.Text = target;
        TxtMinutes.Text = App.Settings.TimerMinutes.ToString();
        foreach (var min in App.Settings.TimerPresets.Distinct().Where(m => m > 0))
        {
            var b = new Button
            {
                Content = $"{min} min",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = $"Start a {min}-minute round with one click",
            };
            b.Click += (_, e) => { TxtMinutes.Text = min.ToString(); StartTimer_Click(b, e); };
            Presets.Children.Add(b);
        }
        RestoreRegionForSlot();
    }

    void RestoreRegionForSlot()
    {
        if (App.Settings.Regions.TryGetValue(Slot, out var r))
        {
            _lastRegion = (r.X, r.Y, r.W, r.H);
            BtnRecapture.IsEnabled = true;
        }
        else
        {
            _lastRegion = null;
            BtnRecapture.IsEnabled = false;
        }
    }

    void SaveSession()
    {
        App.Settings.SignageTarget = CmbAgent.SelectedItem is PiSignage.Signage.SavedDevice d
            ? d.Hostname : CmbAgent.Text.Trim();
        App.SaveSettings();
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
        if (!IsLoaded) return;
        RestoreRegionForSlot();   // each slot remembers its own capture region
        await PreviewSlot();      // show the newly-selected slot's board
    }

    void NumberOnly_PreviewTextInput(object s, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    void Window_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) Close();
    }

    void TimerField_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) StartTimer_Click(s, e);
    }

    void UpdateClock()
    {
        if (_endsAtLocal is not { } end)
        {
            ClockDisplay.Text = "—:—";
            ClockDisplay.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            TimerInfo.Text = "No round running";
            return;
        }
        var rem = (int)Math.Round((end - DateTime.UtcNow).TotalSeconds);
        var round = _timer.Round is { } r ? $"Round {r}" : "Round";
        if (rem <= 0)
        {
            ClockDisplay.Text = "TIME";
            ClockDisplay.Foreground = (System.Windows.Media.Brush)FindResource("Error");
            TimerInfo.Text = $"{round} — time's up (click Stop to clear)";
        }
        else
        {
            ClockDisplay.Text = $"{rem / 60:00}:{rem % 60:00}";
            ClockDisplay.Foreground = SystemColors.ControlTextBrush;
            TimerInfo.Text = _timerEditPending
                ? $"{round} still running — click Start to restart with the new numbers"
                : $"{round} — ends at {end.ToLocalTime():h:mm tt} on the TV";
        }
    }

    string Slot => ((ComboBoxItem)CmbSlot.SelectedItem).Content!.ToString()!;
    // Target from the picked saved device (Ip:8080), else the typed host[:port].
    string Base => CmbAgent.SelectedItem is PiSignage.Signage.SavedDevice d
        ? "http://" + d.Ip + ":8080"
        : "http://" + CmbAgent.Text.Trim();

    string TargetLabel => CmbAgent.SelectedItem is PiSignage.Signage.SavedDevice d ? d.Name : CmbAgent.Text.Trim();

    async void Capture_Click(object s, RoutedEventArgs e)
    {
        var sel = new RegionSelectorWindow
        {
            Owner = this,
            Instruction = $"Drag a box around the {Slot} — Esc to cancel",
        };
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
            App.Settings.Regions[Slot] = new PiSignage.Signage.RegionRect { X = r.x, Y = r.y, W = r.w, H = r.h };
            App.SaveSettings();
        }
        catch (Exception ex)
        {
            Status.Text = "Push failed: " + ex.Message;
            Toaster.Show("Couldn't send the capture to the TV: " + ex.Message, ToastKind.Error);
        }
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

    bool _timerEditPending;   // minutes/round changed while a round is running

    void TimerField_Changed(object s, TextChangedEventArgs e)
    {
        if (!IsLoaded || _endsAtLocal is null) return;
        if (!((TextBox)s).IsKeyboardFocused) return;   // ignore programmatic updates
        _timerEditPending = true;
        UpdateClock();
    }

    async void StartTimer_Click(object s, RoutedEventArgs e)
    {
        int min = int.TryParse(TxtMinutes.Text, out var m) ? m : 25;
        int round = int.TryParse(TxtRound.Text, out var rd) ? rd : 1;
        _timer.Start(min, $"Round {round}", round);
        _endsAtLocal = DateTime.UtcNow.AddSeconds(min * 60);
        _timerEditPending = false;
        UpdateClock();
        App.Settings.TimerMinutes = min;
        App.SaveSettings();
        BtnStart.IsEnabled = BtnStop.IsEnabled = false;
        try
        {
            if (await Post($"Round {round} started"))
                Toaster.Show($"Round {round} started — {min} minutes on the TV clock.", ToastKind.Success);
        }
        finally { BtnStart.IsEnabled = BtnStop.IsEnabled = true; }
    }

    async void StopTimer_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        _endsAtLocal = null;
        _timerEditPending = false;
        UpdateClock();
        BtnStart.IsEnabled = BtnStop.IsEnabled = false;
        try
        {
            if (await Post("Timer stopped"))
                Toaster.Show("Timer stopped — the TV clock is cleared.", ToastKind.Success);
        }
        finally { BtnStart.IsEnabled = BtnStop.IsEnabled = true; }
    }

    // Pin the board on the TV (show-now with no end time). The running timer
    // rides on top via the kiosk's corner overlay, so nothing rotates.
    async void PinBoardToTv_Click(object s, RoutedEventArgs e)
    {
        try
        {
            using var api = ApiFromBase();
            await api.ShowNowAsync(new ShowNowRequest { Type = "url", Source = BoardPageUrl(Slot), Duration = null });
            Toaster.Show($"The {Slot} are pinned on the TV — the timer floats on top while a round runs. Click 'Back to playlist' when the event is over.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't pin it on the TV: " + ex.Message, ToastKind.Error);
        }
    }

    async void BackToPlaylist_Click(object s, RoutedEventArgs e)
    {
        try
        {
            using var api = ApiFromBase();
            await api.ClearShowNowAsync();
            Toaster.Show("The TV is back on its normal playlist.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't switch the TV back: " + ex.Message, ToastKind.Error);
        }
    }

    // The kiosk browser runs ON the Pi, so it reaches its own agent via localhost.
    static string BoardPageUrl(string slot) => $"http://localhost:8080/dashboard?view=board&name={slot}";

    ApiClient ApiFromBase()
    {
        var u = new Uri(Base);
        return new ApiClient(u.Host, u.Port);
    }


    async Task<bool> Post(string msg)
    {
        try
        {
            await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer));
            Status.Text = msg;
            return true;
        }
        catch (Exception ex)
        {
            Status.Text = "Push failed: " + ex.Message;
            Toaster.Show("Couldn't reach the Pi — the TV was not updated: " + ex.Message, ToastKind.Error);
            return false;
        }
    }
}
