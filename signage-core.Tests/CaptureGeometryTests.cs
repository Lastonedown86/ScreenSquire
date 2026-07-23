using PiSignage.Signage;
using Xunit;

public class CaptureGeometryTests
{
    [Fact]
    public void NormalizeHandlesReverseDrag()
    {
        var (x, y, w, h) = CaptureGeometry.Normalize((300, 200), (100, 50));
        Assert.Equal(100, x); Assert.Equal(50, y);
        Assert.Equal(200, w); Assert.Equal(150, h);
    }

    [Fact]
    public void NormalizeForwardDrag()
    {
        var (x, y, w, h) = CaptureGeometry.Normalize((10, 20), (110, 220));
        Assert.Equal(10, x); Assert.Equal(20, y);
        Assert.Equal(100, w); Assert.Equal(200, h);
    }
}
