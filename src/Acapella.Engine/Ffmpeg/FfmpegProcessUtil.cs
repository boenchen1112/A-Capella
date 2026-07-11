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
}
