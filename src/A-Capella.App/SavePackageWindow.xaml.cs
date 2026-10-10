using System.Windows;
using System.Windows.Input;
using A_Capella.Engine.Presets;

namespace A_Capella.App;

/// <summary>Asks for a package name and checks it: not blank, not a built-in package's name, and
/// (after confirming) free to overwrite one of the user's own.</summary>
public partial class SavePackageWindow : Window
{
    private readonly FxPackageService _packages;

    public SavePackageWindow(FxPackageService packages)
    {
        _packages = packages;
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string PackageName { get; private set; } = "";

    private void NameBox_KeyDown(object sender, KeyEventArgs e) => ErrorText.Text = "";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = "Type a name first."; return; }
        if (_packages.IsBuiltInName(name)) { ErrorText.Text = $"\"{name}\" is a built-in package. Pick another name."; return; }
        if (_packages.Exists(name) &&
            MessageBox.Show(this, $"A package named \"{name}\" already exists. Replace it?", "Save as package",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        PackageName = name;
        DialogResult = true;
    }
}
