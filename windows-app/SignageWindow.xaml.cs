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

    sealed class TvChoice
    {
        public PiSignage.Signage.SavedDevice Device { get; init; } = null!;
        public bool Checked { get; set; }
    }
    List<TvChoice> _tvs = new();

    IEnumerable<PushTarget> Targets => _tvs.Where(t => t.Checked)
        .Select(t => new PushTarget(t.Device.Name, $"http://{t.Device.Ip}:8080"));

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
            RestoreSession(saved);
            HydrateFromDevice();         // restore what's already on the device
        };
        Closing += (_, _) => SaveSession();
        // WPF sometimes sends the owner behind other apps when an owned window
        // closes — pull the main window back to the front.
        Closed += (_, _) => Owner?.Activate();
    }

    // Called by MainWindow after a rename so the TV list shows the new
    // name immediately, keeping the same TVs ticked.
    public void RefreshDevices()
    {
        var keep = new HashSet<string>(_tvs.Where(t => t.Checked).Select(t => t.Device.Hostname),
                                       StringComparer.OrdinalIgnoreCase);
        var saved = new PiSignage.Signage.DeviceStore().Load();
        _tvs = saved.Select(d => new TvChoice { Device = d, Checked = keep.Contains(d.Hostname) }).ToList();
        TvList.ItemsSource = _tvs;
        UpdateActionButtons();
    }

    // Reopen where the operator left off: same ticked TVs, timer default, and
    // capture region per slot (so Re-capture works without re-dragging).
    void RestoreSession(System.Collections.Generic.List<PiSignage.Signage.SavedDevice> saved)
    {
        var wanted = new HashSet<string>(App.Settings.SignageTargets, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 && !string.IsNullOrWhiteSpace(App.Settings.LastDeviceHostname))
            wanted.Add(App.Settings.LastDeviceHostname!);
        _tvs = saved.Select(d => new TvChoice { Device = d, Checked = wanted.Contains(d.Hostname) }).ToList();
        if (_tvs.Count > 0 && !_tvs.Any(t => t.Checked)) _tvs[0].Checked = true;  // sane default: first TV
        TvList.ItemsSource = _tvs;
        UpdateActionButtons();
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
            _lastRegion = (r.X, r.Y, r.W, r.H);
        else
            _lastRegion = null;
        UpdateActionButtons();   // single gate: recomputes BtnRecapture from Targets.Any() && _lastRegion
    }

    void SaveSession()
    {
        App.Settings.SignageTargets = _tvs.Where(t => t.Checked).Select(t => t.Device.Hostname).ToList();
        App.SaveSettings();
    }

    // On (re)open, pull the device's current boards + timer so the app reflects
    // what the TV is still showing after the window was closed.
    void SetBusy(bool on) => Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    async void HydrateFromDevice()
    {
        var first = Targets.FirstOrDefault();
        if (first is null) { Status.Text = "Tick at least one TV above."; return; }
        SetBusy(true);
        Status.Text = "Loading from " + first.Name + "…";
        try
        {
            var snap = await _client.GetDashboardAsync(first.BaseUrl);
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
        var first = Targets.FirstOrDefault();
        if (first is not null && _state.Boards.TryGetValue(Slot, out var path))
        {
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PreviewBusy.Visibility = Visibility.Visible;     // spinner while fetching this slot's image
            try
            {
                var fetch = _client.GetMediaAsync(first.BaseUrl, path);
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

    void Tv_CheckChanged(object s, RoutedEventArgs e) => UpdateActionButtons();

    void UpdateActionButtons()
    {
        bool any = Targets.Any();
        NoTvHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        BtnStart.IsEnabled = BtnStop.IsEnabled = any;
        BtnRecapture.IsEnabled = any && _lastRegion is not null;
        BtnCapture.IsEnabled = BtnPin.IsEnabled = BtnBack.IsEnabled = any;
    }

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
        UpdateActionButtons();   // single gate: recomputes BtnRecapture from Targets.Any() && _lastRegion
        await CaptureAndPush(_lastRegion.Value);
    }

    async void Recapture_Click(object s, RoutedEventArgs e)
    {
        if (_lastRegion is { } r) await CaptureAndPush(r);
    }

    // One capture -> every checked TV. Upload the PNG then post the dashboard per TV.
    async Task CaptureAndPush((int x, int y, int w, int h) r)
    {
        if (!Targets.Any()) { Status.Text = "Tick at least one TV above."; return; }
        SetBusy(true);
        try
        {
            var png = ScreenCapture.CaptureRegion(r.x, r.y, r.w, r.h);
            ShowPreview(png);
            var name = $"{Slot}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
            Status.Text = $"Sending {Slot} to your TVs…";
            var result = await MultiPush.RunAsync(Targets, async t =>
            {
                var path = await _client.UploadMediaAsync(t.BaseUrl, name, png);
                _state.Boards[Slot] = path;
                await _client.PostDashboardAsync(t.BaseUrl, DashboardPayload.Build(_state, _timer));
            });
            Status.Text = result.Summary();
            Toaster.Show(result.Summary(), result.AllFailed ? ToastKind.Error : ToastKind.Success);
            if (!result.AllFailed)
            {
                App.Settings.Regions[Slot] = new PiSignage.Signage.RegionRect { X = r.x, Y = r.y, W = r.w, H = r.h };
                App.SaveSettings();
            }
        }
        finally { SetBusy(false); }
    }

    async Task<MultiPushResult> FanOutDashboard()
        => await MultiPush.RunAsync(Targets, t =>
               _client.PostDashboardAsync(t.BaseUrl, DashboardPayload.Build(_state, _timer)));

    async Task<bool> Post(string msg)
    {
        if (!Targets.Any()) { Status.Text = "Tick at least one TV above."; return false; }
        var result = await FanOutDashboard();
        Status.Text = result.Failed.Count == 0 ? msg : result.Summary();
        if (result.Failed.Count > 0)
            Toaster.Show(result.Summary(), result.AllFailed ? ToastKind.Error : ToastKind.Info);
        return !result.AllFailed;
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
        finally { UpdateActionButtons(); }
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
        finally { UpdateActionButtons(); }
    }

    // Pin the board on the TV (show-now with no end time). The running timer
    // rides on top via the kiosk's corner overlay, so nothing rotates.
    async void PinBoardToTv_Click(object s, RoutedEventArgs e)
    {
        var slot = Slot;
        var result = await MultiPush.RunAsync(Targets, async t =>
        {
            var u = new Uri(t.BaseUrl);
            using var api = new ApiClient(u.Host, u.Port);
            await api.ShowNowAsync(new ShowNowRequest { Type = "url", Source = BoardPageUrl(slot), Duration = null });
        });
        Toaster.Show(result.AllFailed
            ? "Couldn't pin it on any TV: " + result.Summary(verb: "pinned")
            : $"The {slot} are pinned — {result.Summary(verb: "pinned")} Click 'Back to playlist' when the event is over.",
            result.AllFailed ? ToastKind.Error : ToastKind.Success);
    }

    async void BackToPlaylist_Click(object s, RoutedEventArgs e)
    {
        var result = await MultiPush.RunAsync(Targets, async t =>
        {
            var u = new Uri(t.BaseUrl);
            using var api = new ApiClient(u.Host, u.Port);
            await api.ClearShowNowAsync();
        });
        Toaster.Show(result.AllFailed
            ? "Couldn't switch any TV back: " + result.Summary(verb: "switched back")
            : result.Summary(verb: "switched back to its playlist"),
            result.AllFailed ? ToastKind.Error : ToastKind.Success);
    }

    // The kiosk browser runs ON the Pi, so it reaches its own agent via localhost.
    static string BoardPageUrl(string slot) => $"http://localhost:8080/dashboard?view=board&name={slot}";
}
