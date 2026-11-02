using A_Capella.Engine.Devices;
using NAudio.Wave;

namespace A_Capella.Engine.Audio;

/// <summary>Plays a sample stream through an output device.</summary>
public interface IAudioOutput : IDisposable
{
    void Init(ISampleProvider source);
    void Play();
    void Stop();
}

/// <summary>Captures raw PCM from an input device.</summary>
public interface IAudioInput : IDisposable
{
    WaveFormat WaveFormat { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    void StartRecording();
    void StopRecording();
}

/// <summary>
/// Platform audio I/O. Windows uses WASAPI (shared mode) via NAudio; macOS (and anything else)
/// uses the system OpenAL, which NAudio has no backend for. Device ids are WASAPI endpoint ids on
/// Windows and OpenAL device names elsewhere; callers treat them as opaque strings from
/// <see cref="GetRenderDevices"/>/<see cref="GetCaptureDevices"/>.
/// </summary>
public static class AudioBackend
{
    public static List<AudioDeviceInfo> GetRenderDevices() =>
        OperatingSystem.IsWindows() ? WasapiBackend.GetRenderDevices() : OpenAlBackend.GetRenderDevices();

    public static List<AudioDeviceInfo> GetCaptureDevices() =>
        OperatingSystem.IsWindows() ? WasapiBackend.GetCaptureDevices() : OpenAlBackend.GetCaptureDevices();

    public static AudioDeviceInfo GetDefaultRenderDevice() =>
        OperatingSystem.IsWindows() ? WasapiBackend.GetDefaultRenderDevice() : OpenAlBackend.GetDefaultRenderDevice();

    /// <summary>Opens an output on <paramref name="deviceId"/>, or the system default when null.
    /// Throws if the id doesn't name a present device.</summary>
    public static IAudioOutput CreateOutput(string? deviceId) =>
        OperatingSystem.IsWindows() ? new WasapiBackend.Output(deviceId) : new OpenAlBackend.Output(deviceId);

    public static IAudioInput CreateInput(string deviceId) =>
        OperatingSystem.IsWindows() ? new WasapiBackend.Input(deviceId) : new OpenAlBackend.Input(deviceId);
}
