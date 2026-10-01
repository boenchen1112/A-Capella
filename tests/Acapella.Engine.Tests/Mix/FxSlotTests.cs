using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Tests.Host;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

public class FxSlotTests
{
    private const int SampleRate = 44100;

    public static IEnumerable<object[]> SlotStages() => FxSlots.All.Select(s => new object[] { s.Stage });

    private static FxSlot Slot(string stage) => FxSlots.All.Single(s => s.Stage == stage);

    private static LayerMixParameters EnabledOnly(FxSlot slot)
    {
        var p = new LayerMixParameters();
        if (slot == FxSlots.NoiseGate) p.NoiseGateEnabled = true;
        else if (slot == FxSlots.Compressor) p.CompressorEnabled = true;
        else if (slot == FxSlots.Eq) p.EqEnabled = true;
        else if (slot == FxSlots.Reverb) p.ReverbEnabled = true;
        else if (slot == FxSlots.Limiter) p.LimiterEnabled = true;
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
        Assert.Equal(new[] { "NoiseGate", "Compressor", "Eq", "Reverb", "Limiter" }, FxSlots.All.Select(s => s.Stage));
        Assert.Equal(FxSlots.All.Count, FxSlots.All.Select(s => s.PluginLabel).Distinct().Count());
        Assert.All(FxSlots.All, s => Assert.True(HostedPluginCatalog.KnownPluginPaths.ContainsKey(s.PluginLabel), s.PluginLabel));
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void EnabledSlot_WithItsPluginInstalled_ProcessesThroughTheSharedLiveInstance(string stage)
    {
        var slot = Slot(stage);
        var fakes = new FakeHostedPluginFactory().With(slot.PluginLabel, latencySamples: 32);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        ReadAll(engine.BuildLayerChain(new MixLayerInput(0, Sine(2048), SampleRate, EnabledOnly(slot)), anySolo: false, SampleRate), 1024);

        var plugin = Assert.Single(fakes.Created);
        Assert.Equal(slot.PluginLabel, plugin.Label);
        Assert.Equal(1, plugin.ResetCount);
        Assert.Same(plugin, service.TryGetLiveInstance(0, slot.Stage));
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void DisabledSlot_CreatesNoInstance_AndLeavesTheChainUntouched(string stage)
    {
        var slot = Slot(stage);
        var fakes = new FakeHostedPluginFactory().With(slot.PluginLabel, latencySamples: 32);
        using var service = fakes.CreateService();
        using var hostedEngine = new MixEngine(service);
        using var nativeEngine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var samples = Sine(2048);
        var withPluginInstalled = ReadAll(hostedEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 8192);
        var withoutPlugin = ReadAll(nativeEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 8192);

        Assert.Empty(fakes.Created);
        Assert.Equal(withoutPlugin, withPluginInstalled);
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void EnabledSlot_WithoutItsPlugin_UsesNativeFallback_OrPassesThroughWhenItHasNone(string stage)
    {
        var slot = Slot(stage);
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var samples = Sine(4096);
        var enabled = ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, EnabledOnly(slot)), anySolo: false, SampleRate), 16384);
        var disabled = ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate), 16384);

        Assert.Equal(disabled.Length, enabled.Length);
        if (!slot.HasNativeFallback)
            Assert.Equal(disabled, enabled);
    }

    [Theory]
    [MemberData(nameof(SlotStages))]
    public void HostedState_RoundTripsThroughItsOwnParameterField_Only(string stage)
    {
        var slot = Slot(stage);
        var p = new LayerMixParameters();

        slot.SetHostedState(p, new byte[] { 4, 2 });

        Assert.Equal(new byte[] { 4, 2 }, slot.GetHostedState(p));
        Assert.All(FxSlots.All.Where(other => other != slot), other => Assert.Null(other.GetHostedState(p)));
    }
}
