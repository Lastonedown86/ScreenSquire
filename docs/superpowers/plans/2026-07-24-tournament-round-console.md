# Tournament Round Console Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade Tournament Signage from single-Pi Start/Stop to a round console: multi-TV push, pause/extend/time's-up alert, one-click Next Round, client-defined boards, console layout.

**Architecture:** All new logic lives in `signage-core` (testable, no WPF deps): a sequential fan-out helper with per-target failure isolation, a board-name slugger, and settings fields. `SignageWindow` re-wires onto those helpers and gets a layout rework. The Pi agent is untouched except ~3 lines of CSS in `dashboard.html` (TIME pulse). Wire payload shape unchanged.

**Tech Stack:** .NET 8 WPF (`windows-app`), .NET 8 class lib + xUnit (`signage-core`, `signage-core.Tests`), FastAPI agent + pytest (`agent`), vanilla JS/CSS (`agent/static/dashboard.html`).

## Global Constraints

- Repo root: `the ScreenSquire repository root` (this is the git repo). All paths below are relative to it; run all `git`/`dotnet`/`pytest` commands from it.
- Spec: `docs/superpowers/specs/2026-07-24-tournament-round-console-design.md`.
- **No agent endpoint changes.** `POST/GET /api/dashboard` and the payload shape stay exactly as-is.
- **Plain language everywhere the client sees:** "TV" not "Pi/kiosk", tooltips name the next step, toasts name buttons exactly as labeled. Toast for non-destructive feedback (`Toaster.Show`), MessageBox only for destructive confirms.
- `pairings` and `standings` boards are permanent — never removable.
- Build check for WPF tasks: `dotnet build windows-app/PiSignageControl.csproj` (must succeed; WPF has no UI tests — manual smoke per task).
- Core tests: `dotnet test signage-core.Tests/signage-core.Tests.csproj`.
- Agent tests: `cd agent `cd agent && ./.venv/Scripts/python.exe -m pytest tests -q` (run from repo root)`cd agent && ./.venv/Scripts/python.exe -m pytest tests -q` (run from repo root) ./.venv/Scripts/python.exe -m pytest tests -q` (must run from `agent/` — tests `import main`).

---

### Task 1: signage-core — MultiPush fan-out helper

**Files:**
- Create: `signage-core/MultiPush.cs`
- Test: `signage-core.Tests/MultiPushTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `PushTarget(string Name, string BaseUrl)` record; `MultiPush.RunAsync(IEnumerable<PushTarget>, Func<PushTarget, Task>) -> Task<MultiPushResult>`; `MultiPushResult { List<string> Succeeded; List<(string Name, string Error)> Failed; bool AllFailed; string Summary(string verb = "updated") }`. Tasks 4–6 call these from `SignageWindow`.

- [ ] **Step 1: Write the failing tests**

`signage-core.Tests/MultiPushTests.cs`:

```csharp
using PiSignage.Signage;
using Xunit;

public class MultiPushTests
{
    static PushTarget T(string name) => new(name, "http://" + name + ":8080");

    [Fact]
    public async Task AllSucceed()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front"), T("Back") }, _ => Task.CompletedTask);
        Assert.Equal(new[] { "Front", "Back" }, r.Succeeded);
        Assert.Empty(r.Failed);
        Assert.False(r.AllFailed);
    }

    [Fact]
    public async Task OneFailureDoesNotBlockTheRest()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front"), T("Back"), T("Side") },
            t => t.Name == "Back" ? Task.FromException(new HttpRequestException("timeout")) : Task.CompletedTask);
        Assert.Equal(new[] { "Front", "Side" }, r.Succeeded);
        Assert.Single(r.Failed);
        Assert.Equal("Back", r.Failed[0].Name);
    }

    [Fact]
    public async Task SummaryNamesSuccessesAndFailuresInClientLanguage()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front"), T("Back") },
            t => t.Name == "Back" ? Task.FromException(new HttpRequestException("x")) : Task.CompletedTask);
        Assert.Equal("Front updated. Back unreachable — that TV was not updated.", r.Summary());
    }

    [Fact]
    public async Task AllFailedFlag()
    {
        var r = await MultiPush.RunAsync(new[] { T("Front") },
            _ => Task.FromException(new HttpRequestException("x")));
        Assert.True(r.AllFailed);
    }
}
```

Add `using System.Net.Http;` at the top if `HttpRequestException` is unresolved.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test signage-core.Tests/signage-core.Tests.csproj --filter MultiPushTests`
Expected: FAIL — `PushTarget`/`MultiPush` do not exist (compile error).

- [ ] **Step 3: Implement**

`signage-core/MultiPush.cs`:

