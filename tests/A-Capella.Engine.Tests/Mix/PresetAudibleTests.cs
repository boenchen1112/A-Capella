using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using NAudio.Wave;
using Xunit.Abstractions;

namespace A_Capella.Engine.Tests.Mix;

/// <summary>A chosen factory preset must change what the mix sounds like, through the real plugin.</summary>
[Collection("JuceHosting")]
public class PresetAudibleTests
{
    private const int SampleRate = 44100;
    private readonly ITestOutputHelper _out;

    public PresetAudibleTests(ITestOutputHelper output) => _out = output;

    private static float[] Render(MixEngine engine, PresetRef? preset)
    {
        // A quiet-then-loud tone, so a compressor has something to act on.
        var samples = Enumerable.Range(0, SampleRate * 2)
            .Select(i => (float)((i < SampleRate ? 0.1 : 0.8) * Math.Sin(2 * Math.PI * 220 * i / SampleRate))).ToArray();
        var layer = new MixLayerInput(1, samples, SampleRate, new LayerMixParameters { CompressorPreset = preset });
        ISampleProvider provider = engine.BuildMix(new[] { layer }, SampleRate, masterLimiter: null);
        var all = new List<float>();
        var buffer = new float[8192];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.Take(n));
        return all.ToArray();
    }

    [Fact]
    public void ACompressorPreset_ChangesTheRenderedAudio()
    {
        const string label = "FabFilter Pro-C 3";
        var availability = new HostedPluginAvailability();
        var library = FabFilterPresetLibrary.Default;
        var preset = library.GetCategories(label).SelectMany(c => c.Presets)
            .FirstOrDefault(p => p.Name != FabFilterPresetLibrary.DefaultSettingName);
        if (!availability.IsAvailable(label) || preset is null) { _out.WriteLine("SKIPPED: Pro-C 3 or its presets not installed."); return; }

        using var service = new HostedPluginService(availability, InlineHostedPluginDispatcher.Instance);
        using var engine = new MixEngine(service);

        var off = Render(engine, null);
        var on = Render(engine, preset);

        int n = Math.Min(off.Length, on.Length);
        double diff = 0;
        for (int i = 0; i < n; i++) diff += Math.Abs(off[i] - on[i]);
        _out.WriteLine($"preset {preset.Category}/{preset.Name}: mean abs difference {diff / n:F5}");
        Assert.True(diff / n > 1e-3, "the preset did not change the audio");
    }
}
