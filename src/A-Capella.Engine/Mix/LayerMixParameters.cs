using A_Capella.Engine.Presets;

namespace A_Capella.Engine.Mix;

public enum PitchBackendSelection
{
    None,
    Automatic2B,
    Manual2A,
}

/// <summary>
/// Per-layer mix parameters, held in memory for the session. Fixed processing order applied by
/// MixEngine: compressor -> EQ -> de-esser -> pan -> reverb -> gain. Each effect is nothing more
/// than a choice of FabFilter factory preset (null = off); there are no per-effect settings.
/// </summary>
public class LayerMixParameters
{
    public float GainDb { get; set; } = 0f;
    public bool Mute { get; set; } = false;
    public bool Solo { get; set; } = false;

    /// <summary>-1 (full left) to +1 (full right), 0 = center.</summary>
    public float Pan { get; set; } = 0f;

    public PresetRef? CompressorPreset { get; set; }
    public PresetRef? EqPreset { get; set; }
    public PresetRef? DeEsserPreset { get; set; }
    public PresetRef? ReverbPreset { get; set; }

    /// <summary>Legacy: only the dormant Melodyne code reads this; the app never sets it.</summary>
    public PitchBackendSelection PitchBackend { get; set; } = PitchBackendSelection.None;

    /// <summary>When true, the layer plays a pre-rendered buffer of its whole chain (everything
    /// above except Mute/Solo, which stay live) instead of running it live -- see MixEngine's
    /// frozen-layer path. Defaults false, including for a project file saved before this field
    /// existed (System.Text.Json just leaves a missing bool at its default).</summary>
    public bool Frozen { get; set; } = false;

    /// <summary>Shallow copy -- used to build a Mute/Solo/Frozen-off variant of a layer's parameters
    /// for rendering its frozen buffer, without disturbing the live parameters object a UI control is
    /// bound to.</summary>
    public LayerMixParameters Clone()
    {
        var clone = new LayerMixParameters();
        foreach (var prop in typeof(LayerMixParameters).GetProperties())
            if (prop.CanWrite) prop.SetValue(clone, prop.GetValue(this));
        return clone;
    }
}
