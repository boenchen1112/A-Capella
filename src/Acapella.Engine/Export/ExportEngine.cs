using System.Diagnostics;
using Acapella.Engine.Composite;
using Acapella.Engine.Ffmpeg;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using SkiaSharp;

namespace Acapella.Engine.Export;

/// <summary>
/// Mixes down all layers (Phase 3's fixed chain) and composites the 2x2 grid (Phase 4) across
/// the full duration, then muxes both into a single H.264+AAC .mp4 via ffmpeg.
/// </summary>
public class ExportEngine : IDisposable
{
    private readonly MixEngine _mixEngine;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

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
        _ffprobePath = ffprobePath;
        _mixEngine = hostedService is not null ? new MixEngine(hostedService) : new MixEngine(hostedPluginAvailability);
    }

    public void Export(LayerCollection layers, string outputPath, int width = 1280, int height = 720, int fps = 30, int sampleRate = 44100, float masterVolumeDb = 0f)
    {
        if (layers.Layers.Count == 0)
            throw new InvalidOperationException("No layers to export.");

        // Sorted by CellIndex (audit B8), not LayerCollection's internal list order: a layer
        // attached to sidebar row 3 before row 2 must still land in grid cell 3 on export, matching
        // PreviewPlaybackEngine.SetLayersCore's same ordering fix.
        var orderedLayers = layers.Layers.OrderBy(l => l.CellIndex).ToList();

        var decodedAudio = layers.Layers.ToDictionary(
            l => l.LayerId,
            l => AudioShiftHelper.ApplyShift(
                TrimHelper.ApplyTrim(Mix.AudioDecodeCache.GetOrDecode(l.SourcePath, sampleRate, _ffmpegPath), l.TrimStartMs, l.TrimEndMs, sampleRate),
                l.GetShiftMs(), sampleRate));

        // Duration from ffprobe's container duration (audit A5), not decoded-audio length: a
        // video-only layer decodes to zero audio samples but still has real video duration, and
        // a video stream that outlasts its own audio stream (common with dshow captures stopped
        // mid-frame) would otherwise truncate the export to the shorter audio length.
        double durationSeconds = layers.Layers
            .Select(l => LayerDurationSeconds(l, sampleRate))
            .DefaultIfEmpty(0)
            .Max();
        int maxSamples = (int)Math.Round(durationSeconds * sampleRate);
        if (maxSamples == 0)
            throw new InvalidOperationException("No decodable media found in any layer.");

        var mixInputs = layers.Layers
            .Select(l => new MixLayerInput(l.LayerId, decodedAudio[l.LayerId], sampleRate, l.MixParameters, l.SourceCacheKey()))
            .ToList();
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
                .Select(l => CreateFrameSource(l, cellWidth, cellHeight, fps))
                .ToList();

            try
            {
                var cellRects = Layout2x2Provider.GetCellRects(width, height, orderedLayers.Count);
                int totalFrames = (int)Math.Ceiling(durationSeconds * fps);

                using var encodeProcess = StartEncodeProcess(outputPath, width, height, fps, tempWavPath);
                var stdin = encodeProcess.StandardInput.BaseStream;
                FfmpegProcessUtil.DrainStderrInBackground(encodeProcess);

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
                    throw new InvalidOperationException($"ffmpeg export failed with exit code {encodeProcess.ExitCode}.");
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

    /// <summary>Mirrors PreviewPlaybackEngine.LayerDurationMs -- trim/shift applied to the
    /// ffprobe'd container duration rather than a decoded sample count, so it works for
    /// video-only layers (see A5). Extended by the layer's own reverb tail (Q2 task 3), same
    /// reasoning as the preview side: without this, export's maxSamples calc would truncate the
    /// mixdown before a Pro-R 2 decay finishes.</summary>
    private double LayerDurationSeconds(LayerModel layer, int sampleRate)
    {
        double rawMs = Ffmpeg.MediaProbe.GetDurationSeconds(layer.SourcePath, _ffprobePath) * 1000.0;
        double trimEndMs = Math.Min(layer.TrimEndMs ?? rawMs, rawMs);
        double trimmedMs = Math.Max(0, trimEndMs - layer.TrimStartMs);

        double shiftMs = layer.GetShiftMs();
        double totalMs = shiftMs >= 0 ? trimmedMs + shiftMs : Math.Max(0, trimmedMs + shiftMs);

        double tailSeconds = _mixEngine.GetReverbTailSeconds(layer.LayerId, layer.MixParameters, sampleRate);
        return totalMs / 1000.0 + tailSeconds;
    }

    private ILayerFrameSource CreateFrameSource(LayerModel layer, int cellWidth, int cellHeight, int fps)
    {
        if (layer.Kind == LayerKind.UploadedAudioOnly)
            return new StaticFrameSource(PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight));

        return new VideoFrameStreamSource(layer.SourcePath, cellWidth, cellHeight, fps, layer.GetShiftMs(), _ffmpegPath, layer.TrimStartMs, layer.TrimEndMs);
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
