using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Regression test for v5 P1 task 3 (transport redesign, audit B15): the transport row
/// and timeline must stay within the window's visible bounds even at MinWidth/MinHeight, instead
/// of being pushed off-screen by the sidebar/preview taking all the space.</summary>
public class TransportLayoutTests
{
    private static void EnsureApplicationResourcesLoaded()
    {
        if (Application.Current is not null) return;

        // Headless: no App.xaml startup, so MainWindow's StaticResource lookups (RowBrush,
        // PanelBrush, ...) would otherwise fail. Load the same merged dictionary App.xaml uses.
        _ = new Application();
        var dictionary = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Acapella.App;component/Theme/DarkTheme.xaml")
        };
        Application.Current.Resources.MergedDictionaries.Add(dictionary);
    }

    [StaFact]
    public void TransportRowAndTimeline_StayWithinWindowBounds_AtMinimumSize()
    {
        EnsureApplicationResourcesLoaded();

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
        };
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;

        try
        {
            window.Show();
            window.UpdateLayout();

            var windowBounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);

            AssertWithinBounds(window, window.PlayStopButton, windowBounds, "PlayStopButton");
            // The timeline Slider itself can be wider than its viewport when zoomed in (that's
            // what makes it scrollable) -- what must never be pushed off-screen is its scrollable
            // *container*, not the (possibly oversized) content inside it.
            AssertWithinBounds(window, window.TimelineScrollViewer, windowBounds, "TimelineScrollViewer");
            AssertWithinBounds(window, window.TimeReadoutText, windowBounds, "TimeReadoutText");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertWithinBounds(Window window, FrameworkElement element, Rect windowBounds, string name)
    {
        Point topLeft = element.TransformToAncestor(window).Transform(new Point(0, 0));
        var elementBounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));

        Assert.True(elementBounds.Right <= windowBounds.Right + 1,
            $"{name} right edge ({elementBounds.Right}) exceeds window width ({windowBounds.Right}).");
        Assert.True(elementBounds.Bottom <= windowBounds.Bottom + 1,
            $"{name} bottom edge ({elementBounds.Bottom}) exceeds window height ({windowBounds.Bottom}).");
        Assert.True(elementBounds.Top >= -1, $"{name} top edge ({elementBounds.Top}) is above the window.");
        Assert.True(elementBounds.Left >= -1, $"{name} left edge ({elementBounds.Left}) is left of the window.");
    }
}
