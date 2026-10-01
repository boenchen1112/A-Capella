namespace Acapella.Engine.Settings;

public class AppSettings
{
    public Dictionary<string, double> LatencyOffsetsMs { get; set; } = new();

    /// <summary>Candidate 4 (Improvement_Proposal_2026-09-24): the last-used recording setup,
    /// matched by dshow device name (stable across reboots, unlike index order -- see
    /// FfmpegCaptureSession's video={name}:audio={name} arguments) rather than by index. Null names
    /// mean "no prior recording in this app install" -- RecordSetupWindow falls back to index 0.</summary>
    public string? LastCameraName { get; set; }
    public string? LastMicName { get; set; }
    public bool LastMetronomeEnabled { get; set; }

    public static string DevicePairKey(string inputDeviceId, string outputDeviceId)
        => $"{inputDeviceId}|{outputDeviceId}";
}