```csharp
namespace PiSignage.Signage;

public sealed record PushTarget(string Name, string BaseUrl);

public sealed class MultiPushResult
{
    public List<string> Succeeded { get; } = new();
    public List<(string Name, string Error)> Failed { get; } = new();
    public bool AllFailed => Succeeded.Count == 0 && Failed.Count > 0;

    // Client-facing toast text: "Front updated. Back unreachable — that TV was not updated."
    public string Summary(string verb = "updated")
    {
        var parts = new List<string>();
        if (Succeeded.Count > 0)
            parts.Add(string.Join(", ", Succeeded) + $" {verb}.");
        foreach (var (name, _) in Failed)
            parts.Add($"{name} unreachable — that TV was not {verb}.");
        return string.Join(" ", parts);
    }
}

public static class MultiPush
{
    // Sequential on purpose: 4 LAN targets, and sequential keeps error handling dead simple.
    // ponytail: parallelize only if the shop ever runs enough TVs for it to matter.
    public static async Task<MultiPushResult> RunAsync(
        IEnumerable<PushTarget> targets, Func<PushTarget, Task> action)
    {
        var result = new MultiPushResult();
        foreach (var t in targets)
        {
            try { await action(t); result.Succeeded.Add(t.Name); }
            catch (Exception ex) { result.Failed.Add((t.Name, ex.Message)); }
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test signage-core.Tests/signage-core.Tests.csproj --filter MultiPushTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add signage-core/MultiPush.cs signage-core.Tests/MultiPushTests.cs
git commit -m "feat(core): MultiPush fan-out with per-target failure isolation"
```

---

### Task 2: signage-core — BoardSlug + paused-payload test

**Files:**
- Create: `signage-core/BoardSlug.cs`
- Test: `signage-core.Tests/BoardSlugTests.cs`
- Modify: `signage-core.Tests/DashboardPayloadTests.cs` (add one test)

**Interfaces:**
- Produces: `BoardSlug.From(string) -> string` (lowercase, letters/digits kept, runs of anything else become single dashes, trimmed). Tasks 6–7 use it for board keys, filenames, and TV URLs.

- [ ] **Step 1: Write the failing tests**

`signage-core.Tests/BoardSlugTests.cs`:

```csharp
using PiSignage.Signage;
using Xunit;

public class BoardSlugTests
{
    [Theory]
    [InlineData("Top 8 Bracket", "top-8-bracket")]
    [InlineData("pairings", "pairings")]
    [InlineData("  Announcements!  ", "announcements")]
    [InlineData("A -- B", "a-b")]
    [InlineData("!!!", "")]
    public void From(string input, string expected) => Assert.Equal(expected, BoardSlug.From(input));
}
```

Append to `signage-core.Tests/DashboardPayloadTests.cs` (inside the class):

```csharp
    [Fact]
    public void PausedTimerSerializesStateAndRemaining()
    {
        var timer = new RoundTimer(); timer.Start(25, "Round 3", 3); timer.Pause(843);
        var json = JsonSerializer.Serialize(DashboardPayload.Build(new DashboardState(), timer));
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement.GetProperty("timer");
        Assert.Equal("paused", t.GetProperty("state").GetString());
        Assert.Equal(843, t.GetProperty("remaining").GetInt32());
    }
```

- [ ] **Step 2: Run tests to verify the new ones fail**

Run: `dotnet test signage-core.Tests/signage-core.Tests.csproj --filter "BoardSlugTests|DashboardPayloadTests"`
Expected: BoardSlugTests FAIL to compile (`BoardSlug` missing). Paused test PASSES already (RoundTimer/payload support paused) — that is fine; it pins the wire shape Tasks 5 relies on.

- [ ] **Step 3: Implement**

`signage-core/BoardSlug.cs`:

```csharp
namespace PiSignage.Signage;

public static class BoardSlug
{
    // "Top 8 Bracket" -> "top-8-bracket". Used for board keys, capture filenames,
    // and the TV page URL (?name=<slug>), so it must stay URL- and filename-safe.
    public static string From(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test signage-core.Tests/signage-core.Tests.csproj --filter "BoardSlugTests|DashboardPayloadTests"`
Expected: PASS (BoardSlug 5 cases + 3 payload tests).

- [ ] **Step 5: Commit**

```bash
git add signage-core/BoardSlug.cs signage-core.Tests/BoardSlugTests.cs signage-core.Tests/DashboardPayloadTests.cs
git commit -m "feat(core): BoardSlug for custom board names; pin paused payload shape"
```

---

### Task 3: signage-core — settings: SignageTargets + Boards (with migration)

**Files:**
- Modify: `signage-core/SettingsStore.cs`
- Test: `signage-core.Tests/SettingsStoreTests.cs` (append tests)

**Interfaces:**
- Produces: `AppSettings.SignageTargets: List<string>` (checked TV hostnames) and `AppSettings.Boards: List<string>` (display names, always containing `"pairings"` and `"standings"`). `SettingsStore.Load()` migrates legacy single `SignageTarget` into `SignageTargets` and guarantees the two default boards. Tasks 4 and 7 read/write these.

