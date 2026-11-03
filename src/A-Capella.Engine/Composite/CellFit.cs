namespace A_Capella.Engine.Composite;

/// <summary>How a video is scaled into a cell whose shape differs from the video's.</summary>
public enum CellFit
{
    /// <summary>Whole picture visible; the leftover space is padded black (letterbox/pillarbox).</summary>
    Fit,
    /// <summary>Picture covers the whole cell; overflow is cropped, centered sideways and weighted toward the top so heads stay in view.</summary>
    Fill,
}

/// <summary>The ffmpeg -vf chain that turns a source video into raw cell-sized frames.</summary>
public static class CellVideoFilter
{
    public static string Build(int width, int height, int fps, CellFit fit, bool mirror = false)
    {
        string scaleAndShape = fit == CellFit.Fill
            ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}:(iw-ow)/2:(ih-oh)*0.3"
            : $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black";
        return $"{(mirror ? "hflip," : "")}{scaleAndShape},fps={fps}";
    }
}
