using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Regression test for v5 P2 task 2: the sidebar's per-layer Volume slider (bound to
/// LayerRowViewModel.GainDb) replaces the old Mixing-screen top-strip "Gain" slider that used to
/// share the same property -- the Mixing screen itself must not expose a second Slider bound to
/// GainDb outside the Limiter panel (LimiterGainDb is a distinct property and is fine).</summary>
public class MixingScreenTests
{
    [StaFact]
    public void MixingScreen_HasNoSliderBoundToGainDb()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();

        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var slidersBoundToGainDb = FindDescendants<Slider>(window.MixingScreen)
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
