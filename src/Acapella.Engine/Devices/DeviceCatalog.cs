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
