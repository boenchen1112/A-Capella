namespace A_Capella.Engine.Capture;

/// <summary>The ffmpeg input-device flavour for this OS: dshow on Windows, avfoundation on macOS.</summary>
public static class CaptureInputFormat
{
    public static string Name => OperatingSystem.IsMacOS() ? "avfoundation" : "dshow";

    /// <summary>Arguments that make ffmpeg print its capture device list to stderr.</summary>
    public static string[] ListDevicesArguments() => OperatingSystem.IsMacOS()
        ? new[] { "-f", "avfoundation", "-list_devices", "true", "-i", "" }
        : new[] { "-f", "dshow", "-list_devices", "true", "-i", "dummy" };

    /// <summary>Input arguments for a camera + microphone pair on one clock.</summary>
    public static string[] VideoAudioArguments(string videoDeviceName, string audioDeviceName) => OperatingSystem.IsMacOS()
        // avfoundation needs an explicit framerate the camera supports; 30 is universal for built-in cameras.
        ? new[] { "-f", "avfoundation", "-framerate", "30", "-i", $"{videoDeviceName}:{audioDeviceName}" }
        : new[] { "-rtbufsize", "256M", "-f", "dshow", "-i", $"video={videoDeviceName}:audio={audioDeviceName}" };

    public static string[] AudioOnlyArguments(string audioDeviceName) => OperatingSystem.IsMacOS()
        ? new[] { "-f", "avfoundation", "-i", $":{audioDeviceName}" }
        : new[] { "-rtbufsize", "256M", "-f", "dshow", "-i", $"audio={audioDeviceName}" };
}
