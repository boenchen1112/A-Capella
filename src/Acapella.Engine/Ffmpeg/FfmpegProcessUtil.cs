using System.Diagnostics;

namespace Acapella.Engine.Ffmpeg;

public static class FfmpegProcessUtil
{
    /// <summary>
    /// Continuously reads and discards a process's stderr on a background thread. Any ffmpeg
    /// process that runs long enough or logs enough (real video files produce far more stderr
    /// output than tiny synthetic test clips) can fill the OS pipe buffer and block the child
    /// process if nothing drains it -- which then blocks whatever pipe we're actually reading
    /// (stdout) or writing (stdin) on our side too. Every ffmpeg process this codebase spawns with
    /// RedirectStandardError = true must call this. Fire-and-forget by design; the process's own
    /// lifetime bounds this thread's lifetime.
    /// </summary>
    public static void DrainStderrInBackground(Process process)
    {
        var stderr = process.StandardError;
        _ = Task.Run(() =>
        {
            var buffer = new char[4096];
            try
            {
                while (stderr.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch { /* process exited/stream closed */ }
        });
    }

    /// <summary>
    /// Same draining guarantee as <see cref="DrainStderrInBackground"/>, but retains the last
    /// <paramref name="maxLines"/> lines so a caller can surface diagnostics (e.g. ffmpeg's
    /// "real-time buffer too full / frame dropped" warnings during live capture) after the
    /// process exits, instead of discarding everything.
    /// </summary>
    public static RecentStderrBuffer DrainStderrKeepingTail(Process process, int maxLines = 50)
    {
        var tail = new RecentStderrBuffer(maxLines);
        var stderr = process.StandardError;
        _ = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = stderr.ReadLine()) is not null)
                    tail.Add(line);
            }
            catch { /* process exited/stream closed */ }
        });
        return tail;
    }
}

public class RecentStderrBuffer
{
    private readonly object _lock = new();
    private readonly Queue<string> _lines;
    private readonly int _maxLines;

    public RecentStderrBuffer(int maxLines)
    {
        _maxLines = maxLines;
        _lines = new Queue<string>(maxLines);
    }

    public void Add(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _maxLines)
                _lines.Dequeue();
        }
    }

    public string[] GetLines()
    {
        lock (_lock)
            return _lines.ToArray();
    }
}
