# Feature Spec — Multi-file import: pick several files at once, each fills an empty layer slot

> Spec as of `2c093cc` (after the save-affordances work: `IsDirty`/`CommitEdit`/`SaveStateChanged`, Ctrl+S, discard prompts). This is a **new user-requested feature**, not a candidate from `reviews/Improvement_Proposal_2026-09-23.md`. Every citation below comes from the current source. Line numbers moved with tonight's commits, so don't reuse the ones in `Feature_Spec_2026-09-23_SaveAffordances.md`. Where this spec departs from the defaults already given to the user, the departure is called out and argued.
>
> **Scope check (Pause Rule 2):** no new dependency. `OpenFileDialog.Multiselect`, `UniformGrid` and `MenuItem` are all stock WPF or `Microsoft.Win32`. **The 4-layer cap is untouched.** `LayerCollection.MaxLayers` (`src/Acapella.Engine/Project/LayerModel.cs:61`) and the throw in `LayerCollection.Add` (`:69-70`) stay exactly as they are. The new planner reads `MaxLayers` and never assigns more than it allows. The project file format does not change. The per-strip Upload button keeps its current single-file behaviour. The one layout judgement is a second button beside "+ Add layer", flagged as a **[human]** check.

---

## The feature in one sentence

A new **"Import files as layers..."** action (Add menu, plus a button next to "+ Add layer") opens one multi-select `OpenFileDialog`. The chosen files are sorted by file name and placed into the empty layer slots in ascending slot order, up to the 4-layer cap. Each file is attached exactly as the per-strip Upload attaches one file. The whole batch is recorded as **one** undo step, which also marks the project dirty. Files that don't fit are reported in `StatusText` and skipped.

---

## Current behaviour (verified)

