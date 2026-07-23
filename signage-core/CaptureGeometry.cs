namespace PiSignage.Signage;

public static class CaptureGeometry
{
    public static (int x, int y, int w, int h) Normalize((int x, int y) a, (int x, int y) b)
    {
        int x = Math.Min(a.x, b.x), y = Math.Min(a.y, b.y);
        int w = Math.Abs(a.x - b.x), h = Math.Abs(a.y - b.y);
        return (x, y, w, h);
    }
}
