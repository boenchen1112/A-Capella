using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Acapella.App.ViewModels;
using Acapella.Engine.Devices;
using Acapella.Engine.Export;
using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using Acapella.Engine.Preview;
using Acapella.Engine.Project;
using Acapella.Engine.Settings;
using Microsoft.Win32;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Acapella.App;

public partial class MainWindow : Window
{
    private readonly DeviceCatalog _deviceCatalog = new();
    private readonly SettingsService _settingsService = new();
    private readonly LayerCollection _layers = new();
    // L1: a directory next to the executable works regardless of how/where the app is launched.
    private readonly string _mediaDir = Path.Combine(AppContext.BaseDirectory, "media");

    private readonly MixEngine _mixEngine = new();
    private readonly ProjectPersistenceService _projectPersistence = new();
    private readonly ObservableCollection<LayerRowViewModel> _tracks = new();
    private readonly PreviewPlaybackEngine _previewEngine = new(canvasWidth: 640, canvasHeight: 480, fps: 30);
    private readonly DispatcherTimer _previewDebounceTimer;

    private SKBitmap? _compositedFrame;
    private double? _lastCalibratedOffsetMs;
    // Metronome now lives only inside RecordSetupWindow (see UI_Design_Spec.md); this just carries
    // the last-used BPM forward across dialogs and into project save/load.
    private double _metronomeBpm = 120;
    private int _previewRefreshGeneration;
    private bool _isScrubbing;
    private double _pixelsPerSecond = 60;
    private LayerRowViewModel? _mixingLayer;

    public MainWindow()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        UpdateAddLayerButtonState();

        _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewDebounceTimer.Tick += (s, e) => { _previewDebounceTimer.Stop(); RefreshPreviewLive(); };

        _previewEngine.FrameReady += frame => Dispatcher.Invoke(() =>
        {
            var old = _compositedFrame;
            _compositedFrame = frame;
            CompositeCanvas.InvalidateVisual();
            old?.Dispose();
            if (!_isScrubbing) TimelineSlider.Value = Math.Min(_previewEngine.PositionMs, TimelineSlider.Maximum);
            TimeReadoutText.Text = $"{FormatTime(_previewEngine.PositionMs)} / {FormatTime(_previewEngine.DurationMs)}";
        });
        _previewEngine.PlaybackStopped += () => Dispatcher.Invoke(() => PlayStopButton.Content = "▶ Play");

