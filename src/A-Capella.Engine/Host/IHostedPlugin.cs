namespace A_Capella.Engine.Host;

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

    /// <summary>The text the plugin displays for a normalized (0..1) parameter value, e.g. "-18.0 dB".
    /// Does not change the parameter. Empty when the plugin can't say.</summary>
    string GetParameterText(int index, float normalizedValue) => "";

    /// <summary>The normalized value the plugin assigns to some display text (the reverse of
    /// GetParameterText); 0 when it can't parse it.</summary>
    float GetParameterValueForText(int index, string text) => 0f;

    /// <summary>Distinct values a parameter can take (2 for a switch); int.MaxValue-ish for a continuous one.</summary>
    int GetParameterNumSteps(int index) => int.MaxValue;

    /// <summary>Number of entries in the plugin's own program (preset) list; 0 or 1 when it has none.</summary>
    int ProgramCount => 0;
    string GetProgramName(int index) => "";
    void SetProgram(int index) { }

    /// <summary>Opens the plugin's own editor in its own top-level window; false if it has none.</summary>
    bool ShowEditorWindow(string title);

    /// <summary>Safe to call when no editor is open.</summary>
    void CloseEditorWindow();

    /// <summary>Hides or shows an already-open editor window without closing it -- used while the
    /// app window is minimized/restored. Safe to call when no editor is open.</summary>
    void SetEditorVisible(bool visible);
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
