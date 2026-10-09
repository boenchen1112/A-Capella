namespace Acapella.Engine.Composite;

/// <summary>How a video is scaled into a cell whose shape differs from the video's.</summary>
public enum CellFit
{
    /// <summary>Whole picture visible; the leftover space is padded black (letterbox/pillarbox).</summary>
    Fit,
    /// <summary>Picture covers the whole cell; the overflow is cropped, centered.</summary>
    Fill,
}

/// <summary>The ffmpeg -vf chain that turns a source video into raw cell-sized frames.</summary>
public static class CellVideoFilter
{
    public static string Build(int width, int height, int fps, CellFit fit, bool mirror = false)
    {
        string scaleAndShape = fit == CellFit.Fill
            ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}"
            : $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black";
        return $"{(mirror ? "hflip," : "")}{scaleAndShape},fps={fps}";
    }
}