| Area | Where | What it does today |
|---|---|---|
| Per-layer Upload | `src/Acapella.App/MainWindow.xaml.cs:528-550` (`UploadChoice_Click`) | Takes the row from `((FrameworkElement)sender).DataContext` (`:530`). Opens a **single-select** `OpenFileDialog` with filter `"Media files\|*.mp4;*.mov;*.mkv;*.wav;*.mp3;*.m4a\|All files\|*.*"` (`:532-536`). Then, in order: works out the kind with `IsAudioOnlyExtension(Path.GetExtension(...)) ? UploadedAudioOnly : UploadedVideo` (`:538-540`), calls `_layers.Add(kind, path)` (`:541`), sets `layer.CellIndex = row.SlotNumber - 1` (`:544`, audit B8), sets `row.Layer = layer` (`:545`), sets `row.Name = $"Layer {row.SlotNumber}"` if it is empty (`:546`), calls `RefreshPreviewLive()` (`:547`), sets `StatusText` (`:548`) and finally calls `PushUndoSnapshot()` (`:549`). The file is **not** probed. An undecodable file only shows up later, as a preview error. |
| Kind detection | `MainWindow.xaml.cs:552-555` (`IsAudioOnlyExtension`) | `.wav`, `.mp3` and `.m4a` count as audio-only. Everything else, including anything picked through "All files", counts as `UploadedVideo`. This is private static and its only caller is `:538`. |
| Where the Upload button lives | `src/Acapella.App/MainWindow.xaml:86-92` | Inside `MixerStripTemplate`, in a `StackPanel` that is visible only while `HasSource` is false (`:87`). So the **Rec** and **Upload** buttons exist only on a strip that **already exists but is still empty**. |
| How rows come to exist | `MainWindow.xaml.cs:446-460` (`AddLayerButton_Click`), `:513-526` (`RecordingSetupMenuItem_Click`), `:561-576` (`RestoreTracksFromLayers`) | **Rows do not pre-exist.** A fresh app or File > New has `_tracks.Count == 0` and shows only the Master strip. A row is created by "+ Add layer" or Add > Add layer (`new LayerRowViewModel(_tracks.Count + 1)`, then `LiveParamChanged += DebounceRefreshPreview`, then `_tracks.Add`, `:454-456`), by Tools > Recording setup or the toolbar ⏺ (the same three lines at `:521-523`), or by `RestoreTracksFromLayers` on Undo/Redo/Open/New. The last of these rebuilds rows `1..maxCellIndex+1` keyed by `CellIndex` and leaves gap rows empty. So `_tracks` is always contiguous by `SlotNumber` (1..N, N ≤ 4). An empty slot is either an existing row whose `Layer` is null, or a slot number with no row yet. |
| Cap handling, "+ Add layer" | `MainWindow.xaml.cs:448-452, 462-463` | The handler guards on `_tracks.Count >= MaxLayers` and reports `"Layer cap reached (4)."` in `StatusText`. `UpdateAddLayerButtonState()` **disables the button** at 4 rows. The Add > Add layer menu item (`MainWindow.xaml:168`) is **never disabled**. It relies on the handler's status message. This spec copies that precedent: disable the button, keep the menu item enabled, guard in the handler. |
| Refresh of `UpdateAddLayerButtonState` | `:102, :457, :524, :575` | It is called at construction, after "+ Add layer", after Recording setup adds a row, and at the end of `RestoreTracksFromLayers` (which covers Undo/Redo/Open/New). It is **not** called after a successful Upload (`:528-550`) or a successful Record (`OpenRecordSetupForRow` `:500-510`). That is harmless today because those paths don't change the row count. It matters for this feature (§4). |
| Layer invariant | `LayerModel.cs:67-81`, `RecordSetupWindow.xaml.cs:297-306` | `LayerCollection.Add` assigns `LayerId = CellIndex = _layers.Count`. Callers then override `CellIndex` to the row's slot. Nothing ever adds a `LayerModel` without attaching it to a row: Record probes before `Add` (M6, `:297-304`), and `RemoveLast` has no remaining caller. So **free slots = `MaxLayers - _layers.Layers.Count`**, and the occupied cells are exactly `_layers.Layers.Select(l => l.CellIndex)`. |
| Undo funnel | `MainWindow.xaml.cs:195-199` (`PushUndoSnapshot`), `ProjectSession.cs:97-101` (`CommitEdit`) | `PushUndoSnapshot` calls `_session.CommitEdit()` unless `_applyingHistory` is set. `CommitEdit` pushes one full-project snapshot **and** calls `MarkDirty()`, which raises `SaveStateChanged` only on a clean→dirty transition (`:65-73`). So one `PushUndoSnapshot()` gives one undo step and at most one title refresh. |
| Multi-part edit, one undo step (precedent) | `MainWindow.xaml.cs:541-549`, `:500-509`, `:120-127` | This is already how the code behaves. Upload does four model mutations (Add, CellIndex, Layer, Name) and then **one** push. Record does the same, plus a BPM write at `:498` that is captured by the same single push. The hosted-editor poll (`:120-127`) checks every opened FX slot and the Melodyne session in one `PollEditorChanges()`. It pushes **once per burst**, however many slots changed. |
| Timer re-entrancy | `MainWindow.xaml.cs:119-128` | `_hostedStatePollTimer` fires every 500 ms whenever the dispatcher pumps, **including during a modal dialog's nested loop**, and it can call `PushUndoSnapshot()`. Any `await` or modal call placed *between* the batch's mutations and its single push would let a partial-import snapshot slip into history. See the invariant in §4. |
| Undo after an upload | `MainWindow.xaml.cs:203-228` → `:561-576` | `ApplyRestore` → `RestoreTracksFromLayers` rebuilds all rows from the restored layers. Trailing empty rows (above the highest remaining `CellIndex`) disappear, and gap rows stay. The same will be true after undoing an import. |
| Existing toolbar/menu surface | `MainWindow.xaml:151-238` (scrolling middle of the toolbar), `:167-169` (`_Add` menu), `:303` (`AddLayerButton`, `DockPanel.Dock="Bottom"` in the fixed 460 px mixer column `:279`) | The toolbar middle is already crowded and scrolls sideways (`:151`). The Add menu has one item. The mixer column's bottom button spans the full inner width, about 440 px. |

---

## Desired behaviour

