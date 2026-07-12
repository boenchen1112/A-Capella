using System.IO;
using System.Windows;
using Acapella.Engine.Capture;
using Acapella.Engine.Composite;
using Acapella.Engine.Devices;
using Acapella.Engine.Export;
using Acapella.Engine.GuideTrack;
using Acapella.Engine.Metronome;
using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using Acapella.Engine.Project;
using Acapella.Engine.Settings;
using Acapella.Engine.Sync;
using Microsoft.Win32;
using NAudio.Wave;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Acapella.App;

public partial class MainWindow : Window
{
    private readonly DeviceCatalog _deviceCatalog = new();
    private readonly SettingsService _settingsService = new();
    private readonly LatencyCalibrator _calibrator;
    private readonly MetronomeEngine _metronome = new();
    private readonly LayerCollection _layers = new();
    private readonly string _mediaDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "media");

    private readonly MixEngine _mixEngine = new();
    private readonly ProjectPersistenceService _projectPersistence = new();

    private WasapiOut? _metronomeOutput;
    private WasapiOut? _previewOutput;
    private FfmpegCaptureSession? _activeCapture;
    private GuideTrackPlayer? _guideTrackPlayer;
    private List<AudioDeviceInfo> _renderDevices = new();
    private List<AudioDeviceInfo> _captureDevices = new();
    private List<DshowDeviceInfo> _dshowVideoDevices = new();
    private List<DshowDeviceInfo> _dshowAudioDevices = new();
    private bool _isLoadingLayerControls;
    private SKBitmap? _compositedFrame;
    private double? _lastCalibratedOffsetMs;

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
            _dshowAudioDevices = dshow.Where(d => !d.IsVideo).ToList();

            AudioOutDeviceCombo.ItemsSource = _renderDevices.Select(d => d.Name).ToList();
            AudioInDeviceCombo.ItemsSource = _captureDevices.Select(d => d.Name).ToList();
            VideoDeviceCombo.ItemsSource = _dshowVideoDevices.Select(d => d.Name).ToList();
            DshowAudioDeviceCombo.ItemsSource = _dshowAudioDevices.Select(d => d.Name).ToList();

            if (AudioOutDeviceCombo.Items.Count > 0) AudioOutDeviceCombo.SelectedIndex = 0;
            if (AudioInDeviceCombo.Items.Count > 0) AudioInDeviceCombo.SelectedIndex = 0;
            if (VideoDeviceCombo.Items.Count > 0) VideoDeviceCombo.SelectedIndex = 0;
            if (DshowAudioDeviceCombo.Items.Count > 0) DshowAudioDeviceCombo.SelectedIndex = 0;

            StatusText.Text = $"Found {_renderDevices.Count} output, {_captureDevices.Count} input, {_dshowVideoDevices.Count} video, {_dshowAudioDevices.Count} dshow audio devices.";
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
            _lastCalibratedOffsetMs = offsetMs;
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
        if (VideoDeviceCombo.SelectedIndex < 0 || AudioInDeviceCombo.SelectedIndex < 0 || DshowAudioDeviceCombo.SelectedIndex < 0)
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
        // WASAPI device drives calibration/latency lookup (SettingsService keys by WASAPI
        // device id); the dshow device is what ffmpeg actually needs to open by name. The two
        // namespaces frequently use different friendly names for the same physical device (e.g.
        // "Microphone (Realtek(R) Audio)" vs "Microphone (Realtek High Definition Audio)"), so
        // passing the WASAPI name to ffmpeg previously failed with "Could not find audio device".
        var audioDevice = _captureDevices[AudioInDeviceCombo.SelectedIndex];
        var dshowAudioDevice = _dshowAudioDevices[DshowAudioDeviceCombo.SelectedIndex];
        int nextLayerId = _layers.Layers.Count;
        string outputPath = Path.Combine(_mediaDir, $"layer{nextLayerId}.mkv");

        _activeCapture = new FfmpegCaptureSession();
        double calibratedOffsetMs = 0;

        if (nextLayerId > 0 && AudioOutDeviceCombo.SelectedIndex >= 0)
        {
            var outputDevice = _renderDevices[AudioOutDeviceCombo.SelectedIndex];
            calibratedOffsetMs = _settingsService.GetLatencyOffsetMs(audioDevice.Id, outputDevice.Id) ?? 0;

            const int sampleRate = 44100;
            var mixInputs = _layers.Layers
                .Select(l => new MixLayerInput(l.LayerId, AudioShiftHelper.ApplyShift(AudioDecoder.DecodeToMonoFloat(l.SourcePath, sampleRate), l.GetShiftMs(), sampleRate), sampleRate, l.MixParameters))
                .ToList();
            var guideMix = _mixEngine.BuildMix(mixInputs, sampleRate);

            // Capture starts first, then the guide plays immediately with no added delay (see
            // GuideTrackPlayer / C4 fix) -- the round-trip latency captured here is stored on the
            // new layer below and trimmed from its head at mix/export time instead.
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            _guideTrackPlayer = new GuideTrackPlayer();
            _guideTrackPlayer.Play(outputDevice.Id, guideMix);

            StatusText.Text = $"Recording layer {nextLayerId} with guide track (offset {calibratedOffsetMs:F1}ms)...";
        }
        else
        {
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            StatusText.Text = $"Recording layer {nextLayerId}...";
        }

        var layer = _layers.Add(LayerKind.RecordedAV, outputPath);
        layer.CalibratedOffsetMs = calibratedOffsetMs;
        RefreshLayersList();
    }

    private void StopRecordButton_Click(object sender, RoutedEventArgs e)
    {
        _activeCapture?.Stop();
        _activeCapture?.Dispose();
        _activeCapture = null;
        _guideTrackPlayer?.Stop();
        _guideTrackPlayer?.Dispose();
        _guideTrackPlayer = null;
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
            LoadLayerControls(layer.MixParameters);
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

    private void LoadLayerControls(LayerMixParameters parameters)
    {
        _isLoadingLayerControls = true;
        GainSlider.Value = parameters.GainDb;
        PanSlider.Value = parameters.Pan;
        LowEqSlider.Value = parameters.LowShelfGainDb;
        MidEqSlider.Value = parameters.MidBellGainDb;
        HighEqSlider.Value = parameters.HighShelfGainDb;
        GateThresholdSlider.Value = parameters.NoiseGateThresholdDb;
        MuteCheckBox.IsChecked = parameters.Mute;
        SoloCheckBox.IsChecked = parameters.Solo;
        PitchBackendCombo.SelectedIndex = (int)parameters.PitchBackend;
        _isLoadingLayerControls = false;
    }

    private void MixParam_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingLayerControls) return;
        if (LayersList.SelectedIndex < 0 || LayersList.SelectedIndex >= _layers.Layers.Count) return;

        var parameters = _layers.Layers[LayersList.SelectedIndex].MixParameters;
        parameters.GainDb = (float)GainSlider.Value;
        parameters.Pan = (float)PanSlider.Value;
        parameters.LowShelfGainDb = (float)LowEqSlider.Value;
        parameters.MidBellGainDb = (float)MidEqSlider.Value;
        parameters.HighShelfGainDb = (float)HighEqSlider.Value;
        parameters.NoiseGateThresholdDb = (float)GateThresholdSlider.Value;
        parameters.Mute = MuteCheckBox.IsChecked == true;
        parameters.Solo = SoloCheckBox.IsChecked == true;
        parameters.PitchBackend = (PitchBackendSelection)PitchBackendCombo.SelectedIndex;

        GainValueText.Text = $"{parameters.GainDb:F1} dB";
        PanValueText.Text = $"{parameters.Pan:F2}";
        LowEqValueText.Text = $"{parameters.LowShelfGainDb:F1} dB";
        MidEqValueText.Text = $"{parameters.MidBellGainDb:F1} dB";
        HighEqValueText.Text = $"{parameters.HighShelfGainDb:F1} dB";
        GateThresholdValueText.Text = $"{parameters.NoiseGateThresholdDb:F1} dB";
    }

    private void PreviewMixButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOutput is not null)
        {
            _previewOutput.Stop();
            _previewOutput.Dispose();
            _previewOutput = null;
            PreviewMixButton.Content = "Preview Mix";
            StatusText.Text = "Preview stopped.";
            return;
        }

        if (_layers.Layers.Count == 0 || AudioOutDeviceCombo.SelectedIndex < 0)
        {
            StatusText.Text = "Add at least one layer and select an audio output device first.";
            return;
        }

        try
        {
            const int sampleRate = 44100;
            var mixInputs = _layers.Layers
                .Select(l => new MixLayerInput(l.LayerId, AudioShiftHelper.ApplyShift(AudioDecoder.DecodeToMonoFloat(l.SourcePath, sampleRate), l.GetShiftMs(), sampleRate), sampleRate, l.MixParameters))
                .ToList();

            var mix = _mixEngine.BuildMix(mixInputs, sampleRate);

            var outputDevice = _renderDevices[AudioOutDeviceCombo.SelectedIndex];
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(outputDevice.Id);
            _previewOutput = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
            _previewOutput.Init(mix);
            _previewOutput.Play();
            PreviewMixButton.Content = "Stop Preview";
            StatusText.Text = $"Previewing mix of {mixInputs.Count} layer(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Preview failed: {ex.Message}";
        }
    }

    private void CompositePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layers.Layers.Count == 0)
        {
            StatusText.Text = "Add at least one layer first.";
            return;
        }

        try
        {
            const int cellWidth = 240;
            const int cellHeight = 180;
            const int canvasWidth = cellWidth * 2;
            const int canvasHeight = cellHeight * 2;

            var frames = _layers.Layers
                .Select(l => l.Kind == LayerKind.UploadedAudioOnly
                    ? PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight)
                    : VideoFrameDecoder.DecodeFirstFrame(l.SourcePath, cellWidth, cellHeight) ?? PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight))
                .ToList();

            var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);
            _compositedFrame?.Dispose();
            _compositedFrame = Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects);

            CompositeCanvas.InvalidateVisual();
            StatusText.Text = $"Composited preview of {frames.Count} layer(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Composite preview failed: {ex.Message}";
        }
    }

    private void CompositeCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_compositedFrame is not null)
            canvas.DrawBitmap(_compositedFrame, new SKRect(0, 0, e.Info.Width, e.Info.Height));
    }

    private void SaveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Acapella project|*.acapella.json", DefaultExt = ".acapella.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            double.TryParse(BpmTextBox.Text, out double bpm);
            var dto = _projectPersistence.ToDto(_layers, bpm, _lastCalibratedOffsetMs);
            _projectPersistence.SaveToFile(dto, dialog.FileName);
            StatusText.Text = $"Project saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OpenProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Acapella project|*.acapella.json;*.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var dto = _projectPersistence.LoadFromFile(dialog.FileName);
            var (loadedLayers, bpm, latencyOffset) = _projectPersistence.FromDto(dto);

            _layers.Restore(loadedLayers.Layers);
            _lastCalibratedOffsetMs = latencyOffset;
            BpmTextBox.Text = bpm.ToString("F0");
            _metronome.Bpm = bpm;

            RefreshLayersList();
            StatusText.Text = $"Project opened: {Path.GetFileName(dialog.FileName)} ({loadedLayers.Layers.Count} layer(s)).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open failed: {ex.Message}";
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layers.Layers.Count == 0)
        {
            StatusText.Text = "Add at least one layer before exporting.";
            return;
        }

        var dialog = new SaveFileDialog { Filter = "MP4 video|*.mp4", DefaultExt = ".mp4" };
        if (dialog.ShowDialog() != true) return;

        StatusText.Text = "Exporting...";
        ExportButton.IsEnabled = false;

        Task.Run(() =>
        {
            try
            {
                new ExportEngine().Export(_layers, dialog.FileName);
                Dispatcher.Invoke(() => StatusText.Text = $"Export complete: {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Export failed: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() => ExportButton.IsEnabled = true);
            }
        });
    }
}
