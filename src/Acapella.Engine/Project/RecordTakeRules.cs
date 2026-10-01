namespace Acapella.Engine.Project;

/// <summary>Pure rules for starting a take in the Recording setup dialog (retake spec D2).
/// retakeLayerId == null means "record a new layer" and must reproduce the pre-retake behaviour
/// exactly: same cap check, guide = every layer in collection order.</summary>
public static class RecordTakeRules
{
    /// <summary>A new take is refused at the cap; a retake replaces an existing layer's source,
    /// so it never adds one and is never blocked by the cap.</summary>
    public static bool IsBlockedByLayerCap(int layerCount, int? retakeLayerId) =>
        retakeLayerId is null && layerCount >= LayerCollection.MaxLayers;

    /// <summary>The layers the singer hears as the guide: every layer for a new take; every layer
    /// EXCEPT the one being replaced for a retake. Excluded from the input list, not muted --
    /// muting a soloed target would keep anySolo true and silence the whole guide (spec (b)).
    /// The caller must use this same list for BOTH "is there a guide?" and the guide mix (spec (a)).</summary>
    public static IReadOnlyList<LayerModel> GuideLayers(IEnumerable<LayerModel> layers, int? retakeLayerId)
    {
        if (retakeLayerId is not int target)
            return layers.ToList();
        return layers.Where(l => l.LayerId != target).ToList();
    }
}
