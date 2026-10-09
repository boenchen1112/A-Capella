using System.Windows;
using System.Windows.Controls;
using Acapella.Engine.Persistence;

namespace Acapella.App;

/// <summary>Checklist of the layers in another project or collab package; SelectedLayerIds are the
/// source file's own layer ids, in the order shown.</summary>
public partial class ImportLayersWindow : Window
{
    private readonly List<(CheckBox Box, int LayerId)> _rows = new();

    public IReadOnlyList<int> SelectedLayerIds { get; private set; } = Array.Empty<int>();

    public ImportLayersWindow(ProjectFileDto source, int freeSlots)
    {
        InitializeComponent();
        HintText.Text = freeSlots > 0
            ? $"Pick the layers to add. Each goes into the next empty cell ({freeSlots} free)."
            : "There are no empty cells left in this project.";

        foreach (var layer in source.Layers.OrderBy(l => l.CellIndex).ThenBy(l => l.LayerId))
        {
            string name = string.IsNullOrWhiteSpace(layer.Name) ? $"Layer {layer.CellIndex + 1}" : layer.Name;
            string kind = layer.Kind switch { "UploadedAudioOnly" => "audio only", "UploadedVideo" => "video", _ => "recorded" };
            var box = new CheckBox { Content = $"{name}  ({kind})", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
            box.Checked += (_, _) => UpdateOkState();
            box.Unchecked += (_, _) => UpdateOkState();
            LayerList.Children.Add(box);
            _rows.Add((box, layer.LayerId));
        }
        OkButton.IsEnabled = freeSlots > 0 && _rows.Count > 0;
        _freeSlots = freeSlots;
        UpdateOkState();
    }

    private readonly int _freeSlots;

    private void UpdateOkState()
    {
        if (OkButton is null) return;
        OkButton.IsEnabled = _freeSlots > 0 && _rows.Any(r => r.Box.IsChecked == true);
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        bool all = _rows.All(r => r.Box.IsChecked == true);
        foreach (var (box, _) in _rows) box.IsChecked = !all;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedLayerIds = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.LayerId).ToList();
        DialogResult = true;
    }
}
