using Acapella.App.ViewModels;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Xunit;

namespace Acapella.App.Tests;

[CollectionDefinition("SharedHostedService", DisableParallelization = true)]
public sealed class SharedHostedServiceCollection { }

/// <summary>Bug audit #9: Undo/Redo rebuilds every row (MainWindow.RestoreTracksFromLayers);
/// CarryEditorTracking keeps a still-open FabFilter editor polled across that rebuild.</summary>
[Collection("SharedHostedService")]
public class LayerRowEditorTrackingTests
{
    private sealed class StatefulFakePlugin : IHostedPlugin
    {
        public byte[] State = Array.Empty<byte>();
        public int LatencySamples => 0;
        public double TailSeconds => 0;
        public void ProcessBlock(float[] inL, float[] inR, float[] outL, float[] outR, int numSamples) { }
        public void Reset() { }
        public byte[] GetState() => (byte[])State.Clone();
        public void SetState(byte[] data) { if (data.Length > 0) State = (byte[])data.Clone(); }
        public int ParameterCount => 0;
        public string GetParameterName(int index) => "";
        public float GetParameterValue(int index) => 0f;
        public void SetParameterValue(int index, float value) { }
        public bool EditorOpen { get; private set; }
        public bool? EditorVisible { get; private set; }
        public bool ShowEditorWindow(string title) { EditorOpen = true; EditorVisible = true; return true; }
        public void CloseEditorWindow() { EditorOpen = false; EditorVisible = null; }
        public void SetEditorVisible(bool visible) { if (EditorOpen) EditorVisible = visible; }
        public void Dispose() { }
    }

    private sealed class FakeFactory : IHostedPluginFactory
    {
        public IHostedPlugin Create(string pluginLabel, double sampleRate, int maxBlockSize) => new StatefulFakePlugin();
    }

    private sealed class EverythingAvailable : IHostedPluginAvailability
    {
        public bool IsAvailable(string pluginLabel) => true;
    }

    private static LayerModel Layer(int layerId, int cellIndex) =>
        new() { LayerId = layerId, Kind = LayerKind.UploadedVideo, SourcePath = $"l{layerId}.mp4", CellIndex = cellIndex };

    /// <summary>Runs body with a fresh fake-backed SharedHostedService, restoring the previous one after.</summary>
    private static void WithService(Action<HostedPluginService> body)
    {
        var saved = LayerRowViewModel.SharedHostedService;
        var service = new HostedPluginService(new EverythingAvailable(), dispatcher: null, factory: new FakeFactory());
        LayerRowViewModel.SharedHostedService = service;
        try { body(service); }
        finally { LayerRowViewModel.SharedHostedService = saved; service.Dispose(); }
    }

    private static StatefulFakePlugin Live(HostedPluginService s, int layerId) =>
        (StatefulFakePlugin)s.TryGetLiveInstance(layerId, FxSlots.Eq.Stage)!;

    /// <summary>Open Eq on a row and settle the open-time poll (OpenHostedEditor seeds from Params,
    /// which is null for these never-hosted layers, so its first poll always reports -- existing
    /// behaviour, not under test here).</summary>
    private static LayerRowViewModel RowWithOpenEq(int slotNumber, LayerModel layer)
    {
        var row = new LayerRowViewModel(slotNumber) { Layer = layer };
        row.OpenHostedEditor(FxSlots.Eq);
        row.PollEditorChanges();
        return row;
    }

    [StaFact]
    public void RebuiltRow_WithoutCarry_DoesNotSeeTweaksInAStillOpenEditor() => WithService(s =>
    {
        RowWithOpenEq(1, Layer(0, 0));
        var rebuilt = new LayerRowViewModel(1) { Layer = Layer(0, 0) };   // what RestoreTracksFromLayers does

        Live(s, 0).State = new byte[] { 7 };                               // tweak in the still-open editor

        Assert.False(rebuilt.PollEditorChanges());                        // characterizes the bug
    });

    [StaFact]
    public void CarriedTracking_SeesTheNextTweakInTheStillOpenEditor() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        var rebuilt = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        Live(s, 0).State = new byte[] { 7 };

