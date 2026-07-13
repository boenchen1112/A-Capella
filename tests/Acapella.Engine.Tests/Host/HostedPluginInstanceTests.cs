using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

[Collection("JuceHosting")]
public class HostedPluginInstanceTests
{
    private const double SampleRate = 44100.0;
    private const int BlockSize = 512;

    public static IEnumerable<object[]> AllKnownPlugins =>
        HostedPluginCatalog.KnownPluginPaths.Select(kv => new object[] { kv.Key, kv.Value });

    [Theory]
    [MemberData(nameof(AllKnownPlugins))]
    public void Scan_FindsEachKnownPlugin(string label, string path)
    {
        bool found = HostedPluginInstance.TryScan(path, out var description);
        Assert.True(found, $"{label} not found at {path}");
        Assert.False(string.IsNullOrEmpty(description!.Name));
    }

    [Fact]
    public void CreateProcessRelease_HundredTimes_DoesNotLeakOrThrow()
    {
        string path = HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-G"];
        var inL = new float[BlockSize];
        var inR = new float[BlockSize];
        var outL = new float[BlockSize];
        var outR = new float[BlockSize];
        inL[0] = 1f;
        inR[0] = 1f;

        for (int i = 0; i < 100; i++)
        {
            using var instance = HostedPluginInstance.Create(path, SampleRate, BlockSize);
            instance.ProcessBlock(inL, inR, outL, outR, BlockSize);
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public HostedPluginInstanceTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

    [Fact]
    public void State_RoundTripsThroughGetAndSet()
    {
        string path = HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"];
        using var instance = HostedPluginInstance.Create(path, SampleRate, BlockSize);

        Assert.True(instance.ParameterCount > 0);

        // Not every parameter is a free-running continuous knob -- e.g. "Band 1 Used" is a boolean
        // toggle that happens to echo back an unquantized 0.75 from GetParameterValue() right after
        // SetParameterValue(), but quantizes to 1 once round-tripped through plugin state. Pick a
        // parameter whose name suggests a real continuous control (gain/frequency/etc.) so the test
        // verifies real state persistence rather than an artifact of boolean-toggle rounding.
        int paramIndex = -1;
        for (int i = 0; i < instance.ParameterCount; i++)
        {
            string name = instance.GetParameterName(i);
            if (!name.Contains("Gain", StringComparison.OrdinalIgnoreCase))
                continue;
            instance.SetParameterValue(i, 0.75f);
            if (MathF.Abs(instance.GetParameterValue(i) - 0.75f) < 0.01f)
            {
                paramIndex = i;
                break;
            }
        }
        Assert.True(paramIndex >= 0, "expected at least one continuous 'Gain' parameter accepting 0.75");

        // Some VST3 plugins only commit a parameter change into their persisted component state
        // once a block has actually been processed with the change applied, rather than the moment
        // setValue() returns -- push one silent block through before reading state back.
        var silence = new float[BlockSize];
        var scratch = new float[BlockSize];
        instance.ProcessBlock(silence, silence, scratch, scratch, BlockSize);

        byte[] state = instance.GetState();
        Assert.NotEmpty(state);
        _output.WriteLine($"paramIndex={paramIndex} name={instance.GetParameterName(paramIndex)} preStateValue={instance.GetParameterValue(paramIndex)} stateBytes={state.Length}");

        using var restored = HostedPluginInstance.Create(path, SampleRate, BlockSize);
        _output.WriteLine($"restored default value={restored.GetParameterValue(paramIndex)}");
        restored.SetState(state);
        _output.WriteLine($"restored post-setstate value={restored.GetParameterValue(paramIndex)}");

        Assert.Equal(0.75f, restored.GetParameterValue(paramIndex), precision: 2);
    }

    // v6 P3a's critical latency-compensation groundwork test: an impulse fed through Pro-L 2 (which
    // reports non-zero lookahead latency) must land at the same sample index once GetLatencySamples
    // worth of leading samples are trimmed from the output. This is the exact class of desync bug
    // this project's own history shows gets built twice if not tested up front (see P0's audit).
    [Fact]
    public void LatencySamples_WhenTrimmed_AlignsImpulseWithInput()
    {
        string path = HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-L 2"];
        using var instance = HostedPluginInstance.Create(path, SampleRate, BlockSize);

        int latency = instance.LatencySamples;
        Assert.True(latency > 0, "Pro-L 2 is expected to report non-zero lookahead latency");

        int totalSamples = BlockSize * 8;
        var inL = new float[totalSamples];
        var inR = new float[totalSamples];
        const int impulseIndex = 100;
        inL[impulseIndex] = 1f;
        inR[impulseIndex] = 1f;

        var outL = new float[totalSamples];
        var outR = new float[totalSamples];

        for (int offset = 0; offset < totalSamples; offset += BlockSize)
        {
            var chunkInL = inL.Skip(offset).Take(BlockSize).ToArray();
            var chunkInR = inR.Skip(offset).Take(BlockSize).ToArray();
            var chunkOutL = new float[BlockSize];
            var chunkOutR = new float[BlockSize];
            instance.ProcessBlock(chunkInL, chunkInR, chunkOutL, chunkOutR, BlockSize);
            Array.Copy(chunkOutL, 0, outL, offset, BlockSize);
            Array.Copy(chunkOutR, 0, outR, offset, BlockSize);
        }

        int expectedIndex = impulseIndex + latency;
        const int window = 4;
        int searchStart = Math.Max(0, expectedIndex - window);
        int searchEnd = Math.Min(totalSamples, expectedIndex + window + 1);

        float peak = 0f;
        int peakIndex = searchStart;
        for (int i = searchStart; i < searchEnd; i++)
        {
            if (MathF.Abs(outL[i]) > peak)
            {
                peak = MathF.Abs(outL[i]);
                peakIndex = i;
            }
        }

        Assert.True(peak > 0f, "expected a non-zero response near the latency-compensated impulse index");
        // Allow a small tolerance: a limiter's lookahead peak-detection can spread an impulse's
        // energy across a couple of samples rather than reproducing a single-sample spike exactly.
        Assert.InRange(peakIndex, expectedIndex - 2, expectedIndex + 2);
    }

    /// <summary>
    /// P3a task 7 threading spike: the app's design calls for all plugin lifecycle/editor calls to
    /// happen on the WPF UI thread (where JUCE's MessageManager gets bound via Initialize()), while
    /// ProcessBlock runs on a separate audio/command thread -- matching how a real DAW's realtime
    /// audio thread is separate from its GUI thread. This proves that split doesn't crash or hang
    /// JUCE before any UI is built on top of the assumption: open an editor window on this thread,
    /// hammer ProcessBlock concurrently from a background thread, close the editor, release clean.
    /// A hang here would show up as this test timing out; a JUCE assert now routes to stderr
    /// instead of a blocking dialog (see HostBridge.cpp's juceInit()).
    /// </summary>
    [Fact]
    public void EditorWindow_OpensAndClosesSafely_WhileProcessBlockRunsOnAnotherThread()
    {
        HostedPluginInstance.Initialize();
        string path = HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"];
        using var instance = HostedPluginInstance.Create(path, SampleRate, BlockSize);

        bool opened = instance.ShowEditorWindow("Pro-Q 4 (threading spike)");
        Assert.True(opened, "expected Pro-Q 4 to report an editor");

        var inL = new float[BlockSize];
        var inR = new float[BlockSize];
        var outL = new float[BlockSize];
        var outR = new float[BlockSize];

        using var cts = new System.Threading.CancellationTokenSource();
        var processingTask = System.Threading.Tasks.Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
                instance.ProcessBlock(inL, inR, outL, outR, BlockSize);
        });

        System.Threading.Thread.Sleep(500);
        cts.Cancel();
        Assert.True(processingTask.Wait(TimeSpan.FromSeconds(5)), "processBlock loop did not stop cleanly");

        instance.CloseEditorWindow();
    }
}
