using Acapella.Engine.Project;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>v8 redesign, fixed 4-lane model: the lanes panel never grows/shrinks -- it's always
/// exactly LayerCollection.MaxLayers rows, even for a brand-new project with no layers yet. (Delete
/// Layer's own logic -- remove from LayerCollection, null the row's Layer, no cell compaction -- is
/// exercised at the data layer by LayerCollectionTests.Remove_DropsOnlyTheGivenLayer...; a UI-level
/// test here would need to drive MainWindow's real async preview-refresh path, which this headless
/// test host has no pumped Dispatcher/SynchronizationContext for -- every other MainWindow test in
/// this suite avoids that for the same reason, see ExportPlaybackGateTests' early-return gates.)</summary>
public class MainWindowDeleteLayerTests
{
    [StaFact]
    public void TrackList_AlwaysHasExactlyFourRows_ForABrandNewProject()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            Assert.Equal(LayerCollection.MaxLayers, window.TrackList.Items.Count);
        }
        finally
        {
            window.Close();
        }
    }
}
