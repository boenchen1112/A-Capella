using Xunit;

namespace Acapella.App.Tests;

/// <summary>Clicking anywhere on the preview timeline track should seek there, not just dragging
/// the thumb -- without Slider.IsMoveToPointEnabled, a click on the track doesn't change Value, so
/// TimelineSlider_PreviewMouseUp's seek-to-current-Value ends up seeking to wherever the thumb
/// already was instead of where the user clicked.</summary>
public class TimelineSliderClickToSeekTests
{
    [StaFact]
    public void TimelineSlider_HasMoveToPointEnabled()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            Assert.True(window.TimelineSlider.IsMoveToPointEnabled,
                "A click on the timeline's track should jump the thumb (and Value) straight to that point.");
        }
        finally
        {
            window.Close();
        }
    }
}