        Closing += (s, e) => { _previewEngine.Dispose(); _compositedFrame?.Dispose(); };
    }

    // ----- Track sidebar: add / record / upload -----

    private void AddLayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }

        var row = new LayerRowViewModel(_tracks.Count + 1);
        row.LiveParamChanged += DebounceRefreshPreview;
        _tracks.Add(row);
        UpdateAddLayerButtonState();
    }

    private void UpdateAddLayerButtonState() =>
        AddLayerButton.IsEnabled = _tracks.Count < LayerCollection.MaxLayers;

    private void RecordChoice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;

        var dialog = new RecordSetupWindow(_deviceCatalog, _settingsService, _layers, _mixEngine, _mediaDir, _metronomeBpm) { Owner = this };
        bool? result = dialog.ShowDialog();
        _metronomeBpm = dialog.Bpm;

        if (result == true && dialog.CreatedLayer is not null)
        {
            row.Layer = dialog.CreatedLayer;
            RefreshPreviewLive();
            StatusText.Text = $"Recorded {row.DisplayName}.";
        }
    }

    private void UploadChoice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Media files|*.mp4;*.mov;*.mkv;*.wav;*.mp3;*.m4a|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var kind = IsAudioOnlyExtension(Path.GetExtension(dialog.FileName))
            ? LayerKind.UploadedAudioOnly
            : LayerKind.UploadedVideo;
        row.Layer = _layers.Add(kind, dialog.FileName);
        RefreshPreviewLive();
        StatusText.Text = $"Uploaded {row.DisplayName}: {Path.GetFileName(dialog.FileName)}";
    }

    private static bool IsAudioOnlyExtension(string ext) =>
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    private void RestoreTracksFromLayers()
    {
        _tracks.Clear();
        int slot = 1;
        foreach (var layer in _layers.Layers)
        {
            var row = new LayerRowViewModel(slot++) { Layer = layer };
            row.LiveParamChanged += DebounceRefreshPreview;
            _tracks.Add(row);
        }
        UpdateAddLayerButtonState();
    }

    // ----- Screen switch: Editor <-> Mixing (UI_Design_Spec v2) -----

    private void OpenMixing_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;

        _mixingLayer = row;
        MixingTopStripPanel.DataContext = row;
        MixingContentGrid.DataContext = row;
        MixingHeaderText.Text = $"Mixing — {row.DisplayName}";
        EditorScreen.Visibility = Visibility.Collapsed;
        MixingScreen.Visibility = Visibility.Visible;
        ShowSubtab(LimiterPanel);
    }

    private void BackToEditor_Click(object sender, RoutedEventArgs e)
    {
        MixingScreen.Visibility = Visibility.Collapsed;
        EditorScreen.Visibility = Visibility.Visible;
        _mixingLayer = null;
    }

    private void SubtabButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = sender switch
        {
            _ when ReferenceEquals(sender, LimiterTabButton) => LimiterPanel,
            _ when ReferenceEquals(sender, CompressorTabButton) => CompressorPanel,
            _ when ReferenceEquals(sender, NoiseGateTabButton) => NoiseGatePanel,
            _ when ReferenceEquals(sender, EqTabButton) => EqPanel,
            _ when ReferenceEquals(sender, MelodyneTabButton) => MelodynePanel,
            _ => LimiterPanel,
        };
        ShowSubtab(panel);
    }

    private void ShowSubtab(StackPanel selected)
    {
        foreach (var panel in new[] { LimiterPanel, CompressorPanel, NoiseGatePanel, EqPanel, MelodynePanel })
            panel.Visibility = ReferenceEquals(panel, selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditMelodyne_Click(object sender, RoutedEventArgs e)
    {
        new MelodyneEditorWindow { Owner = this }.ShowDialog();
    }

    // ----- Preview transport: Restart / Play-Stop / scrub / zoom (UI_Design_Spec v2) -----
    //
    // Video and audio are re-decoded on every Play/Seek by PreviewPlaybackEngine (measured fast
    // enough for the app's up-to-4-layer/small-clip scale -- see the throughput spike referenced
    // in the engine's doc comment), always off the UI thread here so a decode never blocks input.

    private void DebounceRefreshPreview()
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    /// <summary>Rebinds the engine to the current layer set and refreshes whatever's currently
    /// visible (a live-playing frame, or a static frame if paused) at the same timeline position
    /// -- this is the "no manual refresh step" mechanism the spec calls for.</summary>
    private void RefreshPreviewLive()
    {
        var layersSnapshot = _layers.Layers.ToList();

        if (layersSnapshot.Count == 0)
        {
            _previewEngine.SetLayers(layersSnapshot);
            _compositedFrame?.Dispose();
            _compositedFrame = null;
            CompositeCanvas.InvalidateVisual();
            UpdateTimelineRangeUi();
            return;
        }

        int generation = ++_previewRefreshGeneration;
        double positionMs = _previewEngine.PositionMs;

        Task.Run(() =>
        {
            _previewEngine.SetLayers(layersSnapshot);
            _previewEngine.Seek(positionMs);

            Dispatcher.Invoke(() =>
            {
                if (generation != _previewRefreshGeneration) return;
                UpdateTimelineRangeUi();
            });
        });
    }

    private void PlayStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewEngine.IsPlaying)
        {
            _previewEngine.Stop();
            PlayStopButton.Content = "▶ Play";
            StatusText.Text = "Preview stopped.";
            return;
        }

        if (_layers.Layers.Count == 0)
        {
            StatusText.Text = "Add at least one layer first.";
            return;
        }

        PlayStopButton.IsEnabled = false;
        StatusText.Text = "Starting preview...";
        var layersSnapshot = _layers.Layers.ToList();

        Task.Run(() =>
        {
            _previewEngine.SetLayers(layersSnapshot);
            _previewEngine.Play();

            Dispatcher.Invoke(() =>
            {
                PlayStopButton.IsEnabled = true;
                PlayStopButton.Content = "⏸ Stop";
                StatusText.Text = "Playing preview.";
                UpdateTimelineRangeUi();
            });
        });
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            _previewEngine.Restart();
            Dispatcher.Invoke(UpdateTimelineRangeUi);
        });
    }

    private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = false;
        double target = TimelineSlider.Value;
        Task.Run(() =>
        {
            _previewEngine.Seek(target);
            Dispatcher.Invoke(UpdateTimelineRangeUi);
        });
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isScrubbing)
            TimeReadoutText.Text = $"{FormatTime(TimelineSlider.Value)} / {FormatTime(_previewEngine.DurationMs)}";
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _pixelsPerSecond = Math.Min(400, _pixelsPerSecond * 1.5);
        UpdateTimelineRangeUi();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _pixelsPerSecond = Math.Max(10, _pixelsPerSecond / 1.5);
        UpdateTimelineRangeUi();
    }

    /// <summary>Zoom (see UI_Design_Spec v2) scales the timeline's horizontal pixel span, not the
    /// preview picture -- this only ever touches TimelineSlider.Width/Maximum, never CompositeCanvas.</summary>
    private void UpdateTimelineRangeUi()
    {
        double durationMs = _previewEngine.DurationMs;
        TimelineSlider.Maximum = Math.Max(1, durationMs);
        TimelineSlider.Width = Math.Max(200, _pixelsPerSecond * durationMs / 1000.0);
        if (!_isScrubbing) TimelineSlider.Value = Math.Min(_previewEngine.PositionMs, TimelineSlider.Maximum);
        TimeReadoutText.Text = $"{FormatTime(_previewEngine.PositionMs)} / {FormatTime(durationMs)}";
    }

    private static string FormatTime(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private void CompositeCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_compositedFrame is not null)
            canvas.DrawBitmap(_compositedFrame, new SKRect(0, 0, e.Info.Width, e.Info.Height));
    }

    // ----- Project save/load/export -----

    private void SaveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Acapella project|*.acapella.json", DefaultExt = ".acapella.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var dto = _projectPersistence.ToDto(_layers, _metronomeBpm, _lastCalibratedOffsetMs);
            // L7: enforce the suffix explicitly instead of relying on the dialog's own extension
            // logic (DefaultExt doesn't reliably stop a double-append for multi-segment extensions).
            string filePath = dialog.FileName.EndsWith(".acapella.json", StringComparison.OrdinalIgnoreCase)
                ? dialog.FileName
                : dialog.FileName + ".acapella.json";
            _projectPersistence.SaveToFile(dto, filePath);
            StatusText.Text = $"Project saved: {Path.GetFileName(filePath)}";
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
            _metronomeBpm = bpm;

            RestoreTracksFromLayers();
            RefreshPreviewLive();
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

        // M7: snapshot via a DTO round-trip so a mid-export layer/parameter edit can't race the
        // background export task's read of live state.
        var snapshotDto = _projectPersistence.ToDto(_layers, _metronomeBpm, _lastCalibratedOffsetMs);
        var (snapshotLayers, _, _) = _projectPersistence.FromDto(snapshotDto);

        Task.Run(() =>
        {
            try
            {
                new ExportEngine().Export(snapshotLayers, dialog.FileName);
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
