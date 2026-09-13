using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Acapella.Engine.Tests.Host;

namespace Acapella.Engine.Tests.Project;

public class ProjectSessionTests : IDisposable
{
    private const int SampleRate = 44100;

    private readonly FakeHostedPluginFactory _fakes = new FakeHostedPluginFactory().With(FxSlots.Eq.PluginLabel);
    private readonly Acapella.Engine.Host.HostedPluginService _service;
    private readonly MixEngine _mixEngine;
    private readonly List<string> _tempFiles = new();

    public ProjectSessionTests()
    {
        _service = _fakes.CreateService();
        _mixEngine = new MixEngine(_service);
    }

    public void Dispose()
    {
        _mixEngine.Dispose();
        _service.Dispose();
        foreach (var f in _tempFiles) File.Delete(f);
    }

    private FakeHostedPlugin OpenEqEditorLiveInstance(LayerModel layer) =>
        (FakeHostedPlugin)_mixEngine.GetOrCreateHostedInstance(layer.LayerId, FxSlots.Eq.Stage, FxSlots.Eq.PluginLabel, FxSlots.Eq.GetHostedState(layer.MixParameters), SampleRate);

    private string TempProjectPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acapella-session-{Guid.NewGuid()}.acapella.json");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void UndoAndRedo_StepThroughCommittedEdits()
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        session.CommitEdit();

        layer.MixParameters.GainDb = -6f;
        session.MasterVolumeDb = -3f;
        session.CommitEdit();

        Assert.True(session.Undo());
        Assert.Equal(0f, session.Layers.Layers.Single().MixParameters.GainDb);
        Assert.Equal(0f, session.MasterVolumeDb);

        Assert.True(session.Redo());
        Assert.Equal(-6f, session.Layers.Layers.Single().MixParameters.GainDb);
        Assert.Equal(-3f, session.MasterVolumeDb);

        Assert.False(session.Redo());
    }

    [Fact]
    public void Layers_IsTheSameCollectionAcrossRestores()
    {
        var session = new ProjectSession(_mixEngine);
        var layers = session.Layers;
        session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        session.CommitEdit();

        session.Undo();
        session.New();

        Assert.Same(layers, session.Layers);
    }

    [Fact]
    public void Snapshot_CapturesLivePluginEditorTweaks()
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        OpenEqEditorLiveInstance(layer).TweakInEditor(new byte[] { 5, 6 });

        var dto = session.Snapshot();

        Assert.Equal(Convert.ToBase64String(new byte[] { 5, 6 }), dto.Layers.Single().MixParameters.EqHostedStateBase64);
    }

    [Fact]
    public void Undo_PushesTheRestoredPluginStateIntoTheStillLiveInstance()
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        var plugin = OpenEqEditorLiveInstance(layer);

        plugin.TweakInEditor(new byte[] { 1 });
        session.CommitEdit();
        plugin.TweakInEditor(new byte[] { 2 });
        session.CommitEdit();

        session.Undo();

        Assert.Equal(new byte[] { 1 }, plugin.State);
        Assert.Equal(new byte[] { 1 }, session.Layers.Layers.Single().MixParameters.EqHostedState);
    }

    [Fact]
    public void SaveThenOpen_RestoresTheProject_AndStartsFreshUndoHistory()
    {
        var saved = new ProjectSession(_mixEngine);
        var layer = saved.Layers.Add(LayerKind.UploadedVideo, "clip.mp4");
        layer.TrimStartMs = 250;
        saved.MetronomeBpm = 96;
        saved.MasterVolumeDb = -2f;
        string path = TempProjectPath();
        saved.Save(path);

        var opened = new ProjectSession(_mixEngine);
        opened.Layers.Add(LayerKind.UploadedAudioOnly, "other.wav");
        opened.CommitEdit();
        opened.Open(path);

        var restored = opened.Layers.Layers.Single();
        Assert.Equal("clip.mp4", restored.SourcePath);
        Assert.Equal(250, restored.TrimStartMs);
        Assert.Equal(96, opened.MetronomeBpm);
        Assert.Equal(-2f, opened.MasterVolumeDb);
        Assert.False(opened.Undo());
    }

    [Fact]
    public void New_EmptiesTheProject_KeepsBpm_AndStartsFreshUndoHistory()
    {
        var session = new ProjectSession(_mixEngine);
        session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        session.MetronomeBpm = 90;
        session.MasterVolumeDb = -4f;
        session.CommitEdit();

        session.New();

        Assert.Empty(session.Layers.Layers);
        Assert.Equal(0f, session.MasterVolumeDb);
        Assert.Null(session.LatencyOffsetMsUsed);
        Assert.Equal(90, session.MetronomeBpm);
        Assert.False(session.Undo());
    }

    [Fact]
    public void SnapshotForExport_IsUnaffectedByLaterEdits()
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        layer.MixParameters.GainDb = -1f;
        session.MasterVolumeDb = -5f;

        var (layers, masterVolumeDb) = session.SnapshotForExport();
        layer.MixParameters.GainDb = -20f;
        session.MasterVolumeDb = 0f;

        Assert.Equal(-1f, layers.Layers.Single().MixParameters.GainDb);
        Assert.Equal(-5f, masterVolumeDb);
    }
}
