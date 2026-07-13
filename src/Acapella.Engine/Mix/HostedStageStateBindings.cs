namespace Acapella.Engine.Mix;

/// <summary>
/// Maps a hosted stage name to its LayerMixParameters state-blob field (v7 Q0 task 3): single
/// source of truth used both by the UI's per-row load/store when opening an editor, and by
/// MainWindow's save/export/undo/restore sync so live plugin state and the project file always
/// agree (audit A2/B7) without duplicating this switch in two places.
/// </summary>
public static class HostedStageStateBindings
{
    public static readonly IReadOnlyDictionary<string, string> PluginLabelByStage = new Dictionary<string, string>
    {
        [MixEngine.NoiseGateStage] = MixEngine.NoiseGatePluginLabel,
        [MixEngine.CompressorStage] = MixEngine.CompressorPluginLabel,
        [MixEngine.EqStage] = MixEngine.EqPluginLabel,
        [MixEngine.LimiterStage] = MixEngine.LimiterPluginLabel,
        [MixEngine.ReverbStage] = MixEngine.ReverbPluginLabel,
    };

    public static byte[]? Get(LayerMixParameters p, string stage) => stage switch
    {
        MixEngine.NoiseGateStage => p.NoiseGateHostedState,
        MixEngine.CompressorStage => p.CompressorHostedState,
        MixEngine.EqStage => p.EqHostedState,
        MixEngine.LimiterStage => p.LimiterHostedState,
        MixEngine.ReverbStage => p.ReverbHostedState,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown hosted stage"),
    };

    public static void Set(LayerMixParameters p, string stage, byte[]? state)
    {
        switch (stage)
        {
            case MixEngine.NoiseGateStage: p.NoiseGateHostedState = state; break;
            case MixEngine.CompressorStage: p.CompressorHostedState = state; break;
            case MixEngine.EqStage: p.EqHostedState = state; break;
            case MixEngine.LimiterStage: p.LimiterHostedState = state; break;
            case MixEngine.ReverbStage: p.ReverbHostedState = state; break;
            default: throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown hosted stage");
        }
    }
}
