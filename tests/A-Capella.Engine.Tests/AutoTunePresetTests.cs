using A_Capella.Engine.Host;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;
using Xunit;

namespace A_Capella.Engine.Tests;

[Collection("JuceHosting")]
public class AutoTunePresetTests
{
    [Fact]
    public void PresetList_HasChromaticStylesAndEveryKeyInMajorAndMinor()
    {
        var all = AutoTunePresets.Categories;
        Assert.Equal(new[] { "Chromatic", "Major", "Minor" }, all.Select(c => c.Key));
        Assert.Equal(4, all[0].Presets.Count);
        Assert.Equal(12, all[1].Presets.Count);
        Assert.All(all.SelectMany(c => c.Presets), p => Assert.True(PresetLibraries.Exists(p)));
    }

    [Fact]
    public void OnTheMac_PitchIsTheFirstSlot_ElsewhereItIsAbsent()
    {
        Assert.Equal(OperatingSystem.IsMacOS(), FxSlots.All.Contains(FxSlots.Pitch));
        if (OperatingSystem.IsMacOS()) Assert.Same(FxSlots.Pitch, FxSlots.All[0]);
    }

    [Fact]
    public void WhenInstalled_EveryPresetLoadsSetsItsParameters_AndProcessesAudio()
    {
        if (!OperatingSystem.IsMacOS()
            || !File.Exists(Path.Combine(HostedPluginCatalog.KnownPluginPaths[HostedPluginCatalog.AutoTunePluginLabel], "Contents", "Info.plist")))
            return;   // Auto-Tune is Mac-only and must be installed

        using var service = new HostedPluginService();
        // 450 Hz sits between A4 and A#4, so every preset has something to correct.
        int n = 44100 * 2;
        var l = new float[n]; var r = new float[n]; var outL = new float[n]; var outR = new float[n];
        for (int i = 0; i < n; i++) l[i] = r[i] = 0.3f * MathF.Sin(2 * MathF.PI * 450f * i / 44100f);

        float[] Run(PresetRef preset)
        {
            var instance = service.GetOrCreatePresetInstance(1, "Pitch", HostedPluginCatalog.AutoTunePluginLabel, preset);
            Assert.NotNull(instance);
            service.Reset(instance!);
            var o = new float[n];
            var bl = new float[512]; var br = new float[512]; var ol = new float[512]; var or = new float[512];
            for (int off = 0; off + 512 <= n; off += 512)
            {
                Array.Copy(l, off, bl, 0, 512); Array.Copy(r, off, br, 0, 512);
                instance!.ProcessBlock(bl, br, ol, or, 512);
                Array.Copy(ol, 0, o, off, 512);
            }
            return o;
        }

        foreach (var preset in AutoTunePresets.Categories.SelectMany(c => c.Presets)) Run(preset);

        // Hard Tune must pull 450 Hz onto a semitone: measure the pitch over the last second.
        var hard = Run(AutoTunePresets.Categories[0].Presets[3]);
        int crossings = 0;
        for (int i = n - 44100 + 1; i < n; i++) if (hard[i - 1] < 0 && hard[i] >= 0) crossings++;
        Assert.InRange(crossings, 438, 442);   // ~440 Hz (A4), not the original 450

        var chrom = service.GetOrCreatePresetInstance(1, "Pitch", HostedPluginCatalog.AutoTunePluginLabel, AutoTunePresets.Categories[0].Presets[3])!;
        Assert.True(chrom.LatencySamples > 0);
        string Param(string name) { for (int i = 0; i < chrom.ParameterCount; i++) if (chrom.GetParameterName(i) == name) return chrom.GetParameterText(i, chrom.GetParameterValue(i)); return "?"; }
        Assert.Equal("0", Param("Retune Speed"));
        Assert.Equal("Chromatic", Param("Scale"));
        Assert.Equal("HQ", Param("Latency Mode"));
        service.ReleaseAll();
    }
}
