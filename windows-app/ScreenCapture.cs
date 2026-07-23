using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PiSignage.Control;

public static class ScreenCapture
{
    // Captures a screen rectangle (device pixels) and returns PNG bytes.
    public static byte[] CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new ArgumentException("Empty capture region");
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
