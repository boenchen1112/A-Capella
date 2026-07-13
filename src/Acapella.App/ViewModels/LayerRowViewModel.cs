using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LiveParamChanged;

    public LayerRowViewModel(int slotNumber)
    {
        SlotNumber = slotNumber;
    }

    public int SlotNumber { get; }

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

    // ----- Melodyne subtab -----

    public int PitchBackendIndex
    {
        get => (int)(Params?.PitchBackend ?? PitchBackendSelection.None);
        set
        {
            if (Params is null) return;
            Params.PitchBackend = (PitchBackendSelection)value;
            OnPropertyChanged(nameof(PitchBackendIndex));
            OnPropertyChanged(nameof(ShowMelodyneButton));
            LiveParamChanged?.Invoke();
        }
    }

    public bool ShowMelodyneButton => Params?.PitchBackend == PitchBackendSelection.Manual2A;

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
        OnPropertyChanged(nameof(NoiseGateThresholdDb)); OnPropertyChanged(nameof(GateDisplay));
        OnPropertyChanged(nameof(NoiseGateReleaseMs)); OnPropertyChanged(nameof(GateReleaseDisplay));
        OnPropertyChanged(nameof(CompressorEnabled));
        OnPropertyChanged(nameof(CompressorThresholdDb)); OnPropertyChanged(nameof(CompressorThresholdDisplay));
        OnPropertyChanged(nameof(CompressorRatio)); OnPropertyChanged(nameof(CompressorRatioDisplay));
        OnPropertyChanged(nameof(LimiterEnabled));
        OnPropertyChanged(nameof(LimiterCeilingDb)); OnPropertyChanged(nameof(LimiterCeilingDisplay));
        OnPropertyChanged(nameof(LimiterGainDb)); OnPropertyChanged(nameof(LimiterGainDisplay));
        OnPropertyChanged(nameof(Mute)); OnPropertyChanged(nameof(Solo));
        OnPropertyChanged(nameof(PitchBackendIndex)); OnPropertyChanged(nameof(ShowMelodyneButton));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
