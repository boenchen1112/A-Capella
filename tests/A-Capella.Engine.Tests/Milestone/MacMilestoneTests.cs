using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using A_Capella.Engine.Export;
using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Persistence;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Project;
using A_Capella.Engine.Sync;
using Xunit.Abstractions;

namespace A_Capella.Engine.Tests.Milestone;

/// <summary>The automatic checks for a first macOS release: presets, export peak, Windows project files, offset stability.</summary>
[Collection("JuceHosting")]
public class MacMilestoneTests
{
    private readonly ITestOutputHelper _out;
    public MacMilestoneTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Plugins =
        { "FabFilter Pro-C 3", "FabFilter Pro-Q 4", "FabFilter Pro-DS", "FabFilter Pro-R 2", "FabFilter Pro-L 2" };

    private static bool Available(string label) => new HostedPluginAvailability().IsAvailable(label);

    // ----- 1. preset library lists every factory preset -----

    [Fact]
    public void PresetLibrary_ListsEveryFactoryPresetFile_ForEachInstalledPlugin()
    {
        var library = FabFilterPresetLibrary.Default;
        int checkedPlugins = 0;
        foreach (var label in Plugins)
        {
            string folder = Path.Combine(library.RootFolder, label.Replace("FabFilter ", ""));
            if (!Available(label) || !Directory.Exists(folder)) { _out.WriteLine($"SKIPPED: {label} not installed."); continue; }

            int onDisk = Directory.EnumerateFiles(folder, "*.ffp", SearchOption.AllDirectories).Count();
            var listed = library.GetCategories(label).SelectMany(c => c.Presets).ToList();
            _out.WriteLine($"{label}: {listed.Count} listed, {onDisk} on disk");
            Assert.True(onDisk > 0);
            Assert.Equal(onDisk, listed.Count);
            Assert.All(listed, p => Assert.True(library.Exists(p)));
            checkedPlugins++;
        }
        _out.WriteLine($"{checkedPlugins} plugins checked");
    }

