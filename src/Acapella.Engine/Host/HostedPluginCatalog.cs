namespace Acapella.Engine.Host;

/// <summary>
/// The hard vendor limit from the v6 plan: only these FabFilter plugins (and Melodyne, hosted
/// separately via ARA in Phase 2A) are ever hosted. Paths are the standard shared VST3 install
/// location confirmed present on this machine (see CLAUDE.md's Environment section). FabFilter's
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
        ["FabFilter Pro-G"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-G.vst3",
        ["FabFilter Pro-R 2"] = $@"{Vst3Root}\FabFilter\FabFilter Pro-R 2.vst3",
        ["Melodyne"] = $@"{Vst3Root}\Celemony\Melodyne\Melodyne.vst3",
    };
}
