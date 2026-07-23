using System.Windows;

namespace PiSignage.Control;

public partial class TextPrompt : Window
{
    public string Value => Input.Text.Trim();

    public TextPrompt(string prompt, string initial)
    {
        InitializeComponent();
        Prompt.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.SelectAll(); Input.Focus(); };
    }

    void Ok_Click(object s, RoutedEventArgs e) => DialogResult = true;

    // Returns the entered value, or null if cancelled/empty.
    public static string? Ask(Window owner, string prompt, string initial)
    {
        var d = new TextPrompt(prompt, initial) { Owner = owner };
        return d.ShowDialog() == true && d.Value.Length > 0 ? d.Value : null;
    }
}
