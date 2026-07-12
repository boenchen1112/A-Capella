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

            // C4: capture starts first, guide plays immediately with zero added delay; the
            // round-trip latency is stored on the new layer and trimmed from its head at
            // mix/preview/export time instead (see LayerModel.GetShiftMs).
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
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
        layer.CalibratedOffsetMs = _pendingCalibratedOffsetMs;
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
