namespace Acapella.Engine.Mix;

public enum PitchBackendSelection
{
    None,
    Automatic2B,
    Manual2A,
}

/// <summary>
/// Per-layer mix parameters, held in memory for the session (full save/load is Phase 5).
/// Fixed processing order applied by MixEngine:
/// pitch correction -> noise gate -> compressor -> EQ -> pan -> gain -> limiter.
/// </summary>
public class LayerMixParameters
{
    public float GainDb { get; set; } = 0f;
    public bool Mute { get; set; } = false;
    public bool Solo { get; set; } = false;

    /// <summary>-1 (full left) to +1 (full right), 0 = center.</summary>
    public float Pan { get; set; } = 0f;

    public float LowShelfGainDb { get; set; } = 0f;
    public float MidBellGainDb { get; set; } = 0f;
    public float HighShelfGainDb { get; set; } = 0f;

    // v7 Q0 task 4 (audit A4): all five FX slots default off, matching the FL Studio insert-slot
    // model -- an empty slot until the user enables it. Live in-memory default is false for a
    // brand-new layer; old project files predating this flag migrate to "enabled" on load instead
    // (see MixParametersDto.NoiseGateEnabled/EqEnabled), since those slots were unconditionally
    // applied before this flag existed.
    public bool NoiseGateEnabled { get; set; } = false;
    public float NoiseGateThresholdDb { get; set; } = -60f;
    public float NoiseGateReleaseMs { get; set; } = 100f;

    public bool CompressorEnabled { get; set; } = false;
    public float CompressorThresholdDb { get; set; } = -18f;
    public float CompressorRatio { get; set; } = 2f;

    public bool EqEnabled { get; set; } = false;

    public bool LimiterEnabled { get; set; } = false;
    public float LimiterCeilingDb { get; set; } = -0.3f;
    public float LimiterGainDb { get; set; } = 0f;

    public PitchBackendSelection PitchBackend { get; set; } = PitchBackendSelection.None;

    // v6 P3: when a stage's matching FabFilter plugin is detected, MixEngine auto-selects the
    // hosted backend for that stage instead of the native math above (native stays as the
    // automatic fallback when the plugin isn't installed -- see HostedPluginAvailability). These
    // hold each hosted stage's VST3 state chunk (persisted via ProjectFileDto's base64 fields) so
    // a user's tweaks in the plugin's own editor survive save/reload; null means "use the plugin's
    // factory default state" (no editor tweak yet, or never hosted).
    public byte[]? NoiseGateHostedState { get; set; }
    public byte[]? CompressorHostedState { get; set; }
    public byte[]? EqHostedState { get; set; }
    public byte[]? LimiterHostedState { get; set; }

    /// <summary>Stereo insert post-pan, only ever hosted (FabFilter Pro-R 2) -- no native fallback
    /// per the plan, so this is simply skipped if Pro-R 2 isn't detected even when true.</summary>
    public bool ReverbEnabled { get; set; } = false;
    public byte[]? ReverbHostedState { get; set; }

    /// <summary>When true, the layer plays a pre-rendered buffer of its whole chain (everything
    /// above except Mute/Solo, which stay live) instead of running it live -- see MixEngine's
    /// frozen-layer path. Defaults false, including for a project file saved before this field
    /// existed (System.Text.Json just leaves a missing bool at its default).</summary>
    public bool Frozen { get; set; } = false;

    /// <summary>Shallow copy -- used to build a Mute/Solo/Frozen-off variant of a layer's parameters
    /// for rendering its frozen buffer, without disturbing the live parameters object a UI control is
    /// bound to. byte[] hosted-state fields are shared by reference with the original, which is safe
    /// here since the render path only reads them.</summary>
    public LayerMixParameters Clone()
    {
        var clone = new LayerMixParameters();
        foreach (var prop in typeof(LayerMixParameters).GetProperties())
            if (prop.CanWrite) prop.SetValue(clone, prop.GetValue(this));
        return clone;
    }
}
