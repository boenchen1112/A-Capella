using System.ComponentModel;
using System.Runtime.CompilerServices;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;

namespace Acapella.App.ViewModels;

/// <summary>
/// One row in the Editor screen's track sidebar (see UI_Design_Spec.md v2). Wraps a LayerModel
/// once a source is attached; Layer is null for a freshly added row still in the Record/Upload
/// choice state. Mix-parameter properties write straight through to the wrapped LayerModel and
/// notify LiveParamChanged so the caller can refresh the live preview -- FX/pan/mute/solo changes
/// don't move pixels, but the preview transport plays audio too, so they still need a refresh
/// while playing (see MainWindow's debounced RefreshPreviewLive).
///
/// v2 removes the standalone manual-offset ("Sync") slider from the UI entirely -- trim now
/// covers the Editor screen's in/out need, and LayerModel.CalibratedOffsetMs (automatic
/// round-trip latency correction) stays wired under the hood via GetShiftMs regardless of what
/// the UI exposes. ManualOffsetMs is intentionally left at its default (0) since v2 gives the
/// user no control to change it.
/// </summary>
public class LayerRowViewModel : INotifyPropertyChanged
{
    private LayerModel? _layer;

    // v7 Q0 task 1 (audit A1): one HostedPluginService per app session -- set once by MainWindow
    // at startup, and the SAME instance MainWindow's MixEngine/PreviewPlaybackEngine/ExportEngine
    // all share, so the instance this row's launcher buttons open an editor on is always the exact
    // instance actually processing audio. Static (not per-row) since there's exactly one per this
    // single-window app; avoids threading an extra constructor parameter through every
    // LayerRowViewModel call site.
    public static HostedPluginService? SharedHostedService { get; set; }

    // v7 Q0 task 3 (audit A2): slots whose editor this row has opened at least once -- their live
    // instance's state gets pulled/polled even after the editor window itself is closed (the
    // instance stays alive in the shared cache until the layer is removed).
    private readonly HashSet<FxSlot> _openedHostedSlots = new();
    private readonly Dictionary<FxSlot, byte[]?> _lastPolledHostedState = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LiveParamChanged;

    public LayerRowViewModel(int slotNumber)
    {
        SlotNumber = slotNumber;
    }

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

