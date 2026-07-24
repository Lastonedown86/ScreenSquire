using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PiSignage.Control;

public enum ToastKind { Info, Success, Warning, Error }

// Non-blocking notification chip. Last-wins: a new Show() replaces the current
// message and restarts the hide timer.
public partial class Toast : UserControl
{
    readonly DispatcherTimer _hide = new();

    public Toast()
    {
        InitializeComponent();
        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
    }

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        Msg.Text = message;
        Chip.Background = (Brush)FindResource(kind switch
        {
            ToastKind.Success => "Success",
            ToastKind.Warning => "Warning",
            ToastKind.Error => "Error",
            _ => "StatusBarBg",
        });
        Chip.BeginAnimation(OpacityProperty, null);   // cancel a running fade
        Chip.Opacity = 1;
        Chip.Visibility = Visibility.Visible;
        _hide.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 6 : 4);
        _hide.Stop();
        _hide.Start();
    }

    void FadeOut()
    {
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        anim.Completed += (_, _) => { Chip.Visibility = Visibility.Collapsed; Chip.BeginAnimation(OpacityProperty, null); Chip.Opacity = 1; };
        Chip.BeginAnimation(OpacityProperty, anim);
    }
}