- [ ] **Step 1: Write the failing tests**

Append to `signage-core.Tests/SettingsStoreTests.cs` (match the file's existing temp-path pattern — it constructs `SettingsStore` with a temp file path; reuse the same helper style found in that file):

```csharp
    [Fact]
    public void LegacySingleTargetMigratesIntoTargetsList()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """{"SignageTarget":"pi-front.local"}""");
        var s = new SettingsStore(path).Load();
        Assert.Equal(new[] { "pi-front.local" }, s.SignageTargets);
        File.Delete(path);
    }

    [Fact]
    public void DefaultBoardsAlwaysPresent()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """{"Boards":["Top 8 bracket"]}""");
        var s = new SettingsStore(path).Load();
        Assert.Contains("pairings", s.Boards);
        Assert.Contains("standings", s.Boards);
        Assert.Contains("Top 8 bracket", s.Boards);
        File.Delete(path);
    }

    [Fact]
    public void TargetsAndBoardsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var store = new SettingsStore(path);
        var s = new AppSettings();
        s.SignageTargets.Add("pi-a.local"); s.SignageTargets.Add("pi-b.local");
        s.Boards.Add("Top 8 bracket");
        store.Save(s);
        var back = store.Load();
        Assert.Equal(new[] { "pi-a.local", "pi-b.local" }, back.SignageTargets);
        Assert.Contains("Top 8 bracket", back.Boards);
        File.Delete(path);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test signage-core.Tests/signage-core.Tests.csproj --filter SettingsStoreTests`
Expected: FAIL to compile — `SignageTargets`/`Boards` do not exist.

- [ ] **Step 3: Implement**

In `signage-core/SettingsStore.cs`, add to `AppSettings` (after `SignageTarget`):

```csharp
    // Checked TVs in the Tournament Signage window (hostnames). Replaces the
    // single SignageTarget, which is kept for migration of old settings files.
    public List<string> SignageTargets { get; set; } = new();
    // Capture boards shown in the board picker (display names as the client typed them).
    public List<string> Boards { get; set; } = new() { "pairings", "standings" };
```

In `SettingsStore.Load()`, replace the `return JsonSerializer...` line so loaded settings are normalized:

```csharp
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
            // migrate: old files had one target; carry it into the checked-TVs list
            if (s.SignageTargets.Count == 0 && !string.IsNullOrWhiteSpace(s.SignageTarget))
                s.SignageTargets.Add(s.SignageTarget!);
            // the two default boards are permanent
            if (!s.Boards.Contains("standings")) s.Boards.Insert(0, "standings");
            if (!s.Boards.Contains("pairings")) s.Boards.Insert(0, "pairings");
            return s;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test signage-core.Tests/signage-core.Tests.csproj`
Expected: PASS — all SettingsStore tests including the pre-existing ones.

- [ ] **Step 5: Commit**

```bash
git add signage-core/SettingsStore.cs signage-core.Tests/SettingsStoreTests.cs
git commit -m "feat(core): SignageTargets list + Boards list in settings, legacy migration"
```

---

### Task 4: SignageWindow — multi-TV checkboxes + fan-out

**Files:**
- Modify: `windows-app/SignageWindow.xaml` (replace the "1. Choose your Pi" block)
- Modify: `windows-app/SignageWindow.xaml.cs` (targets, fan-out, hydrate, enable/disable)

**Interfaces:**
- Consumes: `MultiPush`, `PushTarget` (Task 1); `AppSettings.SignageTargets` (Task 3).
- Produces: `IEnumerable<PushTarget> Targets` property, `Task<MultiPushResult> FanOutDashboard()` (used by Tasks 5–6), `void UpdateActionButtons()`. `CmbAgent`, `Base`, `TargetLabel`, `ApiFromBase` are deleted.

- [ ] **Step 1: XAML — checkbox strip replaces the combo**

In `windows-app/SignageWindow.xaml`, replace:

```xml
            <TextBlock Text="1. Choose your Pi" FontWeight="SemiBold"/>
            <ComboBox x:Name="CmbAgent" IsEditable="True" DisplayMemberPath="Name"
                      IsTextSearchEnabled="False" Margin="0,2,0,10"
                      ToolTip="The Pi whose TV should show the captures and timer"/>
```

with:

```xml
            <TextBlock Text="1. Tick the TVs to send to" FontWeight="SemiBold"/>
            <ItemsControl x:Name="TvList" Margin="0,2,0,2">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <CheckBox Content="{Binding Device.Name}" IsChecked="{Binding Checked, Mode=TwoWay}"
                                  Margin="0,0,14,4" Checked="Tv_CheckChanged" Unchecked="Tv_CheckChanged"
                                  ToolTip="Send captures and the round timer to this TV"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock x:Name="NoTvHint" Text="Tick at least one TV above."
                       Foreground="{StaticResource Warning}" Margin="0,0,0,8" Visibility="Collapsed"/>
```

- [ ] **Step 2: Code-behind — targets + fan-out**

In `windows-app/SignageWindow.xaml.cs`:

Add the choice type and members (near the other fields):

```csharp
    sealed class TvChoice
    {
        public PiSignage.Signage.SavedDevice Device { get; init; } = null!;
        public bool Checked { get; set; }
    }
    List<TvChoice> _tvs = new();

    IEnumerable<PushTarget> Targets => _tvs.Where(t => t.Checked)
        .Select(t => new PushTarget(t.Device.Name, $"http://{t.Device.Ip}:8080"));
```

Replace `RestoreSession`'s device-selection block (the `target`/`dev`/`CmbAgent` lines) with:

```csharp
        var wanted = new HashSet<string>(App.Settings.SignageTargets, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 && !string.IsNullOrWhiteSpace(App.Settings.LastDeviceHostname))
            wanted.Add(App.Settings.LastDeviceHostname!);
        _tvs = saved.Select(d => new TvChoice { Device = d, Checked = wanted.Contains(d.Hostname) }).ToList();
        if (_tvs.Count > 0 && !_tvs.Any(t => t.Checked)) _tvs[0].Checked = true;  // sane default: first TV
        TvList.ItemsSource = _tvs;
        UpdateActionButtons();
```

Replace `SaveSession` body:

```csharp
        App.Settings.SignageTargets = _tvs.Where(t => t.Checked).Select(t => t.Device.Hostname).ToList();
        App.SaveSettings();
```

Replace `RefreshDevices` body (keep checked set across a rename):

```csharp
        var keep = new HashSet<string>(_tvs.Where(t => t.Checked).Select(t => t.Device.Hostname),
                                       StringComparer.OrdinalIgnoreCase);
        var saved = new PiSignage.Signage.DeviceStore().Load();
        _tvs = saved.Select(d => new TvChoice { Device = d, Checked = keep.Contains(d.Hostname) }).ToList();
        TvList.ItemsSource = _tvs;
        UpdateActionButtons();
```

Delete `Base`, `TargetLabel`, and `ApiFromBase()`. Add:

```csharp
    void Tv_CheckChanged(object s, RoutedEventArgs e) => UpdateActionButtons();

    void UpdateActionButtons()
    {
        bool any = Targets.Any();
        NoTvHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        BtnStart.IsEnabled = BtnStop.IsEnabled = any;
        BtnRecapture.IsEnabled = any && _lastRegion is not null;
        BtnCapture.IsEnabled = BtnPin.IsEnabled = BtnBack.IsEnabled = any;
    }
```

Also in the XAML this step: add `x:Name="BtnCapture"` to the "Capture region…" button, `x:Name="BtnPin"` to "Pin to TV", and `x:Name="BtnBack"` to "Back to playlist" so `UpdateActionButtons` can toggle them.

Rewrite the network paths to fan out:

```csharp
    // One capture -> every checked TV. Upload the PNG then post the dashboard per TV.
    async Task CaptureAndPush((int x, int y, int w, int h) r)
    {
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
        var result = await FanOutDashboard();
        Status.Text = result.Failed.Count == 0 ? msg : result.Summary();
        if (result.Failed.Count > 0)
            Toaster.Show(result.Summary(), result.AllFailed ? ToastKind.Error : ToastKind.Info);
        return !result.AllFailed;
    }
```

Pin / Back fan out with a per-target `ApiClient`:

```csharp
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
```

Hydrate from the first checked TV — in `HydrateFromDevice()`, replace only the opening lines (before `try`) and the `GetDashboardAsync` call; everything from `if (snap is null)` down is untouched:

```csharp
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
            // (existing body from here on, verbatim: boards copy, timer restore,
            //  UpdateClock, PreviewSlot, restored-count status text)
```

`PreviewSlot()` — two edits only: guard on a first target and fetch from it. The method keeps its exact current spinner/fallback body; change the opening condition and the fetch line:

```csharp
    async Task PreviewSlot()
    {
        var first = Targets.FirstOrDefault();
        if (first is not null && _state.Boards.TryGetValue(Slot, out var path))
        {
            // (existing spinner body, with the fetch line changed to:)
            var fetch = _client.GetMediaAsync(first.BaseUrl, path);
```

- [ ] **Step 3: Build**

Run: `dotnet build windows-app/PiSignageControl.csproj`
Expected: Build succeeded, 0 errors. (Compiler will catch any leftover `Base`/`CmbAgent` references — fix all of them.)

- [ ] **Step 4: Manual smoke**

Start two local agents (different ports won't match the `:8080` convention — instead run one real/local agent and one bogus saved device): with one reachable agent and one dead entry both ticked, push a capture. Expected: capture lands on the live one; toast reads "X updated. Y unreachable — that TV was not updated." Untick all TVs → buttons disable, hint appears.

- [ ] **Step 5: Commit**

```bash
git add windows-app/SignageWindow.xaml windows-app/SignageWindow.xaml.cs
git commit -m "feat(app): tournament pushes fan out to all ticked TVs"
```

---

### Task 5: SignageWindow — Pause/Resume, +5 min, time's-up alert

**Files:**
- Modify: `windows-app/SignageWindow.xaml` (two buttons after Stop)
- Modify: `windows-app/SignageWindow.xaml.cs`
- Modify (TV pulse is Task 8): none here.

**Interfaces:**
- Consumes: `RoundTimer.Pause/Resume` (existing), `Post()` (Task 4).
- Produces: `PauseResume_Click`, `Extend_Click`, `_timeUpFired` handling inside `UpdateClock()`. Task 6 relies on `StartRound(int min, int round)` extracted here.

- [ ] **Step 1: XAML — add buttons after BtnStop**

```xml
                <Button x:Name="BtnPause" Content="_Pause" Click="PauseResume_Click" Margin="6,0,0,0" Padding="8,3"
                        IsEnabled="False" ToolTip="Pause the round clock — click again to resume"
                        ToolTipService.ShowOnDisabled="True"/>
                <Button x:Name="BtnExtend" Content="+5 _min" Click="Extend_Click" Margin="6,0,0,0" Padding="8,3"
                        IsEnabled="False" ToolTip="Add 5 minutes to the round clock"
                        ToolTipService.ShowOnDisabled="True"/>
```

- [ ] **Step 2: Code-behind**

Add fields:

```csharp
    int _pausedRemaining;     // seconds left while paused (app-side truth)
    bool _timeUpFired;        // one alert per round
```

Extract the shared start path and rewrite the start handler (Task 6 calls `StartRound` too):

```csharp
    async Task<bool> StartRound(int min, int round)
    {
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
        finally { BtnStart.IsEnabled = BtnStop.IsEnabled = true; UpdateActionButtons(); }
    }
```

Pause / resume / extend:

```csharp
    async void PauseResume_Click(object s, RoutedEventArgs e)
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

    async void Extend_Click(object s, RoutedEventArgs e)
    {
        if (_timer.State == PiSignage.Signage.TimerRunState.Running && _endsAtLocal is { } end)
        {
            var rem = Math.Max(0, (int)Math.Round((end - DateTime.UtcNow).TotalSeconds)) + 300;
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
```

`UpdateClock()` gains a paused branch and the alert. Replace the method:

```csharp
    void UpdateClock()
    {
        BtnPause.IsEnabled = BtnExtend.IsEnabled =
            _timer.State != PiSignage.Signage.TimerRunState.Stopped && Targets.Any();

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
```

In `StopTimer_Click`, add `BtnPause.Content = "_Pause"; _timeUpFired = false;` after `_timer.Stop();`.
In `HydrateFromDevice`, after a restored running timer, `_timeUpFired = snap.timer... ` is unnecessary — leave `_timeUpFired = false` default (a restored already-expired timer will beep once; acceptable).

- [ ] **Step 3: Build**

Run: `dotnet build windows-app/PiSignageControl.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Manual smoke**

Against one local agent: Start 1-minute round → Pause (TV chip freezes at next poll ≤15 s) → Resume → +5 min (app clock jumps 5:xx, TV follows on poll) → let it hit zero → laptop plays a sound + toast; TV shows red TIME.

- [ ] **Step 5: Commit**

```bash
git add windows-app/SignageWindow.xaml windows-app/SignageWindow.xaml.cs
git commit -m "feat(app): pause/resume, +5 min extend, time's-up sound + toast"
```

---

### Task 6: SignageWindow — Next Round button

**Files:**
- Modify: `windows-app/SignageWindow.xaml` (button next to Start/Stop for now; Task 8 makes it huge)
- Modify: `windows-app/SignageWindow.xaml.cs`

**Interfaces:**
- Consumes: `StartRound` (Task 5), `CaptureAndPush` (Task 4), `RestoreRegionForSlot` (existing), `BoardSlug` (Task 2, once Task 7 lands; until then the slot list is still the fixed combo and `Slot` returns `"pairings"`/`"standings"` unchanged).
- Produces: `NextRound_Click`, `void SelectBoard(string display)`.

- [ ] **Step 1: XAML — add after BtnStop (before the presets panel)**

```xml
                <Button x:Name="BtnNextRound" Content="▶ _Next round" Click="NextRound_Click"
                        Style="{StaticResource PrimaryButton}" Margin="10,0,0,0" Padding="10,3"
                        ToolTip="Put the new pairings on your screen first, then click — this captures them, sends them to the ticked TVs, and starts the clock"
                        ToolTipService.ShowOnDisabled="True"/>
```

- [ ] **Step 2: Code-behind**

```csharp
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
            if (sel.ShowDialog() != true || sel.Result is null) return;
            _lastRegion = sel.Result;
            BtnRecapture.IsEnabled = true;
        }

        BtnNextRound.IsEnabled = false;
        try
        {
            await CaptureAndPush(_lastRegion.Value);
            if (await StartRound(min, round))
            {
                int n = Targets.Count();
                Toaster.Show($"Round {round} started — pairings sent to {n} TV{(n == 1 ? "" : "s")}, {min}:00 on the clock.",
                             ToastKind.Success);
            }
        }
        finally { BtnNextRound.IsEnabled = true; UpdateActionButtons(); }
    }
