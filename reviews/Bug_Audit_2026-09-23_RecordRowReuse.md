# Bug Audit #7: ⏺, Recording setup and Calibrate latency add a layer row before the dialog opens and never remove it

> Audit as of `691c1b5`, after tonight's landings: Bug Audit #5 (`ReleaseAll` on New/Open), save affordances, multi-file import and Bug Audit #6 (recording filenames). One bug is specced in full: Bug Audit #6's runner-up #2. The other lead handed to this pass, runner-up #3 (a stale hosted instance after an undo frees a `LayerId`), was re-evaluated against current code. It is **deliberately not bundled**. The reasons are under *Runner-up #3: left open* at the end.
>
> **Not a re-report, but a promotion.** Two earlier docs mention this bug in one or two lines. `reviews/Feature_Spec_2026-09-23_MultiFileImport.md:307` says "Record doesn't reuse empty rows". `reviews/Bug_Audit_2026-09-23_RecordingFileOverwrite.md:199-204` rates it runner-up #2. This ticket re-checks it against current source and specs the fix. **Drift from the runner-up text:**
> - The runner-up's line numbers are still exact.
> - Its "permanent stray row" wording is **overstated**. A successful Undo, Redo, New or Open drops trailing empty rows (§Root cause 4). The stray rows are only really permanent in a project with nothing to undo, such as a fresh launch.
> - The runner-up missed two things, and both are *worse* than what it described:
>   - **Repro C:** a stray row pushes the next take into the wrong slot, and undo can't fix that.
>   - **Repro D:** a project with 4 real layers can never reach latency calibration.
> - `AppendEmptyRow()` (`MainWindow.xaml.cs:535-541`) was added tonight for multi-file import. The buggy handler still has its own inline copy of that code.
>
> **Confidence: high on the mechanism and on the fix.** Every link below comes from reading current source. No part of the defect needs a camera: Repros A, B and D only need you to open the dialog and cancel it, or open it and calibrate. Repro C needs one real take and is a **[human]** step. The pure part of the fix (free-cell selection) is unit-tested. The App wiring runs through a modal `ShowDialog()` that a test host can't drive, so it is **[human]**-verified, the same way as in Bug Audit #6 and the multi-file spec. There is one load-bearing ordering rule in the fix (§Proposed fix, step 3). It is spelled out with a counter-example because a quick implementation gets it wrong.

---

## The bug in one sentence

The toolbar ⏺ button, Tools > Recording setup... and Tools > Calibrate latency... all run the same handler. That handler appends a new empty row to `_tracks` *before* the dialog opens, never reuses an existing empty row, and never removes the row it appended. So:
- every cancelled recording and every calibration leaves a stray empty strip that isn't in undo history;
- the next take goes into the wrong slot;
- after four such strips, all three entry points refuse with "Layer cap reached (4)." even when the project has no layers at all.

---

## Root cause

Four facts combine to produce the defect.

### 1. The row is created before the dialog, unconditionally, and is never rolled back

**File:** `src/Acapella.App/MainWindow.xaml.cs:520-533`

```csharp
private void RecordingSetupMenuItem_Click(object sender, RoutedEventArgs e)
{
    if (_tracks.Count >= LayerCollection.MaxLayers)          // :522  row count, not free slots
    {
        StatusText.Text = "Layer cap reached (4).";
        return;
    }

    var row = new LayerRowViewModel(_tracks.Count + 1);      // :528  always a NEW row
    row.LiveParamChanged += DebounceRefreshPreview;
    _tracks.Add(row);                                        // :530  before the dialog even opens
    UpdateAddLayerButtonState();
    OpenRecordSetupForRow(row);                              // :532
}
```

