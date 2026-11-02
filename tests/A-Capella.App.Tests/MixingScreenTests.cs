using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Regression test for v5 P2 task 2 (v8: mixer strip replaces the old sidebar): the
/// channel panel's per-layer Volume slider (bound to LayerRowViewModel.GainDb) is the only control
/// for that property -- the FX panel itself holds only preset pickers and must not expose a second
/// Slider (in particular none bound to GainDb) outside the Limiter panel.</summary>
public class MixingScreenTests
{
    [AvaloniaFact]
    public void FxPanel_HasNoSlider()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Empty(window.FxPanel.GetVisualDescendants().OfType<Slider>());
        }
        finally
        {
            window.Close();
        }
    }
}