```

Add `BtnNextRound.IsEnabled = any;` inside `UpdateActionButtons()` (Task 4's method).

- [ ] **Step 3: Build**

Run: `dotnet build windows-app/PiSignageControl.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Manual smoke**

With a saved pairings region: click Next round → round number bumps, capture pushes, clock restarts, single toast summarizes. Delete the saved region (fresh settings) → click Next round → region selector opens first, then the flow continues.

- [ ] **Step 5: Commit**

```bash
git add windows-app/SignageWindow.xaml windows-app/SignageWindow.xaml.cs
git commit -m "feat(app): one-click Next Round (capture pairings + start clock)"
```

---

### Task 7: SignageWindow — client-defined boards

**Files:**
- Modify: `windows-app/SignageWindow.xaml` (board picker row)
- Modify: `windows-app/SignageWindow.xaml.cs`

**Interfaces:**
- Consumes: `AppSettings.Boards` (Task 3), `BoardSlug` (Task 2), `TextPrompt.Ask` (existing).
- Produces: `Slot` now returns the slug of the selected display name; `SlotDisplay` returns the display name. `SelectBoard` (Task 6) keeps working because items become plain strings.

- [ ] **Step 1: XAML — replace the fixed slot combo**

Replace:

```xml
            <ComboBox x:Name="CmbSlot" SelectedIndex="0" Margin="0,2,0,10" SelectionChanged="Slot_Changed"
                      ToolTip="Which board on the TV this capture fills — pairings or standings">
                <ComboBoxItem>pairings</ComboBoxItem>
                <ComboBoxItem>standings</ComboBoxItem>
            </ComboBox>
```

