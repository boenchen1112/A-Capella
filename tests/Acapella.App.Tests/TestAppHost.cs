using System.Windows;
using Xunit;

// Bug audit #14 fix-up: xUnit parallelizes across test collections by default, and (with no
// [Collection] attribute) every test class here is its own collection -- so two [StaFact] tests
// from different classes can each construct+show a MainWindow at the same moment on separate STA
// threads, all against the one shared Application instance below. That's the same "shared mutable
// global" hazard the SharedHostedService collection (LayerRowEditorTrackingTests.cs) was already
// introduced for, just assembly-wide instead of one class's static field: WPF's Window/Dispatcher
// machinery isn't documented as safe for concurrent construction across threads that all funnel
// into one Application, and empirically it wasn't -- PresentationSource.FromVisual/Keyboard.Focus
// on a freshly-shown window intermittently failed under concurrent test execution (see
// TransportShortcutFocusTests, which is what surfaced this). This test project is small enough
// that running it single-threaded costs nothing worth trading reliability for.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Acapella.App.Tests;

/// <summary>Shared headless Application bootstrap for WPF layout tests. There's no App.xaml
/// startup in a test host, so MainWindow's StaticResource lookups (RowBrush, PanelBrush, ...)
/// need the same merged dictionary loaded manually -- and since xUnit can run multiple [StaFact]
/// tests on separate STA threads concurrently, the null-check-then-construct race must be guarded
/// by a lock (Application's constructor throws if called twice in one process).
///
/// ShutdownMode = OnExplicitShutdown (bug audit #14 fix-up, found while making the new
/// TransportShortcutFocusTests pass): this Application instance is shared by every [StaFact] in
/// the assembly, one per test's own MainWindow. WPF's default ShutdownMode (OnLastWindowClose)
/// calls Application.Shutdown() the moment any one test's `window.Close()` leaves zero open
/// windows -- which happens after basically every test, since each test owns exactly one window
/// at a time. That shutdown is process-wide (Application.Current is a singleton), so it silently
/// broke Window.Show() for whichever test happened to run afterward: PresentationSource.FromVisual
/// on the new window returned null and Keyboard.Focus was a no-op, even though window.IsLoaded
/// looked fine moments earlier in an isolated run. No prior test in this suite noticed because
/// none of them needed PresentationSource or keyboard focus to actually reach a live window --
/// this one does. OnExplicitShutdown stops that auto-shutdown; nothing here ever calls
/// Application.Current.Shutdown(), so the app instance just stays alive for the rest of the run.</summary>
internal static class TestAppHost
{
    private static readonly object Lock = new();

    public static void EnsureApplicationResourcesLoaded()
    {
        lock (Lock)
        {
            if (Application.Current is not null) return;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Acapella.App;component/Theme/DarkTheme.xaml")
            };
            app.Resources.MergedDictionaries.Add(dictionary);
        }
    }
}
