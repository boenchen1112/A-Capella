using A_Capella.Engine.Host;
using A_Capella.Engine.Presets;
using NAudio.Wave;

namespace A_Capella.Engine.Mix;

/// <summary>Where in a layer's chain a slot inserts: mono before pan, or stereo after pan.</summary>
public enum FxSlotPosition
{
    PrePan,
    PostPan,
}

/// <summary>
/// One effect in a layer's rack: a hosted FabFilter plugin driven only by a choice of factory
/// preset (no preset = off). A missing plugin, or a preset that can't be found, leaves the audio
/// untouched. The stage name keys the shared hosted instance cache, so the chain always reaches
/// the same live instance for a layer.
/// </summary>
public sealed class FxSlot
{
    private readonly Func<LayerMixParameters, PresetRef?> _getPreset;
    private readonly Action<LayerMixParameters, PresetRef?> _setPreset;

    internal FxSlot(
        string stage, string title, string pluginLabel, FxSlotPosition position, bool extendsTail,
        Func<LayerMixParameters, PresetRef?> getPreset, Action<LayerMixParameters, PresetRef?> setPreset)
    {
        Stage = stage;
        Title = title;
        PluginLabel = pluginLabel;
        Position = position;
        ExtendsTail = extendsTail;
        _getPreset = getPreset;
        _setPreset = setPreset;
    }

    public string Stage { get; }

    /// <summary>What the UI calls this effect, e.g. "Compressor".</summary>
    public string Title { get; }
    public string PluginLabel { get; }
    public FxSlotPosition Position { get; }

    /// <summary>True if the hosted plugin's tail rings on past the layer's source (reverb) and so
    /// lengthens the layer; other slots' tails are flushed but not counted as extra duration.</summary>
    public bool ExtendsTail { get; }

    public PresetRef? GetPreset(LayerMixParameters parameters) => _getPreset(parameters);
    public void SetPreset(LayerMixParameters parameters, PresetRef? preset) => _setPreset(parameters, preset);
    public bool IsEnabled(LayerMixParameters parameters) => GetPreset(parameters) is not null;

    /// <summary>Inserts this slot after source: the plugin with its preset loaded (reset first so a
    /// reused instance doesn't replay the last run's buffered audio; its latency added to
    /// totalLatency for the layer's single trim), or just source when the slot is off, the plugin
    /// isn't installed, or the preset isn't found.</summary>
    internal ISampleProvider Insert(ISampleProvider source, int layerId, LayerMixParameters parameters, HostedPluginService hosted, int sampleRate, ref int totalLatency)
    {
        var preset = GetPreset(parameters);
        if (preset is null || !hosted.IsAvailable(PluginLabel)) return source;

        var instance = hosted.GetOrCreatePresetInstance(layerId, Stage, PluginLabel, preset, sampleRate);
        if (instance is null) return source;

        hosted.Reset(instance);
        int tailFrames = ExtendsTail ? (int)(instance.TailSeconds * sampleRate) : 0;
        var provider = new HostedPluginSampleProvider(source, instance, HostedPluginSampleProvider.DefaultBlockSize, tailFrames);
        totalLatency += provider.LatencySamples;
        return provider;
    }

    /// <summary>How far this slot extends the layer past its source, in seconds.</summary>
    internal double TailSeconds(int layerId, LayerMixParameters parameters, HostedPluginService hosted, int sampleRate)
    {
        var preset = GetPreset(parameters);
        if (!ExtendsTail || preset is null || !hosted.IsAvailable(PluginLabel)) return 0;
        return hosted.GetOrCreatePresetInstance(layerId, Stage, PluginLabel, preset, sampleRate)?.TailSeconds ?? 0;
    }
}

/// <summary>The fixed effect rack every layer has, in chain order.</summary>
public static class FxSlots
{
    public static readonly FxSlot Compressor = new(
        "Compressor", "Compressor", "FabFilter Pro-C 3", FxSlotPosition.PrePan, extendsTail: false,
        p => p.CompressorPreset, (p, v) => p.CompressorPreset = v);

    public static readonly FxSlot Eq = new(
        "Eq", "EQ", "FabFilter Pro-Q 4", FxSlotPosition.PrePan, extendsTail: false,
        p => p.EqPreset, (p, v) => p.EqPreset = v);

    public static readonly FxSlot DeEsser = new(
        "DeEsser", "De-esser", "FabFilter Pro-DS", FxSlotPosition.PrePan, extendsTail: false,
        p => p.DeEsserPreset, (p, v) => p.DeEsserPreset = v);

    public static readonly FxSlot Reverb = new(
        "Reverb", "Reverb", "FabFilter Pro-R 2", FxSlotPosition.PostPan, extendsTail: true,
        p => p.ReverbPreset, (p, v) => p.ReverbPreset = v);

    public static readonly IReadOnlyList<FxSlot> All = new[] { Compressor, Eq, DeEsser, Reverb };
}
