using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Acapella.App.ViewModels;
using Acapella.Engine.Composite;
using Acapella.Engine.Devices;
using Acapella.Engine.Export;
using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using Acapella.Engine.Project;
using Acapella.Engine.Settings;
using Microsoft.Win32;
using NAudio.Wave;
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

    private WasapiOut? _previewOutput;
    private SKBitmap? _compositedFrame;
    private double? _lastCalibratedOffsetMs;
    // Metronome now lives only inside RecordSetupWindow (see UI_Design_Spec.md); this just carries
    // the last-used BPM forward across dialogs and into project save/load.
    private double _metronomeBpm = 120;
    private string _dockSide = "Right";
    private int _previewMixGeneration;
    private int _compositeGeneration;

    public MainWindow()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;

        _dockSide = _settingsService.Load().TrackPanelDock;
        ApplyDockSide();
        UpdateAddLayerButtonState();
    }

    // ----- Dock side toggle -----

    private void DockToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _dockSide = _dockSide == "Left" ? "Right" : "Left";
        ApplyDockSide();

        var settings = _settingsService.Load();
        settings.TrackPanelDock = _dockSide;
        _settingsService.Save(settings);
    }

    private void ApplyDockSide()
    {
        DockPanel.SetDock(TrackPanelBorder, _dockSide == "Left" ? Dock.Left : Dock.Right);
        DockToggleButton.Content = _dockSide == "Left" ? "Dock right" : "Dock left";
    }

    // ----- Track panel: add / record / upload -----

    private void AddLayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }

        var row = new LayerRowViewModel(_tracks.Count + 1) { IsExpanded = true };
        row.AudioParamChanged += RebuildPreviewIfPlaying;
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
            row.IsExpanded = true;
            RefreshCompositePreview();
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
        row.IsExpanded = true;
        RefreshCompositePreview();
        StatusText.Text = $"Uploaded {row.DisplayName}: {Path.GetFileName(dialog.FileName)}";
    }

    private static bool IsAudioOnlyExtension(string ext) =>
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    private void EditMelodyne_Click(object sender, RoutedEventArgs e)
    {
        new MelodyneEditorWindow { Owner = this }.ShowDialog();
    }

    private void RestoreTracksFromLayers()
    {
        _tracks.Clear();
        int slot = 1;
        foreach (var layer in _layers.Layers)
        {
            var row = new LayerRowViewModel(slot++) { Layer = layer };
            row.AudioParamChanged += RebuildPreviewIfPlaying;
            _tracks.Add(row);
        }
        UpdateAddLayerButtonState();
    }

    // ----- Audio preview (explicit transport, not the "live" visual refresh -- see spec) -----

    private void PreviewPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOutput is not null)
        {
            _previewOutput.Stop();
            _previewOutput.Dispose();
            _previewOutput = null;
            PreviewPlayButton.Content = "Play mix";
            StatusText.Text = "Preview stopped.";
            return;
        }

        if (_layers.Layers.Count == 0)
        {
            StatusText.Text = "Add at least one layer first.";
            return;
        }

        StartOrRebuildPreviewMix();
    }

    /// <summary>M3: while preview is already playing, rebuild the whole graph from current
    /// parameters and swap it in so a slider change is audible without a manual restart.</summary>
    private void RebuildPreviewIfPlaying()
    {
        if (_previewOutput is null) return;
        StartOrRebuildPreviewMix();
    }

    private void StartOrRebuildPreviewMix()
    {
        int generation = ++_previewMixGeneration;
        var layersSnapshot = _layers.Layers.ToList();
        StatusText.Text = "Building preview mix...";

        Task.Run(() =>
        {
            try
            {
                const int sampleRate = 44100;
                var mixInputs = layersSnapshot
                    .Select(l => new MixLayerInput(l.LayerId, AudioShiftHelper.ApplyShift(
                        TrimHelper.ApplyTrim(AudioDecoder.DecodeToMonoFloat(l.SourcePath, sampleRate), l.TrimStartMs, l.TrimEndMs, sampleRate),
                        l.GetShiftMs(), sampleRate), sampleRate, l.MixParameters))
                    .ToList();

                var mix = _mixEngine.BuildMix(mixInputs, sampleRate);

                Dispatcher.Invoke(() =>
                {
                    if (generation != _previewMixGeneration) return; // superseded by a newer rebuild

                    _previewOutput?.Stop();
                    _previewOutput?.Dispose();

                    var outputDevice = _deviceCatalog.GetDefaultRenderDevice();
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    var device = enumerator.GetDevice(outputDevice.Id);
                    var newOutput = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
                    newOutput.Init(mix);
                    // M5: ArraySampleProvider signals end-of-stream instead of padding with
                    // infinite silence, so preview genuinely finishes on its own.
                    newOutput.PlaybackStopped += (s, e) => Dispatcher.Invoke(() =>
                    {
                        if (!ReferenceEquals(_previewOutput, newOutput)) return;
                        _previewOutput.Dispose();
                        _previewOutput = null;
                        PreviewPlayButton.Content = "Play mix";
                        StatusText.Text = "Preview finished.";
                    });
                    _previewOutput = newOutput;
                    newOutput.Play();
                    PreviewPlayButton.Content = "Stop preview";
                    StatusText.Text = $"Previewing mix of {mixInputs.Count} layer(s).";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (generation != _previewMixGeneration) return;
                    StatusText.Text = $"Preview failed: {ex.Message}";
                });
            }
        });
    }

    // ----- Visual composite preview: auto-refreshes on source changes, no manual button (spec) -----

    private void RefreshCompositePreview()
    {
        if (_layers.Layers.Count == 0)
        {
            _compositedFrame?.Dispose();
            _compositedFrame = null;
            CompositeCanvas.InvalidateVisual();
            return;
        }

        int generation = ++_compositeGeneration;
        var layersSnapshot = _layers.Layers.ToList();

        Task.Run(() =>
        {
            const int cellWidth = 320;
            const int cellHeight = 240;
            const int canvasWidth = cellWidth * 2;
            const int canvasHeight = cellHeight * 2;

            var frames = layersSnapshot
                .Select(l => l.Kind == LayerKind.UploadedAudioOnly
                    ? PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight)
                    : VideoFrameDecoder.DecodeFirstFrame(l.SourcePath, cellWidth, cellHeight) ?? PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight))
                .ToList();

            try
            {
                var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);
                var composited = Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects);

                Dispatcher.Invoke(() =>
                {
                    if (generation != _compositeGeneration) { composited.Dispose(); return; }

                    // L2: dispose the previous composited bitmap, not just the per-layer frames.
                    _compositedFrame?.Dispose();
                    _compositedFrame = composited;
                    CompositeCanvas.InvalidateVisual();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Preview render failed: {ex.Message}");
            }
            finally
            {
                foreach (var frame in frames)
                    frame.Dispose();
            }
        });
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
            RefreshCompositePreview();
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
