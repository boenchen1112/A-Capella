using System.Diagnostics;
using Acapella.Engine.Ffmpeg;

namespace Acapella.Engine.Capture;

/// <summary>
/// Wraps one ffmpeg process capturing a single dshow input line combining a video device and an
/// audio device, so both streams share one clock (prevents within-layer lip-sync drift).
/// </summary>
public class FfmpegCaptureSession : IDisposable
{
    private readonly string _ffmpegPath;
    private Process? _process;
    private RecentStderrBuffer? _stderrTail;

    public FfmpegCaptureSession(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Recent ffmpeg stderr lines from the capture that just ran, e.g. "real-time buffer
    /// too full / frame dropped" warnings -- available after Stop() for surfacing diagnostics.</summary>
    public string[] GetRecentStderrLines() => _stderrTail?.GetLines() ?? Array.Empty<string>();

    /// <summary>Blocks until ffmpeg's stderr shows the first per-frame progress line (capture is
    /// actually producing frames), or the timeout elapses. dshow device init after Start()
    /// returns takes 0.5-2s and is variable (audit B4); a caller that starts guide-track playback
    /// immediately after Start() returns -- rather than after this -- has the recorded file's t=0
    /// begin at an unmeasured, variable time after the guide already started, breaking cross-layer
    /// sync alignment differently every take. Returns false (caller should proceed anyway rather
    /// than hang indefinitely) if no progress line appears before the timeout.</summary>
    public bool WaitForCaptureStarted(TimeSpan timeout) => _stderrTail?.CaptureStarted.Wait(timeout) ?? false;

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
        // A live dshow feed (720p+ webcam) can outrun the default real-time buffer and encoder
        // preset, causing ffmpeg to silently drop frames ("real-time buffer too full") -- with
        // stderr previously discarded entirely, that would have been invisible. -rtbufsize gives
        // headroom to absorb bursts; ultrafast/aac keep the encoder ahead of the live feed.
        psi.ArgumentList.Add("-rtbufsize");
        psi.ArgumentList.Add("256M");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("dshow");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"video={videoDeviceName}:audio={audioDeviceName}");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("23");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add(outputPath);

        _process = Process.Start(psi);
        if (_process is not null)
            _stderrTail = FfmpegProcessUtil.DrainStderrKeepingTail(_process);
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
        psi.ArgumentList.Add("-rtbufsize");
        psi.ArgumentList.Add("256M");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("dshow");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"audio={audioDeviceName}");
        psi.ArgumentList.Add(outputPath);

        _process = Process.Start(psi);
        if (_process is not null)
            _stderrTail = FfmpegProcessUtil.DrainStderrKeepingTail(_process);
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