with:

```xml
            <StackPanel Orientation="Horizontal" Margin="0,2,0,10">
                <ComboBox x:Name="CmbSlot" MinWidth="170" SelectionChanged="Slot_Changed"
                          ToolTip="Which board on the TV this capture fills"/>
                <Button Content="+ Add _board…" Click="AddBoard_Click" Margin="6,0,0,0"
                        ToolTip="Add another board to capture into — like 'Top 8 bracket'"/>
                <Button x:Name="BtnRemoveBoard" Content="Remove board" Click="RemoveBoard_Click" Margin="6,0,0,0"
                        ToolTip="Take this board off the list (pairings and standings always stay)"
                        ToolTipService.ShowOnDisabled="True"/>
            </StackPanel>
```

- [ ] **Step 2: Code-behind**

Replace the `Slot` property and add board management:

```csharp
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
```

In `Slot_Changed`, add `BtnRemoveBoard.IsEnabled = SlotDisplay is not ("pairings" or "standings");` as the first line after the `IsLoaded` guard.

In `RestoreSession`, call `LoadBoards();` before `RestoreRegionForSlot();`.

In `Capture_Click`, the instruction string uses the display name: `Instruction = $"Drag a box around the {SlotDisplay} — Esc to cancel",`.

`SelectBoard("pairings")` from Task 6 works as-is (items are now plain strings; the `ComboBoxItem` branch just never matches).

