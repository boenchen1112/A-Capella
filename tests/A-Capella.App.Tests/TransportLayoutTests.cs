using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Regression test for v5 P1 task 3 (transport redesign, audit B15): the transport row
/// and timeline must stay within the window's visible bounds even at MinWidth/MinHeight, instead
/// of being pushed off-screen by the sidebar/preview taking all the space.</summary>
public class TransportLayoutTests
{
    [AvaloniaFact]
    public void TransportRowAndTimeline_StayWithinWindowBounds_AtMinimumSize()
    {
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(0, 0),
        };
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;

        try
        {
            window.Show();
            window.UpdateLayout();

            var windowBounds = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);

            AssertWithinBounds(window, window.PlayButton, windowBounds, "PlayButton");
            // v8 redesign: the timeline Slider now always spans its row's full width directly (no
            // scrollable wrapper -- see UpdateTimelineRangeUi's doc comment), so it's checked as-is.
            AssertWithinBounds(window, window.TimelineSlider, windowBounds, "TimelineSlider");
            AssertWithinBounds(window, window.CurrentTimeText, windowBounds, "CurrentTimeText");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertWithinBounds(Window window, Control element, Rect windowBounds, string name)
    {
        Point topLeft = element.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException($"{name} is not in the window.");
        var elementBounds = new Rect(topLeft, element.Bounds.Size);

        Assert.True(elementBounds.Right <= windowBounds.Right + 1,
            $"{name} right edge ({elementBounds.Right}) exceeds window width ({windowBounds.Right}).");
        Assert.True(elementBounds.Bottom <= windowBounds.Bottom + 1,
            $"{name} bottom edge ({elementBounds.Bottom}) exceeds window height ({windowBounds.Bottom}).");
        Assert.True(elementBounds.Top >= -1, $"{name} top edge ({elementBounds.Top}) is above the window.");
        Assert.True(elementBounds.Left >= -1, $"{name} left edge ({elementBounds.Left}) is left of the window.");
    }
}
