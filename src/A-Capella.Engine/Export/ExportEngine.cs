using System.Diagnostics;
using A_Capella.Engine.Composite;
using A_Capella.Engine.Ffmpeg;
using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Project;
using A_Capella.Engine.Timeline;
using SkiaSharp;

namespace A_Capella.Engine.Export;

/// <summary>
/// Mixes down all layers (Phase 3's fixed chain) and composites the layout grid (Phase 4) across
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

    /// <summary>progress, when given, reports 0.0 once mixdown starts, 0.0..1.0 across the
    /// frame-compositing loop (about once per second of output, not per frame, so the UI thread
    /// isn't flooded), and exactly 1.0 once the encode process has exited successfully -- callers
    /// building a Progress&lt;double&gt; on the UI thread get every callback marshaled back there
    /// automatically with no extra Dispatcher.Invoke.
    ///
    /// rangeStartMs/rangeOutMs (project-wide song in/out): when given, only that span of the
    /// finished mix and grid is rendered -- the cut is made in project time, after the mix, so every
    /// layer is cut at the same instant regardless of its own trim/shift. Null (the default) exports
    /// the whole song, exactly as before either parameter existed. Both are clamped to
    /// [0, full duration]; an empty resulting span throws.</summary>
    public void Export(LayerCollection layers, string outputPath, int? width = null, int? height = null, int fps = 30, int sampleRate = 44100, float masterVolumeDb = 0f, IProgress<double>? progress = null, double? rangeStartMs = null, double? rangeOutMs = null, VideoSettings? video = null)
    {
        // No settings means the pre-layout behavior (2x2, no gap, letterboxed 16:9) so existing callers are unchanged.
        video ??= VideoSettings.Legacy(GridLayoutCatalog.LegacyId);
        var (defaultWidth, defaultHeight) = video.ExportSize;
        ExportCore(layers, outputPath, width ?? defaultWidth, height ?? defaultHeight, fps, sampleRate, masterVolumeDb, progress, video, rangeStartMs, rangeOutMs);
    }

    private void ExportCore(LayerCollection layers, string outputPath, int width, int height, int fps, int sampleRate, float masterVolumeDb, IProgress<double>? progress, VideoSettings video, double? rangeStartMs, double? rangeOutMs)
    {
        var layout = video.Layout;
        double gutterFraction = video.GutterFraction;
        using var style = new StyleRenderer(video.Style, width, height);
        if (layers.Layers.Count == 0)
            throw new InvalidOperationException("No layers to export.");

        progress?.Report(0.0);

        var orderedLayers = LayerTimeline.InCellOrder(layers.Layers);

        double fullDurationMs = _timeline.DurationMs(layers.Layers, sampleRate);
        double rangeStartMsClamped = Math.Clamp(rangeStartMs ?? 0, 0, fullDurationMs);
        double rangeOutMsClamped = Math.Clamp(rangeOutMs ?? fullDurationMs, rangeStartMsClamped, fullDurationMs);
        double durationSeconds = (rangeOutMsClamped - rangeStartMsClamped) / 1000.0;
        if (durationSeconds <= 0)
            throw new InvalidOperationException("Export range is empty.");

        int maxSamples = (int)Math.Round(durationSeconds * sampleRate);
        if (maxSamples == 0)
            throw new InvalidOperationException("No decodable media found in any layer.");

        var mixInputs = layers.Layers.Select(l => _timeline.AudioInput(l, sampleRate)).ToList();
        var mix = _mixEngine.BuildMix(mixInputs, sampleRate, masterVolumeDb);

        // Render the mix from its own start, discarding samples before rangeStartMs -- simplest
        // correct way to slice an ISampleProvider, which has no seek.
        int rangeStartSamples = (int)Math.Round(rangeStartMsClamped / 1000.0 * sampleRate);
        int floatsToSkip = rangeStartSamples * 2;
        if (floatsToSkip > 0)
        {
            var scratch = new float[Math.Min(floatsToSkip, 65536)];
            while (floatsToSkip > 0)
            {
                int n = mix.Read(scratch, 0, Math.Min(scratch.Length, floatsToSkip));
                if (n == 0) break;
                floatsToSkip -= n;
            }
        }

        var mixedSamples = new float[maxSamples * 2]; // interleaved stereo
        int totalRead = 0;
        while (totalRead < mixedSamples.Length)
        {
            int n = mix.Read(mixedSamples, totalRead, mixedSamples.Length - totalRead);
            if (n == 0) break;
            totalRead += n;
        }

        string tempWavPath = Path.Combine(Path.GetTempPath(), $"a-capella-export-audio-{Guid.NewGuid()}.wav");
        WavFileWriter.WriteStereoFloat(tempWavPath, mixedSamples, sampleRate);

        try
        {
            var frameSources = orderedLayers
                .Select(l =>
                {
                    var (cellWidth, cellHeight) = layout.GetCellPixelSize(l.CellIndex, width, height, gutterFraction);
                    return _timeline.FrameSource(l, cellWidth, cellHeight, fps, positionMs: rangeStartMsClamped, fit: video.Fit);
                })
                .ToList();

            try
            {
                // Grid-gap compaction fix: key rects by each layer's actual CellIndex, not by
                // position in orderedLayers -- a lone layer at CellIndex 1 (cell 0 empty) must
                // still land in the top-right cell, not get compacted into top-left.
                var cellRects = layout.GetCellRects(width, height, gutterFraction, orderedLayers.Select(l => l.CellIndex).ToList());
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
                    using var composite = Compositor.Composite(width, height, frames, cellRects, style: style);
                    WriteBitmapPixels(stdin, composite, pixelBuffer);

                    if (frameIndex % fps == 0 || frameIndex == totalFrames - 1)
                        progress?.Report((frameIndex + 1) / (double)totalFrames);
                }

                stdin.Close();
                encodeProcess.WaitForExit();

                if (encodeProcess.ExitCode != 0)
                    throw new InvalidOperationException($"ffmpeg export failed (exit {encodeProcess.ExitCode}):\n{string.Join('\n', stderrTail.GetLines())}");

                progress?.Report(1.0);
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
