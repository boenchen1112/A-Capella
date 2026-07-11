namespace Acapella.Engine.Settings;

public class AppSettings
{
    public Dictionary<string, double> LatencyOffsetsMs { get; set; } = new();

    public static string DevicePairKey(string inputDeviceId, string outputDeviceId)
        => $"{inputDeviceId}|{outputDeviceId}";
}
