using Acapella.Engine.Capture;

namespace Acapella.Engine.Tests.Capture;

/// <summary>
/// Bug audit #6: the old rule -- Path.Combine(dir, $"layer{Count}.mkv") -- returns a name that is
/// only unique within the current project's layer list, not within the shared media/ directory.
/// These tests pin the replacement's "never returns an existing path" guarantee.
/// </summary>
public class RecordingPathAllocatorTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            if (Directory.Exists(d))
                Directory.Delete(d, recursive: true);
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"acapella-recording-allocator-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void Allocate_NeverReturnsALegacyPositionalRecording()
    {
        string dir = NewTempDir();
        for (int i = 0; i < 4; i++)
            File.WriteAllBytes(Path.Combine(dir, $"layer{i}.mkv"), Array.Empty<byte>());

        string result = RecordingPathAllocator.Allocate(dir);

        Assert.False(File.Exists(result));
        Assert.Equal(dir, Path.GetDirectoryName(result));
        Assert.EndsWith(".mkv", result);
    }

    [Fact]
    public void Allocate_SameSecondCollision_AppendsASuffix()
    {
        var now = new DateTime(2026, 9, 23, 22, 15, 30);
        var existing = new HashSet<string>
        {
            Path.Combine("dir", "take-20260923-221530.mkv"),
            Path.Combine("dir", "take-20260923-221530-2.mkv"),
        };

        string result = RecordingPathAllocator.Allocate("dir", now, existing.Contains);

        Assert.Equal(Path.Combine("dir", "take-20260923-221530-3.mkv"), result);
    }

    [Fact]
    public void Allocate_NoCollision_UsesTheBareTimestampName()
    {
        var now = new DateTime(2026, 9, 23, 22, 15, 30);

        string result = RecordingPathAllocator.Allocate("dir", now, _ => false);

        Assert.Equal(Path.Combine("dir", "take-20260923-221530.mkv"), result);
    }

    [Fact]
    public void Allocate_ConsecutiveTakes_NeverShareAPath()
    {
        string dir = NewTempDir();
        var fixedNow = new DateTime(2026, 9, 23, 22, 15, 30);

        string result1 = RecordingPathAllocator.Allocate(dir, fixedNow, File.Exists);
        File.WriteAllBytes(result1, Array.Empty<byte>());
        string result2 = RecordingPathAllocator.Allocate(dir, fixedNow, File.Exists);

        Assert.NotEqual(result1, result2);
        Assert.False(File.Exists(result2));
    }
}
