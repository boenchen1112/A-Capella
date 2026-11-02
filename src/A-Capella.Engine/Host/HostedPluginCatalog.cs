namespace A_Capella.Engine.Host;

/// <summary>
/// The hard vendor limit: only these FabFilter plugins (and Melodyne, hosted via ARA for note
/// analysis only) are ever hosted. Paths are the standard shared VST3 install
/// location for the platform.
/// </summary>
public static class HostedPluginCatalog
{
    // Windows installs FabFilter under a `FabFilter\` vendor subfolder and Melodyne under
    // `Celemony\Melodyne\`; macOS installers drop every .vst3 bundle directly in the shared
    // plug-ins folder.
    private static readonly string Vst3Root = OperatingSystem.IsMacOS()
        ? "/Library/Audio/Plug-Ins/VST3"
        : @"C:\Program Files\Common Files\VST3";

    private static string FabFilter(string name) => OperatingSystem.IsMacOS()
        ? Path.Combine(Vst3Root, $"{name}.vst3")
        : Path.Combine(Vst3Root, "FabFilter", $"{name}.vst3");

    public static readonly IReadOnlyDictionary<string, string> KnownPluginPaths = BuildKnownPaths();

    private static Dictionary<string, string> BuildKnownPaths()
    {
        var paths = new Dictionary<string, string>
    {
        ["FabFilter Pro-Q 4"] = FabFilter("FabFilter Pro-Q 4"),
        ["FabFilter Pro-C 3"] = FabFilter("FabFilter Pro-C 3"),
        ["FabFilter Pro-L 2"] = FabFilter("FabFilter Pro-L 2"),
        ["FabFilter Pro-DS"] = FabFilter("FabFilter Pro-DS"),
        ["FabFilter Pro-R 2"] = FabFilter("FabFilter Pro-R 2"),
        // Analysis only: Melodyne reads note timing for auto-align. It is never used to correct pitch and no editor is opened.
        ["Melodyne"] = OperatingSystem.IsMacOS()
            ? Path.Combine(Vst3Root, "Melodyne.vst3")
            : Path.Combine(Vst3Root, "Celemony", "Melodyne", "Melodyne.vst3"),
        };
        if (OperatingSystem.IsMacOS())
            paths[AutoTunePluginLabel] = Path.Combine(Vst3Root, "AutoTune Advanced.vst3");
        return paths;
    }

    /// <summary>Antares Auto-Tune is the only pitch tool, and it exists only on the Mac.</summary>
    public const string AutoTunePluginLabel = "Antares Auto-Tune";

    public static bool AutoTuneSupported => OperatingSystem.IsMacOS();
}
