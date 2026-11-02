using System.Runtime.Versioning;
using A_Capella.Engine.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace A_Capella.Engine.Audio;

[SupportedOSPlatform("windows")]
internal static class WasapiBackend
{
    public static List<AudioDeviceInfo> GetRenderDevices() => Enumerate(DataFlow.Render);

    public static List<AudioDeviceInfo> GetCaptureDevices() => Enumerate(DataFlow.Capture);

    private static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    public static AudioDeviceInfo GetDefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new AudioDeviceInfo(device.ID, device.FriendlyName);
    }

    public sealed class Output : IAudioOutput
    {
        private readonly string? _deviceId;
        private WasapiOut? _output;

        public Output(string? deviceId) => _deviceId = deviceId;

        public void Init(ISampleProvider source)
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = _deviceId is not null
                ? enumerator.GetDevice(_deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _output = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
            _output.Init(source);
        }

        public void Play() => _output?.Play();

        public void Stop() => _output?.Stop();

        public void Dispose()
        {
            _output?.Dispose();
            _output = null;
        }
    }

    public sealed class Input : IAudioInput
    {
        private readonly WasapiCapture _capture;

        public Input(string deviceId)
        {
            using var enumerator = new MMDeviceEnumerator();
            _capture = new WasapiCapture(enumerator.GetDevice(deviceId));
            _capture.DataAvailable += (s, e) => DataAvailable?.Invoke(this, e);
        }

        public WaveFormat WaveFormat => _capture.WaveFormat;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public void StartRecording() => _capture.StartRecording();
        public void StopRecording() => _capture.StopRecording();
        public void Dispose() => _capture.Dispose();
    }
}
