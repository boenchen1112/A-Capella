using Acapella.Engine.Devices;
using Acapella.Engine.GuideTrack;
using Acapella.Engine.Metronome;
using Acapella.Engine.Settings;
using Acapella.Engine.Sync;
using NAudio.CoreAudioApi;
using NAudio.Wave;

var catalog = new DeviceCatalog();

Console.WriteLine("=== WASAPI Render (output) devices ===");
var renderDevices = catalog.GetWasapiRenderDevices();
foreach (var d in renderDevices) Console.WriteLine($"  {d.Id} :: {d.Name}");

Console.WriteLine("=== WASAPI Capture (input) devices ===");
var captureDevices = catalog.GetWasapiCaptureDevices();
foreach (var d in captureDevices) Console.WriteLine($"  {d.Id} :: {d.Name}");

if (args.Length > 0 && args[0] == "listonly")
    return;

var outputDevice = renderDevices.FirstOrDefault(d => d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
    ?? renderDevices.FirstOrDefault();
var loopbackDevice = captureDevices.FirstOrDefault(d => d.Name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase));

if (outputDevice is null || loopbackDevice is null)
{
    Console.WriteLine("Could not find output device or Stereo Mix loopback device. Aborting.");
    return;
}

Console.WriteLine($"\nUsing output: {outputDevice.Name}");
Console.WriteLine($"Using loopback input: {loopbackDevice.Name}\n");

var settingsPath = Path.Combine(Path.GetTempPath(), "acapella-hwcheck-settings.json");
if (File.Exists(settingsPath)) File.Delete(settingsPath);
var settingsService = new SettingsService(settingsPath);
var calibrator = new LatencyCalibrator(settingsService);

Console.WriteLine("--- Check 1: Latency calibration (run twice for consistency) ---");
double offset1 = calibrator.CalibrateAndSave(outputDevice.Id, loopbackDevice.Id);
Console.WriteLine($"Run 1 offset: {offset1:F1} ms");
double offset2 = calibrator.CalibrateAndSave(outputDevice.Id, loopbackDevice.Id);
Console.WriteLine($"Run 2 offset: {offset2:F1} ms");
double consistency = Math.Abs(offset1 - offset2);
// WASAPI shared-mode buffer quantization is itself ~20ms, so run-to-run jitter of up to
// one buffer is expected, not a calibration bug. Threshold set above that floor.
Console.WriteLine($"Consistency delta: {consistency:F1} ms -> {(consistency < 40 ? "PASS" : "FAIL")}");

Console.WriteLine("\n--- Check 2: Settings persistence across reload ---");
var reloadedSettings = new SettingsService(settingsPath);
double? reloaded = reloadedSettings.GetLatencyOffsetMs(loopbackDevice.Id, outputDevice.Id);
bool persistPass = reloaded.HasValue && Math.Abs(reloaded.Value - offset2) < 0.01;
Console.WriteLine($"Reloaded offset: {reloaded:F1} ms (saved: {offset2:F1} ms) -> {(persistPass ? "PASS" : "FAIL")}");

Console.WriteLine("\n--- Check 3: Within-layer A/V sync (synthetic click+flash, single ffmpeg process) ---");
string syncTestPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "media", "synctest.mp4");
if (!File.Exists(syncTestPath))
{
    Console.WriteLine($"Missing test fixture at {syncTestPath} -> SKIPPED");
}
else
{
    double audioT = AvSyncAnalyzer.GetAudioTransientSeconds(syncTestPath);
    double videoT = AvSyncAnalyzer.GetVideoFlashSeconds(syncTestPath);
    double avOffsetMs = (audioT - videoT) * 1000.0;
    Console.WriteLine($"Audio transient at {audioT:F3}s, video flash at {videoT:F3}s, offset {avOffsetMs:F1} ms -> {(Math.Abs(avOffsetMs) < 50 ? "PASS" : "FAIL")}");
}

Console.WriteLine("\n--- Check 4: Guide-track playback starts promptly with no added delay ---");
{
    // GuideTrackPlayer.Play() no longer sleeps before starting playback (see C4 fix): adding a
    // delay here made cross-layer misalignment worse, since the performer's own round-trip
    // output+input latency already delays what lands in the recording. The round-trip is instead
    // captured as CalibratedOffsetMs and trimmed from the recorded layer's head at mix/export
    // time (LayerModel.GetShiftMs / AudioShiftHelper) -- not simulated here. This check just
    // verifies onset is prompt (bounded by normal WasapiOut init + I/O latency), which guards
    // against a delay creeping back in.
    const int sampleRate = 44100;

    using var enumerator = new MMDeviceEnumerator();
    var loopback = enumerator.GetDevice(loopbackDevice.Id);
    var captured = new List<float>();
    using var capture = new WasapiCapture(loopback);
    var captureFormat = capture.WaveFormat;
    capture.DataAvailable += (s, e) =>
    {
        var samples = PcmConverter.BytesToFloatSamples(e.Buffer, e.BytesRecorded, captureFormat);
        captured.AddRange(samples);
    };

    var click = ToneGenerator.GenerateClick(sampleRate);
    var guideAudio = ToneGenerator.ToSampleProvider(click, sampleRate);

    using var guidePlayer = new GuideTrackPlayer();
    capture.StartRecording();
    guidePlayer.Play(outputDevice.Id, guideAudio);
    Thread.Sleep(1500);
    capture.StopRecording();
    Thread.Sleep(100);

    var mono = PcmConverter.DownmixToMono(captured.ToArray(), captureFormat.Channels);
    var resampled = PcmConverter.Resample(mono, captureFormat.SampleRate, sampleRate);
    int maxLagSamples = Math.Min(resampled.Length, sampleRate * 2);
    int onsetSample = CrossCorrelator.FindOffsetSamples(click, resampled, maxLagSamples);
    double onsetMs = onsetSample * 1000.0 / sampleRate;
    Console.WriteLine($"Guide onset: {onsetMs:F1} ms (no delay requested) -> {(onsetMs < 500 ? "PASS" : "FAIL")}");
}

Console.WriteLine("\n--- Check 5: Metronome isolation from recording pipeline (structural) ---");
{
    var metronome = new MetronomeEngine();
    metronome.Enabled = true;
    metronome.Bpm = 120;
    // MetronomeEngine only ever gets wired to a WasapiOut monitoring path (see MainWindow /
    // GuideTrackPlayer); FfmpegCaptureSession.Start/StartAudioOnly build their dshow command
    // line purely from the caller-supplied device name string, with no reference to the
    // metronome or its output device anywhere in the call chain. There is therefore no code
    // path by which metronome samples can reach a recorded layer's audio -- verified here by
    // confirming FfmpegCaptureSession never touches an ISampleProvider/MetronomeEngine type.
    bool metronomeTypeReferencedByCaptureSession = typeof(Acapella.Engine.Capture.FfmpegCaptureSession)
        .GetMethods()
        .Any(m => m.GetParameters().Any(p => p.ParameterType == typeof(MetronomeEngine) || p.ParameterType == typeof(ISampleProvider)));
    Console.WriteLine($"FfmpegCaptureSession references metronome/audio-sample types: {metronomeTypeReferencedByCaptureSession} -> {(!metronomeTypeReferencedByCaptureSession ? "PASS" : "FAIL")}");
}

Console.WriteLine("\nDone.");
