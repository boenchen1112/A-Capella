using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Acapella.Engine.Composite;
using Acapella.Engine.Project;
using Microsoft.Win32;

namespace Acapella.App;

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

        var w = current.Watermark;
        WatermarkEnabledCheck.IsChecked = w.Enabled;
        WatermarkTextBox.Text = w.Text;
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!fonts.Contains(w.FontFamily)) fonts.Insert(0, w.FontFamily);   // a font from another machine stays selectable
        foreach (var font in fonts) FontCombo.Items.Add(font);
        FontCombo.SelectedItem = w.FontFamily;
        SizeSlider.Value = Math.Round(w.SizeFraction * 100, 1);
        FillColorBox.Text = w.FillColor;
        StrokeColorBox.Text = w.StrokeColor;
        StrokeSlider.Value = Math.Round(w.StrokeFraction * 100, 1);
        OpacitySlider.Value = Math.Round(w.Opacity * 100);
        ShadowCheck.IsChecked = w.Shadow;
        foreach (var (corner, label) in new[]
        {
            (WatermarkCorner.TopLeft, "Top left"), (WatermarkCorner.TopRight, "Top right"),
            (WatermarkCorner.BottomLeft, "Bottom left"), (WatermarkCorner.BottomRight, "Bottom right"),
            (WatermarkCorner.Center, "Center"),
        })
            CornerCombo.Items.Add(new ComboItem<WatermarkCorner>(corner, label));
        Select(CornerCombo, w.Corner);

        UpdateSwatch(BackgroundSwatch, BackgroundColorBox);
        UpdateSwatch(FillSwatch, FillColorBox);
        UpdateSwatch(StrokeSwatch, StrokeColorBox);
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
        if (BackgroundSwatch is null || FillSwatch is null || StrokeSwatch is null) return;
        if (sender == BackgroundColorBox) UpdateSwatch(BackgroundSwatch, BackgroundColorBox);
        else if (sender == FillColorBox) UpdateSwatch(FillSwatch, FillColorBox);
        else if (sender == StrokeColorBox) UpdateSwatch(StrokeSwatch, StrokeColorBox);
    }

    private void GutterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetPercent(GutterValueText, e.NewValue);
    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetPercent(SizeValueText, e.NewValue);
    private void StrokeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetPercent(StrokeValueText, e.NewValue);
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetPercent(OpacityValueText, e.NewValue);

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
        foreach (var (box, what) in new[] { (BackgroundColorBox, "Background"), (FillColorBox, "Text"), (StrokeColorBox, "Outline") })
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
            Watermark = new WatermarkSettings
            {
                Enabled = WatermarkEnabledCheck.IsChecked == true,
                Text = WatermarkTextBox.Text,
                FontFamily = FontCombo.SelectedItem as string ?? _original.Watermark.FontFamily,
                SizeFraction = SizeSlider.Value / 100.0,
                FillColor = FillColorBox.Text.Trim(),
                StrokeColor = StrokeColorBox.Text.Trim(),
                StrokeFraction = StrokeSlider.Value / 100.0,
                Shadow = ShadowCheck.IsChecked == true,
                Opacity = OpacitySlider.Value / 100.0,
                Corner = Selected<WatermarkCorner>(CornerCombo),
            },
        };
        DialogResult = true;
    }
}
