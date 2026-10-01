using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>In-memory hosted plugin: delays its input by LatencySamples (a pure delay line, so
/// latency compensation is exactly checkable), reports a configurable tail, and records Reset and
/// editor calls.</summary>
public sealed class FakeHostedPlugin : IHostedPlugin
{
    private readonly Queue<(float L, float R)> _delay = new();
    private readonly float[] _parameters = new float[4];

    public FakeHostedPlugin(string label, int latencySamples = 0, double tailSeconds = 0)
    {
        Label = label;
        LatencySamples = latencySamples;
        TailSeconds = tailSeconds;
        FillDelay();
    }

    public string Label { get; }
    public int LatencySamples { get; }
    public double TailSeconds { get; }
    public byte[] State { get; private set; } = Array.Empty<byte>();
    public int ResetCount { get; private set; }
    public int ShowEditorCount { get; private set; }
    public bool Disposed { get; private set; }

    public void ProcessBlock(float[] inL, float[] inR, float[] outL, float[] outR, int numSamples)
    {
        for (int i = 0; i < numSamples; i++)
        {
            _delay.Enqueue((inL[i], inR[i]));
            var (l, r) = _delay.Dequeue();
            outL[i] = l;
            outR[i] = r;
        }
    }

    public void Reset()
    {
        ResetCount++;
        FillDelay();
    }

    public byte[] GetState() => State;

    public void SetState(byte[] data)
    {
        if (data.Length > 0) State = data.ToArray();
    }

    /// <summary>Simulates the user tweaking the plugin in its own editor.</summary>
    public void TweakInEditor(byte[] newState) => State = newState.ToArray();

    public int ParameterCount => _parameters.Length;
    public string GetParameterName(int index) => $"Param {index}";
    public float GetParameterValue(int index) => _parameters[index];
    public void SetParameterValue(int index, float value) => _parameters[index] = value;

    public bool ShowEditorWindow(string title)
    {
        ShowEditorCount++;
        return true;
    }

    public void CloseEditorWindow() { }

    public void Dispose() => Disposed = true;

    private void FillDelay()
    {
        _delay.Clear();
        for (int i = 0; i < LatencySamples; i++) _delay.Enqueue((0f, 0f));
    }
}

/// <summary>Creates FakeHostedPlugins with per-label latency/tail, and reports exactly those labels
/// as available.</summary>
public sealed class FakeHostedPluginFactory : IHostedPluginFactory, IHostedPluginAvailability
{
    private readonly Dictionary<string, (int Latency, double Tail)> _plugins = new();

    public List<FakeHostedPlugin> Created { get; } = new();

    public FakeHostedPluginFactory With(string label, int latencySamples = 0, double tailSeconds = 0)
    {
        _plugins[label] = (latencySamples, tailSeconds);
        return this;
    }

    public bool IsAvailable(string pluginLabel) => _plugins.ContainsKey(pluginLabel);

    public IHostedPlugin Create(string pluginLabel, double sampleRate, int maxBlockSize)
    {
        var (latency, tail) = _plugins[pluginLabel];
        var plugin = new FakeHostedPlugin(pluginLabel, latency, tail);
        Created.Add(plugin);
        return plugin;
    }

    public HostedPluginService CreateService() => new(this, dispatcher: null, factory: this);
}
