using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PiSignage.Control;

public partial class RegionSelectorWindow : Window
{
    Point _start;
    bool _dragging;
    public (int x, int y, int w, int h)? Result { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Result = null; DialogResult = false; } };
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Canvas); _dragging = true;
        Canvas.SetLeft(Sel, _start.X); Canvas.SetTop(Sel, _start.Y);
        Sel.Width = 0; Sel.Height = 0; Sel.Visibility = Visibility.Visible;
    }

    void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Canvas);
        var (x, y, w, h) = PiSignage.Signage.CaptureGeometry.Normalize(
            ((int)_start.X, (int)_start.Y), ((int)p.X, (int)p.Y));
        Canvas.SetLeft(Sel, x); Canvas.SetTop(Sel, y); Sel.Width = w; Sel.Height = h;
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        _dragging = false;
        var p = e.GetPosition(Canvas);
        var (x, y, w, h) = PiSignage.Signage.CaptureGeometry.Normalize(
            ((int)_start.X, (int)_start.Y), ((int)p.X, (int)p.Y));
        // WPF units -> device pixels (DPI scale)
        var m = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice;
        Result = ((int)(x * m.M11), (int)(y * m.M22), (int)(w * m.M11), (int)(h * m.M22));
        DialogResult = w > 0 && h > 0;
    }
}
