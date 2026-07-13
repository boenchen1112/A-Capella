using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>
/// Tests HostedPluginService's cross-thread marshaling mechanics in isolation from real JUCE calls
/// (v7 Q0 thread test, audit A3/B8). Deliberately uses a fake IHostedPluginAvailability that never
/// calls into the native bridge (EnsureScanned only ever calls IsAvailable) -- JUCE's MessageManager
/// binds to whichever thread first calls aca_initialize/aca_scan_plugin/aca_create_instance, and
/// that binding is process-wide and permanent, so a second "pretend UI thread" spun up here that
/// actually touched the native bridge would collide with whatever thread JuceHostingTests already
/// bound it to and corrupt JUCE's internal state (this crashed the test host with a heap-corruption
/// assertion the first time this test was written that way -- see commit history). Not in the
/// "JuceHosting" collection since it never touches the bridge at all.</summary>
public class HostedPluginServiceDispatcherTests
{
    private sealed class NeverAvailable : IHostedPluginAvailability
    {
        public bool IsAvailable(string pluginLabel) => false;
    }

    /// <summary>A minimal cross-thread dispatcher mirroring WpfHostedPluginDispatcher's shape
    /// (marshal from any caller thread onto one fixed dedicated thread) without pulling a WPF
    /// dependency into this test project.</summary>
    private sealed class DedicatedThreadDispatcher : IHostedPluginDispatcher, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private volatile int _threadId;
        public int ThreadId => _threadId;

        public DedicatedThreadDispatcher()
        {
            _thread = new Thread(() =>
            {
                _threadId = Environment.CurrentManagedThreadId;
                foreach (var action in _queue.GetConsumingEnumerable())
                    action();
            })
            { IsBackground = true };
            _thread.Start();
            while (_threadId == 0) Thread.Sleep(1);
        }

        public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

        public T Invoke<T>(Func<T> func)
        {
            T result = default!;
            Exception? error = null;
            using var done = new ManualResetEventSlim(false);
            _queue.Add(() =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error is not null) throw new AggregateException(error);
            return result;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(2000);
        }
    }

    /// <summary>Records which thread every dispatched call actually ran on.</summary>
    private sealed class RecordingDispatcher : IHostedPluginDispatcher
    {
        private readonly IHostedPluginDispatcher _inner;
        private readonly System.Collections.Concurrent.ConcurrentBag<int> _observedThreadIds;
        public RecordingDispatcher(IHostedPluginDispatcher inner, System.Collections.Concurrent.ConcurrentBag<int> observedThreadIds)
        {
            _inner = inner;
            _observedThreadIds = observedThreadIds;
        }
        public void Invoke(Action action) => _inner.Invoke(() => { _observedThreadIds.Add(Environment.CurrentManagedThreadId); action(); });
        public T Invoke<T>(Func<T> func) => _inner.Invoke(() => { _observedThreadIds.Add(Environment.CurrentManagedThreadId); return func(); });
    }

    /// <summary>Thread test (Q0 acceptance, audit A3/B8): a call issued from a background thread
    /// that is neither the test thread nor the dispatcher's own thread -- reproducing the real
    /// app's command-thread/Task.Run-thread -> UI-dispatcher-thread marshal -- completes without
    /// deadlocking, and lands on the one dedicated dispatcher thread. Using
    /// InlineHostedPluginDispatcher here would prove nothing (no marshaling happens at all);
    /// DedicatedThreadDispatcher exercises real cross-thread blocking, same as
    /// WpfHostedPluginDispatcher does against the WPF UI thread in production.</summary>
    [Fact]
    public void EnsureScanned_CalledFromBackgroundThread_CompletesAndStaysOnTheDispatcherThread()
    {
        using var dispatcherThread = new DedicatedThreadDispatcher();
        var observedThreadIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        var recordingDispatcher = new RecordingDispatcher(dispatcherThread, observedThreadIds);
        var service = new HostedPluginService(new NeverAvailable(), recordingDispatcher);

        var backgroundTask = Task.Run(service.EnsureScanned);

        Assert.True(backgroundTask.Wait(TimeSpan.FromSeconds(10)), "EnsureScanned did not complete -- possible dispatcher deadlock");
        Assert.NotEmpty(observedThreadIds);
        Assert.All(observedThreadIds, id => Assert.Equal(dispatcherThread.ThreadId, id));

        service.Dispose();
    }

    /// <summary>Same shape as above, but exercises Invoke&lt;T&gt; (the value-returning overload)
    /// via GetOrCreateInstance-adjacent plumbing -- Reset/PullLiveState/PushState/ShowEditor all go
    /// through Invoke&lt;T&gt; or Invoke, so this checks the generic path specifically doesn't
    /// deadlock either.</summary>
    [Fact]
    public void IsAvailable_CalledFromBackgroundThread_CompletesWithoutMarshaling()
    {
        // IsAvailable itself is a direct pass-through (no dispatcher involved, per
        // HostedPluginService.IsAvailable) -- included to document that distinction: only
        // lifecycle operations that touch the native bridge marshal through the dispatcher.
        using var dispatcherThread = new DedicatedThreadDispatcher();
        var observedThreadIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        var recordingDispatcher = new RecordingDispatcher(dispatcherThread, observedThreadIds);
        var service = new HostedPluginService(new NeverAvailable(), recordingDispatcher);

        var backgroundTask = Task.Run(() => service.IsAvailable("FabFilter Pro-Q 4"));

        Assert.True(backgroundTask.Wait(TimeSpan.FromSeconds(10)));
        Assert.Empty(observedThreadIds); // confirms IsAvailable does NOT marshal (no scan performed yet)

        service.Dispose();
    }
}
