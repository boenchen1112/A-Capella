using System.Diagnostics;
using Acapella.Engine.Ffmpeg;
using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>Decodes a single representative frame from a video file via an ffmpeg pipe (rgba raw, scaled to a fixed size).</summary>
public static class VideoFrameDecoder
{
    public static SKBitmap? DecodeFirstFrame(string mediaPath, int width, int height, string ffmpegPath = "ffmpeg")
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
        psi.ArgumentList.Add("-vframes"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgba");
        // Plain scale=w:h stretches the source to the cell's aspect ratio, distorting anything
        // that isn't already that shape (e.g. a 16:9 webcam feed into a non-16:9 cell). Letterbox
        // instead: scale down preserving aspect ratio, then pad the remainder with black.
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add($"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)!;
        FfmpegProcessUtil.DrainStderrInBackground(process);
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        var bytes = ms.ToArray();
        int expectedSize = width * height * 4;
        if (bytes.Length < expectedSize)
            return null; // no video stream (e.g. audio-only file) or decode failure

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), expectedSize);
        return bitmap;
    }
}
