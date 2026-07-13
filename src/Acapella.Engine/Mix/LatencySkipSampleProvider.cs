using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>
/// Discards the first skipFrames frames of source's output before passing the rest through
/// untouched. Used once per layer to trim the combined GetLatencySamples of all its hosted FX
/// stages (each of which independently extends the chain's tail via its own silence flush in
/// HostedPluginSampleProvider) so a layer with hosted stages stays sample-aligned with an
/// unprocessed layer -- v6 P3 task 4's mandatory latency compensation.
/// </summary>
public sealed class LatencySkipSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private int _framesToSkip;

    public LatencySkipSampleProvider(ISampleProvider source, int skipFrames)
    {
        _source = source;
        _framesToSkip = Math.Max(0, skipFrames);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = _source.WaveFormat.Channels;

        while (_framesToSkip > 0)
        {
            int skipSamples = Math.Min(count, _framesToSkip * channels);
            int read = _source.Read(buffer, offset, skipSamples);
            if (read == 0)
            {
                _framesToSkip = 0;
                return 0;
            }
            _framesToSkip -= read / channels;
        }

        return _source.Read(buffer, offset, count);
    }
}