1. **Add > Import files as layers...** and a **"+ Import files..."** button (next to "+ Add layer") open one `OpenFileDialog` with `Multiselect = true` and the same media filter as Upload.
2. When the dialog is confirmed, the chosen files are **sorted by file name** (case-insensitive, full path as the tie-break). They go into the **free slots in ascending slot order**: first sorted file → lowest free slot, and so on. Free slots are existing empty rows *and* slot numbers that have no row yet. Rows are created as needed, so this works on a completely empty project.
3. Each placed file becomes a layer **exactly as per-strip Upload would make it**: same `LayerKind` rule, same `CellIndex = slot - 1`, same default name `"Layer N"`, same default trim and mix state (whatever `new LayerModel` gives).
4. **More files than free slots:** the ones that fit are imported. The rest (the alphabetically last) are skipped and named in `StatusText`: `Imported 3 files; 1 file skipped (4-layer cap reached): zeta.wav.` No dialog.
5. **Fewer files than free slots:** only that many slots are filled. Other empty rows stay empty and nothing is reported as an error.
6. **No free slots (4 layers already):** the button is disabled. The menu item stays enabled and its handler sets `StatusText = "No empty layer slots (4-layer cap reached)."` **without opening the dialog**.
7. **Undo:** one Ctrl+Z removes the **whole** import. One Ctrl+Y brings it all back.
8. **Dirty:** a confirmed import with ≥1 file placed marks the project dirty (`•` appears), through the same single `CommitEdit()`. A cancelled dialog changes nothing: no dirty flag, no undo entry, no status change.
9. Per-strip **Upload stays single-file**, with no change in behaviour.

---

## Design decisions (argued once, so the implementer doesn't re-decide)

**D1 — Entry points: an Add-menu item *and* a mixer-panel button, sharing one handler. The strip's Upload button is not repurposed.** This mirrors the existing pair "Add > Add layer" (`MainWindow.xaml:168`) and "+ Add layer" (`:303`), which share `AddLayerButton_Click`. The menu keeps it findable next to the related action, and the button puts it where layers are created, in the v8 mixer. The toolbar is ruled out: its middle section already overflows into a horizontal scroll (`:151`). Repurposing the per-strip Upload with `Multiselect = true` was considered and rejected for two reasons:
- **(a)** Upload only exists on an *already-added empty* strip (`:87`). In an empty project the user would first have to click "+ Add layer" just to reach it, and the most common use of this feature is exactly the empty project ("I have 4 takes, drop them all in").
- **(b)** "This strip gets the first file, the rest go … where?" has no obvious answer when the clicked strip is slot 3 and slot 1 is empty. A project-level action with a strict "free slots ascending" rule has no such ambiguity.

**D2 — The whole batch is ONE undo step.** This matches the codebase's existing rule, *one push per user action, after all its model mutations* (`PushUndoSnapshot` doc `:192-194`: "undo steps correspond to one user-visible change each"). Upload (`:541-549`), Record (`:500-509`) and the multi-slot hosted-editor poll (`:120-127`) all do several mutations and then push once. One push per file would put 1-3 intermediate states in history that the user never saw as separate actions. It would also mean a wrong import (wrong folder, wrong files) takes up to four Ctrl+Z presses to back out. Consequences, stated so they aren't mistaken for bugs:
- `SaveStateChanged` fires at most once.
- Undo rebuilds the rows (`RestoreTracksFromLayers`), so rows the import appended disappear, which matches single-Upload undo today.
- An empty row that existed before the import and sits above the highest remaining `CellIndex` also disappears on undo, again exactly as single-Upload undo already behaves.

**D3 — Order is by file name, not "selection order". This deviates from the default given to the user.** The default was "first selected file → first empty slot". `OpenFileDialog.FileNames` doesn't promise to return files in the order the user clicked them, and the user has no visible, reliable way to set that order in the dialog (Ctrl+A, Shift-range and Ctrl-click all behave differently). Sorting by file name makes the result predictable from the file names alone: `take1.wav, take2.wav, take3.wav` always land in layers 1, 2, 3 in that order. It also makes the skip rule easy to state ("the alphabetically last ones are skipped"). Use `StringComparer.OrdinalIgnoreCase` on `Path.GetFileName`, with the full path as the tie-break. The price is that `take10` sorts before `take2`. See Known limitations and its `TODO(polish)`.

