using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using Acapella.App.ViewModels;
using Acapella.Engine.Devices;
using Acapella.Engine.Export;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
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
    public static readonly RoutedCommand SaveCommand = new();
    public static readonly RoutedCommand SaveAsCommand = new();

    private const string MediaFileFilter = "Media files|*.mp4;*.mov;*.mkv;*.wav;*.mp3;*.m4a|All files|*.*";

    private readonly DeviceCatalog _deviceCatalog = new();
    private readonly SettingsService _settingsService = new();
    private readonly ProjectSession _session;
    private LayerCollection _layers => _session.Layers;
    // L1: a directory next to the executable works regardless of how/where the app is launched.
    private readonly string _mediaDir = Path.Combine(AppContext.BaseDirectory, "media");

    // v7 Q0 task 1 (audit A1): exactly one HostedPluginService for the whole app session, shared by
    // MixEngine, PreviewPlaybackEngine, ExportEngine, and (via LayerRowViewModel.SharedHostedService)
    // the Mixing screen's launcher buttons -- fixes the "three disconnected plugin-instance caches"
    // defect where editing a plugin was inaudible and export matched neither the editor nor the
    // preview. All lifecycle calls marshal through WpfHostedPluginDispatcher onto this window's
    // Dispatcher (A3/B8).
    private readonly HostedPluginService _hostedService = new(new HostedPluginAvailability(), new WpfHostedPluginDispatcher(Dispatcher.CurrentDispatcher));
    private readonly MixEngine _mixEngine;
    private readonly ObservableCollection<LayerRowViewModel> _tracks = new();
    private readonly PreviewPlaybackEngine _previewEngine;
    private readonly DispatcherTimer _previewDebounceTimer;
    // v7 Q0 task 8 (v6 task 9): polls open hosted-plugin editors for state changes at a fixed
    // interval, independent of the (self-resetting) preview-refresh debounce timer above -- a plugin
    // tweak needs to be captured even if the user never triggers another live-param change.
    private readonly DispatcherTimer _hostedStatePollTimer;

    // v7 Q1 task 3: ~30Hz meter refresh, separate timer from the 500ms hosted-state poll above
    // since meters need to feel live even when nothing else is happening.
    private readonly DispatcherTimer _meterPollTimer;

    // v8 redesign: 1Hz CPU/memory readout in the toolbar. Separate from the 33ms meter timer since
    // TotalProcessorTime deltas need a slower, coarser sampling interval to read as a stable percent.
    private readonly DispatcherTimer _perfPollTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

    private SKBitmap? _compositedFrame;
    private readonly object _frameMailboxLock = new();
    private SKBitmap? _pendingFrame;
    private bool _framePumpQueued;
    private bool _monitorMuted;
    private bool _syncingMasterVolume;
    private bool _isScrubbing;

    // v8 redesign: replaces the old two-screen Editor/Mixing split's _mixingLayer -- exactly one
    // mixer strip (a layer, or the Master strip) is selected at a time, driving the FX panel's
    // DataContext. Null LayerRowViewModel + _masterSelected=true means the Master strip.
    private LayerRowViewModel? _selectedLayer;
    private bool _masterSelected = true;

    // Undo/redo (v5 P1 task 2): one undo step per discrete edit (slider release/focus-loss, not
    // per-tick). _applyingHistory guards against re-pushing a snapshot while an Undo/Redo restore
    // itself is mutating bound view models.
    private bool _applyingHistory;

    public MainWindow()
    {
        // Must happen before any other bridge call, on this (the WPF UI) thread -- all hosted
        // plugin lifecycle/editor calls are required to run on the thread that initialized JUCE's
        // MessageManager (see HostedPluginInstance.Initialize's doc comment). _hostedService's
        // WpfHostedPluginDispatcher was already built against this same thread's Dispatcher above.
        HostedPluginInstance.Initialize();
        _mixEngine = new MixEngine(_hostedService);
        _session = new ProjectSession(_mixEngine);
        _previewEngine = new PreviewPlaybackEngine(canvasWidth: 640, canvasHeight: 480, fps: 30, hostedService: _hostedService);
        LayerRowViewModel.SharedHostedService = _hostedService;

        // Scan for installed FabFilter plugins off the critical Play path (audit B2) -- the first
        // real chain build should never stall on loading six plugin binaries.
        Task.Run(() => _hostedService.EnsureScanned());

        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        UpdateAddLayerButtonState();
        MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
        SelectMasterStrip();

        _session.SaveStateChanged += UpdateWindowTitle;
        UpdateWindowTitle();

        _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewDebounceTimer.Tick += (s, e) => { _previewDebounceTimer.Stop(); RefreshPreviewLive(); };

        // v7 Q0 task 8 (v6 task 9): poll the currently open Mixing-screen layer's hosted editors for
        // state changes every 500ms so a plugin tweak is captured (and the preview refreshed) even
        // if the user never triggers another live-param change while the editor is open.
        // v7 Q2 task 2: also pushes an undo snapshot per detected change burst -- previously a
        // plugin tweak refreshed the live preview but was invisible to undo/redo entirely (every
        // other mix-parameter edit pushes a snapshot via CommitSlider/CommitCheckBox_Click, but
        // nothing calls those for a knob dragged inside a plugin's own native editor window).
        _hostedStatePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hostedStatePollTimer.Tick += (s, e) =>
        {
            if (_selectedLayer?.PollEditorChanges() == true)
            {
                DebounceRefreshPreview();
                PushUndoSnapshot();
            }
        };
        _hostedStatePollTimer.Start();

        // v7 Q1 task 3: ~30Hz meter poll -- reads GetLayerLevels/GetMasterLevels straight off the
        // live PreviewPlaybackEngine (volatile fields on its meter taps, no command-queue round
        // trip) and drives the two ProgressBars. Only meaningful on the Mixing screen; runs
        // regardless of screen since it's cheap and DrainFrameMailbox-style always-on polling
        // already exists elsewhere in this file (audit consistency, not a new pattern).
        _meterPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterPollTimer.Tick += (s, e) => UpdateMeters();
        _meterPollTimer.Start();

        _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
        _lastCpuSampleAt = DateTime.UtcNow;
        _perfPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _perfPollTimer.Tick += (s, e) => UpdatePerfCounters();
        _perfPollTimer.Start();

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
        _previewEngine.PlaybackStopped += () => Dispatcher.Invoke(() => SetPlayStopContent(false));

        Closing += (s, e) =>
        {
            // Save-affordances spec: prompt FIRST, before anything is stopped or disposed -- a cancelled
            // close (Cancel, or Yes followed by a cancelled/failed save) must leave the app fully running.
            if (!ConfirmDiscardUnsavedChanges())
            {
                e.Cancel = true;
                return;
            }

            _hostedStatePollTimer.Stop();
            _meterPollTimer.Stop();
            _perfPollTimer.Stop();
            _previewEngine.Dispose();
            _mixEngine.Dispose();
            _hostedService.Dispose();
            _compositedFrame?.Dispose();
            _pendingFrame?.Dispose();
        };

        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ----- Undo/redo (v5 P1 task 2) -----

    /// <summary>Call after any discrete project edit completes (a slider release, a checkbox
    /// toggle, adding/recording/uploading a layer) -- never mid-drag, so undo steps correspond to
    /// one user-visible change each.</summary>
    private void PushUndoSnapshot()
    {
        if (_applyingHistory) return;
        _session.CommitEdit();
    }

    /// <summary>Applies a session-side restore (undo/redo/open/new) to the UI, suppressing the
    /// undo snapshots the bound view models would otherwise push while being repopulated.</summary>
    private bool ApplyRestore(Func<bool> restore)
    {
        _applyingHistory = true;
        try
        {
            if (!restore()) return false;

            MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
            MasterVolumeSlider.Value = _session.MasterVolumeDb;
            RestoreTracksFromLayers();
            if (!_masterSelected && _selectedLayer is not null)
            {
                var stillPresent = _tracks.FirstOrDefault(t => t.Layer?.LayerId == _selectedLayer.Layer?.LayerId);
                if (stillPresent is not null)
                    SelectMixerStrip(stillPresent);
                else
                    SelectMasterStrip();
            }
            RefreshPreviewLive();
            return true;
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
        if (ApplyRestore(_session.Undo))
            StatusText.Text = "Undo.";
    }

    private void PerformRedo()
    {
        if (ApplyRestore(_session.Redo))
            StatusText.Text = "Redo.";
    }

    /// <summary>Commit-style handler shared by every FX/mix-parameter slider (Style="{StaticResource
    /// CommitSlider}"): pushes an undo snapshot once the drag ends, not per-tick.</summary>
    private void CommitSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => PushUndoSnapshot();

    private void CommitCheckBox_Click(object sender, RoutedEventArgs e) => PushUndoSnapshot();

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
                if (_previewEngine.IsPlaying) StopButton_Click(this, new RoutedEventArgs());
                else PlayButton_Click(this, new RoutedEventArgs());
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

    private void SeekRelative(double deltaMs) => RunPreviewCommand(_previewEngine.SeekByAsync(deltaMs));

    /// <summary>Toolbar master-volume knob and the mixer's Master-strip fader both control the
    /// same value (image shows both) -- kept as two independent Sliders synced here rather than a
    /// shared binding source, guarded against the re-entrant ValueChanged each Slider.Value write
    /// below would otherwise trigger.</summary>
    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingMasterVolume) return;
        _session.MasterVolumeDb = (float)e.NewValue;
        ApplyMasterVolume();
        _syncingMasterVolume = true;
        MixerMasterFader.Value = e.NewValue;
        _syncingMasterVolume = false;
    }

    private void MixerMasterFader_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingMasterVolume) return;
        _session.MasterVolumeDb = (float)e.NewValue;
        ApplyMasterVolume();
        _syncingMasterVolume = true;
        MasterVolumeSlider.Value = e.NewValue;
        _syncingMasterVolume = false;
    }

    /// <summary>Applies the stored master volume unless the Monitor toggle has muted the output --
    /// muting never touches the session's MasterVolumeDb itself so un-muting restores exactly what the sliders
    /// still show.</summary>
    private void ApplyMasterVolume() => _previewEngine.MasterVolumeDb = _monitorMuted ? -96f : _session.MasterVolumeDb;

    private void MonitorToggle_Changed(object sender, RoutedEventArgs e)
    {
        _monitorMuted = MonitorToggle.IsChecked != true;
        ApplyMasterVolume();
    }

    private void MasterVolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => PushUndoSnapshot();

    private void MetronomeBpmTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(MetronomeBpmTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bpm) && bpm > 0)
            _session.MetronomeBpm = bpm;
        else
            MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
    }

    /// <summary>v8 redesign: 1Hz CPU/memory readout. CPU% derived from the process's own
    /// TotalProcessorTime delta over the wall-clock delta, normalized by core count (matches Task
    /// Manager's per-process convention, not a single-core-pegged 100%-per-core reading).</summary>
    private void UpdatePerfCounters()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpuNow = process.TotalProcessorTime;

        double wallElapsedMs = (now - _lastCpuSampleAt).TotalMilliseconds;
        if (wallElapsedMs > 0)
        {
            double cpuElapsedMs = (cpuNow - _lastCpuTime).TotalMilliseconds;
            double cpuPercent = 100.0 * cpuElapsedMs / (wallElapsedMs * Environment.ProcessorCount);
            CpuLoadText.Text = $"CPU {Math.Clamp(cpuPercent, 0, 100):F0}%";
        }
        _lastCpuTime = cpuNow;
        _lastCpuSampleAt = now;

        MemoryUsageText.Text = $"{process.WorkingSet64 / (1024 * 1024)} MB";
    }

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
        if (!_isScrubbing)
            TimelineSlider.Value = Math.Min(_previewEngine.PositionMs, TimelineSlider.Maximum);
        SetTimeReadout(_previewEngine.PositionMs, _previewEngine.DurationMs);
    }

    /// <summary>v8 redesign: Current timing and Total Duration are now two separate toolbar
    /// fields (image shows them apart), each h:mm:ss -- replaces the old single "0:00 / 0:00"
    /// combined readout.</summary>
    /// <summary>Highlights whichever of the separate Play/Stop buttons reflects the current
    /// transport state (image shows them as distinct buttons, not one toggling label).</summary>
    private void SetPlayStopContent(bool isPlaying)
    {
        PlayButton.Background = isPlaying ? (System.Windows.Media.Brush)Resources["AccentBrush"] : (System.Windows.Media.Brush)Resources["RowBrush"];
    }

    private void SetTimeReadout(double positionMs, double durationMs)
    {
        CurrentTimeText.Text = FormatTime(positionMs);
        TotalDurationText.Text = FormatTime(durationMs);
    }

    // ----- Hint panel (v8 redesign): shows a short instruction for whatever the pointer is over.
    // Only wired to a curated set of controls (transport, Melodyne launcher) per Q1's "create one,
    // show instruction to user" -- not every widget, to avoid the project's no-gold-plating rule. -----

    private const string DefaultHint = "Click a mixer channel to edit its FX. Space = Play/Stop.";

    private void Hint_MouseEnter(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string hint)
            HintText.Text = hint;
    }

    // ----- Custom window chrome (v8 redesign): minimize/restore/close glyphs in the toolbar,
    // wired through the standard SystemCommands so they behave exactly like OS titlebar buttons. -----

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void MainWindow_StateChanged(object? sender, EventArgs e) =>
        RestoreButton.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";

    /// <summary>v7 Q1 task 3 (v8: per-strip meters). Maps -60..0 dBFS RMS onto the 0..1 range
    /// (below -60dB reads as silence) -- a fixed floor is simpler than a full logarithmic meter
    /// ballistics model and good enough for "the meters respond" per the plan's [human] criterion.
    /// Now drives every visible mixer strip's MeterLevel (bound property) plus the master strip's
    /// two meter bars, instead of a single "currently mixing layer" bar.</summary>
    private void UpdateMeters()
    {
        const float FloorDb = -60f;
        float ToUnit(float db) => float.IsNegativeInfinity(db) ? 0f : Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);

        foreach (var row in _tracks)
        {
            if (row.Layer is null) { row.MeterLevel = 0; continue; }
            var (_, rmsDb) = _previewEngine.GetLayerLevels(row.Layer.LayerId);
            row.MeterLevel = ToUnit(rmsDb);
        }

        var (masterPeakDb, masterRmsDb) = _previewEngine.GetMasterLevels();
        MasterMeterBar.Value = ToUnit(masterRmsDb);
        ToolbarPeakMeter.Value = ToUnit(masterPeakDb);
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

    private void AddLayerMenuItem_Click(object sender, RoutedEventArgs e) => AddLayerButton_Click(sender, e);

    private void RecordChoice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        OpenRecordSetupForRow(row);
    }

    /// <summary>v7 Q3 (media-folder hygiene): recorded layerN.mkv files accumulate silently in
    /// _mediaDir with no visibility into how much disk they're using. Read-only report -- no
    /// deletion here, since deleting recorded media is a CLAUDE.md Pause Rule 1 action (needs the
    /// user's explicit say, not an automatic cleanup on close).</summary>
    private void RecordingsFolderSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_mediaDir))
        {
            MessageBox.Show(this, "No recordings folder yet.", "Recordings Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = Directory.GetFiles(_mediaDir, "*", SearchOption.AllDirectories);
        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        double mb = totalBytes / (1024.0 * 1024.0);
        MessageBox.Show(this, $"{_mediaDir}\n\n{files.Length} file(s), {mb:F1} MB.", "Recordings Folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Shared by a track row's own "Record" button and the Tools menu's "Recording
    /// setup..."/"Calibrate latency..." entries -- both funnel into the same capture-setup dialog
    /// (its Calibrate button is usable standalone, without completing a recording).</summary>
    private void OpenRecordSetupForRow(LayerRowViewModel row)
    {
        var dialog = new RecordSetupWindow(_deviceCatalog, _settingsService, _layers, _mixEngine, _mediaDir, _session.MetronomeBpm) { Owner = this };
        bool? result = dialog.ShowDialog();
        _session.MetronomeBpm = dialog.Bpm;

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

    private LayerRowViewModel AppendEmptyRow()
    {
        var row = new LayerRowViewModel(_tracks.Count + 1);
        row.LiveParamChanged += DebounceRefreshPreview;
        _tracks.Add(row);
        return row;
    }

    /// <summary>Attaches one uploaded media file to an empty row -- the single definition of "what an
    /// upload does to the model", shared by per-strip Upload and multi-file Import (spec D5). Does NOT
    /// refresh the preview, set status, or push undo: callers do those once per user action.</summary>
    private void AttachUploadedFile(LayerRowViewModel row, string filePath)
    {
        var kind = IsAudioOnlyExtension(Path.GetExtension(filePath))
            ? LayerKind.UploadedAudioOnly
            : LayerKind.UploadedVideo;
        var layer = _layers.Add(kind, filePath);
        // CellIndex binds to the row's own position (audit B8), not LayerCollection's insertion
        // order -- e.g. row 2 uploading before row 1 must still land in grid cell 2.
        layer.CellIndex = row.SlotNumber - 1;
        row.Layer = layer;                                               // MUST precede Name: Name's setter no-ops while Layer is null
        if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
    }

    private void UploadChoice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;

        var dialog = new OpenFileDialog { Filter = MediaFileFilter };
        if (dialog.ShowDialog() != true) return;

        AttachUploadedFile(row, dialog.FileName);
        UpdateAddLayerButtonState();                                     // new (D6)
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

    // ----- Mixer strip selection (v8 redesign, replaces the old Editor/Mixing screen switch) -----
    // Clicking a strip's Border (MixerStripTemplate/Master strip in MainWindow.xaml) switches which
    // layer's FX chain the FX panel shows; the Master strip has no FX (locked product decision) so
    // it shows a placeholder instead.

    private void MixerStrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        SelectMixerStrip(row);
    }

    private void MasterStrip_MouseDown(object sender, MouseButtonEventArgs e) => SelectMasterStrip();

    private void SelectMixerStrip(LayerRowViewModel row)
    {
        foreach (var track in _tracks) track.IsSelected = track == row;
        _selectedLayer = row;
        _masterSelected = false;
        FxPanel.DataContext = row;
        FxHeaderText.Text = $"FX — {row.DisplayName}";
        FxPlaceholderText.Visibility = Visibility.Collapsed;
        FxRackScrollViewer.Visibility = Visibility.Visible;
    }

    private void SelectMasterStrip()
    {
        foreach (var track in _tracks) track.IsSelected = false;
        _selectedLayer = null;
        _masterSelected = true;
        FxPanel.DataContext = null;
        FxHeaderText.Text = "FX — Master";
        FxPlaceholderText.Visibility = Visibility.Visible;
        FxRackScrollViewer.Visibility = Visibility.Collapsed;
    }

    // Bug audit A1/C3/C5: replaces the old stub (a plain dialog explaining itself) with a real
    // launcher for the layer's persistent ARA session's own Melodyne editor GUI -- mirrors
    // OpenEqEditor_Click etc. below. Returns false only when the layer's chain has never been built
    // with Manual2A selected yet (no session exists to show an editor for), which the status bar
    // message below explains rather than silently no-opping.
    private void EditMelodyne_Click(object sender, MouseButtonEventArgs e)
    {
        // Deliberately not gated on IsMelodyneHosted (unlike the FabFilter handlers below): the
        // real gate for Melodyne is "has this layer's persistent ARA session been created yet"
        // (requires a Play with Manual2A selected first), which OpenMelodyneEditor already checks
        // and reports via the status message -- gating on IsMelodyneHosted too made this silently
        // no-op whenever the ARA-capability scan's cache hadn't caught up yet, breaking the button.
        if (_selectedLayer is null) return;
        if (!_selectedLayer.OpenMelodyneEditor())
            StatusText.Text = "Play this layer once with Melodyne manual selected before editing.";
    }

    // ----- v6 P3 (v8 redesign: name-click, not a separate button): each slot's own checkbox is
    // its enable/disable, and clicking the FX name opens the live hosted plugin instance's own
    // editor window (never embedded -- P3a task 7); no-ops when that stage isn't hosted, since
    // there's no plugin editor to open (the native fallback sliders are what's shown instead). -----
    private void OpenEqEditor_Click(object sender, MouseButtonEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Eq);
    private void OpenNoiseGateEditor_Click(object sender, MouseButtonEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.NoiseGate);
    private void OpenCompressorEditor_Click(object sender, MouseButtonEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Compressor);
    private void OpenLimiterEditor_Click(object sender, MouseButtonEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Limiter);
    private void OpenReverbEditor_Click(object sender, MouseButtonEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Reverb);

    // ----- Preview transport: Restart / Play-Stop / scrub / zoom (UI_Design_Spec v2) -----
    //
    // Video and audio are re-decoded on every Play/Seek by PreviewPlaybackEngine (measured fast
    // enough for the app's up-to-4-layer/small-clip scale -- see the throughput spike referenced
    // in the engine's doc comment). Engine commands are awaited, never waited on, so a decode never
    // blocks input.

    private void DebounceRefreshPreview()
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    /// <summary>Awaits a preview command without blocking the UI thread, then refreshes the
    /// timeline. Returns false (and reports to the status bar, Q3 task 33) if the command failed.</summary>
    private async Task<bool> AwaitPreviewCommand(Task command)
    {
        try
        {
            await command;
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Preview error: {ex.Message}";
            return false;
        }
        finally
        {
            UpdateTimelineRangeUi();
        }
    }

    private async void RunPreviewCommand(Task command) => await AwaitPreviewCommand(command);

    /// <summary>Rebinds the engine to the current layer set and refreshes whatever's currently
    /// visible (a live-playing frame, or a static frame if paused) at the same timeline position
    /// -- this is the "no manual refresh step" mechanism the spec calls for.</summary>
    private async void RefreshPreviewLive()
    {
        if (_layers.Layers.Count == 0)
        {
            await AwaitPreviewCommand(_previewEngine.SetLayersAsync(_layers.Layers));
            _compositedFrame?.Dispose();
            _compositedFrame = null;
            CompositeCanvas.InvalidateVisual();
            return;
        }

        await AwaitPreviewCommand(_previewEngine.RefreshAsync(_layers.Layers));
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewEngine.IsPlaying) return;

        if (_layers.Layers.Count == 0)
        {
            StatusText.Text = "Add at least one layer first.";
            return;
        }

        PlayButton.IsEnabled = false;
        StatusText.Text = "Starting preview...";

        bool started = await AwaitPreviewCommand(SetLayersThenPlay());

        PlayButton.IsEnabled = true;
        SetPlayStopContent(started);
        if (started) StatusText.Text = "Playing preview.";

        async Task SetLayersThenPlay()
        {
            await _previewEngine.SetLayersAsync(_layers.Layers);
            await _previewEngine.PlayAsync();
        }
    }

    /// <summary>Stop resets the playhead to the start, not just pausing in place (media-player
    /// convention, per redesign feedback) -- always seeks to 0 even if playback was already
    /// stopped, so pressing Stop is also a reliable "rewind" shortcut.</summary>
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        bool wasPlaying = _previewEngine.IsPlaying;
        var stop = _previewEngine.StopAsync();
        var rewind = _previewEngine.SeekAsync(0);
        if (await AwaitPreviewCommand(Task.WhenAll(stop, rewind)) && wasPlaying)
        {
            SetPlayStopContent(false);
            StatusText.Text = "Preview stopped.";
        }
    }

    /// <summary>Toolbar's Record button (image shows it distinct from Play/Stop): opens the same
    /// capture-setup dialog as Tools > Recording setup... for the next empty layer slot.</summary>
    private void RecordButton_Click(object sender, RoutedEventArgs e) => RecordingSetupMenuItem_Click(sender, e);

    private void RestartButton_Click(object sender, RoutedEventArgs e) => RunPreviewCommand(_previewEngine.RestartAsync());

    private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = false;
        RunPreviewCommand(_previewEngine.SeekAsync(((Slider)sender).Value));
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isScrubbing)
            SetTimeReadout(((Slider)sender).Value, _previewEngine.DurationMs);
    }

    private void ShowLayerLabelsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _previewEngine.ShowLayerLabels = ShowLayerLabelsMenuItem.IsChecked;
        // Re-render whatever's currently visible so toggling the overlay is reflected immediately,
        // not just on the next Play/Seek.
        RunPreviewCommand(_previewEngine.SeekByAsync(0));
    }

    /// <summary>v8 redesign: the song-position bar now always spans the full row width (dropped
    /// the old scrollable/zoomable timeline, which structurally conflicted with "span from the
    /// hint panel to under the close button") -- this only ever touches TimelineSlider.Maximum/
    /// Value, never its Width, and never CompositeCanvas.</summary>
    private void UpdateTimelineRangeUi()
    {
        double durationMs = _previewEngine.DurationMs;
        TimelineSlider.Maximum = Math.Max(1, durationMs);
        if (!_isScrubbing)
            TimelineSlider.Value = Math.Min(_previewEngine.PositionMs, TimelineSlider.Maximum);
        SetTimeReadout(_previewEngine.PositionMs, durationMs);
    }

    /// <summary>v8 redesign: h:mm:ss (Q1 answer switches the reference layout's B:S:T bars/beats
    /// format to plain time, since Acapella has no bars/beats sequencing).</summary>
    private static string FormatTime(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void CompositeCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        if (_compositedFrame is not null)
            canvas.DrawBitmap(_compositedFrame, new SKRect(0, 0, e.Info.Width, e.Info.Height));
    }

    // ----- Project save/load/export -----

    private void SaveMenuItem_Click(object sender, RoutedEventArgs e) => SaveProject(forceDialog: false);
    /// <summary>File > Save As...</summary>
    private void SaveProjectButton_Click(object sender, RoutedEventArgs e) => SaveProject(forceDialog: true);
    private void SaveCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject(forceDialog: false);
    private void SaveAsCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject(forceDialog: true);

    /// <summary>Save (forceDialog=false: straight to CurrentFilePath, dialog only if untitled) or
    /// Save As (forceDialog=true: always the dialog). Returns true only if the file was actually
    /// written -- the discard prompt relies on that to abort on a cancelled dialog or failed write.</summary>
    // TODO(polish): flush focused TextBox before save/close (a trim or BPM edit still mid-focus
    // isn't committed by Ctrl+S/Alt+F4 -- see spec's Known limitations).
    private bool SaveProject(bool forceDialog)
    {
        string? filePath = forceDialog ? null : _session.CurrentFilePath;
        if (filePath is null)
        {
            var dialog = new SaveFileDialog { Filter = "Acapella project|*.acapella.json", DefaultExt = ProjectSession.ProjectFileSuffix };
            if (_session.CurrentFilePath is not null)
            {
                dialog.InitialDirectory = Path.GetDirectoryName(_session.CurrentFilePath);
                dialog.FileName = Path.GetFileName(_session.CurrentFilePath);
            }
            if (dialog.ShowDialog() != true) return false;

            // L7: enforce the suffix explicitly instead of relying on the dialog's own extension
            // logic (DefaultExt doesn't reliably stop a double-append for multi-segment extensions).
            // Dialog results only -- a project opened as plain *.json is saved back under its own name (D5).
            filePath = dialog.FileName.EndsWith(ProjectSession.ProjectFileSuffix, StringComparison.OrdinalIgnoreCase)
                ? dialog.FileName
                : dialog.FileName + ProjectSession.ProjectFileSuffix;
        }

        try
        {
            _session.Save(filePath);
            StatusText.Text = $"Project saved: {Path.GetFileName(filePath)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Before a dirty project is discarded (File > New, File > Open, window close): Yes =
    /// save first (may show the Save As dialog), No = discard, Cancel = abort. Returns true if the
    /// caller may go ahead and replace/close the project. Must run BEFORE any preview stop or
    /// teardown so that Cancel leaves everything running.</summary>
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!_session.IsDirty) return true;

        var answer = MessageBox.Show(this, $"Save changes to {_session.DisplayName}?", "Acapella",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(forceDialog: false),
            MessageBoxResult.No => true,
            _ => false,   // Cancel, or the message box's own close button
        };
    }

    /// <summary>Driven by ProjectSession.SaveStateChanged. Direct writes, matching the rest of this
    /// window (no INotifyPropertyChanged on MainWindow). Title shows in taskbar/Alt+Tab only
    /// (WindowStyle=None), hence the toolbar copy.</summary>
    private void UpdateWindowTitle()
    {
        string label = _session.IsDirty ? $"{_session.DisplayName} •" : _session.DisplayName;
        Title = $"Acapella — {label}";
        ProjectTitleText.Text = label;
    }

    private async void OpenProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";
            return;
        }

        if (!ConfirmDiscardUnsavedChanges()) return;

        var dialog = new OpenFileDialog { Filter = "Acapella project|*.acapella.json;*.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Bug audit #5: ProjectSession.Open() now releases every live hosted-plugin instance
            // before restoring. If preview playback is still running, HostedPluginSampleProvider
            // objects wired into its chain hold raw IHostedPlugin references that the WASAPI
            // render thread calls ProcessBlock on -- releasing out from under that is a
            // use-after-free on native memory. Stop playback first, same shape as StopButton_Click.
            await AwaitPreviewCommand(_previewEngine.StopAsync());

            ApplyRestore(() =>
            {
                _session.Open(dialog.FileName);
                return true;
            });
            StatusText.Text = $"Project opened: {Path.GetFileName(dialog.FileName)} ({_layers.Layers.Count} layer(s)).";
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

        var (snapshotLayers, snapshotMasterVolumeDb) = _session.SnapshotForExport();

        Task.Run(() =>
        {
            try
            {
                using var exportEngine = new ExportEngine(hostedService: _hostedService);
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

    private async void NewProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";
            return;
        }

        if (!ConfirmDiscardUnsavedChanges()) return;

        // Bug audit #5: same use-after-free hazard as OpenProjectButton_Click -- ProjectSession.New()
        // now releases every live hosted-plugin instance, so preview playback must be stopped first.
        await AwaitPreviewCommand(_previewEngine.StopAsync());

        ApplyRestore(() =>
        {
            _session.New();
            return true;
        });
        StatusText.Text = "New project.";
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Acapella\nMulti-layer vocal recording and mixing.", "About Acapella",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
