namespace A_Capella.Engine.Host;

/// <summary>
/// The hard vendor limit: only these FabFilter plugins (and Melodyne, hosted via ARA for note
/// analysis only) are ever hosted. Paths are the standard shared VST3 install
/// location. FabFilter's
/// installer places each plugin under a `FabFilter\` vendor subfolder (confirmed 2026-07-14 after
/// a reinstall), matching Melodyne's existing `Celemony\Melodyne\` subfolder pattern.
/// </summary>
public static class HostedPluginCatalog
{
    private const string Vst3Root = @"C:\Program Files\Common Files\VST3";

    public static readonly IReadOnlyDictionary<string, string> KnownPluginPaths = new Dictionary<string, string>
    {
        ["FabFilter Pro-Q 4"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-Q 4.vst3",
        ["FabFilter Pro-C 3"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-C 3.vst3",
        ["FabFilter Pro-L 2"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-L 2.vst3",
        ["FabFilter Pro-DS"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-DS.vst3",
        ["FabFilter Pro-R 2"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-R 2.vst3",
        // Analysis only: Melodyne reads note timing for auto-align. It is never used to correct pitch and no editor is opened.
        ["Melodyne"] = $@"{Vst3Root}\Celemony\Melodyne\Melodyne.vst3",
    };

    /// <summary>Reserved for the macOS port: Antares Auto-Tune is the only pitch tool, and it exists
    /// only on the Mac. It has no entry in KnownPluginPaths, so nothing on Windows can host it.</summary>
    public const string AutoTunePluginLabel = "Antares Auto-Tune";

    public static bool AutoTuneSupported => OperatingSystem.IsMacOS();
}
