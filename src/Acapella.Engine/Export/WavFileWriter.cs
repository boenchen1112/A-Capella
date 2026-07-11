using NAudio.Wave;

namespace Acapella.Engine.Export;

/// <summary>Writes interleaved stereo float samples to a standard PCM WAV file (used as the
/// intermediate audio hand-off into the final mux -- ffmpeg reads this as a second input
/// alongside the piped raw video).</summary>
public static class WavFileWriter
{
    public static void WriteStereoFloat(string path, float[] interleavedSamples, int sampleRate)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        using var writer = new NAudio.Wave.WaveFileWriter(path, format);
        writer.WriteSamples(interleavedSamples, 0, interleavedSamples.Length);
    }
}
