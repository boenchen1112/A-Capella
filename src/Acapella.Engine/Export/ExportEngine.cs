using System.Diagnostics;
using Acapella.Engine.Composite;
using Acapella.Engine.Ffmpeg;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Acapella.Engine.Timeline;
using SkiaSharp;

namespace Acapella.Engine.Export;

/// <summary>
/// Mixes down all layers (Phase 3's fixed chain) and composites the 2x2 grid (Phase 4) across
/// the full duration, then muxes both into a single H.264+AAC .mp4 via ffmpeg.
/// </summary>
public class ExportEngine : IDisposable
{
    private readonly MixEngine _mixEngine;
    private readonly LayerTimeline _timeline;
    private readonly string _ffmpegPath;

    /// <summary>hostedService, when given, is the same shared service MainWindow's launcher UI
    /// and PreviewPlaybackEngine use (v7 Q0 task 1, audit A1/B1): export reuses/reads the live
    /// instances (reset before processing, per A5) rather than instantiating a stale parallel set
    /// from the DTO, so export sounds exactly like the preview. Pass hostedPluginAvailability
    /// instead only for isolated tests/callers that don't need that sharing (defaults to real
    /// detection so exports use the same FabFilter-when-detected backend as the live preview;
    /// pass NoHostedPluginsAvailable.Instance to force pure native processing).</summary>
    public ExportEngine(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe", IHostedPluginAvailability? hostedPluginAvailability = null, HostedPluginService? hostedService = null)
    {
        _ffmpegPath = ffmpegPath;
        _mixEngine = hostedService is not null ? new MixEngine(hostedService) : new MixEngine(hostedPluginAvailability);
        _timeline = new LayerTimeline(_mixEngine, ffmpegPath, ffprobePath);
    }

    public void Export(LayerCollection layers, string outputPath, int width = 1280, int height = 720, int fps = 30, int sampleRate = 44100, float masterVolumeDb = 0f)
    {
        if (layers.Layers.Count == 0)
            throw new InvalidOperationException("No layers to export.");

        var orderedLayers = LayerTimeline.InCellOrder(layers.Layers);

        double durationSeconds = _timeline.DurationMs(layers.Layers, sampleRate) / 1000.0;
        int maxSamples = (int)Math.Round(durationSeconds * sampleRate);
        if (maxSamples == 0)
            throw new InvalidOperationException("No decodable media found in any layer.");

        var mixInputs = layers.Layers.Select(l => _timeline.AudioInput(l, sampleRate)).ToList();
        var mix = _mixEngine.BuildMix(mixInputs, sampleRate, masterVolumeDb);

        var mixedSamples = new float[maxSamples * 2]; // interleaved stereo
        int totalRead = 0;
        while (totalRead < mixedSamples.Length)
        {
            int n = mix.Read(mixedSamples, totalRead, mixedSamples.Length - totalRead);
            if (n == 0) break;
            totalRead += n;
        }

        string tempWavPath = Path.Combine(Path.GetTempPath(), $"acapella-export-audio-{Guid.NewGuid()}.wav");
        WavFileWriter.WriteStereoFloat(tempWavPath, mixedSamples, sampleRate);

        try
        {
            int cellWidth = width / 2;
            int cellHeight = height / 2;

            var frameSources = orderedLayers
                .Select(l => _timeline.FrameSource(l, cellWidth, cellHeight, fps))
                .ToList();

            try
            {
                var cellRects = Layout2x2Provider.GetCellRects(width, height, orderedLayers.Count);
                int totalFrames = (int)Math.Ceiling(durationSeconds * fps);

                using var encodeProcess = StartEncodeProcess(outputPath, width, height, fps, tempWavPath);
                var stdin = encodeProcess.StandardInput.BaseStream;
                var stderrTail = FfmpegProcessUtil.DrainStderrKeepingTail(encodeProcess);

                // L3: hoisted out of the per-frame loop -- every composited frame is the same
                // fixed size, so there's no need to allocate a new byte[] on every iteration.
                var pixelBuffer = new byte[width * height * 4];

                for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
                {
                    var frames = frameSources.Select(s => s.GetNextFrame()).ToList();
                    using var composite = Compositor.Composite(width, height, frames, cellRects);
                    WriteBitmapPixels(stdin, composite, pixelBuffer);
                }

                stdin.Close();
                encodeProcess.WaitForExit();

                if (encodeProcess.ExitCode != 0)
                    throw new InvalidOperationException($"ffmpeg export failed (exit {encodeProcess.ExitCode}):\n{string.Join('\n', stderrTail.GetLines())}");
            }
            finally
            {
                foreach (var source in frameSources)
                    source.Dispose();
            }
        }
        finally
        {
            if (File.Exists(tempWavPath))
                File.Delete(tempWavPath);
        }
    }

    private static void WriteBitmapPixels(Stream stdin, SKBitmap bitmap, byte[] buffer)
    {
        // L3: the raw copy assumes rows are tightly packed (RowBytes == width*4); true today for
        // every Rgba8888 bitmap this pipeline creates, but assert it so that assumption is
        // explicit instead of silently corrupting frames if that ever changes.
        int expectedRowBytes = bitmap.Width * 4;
        if (bitmap.RowBytes != expectedRowBytes)
            throw new InvalidOperationException($"Expected tightly packed Rgba8888 rows ({expectedRowBytes} bytes), got RowBytes={bitmap.RowBytes}.");

        var pixels = bitmap.GetPixels();
        int byteCount = bitmap.ByteCount;
        System.Runtime.InteropServices.Marshal.Copy(pixels, buffer, 0, byteCount);
        stdin.Write(buffer, 0, byteCount);
    }

    private Process StartEncodeProcess(string outputPath, int width, int height, int fps, string audioWavPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add("-s"); psi.ArgumentList.Add($"{width}x{height}");
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(fps.ToString());
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioWavPath);
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add("-shortest");
        psi.ArgumentList.Add(outputPath);

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg encode process.");
    }

    public void Dispose() => _mixEngine.Dispose();
}
