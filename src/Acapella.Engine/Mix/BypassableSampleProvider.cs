using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>A live slot node whose processing can be switched on/off while the chain is playing,
/// without rebuilding the chain. MixEngine keeps a registry of these per (layerId, stage) so a
/// slot's power toggle can flip Bypassed on the already-running node instead of forcing a full
/// preview rebuild.</summary>
public interface ISlotBypassNode
{
    bool Bypassed { get; set; }
}

/// <summary>Wraps a native FX stage (gate/compressor/EQ/limiter) with a toggle: while bypassed,
/// reads straight from the unprocessed source instead of the processed stage, so there is always
/// exactly one reader of source per Read() call (never both at once). Native stages report no
/// latency, so a plain passthrough is correct -- no delay compensation needed, unlike the hosted
/// equivalent.</summary>
public sealed class BypassableSampleProvider : ISampleProvider, ISlotBypassNode
{
    private readonly ISampleProvider _source;
    private readonly ISampleProvider _processed;
    private volatile bool _bypassed;

    public BypassableSampleProvider(ISampleProvider source, ISampleProvider processed)
    {
        _source = source;
        _processed = processed;
    }

    public bool Bypassed
    {
        get => _bypassed;
        set => _bypassed = value;
    }

    public WaveFormat WaveFormat => _processed.WaveFormat;

    public int Read(float[] buffer, int offset, int count) =>
        _bypassed ? _source.Read(buffer, offset, count) : _processed.Read(buffer, offset, count);
}
