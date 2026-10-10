using A_Capella.App.ViewModels;
using A_Capella.Engine.Project;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>LayerRowViewModel.NotifySourceReplaced raises exactly the source- and trim-derived
/// properties, and never Layer/HasSource.</summary>
public class LayerRowRetakeTests
{
    [StaFact]
    public void NotifySourceReplaced_RaisesTheSourceAndTrimProperties_ButNotLayer()
    {
        var row = new LayerRowViewModel(1) { Layer = new LayerCollection().Add(LayerKind.UploadedVideo, "a.mp4") };

        var raised = new List<string>();
        row.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        row.Layer!.ReplaceSource(LayerKind.UploadedAudioOnly, "b.wav", 0);
        row.NotifySourceReplaced();

        Assert.Contains(nameof(LayerRowViewModel.TrimStartMs), raised);
        Assert.Contains(nameof(LayerRowViewModel.TrimEndText), raised);
        Assert.Contains(nameof(LayerRowViewModel.IconGlyph), raised);
        Assert.Contains(nameof(LayerRowViewModel.SourceStateLabel), raised);
        Assert.DoesNotContain(nameof(LayerRowViewModel.Layer), raised);
        Assert.DoesNotContain(nameof(LayerRowViewModel.HasSource), raised);
        Assert.Equal("uploaded (audio)", row.SourceStateLabel);
    }
}
