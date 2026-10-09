using Acapella.App.ViewModels;
using Acapella.Engine.Project;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>The strip's -/+ timing nudge: moves the layer's manual offset, refreshes the live preview,
/// updates the label, and a retake (ReplaceSource) resets it.</summary>
public class LayerRowNudgeTests
{
    private static LayerRowViewModel RowWithLayer() =>
        new(1) { Layer = new LayerCollection().Add(LayerKind.UploadedVideo, "a.mp4") };

    [StaFact]
    public void Nudge_MovesTheOffset_RefreshesThePreview_AndUpdatesTheLabel()
    {
        var row = RowWithLayer();
        int refreshes = 0;
        row.LiveParamChanged += () => refreshes++;
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        row.Nudge(10);
        row.Nudge(10);
        row.Nudge(-30);

        Assert.Equal(-10, row.Layer!.ManualOffsetMs);
        Assert.Equal("-10 ms", row.ManualOffsetDisplay);
        Assert.Equal(3, refreshes);
        Assert.Contains(nameof(LayerRowViewModel.ManualOffsetDisplay), raised);
    }

    [StaFact]
    public void ResetNudge_ClearsTheOffset_AndIsANoOpAtZero()
    {
        var row = RowWithLayer();
        int refreshes = 0;
        row.LiveParamChanged += () => refreshes++;

        row.ResetNudge();
        Assert.Equal(0, refreshes);

        row.Nudge(40);
        row.ResetNudge();
        Assert.Equal(0, row.ManualOffsetMs);
        Assert.Equal("0 ms", row.ManualOffsetDisplay);
        Assert.Equal(2, refreshes);
    }

    [StaFact]
    public void AnEmptyRow_IgnoresNudges()
    {
        var row = new LayerRowViewModel(1);
        row.Nudge(10);
        Assert.Equal(0, row.ManualOffsetMs);
    }

    [StaFact]
    public void Retake_ResetsTheNudge_AndNotifiesTheLabel()
    {
        var row = RowWithLayer();
        row.Nudge(50);
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        row.Layer!.ReplaceSource(LayerKind.UploadedVideo, "b.mp4", 0);
        row.NotifySourceReplaced();

        Assert.Equal("0 ms", row.ManualOffsetDisplay);
        Assert.Contains(nameof(LayerRowViewModel.ManualOffsetDisplay), raised);
    }
}
