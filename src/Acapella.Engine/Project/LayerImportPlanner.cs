namespace Acapella.Engine.Project;

/// <summary>Multi-file import (Feature_Spec_2026-09-23_MultiFileImport): decides which file goes
/// into which grid cell. Pure -- no I/O, no model mutation -- so the cap/order/gap rules are
/// unit-testable without WPF. Files are sorted by file name (case-insensitive, full path as the
/// tie-break) because OpenFileDialog.FileNames doesn't guarantee click order (spec D3); free cells
/// are filled lowest-first, never beyond LayerCollection.MaxLayers.</summary>
public static class LayerImportPlanner
{
    public readonly record struct Assignment(int CellIndex, string FilePath);

    /// <summary>Unoccupied grid cells in ascending order, never beyond LayerCollection.MaxLayers. The one
    /// definition of "free slot", shared by Plan (multi-file import) and the Record entry points (bug
    /// audit #7), so both fill the lowest free cell first.</summary>
    public static IReadOnlyList<int> FreeCells(IEnumerable<int> occupiedCellIndices)
    {
        var occupied = occupiedCellIndices.ToHashSet();
        return Enumerable.Range(0, LayerCollection.MaxLayers).Where(c => !occupied.Contains(c)).ToList();
    }

    public static (IReadOnlyList<Assignment> Assignments, IReadOnlyList<string> Skipped) Plan(
        IEnumerable<int> occupiedCellIndices, IEnumerable<string> filePaths)
    {
        var freeCells = FreeCells(occupiedCellIndices);

        // TODO(polish): natural sort so "take10" follows "take2" (spec Known limitations).
        var sorted = filePaths
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int placed = Math.Min(freeCells.Count, sorted.Count);
        var assignments = Enumerable.Range(0, placed).Select(i => new Assignment(freeCells[i], sorted[i])).ToList();
        return (assignments, sorted.Skip(placed).ToList());
    }
}
