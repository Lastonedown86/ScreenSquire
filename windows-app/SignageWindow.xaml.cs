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
    int _pausedRemaining;     // seconds left while paused (app-side truth)
    bool _timeUpFired;        // one alert per round
    bool _timerPostInFlight;  // guards Pause/Extend against a double-click race

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
        LoadBoards();
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
            if (snap is null) { Status.Text = "Nothing on the TV yet"; return; }

            _state.Boards.Clear();
            foreach (var kv in snap.view_data.boards) _state.Boards[kv.Key] = kv.Value;

            if (snap.timer.state == "running" && snap.timer.endsAt is { } ends)
            {
                _timer.RestoreRunning(snap.timer.remaining ?? 0, snap.timer.round, snap.timer.label);
                _endsAtLocal = DateTimeOffset.FromUnixTimeMilliseconds(ends).UtcDateTime;
                if (snap.timer.round is { } rd) TxtRound.Text = rd.ToString();
            }
            else if (snap.timer.state == "paused" && snap.timer.remaining is { } prem)
            {
                _pausedRemaining = prem;
                _timer.Pause(prem);
                if (snap.timer.round is { } prd) { TxtRound.Text = prd.ToString(); }
                _endsAtLocal = null;
                BtnPause.Content = "_Resume";
            }
            else { _timer.Stop(); _endsAtLocal = null; }
            UpdateClock();

            await PreviewSlot();   // show the currently-selected slot's board

            int n = _state.Boards.Count;
            bool timerRunning = _endsAtLocal is not null;
            bool timerPaused = _timer.State == PiSignage.Signage.TimerRunState.Paused;
            string timerNote = timerRunning ? ", timer running" : timerPaused ? ", timer paused" : "";
            Status.Text = (n > 0 || timerRunning || timerPaused)
                ? $"Restored from the TV ({n} board{(n == 1 ? "" : "s")}{timerNote})"
                : "Nothing on the TV yet";
        }
        catch { Status.Text = "TV not reachable — nothing restored"; }
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
        BtnRemoveBoard.IsEnabled = SlotDisplay is not ("pairings" or "standings");
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
        BtnPause.IsEnabled = BtnExtend.IsEnabled =
            _timer.State != PiSignage.Signage.TimerRunState.Stopped && Targets.Any() && !_timerPostInFlight;

        if (_timer.State == PiSignage.Signage.TimerRunState.Paused)
        {
            ClockDisplay.Text = $"{_pausedRemaining / 60:00}:{_pausedRemaining % 60:00}";
            ClockDisplay.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            TimerInfo.Text = $"Round {_timer.Round} — paused (click Resume to continue)";
            return;
        }
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
            if (!_timeUpFired)
            {
                _timeUpFired = true;
                System.Media.SystemSounds.Exclamation.Play();
                Toaster.Show($"{round} — time is up!", ToastKind.Info);
            }
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

    string SlotDisplay => CmbSlot.SelectedItem as string ?? "pairings";
    string Slot => PiSignage.Signage.BoardSlug.From(SlotDisplay);

    void LoadBoards(string? select = null)
    {
        CmbSlot.ItemsSource = null;
        CmbSlot.ItemsSource = App.Settings.Boards;
        CmbSlot.SelectedItem = select is not null && App.Settings.Boards.Contains(select)
            ? select : App.Settings.Boards[0];
        BtnRemoveBoard.IsEnabled = SlotDisplay is not ("pairings" or "standings");
    }

    void AddBoard_Click(object s, RoutedEventArgs e)
    {
        var name = TextPrompt.Ask(this, "What's on this board? For example: Top 8 bracket", "", "Add board");
        if (name is null) return;
        if (PiSignage.Signage.BoardSlug.From(name).Length == 0)
        { Toaster.Show("That name needs at least one letter or number.", ToastKind.Error); return; }
        var existing = App.Settings.Boards.FirstOrDefault(b =>
            PiSignage.Signage.BoardSlug.From(b) == PiSignage.Signage.BoardSlug.From(name));
        if (existing is not null) { LoadBoards(existing); return; }   // already there — just select it
        App.Settings.Boards.Add(name);
        App.SaveSettings();
        LoadBoards(name);
        Toaster.Show($"Board '{name}' added — capture into it like any other board.", ToastKind.Success);
    }

    void RemoveBoard_Click(object s, RoutedEventArgs e)
    {
        var name = SlotDisplay;
        if (name is "pairings" or "standings") return;
        App.Settings.Boards.Remove(name);
        App.Settings.Regions.Remove(Slot);
        App.SaveSettings();
        LoadBoards();
        Toaster.Show($"Board '{name}' removed from the list.", ToastKind.Success);
    }

    void Tv_CheckChanged(object s, RoutedEventArgs e) => UpdateActionButtons();

    void UpdateActionButtons()
    {
        bool any = Targets.Any();
        NoTvHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        BtnStart.IsEnabled = BtnStop.IsEnabled = any;
        BtnRecapture.IsEnabled = any && _lastRegion is not null;
        BtnCapture.IsEnabled = BtnPin.IsEnabled = BtnBack.IsEnabled = any;
        BtnNextRound.IsEnabled = any;
    }

    // Selects a board in the picker by its display name (no-op if not present).
    void SelectBoard(string display)
    {
        foreach (var item in CmbSlot.Items)
            if (string.Equals(item is ComboBoxItem c ? c.Content?.ToString() : item?.ToString(),
                              display, StringComparison.OrdinalIgnoreCase))
            { CmbSlot.SelectedItem = item; return; }
    }

    // One click = whole round turnover: round+1, capture pairings, push, start clock.
    async void NextRound_Click(object s, RoutedEventArgs e)
    {
        string prevRoundText = TxtRound.Text;
        int round = (int.TryParse(TxtRound.Text, out var r) ? r : 0) + 1;
        int min = int.TryParse(TxtMinutes.Text, out var m) ? m : 25;
        TxtRound.Text = round.ToString();
        SelectBoard("pairings");            // Slot_Changed restores the pairings region

        if (_lastRegion is null)
        {
            var sel = new RegionSelectorWindow
            {
                Owner = this,
                Instruction = "First time: drag a box around the pairings — Esc to cancel",
            };
            if (sel.ShowDialog() != true || sel.Result is null) { TxtRound.Text = prevRoundText; return; }
            _lastRegion = sel.Result;
            UpdateActionButtons();   // single gate: recomputes BtnRecapture from Targets.Any() && _lastRegion
        }

        BtnNextRound.IsEnabled = false;
        try
        {
            var pushResult = await CaptureAndPush(_lastRegion.Value);
            if (pushResult is null || pushResult.AllFailed) return;   // CaptureAndPush already toasted the failure
            if (await StartRound(min, round))
            {
                int n = pushResult.Succeeded.Count;
                Toaster.Show($"Round {round} started — pairings sent to {n} TV{(n == 1 ? "" : "s")}, {min}:00 on the clock.",
                             ToastKind.Success);
            }
        }
        finally { BtnNextRound.IsEnabled = true; UpdateActionButtons(); }
    }

    async void Capture_Click(object s, RoutedEventArgs e)
    {
        var sel = new RegionSelectorWindow
        {
            Owner = this,
            Instruction = $"Drag a box around the {SlotDisplay} — Esc to cancel",
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
    async Task<MultiPushResult?> CaptureAndPush((int x, int y, int w, int h) r)
    {
        if (!Targets.Any()) { Status.Text = "Tick at least one TV above."; return null; }
        var display = SlotDisplay;   // capture before the async work — client-facing text uses this, not the slug
        var slot = Slot;             // ditto: Slot is live off the combo box, snapshot it before awaits reorder under the user
        SetBusy(true);
        try
        {
            var png = ScreenCapture.CaptureRegion(r.x, r.y, r.w, r.h);
            ShowPreview(png);
            var name = $"{slot}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
            Status.Text = $"Sending {display} to your TVs…";
            var result = await MultiPush.RunAsync(Targets, async t =>
            {
                var path = await _client.UploadMediaAsync(t.BaseUrl, name, png);
                _state.Boards[slot] = path;
                await _client.PostDashboardAsync(t.BaseUrl, DashboardPayload.Build(_state, _timer));
            });
            Status.Text = result.Summary();
            Toaster.Show(result.Summary(),
                result.AllFailed ? ToastKind.Error : result.Failed.Count > 0 ? ToastKind.Warning : ToastKind.Success);
            if (!result.AllFailed)
            {
                App.Settings.Regions[slot] = new PiSignage.Signage.RegionRect { X = r.x, Y = r.y, W = r.w, H = r.h };
                App.SaveSettings();
            }
            return result;
        }
        finally { SetBusy(false); }
    }

    // Timer-only fan-out: no view_data, so a TV that never got an upload keeps
    // whatever boards it already has instead of the sender re-advertising its own.
    async Task<MultiPushResult> FanOutDashboard()
        => await MultiPush.RunAsync(Targets, t =>
               _client.PostDashboardAsync(t.BaseUrl, DashboardPayload.BuildTimerOnly(_timer)));

    async Task<bool> Post(string msg)
    {
        if (!Targets.Any()) { Status.Text = "Tick at least one TV above."; return false; }
        var result = await FanOutDashboard();
        Status.Text = result.Failed.Count == 0 ? msg : result.Summary();
        if (result.Failed.Count > 0)
            Toaster.Show(result.Summary(), result.AllFailed ? ToastKind.Error : ToastKind.Warning);
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

    async Task<bool> StartRound(int min, int round)
    {
        if (!Targets.Any()) { Status.Text = "Tick at least one TV above."; return false; }
        _timer.Start(min, $"Round {round}", round);
        _endsAtLocal = DateTime.UtcNow.AddSeconds(min * 60);
        _timerEditPending = false; _timeUpFired = false;
        BtnPause.Content = "_Pause";
        UpdateClock();
        App.Settings.TimerMinutes = min;
        App.SaveSettings();
        return await Post($"Round {round} started");
    }

    async void StartTimer_Click(object s, RoutedEventArgs e)
    {
        int min = int.TryParse(TxtMinutes.Text, out var m) ? m : 25;
        int round = int.TryParse(TxtRound.Text, out var rd) ? rd : 1;
        BtnStart.IsEnabled = BtnStop.IsEnabled = false;
        try
        {
            if (await StartRound(min, round))
                Toaster.Show($"Round {round} started — {min} minutes on the TV clock.", ToastKind.Success);
        }
        finally { UpdateActionButtons(); }
    }

    async void PauseResume_Click(object s, RoutedEventArgs e)
    {
        if (_timerPostInFlight) return;
        _timerPostInFlight = true;
        try
        {
            if (_timer.State == PiSignage.Signage.TimerRunState.Running && _endsAtLocal is { } end)
            {
                _pausedRemaining = Math.Max(0, (int)Math.Round((end - DateTime.UtcNow).TotalSeconds));
                _timer.Pause(_pausedRemaining);
                _endsAtLocal = null;
                BtnPause.Content = "_Resume";
                UpdateClock();
                if (await Post("Timer paused"))
                    Toaster.Show("Round clock paused — click Resume when you're ready.", ToastKind.Success);
            }
            else if (_timer.State == PiSignage.Signage.TimerRunState.Paused)
            {
                _timer.Resume(_pausedRemaining);
                _endsAtLocal = DateTime.UtcNow.AddSeconds(_pausedRemaining);
                BtnPause.Content = "_Pause";
                UpdateClock();
                if (await Post("Timer resumed"))
                    Toaster.Show("Round clock running again.", ToastKind.Success);
            }
        }
        finally { _timerPostInFlight = false; }
    }

    async void Extend_Click(object s, RoutedEventArgs e)
    {
        if (_timerPostInFlight) return;
        _timerPostInFlight = true;
        try
        {
            if (_timer.State == PiSignage.Signage.TimerRunState.Running && _endsAtLocal is { } end)
            {
                var rem = Math.Max(0, (int)Math.Round((end - DateTime.UtcNow).TotalSeconds)) + 300;
                if (rem == _timer.RemainingSeconds) rem += 1;  // dodge the agent's same-timer check so +5 always re-anchors
                _timer.Resume(rem);                      // still running, new remaining
                _endsAtLocal = DateTime.UtcNow.AddSeconds(rem);
                _timeUpFired = false;
                UpdateClock();
                if (await Post("Added 5 minutes"))
                    Toaster.Show("Added 5 minutes to the round clock.", ToastKind.Success);
            }
            else if (_timer.State == PiSignage.Signage.TimerRunState.Paused)
            {
                _pausedRemaining += 300;
                _timer.Pause(_pausedRemaining);
                _timeUpFired = false;
                UpdateClock();
                if (await Post("Added 5 minutes"))
                    Toaster.Show("Added 5 minutes to the round clock.", ToastKind.Success);
            }
        }
        finally { _timerPostInFlight = false; }
    }

    async void StopTimer_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        _endsAtLocal = null;
        _timerEditPending = false;
        BtnPause.Content = "_Pause";
        _timeUpFired = false;
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
        var display = SlotDisplay;   // client-facing text uses this, not the slug
        var result = await MultiPush.RunAsync(Targets, async t =>
        {
            var u = new Uri(t.BaseUrl);
            using var api = new ApiClient(u.Host, u.Port);
            await api.ShowNowAsync(new ShowNowRequest { Type = "url", Source = BoardPageUrl(slot), Duration = null });
        });
        Toaster.Show(result.AllFailed
            ? "Couldn't pin it on any TV: " + result.Summary(verb: "pinned")
            : $"The {display} are pinned — {result.Summary(verb: "pinned")} Click 'Back to playlist' when the event is over.",
            result.AllFailed ? ToastKind.Error : result.Failed.Count > 0 ? ToastKind.Warning : ToastKind.Success);
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
            : result.Summary(verb: "switched back to the normal playlist"),
            result.AllFailed ? ToastKind.Error : result.Failed.Count > 0 ? ToastKind.Warning : ToastKind.Success);
    }

    // The kiosk browser runs ON the Pi, so it reaches its own agent via localhost.
    static string BoardPageUrl(string slot) => $"http://localhost:8080/dashboard?view=board&name={slot}";
}
