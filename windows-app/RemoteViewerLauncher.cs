using System.Collections.Generic;
using System.Diagnostics;

namespace PiSignage.Control;

public static class RemoteViewerLauncher
{
    public static (string exePath, string args, IDictionary<string, string> env) BuildLaunch(
        string viewerExe, string host, RemoteDesktopSession session)
    {
        var args = $"SecurityTypes=RA2ne,RA2 {host}::{session.Port}";
        var env = new Dictionary<string, string>
        {
            ["VNC_USERNAME"] = session.Username,
            ["VNC_PASSWORD"] = session.Password,
        };
        return (viewerExe, args, env);
    }

    public static Process Launch(string viewerExe, string host, RemoteDesktopSession session)
    {
        var (exe, args, env) = BuildLaunch(viewerExe, host, session);
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi)!;
    }
}
