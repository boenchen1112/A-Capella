using A_Capella.Engine.Mix;
using A_Capella.Engine.Presets;

namespace A_Capella.Engine.Tests.Presets;

public class FxPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "a-capella-packages-" + Guid.NewGuid());
    private string BuiltIn => Path.Combine(_root, "builtin");
    private string User => Path.Combine(_root, "user");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static LayerMixParameters Chosen() => new()
    {
        CompressorPreset = new PresetRef("FabFilter Pro-C 3", "Basic", "Vocal"),
        EqPreset = new PresetRef("FabFilter Pro-Q 4", "", "Bright"),
        ReverbPreset = new PresetRef("FabFilter Pro-R 2", "_2 Small", "Studio A"),
    };

    [Fact]
    public void List_WithNoFolders_IsEmpty() =>
        Assert.Empty(new FxPackageService(BuiltIn, User).List());

    [Fact]
    public void SaveThenList_RoundTripsThePresetChoices()
    {
        var service = new FxPackageService(BuiltIn, User);
        service.Save(FxPackage.FromParameters("  Warm lead ", Chosen()));

        var package = Assert.Single(service.List());

        Assert.Equal("Warm lead", package.Name);
        Assert.False(package.IsBuiltIn);
        Assert.Equal(Chosen().CompressorPreset, package.CompressorPreset);
        Assert.Equal(Chosen().EqPreset, package.EqPreset);
        Assert.Null(package.DeEsserPreset);
        Assert.Equal(Chosen().ReverbPreset, package.ReverbPreset);
    }

    [Fact]
    public void Save_WithSameName_Overwrites()
    {
        var service = new FxPackageService(BuiltIn, User);
        service.Save(FxPackage.FromParameters("Mine", Chosen()));
        service.Save(FxPackage.FromParameters("mine", new LayerMixParameters()));

        Assert.Single(service.List());
        Assert.Null(service.List()[0].CompressorPreset);
    }

    [Fact]
    public void ApplyTo_SetsAllFourEffects_AndSwitchesOffTheOnesThePackageOmits()
    {
        var package = FxPackage.FromParameters("P", new LayerMixParameters { EqPreset = new PresetRef("FabFilter Pro-Q 4", "", "Bright") });
        var layer = Chosen();

        package.ApplyTo(layer);

        Assert.Null(layer.CompressorPreset);
        Assert.Equal(new PresetRef("FabFilter Pro-Q 4", "", "Bright"), layer.EqPreset);
        Assert.Null(layer.ReverbPreset);
    }

    [Fact]
    public void BuiltInPackages_AreListedFirst_AndCannotBeOverwritten()
    {
        Directory.CreateDirectory(BuiltIn);
        File.WriteAllText(Path.Combine(BuiltIn, "starter.json"), "{\"Name\":\"Starter\",\"EqPreset\":{\"Plugin\":\"FabFilter Pro-Q 4\",\"Category\":\"\",\"Name\":\"Bright\"}}");
        var service = new FxPackageService(BuiltIn, User);
        service.Save(FxPackage.FromParameters("Zeta", Chosen()));

        var list = service.List();

        Assert.Equal(new[] { "Starter", "Zeta" }, list.Select(p => p.Name));
        Assert.True(list[0].IsBuiltIn);
        Assert.Throws<ArgumentException>(() => service.Save(FxPackage.FromParameters("starter", Chosen())));
        Assert.False(service.Delete("Starter"));
    }

    [Fact]
    public void Delete_RemovesAUsersPackage_AndSkipsUnreadableFiles()
    {
        var service = new FxPackageService(BuiltIn, User);
        service.Save(FxPackage.FromParameters("Mine", Chosen()));
        File.WriteAllText(Path.Combine(User, "broken.json"), "not json");

        Assert.Single(service.List());
        Assert.True(service.Delete("Mine"));
        Assert.Empty(service.List());
    }

    [Fact]
    public void Save_WithBlankName_Throws() =>
        Assert.Throws<ArgumentException>(() => new FxPackageService(BuiltIn, User).Save(FxPackage.FromParameters("  ", Chosen())));
}