**D4 — "Fill empty slots" means *free cells ascending*: existing empty rows are reused first, then rows are appended.** Rows are contiguous 1..N (see the table), so every free cell below N is an existing empty row, and every free cell ≥ N needs a new row. Walking free cells in ascending order and running `while (_tracks.Count <= cellIndex) AppendEmptyRow();` therefore appends **exactly one** row per new cell. It never leaves a stray empty row, because any intermediate cell would itself be free and would have been assigned first. An empty row the user added (for example, meaning to Record into it later) *will* be filled if it's the lowest free cell. That is what "first empty slot" means, and the user can Undo.

**D5 — Slot assignment is a pure engine function. The per-file attach logic is *extracted* from `UploadChoice_Click`, not reimplemented.** Cap, order and gap handling are the objective, error-prone parts, so they go in `Acapella.Engine.Project.LayerImportPlanner`, a static, side-effect-free class that is unit-testable with no WPF (§1). The per-file model/row wiring (kind, `_layers.Add`, `CellIndex`, `row.Layer`, default name) moves out of `UploadChoice_Click` into one private helper that **both** Upload and Import call. That is what guarantees point 3 of Desired behaviour ("exactly as if uploaded individually") and lets `IsAudioOnlyExtension` stay the single kind rule. `IsAudioOnlyExtension` stays where it is: moving it into the engine would be churn with no second consumer.

**D6 — The button is enabled on *free layer slots*, not on row count.** The rule is `ImportFilesButton.IsEnabled = _layers.Layers.Count < LayerCollection.MaxLayers`. It deliberately differs from `AddLayerButton`'s `_tracks.Count < MaxLayers`: four rows with one still empty *does* have room for one import. Because layer count also changes on the Upload and Record success paths, `UpdateAddLayerButtonState()` must now be called there too (§4). Otherwise the button stays enabled after the last slot fills. It wouldn't be dangerous, since the handler guard catches it, but it would be wrong.

---

## Proposed implementation

### 1. New file: `src/Acapella.Engine/Project/LayerImportPlanner.cs`

```csharp
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
```

`LayerCollection` itself is unchanged. It is still the hard cap, and it still throws if anything ever tried to exceed it.

### 2. `src/Acapella.App/MainWindow.xaml`: the menu item and the button

In the `_Add` menu (`:167-169`), after `Add _layer`:

```xml
<MenuItem Header="Import _files as layers..." Click="ImportFilesMenuItem_Click"/>
```

Replace the single `AddLayerButton` line (`:303`) with a two-button row. Only `DockPanel.Dock="Bottom"` and the top margin move from the button to the container. The 460 px column (`:279`) leaves about 440 px inside, so each button gets about 218 px.

```xml
<UniformGrid DockPanel.Dock="Bottom" Rows="1" Margin="0,8,0,0">
    <Button x:Name="AddLayerButton" Content="+ Add layer" Click="AddLayerButton_Click" Margin="0,0,4,0"/>
    <Button x:Name="ImportFilesButton" Content="+ Import files..." Click="ImportFilesMenuItem_Click" Margin="4,0,0,0"
            MouseEnter="Hint_MouseEnter" Tag="Pick several media files at once; each fills the next empty layer (up to 4)."/>
</UniformGrid>
```

The per-strip Upload button (`:90-91`) is unchanged.

### 3. `MainWindow.xaml.cs`: share the Upload logic

Add a shared filter constant next to the other fields, so the two dialogs can't drift apart:

```csharp
private const string MediaFileFilter = "Media files|*.mp4;*.mov;*.mkv;*.wav;*.mp3;*.m4a|All files|*.*";
```

Add a row factory (the same three lines as `:454-456` and `:521-523`). It's used by the import. The two existing copies **may** be switched to it, but that isn't required:

```csharp
private LayerRowViewModel AppendEmptyRow()
{
    var row = new LayerRowViewModel(_tracks.Count + 1);
    row.LiveParamChanged += DebounceRefreshPreview;
    _tracks.Add(row);
    return row;
}
```

Extract `UploadChoice_Click`'s per-file body (`:538-546`) into one helper, and have Upload call it:

