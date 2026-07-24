using PiSignage.Signage;
using Xunit;

public class VncLauncherTests
{
    [Fact]
    public void TargetFormatsIpAndPort()
    {
        Assert.Equal("192.168.0.58::5900", VncLauncher.Target("192.168.0.58"));
        Assert.Equal("10.0.0.5::5901", VncLauncher.Target("10.0.0.5", 5901));
    }

    [Fact]
    public void FindViewerReturnsFirstExisting()
    {
        var real = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vnc-{System.Guid.NewGuid():N}.exe");
        System.IO.File.WriteAllText(real, "x");
        try
        {
            var found = VncLauncher.FindViewer(new[] { @"C:\nope\vncviewer.exe", real });
            Assert.Equal(real, found);
        }
        finally { System.IO.File.Delete(real); }
    }

    [Fact]
    public void FindViewerReturnsNullWhenNoneExist()
    {
        Assert.Null(VncLauncher.FindViewer(new[] { @"C:\nope1\vncviewer.exe", @"C:\nope2\vncviewer.exe" }));
    }

    [Fact]
    public void TigerVncLaunchesWindowed()
    {
        var args = VncLauncher.BuildLaunchArgs(@"C:\Program Files\TigerVNC\vncviewer.exe", "192.168.0.58");
        Assert.Contains("192.168.0.58::5900", args);
        Assert.Contains("-FullScreen=0", args);
    }

    [Fact]
    public void NonTigerViewerGetsPlainTarget()
    {
        var args = VncLauncher.BuildLaunchArgs(@"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe", "10.0.0.5");
        Assert.Equal("10.0.0.5::5900", args);
    }
}
