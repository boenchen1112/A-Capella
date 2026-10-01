using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #13: CommitSlider's style must commit on a keyboard-driven edit, not only a
/// mouse-up -- Slider.Value binds TwoWay with the framework-default PropertyChanged trigger, so a
/// keyboard edit already applies live with nothing else to catch it. Inspects the actual style
/// resource rather than simulating a routed keyboard event, so it isn't subject to the focus/route
/// uncertainties a full keyboard-simulation test would carry (no established precedent for raising
/// KeyDown/KeyUp in this suite -- see the doc's "why the suite misses it").</summary>
public class CommitSliderKeyboardTests
{
    [StaFact]
    public void CommitSlider_Style_HasAKeyboardCommitHandler()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();

            var style = (Style)window.FindResource("CommitSlider");
            bool hasKeyboardCommit = style.Setters.OfType<EventSetter>()
                .Any(es => es.Event.Name == "PreviewKeyUp");

            Assert.True(hasKeyboardCommit,
                "CommitSlider only wires PreviewMouseUp -- a keyboard-only slider edit (Up/Down/PageUp/PageDown/End) applies live with no undo step and no dirty mark. Expected a PreviewKeyUp EventSetter specifically (see the doc's rejected alternatives for why not KeyUp/LostKeyboardFocus).");
        }
        finally
        {
            window.Close();
        }
    }
}
