using A_Capella.Engine.Host;

namespace A_Capella.Engine.Tests.Host;

/// <summary>Reading back the notes Melodyne detects in an analysed recording (used to line tracks up).
/// Skips when Melodyne with ARA is not installed.</summary>
[Collection("JuceHosting")]
public class AraNoteReadingTests
{
    private const int SampleRate = 44100;

    // (start, end, frequency): three sung-like notes with soft attacks and harmonics, with short
    // gaps so the note boundaries are unambiguous.
    private static readonly (double Start, double End, double Hz)[] Melody =
    {
        (0.50, 1.30, 220.0),
        (1.50, 2.30, 261.6),
        (2.60, 3.50, 196.0),
    };

    private static float[] SungLine(double seconds)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        foreach (var (start, end, hz) in Melody)
        {
            int from = (int)(start * SampleRate), to = (int)(end * SampleRate);
            for (int i = from; i < to && i < samples.Length; i++)
            {
                double t = (double)i / SampleRate;
                double attack = Math.Min(1.0, (t - start) / 0.06);
                double release = Math.Min(1.0, (end - t) / 0.08);
                double env = Math.Max(0, Math.Min(attack, release));
                double wave = Math.Sin(2 * Math.PI * hz * t) + 0.5 * Math.Sin(2 * Math.PI * 2 * hz * t) + 0.25 * Math.Sin(2 * Math.PI * 3 * hz * t);
                samples[i] = (float)(0.3 * env * wave);
            }
        }
        return samples;
    }

    [Fact]
    public void ReadNotes_FindsTheStartOfEachSungNote()
    {
        string melodynePath = HostedPluginCatalog.KnownPluginPaths["Melodyne"];
        if (!HostedPluginInstance.TryScanAraCapability(melodynePath, out var capability) || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine($"SKIPPED: Melodyne at '{melodynePath}' is not ARA-capable on this machine (or not found).");
            return;
        }

        using var session = AraHostSession.Create(melodynePath, SampleRate, 512);
        var samples = SungLine(4.0);
        var source = session.RegisterAudioSource(new[] { samples }, samples.Length, SampleRate, "a-capella-test-notes");
        session.AddPlaybackRegion(source);
        Assert.True(session.WaitForAnalysisComplete(source, timeoutMs: 60_000), "analysis did not complete");

        var notes = session.ReadNotes(source);
        Console.WriteLine(notes is null ? "notes: unavailable" : string.Join("\n", notes.Select(n => $"note start={n.StartSeconds:F3}s dur={n.DurationSeconds:F3}s hz={n.FrequencyHz:F1} vol={n.Volume:F2}")));

        Assert.NotNull(notes);
        Assert.Equal(Melody.Length, notes!.Count);
        for (int i = 0; i < Melody.Length; i++)
            Assert.InRange(notes[i].StartSeconds, Melody[i].Start - 0.1, Melody[i].Start + 0.1);
    }
}
