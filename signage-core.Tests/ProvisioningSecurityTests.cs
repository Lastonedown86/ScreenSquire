using System.Linq;
using PiSignage.Control;

namespace signage_core.Tests;

public sealed class ProvisioningSecurityTests
{
    [Fact]
    public void Production_provisioning_disables_vnc_and_wpf_gates_remote_control_behind_pairing()
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

        // Task 6 wires the remote-control button into the UI; the invariant that
        // still matters here is that it ships disabled and only the paired,
        // signed connect path (verified in MainWindow.xaml.cs) turns it on.
        var remoteButtonLine = xaml
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.Contains("x:Name=\"BtnRemote\"", StringComparison.Ordinal));
        Assert.NotNull(remoteButtonLine);
        Assert.Contains("IsEnabled=\"False\"", remoteButtonLine, StringComparison.Ordinal);

        // Pin the runtime gate itself, not just the static default: if a future
        // change enables BtnRemote unconditionally (paired or not), this must fail.
        Assert.Contains(
            "BtnRemote.IsEnabled = _connectedControlContext is not null",
            codeBehind,
            StringComparison.Ordinal);
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