- [ ] **Step 3: Build**

Run: `dotnet build windows-app/PiSignageControl.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Manual smoke**

Add board "Top 8 Bracket" → appears in picker, capture pushes to `/media/top-8-bracket-<ts>.png`, TV shows it at `/dashboard?view=board&name=top-8-bracket` via Pin to TV. Remove it → picker returns to pairings; pairings/standings show no Remove.

- [ ] **Step 5: Commit**

```bash
git add windows-app/SignageWindow.xaml windows-app/SignageWindow.xaml.cs
git commit -m "feat(app): client-defined capture boards with add/remove"
```

---

### Task 8: SignageWindow — round-console layout

**Files:**
- Modify: `windows-app/SignageWindow.xaml` (reorganize; all handlers already exist)

**Interfaces:**
- Consumes: every named control and handler from Tasks 4–7. No new code-behind.

- [ ] **Step 1: Rearrange the XAML**

Replace the top `StackPanel` (everything between the `Busy` progress bar and the preview `Border`) with the console arrangement — same controls, same names, new grouping:

```xml
        <StackPanel DockPanel.Dock="Top" Margin="14,10,14,0">
            <!-- setup strip: touched rarely -->
            <TextBlock Text="TVs to send to" FontWeight="SemiBold"/>
            <ItemsControl x:Name="TvList" Margin="0,2,0,2">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <CheckBox Content="{Binding Device.Name}" IsChecked="{Binding Checked, Mode=TwoWay}"
                                  Margin="0,0,14,4" Checked="Tv_CheckChanged" Unchecked="Tv_CheckChanged"
                                  ToolTip="Send captures and the round timer to this TV"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock x:Name="NoTvHint" Text="Tick at least one TV above."
                       Foreground="{StaticResource Warning}" Margin="0,0,0,4" Visibility="Collapsed"/>

            <WrapPanel Margin="0,4,0,0" VerticalAlignment="Center">
                <TextBlock Text="Board:" VerticalAlignment="Center" Margin="0,0,4,0" Foreground="{StaticResource TextMuted}"/>
                <ComboBox x:Name="CmbSlot" MinWidth="170" SelectionChanged="Slot_Changed" VerticalAlignment="Center"
                          ToolTip="Which board on the TV this capture fills"/>
                <Button Content="+ Add _board…" Click="AddBoard_Click" Margin="6,0,0,0"
                        ToolTip="Add another board to capture into — like 'Top 8 bracket'"/>
                <Button x:Name="BtnRemoveBoard" Content="Remove board" Click="RemoveBoard_Click" Margin="6,0,0,0"
                        ToolTip="Take this board off the list (pairings and standings always stay)"
                        ToolTipService.ShowOnDisabled="True"/>
                <TextBlock Text="Minutes:" VerticalAlignment="Center" Margin="16,0,4,0" Foreground="{StaticResource TextMuted}"/>
                <TextBox x:Name="TxtMinutes" Text="25" Width="46" VerticalAlignment="Center"
                         PreviewTextInput="NumberOnly_PreviewTextInput" KeyDown="TimerField_KeyDown"
                         TextChanged="TimerField_Changed"/>
                <StackPanel x:Name="Presets" Orientation="Horizontal" Margin="6,0,0,0"/>
                <TextBlock Text="Round:" VerticalAlignment="Center" Margin="16,0,4,0" Foreground="{StaticResource TextMuted}"/>
                <TextBox x:Name="TxtRound" Text="1" Width="46" VerticalAlignment="Center"
                         PreviewTextInput="NumberOnly_PreviewTextInput" KeyDown="TimerField_KeyDown"
                         TextChanged="TimerField_Changed"/>
            </WrapPanel>

            <Separator Margin="0,10,0,10"/>

            <!-- round console: touched every round -->
            <StackPanel HorizontalAlignment="Center">
                <TextBlock x:Name="ClockDisplay" Text="—:—" FontSize="56" FontWeight="Bold"
                           FontFamily="Consolas" HorizontalAlignment="Center"/>
                <TextBlock x:Name="TimerInfo" Text="No round running" HorizontalAlignment="Center"
                           Foreground="{StaticResource TextMuted}" FontSize="12" Margin="0,0,0,10"/>
                <Button x:Name="BtnNextRound" Content="▶  Next round — capture pairings and start the clock"
                        Click="NextRound_Click" Style="{StaticResource PrimaryButton}"
                        FontSize="16" Padding="22,10" HorizontalAlignment="Center"
                        ToolTip="Put the new pairings on your screen first, then click"
                        ToolTipService.ShowOnDisabled="True"/>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,8,0,0">
                    <Button x:Name="BtnStart" Content="_Start" Click="StartTimer_Click" Padding="10,3"
                            ToolTip="Start the countdown with the minutes and round above (or press Enter)"/>
                    <Button x:Name="BtnPause" Content="_Pause" Click="PauseResume_Click" Margin="6,0,0,0" Padding="8,3"
                            IsEnabled="False" ToolTip="Pause the round clock — click again to resume"
                            ToolTipService.ShowOnDisabled="True"/>
                    <Button x:Name="BtnExtend" Content="+5 _min" Click="Extend_Click" Margin="6,0,0,0" Padding="8,3"
                            IsEnabled="False" ToolTip="Add 5 minutes to the round clock"
                            ToolTipService.ShowOnDisabled="True"/>
                    <Button x:Name="BtnStop" Content="St_op" Click="StopTimer_Click" Margin="6,0,0,0" Padding="8,3"
                            ToolTip="Stop the countdown and clear the TV clock"/>
                </StackPanel>
            </StackPanel>

            <Separator Margin="0,10,0,10"/>

            <WrapPanel>
                <TextBlock Text="Capture:" VerticalAlignment="Center" Margin="0,0,6,0" Foreground="{StaticResource TextMuted}"/>
                <Button x:Name="BtnCapture" Content="_Capture region…" Click="Capture_Click" Padding="10,4"
                        ToolTip="Draw a box on your screen — that area is sent to the ticked TVs"/>
                <Button x:Name="BtnRecapture" Content="_Re-capture" Click="Recapture_Click"
                        Padding="10,4" Margin="8,0,0,0" IsEnabled="False"
                        ToolTip="Grab the same area again (after the page on your screen updates)"
                        ToolTipService.ShowOnDisabled="True"/>
                <Button x:Name="BtnPin" Content="P_in to TV" Click="PinBoardToTv_Click"
                        Padding="10,4" Margin="8,0,0,0"
                        ToolTip="Show this board on the ticked TVs and keep it there — the round timer floats on top"/>
                <Button x:Name="BtnBack" Content="Bac_k to playlist" Click="BackToPlaylist_Click"
                        Padding="10,4" Margin="8,0,0,0"
                        ToolTip="Unpin and let the TVs go back to their normal playlists"/>
            </WrapPanel>

            <TextBlock x:Name="Status" Margin="0,10,0,0" Foreground="{StaticResource TextMuted}" TextWrapping="Wrap"/>
            <TextBlock Text="Last capture (what's on the TV)" Margin="0,10,0,6" Foreground="{StaticResource TextMuted}"/>
        </StackPanel>