```csharp
/// <summary>Attaches one uploaded media file to an empty row -- the single definition of "what an
/// upload does to the model", shared by per-strip Upload and multi-file Import (spec D5). Does NOT
/// refresh the preview, set status, or push undo: callers do those once per user action.</summary>
private void AttachUploadedFile(LayerRowViewModel row, string filePath)
{
    var kind = IsAudioOnlyExtension(Path.GetExtension(filePath))
        ? LayerKind.UploadedAudioOnly
        : LayerKind.UploadedVideo;
    var layer = _layers.Add(kind, filePath);
    // CellIndex binds to the row's own position (audit B8), not LayerCollection's insertion
    // order -- e.g. row 2 uploading before row 1 must still land in grid cell 2.
    layer.CellIndex = row.SlotNumber - 1;
    row.Layer = layer;                                               // MUST precede Name: Name's setter no-ops while Layer is null
    if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
}

private void UploadChoice_Click(object sender, RoutedEventArgs e)
{
    if (((FrameworkElement)sender).DataContext is not LayerRowViewModel row) return;

    var dialog = new OpenFileDialog { Filter = MediaFileFilter };
    if (dialog.ShowDialog() != true) return;

    AttachUploadedFile(row, dialog.FileName);
    UpdateAddLayerButtonState();                                     // new (D6)
    RefreshPreviewLive();
    StatusText.Text = $"Uploaded {row.DisplayName}: {Path.GetFileName(dialog.FileName)}";
    PushUndoSnapshot();
}
```

Upload's observable behaviour is identical. The only change is the added `UpdateAddLayerButtonState()` call.

### 4. `MainWindow.xaml.cs`: the import handler, and the enabled state

Extend `UpdateAddLayerButtonState` (`:462-463`) into a block body:

```csharp
private void UpdateAddLayerButtonState()
{
    AddLayerButton.IsEnabled = _tracks.Count < LayerCollection.MaxLayers;
    // Multi-file import: enabled while any LAYER slot is free, even if all 4 rows exist (spec D6).
    ImportFilesButton.IsEnabled = _layers.Layers.Count < LayerCollection.MaxLayers;
}
```

In `OpenRecordSetupForRow`'s success block (`:500-510`), add `UpdateAddLayerButtonState();` right after `row.Layer = dialog.CreatedLayer;` (`:505`). Upload gets the same call in §3. Undo, Redo, Open and New are already covered through `RestoreTracksFromLayers` (`:575`).

Place the new handler in the "Track sidebar: add / record / upload" region, after `UploadChoice_Click`:

```csharp
/// <summary>Add > Import files as layers... and the mixer's "+ Import files..." button: one
/// multi-select dialog; files (sorted by name, spec D3) fill free slots lowest-first up to the
/// 4-layer cap, each attached exactly like a per-strip Upload; the whole batch is ONE undo step
/// (spec D2). Overflow is reported in StatusText, never a dialog.</summary>
private void ImportFilesMenuItem_Click(object sender, RoutedEventArgs e)
{
    if (_layers.Layers.Count >= LayerCollection.MaxLayers)
    {
        StatusText.Text = "No empty layer slots (4-layer cap reached).";
        return;
    }

    var dialog = new OpenFileDialog { Filter = MediaFileFilter, Multiselect = true, Title = "Import files as layers" };
    if (dialog.ShowDialog() != true) return;

    // Occupancy is read AFTER the dialog closes, not before it opens.
    var (assignments, skipped) = LayerImportPlanner.Plan(_layers.Layers.Select(l => l.CellIndex), dialog.FileNames);

    // Invariant: from here to PushUndoSnapshot() there must be no await, MessageBox, dialog or other
    // dispatcher pumping -- _hostedStatePollTimer can push an undo snapshot on any pump, which would
    // record a half-imported project as its own undo step (breaks spec D2).
    foreach (var (cellIndex, filePath) in assignments)
    {
        while (_tracks.Count <= cellIndex) AppendEmptyRow();         // appends exactly one row per new cell (spec D4)
        AttachUploadedFile(_tracks[cellIndex], filePath);
    }

    UpdateAddLayerButtonState();
    if (assignments.Count > 0)
    {
        RefreshPreviewLive();                                        // once per batch, not per file
        PushUndoSnapshot();                                          // ONE undo step + one MarkDirty for the batch
    }

    StatusText.Text = FormatImportStatus(assignments.Count, skipped);
}

private static string FormatImportStatus(int imported, IReadOnlyList<string> skipped)
{
    static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
    string text = $"Imported {Files(imported)}";
    if (skipped.Count > 0)
        text += $"; {Files(skipped.Count)} skipped (4-layer cap reached): {string.Join(", ", skipped.Select(Path.GetFileName))}";
    return text + ".";
}
```

