using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>
/// v7 Q0 (audit B8): aca_scan_plugin/aca_create_instance now fail loudly instead of
/// self-initialising JUCE, so every test that touches the bridge needs HostedPluginInstance.
/// Initialize() to have run first. xunit constructs a collection fixture exactly once, before any
/// test in the collection runs (and disposes it after the last one), so calling Initialize() here
/// covers the whole "JuceHosting" collection with a single call on this collection's own thread --
/// exactly the "one JUCE thread for this whole collection" shape InlineHostedPluginDispatcher
/// assumes in tests.
/// </summary>
public sealed class JuceHostingFixture
{
    public JuceHostingFixture() => HostedPluginInstance.Initialize();
}

/// <summary>
/// xunit runs each test class as its own collection by default, and collections run in parallel
/// on separate thread-pool threads. JUCE's VST3 hosting touches COM internally; two of those
/// collections initializing it concurrently on different threads throws RPC_E_CHANGED_MODE
/// (0x80010106) and can leave an unresponsive native window behind. Every test class that creates
/// a HostedPluginInstance (directly or via MixEngine) must share this one collection so xunit
/// runs them sequentially instead. Production never hits this: PreviewPlaybackEngine's single
/// command thread is the only thing that ever calls into the bridge.
/// </summary>
[CollectionDefinition("JuceHosting", DisableParallelization = true)]
public class JuceHostingCollection : ICollectionFixture<JuceHostingFixture>
{
}