```

Notes: `ClockDisplay`/`TimerInfo` moved out of the old timer row (delete the old `StackPanel` holding them); numbered "1./2./3." headings are gone — the console reads top strip → big clock → actions. Window default `Height="820"` still fits; leave it.

- [ ] **Step 2: Build**

Run: `dotnet build windows-app/PiSignageControl.csproj`
Expected: Build succeeded (all `x:Name`s and handlers already exist — the compiler flags anything orphaned; delete leftovers it names).

- [ ] **Step 3: Manual smoke**

Open Tournament Signage: setup strip on top, big clock + big Next-round button centered, Pause/+5/Stop under it, capture row below, preview at the bottom fills remaining space. Alt-key mnemonics still unique. Resize small (560×480 min) — WrapPanels wrap, nothing clips.

- [ ] **Step 4: Commit**

```bash
git add windows-app/SignageWindow.xaml
git commit -m "feat(app): round-console layout for tournament signage"
```

---

### Task 9: TV page — pulsing TIME + agent test

**Files:**
- Modify: `agent/static/dashboard.html`
- Test: `agent/tests/test_dashboard.py` (append one test)

**Interfaces:**
- Consumes: existing `.over` class on `#board-timer` and `.clock .time` (already toggled by `tick()`).
- Produces: nothing downstream.

