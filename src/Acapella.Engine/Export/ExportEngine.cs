using System.Diagnostics;
using Acapella.Engine.Composite;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using SkiaSharp;

namespace Acapella.Engine.Export;

/// <summary>
/// Mixes down all layers (Phase 3's fixed chain) and composites the 2x2 grid (Phase 4) across
/// the full duration, then muxes both into a single H.264+AAC .mp4 via ffmpeg.
/// </summary>
public class ExportEngine
{
    private readonly MixEngine _mixEngine = new();
    private readonly string _ffmpegPath;

    public ExportEngine(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
    }

    public void Export(LayerCollection layers, string outputPath, int width = 1280, int height = 720, int fps = 30, int sampleRate = 44100)
    {
        if (layers.Layers.Count == 0)
            throw new InvalidOperationException("No layers to export.");

        var decodedAudio = layers.Layers.ToDictionary(
            l => l.LayerId,
            l => Mix.AudioDecoder.DecodeToMonoFloat(l.SourcePath, sampleRate, _ffmpegPath));

        int maxSamples = decodedAudio.Values.Max(a => a.Length);
        if (maxSamples == 0)
            throw new InvalidOperationException("No decodable audio found in any layer.");
        double durationSeconds = maxSamples / (double)sampleRate;

        var mixInputs = layers.Layers
            .Select(l => new MixLayerInput(l.LayerId, decodedAudio[l.LayerId], sampleRate, l.MixParameters))
            .ToList();
        var mix = _mixEngine.BuildMix(mixInputs, sampleRate);

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

            var frameSources = layers.Layers
                .Select(l => CreateFrameSource(l, cellWidth, cellHeight, fps))
                .ToList();

            try
            {
                var cellRects = Layout2x2Provider.GetCellRects(width, height, layers.Layers.Count);
                int totalFrames = (int)Math.Ceiling(durationSeconds * fps);

                using var encodeProcess = StartEncodeProcess(outputPath, width, height, fps, tempWavPath);
                var stdin = encodeProcess.StandardInput.BaseStream;
                FfmpegProcessUtil.DrainStderrInBackground(encodeProcess);

                for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
                {
                    var frames = frameSources.Select(s => s.GetNextFrame()).ToList();
                    using var composite = Compositor.Composite(width, height, frames, cellRects);
                    WriteBitmapPixels(stdin, composite);
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

    private ILayerFrameSource CreateFrameSource(LayerModel layer, int cellWidth, int cellHeight, int fps)
    {
        if (layer.Kind == LayerKind.UploadedAudioOnly)
            return new StaticFrameSource(PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight));

        return new VideoFrameStreamSource(layer.SourcePath, cellWidth, cellHeight, fps, _ffmpegPath);
    }

    private static void WriteBitmapPixels(Stream stdin, SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixels();
        int byteCount = bitmap.ByteCount;
        var buffer = new byte[byteCount];
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
}