    public LayerModel? Layer
    {
        get => _layer;
        set
        {
            _layer = value;
            // Bug audit #5: a row's poll tracking is per-row but rows outlive a project swap --
            // only Layer is reassigned. Without this, a fresh instance for the new project's
            // (LayerId, Stage) still gets diffed against the previous project's polled bytes on
            // the first poll, firing a spurious PushUndoSnapshot() for an edit nobody made.
            _openedHostedSlots.Clear();
            _lastPolledHostedState.Clear();
            OnPropertyChanged(nameof(Layer));
            OnPropertyChanged(nameof(HasSource));
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(MixingHeaderLabel));
            OnPropertyChanged(nameof(SourceStateLabel));
            OnPropertyChanged(nameof(CellColor));
            RefreshMixDisplayProperties();
        }
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
            var color = Acapella.Engine.Composite.LayerColorPalette.GetColor(_layer?.CellIndex ?? SlotNumber - 1);
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
            LiveParamChanged?.Invoke();
        }
    }

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

    public bool Solo
    {
        get => Params?.Solo ?? false;
        set { if (Params is null) return; Params.Solo = value; OnPropertyChanged(nameof(Solo)); LiveParamChanged?.Invoke(); }
    }

    // ----- EQ subtab -----

    public bool EqEnabled
    {
        get => Params?.EqEnabled ?? false;
        set { if (Params is null) return; Params.EqEnabled = value; OnPropertyChanged(nameof(EqEnabled)); LiveParamChanged?.Invoke(); }
    }

    public float LowShelfGainDb
    {
        get => Params?.LowShelfGainDb ?? 0f;
        set { if (Params is null) return; Params.LowShelfGainDb = value; OnPropertyChanged(nameof(LowShelfGainDb)); OnPropertyChanged(nameof(LowEqDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string LowEqDisplay => $"{LowShelfGainDb:F1} dB";

    public float MidBellGainDb
    {
        get => Params?.MidBellGainDb ?? 0f;
        set { if (Params is null) return; Params.MidBellGainDb = value; OnPropertyChanged(nameof(MidBellGainDb)); OnPropertyChanged(nameof(MidEqDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string MidEqDisplay => $"{MidBellGainDb:F1} dB";

    public float HighShelfGainDb
    {
        get => Params?.HighShelfGainDb ?? 0f;
        set { if (Params is null) return; Params.HighShelfGainDb = value; OnPropertyChanged(nameof(HighShelfGainDb)); OnPropertyChanged(nameof(HighEqDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string HighEqDisplay => $"{HighShelfGainDb:F1} dB";

    // ----- Noise gate subtab -----

    // v7 Q1 task 2: Q0 added the Enabled flag to LayerMixParameters (FL-style insert slot, off by
    // default) but never surfaced it here -- without this the slot's power button has nothing to
    // bind to.
    public bool NoiseGateEnabled
    {
        get => Params?.NoiseGateEnabled ?? false;
        set { if (Params is null) return; Params.NoiseGateEnabled = value; OnPropertyChanged(nameof(NoiseGateEnabled)); LiveParamChanged?.Invoke(); }
    }

    public float NoiseGateThresholdDb
    {
        get => Params?.NoiseGateThresholdDb ?? -60f;
        set { if (Params is null) return; Params.NoiseGateThresholdDb = value; OnPropertyChanged(nameof(NoiseGateThresholdDb)); OnPropertyChanged(nameof(GateDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string GateDisplay => $"{NoiseGateThresholdDb:F1} dB";

    public float NoiseGateReleaseMs
    {
        get => Params?.NoiseGateReleaseMs ?? 100f;
        set { if (Params is null) return; Params.NoiseGateReleaseMs = value; OnPropertyChanged(nameof(NoiseGateReleaseMs)); OnPropertyChanged(nameof(GateReleaseDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string GateReleaseDisplay => $"{NoiseGateReleaseMs:F0} ms";

    // ----- Compressor subtab (new FX, user-approved scope change) -----

    public bool CompressorEnabled
    {
        get => Params?.CompressorEnabled ?? false;
        set { if (Params is null) return; Params.CompressorEnabled = value; OnPropertyChanged(nameof(CompressorEnabled)); LiveParamChanged?.Invoke(); }
    }

    public float CompressorThresholdDb
    {
        get => Params?.CompressorThresholdDb ?? -18f;
        set { if (Params is null) return; Params.CompressorThresholdDb = value; OnPropertyChanged(nameof(CompressorThresholdDb)); OnPropertyChanged(nameof(CompressorThresholdDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string CompressorThresholdDisplay => $"{CompressorThresholdDb:F1} dB";

    public float CompressorRatio
    {
        get => Params?.CompressorRatio ?? 2f;
        set { if (Params is null) return; Params.CompressorRatio = value; OnPropertyChanged(nameof(CompressorRatio)); OnPropertyChanged(nameof(CompressorRatioDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string CompressorRatioDisplay => $"{CompressorRatio:F1}:1";

    // ----- Limiter subtab (new FX, user-approved scope change) -----

    public bool LimiterEnabled
    {
        get => Params?.LimiterEnabled ?? false;
        set { if (Params is null) return; Params.LimiterEnabled = value; OnPropertyChanged(nameof(LimiterEnabled)); LiveParamChanged?.Invoke(); }
    }

    public float LimiterCeilingDb
    {
        get => Params?.LimiterCeilingDb ?? -0.3f;
        set { if (Params is null) return; Params.LimiterCeilingDb = value; OnPropertyChanged(nameof(LimiterCeilingDb)); OnPropertyChanged(nameof(LimiterCeilingDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string LimiterCeilingDisplay => $"{LimiterCeilingDb:F1} dB";

    public float LimiterGainDb
    {
        get => Params?.LimiterGainDb ?? 0f;
        set { if (Params is null) return; Params.LimiterGainDb = value; OnPropertyChanged(nameof(LimiterGainDb)); OnPropertyChanged(nameof(LimiterGainDisplay)); LiveParamChanged?.Invoke(); }
    }
    public string LimiterGainDisplay => $"{LimiterGainDb:F1} dB";

    // ----- Reverb subtab (hosted-only, no native fallback -- v6 P3 task 5) -----

    public bool ReverbEnabled
    {
        get => Params?.ReverbEnabled ?? false;
        set { if (Params is null) return; Params.ReverbEnabled = value; OnPropertyChanged(nameof(ReverbEnabled)); LiveParamChanged?.Invoke(); }
    }

    // ----- Hosted-backend auto-selection (v6 P3 task 1): each stage below shows a native slider
    // panel when its matching FabFilter plugin isn't detected, or a launcher button when it is --
    // never both, never a user-facing selector (see build plan P3 task 1's rationale). -----

    public bool IsReverbHosted => IsHosted(FxSlots.Reverb);
    public bool IsEqHosted => IsHosted(FxSlots.Eq);
    public bool IsNoiseGateHosted => IsHosted(FxSlots.NoiseGate);
    public bool IsCompressorHosted => IsHosted(FxSlots.Compressor);
    public bool IsLimiterHosted => IsHosted(FxSlots.Limiter);

    private static bool IsHosted(FxSlot slot) => SharedHostedService?.IsAvailable(slot.PluginLabel) ?? false;

    /// <summary>Fetches (or lazily creates, via the one shared HostedPluginService -- v7 Q0 task 1,
    /// audit A1) the same live instance the mix chain actually processes audio through, and opens
    /// its own top-level editor window (P3a task 7). No-op when the slot's plugin isn't installed
    /// (the native sliders are shown instead). Edits made there are audible on the next rebuild
    /// since it's the live instance, not a copy. Marks the slot as "opened" so
    /// PollHostedStateChanges (task 8) keeps pulling its state even after this editor window is
    /// closed (the instance itself stays alive until the layer is removed).</summary>
    public void OpenHostedEditor(FxSlot slot)
    {
        if (_layer is null || SharedHostedService is null || Params is null || !IsHosted(slot)) return;
        var instance = SharedHostedService.GetOrCreateInstance(_layer.LayerId, slot.Stage, slot.PluginLabel, slot.GetHostedState(Params));
        SharedHostedService.ShowEditor(instance, $"{slot.PluginLabel} — {DisplayName}");
        _openedHostedSlots.Add(slot);
        _lastPolledHostedState[slot] = slot.GetHostedState(Params);
    }

    /// <summary>Polls every plugin editor this layer uses -- the FX slots' hosted plugins and the
    /// Melodyne ARA session -- for edits made in their own windows since the last poll (there's no
    /// native "edited" event to hook). Returns true if anything changed, so the caller can refresh
    /// the preview and record an undo step. Both kinds are always polled; a change found in one must
    /// never skip the other (audit A5).</summary>
    public bool PollEditorChanges()
    {
        bool fxChanged = PollHostedStateChanges();
        bool melodyneChanged = PollMelodyneStateChanged();
        return fxChanged || melodyneChanged;
    }

    /// <summary>Pulls live state for every slot this row has ever opened an editor for, and
    /// updates Params' matching hosted-state field if it changed since the last poll (v7 Q0 task 8,
    /// audit A2/B11 -- also what lets a Pro-R tail-length change picked up here feed into the next
    /// chain rebuild). Cheap when nothing is open (loops zero times) or nothing changed (one native
    /// call per open slot, no allocation beyond the pulled bytes).</summary>
    private bool PollHostedStateChanges()
    {
        if (_layer is null || SharedHostedService is null || Params is null || _openedHostedSlots.Count == 0)
            return false;

        bool changed = false;
        foreach (var slot in _openedHostedSlots)
        {
            var instance = SharedHostedService.TryGetLiveInstance(_layer.LayerId, slot.Stage);
            if (instance is null) continue;

            var newState = SharedHostedService.PullLiveState(instance);
            var previous = _lastPolledHostedState.GetValueOrDefault(slot);
            if (previous is not null && newState.AsSpan().SequenceEqual(previous)) continue;

            slot.SetHostedState(Params, newState);
            _lastPolledHostedState[slot] = newState;
            changed = true;
        }
        return changed;
    }

    // ----- Melodyne slot (v8 redesign: styled the same as the FX slots above -- a plain enable
    // checkbox + name, no separate mode picker). PitchBackendSelection.NativeAutomatic stays a
    // valid enum value and MixEngine still handles it -- it's just never reachable from this UI,
    // per explicit user direction to keep the native fallback in code but not show it. -----

    public bool MelodyneEnabled
    {
        get => Params?.PitchBackend == PitchBackendSelection.Manual2A;
        set
        {
            if (Params is null) return;
            Params.PitchBackend = value ? PitchBackendSelection.Manual2A : PitchBackendSelection.None;
            OnPropertyChanged(nameof(MelodyneEnabled));
            LiveParamChanged?.Invoke();
        }
    }

    /// <summary>Bug audit A1: opens Melodyne's own editor GUI for this layer's persistent ARA
    /// session (the same live, already-analyzed session MixEngine's pitch stage renders through --
    /// not a disposable stand-in). Returns false if that session doesn't exist yet, which happens
    /// when the layer's chain has never been built with Manual2A selected (e.g. Play hasn't run
    /// since switching the dropdown to "Melodyne manual") -- the caller should tell the user to
    /// play the layer once first rather than treating this as an error.</summary>
    public bool OpenMelodyneEditor()
    {
        if (_layer is null || SharedHostedService is null) return false;
        return SharedHostedService.ShowAraEditor(_layer.LayerId, $"Melodyne — {DisplayName}");
    }

    /// <summary>Bug audit A5 ("stale correction cache"): a real change in this layer's ARA archive
    /// bumps the edit generation MixEngine folds into its Manual2A cache key, so the next rebuild
    /// re-renders through Melodyne instead of replaying stale output.</summary>
    private bool PollMelodyneStateChanged()
    {
        if (_layer is null || SharedHostedService is null) return false;
        return SharedHostedService.PollAraStateChanged(_layer.LayerId);
    }

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
        OnPropertyChanged(nameof(CellColor));
        OnPropertyChanged(nameof(TrimStartMs)); OnPropertyChanged(nameof(TrimEndText));
        OnPropertyChanged(nameof(GainDb)); OnPropertyChanged(nameof(GainDisplay));
        OnPropertyChanged(nameof(Pan)); OnPropertyChanged(nameof(PanDisplay));
        OnPropertyChanged(nameof(LowShelfGainDb)); OnPropertyChanged(nameof(LowEqDisplay));
        OnPropertyChanged(nameof(MidBellGainDb)); OnPropertyChanged(nameof(MidEqDisplay));
        OnPropertyChanged(nameof(HighShelfGainDb)); OnPropertyChanged(nameof(HighEqDisplay));
        OnPropertyChanged(nameof(NoiseGateEnabled));
        OnPropertyChanged(nameof(NoiseGateThresholdDb)); OnPropertyChanged(nameof(GateDisplay));
        OnPropertyChanged(nameof(NoiseGateReleaseMs)); OnPropertyChanged(nameof(GateReleaseDisplay));
        OnPropertyChanged(nameof(EqEnabled));
        OnPropertyChanged(nameof(CompressorEnabled));
        OnPropertyChanged(nameof(CompressorThresholdDb)); OnPropertyChanged(nameof(CompressorThresholdDisplay));
        OnPropertyChanged(nameof(CompressorRatio)); OnPropertyChanged(nameof(CompressorRatioDisplay));
        OnPropertyChanged(nameof(LimiterEnabled));
        OnPropertyChanged(nameof(LimiterCeilingDb)); OnPropertyChanged(nameof(LimiterCeilingDisplay));
        OnPropertyChanged(nameof(LimiterGainDb)); OnPropertyChanged(nameof(LimiterGainDisplay));
        OnPropertyChanged(nameof(ReverbEnabled)); OnPropertyChanged(nameof(IsReverbHosted));
        OnPropertyChanged(nameof(IsEqHosted)); OnPropertyChanged(nameof(IsNoiseGateHosted));
        OnPropertyChanged(nameof(IsCompressorHosted)); OnPropertyChanged(nameof(IsLimiterHosted));
        OnPropertyChanged(nameof(Mute)); OnPropertyChanged(nameof(Solo));
        OnPropertyChanged(nameof(MelodyneEnabled));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