- [ ] **Step 1: Write the failing test**

Append to `agent/tests/test_dashboard.py`:

```python
def test_dashboard_page_pulses_time_up():
    html = client.get("/dashboard").text
    assert "timepulse" in html   # TIME pulses so it reads across the room
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py::test_dashboard_page_pulses_time_up -q`
Expected: FAIL — `assert "timepulse" in html`.

- [ ] **Step 3: Implement**

In `agent/static/dashboard.html`, add to the `<style>` block (after the `#board-timer.over` rule):

```css
  /* time's up: slow pulse so it's obvious across the room; opacity only (Pi-cheap) */
  @keyframes timepulse { 0%,100% { opacity:1 } 50% { opacity:.35 } }
  .clock .time.over, #board-timer.over { animation: timepulse 1.6s ease-in-out infinite; }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd agent && ./.venv/Scripts/python.exe -m pytest tests -q`
Expected: all pass (existing dashboard/kiosk/wifi tests included).

- [ ] **Step 5: Manual smoke**

Open `/dashboard?view=timer` in a browser with a short running timer posted; at zero, "TIME" pulses. Same for the corner chip on `?view=board&name=pairings`.

- [ ] **Step 6: Commit**

```bash
git add agent/static/dashboard.html agent/tests/test_dashboard.py
git commit -m "feat(agent): pulse TIME on the TV when the round clock hits zero"
```

---

## Final verification

- [ ] `dotnet test signage-core.Tests/signage-core.Tests.csproj` — all green.
- [ ] `dotnet build windows-app/PiSignageControl.csproj` — clean.
- [ ] `cd agent && ./.venv/Scripts/python.exe -m pytest tests -q` — all green.
- [ ] End-to-end against a real/local agent: tick 2 TVs (one dead) → Next round → per-TV toast, capture on live TV, clock running; Pause/Resume/+5; add a custom board and Pin it; time-out → laptop sound + pulsing TV TIME.
- [ ] Regression: main window playlist/media/show-now untouched; kiosk unchanged.
- [ ] Deploy to the shop Pi with `deploy-agent.ps1` when the client is ready (dashboard.html change only).
