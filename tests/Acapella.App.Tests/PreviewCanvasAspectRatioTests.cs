using Xunit;

namespace Acapella.App.Tests;

/// <summary>"Also noticed" in Improvement_Proposal_2026-09-24: the preview canvas must share
/// export's 16:9 aspect ratio, so a 16:9 source letterboxes identically in both -- previously the
/// preview was 640x480 (4:3) while export defaulted to 1280x720 (16:9), so what you saw while
/// editing didn't match what you got in the MP4.</summary>
public class PreviewCanvasAspectRatioTests
{
    [StaFact]
    public void CompositeCanvas_MatchesExportDefaultAspectRatio()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();

            const double exportAspect = 1280.0 / 720.0;
            double previewAspect = window.CompositeCanvas.Width / window.CompositeCanvas.Height;

            Assert.Equal(exportAspect, previewAspect, 6);
        }
        finally
        {
            window.Close();
        }
    }
}
