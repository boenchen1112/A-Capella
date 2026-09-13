using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Tests.Host;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

/// <summary>Hosted-stage chain behavior (latency compensation, reverb tail, reset-before-reuse,
/// shared instances, live state sync) against fake plugins, so it runs on any machine.
/// HostedFxChainTests covers the same ground against the real FabFilter installs.</summary>
public class HostedChainTests
{
    private const int SampleRate = 44100;

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

    private static float[] Impulse(int length, int index)
    {
        var samples = new float[length];
        samples[index] = 1f;
        return samples;
    }

    private static int PeakFrame(float[] stereo) =>
        Enumerable.Range(0, stereo.Length / 2).MaxBy(frame => Math.Abs(stereo[frame * 2]));

    [Fact]
    public void HostedStageLatencies_AreSummedAndTrimmed_SoTheImpulseStaysAtItsSourceIndex()
    {
        var fakes = new FakeHostedPluginFactory()
            .With(MixEngine.EqPluginLabel, latencySamples: 128)
            .With(MixEngine.LimiterPluginLabel, latencySamples: 256);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        var parameters = new LayerMixParameters { EqEnabled = true, LimiterEnabled = true };
        var chain = engine.BuildLayerChain(new MixLayerInput(0, Impulse(SampleRate / 4, 4410), SampleRate, parameters), anySolo: false, SampleRate);

        Assert.Equal(4410, PeakFrame(ReadAll(chain, SampleRate)));
    }

    [Fact]
    public void LatencyOnOneLayerOnly_DoesNotOffsetItAgainstANativeLayer_InTheSummedMix()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.LimiterPluginLabel, latencySamples: 512);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        var mix = engine.BuildMix(new[]
        {
            new MixLayerInput(0, Impulse(SampleRate / 4, 4410), SampleRate, new LayerMixParameters { LimiterEnabled = true }),
            new MixLayerInput(1, Impulse(SampleRate / 4, 4410), SampleRate, new LayerMixParameters()),
        }, SampleRate);

        var output = ReadAll(mix, SampleRate);
        Assert.Equal(4410, PeakFrame(output));
        Assert.True(Math.Abs(output[4411 * 2]) < 1e-6f, "a delayed copy of the hosted layer's impulse leaked past its source index");
    }

    [Fact]
    public void ReverbTail_ExtendsChainOutputAndReportedTail_ByThePluginsTail()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.ReverbPluginLabel, tailSeconds: 0.25);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        var parameters = new LayerMixParameters { ReverbEnabled = true };
        int sourceFrames = SampleRate / 2;
        var chain = engine.BuildLayerChain(new MixLayerInput(0, new float[sourceFrames], SampleRate, parameters), anySolo: false, SampleRate);

        Assert.Equal(0.25, engine.GetReverbTailSeconds(0, parameters, SampleRate), 6);
        Assert.Equal(sourceFrames + SampleRate / 4, ReadAll(chain, SampleRate * 4).Length / 2);
    }

    [Fact]
    public void ReverbDisabled_ReportsNoTail_EvenWhenTheReverbPluginIsAvailable()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.ReverbPluginLabel, tailSeconds: 3);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        Assert.Equal(0, engine.GetReverbTailSeconds(0, new LayerMixParameters(), SampleRate));
        Assert.Empty(fakes.Created);
    }

    [Fact]
    public void EachChainBuild_ReusesTheCachedInstance_AndResetsItFirst()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.LimiterPluginLabel, latencySamples: 64);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);

        var parameters = new LayerMixParameters { LimiterEnabled = true };
        var input = new MixLayerInput(0, Impulse(2048, 100), SampleRate, parameters);
        var first = ReadAll(engine.BuildLayerChain(input, anySolo: false, SampleRate), 8192);
        var second = ReadAll(engine.BuildLayerChain(input, anySolo: false, SampleRate), 8192);

        var plugin = Assert.Single(fakes.Created);
        Assert.Equal(2, plugin.ResetCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MixEnginesSharingOneService_GetTheSameLiveInstance()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.EqPluginLabel);
        using var service = fakes.CreateService();
        using var chainEngine = new MixEngine(service);
        using var uiEngine = new MixEngine(service);

        ReadAll(chainEngine.BuildLayerChain(new MixLayerInput(0, new float[1000], SampleRate, new LayerMixParameters { EqEnabled = true }), anySolo: false, SampleRate), 512);

        Assert.Same(
            chainEngine.GetOrCreateHostedInstance(0, MixEngine.EqStage, MixEngine.EqPluginLabel, null, SampleRate),
            uiEngine.GetOrCreateHostedInstance(0, MixEngine.EqStage, MixEngine.EqPluginLabel, null, SampleRate));
    }

    [Fact]
    public void LiveEditorTweak_IsPulledIntoParameters_AndSavedStateIsPushedBackIntoLiveInstance()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.EqPluginLabel);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);
        var parameters = new LayerMixParameters { EqEnabled = true };
        ReadAll(engine.BuildLayerChain(new MixLayerInput(0, new float[1000], SampleRate, parameters), anySolo: false, SampleRate), 512);
        var plugin = Assert.Single(fakes.Created);

        plugin.TweakInEditor(new byte[] { 1, 2, 3 });
        engine.SyncLiveStateIntoParameters(0, parameters);
        Assert.Equal(new byte[] { 1, 2, 3 }, parameters.EqHostedState);

        parameters.EqHostedState = new byte[] { 9, 9 };
        engine.PushSavedStateIntoLiveInstances(0, parameters);
        Assert.Equal(new byte[] { 9, 9 }, plugin.State);
    }

    [Fact]
    public void ReleasedInstance_IsRecreatedFromSavedState_OnTheNextChainBuild()
    {
        var fakes = new FakeHostedPluginFactory().With(MixEngine.LimiterPluginLabel);
        using var service = fakes.CreateService();
        using var engine = new MixEngine(service);
        var parameters = new LayerMixParameters { LimiterEnabled = true, LimiterHostedState = new byte[] { 7 } };
        var input = new MixLayerInput(0, new float[1000], SampleRate, parameters);

        engine.BuildLayerChain(input, anySolo: false, SampleRate);
        service.Release(0, MixEngine.LimiterStage);
        engine.BuildLayerChain(input, anySolo: false, SampleRate);

        Assert.Equal(2, fakes.Created.Count);
        Assert.True(fakes.Created[0].Disposed);
        Assert.Equal(new byte[] { 7 }, fakes.Created[1].State);
    }
}
