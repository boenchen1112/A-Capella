using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Reflection;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #14: MainWindow_PreviewKeyDown must not steal Space from a focused
/// Button/CheckBox/ToggleButton -- Space is ButtonBase's own activation key, and this handler
/// runs during the tunnel phase, before the focused control's own bubble-phase KeyDown handling
/// (which is what "e.Handled" suppresses -- see the doc's root cause 2). Invokes the private
/// handler directly with a synthetic KeyEventArgs and a real focused control, rather than
/// attempting a full OS-level keyboard input simulation (no precedent for that in this suite --
/// see Bug Audit #13's own CommitSliderKeyboardTests, which took the same "test the mechanism,
/// not the full routed-event pipeline" approach for the identical reason).</summary>
public class TransportShortcutFocusTests
{
    [StaFact]
    public void MainWindow_PreviewKeyDown_Space_WithToggleButtonFocused_IsSuppressed()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            Keyboard.Focus(window.MonitorToggle);
            bool checkedBefore = window.MonitorToggle.IsChecked == true;

            RaisePreviewKeyDown(window, Key.Space);

            Assert.Equal(checkedBefore, window.MonitorToggle.IsChecked == true); // handler didn't consume it wrongly and MonitorToggle wasn't left to WPF's own routing to flip it via this call
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void MainWindow_PreviewKeyDown_Space_WithTextBoxFocused_StillWorksAsTransport()
    {
        // Regression guard: the pre-existing TextBox exclusion (:290) must keep working --
        // this fix only adds a second, narrower exclusion, it must not replace the first.
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            Keyboard.Focus(window.MetronomeBpmTextBox);

            var handled = RaisePreviewKeyDown(window, Key.Space);

            Assert.False(handled, "TextBox-focused Space must still be left alone by the transport shortcut.");
        }
        finally
        {
            window.Close();
        }
    }

    private static bool RaisePreviewKeyDown(MainWindow window, Key key)
    {
        var method = typeof(MainWindow).GetMethod("MainWindow_PreviewKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var source = PresentationSource.FromVisual(window)!;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        method.Invoke(window, new object[] { window, args });
        return args.Handled;
    }
}
