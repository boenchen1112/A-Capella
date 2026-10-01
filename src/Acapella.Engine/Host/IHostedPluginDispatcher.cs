namespace Acapella.Engine.Host;

/// <summary>
/// Marshals a hosted-plugin lifecycle call onto whatever single thread JUCE's MessageManager was
/// bound to via HostedPluginInstance.Initialize() (v7 Q0 tasks 1-2, audit A3/B8). Production wires
/// a WPF-Dispatcher-backed implementation (Acapella.App, so this engine project stays UI-framework
/// agnostic); tests use InlineHostedPluginDispatcher since the test's own calling thread already
/// plays the role of "the one JUCE thread" there.
/// </summary>
public interface IHostedPluginDispatcher
{
    void Invoke(Action action);
    T Invoke<T>(Func<T> func);
}

/// <summary>Executes immediately on the calling thread -- no marshaling. Correct only when the
/// caller is already running on the same thread that called HostedPluginInstance.Initialize()
/// (true for every existing test, which calls Initialize() and then everything else from its own
/// single test-method thread). NOT safe to use in the app if any hosted-plugin call can arrive
/// from a thread other than the one that initialized JUCE -- that's exactly the bug this
/// dispatcher abstraction exists to prevent in production.</summary>
public sealed class InlineHostedPluginDispatcher : IHostedPluginDispatcher
{
    public static readonly InlineHostedPluginDispatcher Instance = new();
    public void Invoke(Action action) => action();
    public T Invoke<T>(Func<T> func) => func();
}
