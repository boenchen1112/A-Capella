using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Tests.Host;
using NAudio.Wave;

namespace A_Capella.Engine.Tests.Mix;

public class FxSlotTests
{
    private const int SampleRate = 44100;

    public static IEnumerable<object[]> SlotStages() => FxSlots.All.Select(s => new object[] { s.Stage });

    private static FxSlot Slot(string stage) => FxSlots.All.Single(s => s.Stage == stage);

    private static LayerMixParameters WithPreset(FxSlot slot, string name = "Warm")
    {
        var p = new LayerMixParameters();
        slot.SetPreset(p, new PresetRef(slot.PluginLabel, "Basic", name));
        return p;
    }

    private static float[] ReadAll(ISampleProvider provider, int maxCount)
    {
        var buffer = new float[maxCount];
        int read = 0;
        while (read < maxCount)
        {
            int n = provider.Read(buffer, read, maxCount - read);
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }

    private static float[] Sine(int length) =>
        Enumerable.Range(0, length).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / SampleRate))).ToArray();

    [Fact]
    public void Rack_IsInChainOrder_WithDistinctStagesAndPlugins()
    {
        Assert.Equal(new[] { "Compressor", "Eq", "DeEsser", "Reverb" }, FxSlots.All.Select(s => s.Stage));
        Assert.Equal(FxSlots.All.Count, FxSlots.All.Select(s => s.PluginLabel).Distinct().Count());
        Assert.All(FxSlots.All, s => Assert.True(HostedPluginCatalog.KnownPluginPaths.ContainsKey(s.PluginLabel), s.PluginLabel));
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void SlotWithPreset_ProcessesThroughTheSharedLiveInstance_CarryingThatPreset(string stage)
    {
        var slot = Slot(stage);
        var fakes = new FakeHostedPluginFactory().With(slot.PluginLabel, latencySamples: 32);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        ReadAll(engine.BuildLayerChain(new MixLayerInput(0, Sine(2048), SampleRate, WithPreset(slot)), anySolo: false, SampleRate), 1024);

        var plugin = Assert.Single(fakes.Created);
        Assert.Equal(slot.PluginLabel, plugin.Label);
        Assert.Equal(1, plugin.ResetCount);
        Assert.Equal("Warm", System.Text.Encoding.UTF8.GetString(plugin.GetState()));
        Assert.Same(plugin, service.TryGetLiveInstance(0, slot.Stage));
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void SlotOff_CreatesNoInstance_AndLeavesTheChainUntouched(string stage)
    {
        var slot = Slot(stage);
        var fakes = new FakeHostedPluginFactory().With(slot.PluginLabel, latencySamples: 32);
        using var service = fakes.CreateService();
        using var hostedEngine = new MixEngine(service);
        using var bareEngine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var samples = Sine(2048);
        var withPluginInstalled = ReadAll(hostedEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 8192);
        var withoutPlugin = ReadAll(bareEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 8192);

        Assert.Empty(fakes.Created);
        Assert.Equal(withoutPlugin, withPluginInstalled);
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void SlotWithPreset_WithoutItsPlugin_PassesAudioThrough(string stage)
    {
        var slot = Slot(stage);
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var samples = Sine(4096);
        var chosen = ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, WithPreset(slot)), anySolo: false, SampleRate), 16384);
        var off = ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 16384);

        Assert.Equal(off, chosen);
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void MissingPreset_LeavesTheEffectOff(string stage)
    {
        var slot = Slot(stage);
        var fakes = new FakeHostedPluginFactory().With(slot.PluginLabel, latencySamples: 32);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);
        using var bareEngine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var samples = Sine(2048);
        var missing = ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, WithPreset(slot, "Missing")), anySolo: false, SampleRate), 8192);
        var off = ReadAll(bareEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 8192);

        Assert.Equal(off, missing);
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void Preset_RoundTripsThroughItsOwnParameterField_Only(string stage)
    {
        var slot = Slot(stage);
        var p = new LayerMixParameters();
        var preset = new PresetRef(slot.PluginLabel, "Basic", "Warm");

        slot.SetPreset(p, preset);

        Assert.Equal(preset, slot.GetPreset(p));
        Assert.True(slot.IsEnabled(p));
        Assert.All(FxSlots.All.Where(other => other != slot), other => Assert.Null(other.GetPreset(p)));
    }
}
