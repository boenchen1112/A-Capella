namespace A_Capella.Engine.Presets;

/// <summary>Names one FabFilter factory preset: the hosted plugin's label (e.g. "FabFilter Pro-C 3"),
/// the category folder it lives in ("" for the plugin's top level; nested folders use '/'), and the
/// preset's name. A reference rather than the preset itself, so a project only stores what was chosen.</summary>
public sealed record PresetRef(string Plugin, string Category, string Name)
{
    public override string ToString() => string.IsNullOrEmpty(Category) ? $"{Plugin}: {Name}" : $"{Plugin}: {Category}/{Name}";
}

/// <summary>A folder of presets as FabFilter groups them, with a readable title for menus.</summary>
public sealed record PresetCategory(string Key, string Title, IReadOnlyList<PresetRef> Presets);

/// <summary>Turns a preset reference into a state blob for a freshly created plugin instance.
/// Null when the preset can't be found.</summary>
public interface IPresetStateBuilder
{
    byte[]? Build(PresetRef preset, byte[] defaultState);
}

/// <summary>
/// FabFilter's official factory presets, read from their .ffp files under the user's Documents
/// folder (which Windows may redirect, e.g. to OneDrive). Lists them by category and builds the
/// plugin state that carries one of them.
/// </summary>
public sealed class FabFilterPresetLibrary : IPresetStateBuilder
{
    public const string DefaultSettingName = "Default Setting";

    public static FabFilterPresetLibrary Default { get; } = new();

    private readonly string? _rootOverride;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<PresetCategory>> _categories = new();
    private readonly Dictionary<string, FabFilterPresetFile?> _defaultPresets = new();

    public FabFilterPresetLibrary(string? root = null) => _rootOverride = root;

    public string RootFolder => _rootOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FabFilter", "Presets");

    private static string FolderFor(string pluginLabel) =>
        pluginLabel.StartsWith("FabFilter ", StringComparison.Ordinal) ? pluginLabel["FabFilter ".Length..] : pluginLabel;

    /// <summary>The plugin's presets grouped by category; empty (not an error) when none are installed.</summary>
    public IReadOnlyList<PresetCategory> GetCategories(string pluginLabel)
    {
        lock (_gate)
        {
            if (_categories.TryGetValue(pluginLabel, out var cached)) return cached;
            var result = Scan(pluginLabel);
            _categories[pluginLabel] = result;
            return result;
        }
    }

    /// <summary>Forgets what was scanned, so presets installed since are picked up.</summary>
    public void Refresh()
    {
        lock (_gate) { _categories.Clear(); _defaultPresets.Clear(); }
    }

    private IReadOnlyList<PresetCategory> Scan(string pluginLabel)
    {
        string dir = Path.Combine(RootFolder, FolderFor(pluginLabel));
        if (!Directory.Exists(dir)) return Array.Empty<PresetCategory>();

        var byCategory = new SortedDictionary<string, List<PresetRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.ffp", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(dir, file);
            string category = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
            string name = Path.GetFileNameWithoutExtension(file);
            if (!byCategory.TryGetValue(category, out var list)) byCategory[category] = list = new List<PresetRef>();
            list.Add(new PresetRef(pluginLabel, category, name));
        }

        return byCategory
            .Select(kv => new PresetCategory(kv.Key, Title(kv.Key),
                kv.Value.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    /// <summary>"_2 Small/Drums" becomes "Small / Drums"; the leading underscore and sort number are
    /// FabFilter's ordering, not part of the name.</summary>
    public static string Title(string category)
    {
        if (category.Length == 0) return "General";
        var parts = category.Split('/').Select(part =>
        {
            part = part.TrimStart('_');
            int i = 0;
            while (i < part.Length && char.IsDigit(part[i])) i++;
            if (i > 0 && i < part.Length && part[i] == ' ') part = part[(i + 1)..];
            return part;
        });
        return string.Join(" / ", parts);
    }

    public string? ResolvePath(PresetRef preset)
    {
        string path = Path.Combine(RootFolder, FolderFor(preset.Plugin), preset.Category.Replace('/', Path.DirectorySeparatorChar), preset.Name + ".ffp");
        return File.Exists(path) ? path : null;
    }

    public bool Exists(PresetRef preset) => ResolvePath(preset) is not null;

    public byte[]? Build(PresetRef preset, byte[] defaultState)
    {
        string? path = ResolvePath(preset);
        if (path is null) return null;
        try
        {
            var file = FabFilterPresetFile.Parse(File.ReadAllBytes(path));
            return FabFilterPresetState.Build(defaultState, file, GetDefaultPreset(preset.Plugin));
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private FabFilterPresetFile? GetDefaultPreset(string pluginLabel)
    {
        lock (_gate)
        {
            if (_defaultPresets.TryGetValue(pluginLabel, out var cached)) return cached;
            FabFilterPresetFile? parsed = null;
            string path = Path.Combine(RootFolder, FolderFor(pluginLabel), DefaultSettingName + ".ffp");
            if (File.Exists(path))
            {
                try { parsed = FabFilterPresetFile.Parse(File.ReadAllBytes(path)); }
                catch (Exception ex) when (ex is FormatException or IOException) { }
            }
            _defaultPresets[pluginLabel] = parsed;
            return parsed;
        }
    }
}
