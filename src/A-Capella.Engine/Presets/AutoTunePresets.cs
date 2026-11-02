using A_Capella.Engine.Host;

namespace A_Capella.Engine.Presets;

/// <summary>
/// Auto-Tune ships no preset files and no program list, so its presets are defined here as a
/// handful of parameter settings (retune speed, humanize, key, scale) applied to a fresh instance.
/// Everything else stays at the plugin's defaults, except the latency mode, which is set to the
/// highest quality because the chain compensates for the plugin's latency.
/// </summary>
public static class AutoTunePresets
{
    private sealed record Style(string Name, string RetuneSpeed, string Humanize, string FlexTune);

    private static readonly Style[] Styles =
    {
        new("Transparent", "120", "40", "30"),
        new("Natural", "40", "20", "20"),
        new("Tight", "15", "0", "0"),
        new("Hard Tune", "0", "0", "0"),
    };

    private static readonly string[] Keys = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly Lazy<IReadOnlyList<PresetCategory>> Lazily = new(Build);

    public static IReadOnlyList<PresetCategory> Categories => Lazily.Value;

    private static string Label => HostedPluginCatalog.AutoTunePluginLabel;

    private static IReadOnlyList<PresetCategory> Build()
    {
        var categories = new List<PresetCategory>
        {
            new("Chromatic", "Any key (nearest note)",
                Styles.Select(s => new PresetRef(Label, "Chromatic", s.Name)).ToList()),
        };
        foreach (var scale in new[] { "Major", "Minor" })
            categories.Add(new PresetCategory(scale, $"{scale} key",
                Keys.Select(k => new PresetRef(Label, scale, $"{k} {scale} - Natural")).ToList()));
        return categories;
    }

    public static bool Exists(PresetRef preset) =>
        preset.Plugin == Label && Categories.Any(c => c.Key == preset.Category && c.Presets.Contains(preset));

    /// <summary>Sets the plugin's parameters for a preset. False if the preset isn't one of ours.</summary>
    public static bool Apply(IHostedPlugin plugin, PresetRef preset)
    {
        if (!Exists(preset)) return false;

        string key = "C", scale = "Chromatic";
        Style style;
        if (preset.Category == "Chromatic")
            style = Styles.First(s => s.Name == preset.Name);
        else
        {
            scale = preset.Category;
            key = preset.Name.Split(' ')[0];
            style = Styles.First(s => s.Name == "Natural");
        }

        Set(plugin, "Retune Speed", style.RetuneSpeed);
        Set(plugin, "Humanize", style.Humanize);
        Set(plugin, "Flex Tune", style.FlexTune);
        Set(plugin, "Key", key);
        Set(plugin, "Scale", scale);
        Set(plugin, "Latency Mode", "HQ");
        Set(plugin, "AutoKey Listen", "Off");
        return true;
    }

    private static void Set(IHostedPlugin plugin, string parameter, string text)
    {
        for (int i = 0; i < plugin.ParameterCount; i++)
        {
            if (plugin.GetParameterName(i) != parameter) continue;
            plugin.SetParameterValue(i, plugin.GetParameterValueForText(i, text));
            return;
        }
    }
}

/// <summary>One place to ask about any hosted effect's presets, whichever vendor they come from.</summary>
public static class PresetLibraries
{
    public static IReadOnlyList<PresetCategory> GetCategories(string pluginLabel) =>
        pluginLabel == Host.HostedPluginCatalog.AutoTunePluginLabel
            ? AutoTunePresets.Categories
            : FabFilterPresetLibrary.Default.GetCategories(pluginLabel);

    public static bool Exists(PresetRef preset) =>
        preset.Plugin == Host.HostedPluginCatalog.AutoTunePluginLabel
            ? AutoTunePresets.Exists(preset)
            : FabFilterPresetLibrary.Default.Exists(preset);

    public static void Refresh() => FabFilterPresetLibrary.Default.Refresh();
}
