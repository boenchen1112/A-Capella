using System.Text.Json;
using Acapella.Engine.Persistence;

namespace Acapella.Engine.Tests.Persistence;

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
}
