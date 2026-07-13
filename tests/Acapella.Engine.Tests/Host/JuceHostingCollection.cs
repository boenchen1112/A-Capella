namespace Acapella.Engine.Tests.Host;

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
public class JuceHostingCollection
{
}
