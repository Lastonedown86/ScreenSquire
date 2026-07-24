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
            s.Windows["Main"] = new WindowPlacement { Left = 10, Top = 20, Width = 960, Height = 640 };
            s.Regions["pairings"] = new RegionRect { X = 1, Y = 2, W = 300, H = 200 };
            store.Save(s);

            var got = store.Load();
            Assert.Equal("pisignage1", got.LastDeviceHostname);
            Assert.Equal("pisignage2", got.SignageTarget);
            Assert.Equal(40, got.TimerMinutes);
            Assert.Equal(960, got.Windows["Main"].Width);
            Assert.Equal(300, got.Regions["pairings"].W);
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
}
