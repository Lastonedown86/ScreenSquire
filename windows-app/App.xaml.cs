using System.Windows;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class App : Application
{
    public static AppSettings Settings { get; } = new SettingsStore().Load();

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
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Something went wrong: " + args.Exception.Message + "\n\nThe app will keep running.",
                "Pi Signage Control", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