`OpenRecordSetupForRow` (`:500-518`) only touches `row` inside `if (result == true && dialog.CreatedLayer is not null)` (`:506`). When the dialog closes any other way, nothing happens to the row: Cancel (`RecordSetupWindow.xaml.cs:319-323`), the ✕ button, a failed M6 probe followed by a close (`:305-310`), or a calibrate-only session. Nothing removes it and nothing records it. The row has no `LayerModel`, so it isn't part of the `ProjectFileDto` snapshot, and an undo entry couldn't hold it even if one were pushed. The same reasoning is written out in the comment on "+ Add layer" (`:460-461`), where an empty row is the *intended* result. Here it isn't.

### 2. Three entry points share that handler, and one of them is not a recording action at all

- **Toolbar ⏺:** `RecordButton_Click` (`MainWindow.xaml.cs:802`) → `RecordingSetupMenuItem_Click`.
- **Tools > Recording setup...:** `MainWindow.xaml:178` → `RecordingSetupMenuItem_Click`.
- **Tools > Calibrate latency...:** `MainWindow.xaml:179` → **`RecordingSetupMenuItem_Click`**.

Calibration is fully self-contained inside the dialog. `CalibrateButton_Click` (`RecordSetupWindow.xaml.cs:87-105`) measures and saves a settings value and never touches `_layers`. So every use of Calibrate latency adds a strip for a layer that nobody meant to create.

### 3. The cap guard counts rows, not free layer slots, and the handler never reuses an empty row

`:522` refuses when `_tracks.Count >= 4`. Empty rows count toward that total. Rows can be empty for three legitimate reasons, besides this bug's stray rows:
- "+ Add layer" (`:448-462`);
- gap rows rebuilt by `RestoreTracksFromLayers` (`:628-643`);
- a row whose take failed M6.

The handler never looks for an existing empty row either. It always does `_tracks.Count + 1`. Compare the two paths that were fixed tonight:
- Multi-file import gates on `_layers.Layers.Count < MaxLayers` (spec D6, `:468`, `:579`) and fills free cells lowest-first through `LayerImportPlanner` (`:589-598`).
- The dialog's *own* guard at `RecordSetupWindow.xaml.cs:130` is also layer-based.

Only this handler is still row-based. The UI text claims otherwise:
- `MainWindow.xaml:207` (⏺ tooltip): "Open the recording setup dialog for the next empty layer."
- `MainWindow.xaml:197-200` (XAML comment): "... for the next open slot."
- `MainWindow.xaml.cs:800-801` (doc comment): "... for the next empty layer slot."

None of these is true today.

### 4. Why the stray rows are *mostly* but not fully permanent

`ApplyRestore` (`MainWindow.xaml.cs:205-230`) → `RestoreTracksFromLayers` (`:628-643`) rebuilds rows `1..maxCellIndex+1` from the layers. **Trailing** empty rows therefore disappear on any successful Undo, Redo, File > New or File > Open. But:
- In a project with nothing to undo (a fresh launch, or straight after New or Open), `ProjectUndoStack.Undo()` returns `null` (`src/Acapella.Engine/Persistence/ProjectUndoStack.cs:56-59`). `ProjectSession.Undo()` then returns `false` (`ProjectSession.cs:104-109`), and `ApplyRestore` returns before `RestoreTracksFromLayers` runs. **Ctrl+Z does nothing and the rows stay.**
- In a project with history, Ctrl+Z does clear trailing stray rows, but only by **also undoing the user's last real edit**. That is not a fix the user can reasonably be expected to find.
- A stray row that has a real layer in a row *after* it is not trailing. `RestoreTracksFromLayers` recreates it as a gap row every time (Repro C), so undo never clears it.

---

## Repro A: four cancels lock out ⏺ in an empty project

1. Launch the app, so the project is empty and has no undo history.
2. Press ⏺ and Cancel the dialog. Repeat 4 times. There are now 4 empty strips in the mixer, and "+ Add layer" is greyed out (`:466`).
3. Press ⏺ again. Status: **"Layer cap reached (4)."** Tools > Recording setup... and Tools > Calibrate latency... give the same refusal. **The project has zero layers.**
4. Press Ctrl+Z. Nothing happens (§Root cause 4).

