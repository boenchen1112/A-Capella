using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Project;

public class LayerCollectionTests
{
    [Fact]
    public void RemoveLast_RemovesMostRecentlyAddedLayer()
    {
        var layers = new LayerCollection();
        layers.Add(LayerKind.RecordedAV, "a.mkv");
        layers.Add(LayerKind.RecordedAV, "b.mkv");

        layers.RemoveLast();

        Assert.Single(layers.Layers);
        Assert.Equal("a.mkv", layers.Layers[0].SourcePath);
    }

    [Fact]
    public void RemoveLast_EmptyCollection_DoesNotThrow()
    {
        var layers = new LayerCollection();
        var exception = Record.Exception(() => layers.RemoveLast());
        Assert.Null(exception);
    }

    [Fact]
    public void RemoveLast_FreesSlotForAnotherLayer()
    {
        var layers = new LayerCollection();
        for (int i = 0; i < LayerCollection.MaxLayers; i++)
            layers.Add(LayerKind.RecordedAV, $"layer{i}.mkv");

        layers.RemoveLast();
        var exception = Record.Exception(() => layers.Add(LayerKind.RecordedAV, "retry.mkv"));

        Assert.Null(exception);
        Assert.Equal(LayerCollection.MaxLayers, layers.Layers.Count);
    }

    [Fact]
    public void Remove_DropsOnlyTheGivenLayer_KeepingOthersAndTheirCellIndex()
    {
        var layers = new LayerCollection();
        var a = layers.Add(LayerKind.RecordedAV, "a.mkv");
        var b = layers.Add(LayerKind.RecordedAV, "b.mkv");
        b.CellIndex = 2;   // non-contiguous, as a UI slot assignment would leave it

        layers.Remove(a);

        Assert.Single(layers.Layers);
        Assert.Same(b, layers.Layers[0]);
        Assert.Equal(2, b.CellIndex);   // not compacted to 0 (grid-gap-compaction fix)
    }

    [Fact]
    public void Add_AfterRemove_NeverReusesALiveLayerId()
    {
        // A reused LayerId would collide with a still-live hosted-plugin instance cache key for a
        // deleted layer whose instances are deliberately never released (see LayerCollection.Remove's
        // doc comment) -- the new layer would silently inherit the deleted layer's FX state.
        var layers = new LayerCollection();
        var a = layers.Add(LayerKind.RecordedAV, "a.mkv");
        layers.Add(LayerKind.RecordedAV, "b.mkv");
        layers.Remove(a);

        var c = layers.Add(LayerKind.RecordedAV, "c.mkv");

        Assert.DoesNotContain(layers.Layers, l => l != c && l.LayerId == c.LayerId);
        Assert.Equal(2, c.LayerId);
    }

    [Fact]
    public void Restore_AdvancesTheIdCounterPastRestoredLayers()
    {
        var layers = new LayerCollection();
        var restored = new LayerModel { LayerId = 5, Kind = LayerKind.RecordedAV, SourcePath = "x.mkv" };
        layers.Restore(new[] { restored });

        var next = layers.Add(LayerKind.RecordedAV, "y.mkv");

        Assert.Equal(6, next.LayerId);
    }
}
