using System.Text.Json;

namespace Acapella.Engine.Persistence;

/// <summary>
/// App-wide undo/redo over project edits (P1 task 2): snapshots the whole project DTO on every
/// discrete edit (a slider commit, not per-tick) rather than diffing individual fields -- simplest
/// robust approach given ProjectFileDto is already the save/load format. Explicitly not in scope:
/// undoing a recording capture or a file export (those aren't project-state edits).
/// </summary>
public class ProjectUndoStack
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly int _maxDepth;
    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private string? _current;

    public ProjectUndoStack(int maxDepth = 100)
    {
        _maxDepth = maxDepth;
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Establishes the baseline snapshot without creating an undo entry -- call once
    /// when a project is loaded/created, before any Push.</summary>
    public void Reset(ProjectFileDto initialState)
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _current = Serialize(initialState);
    }

    /// <summary>Records a completed edit. Must be a deep-copied snapshot (serialized to a string)
    /// so later in-place mutation of the live DTO/model can't corrupt history. Clears the redo
    /// stack, matching standard undo/redo semantics (a new edit invalidates the old redo branch).</summary>
    public void Push(ProjectFileDto newState)
    {
        if (_current is null)
        {
            Reset(newState);
            return;
        }

        _undoStack.Add(_current);
        if (_undoStack.Count > _maxDepth)
            _undoStack.RemoveAt(0);

        _redoStack.Clear();
        _current = Serialize(newState);
    }

    public ProjectFileDto? Undo()
    {
        if (!CanUndo)
            return null;

        _redoStack.Add(_current!);
        string previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _current = previous;
        return Deserialize(_current);
    }

    public ProjectFileDto? Redo()
    {
        if (!CanRedo)
            return null;

        _undoStack.Add(_current!);
        string next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _current = next;
        return Deserialize(_current);
    }

    private static string Serialize(ProjectFileDto dto) => JsonSerializer.Serialize(dto, JsonOptions);

    private static ProjectFileDto Deserialize(string json) =>
        JsonSerializer.Deserialize<ProjectFileDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Could not restore project snapshot from undo/redo history.");
}
