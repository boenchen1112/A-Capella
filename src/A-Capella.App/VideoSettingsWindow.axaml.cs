using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using A_Capella.Engine.Composite;
using A_Capella.Engine.Project;

namespace A_Capella.App;

/// <summary>Edits the project's VideoSettings; Result is the edited copy (the input is never mutated).
/// Colors are typed as #RRGGBB with a live swatch; an invalid color blocks OK rather than being
/// silently replaced.</summary>
public partial class VideoSettingsWindow : Window
{
    private readonly VideoSettings _original;
    private string? _imagePath;

    public VideoSettings Result { get; private set; }

    public VideoSettingsWindow() : this(new VideoSettings()) { }

    public VideoSettingsWindow(VideoSettings current)
    {
        _original = current;
        Result = current;
        InitializeComponent();

        foreach (var ratio in Enum.GetValues<AspectRatioOption>())
            AspectRatioCombo.Items.Add(new ComboItem<AspectRatioOption>(ratio, VideoSettings.Describe(ratio)));
        Select(AspectRatioCombo, current.AspectRatio);

        (current.Fit == CellFit.Fill ? FillRadio : FitRadio).IsChecked = true;
        GutterSlider.ValueChanged += (_, e) => SetPercent(GutterValueText, e.NewValue);
        GutterSlider.Value = Math.Round(current.GutterFraction * 100, 1);
        SetPercent(GutterValueText, GutterSlider.Value);

        BackgroundColorBox.TextChanged += (_, _) => UpdateSwatch(BackgroundSwatch, BackgroundColorBox);
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

    private static T Selected<T>(ComboBox combo) => ((ComboItem<T>)combo.SelectedItem!).Value;

    private static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        text = text?.Trim() ?? "";
        return text.Length == 7 && text[0] == '#' && Color.TryParse(text, out color);
    }

    private static void UpdateSwatch(Border swatch, TextBox box)
    {
        bool valid = TryParseColor(box.Text, out var c);
        swatch.Background = valid ? new SolidColorBrush(c) : Brushes.Transparent;
        if (valid) box.ClearValue(TemplatedControl.BorderBrushProperty);
        else box.BorderBrush = Brushes.IndianRed;
    }

    private static void SetPercent(TextBlock? label, double value)
    {
        if (label is not null) label.Text = $"{value:0.#}%";
    }

    private async void BrowseImageButton_Click(object? sender, RoutedEventArgs e)
    {
        string? path = await Dialogs.PickFileAsync(this, "Background image", Dialogs.ImageFiles);
        if (path is null) return;
        _imagePath = path;
        BackgroundImageBox.Text = _imagePath;
    }

    private void ClearImageButton_Click(object? sender, RoutedEventArgs e)
    {
        _imagePath = null;
        BackgroundImageBox.Text = "(no image)";
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var (box, what) in new[] { (BackgroundColorBox, "Background") })
        {
            if (TryParseColor(box.Text, out _)) continue;
            await Dialogs.ShowMessageAsync(this, $"{what} color must look like #RRGGBB, for example #FF8800.", "Video settings");
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
                Color = (BackgroundColorBox.Text ?? "").Trim(),
                ImagePath = _imagePath,
                ImageMode = Selected<BackgroundImageMode>(ImageModeCombo),
            },
        };
        Close(true);
    }
}