Notes:
- `_tracks[cellIndex]` is correct because rows are contiguous by `SlotNumber`, so `_tracks[i].SlotNumber == i + 1` (see the table). Don't search by `SlotNumber`, and don't reorder `_tracks`.
- `RefreshPreviewLive()` is `async void`. It *starts* the refresh and returns at its first `await`, before the push. The continuation runs later and touches only the preview and `StatusText`, not the model. So the invariant holds as long as nothing in the loop *itself* awaits. Keeping the order "refresh, then push" matches Upload (`:547-549`). After an import that fails to decode, `StatusText` may show `Preview error: ...` shortly after the import message, exactly as a single Upload of a bad file does today.
- The `assignments.Count > 0` guard is defensive. With the pre-dialog guard and a confirmed dialog (which always has ≥1 file) it's always true, but it keeps "nothing imported ⇒ no undo entry and no dirty mark" local and obvious.
- There is no export-in-flight gate and no preview stop. Upload has neither, the batch is the same kind of edit, and `SnapshotForExport` is a deep copy (M7).
- Don't touch `_applyingHistory` or `ApplyRestore`. This is a forward edit, not a restore.

### 5. Deliberately **not** changed

- `LayerCollection` / `MaxLayers` / the `Add` throw: the cap stays single-sourced and hard.
- The per-strip Upload stays single-select (D1). Its hint text is unchanged.
- `ProjectSession`: `CommitEdit` already records the undo step and marks dirty (D2). No new API.
- `RestoreTracksFromLayers`, `ApplyRestore`, `PushUndoSnapshot`: no edits.
- `AddLayerButton`'s enable rule stays row-based. Add > Add layer stays always enabled.
- `IsAudioOnlyExtension` stays private in MainWindow (D5).
- No drag-and-drop onto the window. It wasn't asked for, and it's a natural follow-up that would reuse `LayerImportPlanner` plus the same loop.

---

## Tests — `tests/Acapella.Engine.Tests/Project/LayerImportPlannerTests.cs` (new)

These are plain `[Fact]` tests with no fixture, no WPF, no native DLL and no real files, because the planner does no I/O. Paths can be fake strings such as `@"C:\takes\b.wav"`.

