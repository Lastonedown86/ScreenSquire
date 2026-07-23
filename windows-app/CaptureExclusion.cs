using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PiSignage.Control;

static class CaptureExclusion
{
    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;  // Win10 2004+: window renders normally but is skipped by screen capture

    // Make this window invisible to screen capture (CopyFromScreen sees through it),
    // without hiding it or changing z-order — so grabbing the screen never includes
    // our own app chrome, and we don't need to Hide()/fade the window.
    public static void ExcludeFromCapture(this Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
    }
}
