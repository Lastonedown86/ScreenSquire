using System.Windows;
using PiSignage.Signage;

namespace PiSignage.Control;

// One-click "sign in on the TV": start a remote-desktop session, confirm the
// server fingerprint, have the agent swap the kiosk for a windowed browser on
// the kiosk profile at the site's page, and launch the bundled viewer. When
// the operator closes the viewer, put everything back. Shared by the Spotify
// and YouTube windows; mirrors MainWindow.BtnRemote_Click's shape.
public static class TvSigninFlow
{
    public static async Task RunAsync(
        Window owner, Toast toaster, ApiClient api, ControlContext ctx,
        SavedDevice dev, string signinApiPath, string siteName, Action done)
    {
        var viewer = RemoteViewerLauncher.BundledViewerPath();
        if (!System.IO.File.Exists(viewer))
        {
            toaster.Show("Remote viewer is missing from this install.", ToastKind.Error);
            done();
            return;
        }
        try
        {
            var session = await api.StartRemoteDesktopAsync(ctx);
            if (session == null)
            {
                toaster.Show("The Pi did not start remote control.", ToastKind.Error);
                done();
                return;
            }
            if (!RemoteTrust.ConfirmServerFingerprint(owner, dev.DeviceId, session))
            {
                try { await api.StopRemoteDesktopAsync(ctx); } catch { }
                done();
                return;
            }
            try
            {
                await api.StartSigninAsync(signinApiPath, ctx);
            }
            catch (Exception ex)
            {
                toaster.Show("Couldn't open the sign-in page: " + ex.Message +
                             " — if this Pi's software is older, click 'Update Pi software' on the main screen.",
                             ToastKind.Error);
                try { await api.StopRemoteDesktopAsync(ctx); } catch { }
                done();
                return;
            }
            toaster.Show($"Sign in to {siteName} in the viewer window, then close it.", ToastKind.Success);
            var proc = RemoteViewerLauncher.Launch(viewer, dev.Ip, session);
            _ = Task.Run(async () =>
            {
                // done always runs; the agent's idle timeout is the backstop
                // if these stops never arrive.
                try
                {
                    await proc.WaitForExitAsync();
                    try { await api.StopSigninAsync(signinApiPath, ctx); } catch { }
                    try { await api.StopRemoteDesktopAsync(ctx); } catch { }
                }
                finally { owner.Dispatcher.Invoke(done); }
            });
        }
        catch (Exception ex)
        {
            toaster.Show("Couldn't start sign-in: " + ex.Message, ToastKind.Error);
            done();
        }
    }
}
