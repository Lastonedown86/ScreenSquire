using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PiSignage.Signage;

namespace PiSignage.Control;

// Saved YouTube links, playable on one TV with sound via the kiosk's IFrame
// player. Agents older than the feature fall back to the muted looping embed.
public partial class BookmarksWindow : Window
{
    static readonly int[] MinYouTubeAgent = { 2026, 7, 26, 3 };
    static readonly HttpClient OEmbedHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly CredentialVault _credentialVault = new(new DpapiSecretProtector());
    readonly ObservableCollection<BookmarkRow> _rows = new();
    readonly DispatcherTimer _poll;
    ApiClient? _api;
    ControlContext? _ctx;
    bool _supportsYouTube;
    bool _polling;             // one poll in flight at a time
    List<BookmarkRow>? _queue; // ticked rows in list order; single play = queue of one
    int _queueIndex;
    BookmarkRow? _current;
    bool _lastPaused;

    public BookmarksWindow()
    {
        InitializeComponent();
        App.TrackPlacement(this, "Bookmarks");
        foreach (var b in App.Settings.YouTubeBookmarks) _rows.Add(new BookmarkRow(b));
        LstBookmarks.ItemsSource = _rows;
        SldVolume.Value = Math.Clamp(App.Settings.YouTubeVolume, 0, 100);
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();
        Loaded += (_, _) =>
        {
            var devices = new DeviceStore().Load();
            CmbDevice.ItemsSource = devices;
            if (devices.Count > 0) CmbDevice.SelectedIndex = 0;
            foreach (var row in _rows) _ = LoadThumbAsync(row);
        };
        Closed += (_, _) => { _poll.Stop(); _api?.Dispose(); Owner?.Activate(); };
        UpdateControls();
    }

    // ---------------------------------------------------------------- device

    async void Device_Changed(object sender, RoutedEventArgs e)
    {
        StopQueue();
        _api?.Dispose();
        _api = null; _ctx = null; _supportsYouTube = false;
        LblAgentHint.Text = "";
        if (CmbDevice.SelectedItem is not SavedDevice dev) { UpdateControls(); return; }

        _ctx = TryControlContext(dev.DeviceId);
        var api = new ApiClient(dev.Ip, dev.Port);
        _api = api;
        if (_ctx is null)
            LblAgentHint.Text = "Pair this Pi from the main screen before it can be controlled.";
        UpdateControls();
        try
        {
            var s = await api.GetStatusAsync();
            if (!ReferenceEquals(api, _api)) return;   // user switched device meanwhile
            _supportsYouTube = AgentSupportsYouTube(s?.AgentVersion);
            if (!_supportsYouTube && _ctx is not null)
                LblAgentHint.Text = "Update Pi software (main screen) to get sound, pause, "
                                    + "seek and queueing — for now videos play muted.";
        }
        catch
        {
            if (!ReferenceEquals(api, _api)) return;
            LblAgentHint.Text = "Can't reach this Pi right now.";
        }
        UpdateControls();
    }

    static bool AgentSupportsYouTube(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Split('.');
        for (var i = 0; i < MinYouTubeAgent.Length; i++)
        {
            var have = i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
            if (have != MinYouTubeAgent[i]) return have > MinYouTubeAgent[i];
        }
        return true;
    }

    void UpdateControls()
    {
        var connected = _api is not null && _ctx is not null;
        BtnPlay.IsEnabled = connected;
        BtnStop.IsEnabled = connected;
        var full = connected && _supportsYouTube;
        BtnPlayChecked.IsEnabled = full;
        BtnPause.IsEnabled = full;
        BtnBack10.IsEnabled = full;
        BtnFwd10.IsEnabled = full;
        SldVolume.IsEnabled = full;
        var updateHint = "Needs newer Pi software — click 'Update Pi software' on the main screen";
        BtnPlayChecked.ToolTip = full ? "Play every ticked video in order, top to bottom" : updateHint;
        BtnPause.ToolTip = full ? "Pause or resume the video on the TV" : updateHint;
        BtnBack10.ToolTip = full ? "Jump back ten seconds" : updateHint;
        BtnFwd10.ToolTip = full ? "Jump ahead ten seconds" : updateHint;
    }

    ControlContext? TryControlContext(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return ControlContext.LegacyUnsigned();
        var credential = _credentialVault.TryGet(deviceId);
        if (credential is null)
            return null;
        var stableDeviceId = deviceId;
        return new ControlContext(
            stableDeviceId,
            _credentialVault.Load().ControllerId,
            credential.Secret,
            () => _credentialVault.TakeNextCounter(stableDeviceId),
            _credentialVault.Path);
    }

