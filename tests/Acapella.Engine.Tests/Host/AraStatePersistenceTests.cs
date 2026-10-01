using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>
/// v7 2A task 40 acceptance test: the whole-document archive export/import round-trip. Exports a
/// session's analyzed state after registering a source and attaching a playback region, then
/// re-imports it into the same session and confirms the call succeeds without throwing. Proves the
/// bridge functions work end to end -- wiring this into actual project save/load, keyed by
/// (layerId, sourceAudioHash), is the caller's job once MixEngine's pitch-stage integration uses
/// this session type.
/// </summary>
[Collection("JuceHosting")]
public class AraStatePersistenceTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void ExportState_AfterRegisteringAndAnalyzing_ReturnsNonEmptyState_AndImportRoundTrips()
    {
        string melodynePath = HostedPluginCatalog.KnownPluginPaths["Melodyne"];
        if (!HostedPluginInstance.TryScanAraCapability(melodynePath, out var capability) || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine($"SKIPPED: Melodyne at '{melodynePath}' is not ARA-capable on this machine (or not found).");
            return;
        }

        using var session = AraHostSession.Create(melodynePath, SampleRate, 512);

        int numSamples = SampleRate;
        var samples = new float[numSamples];
        for (int i = 0; i < numSamples; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));

        var audioSource = session.RegisterAudioSource(new[] { samples }, numSamples, SampleRate, "acapella-test-persistence");
        session.AddPlaybackRegion(audioSource);

        var state = session.ExportState();
        Assert.NotEmpty(state);

        // Round-trip onto the same session/graph -- restoreObjectsFromArchive matches by
        // persistent ID, and this session's audio source is already registered under the same ID
        // the archive was exported with.
        session.ImportState(state);
    }
}
