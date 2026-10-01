using System.Diagnostics;
using Acapella.Engine.Ffmpeg;

namespace Acapella.Engine.Mix;

/// <summary>Decodes any ffmpeg-readable media file's audio track to mono float samples via a pipe.</summary>
public static class AudioDecoder
{
    public static float[] DecodeToMonoFloat(string mediaPath, int sampleRate, string ffmpegPath = "ffmpeg")
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

        // Note: a nonzero exit code here is not necessarily a real failure -- a video-only layer
        // (no audio stream to map) legitimately makes ffmpeg exit nonzero while still being a
        // valid, expected input (see ExportEngine's video-only layer handling). Zero decoded bytes
        // is the layer's normal "no audio" case, not surfaced as an error.
        var bytes = ms.ToArray();
        int sampleCount = bytes.Length / 4;
        var samples = new float[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * 4);
        return samples;
    }
}
