using System.IO.Compression;
using System.Text;
using Acapella.Engine.Collab;
using Acapella.Engine.Composite;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Collab;

public class CollabPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"acapella-collab-test-{Guid.NewGuid():N}");
    private readonly MixEngine _mixEngine = new(NoHostedPluginsAvailable.Instance);
    private readonly CollabPackageService _service = new();

    public CollabPackageServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MediaFile(string name, string content)
    {
        string path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ProjectSession NewSession() => new(_mixEngine);

    private static LayerModel AddLayer(ProjectSession s, string path, int cell, string? name = null)
    {
        var layer = s.Layers.Add(LayerKind.RecordedAV, path);
        layer.CellIndex = cell;
        layer.Name = name;
        return layer;
    }

    // ----- export / open -----

    [Fact]
    public void ExportThenOpen_RoundTripsTheProjectAndItsMedia()
    {
        var session = NewSession();
        var a = AddLayer(session, MediaFile("a.mkv", "AAA"), 0, "Lead");
        a.Mirror = true;
        a.MixParameters.Pan = -0.4f;
        a.MixParameters.EqHostedState = new byte[] { 1, 2, 3 };
        AddLayer(session, MediaFile("b.mp4", "BBB"), 1, "Bass");
        string bg = MediaFile("bg.png", "PNG");
        session.Video = session.Video with { Background = new BackgroundSettings { ImagePath = bg } };

        string zip = Path.Combine(_root, "out", "song" + CollabPackageService.PackageSuffix);
        _service.Export(session.Snapshot(), zip);
        Assert.True(File.Exists(zip));

        var (project, folder) = _service.OpenPackage(zip, Path.Combine(_root, "extract"));
        Assert.Equal(2, project.Layers.Count);
        Assert.All(project.Layers, l => Assert.StartsWith(folder, l.SourcePath));
        Assert.Equal("AAA", File.ReadAllText(project.Layers[0].SourcePath));
        Assert.Equal("BBB", File.ReadAllText(project.Layers[1].SourcePath));
        Assert.Equal("Lead", project.Layers[0].Name);
        Assert.True(project.Layers[0].Mirror);
        Assert.Equal(-0.4f, project.Layers[0].MixParameters.Pan);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), project.Layers[0].MixParameters.EqHostedStateBase64);
        Assert.Equal("PNG", File.ReadAllText(project.Video!.BackgroundImagePath!));
    }

    [Fact]
    public void Export_DoesNotChangeTheCallersProject()
    {
        var session = NewSession();
        string original = MediaFile("a.mkv", "AAA");
        AddLayer(session, original, 0);
        var dto = session.Snapshot();

        _service.Export(dto, Path.Combine(_root, "p.acapella.zip"));

        Assert.Equal(original, dto.Layers[0].SourcePath);
    }

    [Fact]
    public void Export_WithAMissingMediaFile_FailsAndWritesNothing()
    {
        var session = NewSession();
        AddLayer(session, MediaFile("a.mkv", "AAA"), 0);
        AddLayer(session, Path.Combine(_root, "gone.mkv"), 1);
        string zip = Path.Combine(_root, "p.acapella.zip");

        var ex = Assert.Throws<FileNotFoundException>(() => _service.Export(session.Snapshot(), zip));

        Assert.Contains("gone.mkv", ex.Message);
        Assert.False(File.Exists(zip));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Export_OverwritesAnExistingPackage()
    {
        var session = NewSession();
        AddLayer(session, MediaFile("a.mkv", "first"), 0);
        string zip = Path.Combine(_root, "p.acapella.zip");
        _service.Export(session.Snapshot(), zip);

        File.WriteAllText(session.Layers.Layers[0].SourcePath, "second");
        _service.Export(session.Snapshot(), zip);

        var (project, _) = _service.OpenPackage(zip, Path.Combine(_root, "x"));
        Assert.Equal("second", File.ReadAllText(project.Layers[0].SourcePath));
    }

    [Fact]
    public void OpenPackage_RejectsAZipThatIsNotAPackage_AndLeavesNoFolderBehind()
    {
        string zip = Path.Combine(_root, "random.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { var e = z.CreateEntry("hello.txt"); using var w = new StreamWriter(e.Open()); w.Write("hi"); }
        string extractRoot = Path.Combine(_root, "extract");

        Assert.Throws<InvalidDataException>(() => _service.OpenPackage(zip, extractRoot));

        Assert.Empty(Directory.GetDirectories(extractRoot));
    }

    [Fact]
    public void OpenPackage_RejectsEntriesThatEscapeTheFolder()
    {
        string zip = Path.Combine(_root, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(z, "package.json", "{}");
            Write(z, "project.acapella.json", "{\"LayoutId\":\"2x2\",\"Layers\":[]}");
            Write(z, "../escaped.txt", "pwned");
        }

        Assert.Throws<InvalidDataException>(() => _service.OpenPackage(zip, Path.Combine(_root, "extract")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "extract", "escaped.txt")));
    }

    [Theory]
    [InlineData("../../outside.wav")]
    [InlineData("/etc/passwd")]
    public void OpenPackage_RejectsAProjectPointingOutsideThePackage(string sourcePath)
    {
        string zip = Path.Combine(_root, "sneaky.zip");
        string json = "{\"LayoutId\":\"2x2\",\"Layers\":[{\"LayerId\":0,\"Kind\":\"RecordedAV\",\"SourcePath\":\"" + sourcePath + "\"}]}";
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { Write(z, "package.json", "{}"); Write(z, "project.acapella.json", json); }

        Assert.Throws<InvalidDataException>(() => _service.OpenPackage(zip, Path.Combine(_root, "extract")));
    }

    private static void Write(ZipArchive z, string name, string text)
    {
        var e = z.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    // ----- import -----

    private static ProjectFileDto SourceProject(params (int id, int cell, string name)[] layers)
    {
        var dto = new ProjectFileDto { LayoutId = "2x2" };
        foreach (var (id, cell, name) in layers)
            dto.Layers.Add(new LayerDto
            {
                LayerId = id, CellIndex = cell, Kind = "RecordedAV", SourcePath = $"/media/{name}.mkv", Name = name,
                CalibratedOffsetMs = 120, ManualOffsetMs = 7, TrimStartMs = 50, TrimEndMs = 900, Mirror = true,
                AraArchiveKey = $"{id}|hash",
                MixParameters = new MixParametersDto { GainDb = -3, Pan = 0.5f, Mute = true, PitchBackend = "Automatic2B", EqHostedStateBase64 = "AQID" },
            });
        return dto;
    }

    [Fact]
    public void ImportLayers_PlacesThemInFreeCells_WithFreshIds_AndKeepsTheirSettings()
    {
        var session = NewSession();
        AddLayer(session, MediaFile("mine.mkv", "x"), 0, "Mine");   // id 0, cell 0

        var result = _service.ImportLayers(session, SourceProject((0, 0, "Alto"), (1, 1, "Tenor")), new[] { 0, 1 });

        Assert.Equal(2, result.Added.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { 0, 1, 2 }, session.Layers.Layers.Select(l => l.LayerId));
        Assert.Equal(new[] { 0, 1, 2 }, session.Layers.Layers.Select(l => l.CellIndex));
        var alto = session.Layers.Layers[1];
        Assert.Equal("Alto", alto.Name);
        Assert.Equal("/media/Alto.mkv", alto.SourcePath);
        Assert.True(alto.Mirror);
        Assert.Equal(120, alto.CalibratedOffsetMs);
        Assert.Equal(7, alto.ManualOffsetMs);
        Assert.Equal(50, alto.TrimStartMs);
        Assert.Equal(900, alto.TrimEndMs);
        Assert.Equal(-3, alto.MixParameters.GainDb);
        Assert.Equal(0.5f, alto.MixParameters.Pan);
        Assert.True(alto.MixParameters.Mute);
        Assert.Equal(new byte[] { 1, 2, 3 }, alto.MixParameters.EqHostedState);
        Assert.Null(alto.AraArchiveKey);
    }

    [Fact]
    public void ImportLayers_FillsGapsLowestFirst_AndOnlyTheChosenLayers()
    {
        var session = NewSession();
        AddLayer(session, MediaFile("a.mkv", "x"), 0);
        AddLayer(session, MediaFile("c.mkv", "x"), 2);

        _service.ImportLayers(session, SourceProject((0, 0, "A"), (1, 1, "B"), (2, 2, "C")), new[] { 2, 0 });

        var cells = session.Layers.Layers.Select(l => (l.Name, l.CellIndex)).Where(t => t.Name is "A" or "C").ToList();
        Assert.Equal(new[] { ("A", 1), ("C", 3) }, cells);   // source order (by cell), into the gaps 1 and 3
        Assert.DoesNotContain(session.Layers.Layers, l => l.Name == "B");
    }

    [Fact]
    public void ImportLayers_NewIdsNeverCollide_EvenWhenExistingIdsHaveGaps()
    {
        var session = NewSession();
        AddLayer(session, MediaFile("a.mkv", "x"), 0);
        var b = AddLayer(session, MediaFile("b.mkv", "x"), 1);
        session.Layers.Restore(new[] { b });   // ids now {1}: count (1) would collide with the surviving id

        _service.ImportLayers(session, SourceProject((0, 0, "X")), new[] { 0 });

        Assert.Equal(session.Layers.Layers.Count, session.Layers.Layers.Select(l => l.LayerId).Distinct().Count());
        var plainAdd = session.Layers.Add(LayerKind.UploadedAudioOnly, "z.wav");
        Assert.Equal(session.Layers.Layers.Count, session.Layers.Layers.Select(l => l.LayerId).Distinct().Count());
        Assert.Equal(b.LayerId + 2, plainAdd.LayerId);   // one past the highest id in use (the import took b+1)
    }

    [Fact]
    public void ImportLayers_GrowsTheLayoutWhenLayersOutgrowIt()
    {
        var session = NewSession();
        for (int i = 0; i < 3; i++) AddLayer(session, MediaFile($"m{i}.mkv", "x"), i);
        Assert.Equal("2x2", session.Video.LayoutId);

        _service.ImportLayers(session, SourceProject((0, 0, "A"), (1, 1, "B"), (2, 2, "C")), new[] { 0, 1, 2 });

        Assert.Equal(6, session.Layers.Layers.Count);
        Assert.True(session.Video.Layout.Cells.Count >= 6);
    }

    [Fact]
    public void ImportLayers_StopsAtTheLayerCap_AndReportsWhatWasLeftOut()
    {
        var session = NewSession();
        for (int i = 0; i < LayerCollection.MaxLayers - 1; i++) AddLayer(session, MediaFile($"m{i}.mkv", "x"), i);

        var result = _service.ImportLayers(session, SourceProject((0, 0, "First"), (1, 1, "Second"), (2, 2, "")), new[] { 0, 1, 2 });

        Assert.Single(result.Added);
        Assert.Equal(LayerCollection.MaxLayers, session.Layers.Layers.Count);
        Assert.Equal(new[] { "Second", "Layer 3" }, result.Skipped);
    }

    [Fact]
    public void ImportLayers_IgnoresUnknownIds_AndLeavesTheSourceUntouched()
    {
        var session = NewSession();
        var source = SourceProject((5, 0, "Only"));

        var result = _service.ImportLayers(session, source, new[] { 99 });

        Assert.Empty(result.Added);
        Assert.Empty(session.Layers.Layers);
        Assert.Equal(5, source.Layers[0].LayerId);
        Assert.Equal("5|hash", source.Layers[0].AraArchiveKey);
    }

    [Fact]
    public void ImportLayers_IsUndoable()
    {
        var session = NewSession();
        AddLayer(session, MediaFile("a.mkv", "x"), 0);
        session.CommitEdit();

        _service.ImportLayers(session, SourceProject((0, 0, "A")), new[] { 0 });
        session.CommitEdit();
        Assert.Equal(2, session.Layers.Layers.Count);

        Assert.True(session.Undo());
        Assert.Single(session.Layers.Layers);
    }

    [Fact]
    public void LoadSource_ReadsAPlainProjectFileAsIs_AndAPackageViaExtraction()
    {
        var session = NewSession();
        string media = MediaFile("a.mkv", "AAA");
        AddLayer(session, media, 0);
        string json = Path.Combine(_root, "plain" + ProjectSession.ProjectFileSuffix);
        session.Save(json);
        string zip = Path.Combine(_root, "pkg" + CollabPackageService.PackageSuffix);
        _service.Export(session.Snapshot(), zip);

        var plain = _service.LoadSource(json, Path.Combine(_root, "x"));
        var packaged = _service.LoadSource(zip, Path.Combine(_root, "x"));

        Assert.Equal(media, plain.Layers[0].SourcePath);
        Assert.NotEqual(media, packaged.Layers[0].SourcePath);
        Assert.Equal("AAA", File.ReadAllText(packaged.Layers[0].SourcePath));
    }
}
