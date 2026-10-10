using System.Text;
using A_Capella.Engine.Host;
using A_Capella.Engine.Presets;
using Xunit.Abstractions;

namespace A_Capella.Engine.Tests.Presets;

[Collection("JuceHosting")]
public class FabFilterPresetTests
{
    private readonly ITestOutputHelper _out;
    public FabFilterPresetTests(ITestOutputHelper output) => _out = output;

    private static readonly string PresetRoot = FabFilterPresetLibrary.Default.RootFolder;

    public static IEnumerable<object[]> Plugins() => new[]
    {
        new object[] { "FabFilter Pro-C 3", "Pro-C 3" },
        new object[] { "FabFilter Pro-Q 4", "Pro-Q 4" },
        new object[] { "FabFilter Pro-DS", "Pro-DS" },
        new object[] { "FabFilter Pro-R 2", "Pro-R 2" },
        new object[] { "FabFilter Pro-L 2", "Pro-L 2" },
    };

    private static bool Installed(string label, string folder) =>
        HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths[label], out _)
        && File.Exists(Path.Combine(PresetRoot, folder, "Default Setting.ffp"));

    private static FabFilterPresetFile Load(string path) => FabFilterPresetFile.Parse(File.ReadAllBytes(path));

    // ----- parsing and listing (no plugin needed) -----

    [Fact]
    public void Parse_TextPreset_KeepsKeysInFileOrder()
    {
        var file = FabFilterPresetFile.Parse(Encoding.UTF8.GetBytes("[Preset]\nSignature=FPQ4\nVersion=1\n[Parameters]\nBand 1 Gain=2.5\nBand 1 Frequency=3.25\n"));

        Assert.False(file.IsBinary);
        Assert.Equal(new[] { "Band 1 Gain", "Band 1 Frequency" }, file.Named.Select(kv => kv.Key));
        Assert.Equal(new[] { 2.5f, 3.25f }, file.Named.Select(kv => kv.Value));
    }

    [Fact]
    public void Parse_BinaryPreset_ReadsSignatureVersionAndFloats()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("FPRr"));
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(BitConverter.GetBytes(2));
        bytes.AddRange(BitConverter.GetBytes(0.5f));
        bytes.AddRange(BitConverter.GetBytes(-1.5f));

        var file = FabFilterPresetFile.Parse(bytes.ToArray());

        Assert.True(file.IsBinary);
        Assert.Equal("FPRr", file.Signature);
        Assert.Equal(new[] { 0.5f, -1.5f }, file.Binary);
    }

    [Fact]
    public void Library_GroupsPresetsByCategory_WithReadableTitles()
    {
        string root = Path.Combine(Path.GetTempPath(), "a-capella-presets-" + Guid.NewGuid());
        try
        {
            string dir = Path.Combine(root, "Pro-C 3");
            Directory.CreateDirectory(Path.Combine(dir, "_2 Small", "Drums"));
            Directory.CreateDirectory(Path.Combine(dir, "Basic"));
            File.WriteAllText(Path.Combine(dir, "Default Setting.ffp"), "x");
            File.WriteAllText(Path.Combine(dir, "Basic", "Vocal.ffp"), "x");
            File.WriteAllText(Path.Combine(dir, "Basic", "Bus.ffp"), "x");
            File.WriteAllText(Path.Combine(dir, "_2 Small", "Drums", "Snare.ffp"), "x");

            var library = new FabFilterPresetLibrary(root);
            var categories = library.GetCategories("FabFilter Pro-C 3");

            Assert.Equal(new[] { "General", "Small / Drums", "Basic" }.OrderBy(t => t), categories.Select(c => c.Title).OrderBy(t => t));
            var basic = categories.Single(c => c.Title == "Basic");
            Assert.Equal(new[] { "Bus", "Vocal" }, basic.Presets.Select(p => p.Name));
            Assert.True(library.Exists(basic.Presets[0]));
            Assert.False(library.Exists(new PresetRef("FabFilter Pro-C 3", "Basic", "Nope")));
            Assert.Empty(library.GetCategories("FabFilter Pro-Q 4"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("", "General")]
    [InlineData("Basic", "Basic")]
    [InlineData("_2 Small/Drums", "Small / Drums")]
    [InlineData("_1 Ambience", "Ambience")]
    public void CategoryTitle_StripsOrderingPrefixes(string key, string expected) =>
        Assert.Equal(expected, FabFilterPresetLibrary.Title(key));

    [Fact]
    public void Library_Build_ReturnsNullForAMissingPreset()
    {
        var library = new FabFilterPresetLibrary(Path.Combine(Path.GetTempPath(), "a-capella-no-presets-" + Guid.NewGuid()));
        Assert.Null(library.Build(new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal"), new byte[] { 1 }));
    }

    // ----- against the real plugins and their factory presets -----

    [Theory]
    [MemberData(nameof(Plugins))]
    public void DefaultSettingPreset_RebuildsThePluginsOwnDefaultState(string label, string folder)
    {
        if (!Installed(label, folder)) { _out.WriteLine($"SKIPPED: {label} or its presets not installed."); return; }
        using var inst = HostedPluginInstance.Create(HostedPluginCatalog.KnownPluginPaths[label], 44100, 512);
        var defaultState = inst.GetState();
        var defaultPreset = Load(Path.Combine(PresetRoot, folder, "Default Setting.ffp"));

        var rebuilt = FabFilterPresetState.Build(defaultState, defaultPreset, defaultPreset);

        var a = FabFilterPresetState.ReadFloats(defaultState);
        var b = FabFilterPresetState.ReadFloats(rebuilt);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) < 1e-4, $"{label} float {i}: {a[i]} vs {b[i]}");
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void EveryFactoryPreset_LoadsIntoThePlugin_AndReadsBackAsRequested(string label, string folder)
    {
        if (!Installed(label, folder)) { _out.WriteLine($"SKIPPED: {label} or its presets not installed."); return; }
        using var inst = HostedPluginInstance.Create(HostedPluginCatalog.KnownPluginPaths[label], 44100, 512);
        var defaultState = inst.GetState();
        var defaultPreset = Load(Path.Combine(PresetRoot, folder, "Default Setting.ffp"));

        int count = 0;
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(PresetRoot, folder), "*.ffp", SearchOption.AllDirectories))
        {
            count++;
            var state = FabFilterPresetState.Build(defaultState, Load(file), defaultPreset);
            inst.SetState(state);
            var requested = FabFilterPresetState.ReadFloats(state);
            var got = FabFilterPresetState.ReadFloats(inst.GetState());
            int mismatches = 0;
            for (int i = 0; i < requested.Length; i++)
                if (Math.Abs(requested[i] - got[i]) > 1e-3 * Math.Max(1, Math.Abs(requested[i]))) mismatches++;
            if (mismatches > 0) bad.Add($"{Path.GetFileName(file)}:{mismatches}");
        }

        _out.WriteLine($"{label}: {count} presets, {bad.Count} with mismatches");
        Assert.True(count > 0);
        Assert.True(bad.Count == 0, string.Join(", ", bad.Take(10)));
    }

    [Fact]
    public void ApplyingAFactoryPreset_ChangesTheCompressorsParameters()
    {
        const string label = "FabFilter Pro-C 3";
        if (!Installed(label, "Pro-C 3")) { _out.WriteLine("SKIPPED: Pro-C 3 or its presets not installed."); return; }
        using var inst = HostedPluginInstance.Create(HostedPluginCatalog.KnownPluginPaths[label], 44100, 512);
        var defaultState = inst.GetState();
        var library = FabFilterPresetLibrary.Default;
        var preset = library.GetCategories(label).SelectMany(c => c.Presets).First(p => p.Name != FabFilterPresetLibrary.DefaultSettingName);

        var before = Enumerable.Range(0, inst.ParameterCount).Select(inst.GetParameterValue).ToArray();
        inst.SetState(library.Build(preset, defaultState)!);
        var after = Enumerable.Range(0, inst.ParameterCount).Select(inst.GetParameterValue).ToArray();

        Assert.NotEqual(before, after);
    }
}
