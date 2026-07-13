using System.Windows;

namespace Acapella.App.Tests;

/// <summary>Shared headless Application bootstrap for WPF layout tests. There's no App.xaml
/// startup in a test host, so MainWindow's StaticResource lookups (RowBrush, PanelBrush, ...)
/// need the same merged dictionary loaded manually -- and since xUnit can run multiple [StaFact]
/// tests on separate STA threads concurrently, the null-check-then-construct race must be guarded
/// by a lock (Application's constructor throws if called twice in one process).</summary>
internal static class TestAppHost
{
    private static readonly object Lock = new();

    public static void EnsureApplicationResourcesLoaded()
    {
        lock (Lock)
        {
            if (Application.Current is not null) return;

            _ = new Application();
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Acapella.App;component/Theme/DarkTheme.xaml")
            };
            Application.Current.Resources.MergedDictionaries.Add(dictionary);
        }
    }
}
