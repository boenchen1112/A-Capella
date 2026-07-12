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

    public float NoiseGateThresholdDb { get; set; } = -60f;
    public float NoiseGateReleaseMs { get; set; } = 100f;

    public bool CompressorEnabled { get; set; } = false;
    public float CompressorThresholdDb { get; set; } = -18f;
    public float CompressorRatio { get; set; } = 2f;

    public bool LimiterEnabled { get; set; } = false;
    public float LimiterCeilingDb { get; set; } = -0.3f;
    public float LimiterGainDb { get; set; } = 0f;

    public PitchBackendSelection PitchBackend { get; set; } = PitchBackendSelection.None;
}