Escape routes: the strips' own Rec, Upload and Import still work, because they are layer-based or use an existing row. File > New also clears the rows, because an untouched project isn't dirty, so there's no prompt. Nothing about the UI points the user to either route.

## Repro B: Calibrate latency, repeatedly

In any project with *k* layers, each Tools > Calibrate latency... → Calibrate → Close adds one empty strip. After `4 - k` calibrations, every entry point from Repro A refuses, including Calibrate itself.

## Repro C: one cancel sends the next take into the wrong slot (**[human]**, needs a camera; undo can't fix it)

1. Fresh launch. Press ⏺ and Cancel. Row 1 is now empty.
2. Press ⏺ again. The guard sees `_tracks.Count == 1` and passes, so a **row 2 is appended** (`:528-530`). Record a take and Stop.
3. `OpenRecordSetupForRow` sets `CellIndex = row.SlotNumber - 1 = 1` (`:510`) and `Name = "Layer 2"` (`:512`). Status: "Recorded Layer 2."

Observable results:
- The project's only layer is persisted with `CellIndex: 1` and the name "Layer 2", and it gets cell 2's colour (`LayerColorPalette.GetColor(CellIndex)`, `PreviewPlaybackEngine.cs:293`).
- The mixer shows an empty strip 1 in front of it.
- Undo back to empty, then Redo: `RestoreTracksFromLayers` rebuilds rows 1 (empty) and 2 from `CellIndex`. The gap survives undo, redo, save and reopen.

