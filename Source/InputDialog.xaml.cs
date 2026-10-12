using System.Windows;

namespace PoGGaduchy;

/// <summary>Proste okno z jednym polem tekstowym (np. nazwa nowej osoby).</summary>
public partial class InputDialog : Window
{
    public string Value => Box.Text.Trim();

    public InputDialog(string prompt, string okText = "OK", string initial = "")
    {
        InitializeComponent();
        Prompt.Text = prompt;
        OkButton.Content = okText;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) { Box.Focus(); return; }
        DialogResult = true;
    }

    public static string? Ask(Window owner, string prompt, string okText = "OK", string initial = "")
    {
        var d = new InputDialog(prompt, okText, initial) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }
}
