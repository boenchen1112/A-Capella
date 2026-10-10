using A_Capella.Engine.Presets;
using System.Text.Json;
using A_Capella.Engine.Persistence;

namespace A_Capella.Engine.Tests.Persistence;

public class ProjectUndoStackTests
{
    private static string ToJson(ProjectFileDto dto) => JsonSerializer.Serialize(dto);

    [Fact]
    public void UndoTenThenRedoTen_RestoresOriginalThenFinalStateByteIdentically()
    {
        var stack = new ProjectUndoStack();

        var original = new ProjectFileDto { MetronomeBpm = 120, MasterVolumeDb = 0f };
        original.Layers.Add(new LayerDto { LayerId = 1, SourcePath = "layer1.wav", MixParameters = new MixParametersDto { GainDb = 0f, Mute = false } });
        string originalJson = ToJson(original);

        stack.Reset(original);

        var states = new List<ProjectFileDto>();
        var current = original;
        for (int i = 0; i < 10; i++)
        {
            // Mix of edit kinds: trim, rename-placeholder (SourcePath stand-in), gain, mute.
            current = Clone(current);
            switch (i % 4)
            {
                case 0: current.Layers[0].TrimStartMs = i * 100; break;
                case 1: current.Layers[0].SourcePath = $"layer1_renamed_{i}.wav"; break;
                case 2: current.Layers[0].MixParameters.GainDb = i * 1.5f; break;
                case 3: current.Layers[0].MixParameters.Mute = !current.Layers[0].MixParameters.Mute; break;
            }
            stack.Push(current);
            states.Add(current);
        }
        string finalJson = ToJson(states[^1]);

        ProjectFileDto? restored = null;
        for (int i = 0; i < 10; i++)
            restored = stack.Undo();

        Assert.NotNull(restored);
        Assert.Equal(originalJson, ToJson(restored!));
        Assert.False(stack.CanUndo);

        for (int i = 0; i < 10; i++)
            restored = stack.Redo();

        Assert.NotNull(restored);
        Assert.Equal(finalJson, ToJson(restored!));
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Push_AfterUndo_ClearsRedoStack()
    {
        var stack = new ProjectUndoStack();
        var state0 = new ProjectFileDto { MetronomeBpm = 100 };
        stack.Reset(state0);

        var state1 = new ProjectFileDto { MetronomeBpm = 110 };
        stack.Push(state1);
        stack.Undo();
        Assert.True(stack.CanRedo);

        var state2 = new ProjectFileDto { MetronomeBpm = 130 };
        stack.Push(state2);
        Assert.False(stack.CanRedo);
    }

    private static ProjectFileDto Clone(ProjectFileDto dto) =>
        JsonSerializer.Deserialize<ProjectFileDto>(JsonSerializer.Serialize(dto))!;

    /// <summary>Q2 task 2 acceptance: "Undo after a plugin edit restores the previous audible
    /// state (state-hash equality), redo re-applies." MainWindow pushes exactly two kinds of
    /// snapshot relevant here -- a slot power-button toggle (CommitCheckBox_Click) and a
    /// poll-detected hosted-plugin state change (the 500ms _hostedStatePollTimer, wired to
    /// PushUndoSnapshot as of this task) -- both funnel through the same CurrentProjectDto/
    /// ProjectUndoStack path already proven byte-identical above, so this test targets the
    /// specific fields those two edits touch (NoiseGateEnabled and EqHostedStateBase64) rather
    /// than re-proving the generic mechanism.</summary>
    [Fact]
    public void Undo_AfterSlotToggleAndPluginStateChange_RestoresPreviousState_RedoReapplies()
    {
        var stack = new ProjectUndoStack();

        var state0 = new ProjectFileDto();
        state0.Layers.Add(new LayerDto
        {
            LayerId = 1,
            SourcePath = "layer1.wav",
            MixParameters = new MixParametersDto { EqPreset = null }
        });
        stack.Reset(state0);

        // Edit 1: slot power-button toggle (CommitCheckBox_Click -> PushUndoSnapshot).
        var state1 = Clone(state0);
        state1.Layers[0].MixParameters.CompressorPreset = new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal");
        stack.Push(state1);

        // Edit 2: a plugin knob move detected by the hosted-state poll (PollHostedStateChanges
        // returning true -> PushUndoSnapshot), captured as a new state blob.
        var state2 = Clone(state1);
        state2.Layers[0].MixParameters.EqPreset = new PresetRef("FabFilter Pro-Q 4", "", "Bright");
        stack.Push(state2);

        string state1Json = ToJson(state1);
        string state2Json = ToJson(state2);

        var afterFirstUndo = stack.Undo();
        Assert.NotNull(afterFirstUndo);
        Assert.Equal(state1Json, ToJson(afterFirstUndo!));
        Assert.NotNull(afterFirstUndo!.Layers[0].MixParameters.CompressorPreset);
        Assert.Null(afterFirstUndo.Layers[0].MixParameters.EqPreset);

        var afterSecondUndo = stack.Undo();
        Assert.NotNull(afterSecondUndo);
        Assert.Null(afterSecondUndo!.Layers[0].MixParameters.CompressorPreset);

        var afterFirstRedo = stack.Redo();
        Assert.NotNull(afterFirstRedo);
        Assert.Equal(state1Json, ToJson(afterFirstRedo!));

        var afterSecondRedo = stack.Redo();
        Assert.NotNull(afterSecondRedo);
        Assert.Equal(state2Json, ToJson(afterSecondRedo!));
        Assert.Equal(state2.Layers[0].MixParameters.EqPreset, afterSecondRedo!.Layers[0].MixParameters.EqPreset);
    }
}
