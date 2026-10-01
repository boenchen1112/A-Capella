using System.Windows.Threading;
using Acapella.Engine.Host;

namespace Acapella.App;

/// <summary>
/// Marshals every hosted-plugin lifecycle call onto the WPF UI thread (v7 Q0 tasks 1-2, audit
/// A3/B8) -- the same thread MainWindow calls HostedPluginInstance.Initialize() from at startup,
/// so JUCE's MessageManager and this dispatcher always agree on which thread is "the" JUCE thread.
/// A WPF Dispatcher's own message pump (a real Win32 GetMessage/DispatchMessage loop) is also what
/// lets a JUCE-owned native editor window receive input at all -- any thread with such a loop
/// dispatches messages to every HWND created on it, not just its own windows.
///
/// CheckAccess() short-circuits when already on the dispatcher thread (e.g. a call made directly
/// from MainWindow's own constructor/event handlers) so it runs inline instead of a needless
/// re-entrant Invoke; callers from any other thread (PreviewPlaybackEngine's command thread,
/// ExportEngine's Task.Run) block until the UI thread services the call.
/// </summary>
public sealed class WpfHostedPluginDispatcher : IHostedPluginDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfHostedPluginDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Invoke(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }

    public T Invoke<T>(Func<T> func) =>
        _dispatcher.CheckAccess() ? func() : _dispatcher.Invoke(func);
}
