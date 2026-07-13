using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Acapella.App.ViewModels;
using Acapella.Engine.Devices;
using Acapella.Engine.Export;
using Acapella.Engine.Host;
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
    public static readonly RoutedCommand UndoCommand = new();
    public static readonly RoutedCommand RedoCommand = new();

    private readonly DeviceCatalog _deviceCatalog = new();
    private readonly SettingsService _settingsService = new();
    private readonly LayerCollection _layers = new();
    // L1: a directory next to the executable works regardless of how/where the app is launched.
    private readonly string _mediaDir = Path.Combine(AppContext.BaseDirectory, "media");

    // v6 P3: MixEngine's default constructor auto-selects a hosted FabFilter plugin over native
    // DSP wherever it's detected -- correct per the plan, but only once there's a way to actually
    // control the hosted plugin's parameters. That's the launcher + own-window editor from P3a
    // task 7, not yet built, so a hosted stage would silently run at its untouched factory-default
    // state while these sliders (bound to LayerMixParameters) do nothing audible. Force native
    // until that UI exists; drop this override once P3a task 7 ships.
    private readonly MixEngine _mixEngine = new(NoHostedPluginsAvailable.Instance);
    private readonly ProjectPersistenceService _projectPersistence = new();
    private readonly ObservableCollection<LayerRowViewModel> _tracks = new();
    private readonly PreviewPlaybackEngine _previewEngine = new(canvasWidth: 640, canvasHeight: 480, fps: 30, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
    private readonly DispatcherTimer _previewDebounceTimer;

    private SKBitmap? _compositedFrame;
    private readonly object _frameMailboxLock = new();
    private SKBitmap? _pendingFrame;
    private bool _framePumpQueued;
    private double? _lastCalibratedOffsetMs;
    // Metronome now lives only inside RecordSetupWindow (see UI_Design_Spec.md); this just carries
    // the last-used BPM forward across dialogs and into project save/load.
    private double _metronomeBpm = 120;
    private float _masterVolumeDb;
    private int _previewRefreshGeneration;
    private bool _isScrubbing;
    private double _pixelsPerSecond = 60;
    private LayerRowViewModel? _mixingLayer;

    // Undo/redo (v5 P1 task 2): snapshots the whole project DTO on each discrete edit (slider
    // release/focus-loss, not per-tick). _applyingHistory guards against re-pushing a snapshot
    // while an Undo/Redo restore itself is mutating bound view models.
    private readonly ProjectUndoStack _undoStack = new();
    private bool _applyingHistory;

    public MainWindow()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        UpdateAddLayerButtonState();

        _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewDebounceTimer.Tick += (s, e) => { _previewDebounceTimer.Stop(); RefreshPreviewLive(); };

        // Latest-frame-wins mailbox (audit A2/B14): FrameReady fires on the frame-loop thread, and
        // that thread must never block on the UI thread (a blocking Dispatcher.Invoke here used to
        // throttle the render loop by UI-thread availability -- slider updates, layout passes --
        // turning UI jank into permanent video lag). InvokeAsync queues at most one pending pump;
        // frames that arrive while a pump is already queued just overwrite the mailbox slot and
        // are disposed immediately rather than piling up a backlog.
        _previewEngine.FrameReady += frame =>
        {
            SKBitmap? overwritten;
            bool alreadyQueued;
            lock (_frameMailboxLock)
            {
                overwritten = _pendingFrame;
                _pendingFrame = frame;
                alreadyQueued = _framePumpQueued;
                _framePumpQueued = true;
            }
            overwritten?.Dispose();
            if (!alreadyQueued) Dispatcher.InvokeAsync(DrainFrameMailbox);
        };
        _previewEngine.PlaybackStopped += () => Dispatcher.Invoke(() => PlayStopButton.Content = "▶ Play");

        Closing += (s, e) => { _previewEngine.Dispose(); _compositedFrame?.Dispose(); _pendingFrame?.Dispose(); };

        _undoStack.Reset(CurrentProjectDto());
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ----- Undo/redo (v5 P1 task 2) -----

    private ProjectFileDto CurrentProjectDto() =>
        _projectPersistence.ToDto(_layers, _metronomeBpm, _lastCalibratedOffsetMs, _masterVolumeDb);

    /// <summary>Call after any discrete project edit completes (a slider release, a checkbox
    /// toggle, adding/recording/uploading a layer) -- never mid-drag, so undo steps correspond to
    /// one user-visible change each.</summary>
    private void PushUndoSnapshot()
    {
        if (_applyingHistory) return;
        _undoStack.Push(CurrentProjectDto());
    }

    private void RestoreProjectDto(ProjectFileDto dto)
    {
        _applyingHistory = true;
        try
        {
            var (loadedLayers, bpm, latencyOffset, masterVolumeDb) = _projectPersistence.FromDto(dto);
            _layers.Restore(loadedLayers.Layers);
            _lastCalibratedOffsetMs = latencyOffset;
            _metronomeBpm = bpm;
            _masterVolumeDb = masterVolumeDb;
            MasterVolumeSlider.Value = masterVolumeDb;

            RestoreTracksFromLayers();
            if (_mixingLayer is not null)
            {
                var stillPresent = _tracks.FirstOrDefault(t => t.Layer?.LayerId == _mixingLayer.Layer?.LayerId);
                _mixingLayer = stillPresent;
                if (stillPresent is not null)
                {
                    MixingScreen.DataContext = stillPresent;
                    MixingContentGrid.DataContext = stillPresent;
                }
                else
                {
                    BackToEditor_Click(this, new RoutedEventArgs());
                }
            }
            RefreshPreviewLive();
        }
        finally
        {
            _applyingHistory = false;
        }
    }

    private void UndoMenuItem_Click(object sender, RoutedEventArgs e) => PerformUndo();
    private void RedoMenuItem_Click(object sender, RoutedEventArgs e) => PerformRedo();
    private void UndoCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => PerformUndo();
    private void RedoCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => PerformRedo();

    private void PerformUndo()
    {
        var restored = _undoStack.Undo();
        if (restored is not null)
        {
            RestoreProjectDto(restored);
            StatusText.Text = "Undo.";
        }
    }

    private void PerformRedo()
    {
        var restored = _undoStack.Redo();
        if (restored is not null)
        {
            RestoreProjectDto(restored);
            StatusText.Text = "Redo.";
        }
    }

    /// <summary>Commit-style handler shared by every FX/mix-parameter slider (Style="{StaticResource
    /// CommitSlider}"): pushes an undo snapshot once the drag ends, not per-tick.</summary>
    private void CommitSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => PushUndoSnapshot();

    private void CommitCheckBox_Click(object sender, RoutedEventArgs e) => PushUndoSnapshot();

    private void CommitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PushUndoSnapshot();

    private void TrimTextBox_LostFocus(object sender, RoutedEventArgs e) => PushUndoSnapshot();

    private void LayerNameTextBox_LostFocus(object sender, RoutedEventArgs e) => PushUndoSnapshot();

    // ----- Keyboard transport shortcuts (v5 P1 task 6) -----

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't hijack typing in a trim/rename textbox.
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.Space:
                PlayStopButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(-5000);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(5000);
                e.Handled = true;
                break;
            case Key.Home:
                RestartButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void SeekBackButton_Click(object sender, RoutedEventArgs e) => SeekRelative(-5000);
    private void SeekForwardButton_Click(object sender, RoutedEventArgs e) => SeekRelative(5000);

    private void SeekRelative(double deltaMs)
    {
        double target = Math.Max(0, Math.Min(_previewEngine.DurationMs, _previewEngine.PositionMs + deltaMs));
        Task.Run(() =>
        {
            _previewEngine.Seek(target);
            Dispatcher.Invoke(UpdateTimelineRangeUi);
        });
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _masterVolumeDb = (float)e.NewValue;
        _previewEngine.MasterVolumeDb = _masterVolumeDb;
    }

    private void MasterVolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => PushUndoSnapshot();

    private void DrainFrameMailbox()
    {
        SKBitmap? frame;
        lock (_frameMailboxLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _framePumpQueued = false;
        }
        if (frame is null) return;

        var old = _compositedFrame;
        _compositedFrame = frame;
        CompositeCanvas.InvalidateVisual();
        old?.Dispose();
        if (!_isScrubbing) TimelineSlider.Value = Math.Min(_previewEngine.PositionMs, TimelineSlider.Maximum);
        TimeReadoutText.Text = $"{FormatTime(_previewEngine.PositionMs)} / {FormatTime(_previewEngine.DurationMs)}";
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
        // Not pushed to undo history yet: this row has no LayerModel until Record/Upload attaches
        // a source, so it isn't part of the ProjectFileDto snapshot ToDto serializes.
    }

    private void UpdateAddLayerButtonState() =>
        AddLayerButton.IsEnabled = _tracks.Count < LayerCollection.MaxLayers;

    private void RecordChoice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        OpenRecordSetupForRow(row);
    }

    /// <summary>Shared by a track row's own "Record" button and the Tools menu's "Recording
    /// setup..."/"Calibrate latency..." entries -- both funnel into the same capture-setup dialog
    /// (its Calibrate button is usable standalone, without completing a recording).</summary>
    private void OpenRecordSetupForRow(LayerRowViewModel row)
    {
        var dialog = new RecordSetupWindow(_deviceCatalog, _settingsService, _layers, _mixEngine, _mediaDir, _metronomeBpm) { Owner = this };
        bool? result = dialog.ShowDialog();
        _metronomeBpm = dialog.Bpm;

        if (result == true && dialog.CreatedLayer is not null)
        {
            // CellIndex binds to the row's own position (audit B8), not LayerCollection's
            // insertion order -- e.g. row 2 recording before row 1 must still land in grid cell 2.
            dialog.CreatedLayer.CellIndex = row.SlotNumber - 1;
            row.Layer = dialog.CreatedLayer;
            if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
            RefreshPreviewLive();
            StatusText.Text = $"Recorded {row.DisplayName}.";
            PushUndoSnapshot();
        }
    }

    private void RecordingSetupMenuItem_Click(object sender, RoutedEventArgs e)
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
        OpenRecordSetupForRow(row);
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
        var layer = _layers.Add(kind, dialog.FileName);
        // CellIndex binds to the row's own position (audit B8), not LayerCollection's insertion
        // order -- e.g. row 2 uploading before row 1 must still land in grid cell 2.
        layer.CellIndex = row.SlotNumber - 1;
        row.Layer = layer;
        if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
        RefreshPreviewLive();
        StatusText.Text = $"Uploaded {row.DisplayName}: {Path.GetFileName(dialog.FileName)}";
        PushUndoSnapshot();
    }

    private static bool IsAudioOnlyExtension(string ext) =>
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds sidebar rows keyed by CellIndex, not LayerCollection's internal list
    /// order (audit B8/B9): a project saved with gaps (e.g. only cells 0 and 2 populated) recreates
    /// rows 1 and 3 with their layers and rows 2 empty, rather than compacting layers into the
    /// first N rows and silently reassigning their grid cells.</summary>
    private void RestoreTracksFromLayers()
    {
        _tracks.Clear();
        var byCellIndex = _layers.Layers.ToDictionary(l => l.CellIndex);
        int maxCellIndex = byCellIndex.Count > 0 ? byCellIndex.Keys.Max() : -1;

        for (int cellIndex = 0; cellIndex <= maxCellIndex; cellIndex++)
        {
            var row = new LayerRowViewModel(cellIndex + 1);
            if (byCellIndex.TryGetValue(cellIndex, out var layer))
                row.Layer = layer;
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
        MixingScreen.DataContext = row;
        MixingContentGrid.DataContext = row;
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

    private void ShowLayerLabelsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _previewEngine.ShowLayerLabels = ShowLayerLabelsMenuItem.IsChecked;
        // Re-render whatever's currently visible so toggling the overlay is reflected immediately,
        // not just on the next Play/Seek.
        Task.Run(() =>
        {
            _previewEngine.Seek(_previewEngine.PositionMs);
        });
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
            var dto = CurrentProjectDto();
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
            var (loadedLayers, bpm, latencyOffset, masterVolumeDb) = _projectPersistence.FromDto(dto);

            _layers.Restore(loadedLayers.Layers);
            _lastCalibratedOffsetMs = latencyOffset;
            _metronomeBpm = bpm;
            _masterVolumeDb = masterVolumeDb;
            MasterVolumeSlider.Value = masterVolumeDb;

            RestoreTracksFromLayers();
            RefreshPreviewLive();
            StatusText.Text = $"Project opened: {Path.GetFileName(dialog.FileName)} ({loadedLayers.Layers.Count} layer(s)).";
            _undoStack.Reset(CurrentProjectDto());
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
        ExportMenuItem.IsEnabled = false;

        // M7: snapshot via a DTO round-trip so a mid-export layer/parameter edit can't race the
        // background export task's read of live state.
        var snapshotDto = CurrentProjectDto();
        var (snapshotLayers, _, _, snapshotMasterVolumeDb) = _projectPersistence.FromDto(snapshotDto);

        Task.Run(() =>
        {
            try
            {
                using var exportEngine = new ExportEngine(hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
                exportEngine.Export(snapshotLayers, dialog.FileName, masterVolumeDb: snapshotMasterVolumeDb);
                Dispatcher.Invoke(() => StatusText.Text = $"Export complete: {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Export failed: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() => ExportMenuItem.IsEnabled = true);
            }
        });
    }

    // ----- Menu bar: File > New, Help > About (v5 P1 task 1) -----

    private void NewProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _layers.Restore(Enumerable.Empty<LayerModel>());
        _tracks.Clear();
        _lastCalibratedOffsetMs = null;
        _masterVolumeDb = 0f;
        MasterVolumeSlider.Value = 0;
        UpdateAddLayerButtonState();
        RefreshPreviewLive();
        _undoStack.Reset(CurrentProjectDto());
        StatusText.Text = "New project.";
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Acapella\nMulti-layer vocal recording and mixing.", "About Acapella",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
