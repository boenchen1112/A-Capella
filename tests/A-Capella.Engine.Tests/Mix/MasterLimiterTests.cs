using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Tests.Host;
using NAudio.Wave;

namespace A_Capella.Engine.Tests.Mix;

/// <summary>The one limiter on the whole mix, and how a layer's effect choices reach the frozen render.</summary>
public class MasterLimiterTests
{
    private const int SampleRate = 44100;
    private static readonly PresetRef Limiter = new("FabFilter Pro-L 2", "_Basic", "Safe");

    private static float[] Sine(int length, float amplitude) =>
        Enumerable.Range(0, length).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / SampleRate))).ToArray();

    private static float[] ReadAll(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        int read = 0;
        while (read < count)
        {
            int n = provider.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }

    private static MixLayerInput[] NineHotLayers() =>
        Enumerable.Range(0, 9).Select(i => new MixLayerInput(i, Sine(SampleRate, 0.9f), SampleRate, new LayerMixParameters())).ToArray();

    [Fact]
    public void NineHotLayers_NeverPassTheMasterCeiling_WhenTheLimiterPluginIsMissing()
    {
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var output = ReadAll(engine.BuildMix(NineHotLayers(), SampleRate, masterLimiter: Limiter), SampleRate);

        Assert.True(output.Length > 0);
        Assert.True(output.Max(Math.Abs) <= 0.97f, $"peak {output.Max(Math.Abs)}");
    }

    [Fact]
    public void NineHotLayers_AreStillGuarded_WithTheLimiterSetToOff()
    {
        // The safety ceiling is not an effect: with the limiter set to Off the mix is still guarded.
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var output = ReadAll(engine.BuildMix(NineHotLayers(), SampleRate, masterLimiter: null), SampleRate);

        Assert.True(output.Max(Math.Abs) <= 0.97f);
    }

    [Fact]
    public void MasterLimiter_RunsThroughOneSharedInstanceCarryingTheChosenPreset()
    {
        var fakes = new FakeHostedPluginFactory().With("FabFilter Pro-L 2", latencySamples: 16);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        ReadAll(engine.BuildMix(NineHotLayers(), SampleRate, masterLimiter: Limiter), 4096);
        ReadAll(engine.BuildMix(NineHotLayers(), SampleRate, masterLimiter: Limiter), 4096);

        var plugin = Assert.Single(fakes.Created);
        Assert.Equal("Safe", System.Text.Encoding.UTF8.GetString(plugin.GetState()));
    }

    [Fact]
    public void LimiterOff_CreatesNoPluginInstance()
    {
        var fakes = new FakeHostedPluginFactory().With("FabFilter Pro-L 2");
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        ReadAll(engine.BuildMix(NineHotLayers(), SampleRate, masterLimiter: null), 4096);

        Assert.Empty(fakes.Created);
    }

    [Fact]
    public void AutoTune_IsReservedForTheMac_AndHasNoWindowsPlugin()
    {
        Assert.False(HostedPluginCatalog.KnownPluginPaths.ContainsKey(HostedPluginCatalog.AutoTunePluginLabel));
        Assert.Equal(OperatingSystem.IsMacOS(), HostedPluginCatalog.AutoTuneSupported);
    }

    [Fact]
    public void ChangingAnEffectPreset_ChangesTheFrozenRender_NotJustTheLiveChain()
    {
        var fakes = new FakeHostedPluginFactory().With("FabFilter Pro-C 3");
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);
        var samples = Sine(SampleRate, 0.3f);
        var parameters = new LayerMixParameters { Frozen = true };

        ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters, "src"), false, SampleRate), 2048);
        Assert.Empty(fakes.Created);   // nothing chosen: nothing hosted

        parameters.CompressorPreset = new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal");
        ReadAll(engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters, "src"), false, SampleRate), 2048);

        Assert.Single(fakes.Created);   // the preset change re-rendered the frozen buffer through the plugin
    }
}
