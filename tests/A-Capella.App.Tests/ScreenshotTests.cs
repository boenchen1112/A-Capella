using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Renders the main window offscreen to a PNG so the look can be checked without a
/// display. Only does anything when ACAPELLA_SCREENSHOT_DIR is set (it is skipped otherwise).</summary>
public class ScreenshotTests
{
    [AvaloniaFact]
    public void MainWindow_RendersToPng()
    {
        string? dir = Environment.GetEnvironmentVariable("ACAPELLA_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "Set ACAPELLA_SCREENSHOT_DIR to write screenshots.");

        var window = new MainWindow { Width = 1440, Height = 900 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory(dir!);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir!, "main-window.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Capture(Avalonia.Controls.Window window, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("ACAPELLA_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "Set ACAPELLA_SCREENSHOT_DIR to write screenshots.");
        try
        {
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory(dir!);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir!, name));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Dialogs_RenderToPng()
    {
        Capture(new VideoSettingsWindow(new A_Capella.Engine.Project.VideoSettings()), "video-settings.png");
        Capture(new LayoutPickerWindow("", 1), "layout-picker.png");
        Capture(new SavePackageWindow(), "save-package.png");
    }
}
