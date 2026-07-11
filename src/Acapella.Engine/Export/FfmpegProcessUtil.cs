using System.Diagnostics;

namespace Acapella.Engine.Export;

public static class FfmpegProcessUtil
{
    /// <summary>
    /// Continuously reads and discards a process's stderr on a background thread. Export
    /// processes run long enough (multi-frame encode/decode) that ffmpeg's stderr log output can
    /// fill the OS pipe buffer and block the child process if nothing drains it -- which would in
    /// turn block our own stdin writes on the encode process. Fire-and-forget by design; the
    /// process's own lifetime (and Dispose on the owning stream) bounds this thread's lifetime.
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
