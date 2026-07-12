using System.Windows;

namespace Acapella.App;

/// <summary>
/// TODO(polish): stub pop-up. Real Melodyne ARA hosting is Phase 2A (not started -- see
/// CLAUDE.md/primer.md); this just satisfies the UI spec's "own pop-up window" placement so the
/// track row doesn't need to change shape again once Phase 2A actually lands.
/// </summary>
public partial class MelodyneEditorWindow : Window
{
    public MelodyneEditorWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