        Assert.True(rebuilt.PollEditorChanges());
        Assert.Equal(new byte[] { 7 }, rebuilt.Layer!.MixParameters.EqHostedState);
        Assert.False(rebuilt.PollEditorChanges());                        // baseline advanced: no repeat
    });

    [StaFact]
    public void CarriedTracking_DoesNotReportAChange_RightAfterTheUndo() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        Live(s, 0).State = new byte[] { 1 };
        Assert.True(old.PollEditorChanges());                             // old baseline = {1}

        // Undo: restored layer carries {0}; ProjectSession.Restore pushes it into the live instance.
        var restored = Layer(0, 0);
        restored.MixParameters.EqHostedState = new byte[] { 0 };
        s.PushState(Live(s, 0), new byte[] { 0 });
        var rebuilt = new LayerRowViewModel(1) { Layer = restored };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        Assert.False(rebuilt.PollEditorChanges());   // subtlety 1: a copied {1} baseline would push and kill redo
    });

    [StaFact]
    public void CarriedTracking_WhenRestoredStateIsNull_DoesNotReportAChange() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        Live(s, 0).State = new byte[] { 1 };
        old.PollEditorChanges();

        var restored = Layer(0, 0);                  // snapshot predates the instance: EqHostedState == null,
        Assert.Null(restored.MixParameters.EqHostedState);   // so Restore pushes nothing (MixEngine.cs:119)
        var rebuilt = new LayerRowViewModel(1) { Layer = restored };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        Assert.False(rebuilt.PollEditorChanges());   // subtlety 2: a Params-seeded (null) baseline would push
    });

    [StaFact]
    public void CarriedTracking_SkipsSlotsWithNoLiveInstance_AfterReleaseAll() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        s.ReleaseAll();                              // File > Open / New
        var rebuilt = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        // First chain build of the new project creates a fresh instance with its own state.
        var fresh = (StatefulFakePlugin)s.GetOrCreateInstance(0, FxSlots.Eq.Stage, FxSlots.Eq.PluginLabel, null);
        fresh.State = new byte[] { 9 };

        Assert.False(rebuilt.PollEditorChanges());   // subtlety 4 / bug audit #5 §4: no spurious step
    });

    [StaFact]
    public void CarriedTracking_MatchesByLayerId_NotBySlot() => WithService(s =>
    {
        var oldA = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        var oldB = RowWithOpenEq(2, Layer(1, 1));    // Eq opened on layer 1 only
        s.GetOrCreateInstance(0, FxSlots.Eq.Stage, FxSlots.Eq.PluginLabel, null);   // layer 0 has a live, unopened Eq

        // After the restore, layer 1 sits in cell 0 and layer 0 in cell 1.
        var newSlot1 = new LayerRowViewModel(1) { Layer = Layer(1, 0) };
        var newSlot2 = new LayerRowViewModel(2) { Layer = Layer(0, 1) };
        LayerRowViewModel.CarryEditorTracking(new[] { oldA, oldB }, new[] { newSlot1, newSlot2 });

        Live(s, 1).State = new byte[] { 5 };
        Live(s, 0).State = new byte[] { 6 };

        Assert.True(newSlot1.PollEditorChanges());   // layer 1's open editor is tracked
        Assert.False(newSlot2.PollEditorChanges());  // layer 0's editor was never opened
    });

    /// <summary>Closing/removing a layer (e.g. an Undo that drops it) must close that layer's open
    /// hosted-plugin editor window -- it must not leave a Pro-Q window floating for a layer that no
    /// longer exists in the project.</summary>
    [StaFact]
    public void CloseAllOpenedEditors_ClosesTheWindowButLeavesTheLiveInstanceAlone() => WithService(s =>
    {
        var row = RowWithOpenEq(1, Layer(0, 0));
        var instance = Live(s, 0);
        Assert.True(instance.EditorOpen);

        row.CloseAllOpenedEditors();

        Assert.False(instance.EditorOpen);                       // window closed
        Assert.Same(instance, s.TryGetLiveInstance(0, FxSlots.Eq.Stage));   // instance still live
    });

    /// <summary>App minimize/restore hides/shows open editor windows without closing them.</summary>
    [StaFact]
    public void SetOpenedEditorsVisible_HidesAndRestoresWithoutClosing() => WithService(s =>
    {
        var row = RowWithOpenEq(1, Layer(0, 0));
        var instance = Live(s, 0);

        row.SetOpenedEditorsVisible(false);
        Assert.True(instance.EditorOpen);
        Assert.Equal(false, instance.EditorVisible);

        row.SetOpenedEditorsVisible(true);
        Assert.True(instance.EditorOpen);
        Assert.Equal(true, instance.EditorVisible);
    });

    /// <summary>A row with nothing opened is a safe no-op for both operations.</summary>
    [StaFact]
    public void EditorWindowOperations_AreNoOpsWhenNothingWasOpened() => WithService(s =>
    {
        var row = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        row.SetOpenedEditorsVisible(false);
        row.SetOpenedEditorsVisible(true);
        row.CloseAllOpenedEditors();
    });
}
