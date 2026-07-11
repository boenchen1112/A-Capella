using System.Diagnostics;

namespace Acapella.Engine.Capture;

/// <summary>
/// Wraps one ffmpeg process capturing a single dshow input line combining a video device and an
/// audio device, so both streams share one clock (prevents within-layer lip-sync drift).
/// </summary>
public class FfmpegCaptureSession : IDisposable
{
    private readonly string _ffmpegPath;
    private Process? _process;

    public FfmpegCaptureSession(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
    }

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string videoDeviceName, string audioDeviceName, string outputPath)
    {
        if (IsRunning)
            throw new InvalidOperationException("Capture already running.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("dshow");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"video={videoDeviceName}:audio={audioDeviceName}");
        psi.ArgumentList.Add(outputPath);

        _process = Process.Start(psi);
    }

    /// <summary>
    /// Starts an audio-only capture (no video device) — used for hardware-in-loop sync checks.
    /// </summary>
    public void StartAudioOnly(string audioDeviceName, string outputPath)
    {
        if (IsRunning)
            throw new InvalidOperationException("Capture already running.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("dshow");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"audio={audioDeviceName}");
        psi.ArgumentList.Add(outputPath);

        _process = Process.Start(psi);
    }

    public void Stop()
    {
        if (_process is null || _process.HasExited)
            return;

        _process.StandardInput.Write("q");
        _process.StandardInput.Flush();
        if (!_process.WaitForExit(5000))
            _process.Kill();
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
