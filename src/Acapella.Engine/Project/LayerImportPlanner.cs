namespace Acapella.Engine.Project;

/// <summary>Multi-file import (Feature_Spec_2026-09-23_MultiFileImport): decides which file goes
/// into which grid cell. Pure -- no I/O, no model mutation -- so the cap/order/gap rules are
/// unit-testable without WPF. Files are sorted by file name (case-insensitive, full path as the
/// tie-break) because OpenFileDialog.FileNames doesn't guarantee click order (spec D3); free cells
/// are filled lowest-first, never beyond LayerCollection.MaxLayers.</summary>
public static class LayerImportPlanner
{
    public readonly record struct Assignment(int CellIndex, string FilePath);

    public static (IReadOnlyList<Assignment> Assignments, IReadOnlyList<string> Skipped) Plan(
        IEnumerable<int> occupiedCellIndices, IEnumerable<string> filePaths)
    {
        var occupied = occupiedCellIndices.ToHashSet();
        var freeCells = Enumerable.Range(0, LayerCollection.MaxLayers).Where(c => !occupied.Contains(c)).ToList();

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
