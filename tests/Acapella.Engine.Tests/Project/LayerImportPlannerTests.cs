using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Project;

public class LayerImportPlannerTests
{
    [Fact]
    public void EmptyProject_FillsCellsInAscendingOrder_SortedByFileNameIgnoringCase()
    {
        var (assignments, skipped) = LayerImportPlanner.Plan(
            Array.Empty<int>(),
            new[] { @"C:\x\c.wav", @"C:\x\A.mp4", @"C:\x\b.m4a" });

        Assert.Equal(3, assignments.Count);
        Assert.Equal(new LayerImportPlanner.Assignment(0, @"C:\x\A.mp4"), assignments[0]);
        Assert.Equal(new LayerImportPlanner.Assignment(1, @"C:\x\b.m4a"), assignments[1]);
        Assert.Equal(new LayerImportPlanner.Assignment(2, @"C:\x\c.wav"), assignments[2]);
        Assert.Empty(skipped);
    }

    [Fact]
    public void SkipsOccupiedCells_FillingGapsFirst()
    {
        var (assignments, skipped) = LayerImportPlanner.Plan(
            new[] { 0, 2 },
            new[] { @"C:\x\x.wav", @"C:\x\y.wav" });

        Assert.Equal(2, assignments.Count);
        Assert.Equal(new LayerImportPlanner.Assignment(1, @"C:\x\x.wav"), assignments[0]);
        Assert.Equal(new LayerImportPlanner.Assignment(3, @"C:\x\y.wav"), assignments[1]);
        Assert.Empty(skipped);
    }

    [Fact]
    public void MoreFilesThanFreeCells_SkipsTheAlphabeticallyLast()
    {
        var (assignments, skipped) = LayerImportPlanner.Plan(
            new[] { 0, 1, 2 },
            new[] { @"C:\x\d.wav", @"C:\x\a.wav", @"C:\x\c.wav" });

        var assignment = Assert.Single(assignments);
        Assert.Equal(new LayerImportPlanner.Assignment(3, @"C:\x\a.wav"), assignment);
        Assert.Equal(new[] { @"C:\x\c.wav", @"C:\x\d.wav" }, skipped);
    }

    [Fact]
    public void NoFreeCells_AssignsNothing_SkipsEverything()
    {
        var (assignments, skipped) = LayerImportPlanner.Plan(
            new[] { 0, 1, 2, 3 },
            new[] { @"C:\x\a.wav", @"C:\x\b.wav" });

        Assert.Empty(assignments);
        Assert.Equal(new[] { @"C:\x\a.wav", @"C:\x\b.wav" }, skipped);
    }

    [Fact]
    public void FewerFilesThanFreeCells_OnlyFillsThatMany()
    {
        var (assignments, skipped) = LayerImportPlanner.Plan(
            Array.Empty<int>(),
            new[] { @"C:\x\a.wav" });

        var assignment = Assert.Single(assignments);
        Assert.Equal(new LayerImportPlanner.Assignment(0, @"C:\x\a.wav"), assignment);
        Assert.Empty(skipped);
    }

    [Fact]
    public void NeverAssignsMoreThanMaxLayers()
    {
        var files = Enumerable.Range(0, 10).Select(i => $@"C:\x\f{i:D2}.wav").ToList();

        var (assignments, skipped) = LayerImportPlanner.Plan(Array.Empty<int>(), files);

        Assert.Equal(LayerCollection.MaxLayers, assignments.Count);
        var cells = assignments.Select(a => a.CellIndex).ToList();
        Assert.Equal(Enumerable.Range(0, LayerCollection.MaxLayers), cells);
        Assert.Equal(6, skipped.Count);
    }

    [Fact]
    public void SameFileNameInDifferentFolders_OrdersByFullPath()
    {
        var (assignments, _) = LayerImportPlanner.Plan(
            Array.Empty<int>(),
            new[] { @"C:\b\take.wav", @"C:\a\take.wav" });

        Assert.Equal(new LayerImportPlanner.Assignment(0, @"C:\a\take.wav"), assignments[0]);
        Assert.Equal(new LayerImportPlanner.Assignment(1, @"C:\b\take.wav"), assignments[1]);
    }
}
