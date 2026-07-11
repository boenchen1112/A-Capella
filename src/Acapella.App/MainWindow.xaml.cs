using System.IO;
using System.Windows;
using Acapella.Engine.Capture;
using Acapella.Engine.Devices;
using Acapella.Engine.GuideTrack;
using Acapella.Engine.Metronome;
using Acapella.Engine.Project;
using Acapella.Engine.Settings;
using Acapella.Engine.Sync;
using Microsoft.Win32;
using NAudio.Wave;

namespace Acapella.App;

public partial class MainWindow : Window
{
    private readonly DeviceCatalog _deviceCatalog = new();
    private readonly SettingsService _settingsService = new();
    private readonly LatencyCalibrator _calibrator;
    private readonly MetronomeEngine _metronome = new();
    private readonly LayerCollection _layers = new();
    private readonly string _mediaDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "media");

    private WasapiOut? _metronomeOutput;
    private FfmpegCaptureSession? _activeCapture;
    private List<AudioDeviceInfo> _renderDevices = new();
    private List<AudioDeviceInfo> _captureDevices = new();
    private List<DshowDeviceInfo> _dshowVideoDevices = new();

    public MainWindow()
    {
        InitializeComponent();
        _calibrator = new LatencyCalibrator(_settingsService);
        RefreshDevices();
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        try
        {
            _renderDevices = _deviceCatalog.GetWasapiRenderDevices();
            _captureDevices = _deviceCatalog.GetWasapiCaptureDevices();
            var dshow = _deviceCatalog.GetDshowDevices();
            _dshowVideoDevices = dshow.Where(d => d.IsVideo).ToList();

            AudioOutDeviceCombo.ItemsSource = _renderDevices.Select(d => d.Name).ToList();
            AudioInDeviceCombo.ItemsSource = _captureDevices.Select(d => d.Name).ToList();
            VideoDeviceCombo.ItemsSource = _dshowVideoDevices.Select(d => d.Name).ToList();

            if (AudioOutDeviceCombo.Items.Count > 0) AudioOutDeviceCombo.SelectedIndex = 0;
            if (AudioInDeviceCombo.Items.Count > 0) AudioInDeviceCombo.SelectedIndex = 0;
            if (VideoDeviceCombo.Items.Count > 0) VideoDeviceCombo.SelectedIndex = 0;

            StatusText.Text = $"Found {_renderDevices.Count} output, {_captureDevices.Count} input, {_dshowVideoDevices.Count} video devices.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Device scan failed: {ex.Message}";
        }
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        if (AudioOutDeviceCombo.SelectedIndex < 0 || AudioInDeviceCombo.SelectedIndex < 0)
        {
            StatusText.Text = "Select an audio in/out device first.";
            return;
        }

        var outputDevice = _renderDevices[AudioOutDeviceCombo.SelectedIndex];
        var inputDevice = _captureDevices[AudioInDeviceCombo.SelectedIndex];

        try
        {
            double offsetMs = _calibrator.CalibrateAndSave(outputDevice.Id, inputDevice.Id);
            CalibrationResultText.Text = $"Calibrated offset: {offsetMs:F1} ms";
        }
        catch (Exception ex)
        {
            CalibrationResultText.Text = $"Calibration failed: {ex.Message}";
        }
    }

    private void MetronomeToggle_Changed(object sender, RoutedEventArgs e)
    {
        _metronome.Enabled = MetronomeToggle.IsChecked == true;

        if (_metronome.Enabled && _metronomeOutput is null && AudioOutDeviceCombo.SelectedIndex >= 0)
        {
            var outputDevice = _renderDevices[AudioOutDeviceCombo.SelectedIndex];
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(outputDevice.Id);
            _metronomeOutput = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
            _metronomeOutput.Init(_metronome);
            _metronomeOutput.Play();
        }
    }

    private void BpmTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (double.TryParse(BpmTextBox.Text, out double bpm))
            _metronome.Bpm = bpm;
    }

    private void RecordLayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (VideoDeviceCombo.SelectedIndex < 0 || AudioInDeviceCombo.SelectedIndex < 0)
        {
            StatusText.Text = "Select a video and audio input device first.";
            return;
        }

        if (_layers.Layers.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }

        var videoDevice = _dshowVideoDevices[VideoDeviceCombo.SelectedIndex];
        var audioDevice = _captureDevices[AudioInDeviceCombo.SelectedIndex];
        int nextLayerId = _layers.Layers.Count;
        string outputPath = Path.Combine(_mediaDir, $"layer{nextLayerId}.mkv");

        _activeCapture = new FfmpegCaptureSession();

        if (nextLayerId > 0 && AudioOutDeviceCombo.SelectedIndex >= 0)
        {
            var outputDevice = _renderDevices[AudioOutDeviceCombo.SelectedIndex];
            double? offsetMs = _settingsService.GetLatencyOffsetMs(audioDevice.Id, outputDevice.Id);
            StatusText.Text = $"Recording layer {nextLayerId} with guide track (offset {offsetMs ?? 0:F1}ms)...";
            // Guide-track playback of prior layers is driven from the layer's stored source
            // audio via GuideTrackPlayer at composite/mix time in later phases; Phase 1 wires
            // the offset lookup so it's available, without a full mixed-guide signal yet since
            // there's no mixing engine before Phase 3.
        }
        else
        {
            StatusText.Text = $"Recording layer {nextLayerId}...";
        }

        _activeCapture.Start(videoDevice.Name, audioDevice.Name, outputPath);

        var layer = _layers.Add(LayerKind.RecordedAV, outputPath);
        RefreshLayersList();
    }

    private void StopRecordButton_Click(object sender, RoutedEventArgs e)
    {
        _activeCapture?.Stop();
        _activeCapture?.Dispose();
        _activeCapture = null;
        StatusText.Text = "Recording stopped.";
    }

    private void UploadLayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layers.Layers.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Media files|*.mp4;*.mov;*.mkv;*.wav;*.mp3;*.m4a|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            var kind = IsAudioOnlyExtension(Path.GetExtension(dialog.FileName))
                ? LayerKind.UploadedAudioOnly
                : LayerKind.UploadedVideo;
            _layers.Add(kind, dialog.FileName);
            RefreshLayersList();
            StatusText.Text = $"Uploaded layer: {Path.GetFileName(dialog.FileName)}";
        }
    }

    private static bool IsAudioOnlyExtension(string ext) =>
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    private void RefreshLayersList()
    {
        LayersList.ItemsSource = _layers.Layers
            .Select(l => $"Layer {l.LayerId}: {l.Kind} — {Path.GetFileName(l.SourcePath)}")
            .ToList();
    }

    private void LayersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LayersList.SelectedIndex >= 0 && LayersList.SelectedIndex < _layers.Layers.Count)
        {
            var layer = _layers.Layers[LayersList.SelectedIndex];
            OffsetSlider.Value = layer.ManualOffsetMs;
        }
    }

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        OffsetValueText.Text = $"{OffsetSlider.Value:F0} ms";
        if (LayersList.SelectedIndex >= 0 && LayersList.SelectedIndex < _layers.Layers.Count)
        {
            _layers.Layers[LayersList.SelectedIndex].ManualOffsetMs = OffsetSlider.Value;
        }
    }
}
