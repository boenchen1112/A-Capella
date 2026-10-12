using System.ComponentModel;
using System.Runtime.CompilerServices;
using A_Capella.Engine.Ffmpeg;
using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Project;

namespace A_Capella.App.ViewModels;

/// <summary>
/// One row in the Editor screen's track sidebar. Wraps a LayerModel
/// once a source is attached; Layer is null for a freshly added row still in the Record/Upload
/// choice state. Mix-parameter properties write straight through to the wrapped LayerModel and
/// notify LiveParamChanged so the caller can refresh the live preview -- FX/pan/mute/solo changes
/// don't move pixels, but the preview transport plays audio too, so they still need a refresh
/// while playing (see MainWindow's debounced RefreshPreviewLive).
///
/// Timing: LayerModel.CalibratedOffsetMs (automatic round-trip latency correction) is applied
/// under the hood via GetShiftMs; the strip's -/+ buttons add a manual nudge on top
/// (LayerModel.ManualOffsetMs) for layers that still sit slightly off.
/// </summary>
public class LayerRowViewModel : INotifyPropertyChanged
{
    private LayerModel? _layer;

    /// <summary>The one HostedPluginService the app shares, set once by MainWindow at startup. Only
    /// used to tell whether an effect's plugin is installed.</summary>
    public static HostedPluginService? SharedHostedService { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LiveParamChanged;

    /// <summary>Raised when the user picks a different preset (or Off) for one of this layer's
    /// effects, or applies a package -- the caller records an undo step. LiveParamChanged is raised
    /// too, so the preview refreshes.</summary>
    public event Action? EffectChosen;

    public LayerRowViewModel(int slotNumber)
    {
        SlotNumber = slotNumber;
        Effects = FxSlots.All
            .Select(slot => new EffectSlotViewModel(slot, () => _layer?.MixParameters, OnEffectChosen))
            .ToList();
    }

    /// <summary>The four effects, in chain order, each a preset dropdown in the channel panel.</summary>
    public IReadOnlyList<EffectSlotViewModel> Effects { get; }

    private void OnEffectChosen(EffectSlotViewModel _)
    {
        OnPropertyChanged(nameof(SourceLabel));
        LiveParamChanged?.Invoke();
        EffectChosen?.Invoke();
    }

    /// <summary>Puts a package's presets on this layer (all four effects), then refreshes the preview.</summary>
    public void ApplyPackage(FxPackage package)
    {
        if (Params is null) return;
        package.ApplyTo(Params);
        foreach (var effect in Effects) effect.Refresh();
        OnPropertyChanged(nameof(SourceLabel));
        LiveParamChanged?.Invoke();
        EffectChosen?.Invoke();
    }

    /// <summary>This layer's current effect choices as a package with the given name.</summary>
    public FxPackage? ToPackage(string name) => Params is null ? null : FxPackage.FromParameters(name, Params);

    public int SlotNumber { get; }

    private bool _isSelected;

    /// <summary>Mixer-strip selection (redesigned single-screen layout): exactly one strip
    /// (a layer, or the master strip via MainWindow's separate master-selected flag) is selected at
    /// a time, driving which FX chain the FX panel's DataContext currently shows.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    private double _meterLevel;

    /// <summary>0..1 unit-scaled RMS level for this strip's vertical meter, refreshed by
    /// MainWindow's existing ~30Hz meter-poll timer (mirrors the old LayerMeterBar, now per-strip
    /// instead of a single "currently mixing layer" bar since all strips are visible at once).</summary>
    public double MeterLevel
    {
        get => _meterLevel;
        set { _meterLevel = value; OnPropertyChanged(nameof(MeterLevel)); }
    }

    private double _timelineDurationMs;

    /// <summary>The project's overall timeline duration, mirrored onto every row by MainWindow
    /// whenever it changes (UpdateTimelineRangeUi) -- gives the lane clip-bar's XAML bindings
    /// (ClipBarGeometryConverter) a per-row bindable value without MainWindow needing
    /// INotifyPropertyChanged of its own.</summary>
    public double TimelineDurationMs
    {
        get => _timelineDurationMs;
        set { if (_timelineDurationMs == value) return; _timelineDurationMs = value; OnPropertyChanged(nameof(TimelineDurationMs)); }
    }

    public LayerModel? Layer
    {
        get => _layer;
        set
        {
            _layer = value;
            OnPropertyChanged(nameof(Layer));
            OnPropertyChanged(nameof(HasSource));
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(MixingHeaderLabel));
            OnPropertyChanged(nameof(SourceStateLabel));
            OnPropertyChanged(nameof(CellColor));
            OnPropertyChanged(nameof(Mirror));
            NotifyManualOffsetChanged();
            RefreshMixDisplayProperties();
            RefreshSourceDurationAsync();
        }
    }

