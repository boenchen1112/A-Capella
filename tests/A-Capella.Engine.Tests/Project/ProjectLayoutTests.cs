using A_Capella.Engine.Composite;
using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Project;

namespace A_Capella.Engine.Tests.Project;

public class ProjectLayoutTests : IDisposable
{
    private readonly MixEngine _mixEngine = new(NoHostedPluginsAvailable.Instance);
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var f in _temp) if (File.Exists(f)) File.Delete(f);
    }

    private string TempPath()
    {
        string p = Path.Combine(Path.GetTempPath(), $"layout-{Guid.NewGuid()}{ProjectSession.ProjectFileSuffix}");
        _temp.Add(p);
        return p;
    }

    private static void AddLayerAt(ProjectSession s, int cell)
    {
        var l = s.Layers.Add(LayerKind.UploadedAudioOnly, $"a{cell}.wav");
        l.CellIndex = cell;
    }

    [Fact]
    public void NewProject_DefaultsTo2x2()
    {
        var session = new ProjectSession(_mixEngine);
        Assert.Equal("2x2", session.Video.LayoutId);
    }

    [Fact]
    public void TrySetLayout_RejectsALayoutSmallerThanTheLayersInUse()
    {
        var session = new ProjectSession(_mixEngine);
        AddLayerAt(session, 0);
        AddLayerAt(session, 4);   // needs 5 cells

        Assert.False(session.TrySetLayout("2x2", out var error));
        Assert.Contains("5", error);
        Assert.Equal("2x2", session.Video.LayoutId);

        Assert.True(session.TrySetLayout("3x3", out _));
        Assert.Equal("3x3", session.Video.LayoutId);
    }

    [Fact]
    public void TrySetLayout_MarksTheProjectDirty()
    {
        var session = new ProjectSession(_mixEngine);
        Assert.False(session.IsDirty);
        Assert.True(session.TrySetLayout("3-cols", out _));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void EnsureLayoutFits_GrowsTheLayoutOnlyWhenNeeded()
    {
        var session = new ProjectSession(_mixEngine);
        AddLayerAt(session, 2);
        Assert.False(session.EnsureLayoutFits());     // 2x2 holds cells 0..3
        Assert.Equal("2x2", session.Video.LayoutId);

        AddLayerAt(session, 6);                        // needs 7 cells
        Assert.True(session.EnsureLayoutFits());
        Assert.Equal(7, session.Video.Layout.Cells.Count);
    }

    [Fact]
    public void Layout_SurvivesSaveAndOpen()
    {
        var session = new ProjectSession(_mixEngine);
        AddLayerAt(session, 0);
        Assert.True(session.TrySetLayout("1-over-2", out _));
        string path = TempPath();
        session.Save(path);

        var reopened = new ProjectSession(_mixEngine);
        reopened.Open(path);
        Assert.Equal("1-over-2", reopened.Video.LayoutId);
    }

    [Fact]
    public void Layout_IsUndoable()
    {
        var session = new ProjectSession(_mixEngine);
        Assert.True(session.TrySetLayout("2-rows", out _));
        session.CommitEdit();
        Assert.True(session.Undo());
        Assert.Equal("2x2", session.Video.LayoutId);
    }

    [Fact]
    public void LegacyFile_With2x2LayoutId_OpensAs2x2()
    {
        string path = TempPath();
        File.WriteAllText(path, "{\"LayoutId\":\"2x2\",\"MetronomeBpm\":100,\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(path);
        Assert.Equal("2x2", session.Video.LayoutId);
    }

    [Fact]
    public void UnknownLayoutId_FallsBackTo2x2_InsteadOfFailingToOpen()
    {
        string path = TempPath();
        File.WriteAllText(path, "{\"LayoutId\":\"from-the-future\",\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(path);
        Assert.Equal("2x2", session.Video.LayoutId);
    }

    [Fact]
    public void SnapshotForExport_CarriesTheLayout()
    {
        var session = new ProjectSession(_mixEngine);
        Assert.True(session.TrySetLayout("3-cols", out _));
        var (_, _, _, _, video) = session.SnapshotForExport();
        Assert.Equal("3-cols", video.LayoutId);
    }

    [Fact]
    public void LayerCollection_AcceptsNineLayers_AndRejectsATenth()
    {
        var layers = new LayerCollection();
        for (int i = 0; i < 9; i++) layers.Add(LayerKind.UploadedAudioOnly, $"{i}.wav");
        Assert.Throws<InvalidOperationException>(() => layers.Add(LayerKind.UploadedAudioOnly, "10.wav"));
    }
}
