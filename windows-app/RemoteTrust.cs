using System.Windows;

namespace PiSignage.Control;

// Trust-on-first-use for the Pi's remote-control key, mirroring the viewer's
// own prompt but with the expected value delivered over the signed channel.
// Silent when the key matches what we accepted before; loud when it changed.
// Shared by the main window's Remote control and the Spotify sign-in flow.
public static class RemoteTrust
{
    public static bool ConfirmServerFingerprint(
        Window owner, string? deviceId, RemoteDesktopSession session)
    {
        var fp = session.Fingerprint;
        if (string.IsNullOrEmpty(fp)) return true;   // pre-fingerprint agent
        if (string.IsNullOrWhiteSpace(deviceId)) return true;
        App.Settings.RemoteFingerprints.TryGetValue(deviceId, out var known);
        if (known == fp) return true;
        if (known == null)
        {
            if (MessageBox.Show(owner,
                    "First remote session with this Pi.\n\n" +
                    "If the viewer asks you to verify the server, the fingerprint it shows must be exactly:\n\n" +
                    fp + "\n\nContinue?",
                    "Verify remote server", MessageBoxButton.YesNo,
                    MessageBoxImage.Information) != MessageBoxResult.Yes)
                return false;
        }
        else
        {
            if (MessageBox.Show(owner,
                    "WARNING: this Pi's remote-control key has CHANGED.\n\n" +
                    $"Expected: {known}\nReported: {fp}\n\n" +
                    "That's normal only if the Pi was re-flashed or reset. " +
                    "If you didn't do either, don't continue — something else may be answering as this Pi.\n\n" +
                    "Continue anyway?",
                    "Remote server key changed", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
        }
        App.Settings.RemoteFingerprints[deviceId] = fp;
        App.SaveSettings();
        return true;
    }
}