1. **`EmptyProject_FillsCellsInAscendingOrder_SortedByFileNameIgnoringCase`**: no occupied cells, files `[@"C:\x\c.wav", @"C:\x\A.mp4", @"C:\x\b.m4a"]` → assignments `(0, A.mp4)`, `(1, b.m4a)`, `(2, c.wav)`, and nothing skipped.
2. **`SkipsOccupiedCells_FillingGapsFirst`**: occupied `{0, 2}`, files `[x, y]` → `(1, x)`, `(3, y)`.
3. **`MoreFilesThanFreeCells_SkipsTheAlphabeticallyLast`**: occupied `{0, 1, 2}`, files `[d, a, c]` → one assignment `(3, a)`, and skipped == `[c, d]` in that order.
4. **`NoFreeCells_AssignsNothing_SkipsEverything`**: occupied `{0, 1, 2, 3}`, files `[a, b]` → no assignments, and skipped == `[a, b]`.
5. **`FewerFilesThanFreeCells_OnlyFillsThatMany`**: no occupied cells, files `[a]` → exactly `(0, a)`.
6. **`NeverAssignsMoreThanMaxLayers`**: no occupied cells, 10 files → `assignments.Count == LayerCollection.MaxLayers`, cells `0..MaxLayers-1` distinct and ascending, 6 skipped. This guards the locked 4-layer cap.
7. **`SameFileNameInDifferentFolders_OrdersByFullPath`**: `[@"C:\b\take.wav", @"C:\a\take.wav"]` → the `C:\a\` one gets cell 0. This guards determinism.

**No App.Tests `[StaFact]` for the MainWindow wiring, deliberately.** An import dirties the session, and `MainWindow`'s `Closing` handler (`MainWindow.xaml.cs:167-175`) then calls `ConfirmDiscardUnsavedChanges()`, which calls `MessageBox.Show`. That blocks the STA test thread in the test's `finally { window.Close(); }`. Undo doesn't clear dirty (Save-affordances D1), and nothing test-accessible does, short of reflection into `_session`. The wiring (row reuse and append, the single push, button state) is therefore **[human]**-verified below. The logic most likely to be wrong (cap, order, gaps) is covered by the tests above.

**Regression carve-out (per CLAUDE.md):** this touches `UploadChoice_Click`, `OpenRecordSetupForRow` and `UpdateAddLayerButtonState` in MainWindow, and changes `MainWindow.xaml`'s mixer-button layout. Re-run the whole `Acapella.App.Tests` project once (`TransportLayoutTests`, `MixingScreenTests`), since both construct `MainWindow` and the layout test covers min-size bounds. No engine code already covered by tests changes. `Project/*` is re-run anyway because the new test file lives there.

---

## Acceptance criteria

- **[auto]** The 7 `LayerImportPlannerTests` pass. `Acapella.App.Tests` and `Project/*` still pass. The solution builds with no new warnings.
- **[human]**
  - Fresh app (0 rows): Import 3 files named `take1.wav`, `take2.mp4`, `take3.m4a` → three strips appear, Layer 1-3, in that order. The composite shows the video in cell 2. The title and toolbar gain `•`. StatusText reads `Imported 3 files.`
  - **One Ctrl+Z removes all three**, and the strips disappear. One Ctrl+Y restores all three.
  - Project with layers in slots 1 and 3 and an empty row 2 → Import 3 files → row 2 and a new row 4 are filled, and StatusText names the 1 skipped file.
  - With 4 layers: "+ Import files..." is disabled. Add > Import files as layers... shows `No empty layer slots (4-layer cap reached).` and no dialog.
  - With 4 rows but one empty: the button is **enabled**, and importing 2 files fills the empty row and reports 1 skipped.
  - After a per-strip Upload or a Record fills the last free slot, the Import button is disabled straight away.
  - Cancelling the Import dialog: no `•`, no undo entry, StatusText unchanged.
  - Per-strip Upload still picks exactly one file and behaves as before.
  - The file-name ordering matches what the user expects in practice (this confirms the D3 deviation is acceptable).
  - The "+ Add layer" / "+ Import files..." pair sits cleanly at the bottom of the mixer column (the one layout judgement).

---

## Known limitations (accepted, not part of this ticket)

- **Plain case-insensitive name sort:** `take10` sorts before `take2`. Leave the `TODO(polish)` on the sort. The fix is a natural-order comparer (for example `StrCmpLogicalW`, or a small digit-run comparer). It's cosmetic until someone numbers takes past 9, and with a 4-layer cap that's rare.
- **No media validation, same as Upload.** A non-media file picked through "All files" is imported as `UploadedVideo` and fails later as `Preview error: ...`. A `MediaProbe.HasNonzeroDuration` pre-check exists (`RecordSetupWindow.xaml.cs:297`), but it would change Upload's behaviour too. That is a separate ticket.
- **Undo drops trailing empty rows** (D2), exactly as single Upload undo does today.
- **Pre-existing, not caused by this ticket: stale hosted-plugin instances when `LayerId` is reused after an undo.** `LayerId = _layers.Count` (`LayerModel.cs:74`), and `ReleaseAllHostedInstances` runs only on Open/New (`ProjectSession.cs:137, 149`). Import, then open a layer's FabFilter editor and tweak it, then undo the import, then import again: the new layer with the same `LayerId` reconnects to the old live instance. Single Upload → undo → Upload has the same hole today.
- **Pre-existing, observed while reading, not in scope: Record doesn't reuse empty rows.** Toolbar ⏺ and Tools > Recording setup (`MainWindow.xaml.cs:513-526`, `:735`) always *append* a new row. They never reuse an existing empty row, and they refuse at 4 rows even when one is empty. So the ⏺ hint "for the next empty layer" (`MainWindow.xaml:206`) is inaccurate. The fix could reuse this spec's "lowest free cell" rule, but it belongs to a separate ticket.
