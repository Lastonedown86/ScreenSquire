using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class App : Application
{
    public static AppSettings Settings { get; } = new SettingsStore().Load();

    /// <summary>This build's version. 0.0.0 means nobody stamped it, i.e. a
    /// developer build, which never self-updates.</summary>
    public static Version CurrentVersion =>
        typeof(App).Assembly.GetName().Version ?? AppUpdate.DevBuild;

    /// <summary>Sits beside devices.json and settings.json.</summary>
    public static string UpdateStageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PiSignage", "updates");

    public static string ExePath { get; } = Environment.ProcessPath ?? "";

    public static void SaveSettings()
    {
        try { new SettingsStore().Save(Settings); }
        catch { /* losing a preference is not worth a crash */ }
    }

    // Restore a window's saved position/size and save it back on close.
    public static void TrackPlacement(Window w, string key)
    {
        w.SourceInitialized += (_, _) =>
        {
            if (!Settings.Windows.TryGetValue(key, out var p)) return;
            // skip stale geometry from a disconnected monitor
            if (p.Left < SystemParameters.VirtualScreenLeft ||
                p.Top < SystemParameters.VirtualScreenTop ||
                p.Left + p.Width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth ||
                p.Top + p.Height > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                return;
            w.Left = p.Left; w.Top = p.Top; w.Width = p.Width; w.Height = p.Height;
        };
        w.Closing += (_, _) =>
        {
            if (w.WindowState != WindowState.Normal) return;   // don't save maximized/minimized geometry
            Settings.Windows[key] = new WindowPlacement { Left = w.Left, Top = w.Top, Width = w.Width, Height = w.Height };
            SaveSettings();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A freshly downloaded build is launched with this flag before anything is
        // swapped. Exiting 0 is the whole test: it proves the binary actually
        // starts on this machine -- not truncated, not quarantined by antivirus.
        //
        // Environment.Exit, not Shutdown: Shutdown asks WPF to tear down, and a
        // teardown this early unhooks window subclasses through a native
        // WindowsBase entry point that a single-file bundle cannot resolve yet.
        // The published build died there with DllNotFoundException (0xC000041D),
        // so verifyLaunch saw a non-zero exit and no release could ever stage.
        // Nothing is initialised at this point, so there is nothing to unwind.
        if (e.Args.Contains("--selftest"))
        {
            Environment.Exit(0);
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Something went wrong: " + args.Exception.Message + "\n\nThe app will keep running.",
                "Pi Signage Control", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        ApplyPendingUpdate();
    }

    // Runs before any window: swapping the exe is a file rename, and doing it
    // first means the user only ever sees the version they end up running.
    void ApplyPendingUpdate()
    {
        if (string.IsNullOrEmpty(ExePath)) return;

        // Two copies of the app can be open at once, and both reach this line.
        using var mutex = new Mutex(false, @"Local\PiSignageControl.Update." + PathKey(ExePath));
        var held = false;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { held = true; }   // holder died mid-swap
        if (!held) return;

        try
        {
            switch (AppUpdateInstaller.TryApplyPending(ExePath, UpdateStageDir, CurrentVersion))
            {
                case AppUpdateInstaller.Outcome.Applied:
                    Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
                    Shutdown();
                    return;
                case AppUpdateInstaller.Outcome.Nothing:
                    // Nothing waiting, so this build is the settled one and the
                    // previous version beside it is no longer needed.
                    AppUpdateInstaller.CommitPreviousLaunch(ExePath);
                    return;
                default:
                    return;   // blocked; next launch tries again
            }
        }
        catch { /* never let an update failure stop the app from opening */ }
        finally { try { mutex.ReleaseMutex(); } catch { } }
    }

    // Mutex names cannot contain a path separator, and the install location is
    // what two instances actually contend over.
    static string PathKey(string path) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16];
}
