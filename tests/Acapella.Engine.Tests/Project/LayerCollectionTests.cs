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
}
