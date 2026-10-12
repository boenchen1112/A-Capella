using System.ComponentModel;
using System.Runtime.CompilerServices;
using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;

namespace A_Capella.App.ViewModels;

/// <summary>One entry in an effect's preset dropdown: a FabFilter factory preset, or "Off" (Ref null).</summary>
public sealed class PresetChoice
{
    public PresetChoice(PresetRef? reference, string group, string label)
    {
        Ref = reference;
        Group = group;
        Label = label;
    }

    public PresetRef? Ref { get; }

    /// <summary>The FabFilter category the preset sits in, which the dropdown groups by.</summary>
    public string Group { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

/// <summary>
/// One effect (compressor, EQ, de-esser or reverb) of one layer, as the channel panel shows it: a
/// single dropdown of that plugin's factory presets by category, plus "Off". There is
/// nothing else to adjust. Writes straight through to the layer's mix parameters.
/// </summary>
public sealed class EffectSlotViewModel : INotifyPropertyChanged
{
    public const string OffGroup = "";

    private static readonly Dictionary<string, IReadOnlyList<PresetChoice>> ChoiceCache = new();

    private readonly Func<LayerMixParameters?> _getParameters;
    private readonly Action<EffectSlotViewModel> _onChosen;

    public EffectSlotViewModel(FxSlot slot, Func<LayerMixParameters?> getParameters, Action<EffectSlotViewModel> onChosen)
    {
        Slot = slot;
        _getParameters = getParameters;
        _onChosen = onChosen;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FxSlot Slot { get; }
    public string Title => Slot.Title;
    public string PluginLabel => Slot.PluginLabel;

    /// <summary>"Off" followed by the plugin's presets; the picker lays them out by category.</summary>
    public IReadOnlyList<PresetChoice> Choices => AllChoices(Slot.PluginLabel);

    /// <summary>The entries for a plugin: Off first, then each category's presets, in FabFilter's order.</summary>
    public static IReadOnlyList<PresetChoice> AllChoices(string pluginLabel)
    {
        lock (ChoiceCache)
        {
            if (ChoiceCache.TryGetValue(pluginLabel, out var cached)) return cached;
            var list = new List<PresetChoice> { new(null, OffGroup, "Off") };
            foreach (var category in FabFilterPresetLibrary.Default.GetCategories(pluginLabel))
                foreach (var preset in category.Presets)
                    list.Add(new PresetChoice(preset, category.Title, preset.Name));
            ChoiceCache[pluginLabel] = list;
            return list;
        }
    }

    /// <summary>Forgets the cached lists so presets installed since are picked up on the next use.</summary>
    public static void ResetChoiceCache()
    {
        lock (ChoiceCache) ChoiceCache.Clear();
        FabFilterPresetLibrary.Default.Refresh();
    }

    private PresetRef? CurrentPreset => _getParameters() is { } p ? Slot.GetPreset(p) : null;

    public PresetChoice? SelectedChoice
    {
        get
        {
            var current = CurrentPreset;
            return AllChoices(Slot.PluginLabel).FirstOrDefault(c => Equals(c.Ref, current));
        }
        set
        {
            var parameters = _getParameters();
            if (parameters is null || value is null) return;
            if (Equals(Slot.GetPreset(parameters), value.Ref)) return;
            Slot.SetPreset(parameters, value.Ref);
            Refresh();
            _onChosen(this);
        }
    }

    /// <summary>What to tell the user when the effect can't do its job: the plugin isn't installed, the
    /// chosen preset's file has gone, or no presets were found. Empty when all is well.</summary>
    public string Status
    {
        get
        {
            if (_getParameters() is null) return "";
            if (LayerRowViewModel.SharedHostedService is { } service && !service.IsAvailable(Slot.PluginLabel))
                return $"{Slot.PluginLabel} not installed";
            var current = CurrentPreset;
            if (current is not null && !FabFilterPresetLibrary.Default.Exists(current))
                return $"Preset not found: {current.Name}";
            if (AllChoices(Slot.PluginLabel).Count == 1) return "No presets found";
            return "";
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>Re-reads the layer's parameters, after the layer changed or a package was applied.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
