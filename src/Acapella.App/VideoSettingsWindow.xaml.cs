using System.Windows;
using Acapella.Engine.Composite;
using Acapella.Engine.Project;

namespace Acapella.App;

/// <summary>Edits the project's VideoSettings; Result is the edited copy (the input is never mutated).</summary>
public partial class VideoSettingsWindow : Window
{
    private readonly VideoSettings _original;

    public VideoSettings Result { get; private set; }

    public VideoSettingsWindow(VideoSettings current)
    {
        _original = current;
        Result = current;
        InitializeComponent();

        foreach (var ratio in Enum.GetValues<AspectRatioOption>())
            AspectRatioCombo.Items.Add(new ComboItem(ratio, VideoSettings.Describe(ratio)));
        AspectRatioCombo.SelectedItem = AspectRatioCombo.Items.Cast<ComboItem>().First(i => i.Value == current.AspectRatio);

        (current.Fit == CellFit.Fill ? FillRadio : FitRadio).IsChecked = true;
        GutterSlider.Value = Math.Round(current.GutterFraction * 100, 1);
    }

    private sealed record ComboItem(AspectRatioOption Value, string Label)
    {
        public override string ToString() => Label;
    }

    private void GutterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GutterValueText is not null)
            GutterValueText.Text = $"{GutterSlider.Value:0.#}%";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = _original with
        {
            AspectRatio = ((ComboItem)AspectRatioCombo.SelectedItem).Value,
            Fit = FillRadio.IsChecked == true ? CellFit.Fill : CellFit.Fit,
            GutterFraction = Math.Clamp(GutterSlider.Value / 100.0, 0, VideoSettings.MaxGutterFraction),
        };
        DialogResult = true;
    }
}
