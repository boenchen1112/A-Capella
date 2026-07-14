using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>
/// v7 2A task 37 / Pause Rule 3 gate: the first real question Phase 2A must answer before any
/// Document Controller work (task 38) is written -- does the actual Melodyne install on this
/// machine expose an ARA factory, or is it a plain-VST3-only tier? HostedPluginInstance.
/// TryScanAraCapability only reads the plugin's factory metadata (JUCE's PluginDescription::
/// hasARAExtension), so this is safe to call directly with no Initialize() and no
/// [Collection("JuceHosting")] -- unlike every other native-bridge test in this project, it never
/// touches JUCE's MessageManager.
/// </summary>
public class AraCapabilityProbeTests
{
    [Fact]
    public void Melodyne_AraCapabilityScan_ReportsTierOnThisMachine()
    {
        string melodynePath = HostedPluginCatalog.KnownPluginPaths["Melodyne"];
        if (!File.Exists(melodynePath) && !Directory.Exists(melodynePath))
        {
            Console.WriteLine($"SKIPPED: Melodyne not found at '{melodynePath}' on this machine.");
            return;
        }

        bool found = HostedPluginInstance.TryScanAraCapability(melodynePath, out var description);

        // Not an Assert on a specific value -- this test's job is to report the tier so a human
        // (and future Claude Code sessions) knows whether 2A can proceed past task 37 or must take
        // the Pause Rule 3 / fallback-only path (task 41), not to enforce one particular outcome.
        if (!found)
        {
            Console.WriteLine($"aca_scan_ara_capability('{melodynePath}') = not found / not a valid plugin.");
            return;
        }

        Console.WriteLine($"aca_scan_ara_capability('{melodynePath}') = name='{description!.Name}', version='{description.Version}', IsAraCapable={description.IsAraCapable}");
        if (!description.IsAraCapable)
            Console.WriteLine("PAUSE RULE 3: Melodyne found but NOT ARA-capable on this machine -- 2A should take the fallback-only path (task 41), not build the Document Controller (task 38).");
    }
}
