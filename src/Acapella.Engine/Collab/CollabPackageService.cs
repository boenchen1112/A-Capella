using System.IO.Compression;
using System.Text.Json;
using Acapella.Engine.Persistence;
using Acapella.Engine.Project;

namespace Acapella.Engine.Collab;

/// <summary>
/// Offline collaboration: a collab package is one zip holding a project file plus every media file it
/// uses, so a friend can open it, record or add their own layers, and send a package back. Layers are
/// then pulled from a package (or any project file) into the open project with ImportLayers.
/// Everything here is file-based; there is no server.
/// </summary>
public class CollabPackageService
{
    public const string PackageSuffix = ".acapella.zip";
    public const string ProjectEntryName = "project.acapella.json";
    private const string ManifestEntryName = "package.json";
    private const string MediaFolder = "media/";
    private const string FormatName = "acapella-collab";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ProjectPersistenceService _persistence = new();

    /// <summary>Writes the project (already snapshotted, so live plugin edits are included) and its media
    /// to zipPath. Fails before writing anything if a media file is missing. The zip is built next to
    /// the target and moved into place, so a failed export never leaves a half-written package.</summary>
    public void Export(ProjectFileDto project, string zipPath)
    {
        var copy = Clone(project);

        var media = new List<(string EntryName, string SourceFile)>();
        var missing = new List<string>();
        foreach (var layer in copy.Layers)
        {
            if (!File.Exists(layer.SourcePath)) { missing.Add(layer.SourcePath); continue; }
            string entry = $"{MediaFolder}layer{layer.LayerId}-cell{layer.CellIndex}{Path.GetExtension(layer.SourcePath)}";
            media.Add((entry, layer.SourcePath));
            layer.SourcePath = entry;
        }

        if (copy.Video?.BackgroundImagePath is { Length: > 0 } background)
        {
            if (File.Exists(background))
            {
                string entry = $"{MediaFolder}background{Path.GetExtension(background)}";
                media.Add((entry, background));
                copy.Video.BackgroundImagePath = entry;
            }
            else
            {
                copy.Video.BackgroundImagePath = null;   // a missing background is cosmetic; don't block the export
            }
        }

        if (missing.Count > 0)
            throw new FileNotFoundException("These media files are missing, so the project can't be packaged:\n" + string.Join('\n', missing.Select(Path.GetFileName)));

        string dir = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? ".";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                WriteText(zip, ManifestEntryName, JsonSerializer.Serialize(new { format = FormatName, version = FormatVersion }, JsonOptions));
                WriteText(zip, ProjectEntryName, JsonSerializer.Serialize(copy, JsonOptions));
                foreach (var (entryName, sourceFile) in media)
                    zip.CreateEntryFromFile(sourceFile, entryName, CompressionLevel.NoCompression);   // audio/video is already compressed
            }
            File.Move(temp, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Extracts a package into a fresh folder under extractRoot and returns its project with every
    /// media path made absolute (pointing into that folder). Rejects entries that would land outside it.</summary>
    public (ProjectFileDto Project, string Folder) OpenPackage(string zipPath, string extractRoot)
    {
        string name = Path.GetFileName(zipPath);
        if (name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase)) name = name[..^PackageSuffix.Length];
        name = Sanitize(name);
        if (name.Length > 40) name = name[..40];
        string folder = Path.Combine(extractRoot, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string fullFolder = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(ManifestEntryName) is null || zip.GetEntry(ProjectEntryName) is null)
                throw new InvalidDataException("This isn't an Acapella collab package.");

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                if (!target.StartsWith(fullFolder, StringComparison.Ordinal))
                    throw new InvalidDataException($"The package contains an unsafe path: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            string projectFile = Path.Combine(folder, ProjectEntryName);
            var project = _persistence.LoadFromFile(projectFile);
            foreach (var layer in project.Layers)
                layer.SourcePath = ResolveInside(folder, layer.SourcePath);
            if (project.Video?.BackgroundImagePath is { Length: > 0 } bg)
                project.Video.BackgroundImagePath = ResolveInside(folder, bg);
            return (project, folder);
        }
        catch
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* best effort */ }
            throw;
        }
    }

    /// <summary>Loads something to import layers from: a collab package (extracted under extractRoot) or a
    /// plain project file, whose media paths are used as they are.</summary>
    public ProjectFileDto LoadSource(string path, string extractRoot) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? OpenPackage(path, extractRoot).Project
            : _persistence.LoadFromFile(path);

    /// <summary>Adds the chosen layers (ids as they appear in source) to session, each into the lowest
    /// free cell, with fresh layer ids. Copies source, trims, timing, mirror, name and the full mix and
    /// effect settings. The Melodyne archive key is not carried over: it names the source project's
    /// layer id and archive store, so the layer is analysed afresh here. Layers that don't fit under the
    /// layer cap are reported in Skipped. Grows the layout if needed; the caller commits the undo step.</summary>
    public ImportResult ImportLayers(ProjectSession session, ProjectFileDto source, IEnumerable<int> sourceLayerIds)
    {
        var wanted = sourceLayerIds.ToHashSet();
        var chosen = source.Layers.Where(l => wanted.Contains(l.LayerId)).OrderBy(l => l.CellIndex).ThenBy(l => l.LayerId).ToList();

        var free = LayerImportPlanner.FreeCells(session.Layers.Layers.Select(l => l.CellIndex));
        int placeable = Math.Min(free.Count, chosen.Count);
        int nextId = session.Layers.NextLayerId();

        var dtos = new List<LayerDto>();
        for (int i = 0; i < placeable; i++)
        {
            var dto = Clone(chosen[i]);
            dto.LayerId = nextId + i;
            dto.CellIndex = free[i];
            dto.AraArchiveKey = null;
            dtos.Add(dto);
        }

        var added = _persistence.FromDto(new ProjectFileDto { LayoutId = "2x2", Layers = dtos }).Layers.Layers.ToList();
        session.Layers.Restore(session.Layers.Layers.Concat(added));
        session.EnsureLayoutFits();

        var skipped = chosen.Skip(placeable).Select(l => l.Name is { Length: > 0 } n ? n : $"Layer {l.CellIndex + 1}").ToList();
        return new ImportResult(added, skipped);
    }

    public sealed record ImportResult(IReadOnlyList<LayerModel> Added, IReadOnlyList<string> Skipped);

    private static void WriteText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>A package-relative path made absolute under folder; anything already absolute or trying to
    /// climb out of the folder is rejected rather than trusted.</summary>
    private static string ResolveInside(string folder, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(folder, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"The package refers to a file outside itself: {relative}");
        return full;
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "package" : clean;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
