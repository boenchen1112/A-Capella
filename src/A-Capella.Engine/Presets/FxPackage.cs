using System.Text.Json;
using A_Capella.Engine.Mix;

namespace A_Capella.Engine.Presets;

/// <summary>A named set of effect choices -- one factory preset (or none) for each of a layer's
/// four effects -- that can be applied to any layer in one step. Holds preset references, not
/// plugin state, so a package stays tiny and always means the same FabFilter factory presets.</summary>
public sealed class FxPackage
{
    public string Name { get; set; } = "";
    public PresetRef? CompressorPreset { get; set; }
    public PresetRef? EqPreset { get; set; }
    public PresetRef? DeEsserPreset { get; set; }
    public PresetRef? ReverbPreset { get; set; }

    /// <summary>True for packages shipped with the app (read-only); false for the user's own.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBuiltIn { get; set; }

    /// <summary>Captures a layer's current effect choices as a package.</summary>
    public static FxPackage FromParameters(string name, LayerMixParameters parameters) => new()
    {
        Name = name.Trim(),
        CompressorPreset = parameters.CompressorPreset,
        EqPreset = parameters.EqPreset,
        DeEsserPreset = parameters.DeEsserPreset,
        ReverbPreset = parameters.ReverbPreset,
    };

    /// <summary>Sets all four effects on the layer to this package's choices (an effect the package
    /// leaves out is switched off).</summary>
    public void ApplyTo(LayerMixParameters parameters)
    {
        parameters.CompressorPreset = CompressorPreset;
        parameters.EqPreset = EqPreset;
        parameters.DeEsserPreset = DeEsserPreset;
        parameters.ReverbPreset = ReverbPreset;
    }
}

/// <summary>
/// Finds, saves and removes effect packages. Built-in packages are .json files in a folder beside
/// the app (empty until some are added); the user's own live under %APPDATA%\A-Capella\Packages.
/// An unreadable file is skipped rather than failing the list.
/// </summary>
public sealed class FxPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FxPackageService(string? builtInFolder = null, string? userFolder = null)
    {
        BuiltInFolder = builtInFolder ?? Path.Combine(AppContext.BaseDirectory, "Packages");
        UserFolder = userFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "A-Capella", "Packages");
    }

    public string BuiltInFolder { get; }
    public string UserFolder { get; }

    /// <summary>Built-in packages first, then the user's, each sorted by name.</summary>
    public IReadOnlyList<FxPackage> List()
    {
        var all = new List<FxPackage>();
        all.AddRange(Read(BuiltInFolder, builtIn: true));
        all.AddRange(Read(UserFolder, builtIn: false));
        return all;
    }

    private static IEnumerable<FxPackage> Read(string folder, bool builtIn)
    {
        if (!Directory.Exists(folder)) yield break;
        var found = new List<FxPackage>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var package = JsonSerializer.Deserialize<FxPackage>(File.ReadAllText(file));
                if (package is null) continue;
                if (string.IsNullOrWhiteSpace(package.Name)) package.Name = Path.GetFileNameWithoutExtension(file);
                package.IsBuiltIn = builtIn;
                found.Add(package);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var package in found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) yield return package;
    }

    /// <summary>True if a package with this name already exists, built-in or the user's.</summary>
    public bool Exists(string name) => List().Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the name belongs to a built-in package, which can't be overwritten.</summary>
    public bool IsBuiltInName(string name) =>
        Read(BuiltInFolder, builtIn: true).Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves (or overwrites) one of the user's packages. Throws ArgumentException for a blank
    /// name or one that matches a built-in package.</summary>
    public void Save(FxPackage package)
    {
        string name = package.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("A package needs a name.", nameof(package));
        if (IsBuiltInName(name)) throw new ArgumentException($"\"{name}\" is a built-in package.", nameof(package));

        Directory.CreateDirectory(UserFolder);
        package.Name = name;
        File.WriteAllText(FileFor(name), JsonSerializer.Serialize(package, JsonOptions));
    }

    /// <summary>Deletes one of the user's own packages; built-in packages are left alone.</summary>
    public bool Delete(string name)
    {
        string path = FileFor(name.Trim());
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string FileFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return Path.Combine(UserFolder, safe + ".json");
    }
}
