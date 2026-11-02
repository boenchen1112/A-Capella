using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using A_Capella.App.ViewModels;
using A_Capella.Engine.Project;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>The effect dropdown inside the real window: picking a preset from the opened menu must
/// reach the layer.</summary>
[Collection("SharedHostedService")]
public class PresetPickerWindowTests
{
    private static MenuItem FirstPreset(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Items.Count == 0 && item.Header is string h && h != "Off") return item;
            else if (item.Items.Count > 0) return FirstPreset(item);
        throw new InvalidOperationException("no presets");
    }

    private static void SkipWithoutPresets() =>
        Assert.SkipWhen(EffectSlotViewModel.AllChoices("FabFilter Pro-C 3").Count <= 1, "FabFilter Pro-C 3 factory presets aren't installed.");

    [AvaloniaFact]
    public void PickingFromTheOpenedMenu_SetsThePresetOnTheLayer()
    {
        SkipWithoutPresets();
        var window = new MainWindow();
        try
        {
            var row = new LayerRowViewModel(1) { Layer = new LayerCollection().Add(LayerKind.UploadedAudioOnly, "x.wav") };
            int chosen = 0;
            row.EffectChosen += () => chosen++;
            window.ChannelPanel.DataContext = row;
            window.Show();
            window.UpdateLayout();

            var picker = window.FxPanel.GetVisualDescendants().OfType<PresetPicker>()
                .First(p => p.Choices!.Count > 1 && p.Choices[1].Ref?.Plugin == "FabFilter Pro-C 3");
            Assert.True(picker.Choices!.Count > 1, "no presets were listed");

            var menu = picker.CreateMenu();
            var preset = FirstPreset(menu);
            preset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.NotNull(row.Layer!.MixParameters.CompressorPreset);
            Assert.Equal(1, chosen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CategoryItems_HoldTheirPresetsAsSubmenus()
    {
        SkipWithoutPresets();
        var picker = new PresetPicker { Choices = EffectSlotViewModel.AllChoices("FabFilter Pro-C 3") };
        var menu = picker.CreateMenu();
        var category = menu.Items.OfType<MenuItem>().First(m => m.Items.Count > 0);
        Assert.NotNull(category.Header);
        Assert.NotEmpty(category.Items);
    }
}
