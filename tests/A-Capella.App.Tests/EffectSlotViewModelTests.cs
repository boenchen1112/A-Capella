using A_Capella.App;
using A_Capella.App.ViewModels;
using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Project;
using Xunit;

namespace A_Capella.App.Tests;

[CollectionDefinition("SharedHostedService", DisableParallelization = true)]
public sealed class SharedHostedServiceCollection { }

/// <summary>A layer's effects as the channel panel shows them: one preset choice per effect,
/// written straight through to the layer's mix parameters.</summary>
[Collection("SharedHostedService")]
public class EffectSlotViewModelTests
{
    private sealed class NothingInstalled : IHostedPluginAvailability
    {
        public bool IsAvailable(string pluginLabel) => false;
    }

    private static LayerRowViewModel RowWithLayer() =>
        new(1) { Layer = new LayerCollection().Add(LayerKind.UploadedAudioOnly, "does-not-exist.wav") };

    private static PresetChoice Choice(string plugin, string name) => new(new PresetRef(plugin, "Basic", name), "Basic", name);

    [StaFact]
    public void EveryLayerHasTheFourEffects_InChainOrder_AllStartingOff()
    {
        var row = RowWithLayer();

        Assert.Equal(new[] { "Compressor", "EQ", "De-esser", "Reverb" }, row.Effects.Select(e => e.Title));
        Assert.All(row.Effects, e => Assert.Equal("Off", e.SelectedChoice?.Label));
    }

    [StaFact]
    public void PresetMenu_MirrorsFabFilterFolders_CategoriesThenNestedCategoriesThenPresets()
    {
        PresetChoice C(string category, string name) => new(new PresetRef("FabFilter Pro-C 3", category, name), category, name);
        var choices = new List<PresetChoice>
        {
            new(null, "", "Off"),
            C("Basic", "Vocal"), C("Basic", "Bass"),
            C("Tempo Synced/110bpm", "Pump"),
        };
        PresetChoice? picked = null;

        var menu = PresetPicker.BuildMenu(choices, choices[3], c => picked = c);

        Assert.Equal("Off", ((System.Windows.Controls.MenuItem)menu.Items[0]).Header);
        Assert.IsType<System.Windows.Controls.Separator>(menu.Items[1]);
        var basic = (System.Windows.Controls.MenuItem)menu.Items[2];
        Assert.Equal("Basic", basic.Header);
        Assert.Equal(new object[] { "Vocal", "Bass" }, basic.Items.Cast<System.Windows.Controls.MenuItem>().Select(i => i.Header).ToArray());
        var synced = (System.Windows.Controls.MenuItem)menu.Items[3];
        Assert.True(synced.IsChecked);                                   // the path to the chosen preset is ticked
        var folder = (System.Windows.Controls.MenuItem)synced.Items[0];
        var pump = (System.Windows.Controls.MenuItem)folder.Items[0];
        Assert.True(pump.IsChecked);
        pump.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert.Same(choices[3], picked);
    }

    [StaFact]
    public void ChoicesAlwaysStartWithOff()
    {
        var choices = EffectSlotViewModel.AllChoices(FxSlots.Compressor.PluginLabel);

        Assert.Null(choices[0].Ref);
        Assert.Equal("Off", choices[0].Label);
        Assert.All(choices.Skip(1), c => Assert.Equal(FxSlots.Compressor.PluginLabel, c.Ref!.Plugin));
    }

    [StaFact]
    public void PickingAPreset_WritesItToTheLayer_AndRaisesTheChangeEvents()
    {
        var row = RowWithLayer();
        int live = 0, chosen = 0;
        row.LiveParamChanged += () => live++;
        row.EffectChosen += () => chosen++;
        var choice = Choice("FabFilter Pro-C 3", "Vocal");

        row.Effects[0].SelectedChoice = choice;

        Assert.Equal(choice.Ref, row.Layer!.MixParameters.CompressorPreset);
        Assert.Equal((1, 1), (live, chosen));
        Assert.Contains("1 effect", row.SourceLabel);
    }

    [StaFact]
    public void PickingTheSamePresetAgain_ChangesNothing()
    {
        var row = RowWithLayer();
        row.Effects[1].SelectedChoice = Choice("FabFilter Pro-Q 4", "Bright");
        int chosen = 0;
        row.EffectChosen += () => chosen++;

        row.Effects[1].SelectedChoice = Choice("FabFilter Pro-Q 4", "Bright");

        Assert.Equal(0, chosen);
    }

    [StaFact]
    public void PickingOff_ClearsTheEffect()
    {
        var row = RowWithLayer();
        row.Effects[3].SelectedChoice = Choice("FabFilter Pro-R 2", "Studio A");

        row.Effects[3].SelectedChoice = new PresetChoice(null, "", "Off");

        Assert.Null(row.Layer!.MixParameters.ReverbPreset);
    }

    [StaFact]
    public void ApplyingAPackage_SetsAllFourEffects_AndTheRowReadsThemBack()
    {
        var row = RowWithLayer();
        row.Effects[2].SelectedChoice = Choice("FabFilter Pro-DS", "Old");
        var package = new FxPackage
        {
            Name = "Warm",
            CompressorPreset = new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal"),
            ReverbPreset = new PresetRef("FabFilter Pro-R 2", "_2 Small", "Studio A"),
        };
        int chosen = 0;
        row.EffectChosen += () => chosen++;

        row.ApplyPackage(package);

        Assert.Equal(package.CompressorPreset, row.Layer!.MixParameters.CompressorPreset);
        Assert.Null(row.Layer.MixParameters.DeEsserPreset);
        Assert.Equal(package.ReverbPreset, row.Layer.MixParameters.ReverbPreset);
        Assert.Equal(1, chosen);
        Assert.Equal("Off", row.Effects[2].SelectedChoice?.Label);
    }

    [StaFact]
    public void ToPackage_CapturesTheLayersEffects()
    {
        var row = RowWithLayer();
        row.Effects[0].SelectedChoice = Choice("FabFilter Pro-C 3", "Vocal");

        var package = row.ToPackage("Mine");

        Assert.Equal("Mine", package!.Name);
        Assert.Equal(row.Layer!.MixParameters.CompressorPreset, package.CompressorPreset);
    }

    [StaFact]
    public void MissingPlugin_ShowsNotInstalled_AndAMissingPreset_ShowsPresetNotFound()
    {
        var saved = LayerRowViewModel.SharedHostedService;
        using var service = new HostedPluginService(new NothingInstalled());
        LayerRowViewModel.SharedHostedService = service;
        try
        {
            var row = RowWithLayer();
            Assert.Equal("FabFilter Pro-C 3 not installed", row.Effects[0].Status);
        }
        finally { LayerRowViewModel.SharedHostedService = saved; }

        var row2 = RowWithLayer();
        row2.Layer!.MixParameters.EqPreset = new PresetRef("FabFilter Pro-Q 4", "Nope", "Does not exist");
        Assert.Equal("Preset not found: Does not exist", row2.Effects[1].Status);
    }

    [StaFact]
    public void AnEmptyRow_HasNothingToSelectOrReport()
    {
        var row = new LayerRowViewModel(1);

        Assert.Null(row.Effects[0].SelectedChoice?.Ref);
        Assert.Equal("", row.Effects[0].Status);
        row.ApplyPackage(new FxPackage { Name = "x" });   // no layer: nothing to apply to, and no throw
    }
}
