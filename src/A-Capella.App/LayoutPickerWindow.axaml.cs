using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using A_Capella.Engine.Composite;

namespace A_Capella.App;

/// <summary>Grid of layout thumbnails grouped by cell count. Layouts with fewer cells than the
/// project's layers need are shown disabled rather than hidden, so it's clear why they can't be picked.</summary>
public partial class LayoutPickerWindow : Window
{
    private const double ThumbWidth = 96;
    private const double ThumbHeight = 54;

    public string? SelectedLayoutId { get; private set; }

    public LayoutPickerWindow() : this("", 1) { }

    public LayoutPickerWindow(string currentLayoutId, int requiredCells)
    {
        InitializeComponent();
        HintText.Text = requiredCells > 1
            ? $"Your layers use {requiredCells} cells, so layouts with fewer cells are unavailable."
            : "Pick how the layers are arranged in the video.";

        foreach (var group in GridLayoutCatalog.Layouts.GroupBy(l => l.Cells.Count).OrderBy(g => g.Key))
        {
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = group.Key == 1 ? "1 cell" : $"{group.Key} cells",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 4),
            });
            var wrap = new WrapPanel();
            foreach (var layout in group)
                wrap.Children.Add(CreateTile(layout, layout.Id == currentLayoutId, layout.Cells.Count >= requiredCells));
            GroupsPanel.Children.Add(wrap);
        }
    }

    private Button CreateTile(GridLayout layout, bool isCurrent, bool enabled)
    {
        var canvas = new Canvas { Width = ThumbWidth, Height = ThumbHeight, Background = Brushes.Black };
        for (int i = 0; i < layout.Cells.Count; i++)
        {
            var r = layout.GetCellRect(i, (int)ThumbWidth, (int)ThumbHeight, gutterFraction: 0.06);
            var rect = new Rectangle
            {
                Width = Math.Max(1, r.Width),
                Height = Math.Max(1, r.Height),
                Fill = new SolidColorBrush(ToAvalonia(LayerColorPalette.GetColor(i))),
                Opacity = enabled ? 0.9 : 0.35,
            };
            Canvas.SetLeft(rect, r.Left);
            Canvas.SetTop(rect, r.Top);
            canvas.Children.Add(rect);
        }

        var button = new Button
        {
            Content = canvas,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(3),
            IsEnabled = enabled,
            BorderThickness = new Thickness(2),
            BorderBrush = (IBrush)this.FindResource(isCurrent ? "LayerAccent2Brush" : "LineBrush")!,
        };
        ToolTip.SetTip(button, enabled ? $"{layout.Cells.Count} cells" : "Too few cells for the layers in this project");
        button.Click += (_, _) =>
        {
            SelectedLayoutId = layout.Id;
            Close(true);
        };
        return button;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private static Color ToAvalonia(SkiaSharp.SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);
}
