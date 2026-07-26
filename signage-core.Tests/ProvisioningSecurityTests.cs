using PiSignage.Control;

namespace signage_core.Tests;

public sealed class ProvisioningSecurityTests
{
    [Fact]
    public void Production_provisioning_disables_vnc_and_wpf_exposes_no_vnc_launcher()
    {
        var root = RepositoryRoot();
        var provisioning = File.ReadAllText(
            Path.Combine(root, "pi-setup", "provision-usb.sh"));
        var xaml = File.ReadAllText(
            Path.Combine(root, "windows-app", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(
            Path.Combine(root, "windows-app", "MainWindow.xaml.cs"));

        Assert.Contains("do_vnc 1", provisioning, StringComparison.Ordinal);
        Assert.Contains(
            provisioning.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries),
            line => line.Trim() == "sudo raspi-config nonint do_vnc 1");
        Assert.DoesNotContain(
            "do_vnc 1 || true",
            provisioning,
            StringComparison.Ordinal);
        Assert.DoesNotContain("do_vnc 0", provisioning, StringComparison.Ordinal);
        Assert.DoesNotContain("wayvnc", provisioning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BtnRemote", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VncLauncher", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_agent_bundle_contains_both_nonempty_display_pages()
    {
        var files = AgentBundle.Files();

        Assert.True(files.TryGetValue("static/kiosk.html", out var kiosk));
        Assert.NotEmpty(kiosk);
        Assert.True(files.TryGetValue("static/dashboard.html", out var dashboard));
        Assert.NotEmpty(dashboard);
    }

    static string RepositoryRoot() =>
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\"));
}
