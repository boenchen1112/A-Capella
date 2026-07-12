using System.ComponentModel;
using System.Runtime.CompilerServices;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;

namespace Acapella.App.ViewModels;

/// <summary>
/// One row in the track panel's accordion list (see UI_Design_Spec.md). Wraps a LayerModel once
/// a source is attached; Layer is null for a freshly added row still in the Record/Upload choice
/// state. Mix-parameter properties write straight through to the wrapped LayerModel and notify
/// AudioParamChanged so the caller can rebuild an in-progress audio preview -- they deliberately
/// do NOT trigger the visual composite refresh, since none of them change what a frame looks
/// like (see MainWindow's RefreshCompositePreview call sites instead).
/// </summary>
public class LayerRowViewModel : INotifyPropertyChanged
{
    private LayerModel? _layer;
    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? AudioParamChanged;

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
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(SourceStateLabel));
            RefreshMixDisplayProperties();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
    }

    public bool HasSource => _layer is not null;

    public string IconGlyph => _layer?.Kind switch
    {
        LayerKind.RecordedAV => "\U0001F3A4",   // mic
        LayerKind.UploadedVideo => "⬆",     // up arrow
        LayerKind.UploadedAudioOnly => "⬆",
        _ => "+",
    };

    public string DisplayName => $"Layer {SlotNumber}";

    public string SourceStateLabel => _layer?.Kind switch
    {
        LayerKind.RecordedAV => "recorded",
        LayerKind.UploadedVideo => "uploaded",
        LayerKind.UploadedAudioOnly => "uploaded (audio)",
        _ => "empty",
    };

    private LayerMixParameters? Params => _layer?.MixParameters;

    public float GainDb
    {
        get => Params?.GainDb ?? 0f;
        set { if (Params is null) return; Params.GainDb = value; OnPropertyChanged(nameof(GainDb)); OnPropertyChanged(nameof(GainDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string GainDisplay => $"{GainDb:F1} dB";

    public float Pan
    {
        get => Params?.Pan ?? 0f;
        set { if (Params is null) return; Params.Pan = value; OnPropertyChanged(nameof(Pan)); OnPropertyChanged(nameof(PanDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string PanDisplay => $"{Pan:F2}";

    public float LowShelfGainDb
    {
        get => Params?.LowShelfGainDb ?? 0f;
        set { if (Params is null) return; Params.LowShelfGainDb = value; OnPropertyChanged(nameof(LowShelfGainDb)); OnPropertyChanged(nameof(LowEqDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string LowEqDisplay => $"{LowShelfGainDb:F1} dB";

    public float MidBellGainDb
    {
        get => Params?.MidBellGainDb ?? 0f;
        set { if (Params is null) return; Params.MidBellGainDb = value; OnPropertyChanged(nameof(MidBellGainDb)); OnPropertyChanged(nameof(MidEqDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string MidEqDisplay => $"{MidBellGainDb:F1} dB";

    public float HighShelfGainDb
    {
        get => Params?.HighShelfGainDb ?? 0f;
        set { if (Params is null) return; Params.HighShelfGainDb = value; OnPropertyChanged(nameof(HighShelfGainDb)); OnPropertyChanged(nameof(HighEqDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string HighEqDisplay => $"{HighShelfGainDb:F1} dB";

    public float NoiseGateThresholdDb
    {
        get => Params?.NoiseGateThresholdDb ?? -60f;
        set { if (Params is null) return; Params.NoiseGateThresholdDb = value; OnPropertyChanged(nameof(NoiseGateThresholdDb)); OnPropertyChanged(nameof(GateDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string GateDisplay => $"{NoiseGateThresholdDb:F1} dB";

    public bool Mute
    {
        get => Params?.Mute ?? false;
        set { if (Params is null) return; Params.Mute = value; OnPropertyChanged(nameof(Mute)); AudioParamChanged?.Invoke(); }
    }

    public bool Solo
    {
        get => Params?.Solo ?? false;
        set { if (Params is null) return; Params.Solo = value; OnPropertyChanged(nameof(Solo)); AudioParamChanged?.Invoke(); }
    }

    public int PitchBackendIndex
    {
        get => (int)(Params?.PitchBackend ?? PitchBackendSelection.None);
        set
        {
            if (Params is null) return;
            Params.PitchBackend = (PitchBackendSelection)value;
            OnPropertyChanged(nameof(PitchBackendIndex));
            OnPropertyChanged(nameof(ShowMelodyneButton));
            AudioParamChanged?.Invoke();
        }
    }

    public bool ShowMelodyneButton => Params?.PitchBackend == PitchBackendSelection.Manual2A;

    public double ManualOffsetMs
    {
        get => _layer?.ManualOffsetMs ?? 0;
        set { if (_layer is null) return; _layer.ManualOffsetMs = value; OnPropertyChanged(nameof(ManualOffsetMs)); OnPropertyChanged(nameof(OffsetDisplay)); AudioParamChanged?.Invoke(); }
    }
    public string OffsetDisplay => $"{ManualOffsetMs:F0} ms";

    /// <summary>Refreshes every display-bound property after Layer is swapped wholesale (e.g. a
    /// recording/upload just attached, or a project was reopened) so bound controls pick up the
    /// new source's stored values instead of stale defaults.</summary>
    private void RefreshMixDisplayProperties()
    {
        OnPropertyChanged(nameof(GainDb)); OnPropertyChanged(nameof(GainDisplay));
        OnPropertyChanged(nameof(Pan)); OnPropertyChanged(nameof(PanDisplay));
        OnPropertyChanged(nameof(LowShelfGainDb)); OnPropertyChanged(nameof(LowEqDisplay));
        OnPropertyChanged(nameof(MidBellGainDb)); OnPropertyChanged(nameof(MidEqDisplay));
        OnPropertyChanged(nameof(HighShelfGainDb)); OnPropertyChanged(nameof(HighEqDisplay));
        OnPropertyChanged(nameof(NoiseGateThresholdDb)); OnPropertyChanged(nameof(GateDisplay));
        OnPropertyChanged(nameof(Mute)); OnPropertyChanged(nameof(Solo));
        OnPropertyChanged(nameof(PitchBackendIndex)); OnPropertyChanged(nameof(ShowMelodyneButton));
        OnPropertyChanged(nameof(ManualOffsetMs)); OnPropertyChanged(nameof(OffsetDisplay));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
