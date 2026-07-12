using System.Diagnostics;
using System.Globalization;

namespace Acapella.Engine.Ffmpeg;

/// <summary>Quick ffprobe-based sanity check for a just-recorded file.</summary>
public static class MediaProbe
{
    public static bool HasNonzeroDuration(string mediaPath, string ffprobePath = "ffprobe") =>
        GetDurationSeconds(mediaPath, ffprobePath) > 0;

    /// <summary>Container duration in seconds via ffprobe's format=duration, or 0 if unavailable.
    /// This is the container's overall duration -- correct for video-only, audio-only, and mixed
    /// files alike -- so callers that just need "how long is this layer" (audit A5) can use this
    /// instead of fully decoding audio just to measure its length.</summary>
    public static double GetDurationSeconds(string mediaPath, string ffprobePath = "ffprobe")
    {
        if (!File.Exists(mediaPath))
            return 0;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(mediaPath);

        using var process = Process.Start(psi);
        if (process is null)
            return 0;

        FfmpegProcessUtil.DrainStderrInBackground(process);
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) && duration > 0
            ? duration
            : 0;
    }
}
