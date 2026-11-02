using A_Capella.Engine.Devices;
using NAudio.Wave;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;
using Silk.NET.OpenAL.Extensions.EXT;
using Silk.NET.OpenAL.Extensions.EXT.Enumeration;
using AlCapture = Silk.NET.OpenAL.Extensions.EXT.Capture;

namespace A_Capella.Engine.Audio;

/// <summary>
/// OpenAL audio I/O for macOS (system OpenAL.framework). All outputs share one process-wide
/// context on the system default render device -- OpenAL's "current context" is global, so
/// per-output devices would fight over it; each output just gets its own source. Capture opens
/// its own capture device, so a specific microphone can be chosen.
/// </summary>
internal static unsafe class OpenAlBackend
{
    private static readonly object InitLock = new();
    private static ALContext? _alc;
    private static AL? _al;
    private static Device* _device;
    private static Context* _context;

    internal static ALContext Alc { get { EnsureContext(); return _alc!; } }
    internal static AL Al { get { EnsureContext(); return _al!; } }

    private static void EnsureContext()
    {
        if (_context != null) return;
        lock (InitLock)
        {
            if (_context != null) return;
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null)
                throw new InvalidOperationException("OpenAL: no audio output device available.");
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
        }
    }

    public static List<AudioDeviceInfo> GetRenderDevices()
    {
        EnsureContext();
        var names = new List<AudioDeviceInfo>();
        if (_alc!.TryGetExtension<EnumerateAll>(null, out var enumeration))
            foreach (var name in enumeration.GetStringList(GetEnumerateAllContextStringList.AllDevicesSpecifier))
                names.Add(new AudioDeviceInfo(name, name));
        if (names.Count == 0)
            names.Add(GetDefaultRenderDevice());
        return names;
    }

    public static List<AudioDeviceInfo> GetCaptureDevices()
    {
        EnsureContext();
        var names = new List<AudioDeviceInfo>();
        if (_alc!.TryGetExtension<CaptureEnumerationEnumeration>(null, out var capture))
            foreach (var name in capture.GetStringList(GetCaptureContextStringList.CaptureDeviceSpecifiers))
                names.Add(new AudioDeviceInfo(name, name));
        return names;
    }

    public static AudioDeviceInfo GetDefaultRenderDevice()
    {
        EnsureContext();
        string name = _alc!.GetContextProperty(_device, GetContextString.DeviceSpecifier) ?? "Default output";
        return new AudioDeviceInfo(name, name);
    }

    public sealed class Output : IAudioOutput
    {
        private const int BufferCount = 4;
        private const int BufferMs = 50;

        private readonly string? _deviceId;
        private ISampleProvider? _source;
        private uint _sourceId;
        private uint[] _buffers = Array.Empty<uint>();
        private Thread? _thread;
        private volatile bool _stopRequested;
        private BufferFormat _format;
        private int _framesPerBuffer;

        public Output(string? deviceId)
        {
            _deviceId = deviceId;
            // Per-output device selection isn't possible with the shared context, but an id that
            // names nothing must still fail loudly rather than silently play on the default device.
            if (deviceId is not null && !GetRenderDevices().Any(d => d.Id == deviceId))
                throw new ArgumentException($"Audio output device '{deviceId}' not found.", nameof(deviceId));
        }

        public void Init(ISampleProvider source)
        {
            var al = Al;
            _source = source;
            int channels = source.WaveFormat.Channels;
            if (channels is < 1 or > 2)
                throw new NotSupportedException($"OpenAL output supports mono or stereo, not {channels} channels.");
            _format = channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
            _framesPerBuffer = source.WaveFormat.SampleRate * BufferMs / 1000;

            _sourceId = al.GenSource();
            _buffers = al.GenBuffers(BufferCount);
            foreach (var b in _buffers)
                Fill(b);
            al.SourceQueueBuffers(_sourceId, _buffers);
        }

        // Returns false once the source has run dry (end of stream).
        private bool Fill(uint buffer)
        {
            int channels = _source!.WaveFormat.Channels;
            var floats = new float[_framesPerBuffer * channels];
            int read = _source.Read(floats, 0, floats.Length);
            // Always submit a full buffer: a short or empty one would stall the queue; the
            // remainder is silence.
            var pcm = new short[floats.Length];
            for (int i = 0; i < read; i++)
                pcm[i] = (short)Math.Clamp(floats[i] * 32767f, -32768f, 32767f);
            fixed (short* p = pcm)
                Al.BufferData(buffer, _format, p, pcm.Length * sizeof(short), _source.WaveFormat.SampleRate);
            return read > 0;
        }

        public void Play()
        {
            if (_source is null) return;
            _stopRequested = false;
            Al.SourcePlay(_sourceId);
            _thread = new Thread(Pump) { IsBackground = true, Name = "OpenAL output" };
            _thread.Start();
        }

        private void Pump()
        {
            var al = Al;
            int silentBuffers = 0;
            while (!_stopRequested)
            {
                al.GetSourceProperty(_sourceId, GetSourceInteger.BuffersProcessed, out int processed);
                while (processed-- > 0)
                {
                    var buf = new uint[1];
                    al.SourceUnqueueBuffers(_sourceId, buf);
                    silentBuffers = Fill(buf[0]) ? 0 : silentBuffers + 1;
                    al.SourceQueueBuffers(_sourceId, buf);
                }

                al.GetSourceProperty(_sourceId, GetSourceInteger.SourceState, out int state);
                if ((SourceState)state != SourceState.Playing && silentBuffers < BufferCount)
                    al.SourcePlay(_sourceId); // underrun: restart
                if (silentBuffers >= BufferCount)
                    break; // stream ended and the queue has drained
                Thread.Sleep(5);
            }
        }

        public void Stop()
        {
            _stopRequested = true;
            _thread?.Join(500);
            _thread = null;
            if (_sourceId != 0)
                Al.SourceStop(_sourceId);
        }

        public void Dispose()
        {
            Stop();
            if (_sourceId != 0)
            {
                var al = Al;
                al.SourceStop(_sourceId);
                al.SetSourceProperty(_sourceId, SourceInteger.Buffer, 0);
                al.DeleteSource(_sourceId);
                al.DeleteBuffers(_buffers);
                _sourceId = 0;
                _buffers = Array.Empty<uint>();
            }
        }
    }

    public sealed class Input : IAudioInput
    {
        private const int SampleRate = 44100;

        private readonly string _deviceId;
        private readonly AlCapture _capture;
        private Device* _captureDevice;
        private Thread? _thread;
        private volatile bool _running;
        private readonly System.Diagnostics.Stopwatch _sinceStart = new();
        private bool _trimmedBacklog;

        public Input(string deviceId)
        {
            EnsureContext();
            _deviceId = deviceId;
            if (!Alc.TryGetExtension(null, out _capture!))
                throw new NotSupportedException("OpenAL audio capture is not available.");
            // Open once to fail early (missing device, no microphone permission), then close again:
            // macOS starts buffering from the moment a capture device is opened, so a device opened
            // here and started later would hand over ~0.5 s of audio from before StartRecording.
            Open();
            Close();
        }

        private void Open()
        {
            _captureDevice = _capture.CaptureOpenDevice(_deviceId, SampleRate, BufferFormat.Mono16, SampleRate / 2);
            if (_captureDevice == null)
                throw new InvalidOperationException(
                    $"Could not open microphone '{_deviceId}'. On macOS, check System Settings > Privacy & Security > Microphone.");
        }

        private void Close()
        {
            if (_captureDevice == null) return;
            _capture.CaptureCloseDevice(_captureDevice);
            _captureDevice = null;
        }

        public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;

        public void StartRecording()
        {
            if (_running) return;
            if (_captureDevice == null) Open();
            _capture.CaptureStart(_captureDevice);
            _sinceStart.Restart();
            _trimmedBacklog = false;
            _running = true;
            _thread = new Thread(Poll) { IsBackground = true, Name = "OpenAL capture" };
            _thread.Start();
        }

        private void Poll()
        {
            while (_running)
            {
                Drain();
                Thread.Sleep(10);
            }
        }

        private void Drain()
        {
            var device = _captureDevice;
            if (device == null) return;
            int available = _capture.GetAvailableSamples(device);
            if (available <= 0) return;
            var bytes = new byte[available * sizeof(short)];
            fixed (byte* p = bytes)
                _capture.CaptureSamples(device, p, available);
            int count = bytes.Length;
            if (!_trimmedBacklog)
            {
                // macOS hands over ~0.5 s that was buffered before CaptureStart: keep only what can
                // have been captured since StartRecording, so the stream's t=0 is the start call.
                _trimmedBacklog = true;
                int maxBytes = (int)(_sinceStart.Elapsed.TotalSeconds * SampleRate) * sizeof(short);
                if (count > maxBytes + 2 * sizeof(short))
                {
                    int skip = (count - maxBytes) & ~1;
                    Buffer.BlockCopy(bytes, skip, bytes, 0, count - skip);
                    count -= skip;
                }
            }
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, count));
        }

        public void StopRecording()
        {
            if (!_running) return;
            _running = false;
            _thread?.Join(500);
            _thread = null;
            _capture.CaptureStop(_captureDevice);
            Drain();
            Close();
        }

        public void Dispose()
        {
            if (_running) StopRecording();
            Close();
        }
    }
}
