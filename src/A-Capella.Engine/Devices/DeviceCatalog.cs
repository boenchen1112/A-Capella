using System.Diagnostics;
using System.Text.RegularExpressions;
using A_Capella.Engine.Audio;
using A_Capella.Engine.Capture;

namespace A_Capella.Engine.Devices;

public record AudioDeviceInfo(string Id, string Name);

/// <summary>A camera or microphone as ffmpeg's capture input names it (dshow on Windows,
/// avfoundation on macOS). The name is what gets passed back to ffmpeg.</summary>
public record CaptureDeviceInfo(string Name, bool IsVideo);

public class DeviceCatalog
{
    private readonly string _ffmpegPath;

    public DeviceCatalog(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
    }

    public List<AudioDeviceInfo> GetRenderDevices() => AudioBackend.GetRenderDevices();

    public List<AudioDeviceInfo> GetCaptureDevices() => AudioBackend.GetCaptureDevices();

    /// <summary>
    /// The UI has no output-device picker anywhere -- the
    /// Record dialog only exposes camera/mic, and there's no other device UI outside it. Guide
    /// playback, latency calibration, and audio preview all resolve to the system's default
    /// render endpoint instead of a user-chosen one.
    /// </summary>
    public AudioDeviceInfo GetDefaultRenderDevice() => AudioBackend.GetDefaultRenderDevice();

    /// <summary>
    /// Lists cameras and microphones by parsing ffmpeg's device-listing stderr output
    /// (`-f dshow -list_devices true -i dummy` on Windows, `-f avfoundation -list_devices true -i ""`
    /// on macOS).
    /// </summary>
    public List<CaptureDeviceInfo> GetCaptureInputDevices()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in CaptureInputFormat.ListDevicesArguments())
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return ParseDeviceList(stderr);
    }

    internal static List<CaptureDeviceInfo> ParseDeviceList(string stderr)
    {
        var devices = new List<CaptureDeviceInfo>();

        if (OperatingSystem.IsMacOS())
        {
            // "[AVFoundation indev @ 0x...] AVFoundation video devices:" then "... [0] FaceTime HD Camera",
            // then the same for audio. Screen captures are listed among the video devices; not cameras.
            bool? isVideo = null;
            var item = new Regex(@"\]\s*\[(?<index>\d+)\]\s*(?<name>.+?)\s*$");
            foreach (var line in stderr.Split('\n'))
            {
                if (line.Contains("AVFoundation video devices", StringComparison.OrdinalIgnoreCase)) { isVideo = true; continue; }
                if (line.Contains("AVFoundation audio devices", StringComparison.OrdinalIgnoreCase)) { isVideo = false; continue; }
                if (isVideo is null) continue;
                var m = item.Match(line);
                if (!m.Success) continue;
                string name = m.Groups["name"].Value;
                if (isVideo.Value && name.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase)) continue;
                devices.Add(new CaptureDeviceInfo(name, isVideo.Value));
            }
            return devices;
        }

        var regex = new Regex("\"(?<name>[^\"]+)\"\\s*\\((?<kind>video|audio)\\)");
        foreach (Match match in regex.Matches(stderr))
        {
            devices.Add(new CaptureDeviceInfo(
                match.Groups["name"].Value,
                match.Groups["kind"].Value == "video"));
        }

        return devices;
    }
}