(Scope note: `Layout2x2Provider.GetCellRects` (`src/Acapella.Engine/Composite/Layout2x2Provider.cs:11-25`) returns the first *N* rects, so a lone layer at `CellIndex` 1 is still *drawn* top-left. What is wrong is the slot the layer is filed under, plus its name, colour and mixer position, not which quadrant it's drawn in. The compaction is existing compositor behaviour and is not part of this ticket.)

## Repro D: a full project can never calibrate latency

With 4 real layers, `_tracks.Count == 4`, so Tools > Calibrate latency... refuses at `:522`. The strips' Rec buttons are hidden on occupied strips (`MainWindow.xaml:87`, `Visibility="{Binding HasSource, Converter=InverseBoolToVis}"`). ⏺ and Recording setup refuse too. **No path reaches the dialog's Calibrate button**, even though calibration needs no free slot. The dialog's own Record guard (`RecordSetupWindow.xaml.cs:130`) would already stop a fifth recording on its own.

---

## Why the current suite misses it

- Every entry point runs through `RecordSetupWindow.ShowDialog()`, a modal WPF dialog. `tests/Acapella.App.Tests` constructs `MainWindow` headlessly (`MixingScreenTests.cs:20`), but it never clicks a button that opens a dialog, and it couldn't, because `ShowDialog` blocks the STA thread.
- `LayerImportPlannerTests` covers free-cell selection only through `Plan(...)`. Nothing calls it for the Record path, because the Record path doesn't use it.

---

## Proposed fix

**Chosen approach: decide the target cell before the dialog, create or reuse the row only after a take succeeds, and give Calibrate its own handler that never claims a row.** Two alternatives were rejected:
- **Keep pre-creating the row, but remove it on cancel.** This fixes Repros A and B but not Repro C, because the handler would still append instead of reusing. It also needs care on every close path (Cancel, ✕, M6 failure then close). Choosing the row after success has no rollback to get wrong.
- **Reuse the lowest empty row but still claim it before the dialog.** This would be harmless for an existing empty row, but it still has to append (and later roll back) when no empty row exists. It gains nothing over choosing after success.

No project-file format change, no engine behaviour change and no new dependency. `RecordSetupWindow` is **not** modified.

### 1. `src/Acapella.Engine/Project/LayerImportPlanner.cs`: extract `FreeCells`, and have `Plan` use it

"Lowest free slot" should have one definition. `Plan` already computes it at `:15-16`. Extract it:

```csharp
/// <summary>Unoccupied grid cells in ascending order, never beyond LayerCollection.MaxLayers. The one
/// definition of "free slot", shared by Plan (multi-file import) and the Record entry points (bug
/// audit #7), so both fill the lowest free cell first.</summary>
public static IReadOnlyList<int> FreeCells(IEnumerable<int> occupiedCellIndices)
{
    var occupied = occupiedCellIndices.ToHashSet();
    return Enumerable.Range(0, LayerCollection.MaxLayers).Where(c => !occupied.Contains(c)).ToList();
}
```

In `Plan`, replace `:15-16` with `var freeCells = FreeCells(occupiedCellIndices);`. The rest of `Plan` is unchanged. (`freeCells[i]` and `freeCells.Count` work as they do today.)

**Why reuse this and not add a new row-based helper:** row semantics and cell semantics are the same here. The multi-file spec established that rows are contiguous and slot-numbered (`Feature_Spec_2026-09-23_MultiFileImport.md:22`: `_tracks[i].SlotNumber == i + 1`). Every attached layer has `CellIndex == row.SlotNumber - 1` (`MainWindow.xaml.cs:510`, `:554`, `:637-638`). So "lowest empty row, else append" is exactly "lowest free cell". The spec's D4 argument (`:61`) carries over unchanged: walking to the lowest free cell with `while (_tracks.Count <= cell) AppendEmptyRow();` appends **at most one** row and never leaves a stray row. Every cell below the lowest free cell is occupied, and therefore already has a row.

**Do not** call `Plan(occupied, new[] { "" })` with a dummy path to get the first cell. It works, but it hides the intent. The class keeps its `...ImportPlanner` name, and renaming it is out of scope.

### 2. `src/Acapella.App/MainWindow.xaml.cs`: resolve the row after the dialog, never before

Replace `OpenRecordSetupForRow` (`:497-518`) and `RecordingSetupMenuItem_Click` (`:520-533`) with the following. The success-path body is the existing `:508-516`, moved across without changes.

```csharp
/// <summary>Opens the capture-setup dialog. If a take is recorded, attaches it to the row that
/// resolveRow returns. resolveRow runs ONLY on success, AFTER the dialog closes, so a cancelled or
/// calibrate-only session never creates or claims a row (bug audit #7).</summary>
private void OpenRecordSetup(Func<LayerRowViewModel?> resolveRow)
{
    var dialog = new RecordSetupWindow(_deviceCatalog, _settingsService, _layers, _mixEngine, _mediaDir, _session.MetronomeBpm) { Owner = this };
    bool? result = dialog.ShowDialog();
    _session.MetronomeBpm = dialog.Bpm;

    if (result == true && dialog.CreatedLayer is not null)
    {
        var row = resolveRow();
        if (row is null) return;   // unreachable in practice: the dialog refuses to record at the cap (RecordSetupWindow.xaml.cs:130)

        // CellIndex binds to the row's own position (audit B8), not LayerCollection's
        // insertion order -- e.g. row 2 recording before row 1 must still land in grid cell 2.
        dialog.CreatedLayer.CellIndex = row.SlotNumber - 1;
        row.Layer = dialog.CreatedLayer;
        if (string.IsNullOrEmpty(row.Name)) row.Name = $"Layer {row.SlotNumber}";
        UpdateAddLayerButtonState();
        RefreshPreviewLive();
        StatusText.Text = $"Recorded {row.DisplayName}.";
        PushUndoSnapshot();
    }
}

/// <summary>A strip's own Rec button: records into that strip.</summary>
private void OpenRecordSetupForRow(LayerRowViewModel row) => OpenRecordSetup(() => row);

/// <summary>Toolbar ⏺ and Tools > Recording setup...: records into the lowest free layer slot,
/// reusing an existing empty strip before appending one (bug audit #7).</summary>
private void RecordingSetupMenuItem_Click(object sender, RoutedEventArgs e)
{
    int? targetCell = LowestFreeCell();   // read BEFORE ShowDialog -- see bug audit #7 step 3
    if (targetCell is null)
    {
        StatusText.Text = "Layer cap reached (4).";
        return;
    }
    OpenRecordSetup(() => RowForCell(targetCell.Value));
}

/// <summary>Tools > Calibrate latency...: opens the same dialog for its Calibrate button. It never
/// creates or claims a row and is not blocked by the layer cap, because calibration is not a
/// layer. If the user records from here anyway, the take goes to the lowest free slot, as with ⏺.</summary>
private void CalibrateLatencyMenuItem_Click(object sender, RoutedEventArgs e)
{
    int? targetCell = LowestFreeCell();   // same ordering rule; null (a full project) is fine here
    OpenRecordSetup(() => targetCell is int cell ? RowForCell(cell) : null);
}

private int? LowestFreeCell()
{
    var free = LayerImportPlanner.FreeCells(_layers.Layers.Select(l => l.CellIndex));
    return free.Count > 0 ? free[0] : null;
}

/// <summary>The row for a free grid cell: the existing empty row if there is one, else one appended
/// row (rows are contiguous by SlotNumber, so at most one append -- multi-file spec D4).</summary>
private LayerRowViewModel RowForCell(int cellIndex)
{
    while (_tracks.Count <= cellIndex) AppendEmptyRow();
    return _tracks[cellIndex];
}
```

Notes:
- The pre-dialog `UpdateAddLayerButtonState()` (`:531`) goes away with the pre-dialog append. The success path already calls it (`:513` in the moved block), after `RowForCell` may have appended.
- `RecordChoice_Click` (`:473-477`) and `RecordButton_Click` (`:802`) keep their call sites. Only their targets' bodies change.
- **The guard is now layer-based** (`LowestFreeCell() is null` means `_layers.Layers.Count == 4`), which matches import's D6 and the dialog's own `:130`. With the old `_tracks.Count` guard, four empty strips added with "+ Add layer" would still block ⏺. Don't keep it.
- `AddLayerButton`'s own `_tracks.Count` rule (`:466`) is **unchanged**. "+ Add layer" adds *rows*, so a row cap is correct for it.
- Leave `ImportFilesMenuItem_Click`'s inline `while` loop (`:596-597`) alone. It could call `RowForCell` too, but this ticket doesn't touch the import path.

### 3. The load-bearing ordering rule: read free cells **before** `ShowDialog`, never after

This is the part a "simplification" would break, so the reason goes in a code comment and here.

On success, `RecordSetupWindow.StopRecording` has **already** called `_layers.Add(...)` (`RecordSetupWindow.xaml.cs:312`) before `ShowDialog` returns. `LayerCollection.Add` gives the new layer a *temporary* `CellIndex = _layers.Count` (`src/Acapella.Engine/Project/LayerModel.cs:77`), and MainWindow only overwrites it at `:510`. If free cells are read after the dialog, that temporary index is counted as occupied.

**Counter-example:** the project has one layer, in cell 0. A take is recorded, and `Add` gives it a temporary `CellIndex` of 1. A post-dialog read sees `{0, 1}` occupied and picks cell **2**. The take lands in row 3, behind an empty row 2. That is Repro C's defect, reintroduced by the fix.

Reading before the dialog is safe because nothing else can change occupancy while the dialog is modal:
- MainWindow's input and its Ctrl+Z/Ctrl+Y command bindings don't receive input while a modal child is active.
- The only timer that runs, `_hostedStatePollTimer` (`:121-130`), only pushes snapshots and never adds or removes layers.

(If the resolution is ever moved after the dialog on purpose, it must exclude `dialog.CreatedLayer`'s cell. Reading before is simpler.)

### 4. `src/Acapella.App/MainWindow.xaml`: rewire Calibrate, fix the stale text

- `:179`: change `Click="RecordingSetupMenuItem_Click"` to `Click="CalibrateLatencyMenuItem_Click"`.
- `:207` (⏺ tooltip): leave it as it is. "Open the recording setup dialog for the next empty layer." is now true.
- `:197-200` (XAML comment): "...for the next open slot." is now true. Leave it.
- `MainWindow.xaml.cs:497-499`: the old doc comment on `OpenRecordSetupForRow` ("Shared by ... 'Calibrate latency...' entries") is replaced by the doc comments above.
- `MainWindow.xaml.cs:800-801`: "for the next empty layer slot" is now accurate. Optionally add "(bug audit #7: reuses an empty strip before appending)".

### 5. Deliberately **not** changed

- `RecordSetupWindow` (its guard, M6, `Add` and `CellIndex` handling) and `LayerCollection.Add`'s temporary `CellIndex`.
- Undo semantics. An empty row is still not an undo step: it has no `LayerModel` (`:460-461`). A successful take is still exactly one undo step (`PushUndoSnapshot` in the success block).
- The compositor's compaction of gap cells (Repro C scope note).
- Existing projects already saved with a gap caused by Repro C. They load as they were saved (gap row included), and no migration is attempted.

---

## Tests: `tests/Acapella.Engine.Tests/Project/LayerImportPlannerTests.cs` (append to the existing class)

These are plain `[Fact]`s with no WPF, devices or files.

1. **`FreeCells_EmptyProject_IsEveryCellAscending`**: `FreeCells(Array.Empty<int>())` → `[0, 1, 2, 3]`.
2. **`FreeCells_GapBelowHighestOccupied_ComesFirst`**: `FreeCells(new[] { 0, 2 })` → `[1, 3]`. This is the "reuse the lowest empty strip before appending" rule that ⏺ now depends on. The old handler would have appended a new row instead (a third strip, slot 3).
3. **`FreeCells_OccupiedInAnyOrder_IsSortedAscending`**: `FreeCells(new[] { 3, 1 })` → `[0, 2]`. Pins that the order comes from `Enumerable.Range` and not from the input.
4. **`FreeCells_AllOccupied_IsEmpty`**: `FreeCells(new[] { 2, 0, 3, 1 })` → empty. This is the only condition under which ⏺ and Recording setup now refuse.

**No automated test for the MainWindow wiring, deliberately.** Every path goes through the modal `RecordSetupWindow.ShowDialog()`. Testing it would mean adding a dialog-factory seam to `MainWindow` that this ticket doesn't otherwise need. The wiring is **[human]**-verified below, the same reasoning as `Bug_Audit_2026-09-23_RecordingFileOverwrite.md:180` and the multi-file spec. Repros A, B and D need no camera: cancelling or calibrating is enough.

**Regression carve-out (per CLAUDE.md):** `Plan` now calls the extracted `FreeCells`. Re-run the 7 existing `LayerImportPlannerTests` once, and the rest of `Acapella.Engine.Tests`. Also run `Acapella.App.Tests` once, since `MainWindow.xaml` changed and those tests construct `MainWindow`.

---

## Acceptance criteria

- **[auto]** The 4 new `FreeCells_*` tests pass. The 7 existing `LayerImportPlannerTests`, the rest of `Acapella.Engine.Tests` and `Acapella.App.Tests` still pass. The solution builds with no new warnings.
- **[human]** (Repros A, B and D need no camera; C needs one take. **Do not delete anything in `media/`**):
  - Repro A: in a fresh launch, press ⏺ → Cancel 5 times. **No strip appears**, and the 5th ⏺ still opens the dialog. Same for Tools > Recording setup....
  - Repro B: Tools > Calibrate latency... → Calibrate → Close, 5 times. **No strip appears**, and "Calibrated offset: ... ms" shows each time.
  - Repro D: with 4 real layers (Import 4 files is quickest), Tools > Calibrate latency... **opens the dialog** and Calibrate works. ⏺ and Recording setup still refuse with "Layer cap reached (4)." Pressing Record inside the Calibrate-opened dialog shows "Layer cap reached (4)." and no strip or layer is added. Without a camera you'll see "Select a camera and microphone first." instead, because that check runs first (`RecordSetupWindow.xaml.cs:124`). Either result is a pass.
  - Reuse: "+ Add layer" twice (2 empty strips), then ⏺ → record. The take lands in **strip 1** as "Layer 1", and no third strip appears. Then "+ Add layer" until there are 4 strips with 1 filled, then ⏺. The dialog **opens** (the old row-count guard refused here) and the take lands in strip 2.
  - Repro C itself: fresh launch, ⏺ → Cancel, then ⏺ → record. The take lands in **strip 1** as "Layer 1", and exactly one strip exists. (The old code gives "Layer 2" in strip 2 behind an empty strip 1.)
  - The ordering rule (step 3): with one layer in slot 1, press ⏺ → record. The take lands in **strip 2** as "Layer 2", with no empty strip between them. Then Ctrl+Z → Ctrl+Y: still strips 1 and 2, no gap.
  - Per-strip Rec on an empty strip 3 (with strips 1 and 2 empty) still records into strip 3, and the strip's own choice is respected.

---

## Runner-up #3: left open (still not part of any ticket)

**Stale hosted instance after an undo frees a `LayerId`** (Bug Audit #6 runner-up #3; multi-file spec's known limitation). This was re-checked against current source. The rating stands: *real and reachable, but usually harmless*. **It is not bundled here**, and it is not the one-line addition to Bug Audit #5's `ReleaseAll` machinery that it might look like:

- **Bug Audit #5 ruled it out explicitly.** `Bug_Audit_2026-09-23_ProjectSwitchPluginState.md:172` says: "Do **not** add the call to `Undo()`/`Redo()`." The reason given was that releasing on undo closes the user's open editor windows. A *per-dropped-id* release narrows this to the dropped layer's editors, which is arguably correct behaviour. But it goes against a written decision, so it needs its own ticket.
- **Releasing on undo reintroduces Bug Audit #5 §5's use-after-free.**
  - New and Open were made safe only by making their callers `await _previewEngine.StopAsync()` and refuse during export (`MainWindow.xaml.cs:934-938`, `:952`, `:1006-1016`).
  - `PerformUndo` and `PerformRedo` (`:237-247`) are synchronous and do neither. Ctrl+Z during playback is a normal action.
  - Releasing the dropped layer's `(LayerId, Stage)` instances from inside `ProjectSession.Restore` would free native memory that the WASAPI render thread may be calling `ProcessBlock` on at that moment, or that an in-flight export is using.
  - A safe fix has to either stop preview and gate export on every layer-dropping undo (a UX change), or defer the release until the chain is rebuilt. Neither is a one-liner.
- **The per-layer API doesn't exist.**
  - `HostedPluginService.Release(layerId, stage)` exists (`HostedPluginService.cs:118`). There is no per-layer release for `_araLayerSessions` (`:138`), whose own comment (`:124-126`) still says it is "never released mid-session".
  - `MixEngine` would also need per-id removal from `_melodyneBackends` and `_layerTaps` (compare `ReleaseAllHostedInstances`, `MixEngine.cs:130-135`).
  - `ProjectSession.Restore` (`ProjectSession.cs:157-170`) would need a before/after id diff.
  - That is about four files in the engine hosting subsystem, and this ticket touches none of them.
- **It is a different subsystem with a different risk profile.** This ticket is App-side row bookkeeping, with one pure engine helper, and its [human] checks need no plugins. Runner-up #3 is native plugin lifetime, and its checks need FabFilter and a live editor. Bundling them would double this ticket's test surface for a bug that only shows up if the user keeps tweaking an orphaned editor window, or tweaks within 500 ms of opening it (Bug Audit #6 runner-up #3's analysis, unchanged).

**Suggested shape for its own ticket, later:** in `ProjectSession.Restore`, diff the layer ids before and after the restore. For each dropped id, *defer* the release to the next point where no chain can be using it: the next `SetLayersAsync`/`RefreshAsync` rebuild after preview has stopped, or the next New/Open. Don't release inline. Add `HostedPluginService.ReleaseLayer(layerId)`, which covers every stage plus that layer's ARA session. Tests go through `FakeHostedPluginFactory`, the same way as Bug Audit #5's.
