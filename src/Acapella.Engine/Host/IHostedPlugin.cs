namespace Acapella.Engine.Host;

/// <summary>
/// One live hosted plugin instance, as the mix chain and the launcher UI see it. Two adapters sit
/// behind this seam: HostedPluginInstance (the JUCE/VST3 bridge) in production, and in-memory fakes
/// in tests, so chain latency/tail/reset/state behavior is testable on a machine without the
/// native DLL or FabFilter installed.
///
/// Thread affinity: every member except ProcessBlock must run on the hosted-plugin thread --
/// HostedPluginService marshals them there. ProcessBlock runs on the audio thread.
/// </summary>
public interface IHostedPlugin : IDisposable
{
    /// <summary>Processing delay in samples, >= 0.</summary>
    int LatencySamples { get; }

    /// <summary>How long the plugin keeps producing output after its input goes silent, finite
    /// and within [0, HostedPluginInstance.MaxTailSeconds] -- adapters clamp bogus reports.</summary>
    double TailSeconds { get; }

    /// <summary>Always stereo in/out, regardless of the plugin's real bus layout.</summary>
    void ProcessBlock(float[] inL, float[] inR, float[] outL, float[] outR, int numSamples);

    /// <summary>Clears internal DSP state (lookahead/delay lines) so a reused instance doesn't
    /// bleed the previous run's audio into a new chain (audit A5).</summary>
    void Reset();

    /// <summary>Empty when the plugin has no state (factory default).</summary>
    byte[] GetState();

    /// <summary>Ignores an empty or oversized blob.</summary>
    void SetState(byte[] data);

    int ParameterCount { get; }
    string GetParameterName(int index);
    float GetParameterValue(int index);
    void SetParameterValue(int index, float value);

    /// <summary>Opens the plugin's own editor in its own top-level window; false if it has none.</summary>
    bool ShowEditorWindow(string title);

    /// <summary>Safe to call when no editor is open.</summary>
    void CloseEditorWindow();
}

/// <summary>Creates hosted plugin instances by catalog label (HostedPluginCatalog.KnownPluginPaths' keys).</summary>
public interface IHostedPluginFactory
{
    IHostedPlugin Create(string pluginLabel, double sampleRate, int maxBlockSize);
}

public sealed class NativeHostedPluginFactory : IHostedPluginFactory
{
    public static readonly NativeHostedPluginFactory Instance = new();

    public IHostedPlugin Create(string pluginLabel, double sampleRate, int maxBlockSize) =>
        HostedPluginInstance.Create(HostedPluginCatalog.KnownPluginPaths[pluginLabel], sampleRate, maxBlockSize);
}
