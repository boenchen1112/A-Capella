using System.IO;
using System.Windows;
using Acapella.Engine.Capture;
using Acapella.Engine.Devices;
using Acapella.Engine.Ffmpeg;
using Acapella.Engine.GuideTrack;
using Acapella.Engine.Metronome;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Acapella.Engine.Settings;
using Acapella.Engine.Sync;
using NAudio.Wave;

namespace Acapella.App;

/// <summary>
/// Modal "Recording setup" dialog (see UI_Design_Spec.md) -- the only place device pickers and
/// the metronome ever appear. Owns one recording end-to-end: device selection, optional latency
/// calibration, guide-track playback against prior layers, capture start/stop, and zombie-layer
/// validation (M6). On success the created layer is exposed via CreatedLayer and DialogResult is
/// true; the caller (MainWindow) just attaches it to the track row.
/// </summary>
public partial class RecordSetupWindow : Window
{
    private readonly DeviceCatalog _deviceCatalog;
    private readonly SettingsService _settingsService;
    private readonly LatencyCalibrator _calibrator;
    private readonly LayerCollection _layers;
    private readonly MixEngine _mixEngine;
    private readonly string _mediaDir;
    private readonly MetronomeEngine _metronome = new();

    private List<AudioDeviceInfo> _captureDevices = new();
    private List<DshowDeviceInfo> _dshowVideoDevices = new();
    private List<DshowDeviceInfo> _dshowAudioDevices = new();
    private AudioDeviceInfo _outputDevice;

    private FfmpegCaptureSession? _activeCapture;
    private GuideTrackPlayer? _guideTrackPlayer;
    private WasapiOut? _metronomeOutput;
    private bool _isRecording;

    public LayerModel? CreatedLayer { get; private set; }

    /// <summary>Current metronome BPM, read by MainWindow after the dialog closes so the next
    /// dialog (and a saved project) carries the last value forward -- BPM has no home in the
    /// main window anymore since the spec confines the metronome to this dialog.</summary>
    public double Bpm => _metronome.Bpm;

    public RecordSetupWindow(DeviceCatalog deviceCatalog, SettingsService settingsService, LayerCollection layers, MixEngine mixEngine, string mediaDir, double initialBpm)
    {
        InitializeComponent();
        _deviceCatalog = deviceCatalog;
        _settingsService = settingsService;
        _calibrator = new LatencyCalibrator(_settingsService);
        _layers = layers;
        _mixEngine = mixEngine;
        _mediaDir = mediaDir;
        _outputDevice = _deviceCatalog.GetDefaultRenderDevice();
        _metronome.Bpm = initialBpm;
        BpmTextBox.Text = initialBpm.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        RefreshDevices();
    }

    private void RefreshDevices()
    {
        _captureDevices = _deviceCatalog.GetWasapiCaptureDevices();
        var dshow = _deviceCatalog.GetDshowDevices();
        _dshowVideoDevices = dshow.Where(d => d.IsVideo).ToList();
        _dshowAudioDevices = dshow.Where(d => !d.IsVideo).ToList();

        CameraCombo.ItemsSource = _dshowVideoDevices.Select(d => d.Name).ToList();
        MicCombo.ItemsSource = _dshowAudioDevices.Select(d => d.Name).ToList();
        if (CameraCombo.Items.Count > 0) CameraCombo.SelectedIndex = 0;
        if (MicCombo.Items.Count > 0) MicCombo.SelectedIndex = 0;
    }

