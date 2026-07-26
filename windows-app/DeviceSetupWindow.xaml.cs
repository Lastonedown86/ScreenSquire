namespace PiSignage.Control;

// Dumb container for the setup/management buttons moved off the main bar.
// MainWindow wires its existing Click handlers and drives enabled/visible state.
public partial class DeviceSetupWindow : System.Windows.Window
{
    public DeviceSetupWindow() => InitializeComponent();
}
