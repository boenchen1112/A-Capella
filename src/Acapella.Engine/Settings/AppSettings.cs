namespace Acapella.Engine.Settings;

public class AppSettings
{
    public Dictionary<string, double> LatencyOffsetsMs { get; set; } = new();

    /// <summary>Track panel dock side, "Left" or "Right". Additive field for the redesigned UI
    /// (see UI_Design_Spec.md) -- missing on older settings files just falls back to the default.</summary>
    public string TrackPanelDock { get; set; } = "Right";

    public static string DevicePairKey(string inputDeviceId, string outputDeviceId)
        => $"{inputDeviceId}|{outputDeviceId}";
}
