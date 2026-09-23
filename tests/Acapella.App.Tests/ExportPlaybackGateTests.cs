using System.Windows;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #8: while an export is in flight (ExportMenuItem.IsEnabled == false),
/// Play/Space must refuse with "Finish the export first." instead of reaching the empty-project
/// check. Headless side of repro B -- see reviews/Bug_Audit_2026-09-23_ExportPreviewSharedPluginInstances.md.</summary>
public class ExportPlaybackGateTests
{
    [StaFact]
    public void PlayButton_WhileExportInFlight_RefusesWithStatus()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            window.ExportMenuItem.IsEnabled = false;   // what ExportButton_Click does while Task.Run is running
            window.PlayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("Finish the export first.", window.StatusText.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void PlayButton_WithNoExportInFlight_StillReportsNoLayers()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            window.PlayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("Add at least one layer first.", window.StatusText.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
