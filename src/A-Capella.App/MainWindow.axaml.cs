using System.Collections.ObjectModel;
using System.Diagnostics;
using A_Capella.App.Layout;
using A_Capella.App.ViewModels;
using A_Capella.Engine.Devices;
using A_Capella.Engine.Export;
using A_Capella.Engine.Host;
using A_Capella.Engine.Metronome;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Preview;
using A_Capella.Engine.Project;
using A_Capella.Engine.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace A_Capella.App;

public partial class MainWindow : Window
{
    /// <summary>Command on macOS, Ctrl elsewhere.</summary>
    private static readonly KeyModifiers ShortcutModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    internal DeviceCatalog DeviceCatalogInUse { get; set; } = new();
    internal SettingsService SettingsInUse { get; set; } = new();
    private readonly ProjectSession _session;
    private LayerCollection _layers => _session.Layers;
    // L1: a directory next to the executable works regardless of how/where the app is launched.
    internal ProjectSession Session => _session;
    internal PreviewPlaybackEngine PreviewEngine => _previewEngine;
    internal Func<A_Capella.Engine.Capture.FfmpegCaptureSession>? CaptureSessionFactory { get; set; }
    internal string MediaDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "media");

    // v7 Q0 task 1 (audit A1): exactly one HostedPluginService for the whole app session, shared by
    // MixEngine, PreviewPlaybackEngine, ExportEngine, and (via LayerRowViewModel.SharedHostedService)
    // the Mixing screen's launcher buttons -- fixes the "three disconnected plugin-instance caches"
    // defect where editing a plugin was inaudible and export matched neither the editor nor the
    // preview. All lifecycle calls marshal through UiHostedPluginDispatcher onto the UI thread
    // (A3/B8).
    private readonly HostedPluginService _hostedService = new(new HostedPluginAvailability(), new UiHostedPluginDispatcher(Dispatcher.UIThread));
    private readonly MixEngine _mixEngine;
    private readonly A_Capella.Engine.Sync.MelodyneNoteSource _noteSource;
    private readonly ObservableCollection<LayerRowViewModel> _tracks = new();
    private readonly PreviewPlaybackEngine _previewEngine;
    private readonly DispatcherTimer _previewDebounceTimer;

    // v7 Q1 task 3: ~30Hz meter refresh.
    private readonly DispatcherTimer _meterPollTimer;

    // 1Hz CPU/memory readout in the toolbar. Separate from the 33ms meter timer since
    // TotalProcessorTime deltas need a slower, coarser sampling interval to read as a stable percent.
    private readonly DispatcherTimer _perfPollTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

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

    // Set once the unsaved-changes prompt has been answered, so the close that follows isn't asked again.
    private bool _closeConfirmed;

    // The modifier keys of the latest key/pointer event, for click handlers (which carry none).
    private KeyModifiers _lastModifiers;

    public MainWindow()
    {
        // Must happen before any other bridge call, on this (the UI) thread -- all hosted
        // plugin lifecycle/editor calls are required to run on the thread that initialized JUCE's
        // MessageManager (see HostedPluginInstance.Initialize's doc comment). _hostedService's
        // UiHostedPluginDispatcher was already built against this same thread's Dispatcher above.
        try { HostedPluginInstance.Initialize(); }
        catch (DllNotFoundException) { /* native plugin host not built: the built-in effects still work */ }
        _mixEngine = new MixEngine(_hostedService);
        _noteSource = new A_Capella.Engine.Sync.MelodyneNoteSource(_hostedService);
        _session = new ProjectSession(_mixEngine);
        // 16:9 to match ExportEngine's 1280x720 default (half-scale, so cell math stays exact
        // integers): a 4:3 preview canvas letterboxed a 16:9 webcam in the preview even though it
        // fills its cell in the exported MP4, so what you saw while editing didn't match what you
        // got.
        _previewEngine = new PreviewPlaybackEngine(canvasWidth: 640, canvasHeight: 360, fps: 30, hostedService: _hostedService);
        LayerRowViewModel.SharedHostedService = _hostedService;

        // Scan for installed FabFilter plugins off the critical Play path (audit B2) -- the first
        // real chain build should never stall on loading six plugin binaries.
        Task.Run(() => { try { _hostedService.EnsureScanned(); } catch { /* no native host: nothing to scan */ } });

        InitializeComponent();
        ConfigurePlatformChrome();
        TrackList.ItemsSource = _tracks;
        EmptyCellOverlay.ItemsSource = _tracks;
        RestoreTracksFromLayers();   // Fixed 4-lane model: rows exist from app start, not grown on demand
        MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
        foreach (var signature in TimeSignature.Presets) TimeSignatureCombo.Items.Add(signature);
        ShowTimeSignature();
        SetUpMasterLimiterCombo();
        SelectLane(_tracks[0]);

        MasterVolumeSlider.ValueChanged += MasterVolumeSlider_ValueChanged;
        MasterVolumeSlider.AddHandler(PointerReleasedEvent, MasterVolumeSlider_PointerReleased, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        TimelineSlider.ValueChanged += TimelineSlider_ValueChanged;
        TimelineSlider.AddHandler(PointerPressedEvent, TimelineSlider_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimelineSlider.AddHandler(PointerReleasedEvent, TimelineSlider_PointerReleased, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        RulerCanvas.SizeChanged += (_, _) => RedrawRulerCanvas();
        AddHandler(PointerReleasedEvent, CommitSlider_PointerReleased, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, CommitSlider_KeyUp, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, e) => _lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) => _lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, MainWindow_PreviewKeyDown, RoutingStrategies.Tunnel);

        _session.SaveStateChanged += UpdateWindowTitle;
        UpdateWindowTitle();

        _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewDebounceTimer.Tick += (s, e) => { _previewDebounceTimer.Stop(); RefreshPreviewLive(); };

        // v7 Q1 task 3: ~30Hz meter poll -- reads GetLayerLevels/GetMasterLevels straight off the
        // live PreviewPlaybackEngine (volatile fields on its meter taps, no command-queue round
        // trip) and drives the ProgressBar and per-lane meters.
        _meterPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterPollTimer.Tick += (s, e) => UpdateMeters();
        _meterPollTimer.Start();

        _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
        _lastCpuSampleAt = DateTime.UtcNow;
        _perfPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _perfPollTimer.Tick += (s, e) => UpdatePerfCounters();
        _perfPollTimer.Start();

        // Latest-frame-wins mailbox (audit A2/B14): FrameReady fires on the frame-loop thread, and
        // that thread must never block on the UI thread (a blocking Invoke here used to
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
            if (!alreadyQueued) Dispatcher.UIThread.InvokeAsync(DrainFrameMailbox);
        };
        // Queued, never blocking: this fires from the frame-loop thread as it exits, and closing the
        // window waits on that thread from the UI thread -- a blocking Invoke here would deadlock.
        _previewEngine.PlaybackStopped += () => Dispatcher.UIThread.InvokeAsync(() => SetPlayStopContent(false));

        // Background pitch rendering: these fire from a worker thread/thread-pool continuation, so
        // always InvokeAsync, never a blocking Invoke -- same deadlock hazard as PlaybackStopped
        // above if this ever landed while the UI thread is waiting inside Dispose/Closing.
        _previewEngine.PitchRenderStarted += _ => Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingPitchRenderCount++;
            StatusText.Text = "Processing pitch...";
        });
        _previewEngine.PitchRenderCompleted += (_, _) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingPitchRenderCount = Math.Max(0, _pendingPitchRenderCount - 1);
            if (_pendingPitchRenderCount == 0) StatusText.Text = "Pitch correction updated.";
        });
        _previewEngine.PitchRenderFailed += (_, message) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingPitchRenderCount = Math.Max(0, _pendingPitchRenderCount - 1);
            StatusText.Text = $"Pitch correction failed: {message}";
        });

        // Freeze: BuildLayerChain blocks the command/export thread for the whole render, so unlike
        // the pitch events above these just bracket a status message -- there's no "stillCurrent"
        // refresh to do, the chain just built already reflects it.
        _previewEngine.FreezeRenderStarted += layerId => Dispatcher.UIThread.InvokeAsync(() =>
        {
            var row = _tracks.FirstOrDefault(t => t.Layer?.LayerId == layerId);
            StatusText.Text = row is not null ? $"Freezing {row.DisplayName}..." : "Freezing layer...";
        });
        _previewEngine.FreezeRenderCompleted += _ => Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusText.Text = "Layer frozen.";
        });

        Closing += MainWindow_Closing;
    }

    /// <summary>The title band extends into the system caption area: leave room for macOS's traffic
    /// lights at the left, or the Windows/Linux caption buttons at the right, and show the platform's
    /// own shortcut keys in the menus.</summary>
    private void ConfigurePlatformChrome()
    {
        if (OperatingSystem.IsMacOS()) TitleGrid.ColumnDefinitions[0].Width = new GridLength(72);
        else TitleGrid.ColumnDefinitions[4].Width = new GridLength(140);

        SaveMenuItem.InputGesture = new KeyGesture(Key.S, ShortcutModifier);
        SaveAsMenuItem.InputGesture = new KeyGesture(Key.S, ShortcutModifier | KeyModifiers.Shift);
        UndoMenuItem.InputGesture = new KeyGesture(Key.Z, ShortcutModifier);
        RedoMenuItem.InputGesture = OperatingSystem.IsMacOS()
            ? new KeyGesture(Key.Z, ShortcutModifier | KeyModifiers.Shift)
            : new KeyGesture(Key.Y, ShortcutModifier);

        // The empty part of the band drags the window.
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Menu>() is null && v.FindAncestorOfType<Button>() is null
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            TearDown();
            return;
        }

        // Refuse close mid-export: the teardown below would dispose the hosted service under the
        // export and exit, truncating the MP4 (bug audit #15).
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";
            e.Cancel = true;
            return;
        }

        if (!_session.IsDirty)
        {
            TearDown();
            return;
        }

        // Save-affordances spec: prompt FIRST, before anything is stopped or disposed -- a cancelled
        // close (Cancel, or Yes followed by a cancelled/failed save) must leave the app fully running.
        // The prompt is asynchronous, so this close is cancelled and repeated once it's been answered.
        e.Cancel = true;
        _ = ConfirmAndCloseAsync();
    }

    private async Task ConfirmAndCloseAsync()
    {
        if (!await ConfirmDiscardUnsavedChangesAsync()) return;

        _closeConfirmed = true;
        Close();
    }

    private void TearDown()
    {
        _meterPollTimer.Stop();
        _perfPollTimer.Stop();
        _previewDebounceTimer.Stop();
        _previewEngine.Dispose();
        _mixEngine.Dispose();
        _hostedService.Dispose();
        _pendingFrame?.Dispose();
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
            var previousRows = _tracks.ToList();   // bug audit #9: RestoreTracksFromLayers clears _tracks

            if (!restore()) return false;

            MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
            ShowTimeSignature();
            MasterVolumeSlider.Value = _session.MasterVolumeDb;
            RestoreTracksFromLayers();
            ShowMasterLimiter();
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

    private void UndoMenuItem_Click(object? sender, RoutedEventArgs e) => PerformUndo();
    private void RedoMenuItem_Click(object? sender, RoutedEventArgs e) => PerformRedo();

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

    private static bool IsCommitSlider(object? source) =>
        source is Visual v && (v as Slider ?? v.FindAncestorOfType<Slider>()) is { } slider && slider.Classes.Contains("commit");

    /// <summary>Commit-style handler shared by every FX/mix-parameter slider (Classes="commit"):
    /// pushes an undo snapshot once the drag ends, not per-tick.</summary>
    private void CommitSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsCommitSlider(e.Source)) PushUndoSnapshot();
    }

    /// <summary>Bug audit #13: Slider.Value binds TwoWay with the framework-default
    /// UpdateSourceTrigger (PropertyChanged), unlike TextBox.Text -- so a keyboard-driven edit on a
    /// focused commit slider already applies live, with nothing to fire a pointer release. Commits on
    /// key-release (not key-down -- see the doc's ordering subtleties) for the same reason the mouse
    /// handler commits on release, not per-tick: one undo step per discrete edit. Filtered to
    /// Up/Down/PageUp/PageDown/End only -- Left/Right/Home are deliberately excluded: MainWindow's
    /// own transport shortcut (MainWindow_PreviewKeyDown) already consumes those three for
    /// seek/restart before they ever reach a focused Slider's Value (root cause fact 3), so including
    /// them here would fire a commit on every seek performed while any FX slider happens to be
    /// focused -- not just a harmless no-op step, but one that clears the redo stack
    /// (ProjectUndoStack.Push) on every such seek, silently destroying whatever the user could
    /// otherwise have redone.</summary>
    private static readonly Key[] SliderCommitKeys =
        { Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.End };

    private void CommitSlider_KeyUp(object? sender, KeyEventArgs e)
    {
        if (IsCommitSlider(e.Source) && Array.IndexOf(SliderCommitKeys, e.Key) >= 0) PushUndoSnapshot();
    }

    private void CommitCheckBox_Click(object? sender, RoutedEventArgs e) => PushUndoSnapshot();

    // ----- Per-layer timing nudge: -/+ on the strip (one undo step per click) -----

    private double NudgeStepMs() => (_lastModifiers & KeyModifiers.Shift) != 0 ? 1 : 10;

    private void NudgeEarlierButton_Click(object? sender, RoutedEventArgs e) => NudgeRow(sender, -NudgeStepMs());

    private void NudgeLaterButton_Click(object? sender, RoutedEventArgs e) => NudgeRow(sender, NudgeStepMs());

    private void NudgeRow(object? sender, double deltaMs)
    {
        if (((StyledElement)sender!).DataContext is not LayerRowViewModel row) return;
        row.Nudge(deltaMs);
        StatusText.Text = $"{row.DisplayName} timing: {row.ManualOffsetDisplay}.";
        PushUndoSnapshot();
    }

    private void NudgeLabel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (((StyledElement)sender!).DataContext is not LayerRowViewModel row || row.ManualOffsetMs == 0) return;
        row.ResetNudge();
        StatusText.Text = $"{row.DisplayName} timing reset.";
        PushUndoSnapshot();
    }

    // ----- Auto-align: line an uploaded track up with layer 1 (the guide everyone sings to) -----

    private readonly HashSet<int> _aligning = new();
    // One alignment at a time: they share layer 1's decode and the single Melodyne analysis.
    private readonly SemaphoreSlim _alignGate = new(1, 1);

    private async void AutoAlignButton_Click(object? sender, RoutedEventArgs e)
    {
        if (((StyledElement)sender!).DataContext is LayerRowViewModel row)
            await AutoAlignAsync(row, explicitRequest: true);
    }

    /// <summary>Measures how far the row's layer runs from layer 1 and, if the match is clear, moves it
    /// into place (one undo step). Analysis runs off the UI thread; a result for a layer that changed in
    /// the meantime is dropped. Never throws: problems end up in the status line.</summary>
    private async Task AutoAlignAsync(LayerRowViewModel row, bool explicitRequest = false)
    {
        if (!IsLiveRow(row)) return;
        var target = row.Layer!;
        var reference = _layers.Layers.FirstOrDefault(l => l.CellIndex == 0);
        if (reference is null || ReferenceEquals(reference, target))
        {
            if (explicitRequest)
                StatusText.Text = "Auto-align lines a layer up with layer 1, so there is nothing to align here.";
            return;
        }
        if (!_aligning.Add(target.LayerId)) return;

        string targetKey = A_Capella.Engine.Sync.LayerAligner.ContentKey(target);
        string referenceKey = A_Capella.Engine.Sync.LayerAligner.ContentKey(reference);
        StatusText.Text = $"Aligning {row.DisplayName} to layer 1...";
        double calibratedBefore = target.CalibratedOffsetMs, manualBefore = target.ManualOffsetMs;
        try
        {
            await _alignGate.WaitAsync();
            var result = await Task.Run(() =>
            {
                var notes = _noteSource.IsAvailable ? _noteSource : null;
                return A_Capella.Engine.Sync.LayerAligner.Measure(reference, target, notes);
            });

            bool stillCurrent = IsLiveRow(row) && ReferenceEquals(row.Layer, target)
                && _layers.Layers.Contains(reference)
                && A_Capella.Engine.Sync.LayerAligner.ContentKey(target) == targetKey
                && A_Capella.Engine.Sync.LayerAligner.ContentKey(reference) == referenceKey
                && target.CalibratedOffsetMs == calibratedBefore && target.ManualOffsetMs == manualBefore;
            if (!stillCurrent)
            {
                StatusText.Text = $"Skipped aligning {row.DisplayName}: the layer changed meanwhile. Use Auto-align to try again.";
                return;
            }

            string how = result.Method == A_Capella.Engine.Sync.AlignMethod.Notes ? "Melodyne" : "loudness";
            if (!result.Trusted)
            {
                StatusText.Text = $"Couldn't find a confident match for {row.DisplayName} ({how}) - line it up with the timing nudge.";
                return;
            }

            A_Capella.Engine.Sync.LayerAligner.Apply(reference, target, result);
            row.NotifyTimingChanged();
            RefreshPreviewLive();
            StatusText.Text = $"Aligned {row.DisplayName} to layer 1 ({result.OffsetMs:+0;-0} ms, {how}).";
            PushUndoSnapshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't align {row.DisplayName}: {ex.Message}";
        }
        finally
        {
            _alignGate.Release();
            _aligning.Remove(target.LayerId);
        }
    }

    /// <summary>After an upload: layers 2 and up are aligned to layer 1 automatically.</summary>
    private void AutoAlignUploaded(IEnumerable<LayerRowViewModel> rows)
    {
        foreach (var row in rows)
            if (row.Layer is { CellIndex: > 0 }) _ = AutoAlignAsync(row);
    }

    // ----- Trim text boxes: the In/Out fields commit on Enter / focus loss -----

    private void TrimTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || ChannelPanel.DataContext is not LayerRowViewModel row || row.Layer is null) return;

        bool isIn = (string?)box.Tag == "In";
        string before = isIn ? row.TrimStartMs.ToString() : row.TrimEndText;
        string text = box.Text?.Trim() ?? "";
        if (isIn)
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms))
                row.TrimStartMs = ms;
            box.Text = row.TrimStartMs.ToString();
        }
        else
        {
            row.TrimEndText = text;
            box.Text = row.TrimEndText;
        }
        if (box.Text != before) PushUndoSnapshot();
    }

    private void TrimTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Focus();
        e.Handled = true;
    }

    // ----- Lane clip-bar drag-to-trim -----

    private ClipTrimDrag? _activeTrimDrag;
    private LayerRowViewModel? _activeTrimDragRow;

    /// <summary>Snapshots the handle's starting value and the track's current scale. No-ops (leaves
    /// _activeTrimDrag null) when the source duration or timeline duration isn't known yet, so a
    /// drag started before MediaProbe returns has no effect rather than clamping against a bogus 0.</summary>
    private void TrimHandle_DragStarted(object? sender, TrimDragEventArgs e)
    {
        var row = e.Row;
        _activeTrimDrag = null;
        _activeTrimDragRow = null;
        if (row.TimelineDurationMs <= 0 || row.SourceDurationMs <= 0)
            return;

        _activeTrimDragRow = row;
        _activeTrimDrag = e.IsInHandle
            ? new ClipTrimDrag(ClipTrimDrag.Handle.In, row.TrimStartMs, row.EffectiveTrimEndMs, row.SourceDurationMs, row.TimelineDurationMs, e.TrackWidthPx)
            : new ClipTrimDrag(ClipTrimDrag.Handle.Out, row.EffectiveTrimEndMs, row.TrimStartMs, row.SourceDurationMs, row.TimelineDurationMs, e.TrackWidthPx);
    }

    /// <summary>Applies the drag's clamped value live, for immediate visual feedback on the clip
    /// bar -- the actual commit (PushUndoSnapshot) happens once, on release, in DragCompleted.</summary>
    private void TrimHandle_DragDelta(object? sender, TrimDragEventArgs e)
    {
        if (_activeTrimDrag is null || _activeTrimDragRow is null) return;

        double newValueMs = _activeTrimDrag.ValueAt(e.TotalDeltaPx);
        if (e.IsInHandle) _activeTrimDragRow.TrimStartMs = newValueMs;
        else _activeTrimDragRow.TrimEndMs = newValueMs;
    }

    /// <summary>One undo step per completed drag -- none at all if the pointer never actually moved
    /// the value (same no-op-skip rule as CommitNameIfChanged).</summary>
    private void TrimHandle_DragCompleted(object? sender, TrimDragEventArgs e)
    {
        if (_activeTrimDrag is not null && _activeTrimDragRow is not null)
        {
            double currentValueMs = e.IsInHandle ? _activeTrimDragRow.TrimStartMs : _activeTrimDragRow.EffectiveTrimEndMs;
            if (_activeTrimDrag.HasChangedFrom(currentValueMs))
                PushUndoSnapshot();
        }

        _activeTrimDrag = null;
        _activeTrimDragRow = null;
    }

    // ----- Keyboard shortcuts (v5 P1 task 6) -----

    private object? FocusedElement => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    private async void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Project shortcuts: Command/Ctrl+S saves even from a text box; undo/redo leave a text box's
        // own undo alone.
        if ((e.KeyModifiers & ShortcutModifier) != 0)
        {
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (e.Key == Key.S)
            {
                e.Handled = true;
                await SaveProjectAsync(forceDialog: shift);
                return;
            }
            if (FocusedElement is TextBox) return;
            if (e.Key == Key.Z)
            {
                if (shift) PerformRedo(); else PerformUndo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y && !OperatingSystem.IsMacOS())
            {
                PerformRedo();
                e.Handled = true;
            }
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;

        // Don't hijack typing in a trim/rename textbox.
        if (FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.Space:
                // Bug audit #14: Space is Button's own activation key (Button/CheckBox/
                // ToggleButton/RadioButton all invoke Click on Space) -- don't steal it from a
                // focused one of those, or it silently fails to toggle/click and fires Play/Stop
                // instead (see doc's root cause 2-3). Checked here, not added to the TextBox
                // exclusion above, because Left/Right/Home have no such conflict for these
                // controls (root cause fact 3's table lists none) -- see Ordering subtleties for
                // why broadening the exclusion is a regression, not a simplification.
                if (FocusedElement is Button) return;
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

    private void SeekBackButton_Click(object? sender, RoutedEventArgs e) => SeekRelative(-5000);
    private void SeekForwardButton_Click(object? sender, RoutedEventArgs e) => SeekRelative(5000);

    private void SeekRelative(double deltaMs) => RunPreviewCommand(_previewEngine.SeekByAsync(deltaMs));

    /// <summary>MasterVolumeSlider is the single master-volume control (there is no separate
    /// mixer-strip fader).</summary>
    private void MasterVolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _session.MasterVolumeDb = (float)e.NewValue;
        ApplyMasterVolume();
    }

    /// <summary>Applies the stored master volume unless the Monitor toggle has muted the output --
    /// muting never touches the session's MasterVolumeDb itself so un-muting restores exactly what the sliders
    /// still show.</summary>
    private void ApplyMasterVolume() => _previewEngine.MasterVolumeDb = _monitorMuted ? -96f : _session.MasterVolumeDb;

    private void MonitorToggle_Changed(object? sender, RoutedEventArgs e)
    {
        _monitorMuted = MonitorToggle.IsChecked != true;
        ApplyMasterVolume();
    }

    private void MasterVolumeSlider_PointerReleased(object? sender, PointerReleasedEventArgs e) => PushUndoSnapshot();

    private bool _syncingTimeSignature;

    /// <summary>Reflects the session's meter in the toolbar combo without it echoing back as an edit.</summary>
    private void ShowTimeSignature()
    {
        _syncingTimeSignature = true;
        try
        {
            var current = _session.MetronomeTimeSignature;
            if (!TimeSignatureCombo.Items.Contains(current)) TimeSignatureCombo.Items.Add(current);   // a meter from a hand-edited file
            TimeSignatureCombo.SelectedItem = current;
        }
        finally { _syncingTimeSignature = false; }
    }

    private void TimeSignatureCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTimeSignature || _applyingHistory || TimeSignatureCombo.SelectedItem is not TimeSignature chosen) return;
        if (chosen == _session.MetronomeTimeSignature) return;
        _session.MetronomeTimeSignature = chosen;
        StatusText.Text = $"Metronome: {chosen}.";
        PushUndoSnapshot();
    }

    private void MetronomeBpmTextBox_LostFocus(object? sender, RoutedEventArgs e)
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

        CompositeCanvas.SetFrame(frame);   // copies the pixels, so the engine's bitmap can go straight away
        frame.Dispose();
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

    /// <summary>v7 Q1 task 3 (v8: per-strip meters). Maps -60..0 dBFS RMS onto the 0..1 range
    /// (below -60dB reads as silence) -- a fixed floor is simpler than a full logarithmic meter
    /// ballistics model and good enough for "the meters respond" per the plan's [human] criterion.
    /// Drives every lane's MeterLevel (bound property) plus the master meter bar.</summary>
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

    /// <summary>Pushes the project's video settings (layout) into the preview engine; the engine
    /// reads them when it (re)builds its per-cell frame sources, so call before RefreshAsync/Play.</summary>
    private void SyncPreviewVideoSettings()
    {
        _previewEngine.ApplyVideoSettings(_session.Video);
        var (width, height) = _session.Video.PreviewSize;
        CompositeCanvas.Width = width;
        CompositeCanvas.Height = height;
    }

    /// <summary>After layers are added or imported: switch to a layout with enough cells if the
    /// current one is too small, and say so. The caller still pushes the undo step.</summary>
    private void EnsureLayoutFitsLayers()
    {
        if (_session.EnsureLayoutFits())
            StatusText.Text = $"Switched to a {_session.Video.Layout.Cells.Count}-cell layout to fit your layers.";
    }

    private async void VideoSettingsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var window = new VideoSettingsWindow(_session.Video);
        if (await Dialogs.ShowModalAsync(window, this) != true || window.Result == _session.Video) return;

        _session.Video = window.Result;
        RefreshPreviewLive();
        StatusText.Text = "Video settings updated.";
        PushUndoSnapshot();
    }

    private async void ChooseLayoutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        int required = _layers.Layers.Count == 0 ? 1 : _layers.Layers.Max(l => l.CellIndex) + 1;
        var picker = new LayoutPickerWindow(_session.Video.LayoutId, required);
        if (await Dialogs.ShowModalAsync(picker, this) != true || picker.SelectedLayoutId is null) return;
        if (picker.SelectedLayoutId == _session.Video.LayoutId) return;

        if (!_session.TrySetLayout(picker.SelectedLayoutId, out var error))
        {
            StatusText.Text = error ?? "That layout can't be used.";
            return;
        }
        RefreshPreviewLive();
        StatusText.Text = $"Layout: {_session.Video.Layout.Cells.Count} cells.";
        PushUndoSnapshot();
    }

    private void RecordChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (((StyledElement)sender!).DataContext is not LayerRowViewModel row) return;
        OpenRecordSetupForRow(row);
    }

    /// <summary>v7 Q3 (media-folder hygiene): recorded take files accumulate silently in
    /// MediaDir with no visibility into how much disk they're using. Read-only report -- no
    /// deletion here, since deleting recorded media is irreversible (needs the
    /// user's explicit say, not an automatic cleanup on close).</summary>
    private async void RecordingsFolderSizeMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(MediaDir))
        {
            await Dialogs.ShowMessageAsync(this, "No recordings folder yet.", "Recordings Folder");
            return;
        }

        var files = Directory.GetFiles(MediaDir, "*", SearchOption.AllDirectories);
        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        double mb = totalBytes / (1024.0 * 1024.0);
        await Dialogs.ShowMessageAsync(this, $"{MediaDir}\n\n{files.Length} file(s), {mb:F1} MB.", "Recordings Folder");
    }

    /// <summary>The ONLY place RecordSetupWindow is constructed (retake spec D4), so bug audit #8's
    /// export gate covers every way into Recording setup. Returns null when the gate refuses
    /// (StatusText already set); otherwise the closed dialog and whether a take was accepted.
    /// The gate is checked immediately before the dialog opens, and the dialog is modal over this
    /// window, so nothing can start an export in between (bug audit #8).</summary>
    private async Task<(RecordSetupWindow Dialog, bool Recorded)?> ShowRecordSetupDialogAsync(LayerRowViewModel? retakeRow)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";   // bug audit #8
            return null;
        }

        var dialog = new RecordSetupWindow(DeviceCatalogInUse, SettingsInUse, _layers, _mixEngine, MediaDir, _session.MetronomeBpm,
                                           retakeLayerId: retakeRow?.Layer?.LayerId, initialTimeSignature: _session.MetronomeTimeSignature, captureSessionFactory: CaptureSessionFactory);
        if (retakeRow is not null) dialog.Title = $"Re-record {retakeRow.DisplayName}";
        bool recorded = await Dialogs.ShowModalAsync(dialog, this) == true;
        _session.MetronomeBpm = dialog.Bpm;
        _session.MetronomeTimeSignature = dialog.TimeSignature;
        ShowTimeSignature();
        return (dialog, recorded);
    }

    /// <summary>Opens the capture-setup dialog for a NEW layer. If a take is recorded, attaches it to
    /// the row that resolveRow returns. resolveRow runs ONLY on success, AFTER the dialog closes, so a
    /// cancelled or calibrate-only session never creates or claims a row (bug audit #7).</summary>
    private async Task OpenRecordSetupAsync(Func<LayerRowViewModel?> resolveRow)
    {
        var shown = await ShowRecordSetupDialogAsync(retakeRow: null);
        if (shown is null) return;
        var (dialog, recorded) = shown.Value;

        if (recorded && dialog.CreatedLayer is not null)
        {
            var row = resolveRow();
            if (row is null) return;   // unreachable in practice: the dialog refuses to record at the cap (RecordSetupWindow)

            // CellIndex binds to the row's own position (audit B8), not LayerCollection's
            // insertion order -- e.g. row 2 recording before row 1 must still land in grid cell 2.
            dialog.CreatedLayer.CellIndex = row.SlotNumber - 1;
            row.Layer = dialog.CreatedLayer;
            if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
            EnsureLayoutFitsLayers();
            UpdateLaneHeaderCount();
            RefreshPreviewLive();
            StatusText.Text = $"Recorded {row.DisplayName}.";
            PushUndoSnapshot();
        }
    }

    /// <summary>A strip's own Rec button: records into that strip.</summary>
    private void OpenRecordSetupForRow(LayerRowViewModel row) => _ = OpenRecordSetupAsync(() => row);

    /// <summary>Toolbar ⏺ and Tools > Recording setup...: records into the lowest free layer slot,
    /// reusing an existing empty strip before appending one (bug audit #7).</summary>
    private async void RecordingSetupMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        int? targetCell = LowestFreeCell();   // read BEFORE the dialog opens -- see bug audit #7 step 3
        if (targetCell is null)
        {
            StatusText.Text = $"Layer cap reached ({LayerCollection.MaxLayers}).";
            return;
        }
        await OpenRecordSetupAsync(() => RowForCell(targetCell.Value));
    }

    /// <summary>Tools > Calibrate latency...: opens the same dialog for its Calibrate button. It never
    /// creates or claims a row and is not blocked by the layer cap, because calibration is not a
    /// layer. If the user records from here anyway, the take goes to the lowest free slot, as with ⏺.</summary>
    private async void CalibrateLatencyMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        int? targetCell = LowestFreeCell();   // same ordering rule; null (a full project) is fine here
        await OpenRecordSetupAsync(() => targetCell is int cell ? RowForCell(cell) : null);
    }

    // ----- Retake a layer in place (retake spec) -----

    /// <summary>Retake spec D8: the row is still on screen and still wraps a layer that is in the live
    /// collection (Undo/Open/New rebuild rows AND LayerModels, so a captured row can go stale).</summary>
    private bool IsLiveRow(LayerRowViewModel row) =>
        _tracks.Contains(row) && row.Layer is not null && _layers.Layers.Contains(row.Layer);

    /// <summary>A populated strip's ↻ button: a two-item menu built in code (spec D6 -- a XAML
    /// ContextMenu inside a DataTemplate has its own DataContext/visual-tree pitfalls).</summary>
    private void RetakeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not LayerRowViewModel row || !IsLiveRow(row)) return;

        var reRecord = new MenuItem { Header = "Re-record..." };
        reRecord.Click += (_, _) => _ = RetakeRecordAsync(row);
        var replace = new MenuItem { Header = "Replace with file..." };
        replace.Click += (_, _) => _ = RetakeReplaceWithFileAsync(row);

        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        menu.Items.Add(reRecord);
        menu.Items.Add(replace);
        menu.Open(button);
    }

    /// <summary>Re-record this layer in place: guide = the OTHER layers, not blocked by the cap, same
    /// LayerId/cell/name/FX; one undo step. Goes through the single #8-gated helper (spec D4).
    /// Never calls LowestFreeCell/RowForCell/Add (spec (d)).</summary>
    private async Task RetakeRecordAsync(LayerRowViewModel row)
    {
        if (!IsLiveRow(row)) return;
        var target = row.Layer!;                                          // fixed BEFORE the dialog (spec (d))

        var shown = await ShowRecordSetupDialogAsync(row);
        if (shown is null) return;                                        // #8 gate refused; status already set
        var (dialog, recorded) = shown.Value;
        if (!recorded || dialog.RetakeResult is not { } take) return;     // cancelled / closed: nothing changes

        // Defensive (spec D8): unreachable in practice -- the dialog is modal and disables this window.
        if (!IsLiveRow(row) || !ReferenceEquals(row.Layer, target))
        {
            StatusText.Text = $"The layer changed while recording; the take was kept as {Path.GetFileName(take.SourcePath)} but not attached.";
            return;
        }

        // Invariant (spec (e)): no await / message box / dialog from here to PushUndoSnapshot().
        target.ReplaceSource(LayerKind.RecordedAV, take.SourcePath, take.CalibratedOffsetMs);
        row.NotifySourceReplaced();                                       // NOT row.Layer = ... (spec D5)
        RefreshPreviewLive();
        StatusText.Text = $"Re-recorded {row.DisplayName}.";
        PushUndoSnapshot();                                               // ONE step: source + trims + offset (+ any BPM change)
    }

    /// <summary>Replace this layer's source with a file, in place: same kind rule as Upload, trims and
    /// offsets reset, LayerId/cell/name/FX kept; one undo step. No export gate, like Upload (spec D7).</summary>
    private async Task RetakeReplaceWithFileAsync(LayerRowViewModel row)
    {
        if (!IsLiveRow(row)) return;

        string? fileName = await Dialogs.PickFileAsync(this, $"Replace {row.DisplayName} with file", Dialogs.MediaFiles);
        if (fileName is null) return;
        if (!IsLiveRow(row)) return;                                      // defensive (spec D8)

        // Invariant (spec (e)): no await / message box / dialog from here to PushUndoSnapshot().
        row.Layer!.ReplaceSource(UploadedKindFor(fileName), fileName, calibratedOffsetMs: 0);
        row.NotifySourceReplaced();                                       // NOT row.Layer = ... (spec D5)
        RefreshPreviewLive();
        StatusText.Text = $"Replaced {row.DisplayName} with {Path.GetFileName(fileName)}.";
        PushUndoSnapshot();
        AutoAlignUploaded(new[] { row });
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

    private async void UploadChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (((StyledElement)sender!).DataContext is not LayerRowViewModel row) return;

        string? fileName = await Dialogs.PickFileAsync(this, "Upload", Dialogs.MediaFiles);
        if (fileName is null) return;

        AttachUploadedFile(row, fileName);
        EnsureLayoutFitsLayers();
        UpdateLaneHeaderCount();
        RefreshPreviewLive();
        StatusText.Text = $"Uploaded {row.DisplayName}: {Path.GetFileName(fileName)}";
        PushUndoSnapshot();
        AutoAlignUploaded(new[] { row });
    }

    /// <summary>Layers > Import files as layers...: one multi-select dialog; files (sorted by name,
    /// spec D3) fill free slots lowest-first up to the layer cap, each attached exactly like a
    /// per-strip Upload; the whole batch is ONE undo step (spec D2). Overflow is reported in
    /// StatusText, never a dialog.</summary>
    private async void ImportFilesMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_layers.Layers.Count >= LayerCollection.MaxLayers)
        {
            StatusText.Text = $"No empty layer slots ({LayerCollection.MaxLayers}-layer cap reached).";
            return;
        }

        var fileNames = await Dialogs.PickFilesAsync(this, "Import files as layers", Dialogs.MediaFiles, multiple: true);
        if (fileNames.Count == 0) return;

        // Occupancy is read AFTER the dialog closes, not before it opens.
        var (assignments, skipped) = LayerImportPlanner.Plan(_layers.Layers.Select(l => l.CellIndex), fileNames);

        // Invariant: from here to PushUndoSnapshot() there must be no await, message box, dialog or
        // other dispatcher pumping -- a half-imported project would otherwise be recorded as its own
        // undo step (breaks spec D2).
        foreach (var (cellIndex, filePath) in assignments)
            AttachUploadedFile(_tracks[cellIndex], filePath);

        EnsureLayoutFitsLayers();
        UpdateLaneHeaderCount();
        if (assignments.Count > 0)
        {
            RefreshPreviewLive();                                        // once per batch, not per file
            PushUndoSnapshot();                                          // ONE undo step + one MarkDirty for the batch
            AutoAlignUploaded(assignments.Select(x => _tracks[x.CellIndex]).ToList());
        }

        StatusText.Text = FormatImportStatus(assignments.Count, skipped);
    }

    private static string FormatImportStatus(int imported, IReadOnlyList<string> skipped)
    {
        static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
        string text = $"Imported {Files(imported)}";
        if (skipped.Count > 0)
            text += $"; {Files(skipped.Count)} skipped ({LayerCollection.MaxLayers}-layer cap reached): {string.Join(", ", skipped.Select(Path.GetFileName))}";
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
            row.EffectChosen += PushUndoSnapshot;
            _tracks.Add(row);
        }
        UpdateLaneHeaderCount();
    }

    // ----- Lane selection -----
    // Clicking a lane row switches which layer's FX chain the channel panel shows.

    private void LaneRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (((StyledElement)sender!).DataContext is not LayerRowViewModel row) return;
        SelectLane(row);
    }

    private void SelectLane(LayerRowViewModel row)
    {
        foreach (var track in _tracks) track.IsSelected = track == row;
        _selectedLayer = row;
        ChannelPanel.DataContext = row;
        FxHeaderText.Text = row.DisplayName;
    }

    // ----- Master limiter: one preset dropdown for the whole mix -----

    private bool _syncingMasterLimiter;

    private void SetUpMasterLimiterCombo()
    {
        MasterLimiterCombo.Choices = EffectSlotViewModel.AllChoices(ProjectSession.MasterLimiterPlugin);
        ShowMasterLimiter();
        _previewEngine.MasterLimiterPreset = _session.MasterLimiterPreset;
    }

    /// <summary>Reflects the session's limiter choice in the toolbar combo without echoing back as an edit.</summary>
    private void ShowMasterLimiter()
    {
        _syncingMasterLimiter = true;
        try
        {
            var choices = EffectSlotViewModel.AllChoices(ProjectSession.MasterLimiterPlugin);
            MasterLimiterCombo.SelectedChoice = choices.FirstOrDefault(c => Equals(c.Ref, _session.MasterLimiterPreset));
        }
        finally
        {
            _syncingMasterLimiter = false;
        }
        _previewEngine.MasterLimiterPreset = _session.MasterLimiterPreset;
    }

    private void MasterLimiterCombo_SelectionChanged(object? sender, EventArgs e)
    {
        if (_syncingMasterLimiter || MasterLimiterCombo.SelectedChoice is not { } choice) return;
        if (Equals(_session.MasterLimiterPreset, choice.Ref)) return;
        _session.MasterLimiterPreset = choice.Ref;
        _previewEngine.MasterLimiterPreset = choice.Ref;
        DebounceRefreshPreview();
        PushUndoSnapshot();
    }

    // ----- Packages: a saved set of the four effect presets, applied to a layer in one step -----

    internal FxPackageService Packages { get; set; } = new();

    private void PackagesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedLayer is not { Layer: not null } row) return;

        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        var packages = Packages.List();

        foreach (var package in packages.Where(p => p.IsBuiltIn))
            menu.Items.Add(PackageMenuItem(package, row));
        if (packages.Any(p => p.IsBuiltIn) && packages.Any(p => !p.IsBuiltIn)) menu.Items.Add(new Separator());
        foreach (var package in packages.Where(p => !p.IsBuiltIn))
            menu.Items.Add(PackageMenuItem(package, row));
        if (packages.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No packages yet", IsEnabled = false });

        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Save this layer's effects as a package..." };
        save.Click += (_, _) => _ = SaveCurrentAsPackageAsync(row);
        menu.Items.Add(save);

        var mine = packages.Where(p => !p.IsBuiltIn).ToList();
        if (mine.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete a package" };
            foreach (var package in mine)
            {
                var item = new MenuItem { Header = package.Name };
                item.Click += (_, _) => _ = DeletePackageAsync(package);
                delete.Items.Add(item);
            }
            menu.Items.Add(delete);
        }

        menu.Open(PackagesButton);
    }

    private MenuItem PackageMenuItem(FxPackage package, LayerRowViewModel row)
    {
        var item = new MenuItem { Header = package.Name };
        item.Click += (_, _) =>
        {
            row.ApplyPackage(package);
            StatusText.Text = $"Applied package \"{package.Name}\" to {row.DisplayName}.";
        };
        return item;
    }

    private async Task SaveCurrentAsPackageAsync(LayerRowViewModel row)
    {
        var dialog = new SavePackageWindow(Packages);
        if (await Dialogs.ShowModalAsync(dialog, this) != true) return;

        var package = row.ToPackage(dialog.PackageName);
        if (package is null) return;
        try
        {
            Packages.Save(package);
            StatusText.Text = $"Saved package \"{package.Name}\".";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Couldn't save the package: {ex.Message}";
        }
    }

    private async Task DeletePackageAsync(FxPackage package)
    {
        if (await Dialogs.ShowMessageAsync(this, $"Delete the package \"{package.Name}\"?", "Delete package", MessageButtons.YesNo) != MessageResult.Yes) return;
        try
        {
            if (Packages.Delete(package.Name)) StatusText.Text = $"Deleted package \"{package.Name}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Couldn't delete the package: {ex.Message}";
        }
    }

    // ----- Lane name rename-in-place: inline TextBox in the lane row, committed only
    // on a real change via CommitNameIfChanged, matching the CommitSlider/CommitCheckBox pattern. -----

    private void LaneNameTextBox_GotFocus(object? sender, RoutedEventArgs e) => ((TextBox)sender!).SelectAll();

    private void LaneNameTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not LayerRowViewModel row) return;
        if (row.CommitNameIfChanged(textBox.Text ?? ""))
            PushUndoSnapshot();
        textBox.Text = row.Name;   // reflect the trimmed/reverted value
    }

    private void LaneNameTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Focus();
            e.Handled = true;
        }
    }

    // ----- Delete layer: undoable, no cell compaction -- the row stays
    // in its slot, now empty, exactly like a row that was never recorded/uploaded into. -----

    private void ChannelMoreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var row = _selectedLayer;

        var deleteItem = new MenuItem { Header = "Delete layer", IsEnabled = row is not null && IsLiveRow(row) };
        deleteItem.Click += (_, _) => _ = ConfirmAndDeleteLayerAsync(row);

        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        menu.Items.Add(deleteItem);
        menu.Open(button);
    }

    private async void DeleteLayerMenuItem_Click(object? sender, RoutedEventArgs e) => await ConfirmAndDeleteLayerAsync(_selectedLayer);

    /// <summary>Asks before deleting (the slot is still undoable via Ctrl+Z, but a destructive
    /// action that removes a recorded/uploaded layer from the grid still warrants a confirmation).
    /// DeleteLayer itself stays a separate, dialog-free method so the actual delete logic is
    /// testable without a message box blocking the test.</summary>
    private async Task ConfirmAndDeleteLayerAsync(LayerRowViewModel? row)
    {
        if (row is null || !IsLiveRow(row)) return;

        var answer = await Dialogs.ShowMessageAsync(this, $"Delete layer {row.SlotNumber}? You can undo this.", "Delete Layer", MessageButtons.YesNo);
        if (answer != MessageResult.Yes) return;

        DeleteLayer(row);
    }

    private void DeleteLayer(LayerRowViewModel? row)
    {
        if (row is null || !IsLiveRow(row)) return;

        string name = row.DisplayName;
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
    // enough for the app's up-to-9-layer/small-clip scale -- see the throughput spike referenced
    // in the engine's doc comment). Engine commands are awaited, never waited on, so a decode never
    // blocks input.

    private void DebounceRefreshPreview()
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    /// <summary>Freezing bakes the layer's whole chain into a buffer, or plays it live again, so
    /// it always needs a full refresh.</summary>
    private void FreezeToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (((StyledElement)sender!).DataContext is not LayerRowViewModel row || row.Layer is null) return;

        row.Frozen = !row.Frozen;
        DebounceRefreshPreview();
        PushUndoSnapshot();
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
        SyncPreviewVideoSettings();
        if (_layers.Layers.Count == 0)
        {
            await AwaitPreviewCommand(_previewEngine.SetLayersAsync(_layers.Layers));
            CompositeCanvas.SetFrame(null);
            return;
        }

        await AwaitPreviewCommand(_previewEngine.RefreshAsync(_layers.Layers));
    }

    /// <summary>PlayButton is a single toggling 48px circle, not separate
    /// Play/Stop buttons -- branches on IsPlaying first so Space (which just clicks this once, see
    /// MainWindow_PreviewKeyDown) toggles correctly either way.</summary>
    private async void PlayButton_Click(object? sender, RoutedEventArgs e)
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
            SyncPreviewVideoSettings();
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
    private void RecordButton_Click(object? sender, RoutedEventArgs e) => RecordingSetupMenuItem_Click(sender, e);

    private void RestartButton_Click(object? sender, RoutedEventArgs e) => RunPreviewCommand(_previewEngine.RestartAsync());

    private void TimelineSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TimelineSlider).Properties.IsLeftButtonPressed) return;
        _isScrubbing = true;

        // Clicking the track (not the thumb) jumps straight to that point, so the seek issued on
        // release goes where the user clicked instead of wherever the thumb already was.
        bool onThumb = e.Source is Visual source && (source is Thumb || source.FindAncestorOfType<Thumb>() is not null);
        if (!onThumb && TimelineSlider.Bounds.Width > 0)
        {
            double fraction = Math.Clamp(e.GetPosition(TimelineSlider).X / TimelineSlider.Bounds.Width, 0, 1);
            TimelineSlider.Value = TimelineSlider.Minimum + fraction * (TimelineSlider.Maximum - TimelineSlider.Minimum);
        }
    }

    private void TimelineSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        RunPreviewCommand(_previewEngine.SeekAsync(TimelineSlider.Value));
    }

    private void TimelineSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isScrubbing)
            SetTimeReadout(TimelineSlider.Value, _previewEngine.DurationMs);
    }

    private void ShowLayerLabelsMenuItem_Click(object? sender, RoutedEventArgs e)
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

    private IBrush ThemeBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.Gray;

    /// <summary>Draws the ruler's time-tick labels (0:00, 0:30, 1:00 ... scaled to the project
    /// duration) plus the song in/out markers, and updates the readout text beside them. Rebuilt
    /// from scratch on every call (cheap: a handful of TextBlocks/lines) rather than diffed, so a
    /// duration or track-width change, or a song in/out edit, always redraws correctly.</summary>
    private void RedrawRulerCanvas()
    {
        RulerCanvas.Children.Clear();
        double durationMs = _previewEngine.DurationMs;
        double widthPx = RulerCanvas.Bounds.Width;

        foreach (double tickMs in ClipTimelineMath.ComputeTickTimesMs(durationMs, widthPx))
        {
            var (x, _) = ClipTimelineMath.ToPixelRange(tickMs, tickMs, durationMs, widthPx);
            var label = new TextBlock
            {
                Text = ClipTimelineMath.FormatTickLabel(tickMs),
                FontSize = 10,
                Foreground = ThemeBrush("MutedTextBrush"),
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
        var line = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(x, 0),
            EndPoint = new Point(x, 20),
            Stroke = ThemeBrush("StrongBorderBrush"),
            StrokeThickness = 2,
        };
        ToolTip.SetTip(line, $"Song {label}: {FormatTime(ms)}");
        RulerCanvas.Children.Add(line);
    }

    // ----- Song in/out (project-wide export range, set from the playhead) -----

    private void SetSongInMenuItem_Click(object? sender, RoutedEventArgs e) => SetSongIn();
    private void SetSongOutMenuItem_Click(object? sender, RoutedEventArgs e) => SetSongOut();
    private void ClearSongRangeMenuItem_Click(object? sender, RoutedEventArgs e) => ClearSongRange();

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

    // ----- Project save/load/export -----

    private async void SaveMenuItem_Click(object? sender, RoutedEventArgs e) => await SaveProjectAsync(forceDialog: false);
    /// <summary>File > Save As...</summary>
    private async void SaveProjectButton_Click(object? sender, RoutedEventArgs e) => await SaveProjectAsync(forceDialog: true);

    /// <summary>Save (forceDialog=false: straight to CurrentFilePath, dialog only if untitled) or
    /// Save As (forceDialog=true: always the dialog). Returns true only if the file was actually
    /// written -- the discard prompt relies on that to abort on a cancelled dialog or failed write.</summary>
    // TODO(polish): flush focused TextBox before save/close (a trim or BPM edit still mid-focus
    // isn't committed by Ctrl+S/Alt+F4 -- see spec's Known limitations).
    private async Task<bool> SaveProjectAsync(bool forceDialog)
    {
        string? filePath = forceDialog ? null : _session.CurrentFilePath;
        if (filePath is null)
        {
            string? chosen = await Dialogs.PickSavePathAsync(this, "Save project", Dialogs.ProjectFiles, ProjectSession.ProjectFileSuffix,
                suggestedFileName: _session.CurrentFilePath is not null ? Path.GetFileName(_session.CurrentFilePath) : null,
                initialDirectory: _session.CurrentFilePath is not null ? Path.GetDirectoryName(_session.CurrentFilePath) : null);
            if (chosen is null) return false;

            // L7: enforce the suffix explicitly instead of relying on the dialog's own extension
            // logic (DefaultExt doesn't reliably stop a double-append for multi-segment extensions).
            // Dialog results only -- a project opened as plain *.json is saved back under its own name (D5).
            filePath = chosen.EndsWith(ProjectSession.ProjectFileSuffix, StringComparison.OrdinalIgnoreCase)
                ? chosen
                : chosen + ProjectSession.ProjectFileSuffix;
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
    private async Task<bool> ConfirmDiscardUnsavedChangesAsync()
    {
        if (!_session.IsDirty) return true;

        var answer = await Dialogs.ShowMessageAsync(this, $"Save changes to {_session.DisplayName}?", "A-Capella", MessageButtons.YesNoCancel);
        return answer switch
        {
            MessageResult.Yes => await SaveProjectAsync(forceDialog: false),
            MessageResult.No => true,
            _ => false,   // Cancel, or the message box's own close button
        };
    }

    /// <summary>Driven by ProjectSession.SaveStateChanged. Direct writes, matching the rest of this
    /// window (no INotifyPropertyChanged on MainWindow). The title shows in the dock/Alt+Tab and in
    /// the toolbar copy.</summary>
    private void UpdateWindowTitle()
    {
        string label = _session.IsDirty ? $"{_session.DisplayName} •" : _session.DisplayName;
        Title = $"A-Capella — {label}";
        ProjectTitleText.Text = label;
    }

    private async void OpenProjectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";
            return;
        }

        if (!await ConfirmDiscardUnsavedChangesAsync()) return;

        string? fileName = await Dialogs.PickFileAsync(this, "Open project", Dialogs.ProjectFiles);
        if (fileName is null) return;

        try
        {
            // Bug audit #5: ProjectSession.Open() now releases every live hosted-plugin instance
            // before restoring. If preview playback is still running, HostedPluginSampleProvider
            // objects wired into its chain hold raw IHostedPlugin references that the audio
            // render thread calls ProcessBlock on -- releasing out from under that is a
            // use-after-free on native memory. Stop playback first, same shape as StopButton_Click.
            await AwaitPreviewCommand(_previewEngine.StopAsync());

            ApplyRestore(() =>
            {
                _session.Open(fileName);
                return true;
            });
            StatusText.Text = $"Project opened: {Path.GetFileName(fileName)} ({_layers.Layers.Count} layer(s)).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open failed: {ex.Message}";
        }
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
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

        string? exportPath = await Dialogs.PickSavePathAsync(this, "Export MP4", Dialogs.Mp4Files, ".mp4");
        if (exportPath is null) return;
        if (!exportPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) exportPath += ".mp4";

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

        var (snapshotLayers, snapshotMasterVolumeDb, snapshotSongInMs, snapshotSongOutMs, snapshotVideo, snapshotMasterLimiter) = _session.SnapshotForExport();

        // Built on the UI thread (candidate 3 / Improvement_Proposal_2026-09-24): Progress<T>
        // captures the current SynchronizationContext at construction, so every Report() call from
        // the background export thread below marshals back here with no explicit Dispatcher.Invoke.
        ExportProgressBar.Value = 0;
        ExportProgressBar.IsVisible = true;
        bool exportFinished = false;                                // UI thread only: late queued progress reports must not overwrite the final status
        var progress = new Progress<double>(fraction =>
        {
            if (exportFinished) return;
            ExportProgressBar.Value = fraction;
            StatusText.Text = $"Exporting... {fraction:P0}";
        });

        _ = Task.Run(() =>
        {
            try
            {
                using var exportEngine = new ExportEngine(hostedService: _hostedService);
                exportEngine.Export(snapshotLayers, exportPath, masterVolumeDb: snapshotMasterVolumeDb, progress: progress,
                    rangeStartMs: snapshotSongInMs, rangeOutMs: snapshotSongOutMs, video: snapshotVideo, masterLimiter: snapshotMasterLimiter);
                Dispatcher.UIThread.Invoke(() => { exportFinished = true; StatusText.Text = $"Export complete: {Path.GetFileName(exportPath)}"; });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Invoke(() => { exportFinished = true; StatusText.Text = $"Export failed: {ex.Message}"; });
            }
            finally
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    ExportMenuItem.IsEnabled = true;
                    ExportProgressBar.IsVisible = false;
                });
            }
        });
    }

    // ----- Menu bar: File > New, Help > About (v5 P1 task 1) -----

    private async void NewProjectMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";
            return;
        }

        if (!await ConfirmDiscardUnsavedChangesAsync()) return;

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

    private async void AboutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        await Dialogs.ShowMessageAsync(this, "A-Capella\nMulti-layer vocal recording and mixing.", "About A-Capella");
    }
}
