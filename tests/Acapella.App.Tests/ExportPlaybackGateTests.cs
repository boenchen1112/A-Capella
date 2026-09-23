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

    /// <summary>Retake spec: the #8 gate moved into ShowRecordSetupDialog, the single place
    /// RecordSetupWindow is constructed. This pins that Recording setup (toolbar ⏺, which Re-record
    /// shares the same gated helper with) still refuses during an export, without ever constructing
    /// the modal dialog (which would hang this STA test thread).</summary>
    [StaFact]
    public void RecordSetup_WhileExportInFlight_RefusesWithStatus()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            window.ExportMenuItem.IsEnabled = false;   // what ExportButton_Click does while Task.Run is running
            window.RecordButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("Finish the export first.", window.StatusText.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
