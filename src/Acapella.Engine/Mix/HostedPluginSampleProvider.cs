using Acapella.Engine.Host;
using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>
/// Wraps a hosted plugin's ProcessBlock as an ISampleProvider so a hosted FabFilter stage
/// slots into the pull-based mix chain like any native stage (v6 P3 task 4). Does not own the
/// wrapped instance -- HostedPluginInstanceCache does -- and never disposes it.
///
/// Mono stages (pre-pan: gate/compressor/EQ) run against a mono source: the same signal is fed to
/// both plugin channels and only the left output is taken, matching the mono ISampleProvider
/// contract used everywhere else in the chain before PanningSampleProvider. Stereo stages
/// (post-pan: reverb, or the per-layer limiter) pass both channels through.
///
/// Latency is NOT trimmed here. A hosted plugin's own processing delay (GetLatencySamples) pushes
/// its entire output later by that many samples; once the upstream source is exhausted, this stage
/// keeps feeding silence through the plugin for up to (LatencySamples + extraTailFrames) more
/// frames, to flush out real audio still buffered inside it -- extending this stage's total output
/// length by that much. MixEngine sums LatencySamples across a layer's hosted stages and trims that
/// total off the front of the finished chain in one place (LatencySkipSampleProvider); extraTailFrames
/// exists solely for the reverb stage's tail extension (v6 P3 task 5), which is deliberately NOT
/// trimmed away -- it's the desired lengthening of the layer's audible duration.
/// </summary>
public sealed class HostedPluginSampleProvider : ISampleProvider
{
    public const int DefaultBlockSize = 4096;

    private readonly ISampleProvider _source;
    private readonly IHostedPlugin _instance;
    private readonly int _channels;
    private readonly int _blockSize;

    private readonly float[] _sourceScratch;
    private readonly float[] _inL, _inR, _outL, _outR;
    private readonly float[] _pending;
    private int _pendingFrames;
    private int _pendingOffset;

    private bool _sourceExhausted;
    private int _silenceFramesRemaining;

    public HostedPluginSampleProvider(ISampleProvider source, IHostedPlugin instance, int blockSize = DefaultBlockSize, int extraTailFrames = 0)
    {
        _channels = source.WaveFormat.Channels;
        if (_channels != 1 && _channels != 2)
            throw new ArgumentException("HostedPluginSampleProvider only supports mono or stereo sources.", nameof(source));

        _source = source;
        _instance = instance;
        _blockSize = blockSize;

        _sourceScratch = new float[blockSize * _channels];
        _inL = new float[blockSize];
        _inR = new float[blockSize];
        _outL = new float[blockSize];
        _outR = new float[blockSize];
        _pending = new float[blockSize * _channels];

        LatencySamples = instance.LatencySamples;
        _silenceFramesRemaining = LatencySamples + Math.Max(0, extraTailFrames);
    }

    /// <summary>The wrapped instance's own reported processing delay, for MixEngine to sum across
    /// a layer's hosted stages and trim once via LatencySkipSampleProvider.</summary>
    public int LatencySamples { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / _channels;
        int framesWritten = 0;

        while (framesWritten < framesRequested)
        {
            if (_pendingFrames == 0 && !ProduceNextBlock())
                break;

            int toCopy = Math.Min(_pendingFrames, framesRequested - framesWritten);
            Array.Copy(_pending, _pendingOffset * _channels, buffer, offset + framesWritten * _channels, toCopy * _channels);
            _pendingOffset += toCopy;
            _pendingFrames -= toCopy;
            framesWritten += toCopy;
        }

        return framesWritten * _channels;
    }

    /// <summary>Processes exactly one block's worth of audio into _pending. Returns false once
    /// there is truly nothing left (source exhausted and this stage's own latency/tail flush is
    /// done).</summary>
    private bool ProduceNextBlock()
    {
        int framesThisBlock;

        if (!_sourceExhausted)
        {
            int samplesRead = _source.Read(_sourceScratch, 0, _blockSize * _channels);
            framesThisBlock = samplesRead / _channels;
            if (framesThisBlock == 0)
                _sourceExhausted = true;
        }
        else
        {
            framesThisBlock = 0;
        }

        if (_sourceExhausted)
        {
            if (_silenceFramesRemaining <= 0)
                return false;

            framesThisBlock = Math.Min(_blockSize, _silenceFramesRemaining);
            Array.Clear(_inL, 0, framesThisBlock);
            Array.Clear(_inR, 0, framesThisBlock);
            _silenceFramesRemaining -= framesThisBlock;
        }
        else
        {
            for (int i = 0; i < framesThisBlock; i++)
            {
                float l = _sourceScratch[i * _channels];
                _inL[i] = l;
                _inR[i] = _channels == 2 ? _sourceScratch[i * _channels + 1] : l;
            }
        }

        _instance.ProcessBlock(_inL, _inR, _outL, _outR, framesThisBlock);

        for (int i = 0; i < framesThisBlock; i++)
        {
            if (_channels == 1)
                _pending[i] = _outL[i];
            else
            {
                _pending[i * 2] = _outL[i];
                _pending[i * 2 + 1] = _outR[i];
            }
        }
        _pendingFrames = framesThisBlock;
        _pendingOffset = 0;
        return true;
    }
}
