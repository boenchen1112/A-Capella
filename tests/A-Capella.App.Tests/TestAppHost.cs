using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Headless;
using A_Capella.App.Tests;
using Xunit;

// Every [AvaloniaFact] runs on the one headless UI thread Avalonia.Headless keeps for the assembly,
// so tests that build and show a MainWindow already run one at a time; the shared static state
// (LayerRowViewModel.SharedHostedService, the one Application) is safe, and parallelising the rest
// of this small suite buys nothing.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace A_Capella.App.Tests;

/// <summary>Builds the real application (theme included) on Avalonia's headless platform, so tests
/// exercise MainWindow with the same styles and resources the running app uses.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<A_Capella.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
