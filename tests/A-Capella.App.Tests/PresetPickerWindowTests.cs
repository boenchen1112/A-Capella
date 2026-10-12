using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using A_Capella.App.ViewModels;
using A_Capella.Engine.Project;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>The effect dropdown inside the real window: picking a preset from the opened menu must
/// reach the layer.</summary>
[Collection("SharedHostedService")]
public class PresetPickerWindowTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static MenuItem FirstPreset(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Items.Count == 0 && item.Header is string h && h != "Off") return item;
            else if (item.Items.Count > 0) return FirstPreset(item);
        throw new InvalidOperationException("no presets");
    }

    [StaFact]
    public void PickingFromTheOpenedMenu_SetsThePresetOnTheLayer()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            var row = new LayerRowViewModel(1) { Layer = new LayerCollection().Add(LayerKind.UploadedAudioOnly, "x.wav") };
            int chosen = 0;
            row.EffectChosen += () => chosen++;
            window.ChannelPanel.DataContext = row;
            window.Show();
            window.UpdateLayout();

            var picker = Descendants<PresetPicker>(window.FxPanel).First();
            Assert.True(picker.Choices!.Count > 1, "no presets were listed");

            var menu = picker.CreateMenu();
            var preset = FirstPreset(menu);
            preset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.NotNull(row.Layer!.MixParameters.CompressorPreset);
            Assert.Equal(1, chosen);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void CategoryItems_CanOpenASubmenuOfPresets_InTheThemedMenu()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            var picker = new PresetPicker { Choices = EffectSlotViewModel.AllChoices("FabFilter Pro-C 3") };
            var menu = picker.CreateMenu();
            var category = menu.Items.OfType<MenuItem>().First(m => m.Items.Count > 0);
            window.Resources.Add("pp", menu);
            menu.IsOpen = true;
            category.ApplyTemplate();
            Assert.NotNull(category.Template.FindName("Popup", category));
        }
        finally { window.Close(); }
    }
}
