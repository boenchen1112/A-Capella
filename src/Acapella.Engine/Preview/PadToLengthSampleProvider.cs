using NAudio.Wave;

namespace Acapella.Engine.Preview;

/// <summary>
/// Bug audit #10: passes a finite stream through, then emits silence until at least lengthMs of
/// audio has been produced. PreviewPlaybackEngine's audio clock (PositionTrackingSampleProvider)
/// only advances on samples actually returned, and the project duration comes from container
/// durations (LayerTimeline, audit A5) -- without padding, a layer whose audio is shorter than its
/// container (video-only uploads, video outlasting audio, every untrimmed MKV take) left the clock
/// short of DurationMs forever. Mirrors ExportEngine, which zero-pads its mix buffer to
/// DurationMs. Never truncates a longer source: FrameLoop's own DurationMs break ends playback.
/// </summary>
public sealed class PadToLengthSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly long _minTotalSamples; // interleaved
    private long _emitted;
    private bool _sourceEnded;
    private volatile bool _ended;

    public PadToLengthSampleProvider(ISampleProvider source, double lengthMs)
    {
        _source = source;
        var format = source.WaveFormat;
        // +1 frame: absorbs floating-point error in (DurationMs - startPositionMs) so the clock
        // lands at or past DurationMs, never a hair short (see audit #10, ordering subtlety 3).
        long frames = lengthMs > 0 ? (long)Math.Ceiling(lengthMs * format.SampleRate / 1000.0) + 1 : 0;
        _minTotalSamples = frames * format.Channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>True once Read has returned 0 (source and padding both exhausted).</summary>
    public bool Ended => _ended;

    public int Read(float[] buffer, int offset, int count)
    {
        if (!_sourceEnded)
        {
            int read = _source.Read(buffer, offset, count);
            if (read > 0)
            {
                _emitted += read;
                return read;
            }
            // Only a 0-length read means end of stream (ISampleProvider contract); a short read
            // is passed through as-is, never treated as the end.
            _sourceEnded = true;
        }

        long remaining = _minTotalSamples - _emitted;
        if (remaining <= 0)
        {
            _ended = true;
            return 0;
        }

        int toPad = (int)Math.Min(count, remaining);
        Array.Clear(buffer, offset, toPad);
        _emitted += toPad;
        return toPad;
    }
}
