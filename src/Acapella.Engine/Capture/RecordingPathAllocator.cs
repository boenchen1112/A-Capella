namespace Acapella.Engine.Capture;

/// <summary>Bug audit #6: picks the output path for a new capture so that it can never overwrite an
/// existing file. media/ is shared by every project and every app session, and ffmpeg runs with -y,
/// so the old positional "layer{Count}.mkv" name silently destroyed earlier projects' takes.
/// Pure (clock and file-existence are injected) so the never-collide rule is unit-testable.</summary>
public static class RecordingPathAllocator
{
    public static string Allocate(string mediaDir, DateTime now, Func<string, bool> fileExists)
    {
        string stem = $"take-{now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(mediaDir, stem + ".mkv");
        for (int n = 2; fileExists(path); n++)
            path = Path.Combine(mediaDir, $"{stem}-{n}.mkv");
        return path;
    }

    /// <summary>Production overload: local wall clock, real filesystem.</summary>
    public static string Allocate(string mediaDir) => Allocate(mediaDir, DateTime.Now, File.Exists);
}
