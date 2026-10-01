using System.Text.Json;

namespace Acapella.Engine.Settings;

public class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Acapella", "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettings();

        var json = File.ReadAllText(_settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    public double? GetLatencyOffsetMs(string inputDeviceId, string outputDeviceId)
    {
        var settings = Load();
        var key = AppSettings.DevicePairKey(inputDeviceId, outputDeviceId);
        return settings.LatencyOffsetsMs.TryGetValue(key, out var value) ? value : null;
    }

    public void SetLatencyOffsetMs(string inputDeviceId, string outputDeviceId, double offsetMs)
    {
        var settings = Load();
        var key = AppSettings.DevicePairKey(inputDeviceId, outputDeviceId);
        settings.LatencyOffsetsMs[key] = offsetMs;
        Save(settings);
    }

    /// <summary>Candidate 4: the last camera/mic/metronome choice, so the next recording dialog
    /// (even across app launches) doesn't default back to device index 0.</summary>
    public (string? CameraName, string? MicName, bool MetronomeEnabled) GetLastRecordingSetup()
    {
        var settings = Load();
        return (settings.LastCameraName, settings.LastMicName, settings.LastMetronomeEnabled);
    }

    public void SetLastRecordingSetup(string? cameraName, string? micName, bool metronomeEnabled)
    {
        var settings = Load();
        settings.LastCameraName = cameraName;
        settings.LastMicName = micName;
        settings.LastMetronomeEnabled = metronomeEnabled;
        Save(settings);
    }
}
