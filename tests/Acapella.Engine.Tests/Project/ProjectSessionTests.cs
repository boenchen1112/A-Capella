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
    public void Undo_StillReusesTheSameLiveInstance()
    {
        // Guards against over-fixing bug audit #5 by releasing hosted instances on undo: within one
        // project the instance identity is correct (PushSavedStateIntoLiveInstances handles it), so
        // undo must keep reusing the same live instance, not release it -- that would close the
        // user's open plugin editor windows on every undo.
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        var plugin = OpenEqEditorLiveInstance(layer);

        plugin.TweakInEditor(new byte[] { 1 });
        session.CommitEdit();
        plugin.TweakInEditor(new byte[] { 2 });
        session.CommitEdit();

        session.Undo();

        Assert.False(plugin.Disposed);
        Assert.Same(plugin, OpenEqEditorLiveInstance(layer));
    }

    [Fact]
    public void Open_DoesNotLeakThePreviousProjectsPluginStateIntoTheOpenedProject()
    {
        // project B: saved with NO hosted EQ state
        var saved = new ProjectSession(_mixEngine);
        saved.Layers.Add(LayerKind.UploadedVideo, "clip.mp4");
        string path = TempProjectPath();
        saved.Save(path);

        // project A: live in the session, layer 0, EQ tweaked in its editor
        var session = new ProjectSession(_mixEngine);
        var layerA = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        OpenEqEditorLiveInstance(layerA).TweakInEditor(new byte[] { 9, 9, 9 });
        session.CommitEdit();

        session.Open(path);

        Assert.Null(session.Layers.Layers.Single().MixParameters.EqHostedState);

        // The saved file itself must be clean too, not just the in-memory session.
        string path2 = TempProjectPath();
        session.Save(path2);
        var reloaded = new ProjectSession(_mixEngine);
        reloaded.Open(path2);
        Assert.Null(reloaded.Layers.Layers.Single().MixParameters.EqHostedState);
    }

    [Fact]
    public void New_ThenAddLayer_GetsAFreshPluginInstance()
    {
        var session = new ProjectSession(_mixEngine);
        var layerA = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        var pluginA = OpenEqEditorLiveInstance(layerA);
        pluginA.TweakInEditor(new byte[] { 7 });

        session.New();
        var layerB = session.Layers.Add(LayerKind.UploadedAudioOnly, "b.wav");
        var pluginB = OpenEqEditorLiveInstance(layerB);

        Assert.NotSame(pluginA, pluginB);
        Assert.True(pluginA.Disposed);
        Assert.Empty(pluginB.State);
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

    // ----- Save affordances (Ctrl+S / Save vs Save As / dirty marker / discard prompts) -----

    [Fact]
    public void NewSession_IsCleanAndUntitled()
    {
        var session = new ProjectSession(_mixEngine);

        Assert.False(session.IsDirty);
        Assert.Null(session.CurrentFilePath);
        Assert.Equal("Untitled", session.DisplayName);
    }

    [Fact]
    public void CommitEdit_MarksDirty_SaveClearsItAndRemembersThePath()
    {
        var session = new ProjectSession(_mixEngine);
        session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        session.CommitEdit();

        Assert.True(session.IsDirty);

        string path = Path.Combine(Path.GetTempPath(), $"My Song {Guid.NewGuid()}.acapella.json");
        _tempFiles.Add(path);
        session.Save(path);

        Assert.False(session.IsDirty);
        Assert.Equal(path, session.CurrentFilePath);
        Assert.Equal(Path.GetFileName(path)[..^ProjectSession.ProjectFileSuffix.Length], session.DisplayName);

        session.CommitEdit();

        Assert.True(session.IsDirty);
        Assert.Equal(path, session.CurrentFilePath);
    }

    [Fact]
    public void Open_RemembersThePath_AndEndsClean()
    {
        var saved = new ProjectSession(_mixEngine);
        saved.Layers.Add(LayerKind.UploadedVideo, "clip.mp4");
        saved.MetronomeBpm = 96;
        saved.MasterVolumeDb = -2f;
        string path = TempProjectPath();
        saved.Save(path);

        var session = new ProjectSession(_mixEngine);
        session.Layers.Add(LayerKind.UploadedAudioOnly, "other.wav");
        session.CommitEdit();

        Assert.True(session.IsDirty);

        session.Open(path);

        Assert.False(session.IsDirty);
        Assert.Equal(path, session.CurrentFilePath);
    }

    [Fact]
    public void New_ForgetsThePath_AndEndsClean()
    {
        var session = new ProjectSession(_mixEngine);
        string path = TempProjectPath();
        session.Save(path);
        session.MasterVolumeDb = -4f;

        Assert.True(session.IsDirty);

        session.New();

        Assert.Null(session.CurrentFilePath);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void UndoAndRedo_MarkDirty_ButANoOpUndoDoesNot()
    {
        var session = new ProjectSession(_mixEngine);

        Assert.False(session.Undo());
        Assert.False(session.IsDirty);

        session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        session.CommitEdit();
        string path = TempProjectPath();
        session.Save(path);

        Assert.False(session.IsDirty);

        Assert.True(session.Undo());
        Assert.True(session.IsDirty);

        session.Save(path);
        Assert.False(session.IsDirty);

        Assert.True(session.Redo());
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void BpmAndMasterVolume_MarkDirtyOnlyOnARealChange()
    {
        var session = new ProjectSession(_mixEngine);

        session.MetronomeBpm = session.MetronomeBpm;
        session.MasterVolumeDb = session.MasterVolumeDb;
        Assert.False(session.IsDirty);

        session.MetronomeBpm = 100;
        Assert.True(session.IsDirty);

        string path = TempProjectPath();
        session.Save(path);
        Assert.False(session.IsDirty);

        session.MasterVolumeDb = -1f;
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void FailedSave_LeavesPathAndDirtyUntouched()
    {
        string blocker = Path.Combine(Path.GetTempPath(), $"acapella-blocker-{Guid.NewGuid()}");
        File.WriteAllText(blocker, "not a directory");
        _tempFiles.Add(blocker);
        string badPath = Path.Combine(blocker, "x.acapella.json");

        var session = new ProjectSession(_mixEngine);
        string goodPath = TempProjectPath();
        session.Save(goodPath);
        session.MasterVolumeDb = -1f;

        Assert.True(session.IsDirty);

        Assert.ThrowsAny<IOException>(() => session.Save(badPath));

        Assert.True(session.IsDirty);
        Assert.Equal(goodPath, session.CurrentFilePath);
    }

    [Fact]
    public void FailedOpen_LeavesPathAndDirtyUntouched()
    {
        var session = new ProjectSession(_mixEngine);
        string goodPath = TempProjectPath();
        session.Save(goodPath);
        session.MasterVolumeDb = -1f;

        Assert.True(session.IsDirty);

        string missingPath = Path.Combine(Path.GetTempPath(), $"acapella-missing-{Guid.NewGuid()}.acapella.json");
        Assert.Throws<FileNotFoundException>(() => session.Open(missingPath));

        Assert.True(session.IsDirty);
        Assert.Equal(goodPath, session.CurrentFilePath);
    }

    [Fact]
    public void SaveStateChanged_FiresOnlyOnTransitions()
    {
        var session = new ProjectSession(_mixEngine);
        session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");

        int fireCount = 0;
        session.SaveStateChanged += () => fireCount++;

        session.CommitEdit();
        Assert.Equal(1, fireCount);

        session.CommitEdit();
        Assert.Equal(1, fireCount);

        string path = TempProjectPath();
        session.Save(path);
        Assert.Equal(2, fireCount);

        session.Save(path);
        Assert.Equal(2, fireCount);
    }
}
