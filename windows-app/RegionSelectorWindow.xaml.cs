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
    (int x, int y, int w, int h)? _sel;   // current selection in WPF units
    public (int x, int y, int w, int h)? Result { get; private set; }

    // Shown in the top-left hint, e.g. "Drag a box around the standings — …"
    public string Instruction { set => Hint.Text = value; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => this.ExcludeFromCapture();   // overlay never lands in a screenshot
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Canvas); _dragging = true; _sel = null;
        Canvas.SetLeft(Sel, _start.X); Canvas.SetTop(Sel, _start.Y);
        Sel.Width = 0; Sel.Height = 0; Sel.Visibility = Visibility.Visible;
    }

    void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Canvas);
        _sel = PiSignage.Signage.CaptureGeometry.Normalize(
            ((int)_start.X, (int)_start.Y), ((int)p.X, (int)p.Y));
        Apply();
    }

    // Release no longer commits — it enters an adjust step so the box can be
    // nudged pixel-perfect (or re-dragged) before Enter confirms.
    void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var p = e.GetPosition(Canvas);
        _sel = PiSignage.Signage.CaptureGeometry.Normalize(
            ((int)_start.X, (int)_start.Y), ((int)p.X, (int)p.Y));
        Apply();
        if (_sel is { w: > 0, h: > 0 })
            Hint.Text = "Arrow keys nudge, Shift+arrows resize, Enter to confirm, Esc to cancel — or drag again";
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = null; DialogResult = false; return; }

        if (e.Key == Key.Enter && _sel is { w: > 0, h: > 0 } r)
        {
            // WPF units -> device pixels (DPI scale)
            var m = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice;
            Result = ((int)(r.x * m.M11), (int)(r.y * m.M22), (int)(r.w * m.M11), (int)(r.h * m.M22));
            DialogResult = true;
            return;
        }

        if (_sel is not { } cur) return;
        int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (dx == 0 && dy == 0) return;
        bool resize = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _sel = resize
            ? (cur.x, cur.y, System.Math.Max(1, cur.w + dx), System.Math.Max(1, cur.h + dy))
            : (cur.x + dx, cur.y + dy, cur.w, cur.h);
        Apply();
        e.Handled = true;
    }

    void Apply()
    {
        if (_sel is not { } r) return;
        Canvas.SetLeft(Sel, r.x); Canvas.SetTop(Sel, r.y);
        Sel.Width = r.w; Sel.Height = r.h;

        var m = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice;
        Dims.Text = $"{(int)(r.w * m.M11)} × {(int)(r.h * m.M22)} px";
        Dims.Visibility = Visibility.Visible;
        Canvas.SetLeft(Dims, r.x);
        Canvas.SetTop(Dims, System.Math.Max(2, r.y - 22));   // sit just above the box
    }
}
