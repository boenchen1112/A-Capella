using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Bug audit #14: MainWindow_PreviewKeyDown must not steal Space from a focused
/// Button/CheckBox/ToggleButton -- Space is Button's own activation key, and this handler runs
/// during the tunnel phase, before the focused control's own handling. Invokes the private
/// handler directly with a synthetic KeyEventArgs and a real focused control, rather than
/// attempting a full OS-level keyboard input simulation.</summary>
public class TransportShortcutFocusTests
{
    [AvaloniaFact]
    public void MainWindow_PreviewKeyDown_Space_WithToggleButtonFocused_IsLeftAlone()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.MonitorToggle.Focus();
            Assert.Same(window.MonitorToggle, window.FocusManager!.GetFocusedElement());

            var handled = RaisePreviewKeyDown(window, Key.Space);

            Assert.False(handled, "Space on a focused toggle button must be left to the button, not used as Play/Stop.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_PreviewKeyDown_Space_WithTextBoxFocused_StillWorksAsTransport()
    {
        // Regression guard: the pre-existing TextBox exclusion must keep working --
        // this fix only adds a second, narrower exclusion, it must not replace the first.
        var window = new MainWindow();
        try
        {
            window.Show();
            window.MetronomeBpmTextBox.Focus();

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
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = window };
        method.Invoke(window, new object[] { window, args });
        return args.Handled;
    }
}
