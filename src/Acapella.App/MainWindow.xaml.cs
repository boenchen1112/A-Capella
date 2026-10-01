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
using Acapella.App.Layout;
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

    // 1Hz CPU/memory readout in the toolbar. Separate from the 33ms meter timer since
    // TotalProcessorTime deltas need a slower, coarser sampling interval to read as a stable percent.
    private readonly DispatcherTimer _perfPollTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

    private SKBitmap? _compositedFrame;
    private readonly object _frameMailboxLock = new();
    private SKBitmap? _pendingFrame;
    private bool _framePumpQueued;
    private bool _monitorMuted;
    private bool _isScrubbing;

    // Exactly one of the fixed 4 lane rows is selected at a time, driving the channel panel's
    // DataContext.
    private LayerRowViewModel? _selectedLayer;

    // Undo/redo (v5 P1 task 2): one undo step per discrete edit (slider release/focus-loss, not
    // per-tick). _applyingHistory guards against re-pushing a snapshot while an Undo/Redo restore
    // itself is mutating bound view models.
    private bool _applyingHistory;

    // Background pitch rendering (PreviewPlaybackEngine.PitchRenderStarted/Completed/Failed):
    // counts renders currently in flight across all layers so the status bar reads "Processing
    // pitch..." for as long as any of them are still running, and only then.
    private int _pendingPitchRenderCount;

    public MainWindow()
    {
        // Must happen before any other bridge call, on this (the WPF UI) thread -- all hosted
        // plugin lifecycle/editor calls are required to run on the thread that initialized JUCE's
        // MessageManager (see HostedPluginInstance.Initialize's doc comment). _hostedService's
        // WpfHostedPluginDispatcher was already built against this same thread's Dispatcher above.
        HostedPluginInstance.Initialize();
        _mixEngine = new MixEngine(_hostedService);
        _session = new ProjectSession(_mixEngine);
        // 16:9 to match ExportEngine's 1280x720 default (half-scale, so cell math stays exact
        // integers): a 4:3 preview canvas letterboxed a 16:9 webcam in the preview even though it
        // fills its cell in the exported MP4, so what you saw while editing didn't match what you
        // got.
        _previewEngine = new PreviewPlaybackEngine(canvasWidth: 640, canvasHeight: 360, fps: 30, hostedService: _hostedService);
        LayerRowViewModel.SharedHostedService = _hostedService;

        // Scan for installed FabFilter plugins off the critical Play path (audit B2) -- the first
        // real chain build should never stall on loading six plugin binaries.
        Task.Run(() => _hostedService.EnsureScanned());

        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        EmptyCellOverlay.ItemsSource = _tracks;
        RestoreTracksFromLayers();   // Fixed 4-lane model: rows exist from app start, not grown on demand
        MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
        SelectLane(_tracks[0]);

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
        // Queued, never blocking: this fires from the frame-loop thread as it exits, and closing the
        // window waits on that thread from the UI thread -- a blocking Invoke here would deadlock.
        _previewEngine.PlaybackStopped += () => Dispatcher.InvokeAsync(() => SetPlayStopContent(false));

        // Background pitch rendering: these fire from a worker thread/thread-pool continuation, so
        // always InvokeAsync, never a blocking Invoke -- same deadlock hazard as PlaybackStopped
        // above if this ever landed while the UI thread is waiting inside Dispose/Closing.
        _previewEngine.PitchRenderStarted += _ => Dispatcher.InvokeAsync(() =>
        {
            _pendingPitchRenderCount++;
            StatusText.Text = "Processing pitch...";
        });
        _previewEngine.PitchRenderCompleted += _ => Dispatcher.InvokeAsync(() =>
        {
            _pendingPitchRenderCount = Math.Max(0, _pendingPitchRenderCount - 1);
            if (_pendingPitchRenderCount == 0) StatusText.Text = "Pitch correction updated.";
        });
        _previewEngine.PitchRenderFailed += (_, message) => Dispatcher.InvokeAsync(() =>
        {
            _pendingPitchRenderCount = Math.Max(0, _pendingPitchRenderCount - 1);
            StatusText.Text = $"Pitch correction failed: {message}";
        });

        Closing += (s, e) =>
        {
            // Refuse close mid-export: the teardown below would dispose the hosted service under the
            // export and exit, truncating the MP4 (bug audit #15).
            if (!ExportMenuItem.IsEnabled)
            {
                StatusText.Text = "Finish the export first.";
                e.Cancel = true;
                return;
            }

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

    private WindowState _lastWindowState = WindowState.Normal;

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
            var previousRows = _tracks.ToList();   // bug audit #9: RestoreTracksFromLayers clears _tracks

            if (!restore()) return false;

            MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
            MasterVolumeSlider.Value = _session.MasterVolumeDb;
            RestoreTracksFromLayers();
            // Bug audit #9: keep polling any FabFilter editor still open across the undo. AFTER
            // restore() (baseline = post-restore live state), BEFORE RefreshPreviewLive (no chain
            // build can create an instance in between), synchronous (no poll tick can land in between).
            LayerRowViewModel.CarryEditorTracking(previousRows, _tracks);
            // Close editor windows for any layer this restore dropped (e.g. an Undo back past the
            // point a layer was added) -- only the window, not the live instance underneath it (see
            // LayerRowViewModel.CloseAllOpenedEditors), so this is safe during playback/export.
            var survivingLayerIds = _tracks.Where(t => t.Layer is not null).Select(t => t.Layer!.LayerId).ToHashSet();
            foreach (var old in previousRows)
            {
                if (old.Layer is not null && !survivingLayerIds.Contains(old.Layer.LayerId))
                    old.CloseAllOpenedEditors();
            }
            var stillPresent = _selectedLayer is not null
                ? _tracks.FirstOrDefault(t => t.Layer?.LayerId == _selectedLayer.Layer?.LayerId)
                : null;
            SelectLane(stillPresent ?? _tracks[0]);
            RefreshPreviewLive();
            RedrawRulerCanvas();   // song in/out markers and the readout can change across undo/redo/open/new
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

    /// <summary>Bug audit #13: Slider.Value binds TwoWay with the framework-default
    /// UpdateSourceTrigger (PropertyChanged), unlike TextBox.Text -- so a keyboard-driven edit on a
    /// focused CommitSlider already applies live, with nothing to fire PreviewMouseUp. Commits on
    /// key-release (not key-down -- see the doc's ordering subtleties) for the same reason the mouse
    /// handler commits on release, not per-tick: one undo step per discrete edit. Filtered to
    /// Up/Down/PageUp/PageDown/End only -- Left/Right/Home are deliberately excluded: MainWindow's
    /// own transport shortcut (MainWindow_PreviewKeyDown, :267-291) already consumes those three for
    /// seek/restart before they ever reach a focused Slider's Value (root cause fact 3), so including
    /// them here would fire a commit on every seek performed while any FX slider happens to be
    /// focused -- not just a harmless no-op step, but one that clears the redo stack
    /// (ProjectUndoStack.Push, :52) on every such seek, silently destroying whatever the user could
    /// otherwise have redone.</summary>
    private static readonly Key[] SliderCommitKeys =
        { Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.End };

    private void CommitSlider_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (Array.IndexOf(SliderCommitKeys, e.Key) >= 0) PushUndoSnapshot();
    }

    private void CommitCheckBox_Click(object sender, RoutedEventArgs e) => PushUndoSnapshot();

    private void TrimTextBox_LostFocus(object sender, RoutedEventArgs e) => PushUndoSnapshot();

    // ----- Lane clip-bar drag-to-trim -----

    private ClipTrimDrag? _activeTrimDrag;
    private LayerRowViewModel? _activeTrimDragRow;
    private Point _trimDragStartMousePos;
    private Canvas? _trimDragCanvas;

    private static bool IsInHandle(Thumb thumb) => (string)thumb.Tag == "In";

    /// <summary>Snapshots the handle's starting value and the track's current scale. No-ops (leaves
    /// _activeTrimDrag null) when the source duration or timeline duration isn't known yet, so a
    /// drag started before MediaProbe returns has no effect rather than clamping against a bogus 0.</summary>
    private void TrimHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.DataContext is not LayerRowViewModel row || thumb.Parent is not Canvas canvas)
            return;
        if (row.TimelineDurationMs <= 0 || row.SourceDurationMs <= 0)
            return;

        _trimDragCanvas = canvas;
        _trimDragStartMousePos = Mouse.GetPosition(canvas);
        _activeTrimDragRow = row;
        _activeTrimDrag = IsInHandle(thumb)
            ? new ClipTrimDrag(ClipTrimDrag.Handle.In, row.TrimStartMs, row.EffectiveTrimEndMs, row.SourceDurationMs, row.TimelineDurationMs, canvas.ActualWidth)
            : new ClipTrimDrag(ClipTrimDrag.Handle.Out, row.EffectiveTrimEndMs, row.TrimStartMs, row.SourceDurationMs, row.TimelineDurationMs, canvas.ActualWidth);
    }

    /// <summary>Applies the drag's clamped value live, for immediate visual feedback on the clip
    /// bar -- the actual commit (PushUndoSnapshot) happens once, on release, in DragCompleted.</summary>
    private void TrimHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_activeTrimDrag is null || _activeTrimDragRow is null || _trimDragCanvas is null || sender is not Thumb thumb)
            return;

        double totalDeltaPx = Mouse.GetPosition(_trimDragCanvas).X - _trimDragStartMousePos.X;
        double newValueMs = _activeTrimDrag.ValueAt(totalDeltaPx);
        if (IsInHandle(thumb)) _activeTrimDragRow.TrimStartMs = newValueMs;
        else _activeTrimDragRow.TrimEndMs = newValueMs;
    }

    /// <summary>One undo step per completed drag -- none at all if the pointer never actually moved
    /// the value (same no-op-skip rule as CommitNameIfChanged).</summary>
    private void TrimHandle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_activeTrimDrag is not null && _activeTrimDragRow is not null && sender is Thumb thumb)
        {
            double currentValueMs = IsInHandle(thumb) ? _activeTrimDragRow.TrimStartMs : _activeTrimDragRow.EffectiveTrimEndMs;
            if (_activeTrimDrag.HasChangedFrom(currentValueMs))
                PushUndoSnapshot();
        }

        _activeTrimDrag = null;
        _activeTrimDragRow = null;
        _trimDragCanvas = null;
    }

    // ----- Keyboard transport shortcuts (v5 P1 task 6) -----

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't hijack typing in a trim/rename textbox.
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.Space:
                // Bug audit #14: Space is ButtonBase's own activation key (Button/CheckBox/
                // ToggleButton/RadioButton all invoke Click on Space) -- don't steal it from a
                // focused one of those, or it silently fails to toggle/click and fires Play/Stop
                // instead (see doc's root cause 2-3). Checked here, not added to the TextBox
                // exclusion above, because Left/Right/Home have no such conflict for these
                // controls (root cause fact 3's table lists none) -- see Ordering subtleties for
                // why broadening the exclusion is a regression, not a simplification.
                if (Keyboard.FocusedElement is ButtonBase) return;
                // PlayButton is a single Play/Stop toggle (PlayButton_Click
                // itself branches on _previewEngine.IsPlaying), so Space just clicks it once.
                PlayButton_Click(this, new RoutedEventArgs());
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
            case Key.I:
                SetSongIn();
                e.Handled = true;
                break;
            case Key.O:
                SetSongOut();
                e.Handled = true;
                break;
        }
    }

    private void SeekBackButton_Click(object sender, RoutedEventArgs e) => SeekRelative(-5000);
    private void SeekForwardButton_Click(object sender, RoutedEventArgs e) => SeekRelative(5000);

    private void SeekRelative(double deltaMs) => RunPreviewCommand(_previewEngine.SeekByAsync(deltaMs));

    /// <summary>MasterVolumeSlider is the single master-volume control (there is no separate
    /// mixer-strip fader).</summary>
    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _session.MasterVolumeDb = (float)e.NewValue;
        ApplyMasterVolume();
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

    /// <summary>1Hz CPU/memory readout. CPU% derived from the process's own
    /// TotalProcessorTime delta over the wall-clock delta, normalized by core count (matches Task
    /// Manager's per-process convention, not a single-core-pegged 100%-per-core reading).</summary>
    private void UpdatePerfCounters()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpuNow = process.TotalProcessorTime;

        double cpuPercent = 0;
        double wallElapsedMs = (now - _lastCpuSampleAt).TotalMilliseconds;
        if (wallElapsedMs > 0)
        {
            double cpuElapsedMs = (cpuNow - _lastCpuTime).TotalMilliseconds;
            cpuPercent = 100.0 * cpuElapsedMs / (wallElapsedMs * Environment.ProcessorCount);
        }
        _lastCpuTime = cpuNow;
        _lastCpuSampleAt = now;

        long memoryMb = process.WorkingSet64 / (1024 * 1024);
        PerfText.Text = $"CPU {Math.Clamp(cpuPercent, 0, 100):F0}% · {memoryMb} MB";
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

    /// <summary>Current timing and Total Duration are two separate toolbar fields, each h:mm:ss,
    /// rather than a single combined "0:00 / 0:00" readout.</summary>
    /// <summary>PlayButton is a single toggling 48px circle; swap its glyph between play/stop
    /// rather than driving two separate buttons.</summary>
    private void SetPlayStopContent(bool isPlaying)
    {
        PlayButton.Content = isPlaying ? "■" : "▶";
    }

    private void SetTimeReadout(double positionMs, double durationMs)
    {
        CurrentTimeText.Text = FormatTime(positionMs);
        TotalDurationText.Text = FormatTime(durationMs);
    }

    // ----- Custom window chrome: minimize/restore/close glyphs in the toolbar,
    // wired through the standard SystemCommands so they behave exactly like OS titlebar buttons. -----

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>Besides swapping the restore-button glyph, hides every open hosted-plugin (and
    /// Melodyne/ARA) editor window while the app window is minimized, and shows them again on
    /// restore -- a minimized app with its FabFilter/Melodyne editors still floating on screen looks
    /// broken, and leaving them open also means they outlive the window that owns them (Alt+Tab
    /// still cycles to Pro-Q even though "Acapella" is minimized). Only hides/shows
    /// (LayerRowViewModel.SetOpenedEditorsVisible), never closes -- the user's editor layout survives
    /// the round-trip exactly as left.</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        RestoreButton.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";

        bool wasMinimized = _lastWindowState == WindowState.Minimized;
        bool isMinimized = WindowState == WindowState.Minimized;
        _lastWindowState = WindowState;
        if (isMinimized != wasMinimized)
        {
            foreach (var row in _tracks)
                row.SetOpenedEditorsVisible(!isMinimized);
        }
    }

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

        var (masterPeakDb, _) = _previewEngine.GetMasterLevels();
        ToolbarPeakMeter.Value = ToUnit(masterPeakDb);
    }

    // ----- Track sidebar: record / upload. Fixed 4-lane model: no Add/remove-row
    // machinery; UpdateLaneHeaderCount tracks how many of the 4 lanes are filled. -----

    /// <summary>Refreshes the lanes panel's "Layers · n of 4" header and the transport Record
    /// button's "Record layer N" label/enabled state.</summary>
    private void UpdateLaneHeaderCount()
    {
        LaneHeaderCountText.Text = $"Layers · {_layers.Layers.Count} of {LayerCollection.MaxLayers}";
        int? freeCell = LowestFreeCell();
        RecordButtonText.Text = freeCell is int cell ? $"● Record layer {cell + 1}" : "● Record (full)";
        RecordButton.IsEnabled = freeCell is not null;
    }

    private void RecordChoice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        OpenRecordSetupForRow(row);
    }

    /// <summary>v7 Q3 (media-folder hygiene): recorded take files accumulate silently in
    /// _mediaDir with no visibility into how much disk they're using. Read-only report -- no
    /// deletion here, since deleting recorded media is irreversible (needs the
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

    /// <summary>The ONLY place RecordSetupWindow is constructed (retake spec D4), so bug audit #8's
    /// export gate covers every way into Recording setup. Returns null when the gate refuses
    /// (StatusText already set); otherwise the closed dialog and whether a take was accepted.
    /// Invariant (bug audit #8): no await / dispatcher pumping between the gate and ShowDialog.</summary>
    private (RecordSetupWindow Dialog, bool Recorded)? ShowRecordSetupDialog(LayerRowViewModel? retakeRow)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";   // bug audit #8
            return null;
        }

        var dialog = new RecordSetupWindow(_deviceCatalog, _settingsService, _layers, _mixEngine, _mediaDir, _session.MetronomeBpm,
                                           retakeLayerId: retakeRow?.Layer?.LayerId) { Owner = this };
        if (retakeRow is not null) dialog.Title = $"Re-record {retakeRow.DisplayName}";
        bool recorded = dialog.ShowDialog() == true;
        _session.MetronomeBpm = dialog.Bpm;
        return (dialog, recorded);
    }

    /// <summary>Opens the capture-setup dialog for a NEW layer. If a take is recorded, attaches it to
    /// the row that resolveRow returns. resolveRow runs ONLY on success, AFTER the dialog closes, so a
    /// cancelled or calibrate-only session never creates or claims a row (bug audit #7).</summary>
    private void OpenRecordSetup(Func<LayerRowViewModel?> resolveRow)
    {
        var shown = ShowRecordSetupDialog(retakeRow: null);
        if (shown is null) return;
        var (dialog, recorded) = shown.Value;

        if (recorded && dialog.CreatedLayer is not null)
        {
            var row = resolveRow();
            if (row is null) return;   // unreachable in practice: the dialog refuses to record at the cap (RecordSetupWindow.xaml.cs:130)

            // CellIndex binds to the row's own position (audit B8), not LayerCollection's
            // insertion order -- e.g. row 2 recording before row 1 must still land in grid cell 2.
            dialog.CreatedLayer.CellIndex = row.SlotNumber - 1;
            row.Layer = dialog.CreatedLayer;
            if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
            UpdateLaneHeaderCount();
            RefreshPreviewLive();
            StatusText.Text = $"Recorded {row.DisplayName}.";
            PushUndoSnapshot();
        }
    }

    /// <summary>A strip's own Rec button: records into that strip.</summary>
    private void OpenRecordSetupForRow(LayerRowViewModel row) => OpenRecordSetup(() => row);

    /// <summary>Toolbar ⏺ and Tools > Recording setup...: records into the lowest free layer slot,
    /// reusing an existing empty strip before appending one (bug audit #7).</summary>
    private void RecordingSetupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        int? targetCell = LowestFreeCell();   // read BEFORE ShowDialog -- see bug audit #7 step 3
        if (targetCell is null)
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }
        OpenRecordSetup(() => RowForCell(targetCell.Value));
    }

    /// <summary>Tools > Calibrate latency...: opens the same dialog for its Calibrate button. It never
    /// creates or claims a row and is not blocked by the layer cap, because calibration is not a
    /// layer. If the user records from here anyway, the take goes to the lowest free slot, as with ⏺.</summary>
    private void CalibrateLatencyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        int? targetCell = LowestFreeCell();   // same ordering rule; null (a full project) is fine here
        OpenRecordSetup(() => targetCell is int cell ? RowForCell(cell) : null);
    }

    // ----- Retake a layer in place (retake spec) -----

    /// <summary>Retake spec D8: the row is still on screen and still wraps a layer that is in the live
    /// collection (Undo/Open/New rebuild rows AND LayerModels, so a captured row can go stale).</summary>
    private bool IsLiveRow(LayerRowViewModel row) =>
        _tracks.Contains(row) && row.Layer is not null && _layers.Layers.Contains(row.Layer);

    /// <summary>A populated strip's ↻ button: a two-item menu built in code (spec D6 -- a XAML
    /// ContextMenu inside a DataTemplate has its own DataContext/visual-tree pitfalls).</summary>
    private void RetakeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not LayerRowViewModel row || !IsLiveRow(row)) return;

        var reRecord = new MenuItem { Header = "Re-record..." };
        reRecord.Click += (_, _) => RetakeRecord(row);
        var replace = new MenuItem { Header = "Replace with file..." };
        replace.Click += (_, _) => RetakeReplaceWithFile(row);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(reRecord);
        menu.Items.Add(replace);
        menu.IsOpen = true;
    }

    /// <summary>Re-record this layer in place: guide = the OTHER layers, not blocked by the cap, same
    /// LayerId/cell/name/FX; one undo step. Goes through the single #8-gated helper (spec D4).
    /// Never calls LowestFreeCell/RowForCell/Add (spec (d)); no await anywhere (spec (c)).</summary>
    private void RetakeRecord(LayerRowViewModel row)
    {
        if (!IsLiveRow(row)) return;
        var target = row.Layer!;                                          // fixed BEFORE ShowDialog (spec (d))

        var shown = ShowRecordSetupDialog(row);
        if (shown is null) return;                                        // #8 gate refused; status already set
        var (dialog, recorded) = shown.Value;
        if (!recorded || dialog.RetakeResult is not { } take) return;     // cancelled / closed: nothing changes

        // Defensive (spec D8): unreachable in practice -- the dialog is modal and disables this window.
        if (!IsLiveRow(row) || !ReferenceEquals(row.Layer, target))
        {
            StatusText.Text = $"The layer changed while recording; the take was kept as {Path.GetFileName(take.SourcePath)} but not attached.";
            return;
        }

        // Invariant (spec (e)): no await / MessageBox / dialog from here to PushUndoSnapshot().
        target.ReplaceSource(LayerKind.RecordedAV, take.SourcePath, take.CalibratedOffsetMs);
        row.NotifySourceReplaced();                                       // NOT row.Layer = ... (spec D5)
        RefreshPreviewLive();
        StatusText.Text = $"Re-recorded {row.DisplayName}.";
        PushUndoSnapshot();                                               // ONE step: source + trims + offset (+ any BPM change)
    }

    /// <summary>Replace this layer's source with a file, in place: same kind rule as Upload, trims and
    /// offsets reset, LayerId/cell/name/FX kept; one undo step. No export gate, like Upload (spec D7).</summary>
    private void RetakeReplaceWithFile(LayerRowViewModel row)
    {
        if (!IsLiveRow(row)) return;

        var dialog = new OpenFileDialog { Filter = MediaFileFilter, Title = $"Replace {row.DisplayName} with file" };
        if (dialog.ShowDialog() != true) return;
        if (!IsLiveRow(row)) return;                                      // defensive (spec D8)

        // Invariant (spec (e)): no await / MessageBox / dialog from here to PushUndoSnapshot().
        row.Layer!.ReplaceSource(UploadedKindFor(dialog.FileName), dialog.FileName, calibratedOffsetMs: 0);
        row.NotifySourceReplaced();                                       // NOT row.Layer = ... (spec D5)
        RefreshPreviewLive();
        StatusText.Text = $"Replaced {row.DisplayName} with {Path.GetFileName(dialog.FileName)}.";
        PushUndoSnapshot();
    }

    private int? LowestFreeCell()
    {
        var free = LayerImportPlanner.FreeCells(_layers.Layers.Select(l => l.CellIndex));
        return free.Count > 0 ? free[0] : null;
    }

    /// <summary>The row for a grid cell. Rows are a fixed set of exactly
    /// LayerCollection.MaxLayers, always present from app start, so this is just an index now
    /// (no growth/append).</summary>
    private LayerRowViewModel RowForCell(int cellIndex) => _tracks[cellIndex];

    /// <summary>The single "what LayerKind is an uploaded file" rule -- Upload, Import and Replace with
    /// file all use it (retake spec D7).</summary>
    private static LayerKind UploadedKindFor(string filePath) =>
        IsAudioOnlyExtension(Path.GetExtension(filePath)) ? LayerKind.UploadedAudioOnly : LayerKind.UploadedVideo;

    /// <summary>Attaches one uploaded media file to an empty row -- the single definition of "what an
    /// upload does to the model", shared by per-strip Upload and multi-file Import (spec D5). Does NOT
    /// refresh the preview, set status, or push undo: callers do those once per user action.</summary>
    private void AttachUploadedFile(LayerRowViewModel row, string filePath)
    {
        var layer = _layers.Add(UploadedKindFor(filePath), filePath);
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
        UpdateLaneHeaderCount();
        RefreshPreviewLive();
        StatusText.Text = $"Uploaded {row.DisplayName}: {Path.GetFileName(dialog.FileName)}";
        PushUndoSnapshot();
    }

    /// <summary>Add > Import files as layers... and the mixer's "+ Import files..." button: one
    /// multi-select dialog; files (sorted by name, spec D3) fill free slots lowest-first up to the
    /// 4-layer cap, each attached exactly like a per-strip Upload; the whole batch is ONE undo step
    /// (spec D2). Overflow is reported in StatusText, never a dialog.</summary>
    private void ImportFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_layers.Layers.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = "No empty layer slots (4-layer cap reached).";
            return;
        }

        var dialog = new OpenFileDialog { Filter = MediaFileFilter, Multiselect = true, Title = "Import files as layers" };
        if (dialog.ShowDialog() != true) return;

        // Occupancy is read AFTER the dialog closes, not before it opens.
        var (assignments, skipped) = LayerImportPlanner.Plan(_layers.Layers.Select(l => l.CellIndex), dialog.FileNames);

        // Invariant: from here to PushUndoSnapshot() there must be no await, MessageBox, dialog or other
        // dispatcher pumping -- _hostedStatePollTimer can push an undo snapshot on any pump, which would
        // record a half-imported project as its own undo step (breaks spec D2).
        foreach (var (cellIndex, filePath) in assignments)
            AttachUploadedFile(_tracks[cellIndex], filePath);

        UpdateLaneHeaderCount();
        if (assignments.Count > 0)
        {
            RefreshPreviewLive();                                        // once per batch, not per file
            PushUndoSnapshot();                                          // ONE undo step + one MarkDirty for the batch
        }

        StatusText.Text = FormatImportStatus(assignments.Count, skipped);
    }

    private static string FormatImportStatus(int imported, IReadOnlyList<string> skipped)
    {
        static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
        string text = $"Imported {Files(imported)}";
        if (skipped.Count > 0)
            text += $"; {Files(skipped.Count)} skipped (4-layer cap reached): {string.Join(", ", skipped.Select(Path.GetFileName))}";
        return text + ".";
    }

    private static bool IsAudioOnlyExtension(string ext) =>
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds the fixed 4 lane rows keyed by CellIndex, not LayerCollection's
    /// internal list order: a project saved with gaps (e.g. only cells 0 and 2
    /// populated) recreates rows 1 and 3 with their layers and rows 2/4 empty, never compacting
    /// layers into the first N rows or reassigning their grid cells. Always exactly MaxLayers rows,
    /// present even for a brand-new project, since the lanes panel no longer grows on demand.</summary>
    private void RestoreTracksFromLayers()
    {
        _tracks.Clear();
        var byCellIndex = _layers.Layers.ToDictionary(l => l.CellIndex);

        for (int cellIndex = 0; cellIndex < LayerCollection.MaxLayers; cellIndex++)
        {
            var row = new LayerRowViewModel(cellIndex + 1);
            if (byCellIndex.TryGetValue(cellIndex, out var layer))
                row.Layer = layer;
            row.LiveParamChanged += DebounceRefreshPreview;
            row.SlotToggled += (slot, enabled) => OnSlotToggled(row, slot, enabled);
            _tracks.Add(row);
        }
        UpdateLaneHeaderCount();
    }

    // ----- Lane selection -----
    // Clicking a lane row switches which layer's FX chain the channel panel shows.

    private void LaneRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        SelectLane(row);
    }

    private void SelectLane(LayerRowViewModel row)
    {
        foreach (var track in _tracks) track.IsSelected = track == row;
        _selectedLayer = row;
        ChannelPanel.DataContext = row;
        FxHeaderText.Text = row.DisplayName;
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

    // ----- Each slot's own checkbox is its enable/disable (not a separate button); clicking the
    // FX name opens the live hosted plugin instance's own editor window (never embedded);
    // no-ops when that stage isn't hosted, since there's no plugin editor to open
    // (the native fallback sliders are what's shown instead). -----
    private void OpenEqEditor_Click(object sender, RoutedEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Eq);
    private void OpenNoiseGateEditor_Click(object sender, RoutedEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.NoiseGate);
    private void OpenCompressorEditor_Click(object sender, RoutedEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Compressor);
    private void OpenLimiterEditor_Click(object sender, RoutedEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Limiter);
    private void OpenReverbEditor_Click(object sender, RoutedEventArgs e) => _selectedLayer?.OpenHostedEditor(FxSlots.Reverb);

    private void PitchModeMelodyne_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        row.PitchMode = LayerRowViewModel.PitchEngineMode.Melodyne;
        PushUndoSnapshot();
    }

    private void PitchModeAuto_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;
        row.PitchMode = LayerRowViewModel.PitchEngineMode.Auto;
        PushUndoSnapshot();
    }

    // ----- Lane name rename-in-place: inline TextBox in the lane row, committed only
    // on a real change via CommitNameIfChanged, matching the CommitSlider/CommitCheckBox pattern. -----

    private void LaneNameTextBox_GotFocus(object sender, RoutedEventArgs e) => ((TextBox)sender).SelectAll();

    private void LaneNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not LayerRowViewModel row) return;
        if (row.CommitNameIfChanged(textBox.Text))
            PushUndoSnapshot();
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();   // reflect the trimmed/reverted value
    }

    private void LaneNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ----- Delete layer: undoable, no cell compaction -- the row stays
    // in its slot, now empty, exactly like a row that was never recorded/uploaded into. -----

    private void ChannelMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var row = _selectedLayer;

        var deleteItem = new MenuItem { Header = "Delete layer", IsEnabled = row is not null && IsLiveRow(row) };
        deleteItem.Click += (_, _) => ConfirmAndDeleteLayer(row);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(deleteItem);
        menu.IsOpen = true;
    }

    private void DeleteLayerMenuItem_Click(object sender, RoutedEventArgs e) => ConfirmAndDeleteLayer(_selectedLayer);

    /// <summary>Asks before deleting (the slot is still undoable via Ctrl+Z, but a destructive
    /// action that removes a recorded/uploaded layer from the grid still warrants a confirmation).
    /// DeleteLayer itself stays a separate, dialog-free method so the actual delete logic is
    /// testable without a MessageBox blocking the test thread.</summary>
    private void ConfirmAndDeleteLayer(LayerRowViewModel? row)
    {
        if (row is null || !IsLiveRow(row)) return;

        var answer = MessageBox.Show(this, $"Delete layer {row.SlotNumber}? You can undo this.", "Delete Layer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        DeleteLayer(row);
    }

    private void DeleteLayer(LayerRowViewModel? row)
    {
        if (row is null || !IsLiveRow(row)) return;

        string name = row.DisplayName;
        row.CloseAllOpenedEditors();
        _layers.Remove(row.Layer!);
        row.Layer = null;   // the slot stays, now empty -- no cell compaction (locked product decision)
        UpdateLaneHeaderCount();
        RefreshPreviewLive();
        if (ReferenceEquals(_selectedLayer, row))
            SelectLane(row);   // re-show the now-empty row's (now empty) channel panel
        StatusText.Text = $"Deleted {name}.";
        PushUndoSnapshot();
    }

    // ----- Preview transport: Restart / Play-Stop / scrub / zoom -----
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

    /// <summary>A slot power toggle tries to apply in place to the already-running chain first
    /// (instant, no restart/glitch); only falls back to the usual debounced full refresh when
    /// there's no live node for it yet (e.g. the slot has never been enabled this session) or it's
    /// reverb, whose tail extension changes the project's duration and always needs a real
    /// rebuild.</summary>
    private void OnSlotToggled(LayerRowViewModel row, FxSlot slot, bool enabled)
    {
        if (row.Layer is null || !_previewEngine.TrySetSlotEnabled(row.Layer.LayerId, slot.Stage, enabled))
            DebounceRefreshPreview();
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

    /// <summary>PlayButton is a single toggling 48px circle, not separate
    /// Play/Stop buttons -- branches on IsPlaying first so Space (which just clicks this once, see
    /// MainWindow_PreviewKeyDown) toggles correctly either way.</summary>
    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewEngine.IsPlaying)
        {
            await StopPlaybackAsync();
            return;
        }

        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";   // bug audit #8
            return;
        }

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
        // Don't stomp "Processing pitch..." if a background render's Started handler (queued via
        // InvokeAsync, same as this continuation) already landed first -- SetLayersThenPlay's own
        // BuildLayerChain call is exactly what can kick one off.
        if (started && _pendingPitchRenderCount == 0) StatusText.Text = "Playing preview.";

        async Task SetLayersThenPlay()
        {
            await _previewEngine.SetLayersAsync(_layers.Layers);
            await _previewEngine.PlayAsync();
        }
    }

    /// <summary>Stop resets the playhead to the start, not just pausing in place (media-player
    /// convention, per redesign feedback) -- always seeks to 0 even if playback was already
    /// stopped, so pressing Stop is also a reliable "rewind" shortcut.</summary>
    private async Task StopPlaybackAsync()
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
    /// capture-setup dialog as Tools > Recording setup... for the next empty layer slot (bug audit
    /// #7: reuses an empty strip before appending).</summary>
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

    /// <summary>The song-position bar always spans the full row width (there is no
    /// scrollable/zoomable timeline) -- this only ever touches TimelineSlider.Maximum/
    /// Value, never its Width, and never CompositeCanvas.</summary>
    private void UpdateTimelineRangeUi()
    {
        double durationMs = _previewEngine.DurationMs;
        TimelineSlider.Maximum = Math.Max(1, durationMs);
        if (!_isScrubbing)
            TimelineSlider.Value = Math.Min(_previewEngine.PositionMs, TimelineSlider.Maximum);
        SetTimeReadout(_previewEngine.PositionMs, durationMs);
        foreach (var row in _tracks) row.TimelineDurationMs = durationMs;
        RedrawRulerCanvas();
    }

    private void RulerCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawRulerCanvas();

    /// <summary>Draws the ruler's time-tick labels (0:00, 0:30, 1:00 ... scaled to the project
    /// duration) plus the song in/out markers, and updates the readout text beside them. Rebuilt
    /// from scratch on every call (cheap: a handful of TextBlocks/lines) rather than diffed, so a
    /// duration or track-width change, or a song in/out edit, always redraws correctly.</summary>
    private void RedrawRulerCanvas()
    {
        RulerCanvas.Children.Clear();
        double durationMs = _previewEngine.DurationMs;
        double widthPx = RulerCanvas.ActualWidth;

        foreach (double tickMs in ClipTimelineMath.ComputeTickTimesMs(durationMs, widthPx))
        {
            var (x, _) = ClipTimelineMath.ToPixelRange(tickMs, tickMs, durationMs, widthPx);
            var label = new TextBlock
            {
                Text = ClipTimelineMath.FormatTickLabel(tickMs),
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, 2);
            RulerCanvas.Children.Add(label);
        }

        AddSongRangeMarker(_session.SongInMs, durationMs, widthPx, "In");
        AddSongRangeMarker(_session.SongOutMs, durationMs, widthPx, "Out");

        SongRangeReadoutText.Text = _session.SongInMs is null && _session.SongOutMs is null
            ? ""
            : $"In {(_session.SongInMs is double i ? FormatTime(i) : "0:00.0")} · Out {(_session.SongOutMs is double o ? FormatTime(o) : "end")}";
    }

    private void AddSongRangeMarker(double? markerMs, double durationMs, double widthPx, string label)
    {
        if (markerMs is not double ms) return;

        var (x, _) = ClipTimelineMath.ToPixelRange(ms, ms, durationMs, widthPx);
        var line = new System.Windows.Shapes.Line
        {
            X1 = x, X2 = x, Y1 = 0, Y2 = 20,
            Stroke = (System.Windows.Media.Brush)FindResource("StrongBorderBrush"),
            StrokeThickness = 2,
            ToolTip = $"Song {label}: {FormatTime(ms)}",
        };
        RulerCanvas.Children.Add(line);
    }

    // ----- Song in/out (project-wide export range, set from the playhead) -----

    private void SetSongInMenuItem_Click(object sender, RoutedEventArgs e) => SetSongIn();
    private void SetSongOutMenuItem_Click(object sender, RoutedEventArgs e) => SetSongOut();
    private void ClearSongRangeMenuItem_Click(object sender, RoutedEventArgs e) => ClearSongRange();

    /// <summary>Set In at the current playhead; nudged below the existing Out (if any) so In always
    /// stays before Out rather than producing an empty or inverted range.</summary>
    private void SetSongIn()
    {
        double candidateMs = _previewEngine.PositionMs;
        if (_session.SongOutMs is double outMs && candidateMs >= outMs)
            candidateMs = Math.Max(0, outMs - 1);

        _session.SongInMs = candidateMs;
        RedrawRulerCanvas();
        PushUndoSnapshot();
        StatusText.Text = $"Song in set to {FormatTime(candidateMs)}.";
    }

    private void SetSongOut()
    {
        double candidateMs = _previewEngine.PositionMs;
        if (_session.SongInMs is double inMs && candidateMs <= inMs)
            candidateMs = inMs + 1;

        _session.SongOutMs = candidateMs;
        RedrawRulerCanvas();
        PushUndoSnapshot();
        StatusText.Text = $"Song out set to {FormatTime(candidateMs)}.";
    }

    private void ClearSongRange()
    {
        if (!_session.ClearSongRange()) return;

        RedrawRulerCanvas();
        PushUndoSnapshot();
        StatusText.Text = "Song in/out cleared.";
    }

    /// <summary>Short "m:ss.t" format (e.g. "0:07.2"), one decisecond of
    /// precision, used now that current/total share one readout.</summary>
    private static string FormatTime(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        int totalMinutes = (int)ts.TotalMinutes;
        return $"{totalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
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

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportMenuItem.IsEnabled)                   // defensive; the menu item is disabled anyway
        {
            StatusText.Text = "Finish the export first.";
            return;
        }

        if (_layers.Layers.Count == 0)
        {
            StatusText.Text = "Add at least one layer before exporting.";
            return;
        }

        var dialog = new SaveFileDialog { Filter = "MP4 video|*.mp4", DefaultExt = ".mp4" };
        if (dialog.ShowDialog() != true) return;

        // Bug audit #8, subtlety 2: checked AFTER the Save dialog returns, not before it.
        // PlayButton is disabled only while PlayButton_Click is between SetLayersAsync and PlayAsync.
        if (!PlayButton.IsEnabled)
        {
            StatusText.Text = "Preview is starting; try Export again in a moment.";
            return;
        }

        ExportMenuItem.IsEnabled = false;                           // subtlety 1: BEFORE the await
        await AwaitPreviewCommand(_previewEngine.StopAsync());      // bug audit #8: one audio graph at a time
        StatusText.Text = "Exporting...";                           // after the stop: AwaitPreviewCommand may write "Preview error: ..."

        var (snapshotLayers, snapshotMasterVolumeDb, snapshotSongInMs, snapshotSongOutMs) = _session.SnapshotForExport();

        // Built on the UI thread (candidate 3 / Improvement_Proposal_2026-09-24): Progress<T>
        // captures the current SynchronizationContext at construction, so every Report() call from
        // the background export thread below marshals back here with no explicit Dispatcher.Invoke.
        ExportProgressBar.Value = 0;
        ExportProgressBar.Visibility = Visibility.Visible;
        var progress = new Progress<double>(fraction =>
        {
            ExportProgressBar.Value = fraction;
            StatusText.Text = $"Exporting... {fraction:P0}";
        });

        Task.Run(() =>
        {
            try
            {
                using var exportEngine = new ExportEngine(hostedService: _hostedService);
                exportEngine.Export(snapshotLayers, dialog.FileName, masterVolumeDb: snapshotMasterVolumeDb, progress: progress,
                    rangeStartMs: snapshotSongInMs, rangeOutMs: snapshotSongOutMs);
                Dispatcher.Invoke(() => StatusText.Text = $"Export complete: {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Export failed: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    ExportMenuItem.IsEnabled = true;
                    ExportProgressBar.Visibility = Visibility.Collapsed;
                });
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
