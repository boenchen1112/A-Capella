using System.Windows;

namespace Acapella.App;

/// <summary>
/// TODO(polish): stub pop-up. Phase 2A's actual ARA audio processing (MixEngine's Manual2A pitch
/// stage -- MelodyneAraPitchCorrector) is done and tested against real Melodyne, but it creates a
/// fresh AraHostSession per correction call rather than one persistent per-layer session, so
/// there's no live session yet for this window to show Melodyne's own native editor GUI against.
/// Wiring a real "Open Melodyne..." launcher (mirroring HostBridge.cpp's aca_show_editor_window
/// pattern for the plain-VST3 stages) needs that persistent-session lifecycle first -- follow-up
/// work, not required for Manual2A's audio to actually sound like Melodyne's output.
/// </summary>
public partial class MelodyneEditorWindow : Window
{
    public MelodyneEditorWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