    // ----- Source duration: drives the lane clip-bar geometry and the "Recorded · 2:15" label -----

    private double _sourceDurationMs;

    /// <summary>Full source-file duration in ms, via ffprobe (MediaProbe.GetDurationSeconds), or 0
    /// while unknown (no layer, probe still running, or ffprobe failed) -- 0 means "not known yet",
    /// not "zero-length clip"; callers (lane clip-bar drag handles) must treat 0 as "disable the
    /// handle" rather than a real duration (see ClipTimelineMath.ClampTrimEnd's doc comment).</summary>
    public double SourceDurationMs
    {
        get => _sourceDurationMs;
        private set { _sourceDurationMs = value; OnPropertyChanged(nameof(SourceDurationMs)); OnPropertyChanged(nameof(EffectiveTrimEndMs)); OnPropertyChanged(nameof(SourceLabel)); }
    }

    /// <summary>MediaProbe shells out to ffprobe (blocking I/O) -- never called on the UI thread.
    /// Guarded by a per-row generation token so a stale probe for a since-replaced/removed source
    /// can't overwrite a newer one's result.</summary>
    private int _durationProbeGeneration;

    private void RefreshSourceDurationAsync()
    {
        SourceDurationMs = 0;
        var layer = _layer;
        if (layer is null) return;

        int generation = ++_durationProbeGeneration;
        string path = layer.SourcePath;
        // Marshals back onto the UI thread via the app's Dispatcher when one exists (the running
        // app); runs the continuation inline otherwise (headless unit tests have no WPF Dispatcher
        // and don't need thread affinity for a plain property set). Deliberately not
        // TaskScheduler.FromCurrentSynchronizationContext(): that throws outside a WPF message loop,
        // which a plain [StaFact] test (no Window shown/pumped) doesn't install.
        Task.Run(() => MediaProbe.GetDurationSeconds(path)).ContinueWith(t =>
        {
            void Apply()
            {
                if (generation != _durationProbeGeneration) return;   // superseded (Layer changed again, or row reused)
                if (t.IsCompletedSuccessfully && ReferenceEquals(_layer, layer))
                    SourceDurationMs = t.Result * 1000.0;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.Invoke(Apply);
            else
                Apply();
        }, TaskScheduler.Default);
    }

    /// <summary>Call after the wrapped LayerModel's source was replaced IN PLACE (same object,
    /// same LayerId, same MixParameters). Raises only the source- and trim-derived properties.
    /// </summary>
    public void NotifySourceReplaced()
    {
        OnPropertyChanged(nameof(TrimStartMs));        // visible: the strip's In box
        OnPropertyChanged(nameof(TrimEndText));        // visible: the strip's Out box
        NotifyManualOffsetChanged();                   // visible: a retake resets the nudge (ReplaceSource)
        OnPropertyChanged(nameof(IconGlyph));          // not bound today; raised so a future binding is right
        OnPropertyChanged(nameof(SourceStateLabel));   // not bound today; same
    }

    public bool HasSource => _layer is not null;

    public string IconGlyph => _layer?.Kind switch
    {
        LayerKind.RecordedAV => "\U0001F3A4",   // mic
        LayerKind.UploadedVideo => "⬆",     // up arrow
        LayerKind.UploadedAudioOnly => "⬆",
        _ => "+",
    };

    /// <summary>Editable layer name (v5 P2 task 1). Blank clears back to the positional default
    /// ("Layer N") rather than persisting an empty string as a "real" name.</summary>
    public string Name
    {
        get => _layer?.Name ?? "";
        set
        {
            if (_layer is null) return;
            _layer.Name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(MixingHeaderLabel));
        }
    }

    public string DisplayName => _layer?.Name is { Length: > 0 } n ? n : $"Layer {SlotNumber}";

    public string MixingHeaderLabel => $"Mixing — {DisplayName}";