    // ----- 2. three-layer export with presets and a master limiter -----

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {string.Join(' ', args)}");
    }

    private static string Capture(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o + err.Result;
    }

    private static PresetRef? Pick(FabFilterPresetLibrary lib, string label) =>
        lib.GetCategories(label).SelectMany(c => c.Presets).FirstOrDefault(p => p.Name != FabFilterPresetLibrary.DefaultSettingName);

    [Fact]
    public void ThreeLayerExport_WithPresetsAndMasterLimiter_IsAValidMp4_WithAudioUnderTheCeiling() =>
        ThreeLayerExport(reverbLayer: false);

    [Fact]
    public void ThreeLayerExport_WithAReverbLayer_StaysUnderTheCeiling() =>
        ThreeLayerExport(reverbLayer: true);

    private void ThreeLayerExport(bool reverbLayer)
    {
        var library = FabFilterPresetLibrary.Default;
        var availability = new HostedPluginAvailability();
        var comp = Pick(library, "FabFilter Pro-C 3");
        var eq = Pick(library, "FabFilter Pro-Q 4");
        var reverb = Pick(library, "FabFilter Pro-R 2");
        var limiter = ProjectSession.DefaultMasterLimiter;
        if (comp is null || eq is null || reverb is null || !library.Exists(limiter) || !Plugins.All(availability.IsAvailable))
        { _out.WriteLine("SKIPPED: FabFilter plugins or presets not installed."); return; }

        string dir = Path.Combine(Path.GetTempPath(), "a-capella-milestone-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var layers = new LayerCollection();
            var colors = new[] { "red", "green", "blue" };
            var freqs = new[] { 220, 330, 440 };
            for (int i = 0; i < 3; i++)
            {
                string clip = Path.Combine(dir, $"layer{i}.mp4");
                // Loud on purpose: three near-full-scale tones sum well past 0 dBFS before the limiter.
                Run("ffmpeg", "-y", "-f", "lavfi", "-i", $"color=c={colors[i]}:s=64x64:r=10:d=2",
                    "-f", "lavfi", "-i", $"sine=frequency={freqs[i]}:sample_rate=44100:duration=2",
                    "-af", "volume=7", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", clip);
                var layer = layers.Add(LayerKind.RecordedAV, clip);
                if (i == 0) layer.MixParameters.CompressorPreset = comp;
                if (i == 1) layer.MixParameters.EqPreset = eq;
                if (i == 2 && reverbLayer) layer.MixParameters.ReverbPreset = reverb;
            }

            string output = Path.Combine(dir, "export.mp4");
            using var service = new HostedPluginService(availability, InlineHostedPluginDispatcher.Instance);
            var engine = new ExportEngine(hostedService: service);
            engine.Export(layers, output, width: 192, height: 192, fps: 10, sampleRate: 44100, masterLimiter: limiter);

            Assert.True(new FileInfo(output).Length > 0);
            string probe = Capture("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "default=noprint_wrappers=1", output);
            Assert.Contains("codec_name=h264", probe);
            Assert.Contains("codec_name=aac", probe);

            // Decode the exported audio and take the true sample peak.
            string raw = Path.Combine(dir, "out.f32");
            Run("ffmpeg", "-y", "-i", output, "-vn", "-f", "f32le", "-ac", "2", "-ar", "44100", raw);
            var bytes = File.ReadAllBytes(raw);
            float peak = 0;
            int peakAt = 0;
            for (int i = 0; i + 4 <= bytes.Length; i += 4) { float v = Math.Abs(BitConverter.ToSingle(bytes, i)); if (v > peak) { peak = v; peakAt = i / 8; } }
            _out.WriteLine($"peak at frame {peakAt} ({peakAt / 44100.0:F3}s)");
            double peakDb = 20 * Math.Log10(peak);
            _out.WriteLine($"export peak {peak:F4} ({peakDb:F2} dBFS), limiter ceiling -0.10 dBFS, {bytes.Length / 4} samples");
            Assert.True(bytes.Length > 44100 * 4);

            // Baseline: the same layers with no presets and no limiter, so the ceiling test is known to be meaningful.
            foreach (var l in layers.Layers) { l.MixParameters.CompressorPreset = null; l.MixParameters.EqPreset = null; l.MixParameters.ReverbPreset = null; }
            string plain = Path.Combine(dir, "plain.mp4"), plainRaw = Path.Combine(dir, "plain.f32");
            engine.Export(layers, plain, width: 192, height: 192, fps: 10, sampleRate: 44100);
            Run("ffmpeg", "-y", "-i", plain, "-vn", "-f", "f32le", "-ac", "2", "-ar", "44100", plainRaw);
            var pb = File.ReadAllBytes(plainRaw);
            float plainPeak = 0;
            for (int i = 0; i + 4 <= pb.Length; i += 4) plainPeak = Math.Max(plainPeak, Math.Abs(BitConverter.ToSingle(pb, i)));
            _out.WriteLine($"baseline peak without presets or limiter {plainPeak:F4}");
            Assert.True(plainPeak > 0.9, "the baseline must be hot enough to need limiting");
            // The default master limiter (Pro-L 2 "Safe") has a -0.1 dBFS ceiling; the fixed -0.3 dBFS
            // guard only applies when that plugin is unavailable (checked in MasterLimiterTests).
            // AAC rounding can add about 0.1 dB, so the exported file must stay below full scale.
            Assert.True(peak < 1.0f, $"peak {peakDb:F2} dBFS is over the limiter ceiling");
            Assert.True(peak > plainPeak * 0.9, "limiter output should still be loud, not silent");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ----- 3. a project saved by the Windows build -----

    private const string WindowsProject = """
    {
      "LayoutId": "2x2",
      "MetronomeBpm": 96,
      "MetronomeBeatsPerBar": 3,
      "MetronomeBeatUnit": 4,
      "MasterVolumeDb": -1.5,
      "LatencyOffsetMsUsed": 143.2,
      "Layers": [
        {
          "LayerId": 1, "CellIndex": 0, "Mirror": false, "Kind": "RecordedAV",
          "SourcePath": "C:\\Users\\singer\\Music\\take1.mkv", "Name": "Lead",
          "CalibratedOffsetMs": 143.2, "ManualOffsetMs": 0, "TrimStartMs": 0, "TrimEndMs": null,
          "AraArchiveKey": null,
          "MixParameters": {
            "GainDb": -3, "Mute": false, "Solo": false, "Pan": 0.25,
            "CompressorPreset": { "Plugin": "FabFilter Pro-C 3", "Category": "@@C_CAT@@", "Name": "@@C_NAME@@" },
            "EqPreset": null, "DeEsserPreset": null, "ReverbPreset": null,
            "PitchBackend": "None", "Frozen": false
          }
        },
        {
          "LayerId": 2, "CellIndex": 1, "Mirror": true, "Kind": "UploadedAudioOnly",
          "SourcePath": "D:\\Takes\\harmony.wav", "Name": null,
          "CalibratedOffsetMs": 0, "ManualOffsetMs": -12.5, "TrimStartMs": 250, "TrimEndMs": 9000,
          "AraArchiveKey": null,
          "MixParameters": {
            "GainDb": 0, "Mute": false, "Solo": false, "Pan": -0.25,
            "CompressorPreset": null,
            "EqPreset": { "Plugin": "FabFilter Pro-Q 4", "Category": "@@E_CAT@@", "Name": "@@E_NAME@@" },
            "DeEsserPreset": null, "ReverbPreset": null,
            "PitchBackend": "None", "Frozen": false
          }
        }
      ]
    }
    """;

    [Fact]
    public void ProjectSavedOnWindows_OpensRoundTripsAndResolvesPresetsByName()
    {
        var library = FabFilterPresetLibrary.Default;
        var comp = Pick(library, "FabFilter Pro-C 3");
        var eq = Pick(library, "FabFilter Pro-Q 4");
        bool havePresets = comp is not null && eq is not null;
        comp ??= new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal");
        eq ??= new PresetRef("FabFilter Pro-Q 4", "", "Bright");

        string json = WindowsProject
            .Replace("@@C_CAT@@", comp.Category).Replace("@@C_NAME@@", comp.Name)
            .Replace("@@E_CAT@@", eq.Category).Replace("@@E_NAME@@", eq.Name);
        string dir = Path.Combine(Path.GetTempPath(), "a-capella-winproj-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            string winFile = Path.Combine(dir, "win.json");
            File.WriteAllText(winFile, json);
            Assert.DoesNotContain("PitchPreset", json);

            var svc = new ProjectPersistenceService();
            var dto = svc.LoadFromFile(winFile);
            var (layers, bpm, offset, vol, _, sig) = svc.FromDto(dto);

            Assert.Equal(2, layers.Layers.Count);
            Assert.Equal(96, bpm);
            Assert.Equal(143.2, offset);
            Assert.Equal(-1.5f, vol);
            Assert.Equal(3, sig.BeatsPerBar);
            Assert.Equal(@"C:\Users\singer\Music\take1.mkv", layers.Layers[0].SourcePath);
            Assert.Equal(@"D:\Takes\harmony.wav", layers.Layers[1].SourcePath);
            Assert.Null(layers.Layers[0].MixParameters.PitchPreset);
            Assert.Equal(comp, layers.Layers[0].MixParameters.CompressorPreset);
            Assert.Equal(eq, layers.Layers[1].MixParameters.EqPreset);
            if (havePresets)
            {
                Assert.True(library.Exists(layers.Layers[0].MixParameters.CompressorPreset!));
                Assert.True(library.Exists(layers.Layers[1].MixParameters.EqPreset!));
            }

            // Save -> load -> save gives the same JSON.
            string a = Path.Combine(dir, "a.json"), b = Path.Combine(dir, "b.json");
            svc.SaveToFile(svc.ToDto(layers, bpm, offset, vol, dto.SongInMs, dto.SongOutMs, null, sig, dto.MasterLimiterPreset), a);
            var (l2, bpm2, off2, vol2, _, sig2) = svc.FromDto(svc.LoadFromFile(a));
            svc.SaveToFile(svc.ToDto(l2, bpm2, off2, vol2, null, null, null, sig2, null), b);
            Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));

            // Every value the Windows file carried survives into the saved file.
            var original = JsonNode.Parse(json)!;
            var saved = JsonNode.Parse(File.ReadAllText(a))!;
            foreach (var key in new[] { "LayoutId", "MetronomeBpm", "MasterVolumeDb", "LatencyOffsetMsUsed" })
                Assert.Equal(original[key]!.ToJsonString(), saved[key]!.ToJsonString());
            for (int i = 0; i < 2; i++)
            {
                var o = original["Layers"]![i]!.AsObject();
                var s = saved["Layers"]![i]!.AsObject();
                foreach (var kv in o.Where(kv => kv.Key != "MixParameters"))
                    Assert.Equal(kv.Value?.ToJsonString(), s[kv.Key]?.ToJsonString());
                foreach (var kv in o["MixParameters"]!.AsObject())
                    Assert.Equal(kv.Value?.ToJsonString(), s["MixParameters"]![kv.Key]?.ToJsonString());
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    // ----- 4. recording offset is stable across takes -----

    [Fact]
    public void ClickTrackOffset_IsStableAcrossFiveTakes_ThroughASyntheticLoopback()
    {
        const int sr = 44100;
        const double latencyMs = 87.0;            // fixed loopback delay
        int delaySamples = (int)Math.Round(latencyMs * sr / 1000);
        var click = ToneGenerator.GenerateClick(sr);
        var offsets = new List<double>();
        var rng = new Random(7);

        for (int take = 0; take < 5; take++)
        {
            // The captured stream starts at a different point each take; the click plays a fixed latency later.
            int preRoll = 1000 + take * 733;
            var captured = new float[sr * 2];
            for (int i = 0; i < captured.Length; i++) captured[i] = (float)((rng.NextDouble() - 0.5) * 0.02);
            for (int i = 0; i < click.Length; i++) captured[preRoll + delaySamples + i] += click[i];

            int lag = CrossCorrelator.FindOffsetSamples(click, captured, sr);
            offsets.Add((lag - preRoll) * 1000.0 / sr);
        }

        double spread = offsets.Max() - offsets.Min();
        _out.WriteLine($"offsets ms: {string.Join(", ", offsets.Select(o => o.ToString("F3", CultureInfo.InvariantCulture)))}; spread {spread:F3} ms");
        Assert.True(spread < 1.0, $"spread {spread} ms");
        Assert.All(offsets, o => Assert.InRange(o, latencyMs - 1, latencyMs + 1));
    }
}
