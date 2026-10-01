using System.Linq;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Acapella.Engine.Tests.Host;
using Acapella.Engine.Timeline;

namespace Acapella.Engine.Tests.Timeline;

public class LayerTimelineTests
{
    private static LayerModel Layer(double shiftMs = 0, double trimStartMs = 0, double? trimEndMs = null, int cellIndex = 0) => new()
    {
        LayerId = cellIndex,
        Kind = LayerKind.RecordedAV,
        SourcePath = $"layer{cellIndex}.mkv",
        ManualOffsetMs = shiftMs,
        TrimStartMs = trimStartMs,
        TrimEndMs = trimEndMs,
        CellIndex = cellIndex,
    };

    private static LayerTimeline TimelineWithProbe(MixEngine mixEngine, double durationSeconds) =>
        new(mixEngine, "ffmpeg", _ => durationSeconds);

    [Theory]
    [InlineData(-200, 0, 200, 0)]      // negative shift: skip |shift| into the source, no hold (A3)
    [InlineData(-200, 1000, 1200, 0)]  // ...and keep advancing with position
    [InlineData(300, 0, 0, 300)]       // positive shift: hold the full shift at the start
    [InlineData(300, 100, 0, 200)]     // ...partway through the hold
    [InlineData(300, 1000, 700, 0)]    // ...past the hold, source advances by position - shift
    public void VideoWindowAt_PlacesSourceInSyncWithShiftedAudio(double shiftMs, double positionMs, double expectedSourceStartMs, double expectedHoldMs)
    {
        var window = LayerTimeline.VideoWindowAt(Layer(shiftMs, trimStartMs: 0), positionMs);

        Assert.Equal(expectedSourceStartMs, window.SourceStartMs, 6);
        Assert.Equal(expectedHoldMs, window.HoldMs, 6);
    }

    [Fact]
    public void VideoWindowAt_AddsTrimStartOnTopOfShift()
    {
        var window = LayerTimeline.VideoWindowAt(Layer(shiftMs: -200, trimStartMs: 500), positionMs: 0);

        Assert.Equal(700, window.SourceStartMs, 6);
        Assert.Equal(0, window.HoldMs, 6);
    }

    [Theory]
    [InlineData(0, 0, null, 10_000)]        // untrimmed, unshifted
    [InlineData(0, 1000, 4000.0, 3000)]     // trimmed window
    [InlineData(0, 0, 60_000.0, 10_000)]    // trim-out past the source end clamps to the source
    [InlineData(500, 0, null, 10_500)]      // positive shift extends
    [InlineData(-500, 0, null, 9_500)]      // negative shift shortens
    [InlineData(-20_000, 0, null, 0)]       // never negative
    public void DurationMs_AppliesTrimAndShiftToProbedDuration(double shiftMs, double trimStartMs, double? trimEndMs, double expectedMs)
    {
        using var mixEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var timeline = TimelineWithProbe(mixEngine, durationSeconds: 10);

        Assert.Equal(expectedMs, timeline.DurationMs(Layer(shiftMs, trimStartMs, trimEndMs), 44100), 6);
    }

    [Fact]
    public void DurationMs_OfProject_IsLongestLayer_AndZeroWhenEmpty()
    {
        using var mixEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var timeline = TimelineWithProbe(mixEngine, durationSeconds: 2);

        Assert.Equal(0, timeline.DurationMs(Array.Empty<LayerModel>(), 44100));
        Assert.Equal(2500, timeline.DurationMs(new[] { Layer(0, cellIndex: 0), Layer(500, cellIndex: 1) }, 44100), 6);
    }

    /// <summary>Q1 task 5 (bug audit B11): a Pro-R tail-length change made in its own editor must be
    /// picked up by the next DurationMs call (what every preview-rebuild path uses, directly or via
    /// PreviewPlaybackEngine.RefreshAsync's SetLayersCore) -- not frozen at whatever the tail was
    /// when the chain/instance was first created. FxSlot.TailSeconds queries the live instance's
    /// TailSeconds property fresh on every call rather than caching it, so this passes already; the
    /// test pins that contract down rather than leaving it implicit.</summary>
    [Fact]
    public void DurationMs_ReQueriesTheLiveReverbTail_NotTheValueFromWhenTheChainWasBuilt()
    {
        var factory = new FakeHostedPluginFactory().With("FabFilter Pro-R 2", tailSeconds: 1.0);
        using var mixEngine = new MixEngine(factory.CreateService());
        var timeline = TimelineWithProbe(mixEngine, durationSeconds: 10);
        var layer = Layer(cellIndex: 0);
        layer.MixParameters.ReverbEnabled = true;

        Assert.Equal(11_000, timeline.DurationMs(layer, 44100), 6);   // 10s source + 1s tail

        // Simulate the user dragging Pro-R 2's decay knob in its own still-open editor window --
        // the live instance's TailSeconds changes immediately, with no SetState/poll involved.
        factory.Created.Single().TailSeconds = 3.0;

        Assert.Equal(13_000, timeline.DurationMs(layer, 44100), 6);   // picked up without rebuilding the chain
    }

    [Fact]
    public void InCellOrder_SortsByCellIndex_NotInsertionOrder()
    {
        var ordered = LayerTimeline.InCellOrder(new[] { Layer(cellIndex: 2), Layer(cellIndex: 0), Layer(cellIndex: 1) });

        Assert.Equal(new[] { 0, 1, 2 }, ordered.Select(l => l.CellIndex));
    }
}
