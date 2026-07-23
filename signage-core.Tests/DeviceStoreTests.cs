using PiSignage.Signage;
using Xunit;

public class DeviceStoreTests
{
    static string TempFile() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"dev-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var path = TempFile();
        try
        {
            var store = new DeviceStore(path);
            store.Save(new[] { new SavedDevice { Name = "Front TV", Hostname = "pisignage1", Ip = "192.168.0.58" } });
            var got = store.Load();
            Assert.Single(got);
            Assert.Equal("Front TV", got[0].Name);
            Assert.Equal("pisignage1", got[0].Hostname);
            Assert.Equal("192.168.0.58", got[0].Ip);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void MissingFileLoadsEmpty()
    {
        Assert.Empty(new DeviceStore(TempFile()).Load());
    }

    [Fact]
    public void CorruptFileLoadsEmpty()
    {
        var path = TempFile();
        try { System.IO.File.WriteAllText(path, "{not json"); Assert.Empty(new DeviceStore(path).Load()); }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void UpsertUpdatesIpKeepsEditedNameByHostname()
    {
        var list = new List<SavedDevice> { new() { Name = "Front TV", Hostname = "pisignage1", Ip = "192.168.0.58" } };
        // same hostname, new IP, no new name -> keep "Front TV", update IP
        list = DeviceStore.Upsert(list, new SavedDevice { Name = "", Hostname = "PISIGNAGE1", Ip = "192.168.0.99" });
        Assert.Single(list);
        Assert.Equal("Front TV", list[0].Name);
        Assert.Equal("192.168.0.99", list[0].Ip);
    }

    [Fact]
    public void UpsertAddsNewDevice()
    {
        var list = new List<SavedDevice> { new() { Name = "Front TV", Hostname = "pisignage1", Ip = "192.168.0.58" } };
        list = DeviceStore.Upsert(list, new SavedDevice { Name = "pisignage2", Hostname = "pisignage2", Ip = "192.168.0.71" });
        Assert.Equal(2, list.Count);
    }
}
