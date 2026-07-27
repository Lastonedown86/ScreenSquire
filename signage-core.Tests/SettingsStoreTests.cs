using PiSignage.Signage;
using Xunit;

public class SettingsStoreTests
{
    static string TempFile() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"set-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings
            {
                LastDeviceHostname = "pisignage1",
                SignageTarget = "pisignage2",
                TimerMinutes = 40,
            };
            s.SignageDeviceIds.Add("device-id");
            s.Windows["Main"] = new WindowPlacement { Left = 10, Top = 20, Width = 960, Height = 640 };
            s.Regions["pairings"] = new RegionRect { X = 1, Y = 2, W = 300, H = 200 };
            store.Save(s);

            var got = store.Load();
            Assert.Equal("pisignage1", got.LastDeviceHostname);
            Assert.Equal("pisignage2", got.SignageTarget);
            Assert.Equal(new[] { "device-id" }, got.SignageDeviceIds);
            Assert.Equal(40, got.TimerMinutes);
            Assert.Equal(960, got.Windows["Main"].Width);
            Assert.Equal(300, got.Regions["pairings"].W);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void AgentPushRecordRoundTrips()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var when = new System.DateTime(2026, 7, 26, 3, 12, 0, System.DateTimeKind.Local);
            store.Save(new AppSettings
            {
                LastAgentPushLocal = when,
                LastAgentPushSummary = "Front and Back updated.",
            });

            var got = store.Load();
            Assert.Equal(when, got.LastAgentPushLocal);
            Assert.Equal("Front and Back updated.", got.LastAgentPushSummary);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void SettingsWrittenBeforeAutomaticUpdatesExistedStillLoad()
    {
        // Every store laptop already has a settings.json without these fields.
        // Loading one must look like "a sweep has never completed", not throw.
        var path = TempFile();
        try
        {
            System.IO.File.WriteAllText(path,
                "{\"LastDeviceHostname\":\"pisignage1\",\"TimerMinutes\":40}");
            var got = new SettingsStore(path).Load();
            Assert.Null(got.LastAgentPushLocal);
            Assert.Equal("", got.LastAgentPushSummary);
            Assert.Equal("pisignage1", got.LastDeviceHostname);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void MissingOrCorruptFileLoadsDefaults()
    {
        Assert.Equal(25, new SettingsStore(TempFile()).Load().TimerMinutes);
        var path = TempFile();
        try
        {
            System.IO.File.WriteAllText(path, "{not json");
            Assert.NotNull(new SettingsStore(path).Load().Windows);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void LegacySingleTargetMigratesIntoTargetsList()
    {
        var path = TempFile();
        try
        {
            System.IO.File.WriteAllText(path, """{"SignageTarget":"pi-front.local"}""");
            var s = new SettingsStore(path).Load();
            Assert.Equal(new[] { "pi-front.local" }, s.SignageTargets);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void DefaultBoardsAlwaysPresent()
    {
        var path = TempFile();
        try
        {
            System.IO.File.WriteAllText(path, """{"Boards":["Top 8 bracket"]}""");
            var s = new SettingsStore(path).Load();
            Assert.Contains("pairings", s.Boards);
            Assert.Contains("standings", s.Boards);
            Assert.Contains("Top 8 bracket", s.Boards);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void YouTubeBookmarksRoundTrip()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings { YouTubeVolume = 40 };
            s.YouTubeBookmarks.Add(new YouTubeBookmark
            {
                VideoId = "2B_L3WsMqTc",
                Url = "https://www.youtube.com/watch?v=2B_L3WsMqTc",
                Title = "Test video",
                AuthorName = "Someone",
                ThumbnailUrl = "https://i.ytimg.com/vi/2B_L3WsMqTc/hqdefault.jpg",
            });
            store.Save(s);
            var back = store.Load();
            Assert.Equal(40, back.YouTubeVolume);
            var b = Assert.Single(back.YouTubeBookmarks);
            Assert.Equal("2B_L3WsMqTc", b.VideoId);
            Assert.Equal("Test video", b.Title);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void RemoteFingerprintsRoundTrip()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings();
            s.RemoteFingerprints["device-id"] = "aa-bb-cc-dd-ee-ff-00-11";
            store.Save(s);
            Assert.Equal("aa-bb-cc-dd-ee-ff-00-11",
                store.Load().RemoteFingerprints["device-id"]);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void SpotifyBookmarksRoundTrip()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings();
            s.SpotifyBookmarks.Add(new SpotifyBookmark
            {
                Type = "playlist",
                Id = "4uLU6hMCjMI75M1A2tKUQC",
                Url = "https://open.spotify.com/playlist/4uLU6hMCjMI75M1A2tKUQC",
                Title = "Focus mix",
            });
            store.Save(s);
            var b = Assert.Single(store.Load().SpotifyBookmarks);
            Assert.Equal("playlist", b.Type);
            Assert.Equal("4uLU6hMCjMI75M1A2tKUQC", b.Id);
            Assert.Equal("Focus mix", b.Title);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void TargetsAndBoardsRoundTrip()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var s = new AppSettings();
            s.SignageTargets.Add("pi-a.local");
            s.SignageTargets.Add("pi-b.local");
            s.Boards.Add("Top 8 bracket");
            store.Save(s);
            var back = store.Load();
            Assert.Equal(new[] { "pi-a.local", "pi-b.local" }, back.SignageTargets);
            Assert.Contains("Top 8 bracket", back.Boards);
        }
        finally { System.IO.File.Delete(path); }
    }
}
