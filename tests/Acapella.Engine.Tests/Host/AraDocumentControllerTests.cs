using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>
/// v7 2A task 38 acceptance test: creates a real ARA hosting session against the confirmed
/// ARA-capable Melodyne install (see AraCapabilityProbeTests), registers a synthetic mono audio
/// source, and confirms the whole create -> register -> release -> dispose sequence runs without
/// crashing. This does not exercise analysis or rendering (task 39's job, once the C# pitch-stage
/// wrapper exists) -- task 38's scope is only the Document Controller and audio source
/// registration bridge itself.
///
/// Synchronous on the test's own thread, per every other hosted-plugin test in this project
/// (HostedFxChainTests.cs etc.) -- a bare async command thread touching the native bridge is the
/// documented heap-corruption hazard (see primer.md's gotchas).
/// </summary>
[Collection("JuceHosting")]
public class AraDocumentControllerTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void CreateSession_RegisterAudioSource_ReleaseAndDispose_DoesNotCrash()
    {
        string melodynePath = HostedPluginCatalog.KnownPluginPaths["Melodyne"];
        if (!HostedPluginInstance.TryScanAraCapability(melodynePath, out var capability) || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine($"SKIPPED: Melodyne at '{melodynePath}' is not ARA-capable on this machine (or not found).");
            return;
        }

        using var session = AraHostSession.Create(melodynePath, SampleRate, 512);

        // Synthetic 1-second 440Hz mono source.
        var samples = new float[SampleRate];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));

        var audioSource = session.RegisterAudioSource(new[] { samples }, samples.Length, SampleRate, "acapella-test-layer-0");
        Assert.NotEqual(IntPtr.Zero, audioSource);

        session.ReleaseAudioSource(audioSource);
    }
}
