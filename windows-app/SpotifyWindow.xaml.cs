using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PiSignage.Signage;

namespace PiSignage.Control;

// Saved Spotify links, playable on one TV via the kiosk's Embed iFrame player.
// Deliberately smaller than the YouTube window: the Embed API has no volume
// control and no real ended event, and playlists/albums queue themselves.
public partial class SpotifyWindow : Window
{
    static readonly int[] MinSpotifyAgent = { 2026, 7, 26, 4 };
    static readonly HttpClient OEmbedHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly CredentialVault _credentialVault = new(new DpapiSecretProtector());
    readonly ObservableCollection<SpotifyRow> _rows = new();
    readonly DispatcherTimer _poll;
    ApiClient? _api;
    ControlContext? _ctx;
    bool _supportsSpotify;
    bool _polling;
    SpotifyRow? _current;
    bool _lastPaused;

    public SpotifyWindow()
    {
        InitializeComponent();
        App.TrackPlacement(this, "Spotify");
        foreach (var b in App.Settings.SpotifyBookmarks) _rows.Add(new SpotifyRow(b));
        LstBookmarks.ItemsSource = _rows;
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
        StopPlaybackTracking();
        _api?.Dispose();
        _api = null; _ctx = null; _supportsSpotify = false;
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
            _supportsSpotify = AgentSupportsSpotify(s?.AgentVersion);
            if (!_supportsSpotify && _ctx is not null)
                LblAgentHint.Text = "Update Pi software (main screen) to get pause and seek — "
                                    + "for now items play as a plain embed.";
        }
        catch
        {
            if (!ReferenceEquals(api, _api)) return;
            LblAgentHint.Text = "Can't reach this Pi right now.";
        }
        UpdateControls();
    }

    static bool AgentSupportsSpotify(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Split('.');
        for (var i = 0; i < MinSpotifyAgent.Length; i++)
        {
            var have = i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
            if (have != MinSpotifyAgent[i]) return have > MinSpotifyAgent[i];
        }
        return true;
    }

    void UpdateControls()
    {
        var connected = _api is not null && _ctx is not null;
        BtnPlay.IsEnabled = connected;
        BtnStop.IsEnabled = connected;
        var full = connected && _supportsSpotify;
        BtnPause.IsEnabled = full;
        BtnBack10.IsEnabled = full;
        BtnFwd10.IsEnabled = full;
        var updateHint = "Needs newer Pi software — click 'Update Pi software' on the main screen";
        BtnPause.ToolTip = full ? "Pause or resume playback on the TV" : updateHint;
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
        var input = TextPrompt.Ask(this,
            "Spotify link (track, album, playlist, podcast, show or artist):",
            "https://", "Add Spotify link");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (SpotifyUrl.TryParse(input.Trim()) is not { } parsed)
        {
            Toaster.Show("That doesn't look like a Spotify link.", ToastKind.Error);
            return;
        }
        var existing = _rows.FirstOrDefault(r =>
            r.Bookmark.Type == parsed.Type && r.Bookmark.Id == parsed.Id);
        if (existing is not null)
        {
            LstBookmarks.SelectedItem = existing;
            Toaster.Show("Already saved — selected it.", ToastKind.Info);
            return;
        }
        var bm = new SpotifyBookmark
        {
            Type = parsed.Type,
            Id = parsed.Id,
            Url = SpotifyUrl.OpenUrl(parsed.Type, parsed.Id),
        };
        await FetchOEmbedAsync(bm);   // best effort: the bookmark is kept either way
        var row = new SpotifyRow(bm);
        _rows.Add(row);
        App.Settings.SpotifyBookmarks.Add(bm);
        App.SaveSettings();
        LstBookmarks.SelectedItem = row;
        _ = LoadThumbAsync(row);
    }

    async Task FetchOEmbedAsync(SpotifyBookmark bm)
    {
        try
        {
            var url = "https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(bm.Url);
            using var doc = JsonDocument.Parse(await OEmbedHttp.GetStringAsync(url));
            bm.Title = Str(doc, "title");
            bm.ThumbnailUrl = Str(doc, "thumbnail_url");
        }
        catch
        {
            Toaster.Show("Saved without title — couldn't reach Spotify right now.",
                         ToastKind.Warning);
        }

        static string? Str(JsonDocument doc, string prop) =>
            doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }

    static async Task LoadThumbAsync(SpotifyRow row)
    {
        if (string.IsNullOrEmpty(row.Bookmark.ThumbnailUrl)) return;
        row.Thumb = await ThumbnailCache.GetUrlAsync(row.Bookmark.ThumbnailUrl!);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpotifyRow row) return;
        _rows.Remove(row);
        App.Settings.SpotifyBookmarks.Remove(row.Bookmark);
        App.SaveSettings();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpotifyRow row) return;
        var i = _rows.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        _rows.Move(i, j);
        var list = App.Settings.SpotifyBookmarks;
        list.Remove(row.Bookmark);
        list.Insert(j, row.Bookmark);
        App.SaveSettings();
    }

    // ---------------------------------------------------------------- playback

    void LstBookmarks_DoubleClick(object sender, RoutedEventArgs e) => Play_Click(sender, e);

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _ctx is null) return;
        if (LstBookmarks.SelectedItem is not SpotifyRow row)
        {
            Toaster.Show("Pick something in the list first.", ToastKind.Info);
            return;
        }
        try
        {
            var req = _supportsSpotify
                ? new ShowNowRequest
                  {
                      Type = "spotify",
                      Source = SpotifyUrl.CanonicalUri(row.Bookmark.Type, row.Bookmark.Id),
                      Duration = null,
                  }
                // old agent: plain embed URL through the existing url iframe path
                : new ShowNowRequest
                  {
                      Type = "url",
                      Source = SpotifyUrl.EmbedUrl(row.Bookmark.Type, row.Bookmark.Id),
                      Duration = null,
                  };
            await _api.ShowNowAsync(req, _ctx);
        }
        catch (Exception ex)
        {
            Toaster.Show("Couldn't play that on the TV: " + ex.Message, ToastKind.Error);
            return;
        }
        SetCurrent(row);
        _lastPaused = false;
        BtnPause.Content = "⏸ Pause";
        LblStatus.Text = "Playing: " + row.Display;
        if (_supportsSpotify) _poll.Start();
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
            if (!s.OverrideActive) { StopPlaybackTracking(); return; }  // ended+backstop, or cleared elsewhere
            var ps = s.SpotifyState;
            var uri = SpotifyUrl.CanonicalUri(_current.Bookmark.Type, _current.Bookmark.Id);
            if (ps?.Uri != uri) return;
            switch (ps!.State)
            {
                case "playing":
                    _lastPaused = false; BtnPause.Content = "⏸ Pause"; break;
                case "paused":
                    _lastPaused = true; BtnPause.Content = "▶ Resume"; break;
                case "ended":
                    // the agent backstop clears the override; clean up now
                    try { await _api.ClearShowNowAsync(_ctx); } catch { }
                    StopPlaybackTracking();
                    break;
            }
        }
        finally { _polling = false; }
    }

    void StopPlaybackTracking()
    {
        _poll.Stop();
        SetCurrent(null);
        LblStatus.Text = "";
    }

    void SetCurrent(SpotifyRow? row)
    {
        if (_current is not null) _current.IsPlaying = false;
        _current = row;
        if (_current is not null) _current.IsPlaying = true;
    }

    async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _ctx is null) return;
        try
        {
            await _api.SpotifyControlAsync(_lastPaused ? "play" : "pause", null, _ctx);
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
        try { await _api.SpotifyControlAsync("seek", seconds, _ctx); }
        catch (Exception ex) { Toaster.Show("Couldn't reach the TV: " + ex.Message, ToastKind.Error); }
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _ctx is null) return;
        try { await _api.ClearShowNowAsync(_ctx); }
        catch (Exception ex) { Toaster.Show("Couldn't switch back: " + ex.Message, ToastKind.Error); }
        StopPlaybackTracking();
    }
}

// Row view-model over a persisted Spotify bookmark.
sealed class SpotifyRow : System.ComponentModel.INotifyPropertyChanged
{
    public SpotifyBookmark Bookmark { get; }
    public SpotifyRow(SpotifyBookmark bookmark) => Bookmark = bookmark;

    public string Display => string.IsNullOrWhiteSpace(Bookmark.Title)
        ? $"{Bookmark.Type}: {Bookmark.Id}" : Bookmark.Title!;
    public string Kind => Bookmark.Type;

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
