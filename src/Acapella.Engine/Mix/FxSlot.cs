using Acapella.Engine.Host;
using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>Where in a layer's chain a slot inserts: mono before pan, or stereo after pan/gain.</summary>
public enum FxSlotPosition
{
    PrePan,
    PostPan,
    PostGain,
}

/// <summary>
/// One FL-style insert slot in a layer's FX rack: its enabled flag, its hosted FabFilter plugin
/// (with the saved VST3 state blob), and its native fallback when that plugin isn't installed.
/// Slots are off by default (audit A4). A slot with no native fallback (reverb) is skipped when its
/// plugin is missing. The stage name keys the shared hosted instance cache, so the UI's editor
/// launcher and the chain always reach the same live instance (audit A1).
/// </summary>
public sealed class FxSlot
{
    private readonly Func<LayerMixParameters, bool> _isEnabled;
    private readonly Func<LayerMixParameters, byte[]?> _getHostedState;
    private readonly Action<LayerMixParameters, byte[]?> _setHostedState;
    private readonly Func<ISampleProvider, LayerMixParameters, ISampleProvider>? _buildNative;

    internal FxSlot(
        string stage, string pluginLabel, FxSlotPosition position, bool extendsTail,
        Func<LayerMixParameters, bool> isEnabled,
        Func<LayerMixParameters, byte[]?> getHostedState,
        Action<LayerMixParameters, byte[]?> setHostedState,
        Func<ISampleProvider, LayerMixParameters, ISampleProvider>? buildNative)
    {
        Stage = stage;
        PluginLabel = pluginLabel;
        Position = position;
        ExtendsTail = extendsTail;
        _isEnabled = isEnabled;
        _getHostedState = getHostedState;
        _setHostedState = setHostedState;
        _buildNative = buildNative;
    }

    public string Stage { get; }
    public string PluginLabel { get; }
    public FxSlotPosition Position { get; }

    /// <summary>True if the hosted plugin's tail rings on past the layer's source (reverb) and so
    /// lengthens the layer; other slots' tails are flushed but not counted as extra duration.</summary>
    public bool ExtendsTail { get; }

    public bool HasNativeFallback => _buildNative is not null;

    public bool IsEnabled(LayerMixParameters parameters) => _isEnabled(parameters);
    public byte[]? GetHostedState(LayerMixParameters parameters) => _getHostedState(parameters);
    public void SetHostedState(LayerMixParameters parameters, byte[]? state) => _setHostedState(parameters, state);

    /// <summary>Inserts this slot after source: the hosted plugin if installed (reset first so a
    /// reused instance doesn't replay the last run's buffered audio, audit A5; its latency added to
    /// totalLatency for the layer's single trim), else the native fallback, else passthrough.</summary>
    internal ISampleProvider Insert(ISampleProvider source, int layerId, LayerMixParameters parameters, HostedPluginService hosted, int sampleRate, ref int totalLatency)
    {
        if (!IsEnabled(parameters)) return source;

        if (!hosted.IsAvailable(PluginLabel))
            return _buildNative is null ? source : _buildNative(source, parameters);

        var instance = hosted.GetOrCreateInstance(layerId, Stage, PluginLabel, GetHostedState(parameters), sampleRate);
        hosted.Reset(instance);
        int tailFrames = ExtendsTail ? (int)(instance.TailSeconds * sampleRate) : 0;
        var provider = new HostedPluginSampleProvider(source, instance, HostedPluginSampleProvider.DefaultBlockSize, tailFrames);
        totalLatency += provider.LatencySamples;
        return provider;
    }

    /// <summary>How far this slot extends the layer past its source, in seconds.</summary>
    internal double TailSeconds(int layerId, LayerMixParameters parameters, HostedPluginService hosted, int sampleRate)
    {
        if (!ExtendsTail || !IsEnabled(parameters) || !hosted.IsAvailable(PluginLabel)) return 0;
        return hosted.GetOrCreateInstance(layerId, Stage, PluginLabel, GetHostedState(parameters), sampleRate).TailSeconds;
    }
}

/// <summary>The fixed FX rack every layer has, in chain order.</summary>
public static class FxSlots
{
    public static readonly FxSlot NoiseGate = new(
        "NoiseGate", "FabFilter Pro-G", FxSlotPosition.PrePan, extendsTail: false,
        p => p.NoiseGateEnabled, p => p.NoiseGateHostedState, (p, s) => p.NoiseGateHostedState = s,
        (source, p) => new NoiseGateSampleProvider(source, p.NoiseGateThresholdDb, p.NoiseGateReleaseMs));

    public static readonly FxSlot Compressor = new(
        "Compressor", "FabFilter Pro-C 3", FxSlotPosition.PrePan, extendsTail: false,
        p => p.CompressorEnabled, p => p.CompressorHostedState, (p, s) => p.CompressorHostedState = s,
        (source, p) => new CompressorSampleProvider(source, p.CompressorThresholdDb, p.CompressorRatio));

    public static readonly FxSlot Eq = new(
        "Eq", "FabFilter Pro-Q 4", FxSlotPosition.PrePan, extendsTail: false,
        p => p.EqEnabled, p => p.EqHostedState, (p, s) => p.EqHostedState = s,
        (source, p) => new ThreeBandEqSampleProvider(source, p.LowShelfGainDb, p.MidBellGainDb, p.HighShelfGainDb));

    /// <summary>Hosted-only (Pro-R 2): no native fallback, per the plan's v6 P3 task 5.</summary>
    public static readonly FxSlot Reverb = new(
        "Reverb", "FabFilter Pro-R 2", FxSlotPosition.PostPan, extendsTail: true,
        p => p.ReverbEnabled, p => p.ReverbHostedState, (p, s) => p.ReverbHostedState = s,
        buildNative: null);

    public static readonly FxSlot Limiter = new(
        "Limiter", "FabFilter Pro-L 2", FxSlotPosition.PostGain, extendsTail: false,
        p => p.LimiterEnabled, p => p.LimiterHostedState, (p, s) => p.LimiterHostedState = s,
        (source, p) => new LimiterSampleProvider(source, p.LimiterCeilingDb, p.LimiterGainDb));

    public static readonly IReadOnlyList<FxSlot> All = new[] { NoiseGate, Compressor, Eq, Reverb, Limiter };
}
