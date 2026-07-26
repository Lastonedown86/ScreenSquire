using PiSignage.Control;
using Xunit;

public class RemoteViewerLauncherTests
{
    [Fact]
    public void BuildLaunch_puts_credentials_in_env_not_args()
    {
        var s = new RemoteDesktopSession(5900, "user1", "pass1");
        var (exe, args, env) = RemoteViewerLauncher.BuildLaunch(@"C:\vnc\vncviewer.exe", "192.168.0.58", s);

        Assert.Equal(@"C:\vnc\vncviewer.exe", exe);
        Assert.Contains("SecurityTypes=RA2ne,RA2", args);
        Assert.Contains("192.168.0.58::5900", args);
        Assert.DoesNotContain("pass1", args);
        Assert.Equal("user1", env["VNC_USERNAME"]);
        Assert.Equal("pass1", env["VNC_PASSWORD"]);
    }

    [Fact]
    public void BundledViewerPath_is_next_to_the_app()
    {
        Assert.EndsWith("vncviewer.exe", RemoteViewerLauncher.BundledViewerPath());
    }
}