    // ---------------------------------------------------------------- list

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var url = TextPrompt.Ask(this, "YouTube link (watch, shorts, live or youtu.be):",
                                 "https://", "Add YouTube video");
        if (string.IsNullOrWhiteSpace(url)) return;
        var vid = YouTubeUrl.TryGetVideoId(url.Trim());
        if (vid is null)
        {
            Toaster.Show("That doesn't look like a YouTube video link.", ToastKind.Error);
            return;
        }
        var existing = _rows.FirstOrDefault(r => r.Bookmark.VideoId == vid);
        if (existing is not null)
        {
            LstBookmarks.SelectedItem = existing;
            Toaster.Show("That video is already saved — selected it.", ToastKind.Info);
            return;
        }
        var bm = new YouTubeBookmark { VideoId = vid, Url = url.Trim() };
        await FetchOEmbedAsync(bm);   // best effort: the bookmark is kept either way
        var row = new BookmarkRow(bm);
        _rows.Add(row);
        App.Settings.YouTubeBookmarks.Add(bm);
        App.SaveSettings();
        LstBookmarks.SelectedItem = row;
        _ = LoadThumbAsync(row);
    }

    async Task FetchOEmbedAsync(YouTubeBookmark bm)
    {
        try
        {
            var url = "https://www.youtube.com/oembed?url="
                      + Uri.EscapeDataString(YouTubeUrl.WatchUrl(bm.VideoId)) + "&format=json";
            using var doc = JsonDocument.Parse(await OEmbedHttp.GetStringAsync(url));
            bm.Title = Str(doc, "title");
            bm.AuthorName = Str(doc, "author_name");
            bm.ThumbnailUrl = Str(doc, "thumbnail_url");
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Toaster.Show("Saved, but this video may not allow embedding — "
                         + "it might not play on the TV.", ToastKind.Warning);
        }
        catch
        {
            Toaster.Show("Saved without title — couldn't reach YouTube right now.",
                         ToastKind.Warning);
        }

        static string? Str(JsonDocument doc, string prop) =>
            doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }

    static async Task LoadThumbAsync(BookmarkRow row)
    {
        if (string.IsNullOrEmpty(row.Bookmark.ThumbnailUrl)) return;
        row.Thumb = await ThumbnailCache.GetUrlAsync(row.Bookmark.ThumbnailUrl!);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BookmarkRow row) return;
        _rows.Remove(row);
        App.Settings.YouTubeBookmarks.Remove(row.Bookmark);
        App.SaveSettings();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not BookmarkRow row) return;
        var i = _rows.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        _rows.Move(i, j);
        var list = App.Settings.YouTubeBookmarks;
        list.Remove(row.Bookmark);
        list.Insert(j, row.Bookmark);
        App.SaveSettings();
    }

    // ---------------------------------------------------------------- playback

    void LstBookmarks_DoubleClick(object sender, RoutedEventArgs e) => Play_Click(sender, e);

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (LstBookmarks.SelectedItem is not BookmarkRow row)
        {
            Toaster.Show("Pick a video in the list first.", ToastKind.Info);
            return;
        }
        await StartQueueAsync(new List<BookmarkRow> { row });
    }

    async void PlayChecked_Click(object sender, RoutedEventArgs e)
    {
        var q = _rows.Where(r => r.IsChecked).ToList();
        if (q.Count == 0)
        {
            Toaster.Show("Tick at least one video first.", ToastKind.Info);
            return;
        }
        await StartQueueAsync(q);
    }

    async Task StartQueueAsync(List<BookmarkRow> queue)
    {
        if (_api is null || _ctx is null) return;
        StopQueue();
        _queue = queue;
        _queueIndex = 0;
        await PlayCurrentAsync();
    }

    async Task PlayCurrentAsync()
    {
        if (_api is null || _ctx is null || _queue is null) return;
        var row = _queue[_queueIndex];
        try
        {
            var req = _supportsYouTube
                ? new ShowNowRequest
                  {
                      Type = "youtube",
                      Source = row.Bookmark.VideoId,
                      Duration = null,
                      Volume = App.Settings.YouTubeVolume,
                  }
                // old agent: raw watch URL; the agent rewrites it to a muted embed
                : new ShowNowRequest { Type = "url", Source = row.Bookmark.Url, Duration = null };
            await _api.ShowNowAsync(req, _ctx);
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't play that on the TV: " + ex.Message, ToastKind.Error);
            StopQueue();
            return;
        }
        SetCurrent(row);
        _lastPaused = false;
        BtnPause.Content = "⏸ Pause";
        // nothing to poll on old agents — the muted embed loops until stopped
        if (_supportsYouTube) _poll.Start();
        UpdateStatus();
    }

    async Task PollAsync()
    {
        if (_polling || _api is null || _ctx is null || _current is null) return;
        _polling = true;
        try
        {
            StatusInfo? s;
            try { s = await _api.GetStatusAsync(); } catch { return; }  // blip — retry next tick
            if (s is null || _current is null) return;
            if (!s.OverrideActive) { StopQueue(); return; }  // someone else took the TV back
            var ys = s.YouTubeState;
            if (ys?.VideoId != _current.Bookmark.VideoId) return;
            switch (ys!.State)
            {
                case "playing":
                    _lastPaused = false; BtnPause.Content = "⏸ Pause"; break;
                case "paused":
                    _lastPaused = true; BtnPause.Content = "▶ Resume"; break;
                case "error":
                    Toaster.Show($"'{_current.Display}' couldn't play "
                                 + "(embedding may be disabled) — skipping.", ToastKind.Warning);
                    await AdvanceAsync();
                    break;
                case "ended":
                    await AdvanceAsync();
                    break;
            }
        }
        finally { _polling = false; }
    }

    async Task AdvanceAsync()
    {
        _queueIndex++;
        if (_queue is not null && _queueIndex < _queue.Count)
        {
            await PlayCurrentAsync();
            return;
        }
        try { if (_api is not null && _ctx is not null) await _api.ClearShowNowAsync(_ctx); }
        catch { /* the agent backstop clears it anyway */ }
        StopQueue();
    }

    void StopQueue()
    {
        _poll.Stop();
        _queue = null;
        _queueIndex = 0;
        SetCurrent(null);
        UpdateStatus();
    }

    void SetCurrent(BookmarkRow? row)
    {
        if (_current is not null) _current.IsPlaying = false;
        _current = row;
        if (_current is not null) _current.IsPlaying = true;
    }

    void UpdateStatus()
    {
        LblStatus.Text = _current is null || _queue is null
            ? ""
            : $"Playing: {_current.Display} ({_queueIndex + 1} of {_queue.Count})";
    }

    async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _ctx is null) return;
        try
        {
            await _api.YouTubeControlAsync(_lastPaused ? "play" : "pause", null, _ctx);
            _lastPaused = !_lastPaused;
            BtnPause.Content = _lastPaused ? "▶ Resume" : "⏸ Pause";
        }
        catch (Exception ex) { Toaster.Show("Couldn't reach the TV: " + ex.Message, ToastKind.Error); }
    }

    async void Back10_Click(object sender, RoutedEventArgs e) => await SeekAsync(-10);
    async void Fwd10_Click(object sender, RoutedEventArgs e) => await SeekAsync(10);

    async Task SeekAsync(int seconds)
    {
        if (_api is null || _ctx is null) return;
        try { await _api.YouTubeControlAsync("seek", seconds, _ctx); }
        catch (Exception ex) { Toaster.Show("Couldn't reach the TV: " + ex.Message, ToastKind.Error); }
    }

    async void Volume_DragCompleted(object sender, RoutedEventArgs e)
    {
        var v = (int)Math.Round(SldVolume.Value);
        App.Settings.YouTubeVolume = v;
        App.SaveSettings();
        if (_api is null || _ctx is null || !_supportsYouTube) return;
        try { await _api.YouTubeControlAsync("volume", v, _ctx); }
        catch { /* volume still applies to the next play */ }
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _ctx is null) return;
        try { await _api.ClearShowNowAsync(_ctx); }
        catch (Exception ex) { Toaster.Show("Couldn't switch back: " + ex.Message, ToastKind.Error); }
        StopQueue();
    }
}

// Row view-model over a persisted bookmark: checkbox, thumbnail, playing flag.
sealed class BookmarkRow : System.ComponentModel.INotifyPropertyChanged
{
    public YouTubeBookmark Bookmark { get; }
    public BookmarkRow(YouTubeBookmark bookmark) => Bookmark = bookmark;

    public string Display => string.IsNullOrWhiteSpace(Bookmark.Title)
        ? Bookmark.VideoId : Bookmark.Title!;
    public string Author => Bookmark.AuthorName ?? "";

    bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    }

    System.Windows.Media.ImageSource? _thumb;
    public System.Windows.Media.ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); }
    }

    bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
