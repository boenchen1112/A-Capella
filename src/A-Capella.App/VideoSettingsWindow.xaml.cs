using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using A_Capella.Engine.Composite;
using A_Capella.Engine.Project;
using Microsoft.Win32;

namespace A_Capella.App;

/// <summary>Edits the project's VideoSettings; Result is the edited copy (the input is never mutated).
/// Colors are typed as #RRGGBB with a live swatch; an invalid color blocks OK rather than being
/// silently replaced.</summary>
public partial class VideoSettingsWindow : Window
{
    private readonly VideoSettings _original;
    private string? _imagePath;

    public VideoSettings Result { get; private set; }

    public VideoSettingsWindow(VideoSettings current)
    {
        _original = current;
        Result = current;
        InitializeComponent();

        foreach (var ratio in Enum.GetValues<AspectRatioOption>())
            AspectRatioCombo.Items.Add(new ComboItem<AspectRatioOption>(ratio, VideoSettings.Describe(ratio)));
        Select(AspectRatioCombo, current.AspectRatio);

        (current.Fit == CellFit.Fill ? FillRadio : FitRadio).IsChecked = true;
        GutterSlider.Value = Math.Round(current.GutterFraction * 100, 1);

        BackgroundColorBox.Text = current.Background.Color;
        _imagePath = current.Background.ImagePath;
        BackgroundImageBox.Text = _imagePath ?? "(no image)";
        ImageModeCombo.Items.Add(new ComboItem<BackgroundImageMode>(BackgroundImageMode.Stretch, "Stretch image over the whole video"));
        ImageModeCombo.Items.Add(new ComboItem<BackgroundImageMode>(BackgroundImageMode.Tile, "Tile the image"));
        Select(ImageModeCombo, current.Background.ImageMode);

        UpdateSwatch(BackgroundSwatch, BackgroundColorBox);
    }

    private sealed record ComboItem<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.Cast<ComboItem<T>>().First(i => EqualityComparer<T>.Default.Equals(i.Value, value));

    private static T Selected<T>(ComboBox combo) => ((ComboItem<T>)combo.SelectedItem).Value;

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        text = text.Trim();
        if (text.Length != 7 || text[0] != '#') return false;
        try { color = (Color)ColorConverter.ConvertFromString(text); return true; }
        catch (FormatException) { return false; }
    }

    private static void UpdateSwatch(Border swatch, TextBox box)
    {
        swatch.Background = TryParseColor(box.Text, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
        box.BorderBrush = TryParseColor(box.Text, out _) ? null : Brushes.IndianRed;
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Fires during InitializeComponent/initial fill, before every swatch exists.
        if (BackgroundSwatch is null) return;
        UpdateSwatch(BackgroundSwatch, BackgroundColorBox);
    }

    private void GutterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetPercent(GutterValueText, e.NewValue);

    private static void SetPercent(TextBlock? label, double value)
    {
        if (label is not null) label.Text = $"{value:0.#}%";
    }

    private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*", Title = "Background image" };
        if (dialog.ShowDialog(this) != true) return;
        _imagePath = dialog.FileName;
        BackgroundImageBox.Text = _imagePath;
    }

    private void ClearImageButton_Click(object sender, RoutedEventArgs e)
    {
        _imagePath = null;
        BackgroundImageBox.Text = "(no image)";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (box, what) in new[] { (BackgroundColorBox, "Background") })
        {
            if (TryParseColor(box.Text, out _)) continue;
            MessageBox.Show(this, $"{what} color must look like #RRGGBB, for example #FF8800.", "Video settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
            return;
        }

        Result = _original with
        {
            AspectRatio = Selected<AspectRatioOption>(AspectRatioCombo),
            Fit = FillRadio.IsChecked == true ? CellFit.Fill : CellFit.Fit,
            GutterFraction = Math.Clamp(GutterSlider.Value / 100.0, 0, VideoSettings.MaxGutterFraction),
            Background = new BackgroundSettings
            {
                Color = BackgroundColorBox.Text.Trim(),
                ImagePath = _imagePath,
                ImageMode = Selected<BackgroundImageMode>(ImageModeCombo),
            },
        };
        DialogResult = true;
    }
}
