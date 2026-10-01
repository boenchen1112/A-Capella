using System.Diagnostics;
using Acapella.Engine.Mix;

namespace Acapella.Engine.Tests.Mix;

public class AudioDecodeCacheTests
{
    /// <summary>Regression test for audit B2: a second decode request for the same
    /// (path, mtime, sample rate) must reuse the cached array rather than re-running ffmpeg.
    /// Reference equality is the cheapest reliable signal that no re-decode happened.</summary>
    [Fact]
    public void GetOrDecode_SecondCallForUnchangedFile_ReturnsSameCachedArray()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-decode-cache-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string audio = Path.Combine(tempDir, "tone.wav");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            string[] args = { "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1", audio };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var process = Process.Start(psi)!)
            {
                process.StandardError.ReadToEnd();
                process.WaitForExit();
            }

            float[] first = AudioDecodeCache.GetOrDecode(audio, 44100);
            float[] second = AudioDecodeCache.GetOrDecode(audio, 44100);

            Assert.Same(first, second);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
