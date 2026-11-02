using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Bug audit #13: a commit slider must commit on a keyboard-driven edit, not only a
/// pointer release -- Slider.Value binds TwoWay and applies live on every change, so a keyboard
/// edit would otherwise leave no undo step and no dirty mark. Raises a key release on the real
/// Volume slider and checks the project went dirty (CommitEdit ran).</summary>
public class CommitSliderKeyboardTests
{
    private static bool IsDirty(MainWindow window) => window.ProjectTitleText.Text!.EndsWith("•");

    private static void ReleaseKey(Slider slider, Key key) =>
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = slider });

    [AvaloniaFact]
    public void CommitSlider_CommitsOnAKeyboardEdit()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var slider = window.ChannelPanel.GetVisualDescendants().OfType<Slider>().First(s => s.Classes.Contains("commit"));
            Assert.False(IsDirty(window));

            ReleaseKey(slider, Key.Up);

            Assert.True(IsDirty(window),
                "A keyboard-only slider edit (Up/Down/PageUp/PageDown/End) applies live; releasing the key must push an undo step and mark the project dirty.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CommitSlider_DoesNotCommitOnSeekKeys()
    {
        // Left/Right/Home are the transport's seek/restart keys; committing on them would wipe the
        // redo stack on every seek made while a slider happens to hold focus.
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var slider = window.ChannelPanel.GetVisualDescendants().OfType<Slider>().First(s => s.Classes.Contains("commit"));

            ReleaseKey(slider, Key.Left);

            Assert.False(IsDirty(window));
        }
        finally
        {
            window.Close();
        }
    }
}
