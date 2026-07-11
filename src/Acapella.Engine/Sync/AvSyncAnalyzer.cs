using System.Diagnostics;
using Acapella.Engine.Ffmpeg;

namespace Acapella.Engine.Sync;

/// <summary>
/// Measures within-file audio/video sync by finding when the audio transient (via envelope
/// threshold crossing) and the video frame change (via luma threshold crossing) occur, using
/// ffmpeg to decode both streams to raw data piped over stdout — no GUI/hardware required.
/// </summary>
public static class AvSyncAnalyzer
{
    public static double GetAudioTransientSeconds(string mediaPath, int sampleRate = 44100, float threshold = 0.1f, string ffmpegPath = "ffmpeg")
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(sampleRate.ToString());
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)!;
        FfmpegProcessUtil.DrainStderrInBackground(process);
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        var bytes = ms.ToArray();
        int sampleCount = bytes.Length / 4;
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = BitConverter.ToSingle(bytes, i * 4);
            if (Math.Abs(sample) > threshold)
                return i / (double)sampleRate;
        }
        return -1;
    }

    public static double GetVideoFlashSeconds(string mediaPath, double frameRate = 30.0, byte threshold = 128, string ffmpegPath = "ffmpeg")
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("gray");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=64:64");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)!;
        FfmpegProcessUtil.DrainStderrInBackground(process);
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        var bytes = ms.ToArray();
        int frameSize = 64 * 64;
        int frameCount = bytes.Length / frameSize;
        for (int f = 0; f < frameCount; f++)
        {
            long sum = 0;
            for (int p = 0; p < frameSize; p++)
                sum += bytes[f * frameSize + p];
            double avgLuma = sum / (double)frameSize;
            if (avgLuma > threshold)
                return f / frameRate;
        }
        return -1;
    }
}
