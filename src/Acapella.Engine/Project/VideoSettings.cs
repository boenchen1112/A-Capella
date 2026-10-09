using Acapella.Engine.Composite;

namespace Acapella.Engine.Project;

/// <summary>Project-level look of the composited video: which layout the layers' cells belong to.
/// Immutable; change it with <c>with</c> and assign it back to ProjectSession.Video.</summary>
public sealed record VideoSettings
{
    public string LayoutId { get; init; } = GridLayoutCatalog.LegacyId;

    public GridLayout Layout => GridLayoutCatalog.Get(LayoutId);
}
