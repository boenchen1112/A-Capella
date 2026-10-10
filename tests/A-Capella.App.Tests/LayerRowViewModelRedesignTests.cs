using A_Capella.App.ViewModels;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Project;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>LayerRowViewModel's rename-only-if-changed and the lane sub-label/trim helper properties.</summary>
public class LayerRowViewModelRedesignTests
{
    private static LayerRowViewModel RowWithLayer(LayerKind kind = LayerKind.RecordedAV) =>
        new(1) { Layer = new LayerCollection().Add(kind, "does-not-exist.mkv") };   // nonexistent path: SourceDurationMs stays 0 deterministically, no ffprobe dependency

    [StaFact]
    public void CommitNameIfChanged_ReturnsFalse_WhenTheTrimmedValueIsUnchanged()
    {
        var row = RowWithLayer();
        Assert.True(row.CommitNameIfChanged("Melody"));   // "" -> "Melody" is a real change
        Assert.False(row.CommitNameIfChanged("Melody"));  // same value: no-op
        Assert.False(row.CommitNameIfChanged("  Melody  "));  // same after trimming: still no-op
    }

    [StaFact]
    public void CommitNameIfChanged_ReturnsTrue_AndAppliesTheTrimmedValue_WhenChanged()
    {
        var row = RowWithLayer();
        bool changed = row.CommitNameIfChanged("  Harmony  ");
        Assert.True(changed);
        Assert.Equal("Harmony", row.Name);
    }

    [StaFact]
    public void EffectiveTrimEndMs_FallsBackToSourceDuration_WhenNoExplicitTrimOutIsSet()
    {
        var row = RowWithLayer();
        Assert.Null(row.TrimEndMs);
        Assert.Equal(row.SourceDurationMs, row.EffectiveTrimEndMs);

        row.TrimEndMs = 5000;
        Assert.Equal(5000, row.EffectiveTrimEndMs);
    }

    [StaFact]
    public void SourceLabel_ReportsKindAndEnabledEffectCount_WithoutADuration()
    {
        var row = RowWithLayer(LayerKind.UploadedVideo);
        Assert.Equal("Uploaded", row.SourceLabel);   // no duration yet (nonexistent file), no effects on

        row.Effects.Single(e => e.Slot == FxSlots.Eq).SelectedChoice = new PresetChoice(new PresetRef("FabFilter Pro-Q 4", "", "Bright"), "General", "Bright");
        row.Effects.Single(e => e.Slot == FxSlots.Compressor).SelectedChoice = new PresetChoice(new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal"), "Basic", "Vocal");
        Assert.Equal("Uploaded · 2 effects", row.SourceLabel);
    }

    [StaFact]
    public void SourceLabel_ForAnEmptyRow_IsEmpty()
    {
        var row = new LayerRowViewModel(1);
        Assert.Equal("Empty", row.SourceLabel);
    }
}