    /// <summary>The spec's dialog has no separate loopback-device picker -- Windows' built-in
    /// Stereo Mix (see Phase 1 plan) is the app's fixed calibration loopback input, matched by
    /// name from the WASAPI capture list rather than exposed as a choice.</summary>
    private AudioDeviceInfo? FindLoopbackDevice() =>
        _captureDevices.FirstOrDefault(d => d.Name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase))
        ?? _captureDevices.FirstOrDefault();

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        var loopbackDevice = FindLoopbackDevice();
        if (loopbackDevice is null)
        {
            CalibrationResultText.Text = "No loopback capture device found (enable Stereo Mix).";
            return;
        }

        try
        {
            double offsetMs = _calibrator.CalibrateAndSave(_outputDevice.Id, loopbackDevice.Id);
            CalibrationResultText.Text = $"Calibrated offset: {offsetMs:F1} ms";
        }
        catch (Exception ex)
        {
            CalibrationResultText.Text = $"Calibration failed: {ex.Message}";
        }
    }

    private void MetronomeToggle_Changed(object sender, RoutedEventArgs e) =>
        _metronome.Enabled = MetronomeToggle.IsChecked == true;

    private void BpmTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (double.TryParse(BpmTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bpm))
            _metronome.Bpm = bpm;
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            StopRecording();
            return;
        }

        if (CameraCombo.SelectedIndex < 0 || MicCombo.SelectedIndex < 0)
        {
            StatusText.Text = "Select a camera and microphone first.";
            return;
        }

        if (_layers.Layers.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }

        var videoDevice = _dshowVideoDevices[CameraCombo.SelectedIndex];
        var dshowAudioDevice = _dshowAudioDevices[MicCombo.SelectedIndex];
        int nextLayerId = _layers.Layers.Count;
        Directory.CreateDirectory(_mediaDir);
        string outputPath = Path.Combine(_mediaDir, $"layer{nextLayerId}.mkv");

        _activeCapture = new FfmpegCaptureSession();
        double calibratedOffsetMs = 0;
        _pendingGuideReferenceMono = null;

        if (nextLayerId > 0)
        {
            var loopbackDevice = FindLoopbackDevice();
            calibratedOffsetMs = loopbackDevice is not null
                ? _settingsService.GetLatencyOffsetMs(loopbackDevice.Id, _outputDevice.Id) ?? 0
                : 0;

            const int sampleRate = 44100;
            var mixInputs = _layers.Layers
                .Select(l => new MixLayerInput(l.LayerId, AudioShiftHelper.ApplyShift(
                    TrimHelper.ApplyTrim(AudioDecoder.DecodeToMonoFloat(l.SourcePath, sampleRate), l.TrimStartMs, l.TrimEndMs, sampleRate),
                    l.GetShiftMs(), sampleRate), sampleRate, l.MixParameters))
                .ToList();
            var guideMix = _mixEngine.BuildMix(mixInputs, sampleRate);

            // A second, independent provider graph built from the same mixInputs (BuildMix's
            // ArraySampleProvider reads don't mutate the underlying float[] arrays, so this is
            // safe to render separately without disturbing the one actually being played) so
            // StopRecording can cross-correlate the just-recorded mic audio against exactly what
            // the singer heard, without consuming the live playback stream (audit B4's post-take
            // fallback).
            _pendingGuideReferenceMono = RenderMonoReference(_mixEngine.BuildMix(mixInputs, sampleRate));

            // C4: capture starts first so the guide is never delayed by it; but the guide must
            // not start until dshow capture is actually producing frames (audit B4) -- otherwise
            // the recorded file's t=0 begins at an unmeasured, variable point (0.5-2s, dshow
            // device init) after the guide already started, and CalibratedOffsetMs (measured over
            // a WASAPI Stereo-Mix loopback, a different path) can't account for that.
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            _activeCapture.WaitForCaptureStarted(TimeSpan.FromSeconds(3));
            _guideTrackPlayer = new GuideTrackPlayer();
            _guideTrackPlayer.Play(_outputDevice.Id, guideMix);

            StatusText.Text = $"Recording layer {nextLayerId} with guide track (offset {calibratedOffsetMs:F1}ms)...";
        }
        else
        {
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            StatusText.Text = $"Recording layer {nextLayerId}...";
        }

        if (_metronome.Enabled)
        {
            using var metronomeEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var metronomeDevice = metronomeEnumerator.GetDevice(_outputDevice.Id);
            _metronomeOutput = new WasapiOut(metronomeDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
            _metronomeOutput.Init(_metronome);
            _metronomeOutput.Play();
        }

        _isRecording = true;
        RecordButton.Content = "Stop Recording";
        CameraCombo.IsEnabled = false;
        MicCombo.IsEnabled = false;

        // Stash for StopRecording's zombie-layer bookkeeping.
        _pendingLayerId = nextLayerId;
        _pendingOutputPath = outputPath;
        _pendingCalibratedOffsetMs = calibratedOffsetMs;
    }

    private int _pendingLayerId;
    private string _pendingOutputPath = "";
    private double _pendingCalibratedOffsetMs;
    private float[]? _pendingGuideReferenceMono;

    private static float[] RenderMonoReference(NAudio.Wave.ISampleProvider stereoMix)
    {
        // Read in even-sized (frame-aligned) chunks so every chunk holds whole L/R pairs.
        var mono = new List<float>();
        var chunk = new float[8192];
        int n;
        while ((n = stereoMix.Read(chunk, 0, chunk.Length)) > 0)
        {
            int pairCount = n / 2;
            for (int i = 0; i < pairCount; i++)
                mono.Add((chunk[i * 2] + chunk[i * 2 + 1]) / 2f);
        }
        return mono.ToArray();
    }

    /// <summary>Audit B4's post-take fallback: refine the settings-based CalibratedOffsetMs by
    /// cross-correlating the just-recorded mic audio against the guide track it was played
    /// against. Catches whatever the wait-for-first-frame fix (RecordButton_Click) didn't fully
    /// account for -- but only trusts the result when the correlation confidence clears a
    /// threshold, since a headphone-wearing singer's mic picks up no guide bleed at all and would
    /// otherwise correlate on noise.</summary>
    private double MeasureCalibratedOffsetMs(string recordedPath, double fallbackOffsetMs)
    {
        if (_pendingGuideReferenceMono is null || _pendingGuideReferenceMono.Length == 0)
            return fallbackOffsetMs;

        const int sampleRate = 44100;
        float[] recordedMono;
        try
        {
            recordedMono = AudioDecoder.DecodeToMonoFloat(recordedPath, sampleRate);
        }
        catch
        {
            return fallbackOffsetMs;
        }
        if (recordedMono.Length == 0)
            return fallbackOffsetMs;

        // Decimate before searching: latency estimation doesn't need sample-accurate resolution
        // (~1ms is plenty), and a naive full-resolution correlation over several seconds of audio
        // at a multi-second lag search would turn this post-recording step into a multi-second
        // UI-blocking stall.
        const int decimateFactor = 44; // ~1kHz effective rate at a 44.1kHz source.
        const int windowSeconds = 5;
        var reference = Decimate(_pendingGuideReferenceMono, sampleRate, decimateFactor, windowSeconds);
        var signal = Decimate(recordedMono, sampleRate, decimateFactor, windowSeconds);
        int maxLagDecimated = 2 * sampleRate / decimateFactor; // +-2s of round-trip latency headroom.

        var (lagDecimated, confidence) = CrossCorrelator.FindOffsetSamplesWithConfidence(reference, signal, maxLagDecimated);

        const double confidenceThreshold = 0.2;
        if (confidence < confidenceThreshold)
            return fallbackOffsetMs;

        return lagDecimated * decimateFactor * 1000.0 / sampleRate;
    }

    private static float[] Decimate(float[] samples, int sampleRate, int decimateFactor, int windowSeconds)
    {
        int limit = Math.Min(samples.Length, windowSeconds * sampleRate);
        var result = new float[limit / decimateFactor];
        for (int i = 0; i < result.Length; i++)
            result[i] = samples[i * decimateFactor];
        return result;
    }

    private void StopRecording()
    {
        _activeCapture?.Stop();
        string[] stderrTail = _activeCapture?.GetRecentStderrLines() ?? Array.Empty<string>();
        _activeCapture?.Dispose();
        _activeCapture = null;
        _guideTrackPlayer?.Stop();
        _guideTrackPlayer?.Dispose();
        _guideTrackPlayer = null;
        _metronomeOutput?.Stop();
        _metronomeOutput?.Dispose();
        _metronomeOutput = null;

        _isRecording = false;
        RecordButton.Content = "Record";
        CameraCombo.IsEnabled = true;
        MicCombo.IsEnabled = true;

        // M6: verify the just-recorded file has real duration before attaching it as a layer;
        // if not, surface ffmpeg's tail diagnostics and let the user retry from this same dialog
        // instead of leaving a zombie layer or silently closing.
        if (!MediaProbe.HasNonzeroDuration(_pendingOutputPath))
        {
            string diagnostics = stderrTail.Length > 0 ? string.Join(" | ", stderrTail.TakeLast(3)) : "no ffmpeg diagnostics captured";
            StatusText.Text = $"Recording failed: {diagnostics}";
            return;
        }

        var layer = _layers.Add(LayerKind.RecordedAV, _pendingOutputPath);
        layer.CalibratedOffsetMs = MeasureCalibratedOffsetMs(_pendingOutputPath, _pendingCalibratedOffsetMs);
        CreatedLayer = layer;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Don't leave an orphaned ffmpeg process running if the user closes the dialog mid-record.
        if (_isRecording)
        {
            _activeCapture?.Stop();
            _activeCapture?.Dispose();
            _guideTrackPlayer?.Stop();
            _guideTrackPlayer?.Dispose();
            _metronomeOutput?.Stop();
            _metronomeOutput?.Dispose();
        }
    }
}
