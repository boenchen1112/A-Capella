using System.Diagnostics;
using System.Runtime.InteropServices;
using Acapella.Engine.Ffmpeg;
using SkiaSharp;

namespace Acapella.Engine.Export;

public interface ILayerFrameSource : IDisposable
{
    SKBitmap GetNextFrame();
}

/// <summary>Streams decoded video frames from a layer's media file one at a time via an ffmpeg
/// pipe, freezing on the last decoded frame once the source is exhausted (so a shorter layer
/// doesn't blank out while other layers continue).</summary>
public class VideoFrameStreamSource : ILayerFrameSource
{
    private readonly Process? _process;
    private readonly Stream? _stdout;
    private readonly byte[] _frameBuffer;
    private readonly int _width;
    private readonly int _height;
    private SKBitmap _lastFrame;
    private bool _exhausted;
    private int _framesToHold;

    /// <summary>
    /// shiftMs mirrors the same sync shift applied to this layer's audio (see
    /// LayerModel.GetShiftMs): negative skips ahead into the layer's own footage (-ss before
    /// -i), positive holds the initial placeholder frame for the equivalent number of frames
    /// before decoding starts, so video stays aligned with its own layer's shifted audio.
    /// </summary>
    public VideoFrameStreamSource(string mediaPath, int width, int height, int fps, double shiftMs = 0, string ffmpegPath = "ffmpeg")
    {
        _width = width;
        _height = height;
        _frameBuffer = new byte[width * height * 4];
        _lastFrame = CreateSolidFrame(width, height, SKColors.Black);

        if (shiftMs > 0)
            _framesToHold = (int)Math.Round(shiftMs / 1000.0 * fps);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (shiftMs < 0)
        {
            double skipSeconds = -shiftMs / 1000.0;
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(skipSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add($"scale={width}:{height},fps={fps}");
        psi.ArgumentList.Add("-");

        _process = Process.Start(psi);
        if (_process is not null)
        {
            _stdout = _process.StandardOutput.BaseStream;
            FfmpegProcessUtil.DrainStderrInBackground(_process);
        }
    }

    public SKBitmap GetNextFrame()
    {
        if (_framesToHold > 0)
        {
            _framesToHold--;
            return _lastFrame;
        }

        if (!_exhausted && _stdout is not null)
        {
            int totalRead = 0;
            while (totalRead < _frameBuffer.Length)
            {
                int n = _stdout.Read(_frameBuffer, totalRead, _frameBuffer.Length - totalRead);
                if (n <= 0) { _exhausted = true; break; }
                totalRead += n;
            }

            if (totalRead == _frameBuffer.Length)
            {
                var bitmap = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                Marshal.Copy(_frameBuffer, 0, bitmap.GetPixels(), _frameBuffer.Length);
                _lastFrame.Dispose();
                _lastFrame = bitmap;
            }
            else
            {
                _exhausted = true;
            }
        }

        return _lastFrame;
    }

    private static SKBitmap CreateSolidFrame(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    public void Dispose()
    {
        _stdout?.Dispose();
        if (_process is { HasExited: false })
        {
            try { _process.Kill(); } catch { /* already exited */ }
        }
        _process?.Dispose();
        _lastFrame.Dispose();
    }
}

/// <summary>Returns the same placeholder frame every call -- used for audio-only layers.</summary>
public class StaticFrameSource : ILayerFrameSource
{
    private readonly SKBitmap _frame;

    public StaticFrameSource(SKBitmap frame) => _frame = frame;

    public SKBitmap GetNextFrame() => _frame;

    public void Dispose() => _frame.Dispose();
}
