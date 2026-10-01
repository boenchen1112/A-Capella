using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Regression test for v5 P2 task 2 (v8: mixer strip replaces the old sidebar): the
/// mixer strip's per-layer Volume fader (bound to LayerRowViewModel.GainDb) is the only control
/// for that property -- the FX panel itself must not expose a second Slider bound to GainDb
/// outside the Limiter panel (LimiterGainDb is a distinct property and is fine).</summary>
public class MixingScreenTests
{
    [StaFact]
    public void FxPanel_HasNoSliderBoundToGainDb()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();

        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var slidersBoundToGainDb = FindDescendants<Slider>(window.FxPanel)
                .Where(s => BindingOperations.GetBindingExpression(s, Slider.ValueProperty)?.ParentBinding.Path.Path == "GainDb")
                .ToList();

            Assert.Empty(slidersBoundToGainDb);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }
}
