using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #15: closing the main window while an export is in flight
/// (ExportMenuItem.IsEnabled == false) must be refused like Play/Record/Open/New are (bug audit #8)
/// -- otherwise the Closing teardown disposes the hosted service under the export and the app exits,
/// killing the export thread and leaving a truncated MP4. See
/// reviews/Bug_Audit_2026-09-25_WindowCloseAbandonsExport.md.</summary>
public class ExportCloseGateTests
{
    [StaFact]
    public void Close_WhileExportInFlight_IsCancelledWithStatus()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            window.ExportMenuItem.IsEnabled = false;   // what ExportButton_Click does while Task.Run is running

            window.Close();

            Assert.True(window.IsVisible, "Closing must be refused while an export is in flight.");
            Assert.Equal("Finish the export first.", window.StatusText.Text);
        }
        finally
        {
            window.ExportMenuItem.IsEnabled = true;    // "export finished" -- lets cleanup actually close it
            if (window.IsVisible) window.Close();      // guarded: before the fix, the Close() above already closed it
        }
    }

    [StaFact]
    public void Close_WithNoExportInFlight_StillCloses()
    {
        // Regression guard: the gate must only refuse while the flag is down.
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        window.Show();

        window.Close();

        Assert.False(window.IsVisible);
    }
}
