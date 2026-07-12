using System.Diagnostics;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace Acapella.Engine.Devices;

public record AudioDeviceInfo(string Id, string Name);
public record DshowDeviceInfo(string Name, bool IsVideo);

public class DeviceCatalog
{
    private readonly string _ffmpegPath;

    public DeviceCatalog(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
    }

    public List<AudioDeviceInfo> GetWasapiRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    public List<AudioDeviceInfo> GetWasapiCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    /// <summary>
    /// The redesigned UI (see UI_Design_Spec.md) has no output-device picker anywhere -- the
    /// Record dialog only exposes camera/mic, and there's no other device UI outside it. Guide
    /// playback, latency calibration, and audio preview all resolve to the system's default
    /// render endpoint instead of a user-chosen one.
    /// </summary>
    public AudioDeviceInfo GetDefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new AudioDeviceInfo(device.ID, device.FriendlyName);
    }

    /// <summary>
    /// Lists dshow devices (video and audio) by parsing `ffmpeg -f dshow -list_devices true -i dummy` stderr output.
    /// </summary>
    public List<DshowDeviceInfo> GetDshowDevices()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            ArgumentList = { "-f", "dshow", "-list_devices", "true", "-i", "dummy" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var devices = new List<DshowDeviceInfo>();
        var regex = new Regex("\"(?<name>[^\"]+)\"\\s*\\((?<kind>video|audio)\\)");
        foreach (Match match in regex.Matches(stderr))
        {
            devices.Add(new DshowDeviceInfo(
                match.Groups["name"].Value,
                match.Groups["kind"].Value == "video"));
        }

        return devices;
    }
}