    /// <summary>Grid-identification color chip (v5 P2 task 3), keyed by CellIndex so a layer keeps
    /// its color as long as it stays in the same grid cell.</summary>
    public System.Windows.Media.Brush CellColor
    {
        get
        {
            var color = A_Capella.Engine.Composite.LayerColorPalette.GetColor(_layer?.CellIndex ?? SlotNumber - 1);
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue));
        }
    }

    public string SourceStateLabel => _layer?.Kind switch
    {
        LayerKind.RecordedAV => "recorded",
        LayerKind.UploadedVideo => "uploaded",
        LayerKind.UploadedAudioOnly => "uploaded (audio)",
        _ => "empty",
    };

    /// <summary>The lane row's sub-label ("Recorded · 2:15", "Uploaded · 2:00 · 3
    /// effects", or "Empty"). Duration is blank while the probe hasn't returned yet rather than
    /// showing a misleading "0:00".</summary>
    public string SourceLabel
    {
        get
        {
            if (_layer is null) return "Empty";
            string kind = _layer.Kind == LayerKind.RecordedAV ? "Recorded" : "Uploaded";
            string duration = SourceDurationMs > 0 ? FormatMinutesSeconds(SourceDurationMs) : "";
            int effects = EnabledEffectsCount;
            var parts = new List<string> { kind };
            if (duration.Length > 0) parts.Add(duration);
            if (effects > 0) parts.Add(effects == 1 ? "1 effect" : $"{effects} effects");
            return string.Join(" · ", parts);
        }
    }

    private static string FormatMinutesSeconds(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    /// <summary>How many of the layer's effects have a preset chosen, for the lane sub-label's
    /// "· N effects" suffix.</summary>
    private int EnabledEffectsCount => Params is null ? 0 : FxSlots.All.Count(slot => slot.IsEnabled(Params));

    private LayerMixParameters? Params => _layer?.MixParameters;

    // ----- Trim (Editor screen) -----

    public double TrimStartMs
    {
        get => _layer?.TrimStartMs ?? 0;
        set { if (_layer is null) return; _layer.TrimStartMs = Math.Max(0, value); OnPropertyChanged(nameof(TrimStartMs)); LiveParamChanged?.Invoke(); }
    }

    /// <summary>Text-editable end trim: empty/unparseable means "to the end of the source"
    /// (null TrimEndMs).</summary>
    public string TrimEndText
    {
        get => _layer?.TrimEndMs?.ToString("F0") ?? "";
        set
        {
            if (_layer is null) return;
            _layer.TrimEndMs = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) ? ms : null;
            OnPropertyChanged(nameof(TrimEndText));
            OnPropertyChanged(nameof(TrimEndMs));
            OnPropertyChanged(nameof(EffectiveTrimEndMs));
            LiveParamChanged?.Invoke();
        }
    }

    /// <summary>Numeric trim-out, for the lane clip-bar's drag-handle math (ClipTimelineMath) --
    /// TrimEndText exists separately for the channel panel's free-text field. Setting it writes
    /// through the same LayerModel field as TrimEndText.</summary>
    public double? TrimEndMs
    {
        get => _layer?.TrimEndMs;
        set
        {
            if (_layer is null) return;
            _layer.TrimEndMs = value;
            OnPropertyChanged(nameof(TrimEndMs));
            OnPropertyChanged(nameof(TrimEndText));
            OnPropertyChanged(nameof(EffectiveTrimEndMs));
            LiveParamChanged?.Invoke();
        }
    }

    /// <summary>TrimEndMs if the user has set one, else the source's own duration -- "to the end of
    /// the source" resolved to an actual millisecond value for clip-bar geometry. 0 while the
    /// duration probe hasn't returned yet AND no explicit trim-out is set (see
    /// ClipTimelineMath.ClampTrimEnd's doc comment on why 0 means "not known", not "zero-length").</summary>
    public double EffectiveTrimEndMs => TrimEndMs ?? SourceDurationMs;

    // ----- Pan / mute / solo (Mixing screen top strip) -----

    public float Pan
    {
        get => Params?.Pan ?? 0f;
        set { if (Params is null) return; Params.Pan = value; OnPropertyChanged(nameof(Pan)); OnPropertyChanged(nameof(PanDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string PanDisplay => $"{Pan:F2}";

    public bool Mute
    {
        get => Params?.Mute ?? false;
        set { if (Params is null) return; Params.Mute = value; OnPropertyChanged(nameof(Mute)); LiveParamChanged?.Invoke(); }
    }

    /// <summary>Manual timing nudge in ms: positive plays this layer later than the others.</summary>
    public double ManualOffsetMs => _layer?.ManualOffsetMs ?? 0;

    /// <summary>"+12 ms" / "-3 ms" / "0 ms" for the strip's timing label.</summary>
    public string ManualOffsetDisplay => ManualOffsetMs == 0 ? "0 ms" : $"{ManualOffsetMs:+0.#;-0.#} ms";

    /// <summary>Moves this layer earlier or later by deltaMs (see LayerModel.NudgeManualOffset) and
    /// refreshes the live preview; the caller pushes the undo step.</summary>
    public void Nudge(double deltaMs)
    {
        if (_layer is null) return;
        _layer.NudgeManualOffset(deltaMs);
        NotifyManualOffsetChanged();
        LiveParamChanged?.Invoke();
    }

    public void ResetNudge()
    {
        if (_layer is null || _layer.ManualOffsetMs == 0) return;
        _layer.ManualOffsetMs = 0;
        NotifyManualOffsetChanged();
        LiveParamChanged?.Invoke();
    }

    /// <summary>Call after the layer's timing was changed behind this row's back (auto-align).</summary>
    public void NotifyTimingChanged()
    {
        NotifyManualOffsetChanged();
        LiveParamChanged?.Invoke();
    }

    private void NotifyManualOffsetChanged()
    {
        OnPropertyChanged(nameof(ManualOffsetMs));
        OnPropertyChanged(nameof(ManualOffsetDisplay));
    }

    /// <summary>Flips the picture left-to-right in the preview and export (video only).</summary>
    public bool Mirror
    {
        get => _layer?.Mirror ?? false;
        set { if (_layer is null) return; _layer.Mirror = value; OnPropertyChanged(nameof(Mirror)); LiveParamChanged?.Invoke(); }
    }

    public bool Solo
    {
        get => Params?.Solo ?? false;
        set { if (Params is null) return; Params.Solo = value; OnPropertyChanged(nameof(Solo)); LiveParamChanged?.Invoke(); }
    }

    /// <summary>Plays a pre-rendered buffer of this layer's whole chain instead of running it live
    /// (see MixEngine's frozen-layer path) -- everything except Mute/Solo stays baked. Plain
    /// state flip.</summary>
    public bool Frozen
    {
        get => Params?.Frozen ?? false;
        set
        {
            if (Params is null) return;
            Params.Frozen = value;
            OnPropertyChanged(nameof(Frozen));
            OnPropertyChanged(nameof(IsNotFrozen));
            OnPropertyChanged(nameof(SourceLabel));
            LiveParamChanged?.Invoke();
        }
    }

    /// <summary>For the channel panel's FX/pan/gain/pitch controls' IsEnabled bindings -- a frozen
    /// layer's baked chain can't be edited live without unfreezing first.</summary>
    public bool IsNotFrozen => !Frozen;

    /// <summary>Studio (overall) gain -- labeled "Volume" in the sidebar (v5 P2 task 2, replacing
    /// the old Mixing-screen top-strip "Gain" slider it used to share this same property with).</summary>
    public float GainDb
    {
        get => Params?.GainDb ?? 0f;
        set { if (Params is null) return; Params.GainDb = value; OnPropertyChanged(nameof(GainDb)); OnPropertyChanged(nameof(GainDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string GainDisplay => $"{GainDb:F1} dB";

    /// <summary>Refreshes every display-bound property after Layer is swapped wholesale (e.g. a
    /// recording/upload just attached, or a project was reopened) so bound controls pick up the
    /// new source's stored values instead of stale defaults.</summary>
    private void RefreshMixDisplayProperties()
    {
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(MixingHeaderLabel));
        OnPropertyChanged(nameof(CellColor)); OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(TrimStartMs)); OnPropertyChanged(nameof(TrimEndText)); OnPropertyChanged(nameof(TrimEndMs)); OnPropertyChanged(nameof(EffectiveTrimEndMs));
        OnPropertyChanged(nameof(GainDb)); OnPropertyChanged(nameof(GainDisplay));
        OnPropertyChanged(nameof(Pan)); OnPropertyChanged(nameof(PanDisplay));
        foreach (var effect in Effects) effect.Refresh();
        OnPropertyChanged(nameof(Mute)); OnPropertyChanged(nameof(Solo));
        OnPropertyChanged(nameof(Frozen)); OnPropertyChanged(nameof(IsNotFrozen));
    }

    /// <summary>Renames the layer, but only pushes a dirty/undo-worthy change if the trimmed value
    /// actually differs from the current name (MainWindow's lane name TextBox calls this on
    /// LostFocus/Enter instead of setting Name directly) -- so clicking into the field and tabbing
    /// back out without typing anything doesn't record a no-op undo step. Returns true if the name
    /// actually changed.</summary>
    public bool CommitNameIfChanged(string newValue)
    {
        string normalized = string.IsNullOrWhiteSpace(newValue) ? "" : newValue.Trim();
        if (normalized == Name) return false;
        Name = normalized;
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
