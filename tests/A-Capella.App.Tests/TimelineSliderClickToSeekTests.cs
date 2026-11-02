using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Clicking anywhere on the preview timeline track should seek there, not just dragging
/// the thumb -- otherwise a click on the track leaves Value alone, and the seek issued on release
/// goes to wherever the thumb already was instead of where the user clicked.</summary>
public class TimelineSliderClickToSeekTests
{
    [AvaloniaFact]
    public void ClickingTheTrack_JumpsTheSliderToThatPoint()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var slider = window.TimelineSlider;
            slider.Maximum = 1000;
            slider.Value = 0;

            var origin = slider.TranslatePoint(new Point(0, 0), window)!.Value;
            var target = new Point(origin.X + slider.Bounds.Width * 0.75, origin.Y + slider.Bounds.Height / 2);
            window.MouseDown(target, MouseButton.Left);   // the release would seek, and an empty project snaps back to 0

            Assert.InRange(slider.Value, 650, 850);
        }
        finally
        {
            window.Close();
        }
    }
}
