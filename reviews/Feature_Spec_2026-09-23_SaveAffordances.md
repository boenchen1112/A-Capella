# Feature Spec — Save affordances: Ctrl+S, Save vs Save As, dirty marker, discard prompts

> Spec as of `0d6f443` (after Bug Audit #5's fix: `ReleaseAll()`, stop-before-replace, export gate on File > New / File > Open). Implements **Candidate 1** of `reviews/Improvement_Proposal_2026-09-23.md`. Every citation below is read from the current source, not taken from the proposal. Where this spec disagrees with the proposal's scope note, the disagreement is called out and explained.
>
> **Scope check (Pause Rule 2):** no new dependency (`MessageBox`, `SaveFileDialog`, `RoutedCommand`, `KeyBinding` are all stock WPF / `Microsoft.Win32`), no locked product decision touched, **no change to the project file format** (`ProjectFileDto` gets no new fields). The one borderline item is adding a small visible text element to the v8 toolbar (§5). It is a UI detail, not a Pause Rule 2 matter, but it is flagged as a **[human]** check.

---

## The feature in one sentence

`ProjectSession` learns which file it belongs to (`CurrentFilePath`) and whether it has unsaved edits (`IsDirty`). MainWindow uses those two facts to make Ctrl+S save in place, to split Save from Save As, to show `name •` in the window title and toolbar, and to ask "Save changes?" before File > New, File > Open or window close throws away edits.

---

## Current behaviour (verified)

| Area | Where | What it does today |
|---|---|---|
| Save | `src/Acapella.App/MainWindow.xaml.cs:779-798` (`SaveProjectButton_Click`) | Always opens a `SaveFileDialog`. Enforces the `.acapella.json` suffix on the dialog's result (L7, `:786-790`). Catches exceptions into `StatusText` (`:794-797`). Remembers nothing afterwards. |
| Save menu | `src/Acapella.App/MainWindow.xaml:151` | `<MenuItem Header="_Save..." Click="SaveProjectButton_Click"/>`. No Save As item and no `InputGestureText`. |
| Key bindings | `MainWindow.xaml:23-30`, `MainWindow.xaml.cs:27-28, 219-220` | Only `UndoCommand`/`RedoCommand`: static `RoutedCommand` + `KeyBinding` + `CommandBinding` + `*_Executed` handler. **This is the pattern to copy.** |
| Title | `MainWindow.xaml:12-13` | `Title="Acapella"`, a literal. **Also `WindowStyle="None"`** with custom `WindowChrome` (`:20-22`), so `Window.Title` is **never drawn anywhere inside the window**. It only shows in the taskbar button, Alt+Tab and the taskbar thumbnail. The proposal's "title binding" alone would therefore be invisible while the user works. See §5. |
| UI update style | whole of `MainWindow.xaml.cs` | `MainWindow` does **not** implement `INotifyPropertyChanged`. Every window-level status surface is a direct code-behind write (`StatusText.Text`, `FxHeaderText.Text` `:584`, `CurrentTimeText.Text` `:377`, `CpuLoadText.Text` `:337`). Data binding is used only inside `LayerRowViewModel` templates. The proposal's "title binding" does not match this codebase, so this spec uses an explicit `UpdateWindowTitle()`. |
| Engine → UI notifications | `MainWindow.xaml.cs:146-160` | Engine classes expose plain `event Action` members and MainWindow subscribes in its constructor (`_previewEngine.PlaybackStopped += ...`). §3 follows this for `ProjectSession.SaveStateChanged`. |
| Current file / modified state | `src/Acapella.Engine/Project/ProjectSession.cs` | Neither exists. `ProjectUndoStack` (`src/Acapella.Engine/Persistence/ProjectUndoStack.cs` (it lives under **Persistence/**, not Project/)) exposes only `CanUndo`/`CanRedo` (`:25-26`) and keeps its current snapshot in a private string (`:18`). Nothing there tracks a "saved position", so there is nothing to reuse and new state is required. |
| Edit funnel | `ProjectSession.cs:53`, `MainWindow.xaml.cs:182-186` | `CommitEdit()` is called from one place only, `MainWindow.PushUndoSnapshot()`, and that returns early while `_applyingHistory` is set. Call sites of `PushUndoSnapshot`: slider release `:236`, checkbox `:238`, trim `:240`, layer name `:242`, master volume release `:313`, record `:496`, upload `:536`, hosted-editor poll `:120`. |
| Edits that **bypass** the funnel | `MainWindow.xaml.cs:315-321`, `:485` | `MetronomeBpmTextBox_LostFocus` writes `_session.MetronomeBpm` with no `PushUndoSnapshot`. `OpenRecordSetupForRow` writes `_session.MetronomeBpm = dialog.Bpm` unconditionally, even when the dialog was cancelled. BPM is saved in the file (`ProjectPersistenceService.cs:22`), so a flag set only in `CommitEdit` would **miss a BPM-only change** and the close prompt would silently lose it. §1 fixes this in the setter. |
| File > New | `MainWindow.xaml.cs:870-888` | `async void`. Order: **(1)** export-in-flight gate `:872-876` → **(2)** `await AwaitPreviewCommand(_previewEngine.StopAsync())` `:880` → **(3)** `ApplyRestore(() => { _session.New(); ... })` `:882-886`. |
| File > Open | `MainWindow.xaml.cs:800-831` | `async void`. Order: **(1)** export gate `:802-806` → **(2)** `OpenFileDialog` `:808-809` → **(3)** `await ... StopAsync()` `:818` → **(4)** `ApplyRestore(() => { _session.Open(...); ... })` `:820-824`. All of (3)-(4) sits inside `try/catch` → `"Open failed: ..."`. |
| Window close | `MainWindow.xaml.cs:162-172` | **One** `Closing` lambda. It stops the three timers and then disposes `_previewEngine`, `_mixEngine` and `_hostedService` **unconditionally**, without checking `e.Cancel`. Every close path reaches it: the chrome ✕ (`CloseButton_Click` → `SystemCommands.CloseWindow`, `:404`), Alt+F4 and taskbar close. No other code calls `Close()` or `Shutdown()` on the main window (`App.xaml:5` uses `StartupUri`, default `ShutdownMode`). |
| Existing confirm-style UI | `MainWindow.xaml.cs:468, 475, 892` | Plain `MessageBox.Show(this, text, caption, MessageBoxButton..., MessageBoxImage...)`. No custom dialog pattern exists, so the stock `MessageBox` with `YesNoCancel` is the match. |

---

## Desired behaviour

1. A fresh app or File > New project is **Untitled** and **clean**.
2. Any edit that changes what would be written to the file marks the project **dirty**. That means `CommitEdit`, `Undo`, `Redo`, a real change to `MetronomeBpm` or a real change to `MasterVolumeDb`.
3. **Save (Ctrl+S / File > Save):** if `CurrentFilePath` is set, write to it with no dialog. Otherwise behave exactly like Save As.
4. **Save As (Ctrl+Shift+S / File > Save As...):** always show the dialog. On success, the chosen path becomes `CurrentFilePath`.
5. A successful Save, Save As or Open sets `CurrentFilePath` and clears dirty. File > New sets `CurrentFilePath = null` and clears dirty. A **failed** save or open changes neither.
6. The window title reads `Acapella — <name>` or `Acapella — <name> •`, and a toolbar text shows `<name>` or `<name> •`. `<name>` is `Untitled` or the file name with `.acapella.json` stripped.
7. Before File > New, File > Open or window close, if dirty, ask **"Save changes to <name>?"** with Yes / No / Cancel.
   - **Yes** runs Save, which may open the Save As dialog for an untitled project. Continue only if the file was actually written. A cancelled dialog or a failed write aborts the action.
   - **No** discards and continues.
   - **Cancel** (or closing the message box) aborts. Nothing else happens: playback is **not** stopped and the app is **not** torn down.

---

## Design decisions (argued once, so the implementer doesn't re-decide)

**D1 — A plain `bool IsDirty` on `ProjectSession`, not a "saved position" in the undo stack.** Comparing the current snapshot to the last-saved snapshot would clear the marker when the user undoes back to the saved state. It would also need either a `Snapshot()` per title refresh, which pulls live plugin state from native code, or a new saved-index concept in `ProjectUndoStack` that must survive the 100-entry trim (`ProjectUndoStack.cs:49-50`). Both are more than this ticket needs. The consequence is that undoing back to the saved state **leaves the marker on**. That is conservative: it never hides unsaved work, it only occasionally prompts when nothing changed. Leave a `// TODO(polish): clear dirty when undo returns to the saved snapshot` on `Undo()`.

**D2 — `Undo()`/`Redo()` mark dirty. The shared private `Restore()` does not.** `Open()` goes through `Restore()` (`ProjectSession.cs:76`) and must end clean. So the mark goes in the two public methods, and only when they return `true`.

**D3 — BPM and master volume mark dirty in their setters, and only on a real value change.** This covers both funnel bypasses (`:318`, `:485`) and keyboard-driven master slider changes, with no call-site edits. The equality guard matters:
- `OpenRecordSetupForRow` re-assigns the unchanged BPM whenever the record dialog is cancelled.
- `ApplyRestore` writes `MasterVolumeSlider.Value = _session.MasterVolumeDb` (`:198`), and `MasterVolumeSlider_ValueChanged` writes the same value straight back (`:285`).

Without the guard, both would dirty a clean project. Because `Restore()` and `New()` also assign these properties through the setters, **the dirty-clear in `Open()`/`New()` must be the last statement** (see §1).

**D4 — `ProjectSession` raises one `event Action? SaveStateChanged`, and MainWindow does a direct code-behind write in response.** This deliberately differs from the obvious alternative of calling `UpdateWindowTitle()` at every call site. With D3 the set of mutation sites is open-ended: two setters, each written from several MainWindow handlers, plus `CommitEdit`/`Undo`/`Redo`/`Save`/`Open`/`New`. One subscription can't miss a site. It follows the existing `PlaybackStopped` precedent, and the title write itself is still a plain `Title = ...` / `ProjectTitleText.Text = ...` in code-behind, matching the rest of MainWindow. All `ProjectSession` members are already called only on the UI thread (`SnapshotForExport` runs before `Task.Run`, `:847`), so the event needs no marshalling.

**D5 — The suffix rule stays in the dialog path only.** `ProjectSession.Save(path)` records exactly the path it is given. A project opened as `foo.json` (the Open filter allows `*.json`, `:808`) must be written back to `foo.json` by Ctrl+S, not to `foo.json.acapella.json`. L7's suffix enforcement (`:788-790`) is applied only to a `SaveFileDialog` result.

---

## Proposed implementation

### 1. `src/Acapella.Engine/Project/ProjectSession.cs`

Replace the two auto-properties (`:29`, `:34`) with guarded setters, and add the save-state members:

```csharp
/// <summary>The suffix every dialog-chosen project path is forced to (L7).</summary>
public const string ProjectFileSuffix = ".acapella.json";

private double _metronomeBpm = 120;
private float _masterVolumeDb;

public double MetronomeBpm
{
    get => _metronomeBpm;
    set { if (_metronomeBpm == value) return; _metronomeBpm = value; MarkDirty(); }
}

public float MasterVolumeDb
{
    get => _masterVolumeDb;
    set { if (_masterVolumeDb == value) return; _masterVolumeDb = value; MarkDirty(); }
}

/// <summary>The file this project was last successfully saved to or opened from; null for a
/// project that has never been saved (fresh app, File > New).</summary>
public string? CurrentFilePath { get; private set; }

/// <summary>True once anything that would be written to the project file has changed since the
/// last successful Save/Open/New. Conservative: undoing back to the saved state leaves it set.</summary>
public bool IsDirty { get; private set; }

/// <summary>"Untitled", or the file name with ".acapella.json" (else its last extension) stripped.</summary>
public string DisplayName => CurrentFilePath is null ? "Untitled" : StripProjectSuffix(Path.GetFileName(CurrentFilePath));

/// <summary>Raised (on the calling thread) whenever IsDirty or CurrentFilePath actually changes.</summary>
public event Action? SaveStateChanged;

private void MarkDirty() => SetSaveState(CurrentFilePath, dirty: true);

private void SetSaveState(string? filePath, bool dirty)
{
    if (filePath == CurrentFilePath && dirty == IsDirty) return;
    CurrentFilePath = filePath;
    IsDirty = dirty;
    SaveStateChanged?.Invoke();
}

private static string StripProjectSuffix(string fileName) =>
    fileName.EndsWith(ProjectFileSuffix, StringComparison.OrdinalIgnoreCase)
        ? fileName[..^ProjectFileSuffix.Length]
        : Path.GetFileNameWithoutExtension(fileName);
```

`Path.GetFileNameWithoutExtension("foo.acapella.json")` returns `"foo.acapella"`. That is why the suffix is stripped explicitly.

Then change the existing members:

```csharp
public void CommitEdit()
{
    _undoStack.Push(Snapshot());
    MarkDirty();
}

public bool Undo()
{
    if (!Restore(_undoStack.Undo())) return false;
    MarkDirty();   // TODO(polish): clear dirty when undo returns to the saved snapshot (D1)
    return true;
}

public bool Redo()
{
    if (!Restore(_undoStack.Redo())) return false;
    MarkDirty();
    return true;
}

public void Save(string filePath)
{
    _persistence.SaveToFile(Snapshot(), filePath);
    SetSaveState(filePath, dirty: false);   // only reached if the write succeeded
}

public void Open(string filePath)
{
    _mixEngine.ReleaseAllHostedInstances();          // unchanged, still FIRST (bug audit #5)
    Restore(_persistence.LoadFromFile(filePath));
    _undoStack.Reset(Snapshot());
    SetSaveState(filePath, dirty: false);            // LAST: Restore() assigns MetronomeBpm/MasterVolumeDb through their dirty-marking setters
}

public void New()
{
    _mixEngine.ReleaseAllHostedInstances();          // unchanged, still FIRST
    Layers.Restore(Enumerable.Empty<LayerModel>());
    LatencyOffsetMsUsed = null;
    MasterVolumeDb = 0f;
    _undoStack.Reset(Snapshot());
    SetSaveState(null, dirty: false);                // LAST, same reason
}
```

Notes:
- **Do not** move `ReleaseAllHostedInstances()` in `Open`/`New`. Bug Audit #5's correctness argument depends on it running first.
- **Do not** put `MarkDirty()` in the private `Restore()` (D2).
- The constructor is unchanged. It assigns neither setter, so a new session starts clean and untitled.
- A failed `Open` throws out of `LoadFromFile` (or `FromDto`, which parses before `Layers.Restore` mutates anything), so `SetSaveState` is never reached and path/dirty stay as they were.
- Extend the class doc comment with one line: "Tracks the project's file (CurrentFilePath) and unsaved-edit state (IsDirty); see SaveStateChanged."

### 2. `src/Acapella.App/MainWindow.xaml` — commands, key bindings, menu, toolbar text

In `Window.InputBindings` (`:23-26`), add:

```xml
<KeyBinding Key="S" Modifiers="Control" Command="{x:Static local:MainWindow.SaveCommand}"/>
<KeyBinding Key="S" Modifiers="Control+Shift" Command="{x:Static local:MainWindow.SaveAsCommand}"/>
```

In `Window.CommandBindings` (`:27-30`), add:

```xml
<CommandBinding Command="{x:Static local:MainWindow.SaveCommand}" Executed="SaveCommandBinding_Executed"/>
<CommandBinding Command="{x:Static local:MainWindow.SaveAsCommand}" Executed="SaveAsCommandBinding_Executed"/>
```

Replace the File menu's Save item (`:151`) with two items. Save has no ellipsis because it usually doesn't open a dialog (Windows convention). `InputGestureText` matches the Edit menu's Undo/Redo items (`:156-157`):

```xml
<MenuItem Header="_Save" Click="SaveMenuItem_Click" InputGestureText="Ctrl+S"/>
<MenuItem Header="Save _As..." Click="SaveProjectButton_Click" InputGestureText="Ctrl+Shift+S"/>
```

`SaveProjectButton_Click` keeps its name and becomes the Save As handler, to minimise churn.

Visible project name. Because of `WindowStyle="None"`, `Title` alone isn't visible inside the window. Add one `TextBlock` to the toolbar's right-docked group, **between** the undo/redo `StackPanel` (ends `:142`) and the `ScrollViewer` (`:144`). In a `DockPanel`, right-docked children stack right-to-left in declaration order, so it lands just left of ↩ ↪. Being right-docked, it can never scroll off like the middle section can.

```xml
<TextBlock x:Name="ProjectTitleText" DockPanel.Dock="Right" Style="{StaticResource ToolbarText}"
           Foreground="{StaticResource TextBrush}" MaxWidth="220" TextTrimming="CharacterEllipsis"/>
```

Leave out `IsHitTestVisibleInChrome` so the text stays part of the drag-to-move caption like the rest of the band, and leave out the `Hint_MouseEnter` tag, since mouse events in the caption band go to the non-client area anyway.

### 3. `src/Acapella.App/MainWindow.xaml.cs` — commands, title, save helpers

Next to `UndoCommand`/`RedoCommand` (`:27-28`):

```csharp
public static readonly RoutedCommand SaveCommand = new();
public static readonly RoutedCommand SaveAsCommand = new();
```

In the constructor, after `InitializeComponent()` (`:98`) so `ProjectTitleText` exists, for example right after `SelectMasterStrip();` (`:102`):

```csharp
_session.SaveStateChanged += UpdateWindowTitle;
UpdateWindowTitle();
```

New members. Put them in the "Project save/load/export" region (`:777`), replacing the body of `SaveProjectButton_Click` (`:779-798`):

```csharp
private void SaveMenuItem_Click(object sender, RoutedEventArgs e) => SaveProject(forceDialog: false);
/// <summary>File > Save As...</summary>
private void SaveProjectButton_Click(object sender, RoutedEventArgs e) => SaveProject(forceDialog: true);
private void SaveCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject(forceDialog: false);
private void SaveAsCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject(forceDialog: true);

/// <summary>Save (forceDialog=false: straight to CurrentFilePath, dialog only if untitled) or
/// Save As (forceDialog=true: always the dialog). Returns true only if the file was actually
/// written -- the discard prompt relies on that to abort on a cancelled dialog or failed write.</summary>
private bool SaveProject(bool forceDialog)
{
    string? filePath = forceDialog ? null : _session.CurrentFilePath;
    if (filePath is null)
    {
        var dialog = new SaveFileDialog { Filter = "Acapella project|*.acapella.json", DefaultExt = ProjectSession.ProjectFileSuffix };
        if (_session.CurrentFilePath is not null)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_session.CurrentFilePath);
            dialog.FileName = Path.GetFileName(_session.CurrentFilePath);
        }
        if (dialog.ShowDialog() != true) return false;

        // L7: enforce the suffix explicitly instead of relying on the dialog's own extension
        // logic (DefaultExt doesn't reliably stop a double-append for multi-segment extensions).
        // Dialog results only -- a project opened as plain *.json is saved back under its own name (D5).
        filePath = dialog.FileName.EndsWith(ProjectSession.ProjectFileSuffix, StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : dialog.FileName + ProjectSession.ProjectFileSuffix;
    }

    try
    {
        _session.Save(filePath);
        StatusText.Text = $"Project saved: {Path.GetFileName(filePath)}";
        return true;
    }
    catch (Exception ex)
    {
        StatusText.Text = $"Save failed: {ex.Message}";
        return false;
    }
}

/// <summary>Before a dirty project is discarded (File > New, File > Open, window close): Yes =
/// save first (may show the Save As dialog), No = discard, Cancel = abort. Returns true if the
/// caller may go ahead and replace/close the project. Must run BEFORE any preview stop or
/// teardown so that Cancel leaves everything running.</summary>
private bool ConfirmDiscardUnsavedChanges()
{
    if (!_session.IsDirty) return true;

    var answer = MessageBox.Show(this, $"Save changes to {_session.DisplayName}?", "Acapella",
        MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
    return answer switch
    {
        MessageBoxResult.Yes => SaveProject(forceDialog: false),
        MessageBoxResult.No => true,
        _ => false,   // Cancel, or the message box's own close button
    };
}

/// <summary>Driven by ProjectSession.SaveStateChanged. Direct writes, matching the rest of this
/// window (no INotifyPropertyChanged on MainWindow). Title shows in taskbar/Alt+Tab only
/// (WindowStyle=None), hence the toolbar copy.</summary>
private void UpdateWindowTitle()
{
    string label = _session.IsDirty ? $"{_session.DisplayName} •" : _session.DisplayName;
    Title = $"Acapella — {label}";
    ProjectTitleText.Text = label;
}
```

Pre-filling Save As with `Path.GetFileName(CurrentFilePath)` (not `DisplayName`) means the dialog returns a name that already ends in the suffix, so L7 doesn't have to append anything. Save is **not** gated on an in-flight export. It isn't today, and this ticket doesn't change that.

### 4. `MainWindow.xaml.cs` — discard prompt in New / Open / Close, composed with Bug Audit #5

**Hard ordering rule for New and Open:** `export gate → ConfirmDiscardUnsavedChanges() → (Open only: OpenFileDialog) → await StopAsync() → ApplyRestore(New/Open)`.

Why the prompt goes **after** the gate: prompting "Save changes?" and then refusing with "Finish the export first" is backwards.

Why the prompt goes **before** `StopAsync()`, for two reasons:
- **(a) Cancel semantics.** Cancelling File > New must not have already stopped the user's playback.
- **(b) Bug Audit #5's invariant.** All user interaction must be over before the stop-then-release pair runs, with nothing interactive between them. `MessageBox` and the file dialogs run nested message loops. Putting one between `StopAsync()` and `_session.New()/Open()` reopens a window in which queued work runs against a stopped-but-not-yet-replaced project. Keep `await AwaitPreviewCommand(_previewEngine.StopAsync())` as the last `await` before `ApplyRestore`, exactly as it is now.

Saving from inside the prompt while preview is still playing is safe. `Save` → `Snapshot()` only *pulls* state (`SyncLiveStateIntoParameters`) and releases nothing, which is the same thing the 500 ms poll timer's `CommitEdit` already does during playback.

`NewProjectMenuItem_Click` (`:870-888`): insert one statement after the export gate (after `:876`), before the Bug-Audit-#5 comment and `StopAsync` (`:878-880`):

```csharp
if (!ConfirmDiscardUnsavedChanges()) return;
```

`OpenProjectButton_Click` (`:800-831`): insert the same statement after the export gate (after `:806`) and **before** the `OpenFileDialog` (`:808`), matching standard Windows behaviour of asking about the current document before browsing for the next one. Nothing else in the method moves. Cancelling the file dialog after answering No leaves the (still dirty) project open. Cancelling it after answering Yes leaves the now-saved project open and clean. Both are correct.

**Close:** edit the **existing** `Closing` lambda (`:162-172`). Put the prompt at its **top**, with an early `return`:

```csharp
Closing += (s, e) =>
{
    // Save-affordances spec: prompt FIRST, before anything is stopped or disposed -- a cancelled
    // close (Cancel, or Yes followed by a cancelled/failed save) must leave the app fully running.
    if (!ConfirmDiscardUnsavedChanges())
    {
        e.Cancel = true;
        return;
    }

    _hostedStatePollTimer.Stop();
    // ... rest unchanged ...
};
```

Do **not** add a second `Closing` handler. WPF invokes every subscribed handler even after one sets `e.Cancel = true`, so a separate handler would still run the dispose block and tear down the preview, mix engine and hosted service on a cancelled close. `e.Cancel` must be set *and* the teardown skipped, which only works inside the one lambda. The existing ~2 s close-during-playback hang (Bug Audit #5 runner-up) sits downstream in `_previewEngine.Dispose()` and is not touched.

### 5. Things deliberately **not** changed

- `ApplyRestore` (`:190-215`) and `PushUndoSnapshot` (`:182-186`) need no edits. The title updates via the event raised inside `_session.Open/New/Undo/Redo/CommitEdit`.
- `MetronomeBpmTextBox_LostFocus` (`:315-321`) and `OpenRecordSetupForRow` (`:485`) need no edits. The guarded setter (D3) handles them.
- `AddLayerButton_Click` (`:433-447`) correctly does **not** dirty the project. An empty row has no `LayerModel` and isn't in the saved DTO.
- `ExportButton_Click` neither dirties nor cleans. `SnapshotForExport()` → `Snapshot()` doesn't touch save state.
- No Ctrl+N / Ctrl+O bindings. They weren't asked for.

---

## Tests — `tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs`

All of these use the existing fixture (`_mixEngine`, `TempProjectPath()`, `:11-37`). No WPF, no native DLL. The title, prompt and key bindings are WPF UI and, per CLAUDE.md's testing rules, are **[human]**-verified, not unit-tested.

1. **`NewSession_IsCleanAndUntitled`**: `new ProjectSession(_mixEngine)` → `IsDirty` false, `CurrentFilePath` null, `DisplayName == "Untitled"`.
2. **`CommitEdit_MarksDirty_SaveClearsItAndRemembersThePath`**: add a layer, `CommitEdit()` → dirty. Save to `Path.Combine(Path.GetTempPath(), $"My Song {Guid.NewGuid()}.acapella.json")` (register it in `_tempFiles`) → clean, `CurrentFilePath == path`, `DisplayName == "My Song <guid>"` (guards the double-extension strip). Then `CommitEdit()` again → dirty, path unchanged.
3. **`Open_RemembersThePath_AndEndsClean`**: build and save a project with `MetronomeBpm = 96` and `MasterVolumeDb = -2f`. In a second session, `CommitEdit()` so it's dirty, then `Open(path)` → clean, `CurrentFilePath == path`. The BPM/volume values **must differ from the opening session's** so that `Restore()` actually fires the dirty-marking setters. That is what makes this test catch a dirty-clear placed before `Restore`.
4. **`New_ForgetsThePath_AndEndsClean`**: save a session (so it has a path), set `MasterVolumeDb = -4f` (dirty), `New()` → `CurrentFilePath` null, `IsDirty` false. The non-zero volume makes `New()`'s `MasterVolumeDb = 0f` fire the setter mid-method, which guards the "clear last" ordering.
5. **`UndoAndRedo_MarkDirty_ButANoOpUndoDoesNot`**: fresh session, `Undo()` returns false → still clean. Then add a layer, `CommitEdit()`, `Save(path)` → clean. `Undo()` → dirty. `Save(path)` → clean. `Redo()` → dirty.
6. **`BpmAndMasterVolume_MarkDirtyOnlyOnARealChange`**: fresh session. Assign `MetronomeBpm = session.MetronomeBpm` and `MasterVolumeDb = session.MasterVolumeDb` → still clean. `MetronomeBpm = 100` → dirty. Save, then `MasterVolumeDb = -1f` → dirty.
7. **`FailedSave_LeavesPathAndDirtyUntouched`**: create a real temp **file** `blocker` (register it), target `Path.Combine(blocker, "x.acapella.json")`. `SaveToFile`'s `Directory.CreateDirectory` then throws `IOException` because a file already has that name, which is deterministic on Windows. Start from a session saved to a good path and then made dirty. `Assert.ThrowsAny<IOException>(() => session.Save(bad))` → still dirty, `CurrentFilePath` still the good path.
8. **`FailedOpen_LeavesPathAndDirtyUntouched`**: same starting state. `Open` a nonexistent temp path → `Assert.Throws<FileNotFoundException>`. Still dirty, path unchanged.
9. **`SaveStateChanged_FiresOnlyOnTransitions`**: count invocations. First `CommitEdit()` → 1. Second `CommitEdit()` → still 1 (already dirty). `Save(path)` → 2. `Save(path)` again → still 2 (same path, already clean).

**Regression carve-out (per CLAUDE.md):** this changes `ProjectSession`, so re-run the whole of `Project/ProjectSessionTests.cs` once, including Bug Audit #5's three tests (`Open_DoesNotLeak...`, `New_ThenAddLayer_GetsAFreshPluginInstance`, `Undo_StillReusesTheSameLiveInstance`), which exercise `Open`/`New`/`Undo`. Also re-run `Persistence/*` once. No existing test should need edits: none of them assert on the new members, and turning the setters into guarded ones changes no observable value.

---

## Acceptance criteria

- **[auto]** The 9 tests above pass. Existing `ProjectSessionTests` and `Persistence/*` still pass. The solution builds with no new warnings.
- **[human]**
  - Fresh app: title and toolbar show `Untitled`. The first Ctrl+S opens the dialog. After saving, the name shows with no `•`.
  - Any fader release or checkbox toggle adds the `•`. Ctrl+S then saves **with no dialog** and clears it.
  - Ctrl+Shift+S and File > Save As... always show the dialog, pre-filled with the current name.
  - With the `•` showing, File > New, File > Open and ✕ each prompt.
  - **Cancel on ✕ while preview is playing leaves playback, meters and the plugin editors running.**
  - Yes on an untitled project followed by cancelling the Save dialog aborts the New/Open/close.
  - The toolbar text sits cleanly left of ↩ ↪ and doesn't crowd the layout (the one UI-placement judgement in this spec).

---

## Known limitations (accepted, not part of this ticket)

- **Undo back to the saved state leaves `•` on** (D1, `TODO(polish)`).
- **Pre-existing undo-funnel noise now shows up as `•`.** Two cases:
  - Opening a hosted plugin's editor on a layer that has no saved state for that slot seeds `_lastPolledHostedState` with `null` (`LayerRowViewModel.cs:321`). The first poll then treats `null` as "changed" (`:354`) → `PushUndoSnapshot` → dirty, just from opening the editor. (The file really would change from `null` to the plugin's default state blob, so this is arguably correct.)
  - `HostedPluginService.PollAraStateChanged` reports a change on its first poll after a layer's ARA session is created (`HostedPluginService.cs:199-200`), i.e. after the first Play with Melodyne enabled.

  Neither is caused by this ticket. Fixing them means changing the poll baselines, which is a separate change.
- **A TextBox edit in progress isn't flushed by Ctrl+S or Alt+F4.** Trim values are bound with the default `LostFocus` trigger (`MainWindow.xaml:108, 113`), and BPM commits in its `LostFocus` handler. So Ctrl+S while still typing in one of those boxes saves the old value, and the `•` only appears once focus leaves. Alt+F4 while typing closes without the edit and without a prompt. The ✕ button and menu clicks move focus first, so they are fine. Leave a `// TODO(polish): flush focused TextBox before save/close`.
- **A modal prompt doesn't block native plugin editor windows.** They are separate top-level JUCE windows not owned by MainWindow, so the user could still tweak a plugin while "Save changes?" is up. A tweak made after answering is not saved. Not worth guarding.
- **Windows log-off / shutdown** is not specially handled.

## Runner-up (not part of this ticket)

- **Save is not atomic.** `ProjectPersistenceService.SaveToFile` (`ProjectPersistenceService.cs:36-44`) does `File.WriteAllText` straight onto the target. A crash or power loss mid-write truncates the project. Until now almost every save went to a freshly chosen path. With Ctrl+S, overwriting the one real project file in place becomes the normal case, so the exposure grows. Fix later with write-to-temp plus `File.Replace`.
