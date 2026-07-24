namespace PiSignage.Signage;

// Finds an installed VNC viewer and builds the connection target for a Pi.
// The app shells out to the viewer; this class holds the pure, testable bits.
public static class VncLauncher
{
    public const int Port = 5900;   // wayvnc default on the Pi

    // Common install locations for RealVNC / TigerVNC / UltraVNC viewers on Windows.
    public static IEnumerable<string> DefaultViewerPaths()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return new[]
        {
            // TigerVNC first: it supports wayvnc's RSA-AES security (TightVNC does not).
            Path.Combine(pf,  "TigerVNC", "vncviewer.exe"),
            Path.Combine(pf86, "TigerVNC", "vncviewer.exe"),
            Path.Combine(pf,  "RealVNC", "VNC Viewer", "vncviewer.exe"),
            Path.Combine(pf86, "RealVNC", "VNC Viewer", "vncviewer.exe"),
            Path.Combine(pf,  "TightVNC", "tvnviewer.exe"),
            Path.Combine(pf,  "uvnc bvba", "UltraVNC", "vncviewer.exe"),
        };
    }

    // winget id for the viewer we recommend installing (wayvnc-compatible).
    public const string ViewerWingetId = "TigerVNC.TigerVNC";

    // First candidate that exists on disk, or null if no viewer is installed.
    public static string? FindViewer(IEnumerable<string> candidatePaths)
        => candidatePaths.FirstOrDefault(File.Exists);

    // Viewer target argument: "<ip>::<port>" (the double colon forces a raw port,
    // understood by RealVNC and TigerVNC alike).
    public static string Target(string ip, int port = Port) => $"{ip}::{port}";
}
