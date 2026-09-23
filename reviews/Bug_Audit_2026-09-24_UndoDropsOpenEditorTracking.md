# Bug Audit #9: After an Undo/Redo, a still-open FabFilter editor is no longer tracked

> Audit as of `aa834a4` (retake-in-place fully landed). One new bug is specced in full. It has not been flagged in any earlier `reviews/*.md` doc.
>
> **How it differs from Bug Audit #6/#7/#8 runner-up #3** ("stale hosted instance after an undo frees a `LayerId`"):
> - Runner-up #3 is about a layer that the undo **removes**.
> - This bug is about a layer that **survives** the undo. It keeps the same `LayerId`, the same live instance and the same open editor window, but its editor stops being tracked.
> - The fix below leaves runner-up #3 exactly as it is. A layer the undo drops has no rebuilt row, so nothing is carried for it.
>
> **Confidence:**
> - **High on the mechanism.** It is deterministic, every link is cited from current source, and a VM-level headless test reproduces it.
> - **Medium on how often users hit it.** Seeing it needs a real FabFilter editor left open across a Ctrl+Z, then more tweaking in that window. That is the retake spec's own [human] flow (`Feature_Spec_2026-09-24_RetakeInPlace.md:592`), so it is a normal workflow, but I have not watched it happen at runtime.
> - **Medium on one runtime assumption the fix makes:** Pro-Q must return stable `GetState()` bytes right after a `SetState` (§Ordering subtleties, subtlety 2). A [human] check covers it, and there is a fallback if it fails.
>
> The fix is App-only: one new helper on `LayerRowViewModel` and two lines in `MainWindow.ApplyRestore`. It has **four ordering and seeding rules, plus one matching rule** (§Ordering subtleties). Each has a counter-example, because the obvious implementation gets at least two of them wrong.

---

## The bug in one sentence

Every Undo/Redo rebuilds the mixer-strip rows from scratch, and the new rows start with empty editor tracking. A FabFilter editor window left open across the Ctrl+Z is therefore no longer polled. Tweaks made in it afterwards:
- create no undo step;
- don't mark the project dirty;
- don't refresh the preview;
- get silently folded into, or wiped by, the next unrelated undo/redo step.

This also means closing the app after Undo → Save → tweak loses the tweak without a save prompt.

---

## Root cause

Four facts combine to produce the defect.

### 1. Editor-change detection is per row, and only for slots that row opened

**File:** `src/Acapella.App/ViewModels/LayerRowViewModel.cs`

| Line | What it does |
|---|---|
| `:38-39` | `_openedHostedSlots` (a `HashSet<FxSlot>`) and `_lastPolledHostedState` (the baseline bytes per slot) are **instance fields of the row**. |
| `:327-334` | `OpenHostedEditor` is the **only** place that adds to `_openedHostedSlots` (`:332`) and seeds the baseline (`:333`). |
| `:353-356` | `PollHostedStateChanges` returns `false` immediately when `_openedHostedSlots.Count == 0`. |
| `:359-371` | For each opened slot it pulls live state and compares it to the baseline. On a difference it writes the state into `Params` (`:368`), updates the baseline and reports `changed`. |

**File:** `src/Acapella.App/MainWindow.xaml.cs:121-129`. The 500 ms `_hostedStatePollTimer` calls `_selectedLayer?.PollEditorChanges()`. Only a `true` result leads to `DebounceRefreshPreview()` and `PushUndoSnapshot()`, and it is `PushUndoSnapshot()` → `ProjectSession.CommitEdit` (`ProjectSession.cs:97-101`) that creates the undo step and calls `MarkDirty()`.

### 2. Undo/Redo replaces every row with a new, untracked one

`PerformUndo` / `PerformRedo` (`MainWindow.xaml.cs:237-247`) → `ApplyRestore` (`:205-230`) → `RestoreTracksFromLayers()` (`:214`).

`ApplyRestore` (`:214`) is the **only** caller of `RestoreTracksFromLayers`. That covers Undo, Redo, Open (`:1080`) and New (`:1159`). The only other `new LayerRowViewModel(` sites (`:456`, `:654`) append an empty row for a new layer and never replace existing rows. So `ApplyRestore` is the one place a fix needs to hook into.

`RestoreTracksFromLayers` (`:747-762`) calls `_tracks.Clear()` (`:749`) and then builds `new LayerRowViewModel(cellIndex + 1)` (`:755`) for every cell, assigning `row.Layer = layer` (`:757`). A new row's `_openedHostedSlots` is empty. The `Layer` setter clears both collections anyway (`LayerRowViewModel.cs:83-84`, the Bug Audit #5 fix). `ApplyRestore` then re-selects the **new** row with the same `LayerId` (`MainWindow.xaml.cs:215-219`), so `_selectedLayer` now points at a row whose poll short-circuits at `LayerRowViewModel.cs:355-356`.

### 3. The editor window and its live instance survive the undo, by design

- `ProjectSession.Undo` / `Redo` (`ProjectSession.cs:104-117`) → `Restore` (`:157-170`) never releases instances. `ReleaseAllHostedInstances` runs only in `Open` (`:137`) and `New` (`:149`).
- Bug Audit #5 wrote this decision down: "Do **not** add the call to `Undo()`/`Redo()`" (`Bug_Audit_2026-09-23_ProjectSwitchPluginState.md:172`). Its reason was to keep the user's editor windows open.
- `Restore` pushes the snapshot's state into that same live instance (`ProjectSession.cs:167-168` → `MixEngine.cs:113-122`). The window the user is looking at is the one the mix processes audio through.

The user therefore sees an open, working Pro-Q window that the app has stopped watching.

### 4. Untracked live state still leaks into every later snapshot

`ProjectSession.Snapshot` (`ProjectSession.cs:81-86`) calls `MixEngine.SyncLiveStateIntoParameters` (`MixEngine.cs:99-107`) for **every** layer and **every** live instance. It does not look at which rows are tracking what. An untracked tweak is therefore not lost outright. It is pulled into whatever snapshot comes next, and that snapshot belongs to some other action:
- an unrelated edit's `CommitEdit`;
- a `Save`;
- the next Undo's `Restore`, which instead **overwrites** the tweak with older state.

Melodyne is **not** affected. `PollMelodyneStateChanged` (`LayerRowViewModel.cs:407-411`) asks the service, which keys the ARA baseline by `LayerId` (`HostedPluginService.cs:194-209`), not by row.

---

## Repros

Every [human] repro needs FabFilter Pro-Q 4 installed (it is, per `CLAUDE.md`). None needs a camera. "Tweak" means dragging a Pro-Q band in the plugin's own window and then waiting at least 1 s (two poll ticks).

### A. Undo → Save → tweak → close loses the tweak, with no prompt **[human], no camera**
1. Open or record a project with one layer. Select strip 1 and click **EQ** to open Pro-Q 4.
2. Tweak band 1. The toolbar title gets the unsaved marker ` •` (`MainWindow.xaml.cs:1053`), because the poll pushed an undo step.
3. Press **Ctrl+Z**. Pro-Q's band 1 jumps back. Leave the Pro-Q window open.
4. Press **Ctrl+S**. The ` •` disappears.
5. Tweak band 2 in the still-open Pro-Q window and wait 2 s. **Bug:** no ` •` appears.
6. Close the app. **Bug:** no "Save changes?" prompt, because `ConfirmDiscardUnsavedChanges` returns early on `!IsDirty` (`MainWindow.xaml.cs:1036`).
7. Reopen the project and open Pro-Q. Band 2's tweak is gone.

### B. Ctrl+Y silently wipes a tweak made after Ctrl+Z **[human], no camera**
1. Open Pro-Q on strip 1. Tweak band 1 (undo step S1), then band 2 (step S2).
2. Press **Ctrl+Z**. The editor shows S1.
3. Tweak band 3 in the still-open window.
   - Expected: a new step. The redo branch is cleared (`ProjectUndoStack.cs:52`), so Ctrl+Y does nothing.
4. Press **Ctrl+Y**. **Bug:** Pro-Q jumps to S2, because `Restore` pushes S2's blob into the instance (`MixEngine.cs:119-120`). Band 3's tweak is gone, and no Ctrl+Z/Ctrl+Y sequence brings it back, since no snapshot ever contained it.

### C. The tweak is merged into the next unrelated undo step **[human], no camera**
1. Open Pro-Q on strip 1, tweak it, Ctrl+Z, then tweak it again in the still-open window.
2. Drag strip 2's Volume slider and release it. `CommitSlider_PreviewMouseUp` (`MainWindow.xaml.cs:251`) → `PushUndoSnapshot` → `CommitEdit` → `Snapshot` pulls the Pro-Q tweak in as well (fact 4).
3. Press **Ctrl+Z**. **Bug:** both the volume change and the Pro-Q tweak are reverted at once. They were one undo step, although the user made them as two separate edits.

### D. The retake spec's own [human] check stops holding one step later **[human], no camera**
`Feature_Spec_2026-09-24_RetakeInPlace.md:592` says: "open Pro-Q 4 on layer 1, Replace layer 1's source, then tweak a band in the still-open window. The preview follows, and Ctrl+Z undoes the tweak as its own step." That passes. The Replace never rebuilds rows, which is exactly what D5 (`:79`) protected.

Carry on after that Ctrl+Z and tweak again: the tweak isn't a step, and repros A–C apply. D5's counter-example ("the tweak neither refreshes the preview nor becomes an undo step, until the user happens to reopen the editor from the strip") is **exactly what happens after every Undo/Redo**, through `RestoreTracksFromLayers` instead of the `Layer` setter.

### E. Headless evidence of the mechanism **[auto]**
`RebuiltRow_WithoutCarry_DoesNotSeeTweaksInAStillOpenEditor` (§Tests) builds the old row, opens Eq, builds the replacement row the way `RestoreTracksFromLayers` does, changes the live instance's state, and asserts `PollEditorChanges()` returns `false`. It passes today, and that result is the bug.

**Workaround, which lowers severity:** clicking the FX name again re-runs `OpenHostedEditor` (`MainWindow.xaml.cs:820-824`) and re-arms tracking. A user has no reason to do that for a window that is already open, so few will find it.

**Minor extra symptom:** `DebounceRefreshPreview` is not called either. The audio already follows, because the instance is live. What goes stale is anything computed at chain build: for example, a Pro-R 2 decay change after Ctrl+Z doesn't update the tail length (`FxSlot.cs:81`) until some other edit forces a rebuild.

---

## Why the suite misses it

- **Engine tests cover the engine side, which is correct.**
  - `Undo_PushesTheRestoredPluginStateIntoTheStillLiveInstance` (`tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs:88`) and `Undo_StillReusesTheSameLiveInstance` (`:106`) prove the instance survives and receives the restored state.
  - Neither involves rows or polling. The instance is fine; only the App's tracking of it is lost.
- **No test anywhere calls `PollEditorChanges`, `OpenHostedEditor` or sets `SharedHostedService`.** A grep of `tests/` finds zero matches. The poll/diff logic has no headless coverage at all. `LayerRowRetakeTests` (`tests/Acapella.App.Tests/LayerRowRetakeTests.cs`) checks that `NotifySourceReplaced` doesn't raise `Layer`, which is D5's proxy, but never polls.
- **There is no MainWindow-level Undo test, and one isn't practical.** Undo calls `MarkDirty()` (`ProjectSession.cs:107`). A dirty window's `Close()` then opens a `MessageBox` (`MainWindow.xaml.cs:173`, `:1038`), which hangs a headless test.
- **Every [human] check in the retake spec and in Bug Audit #5 stops at the first Ctrl+Z.** None of them tweaks again afterwards.

---

## Proposed fix

**Approach: when `ApplyRestore` rebuilds the rows, carry each old row's editor tracking to the new row with the same `LayerId`. Only carry a slot whose instance is still live, and re-seed its baseline from that live instance after the restore.**

### Change 1: `src/Acapella.App/ViewModels/LayerRowViewModel.cs`: new static helper (next to `PollHostedStateChanges`)

```csharp
/// <summary>Bug audit #9: MainWindow.RestoreTracksFromLayers builds brand-new rows on every
/// Undo/Redo, but the hosted instances -- and any editor window open on them -- survive the undo
/// by design (bug audit #5). Carries each old row's opened-slot tracking to the new row with the
/// same LayerId (the instance cache's key, not SlotNumber/CellIndex), so an editor left open across
/// a Ctrl+Z keeps being polled. Must run AFTER the session restore: the baseline is the live
/// instance's state as it is now (post-PushSavedStateIntoLiveInstances), so the first poll after
/// the undo sees no change. A slot with no live instance is not carried -- after Open/New's
/// ReleaseAll there is none, so this is a no-op there and a later fresh instance is never diffed
/// against a stale baseline (bug audit #5 §4).</summary>
internal static void CarryEditorTracking(IEnumerable<LayerRowViewModel> previousRows, IEnumerable<LayerRowViewModel> newRows)
{
    var service = SharedHostedService;
    if (service is null) return;

    var openedByLayerId = new Dictionary<int, FxSlot[]>();
    foreach (var old in previousRows)
    {
        if (old._layer is not null && old._openedHostedSlots.Count > 0)
            openedByLayerId[old._layer.LayerId] = old._openedHostedSlots.ToArray();
    }
    if (openedByLayerId.Count == 0) return;

    foreach (var row in newRows)
    {
        if (row._layer is null) continue;
        if (!openedByLayerId.TryGetValue(row._layer.LayerId, out var slots)) continue;

        foreach (var slot in slots)
        {
            var instance = service.TryGetLiveInstance(row._layer.LayerId, slot.Stage);
            if (instance is null) continue;   // released (Open/New): nothing to track

            row._openedHostedSlots.Add(slot);
            row._lastPolledHostedState[slot] = service.PullLiveState(instance);
        }
    }
}
```

Notes:
- It reads another instance's private fields. That is legal in C# within the same class, so the tracking stays private and no new public API is added.
- `internal` is enough for the tests: `Acapella.App.csproj:8` already has `InternalsVisibleTo Include="Acapella.App.Tests"`.
- `Params` is **not** written. The state goes into `Params` on the first real change, through the existing `:368`, exactly as before. `Snapshot` already pulls live state for undo/save anyway (fact 4).

### Change 2: `src/Acapella.App/MainWindow.xaml.cs`: `ApplyRestore` (`:205-230`)

```csharp
private bool ApplyRestore(Func<bool> restore)
{
    _applyingHistory = true;
    try
    {
        var previousRows = _tracks.ToList();   // bug audit #9: RestoreTracksFromLayers clears _tracks

        if (!restore()) return false;

        MetronomeBpmTextBox.Text = _session.MetronomeBpm.ToString("F3");
        MasterVolumeSlider.Value = _session.MasterVolumeDb;
        RestoreTracksFromLayers();
        // Bug audit #9: keep polling any FabFilter editor still open across the undo. AFTER
        // restore() (baseline = post-restore live state), BEFORE RefreshPreviewLive (no chain
        // build can create an instance in between), synchronous (no poll tick can land in between).
        LayerRowViewModel.CarryEditorTracking(previousRows, _tracks);
        if (!_masterSelected && _selectedLayer is not null)
        {
            var stillPresent = _tracks.FirstOrDefault(t => t.Layer?.LayerId == _selectedLayer.Layer?.LayerId);
            if (stillPresent is not null)
                SelectMixerStrip(stillPresent);
            else
                SelectMasterStrip();
        }
        RefreshPreviewLive();
        return true;
    }
    finally
    {
        _applyingHistory = false;
    }
}
```

**Why the live-instance check rather than an explicit "Undo/Redo only" flag:** `ApplyRestore` also serves Open (`:1080`) and New (`:1159`). Two ways to keep those two out:
- **(a) Pass a `carryTracking` flag** from `PerformUndo` / `PerformRedo` only.
- **(b) The live-instance check inside the helper (chosen).** Open/New run `ReleaseAllHostedInstances` before anything else (`ProjectSession.cs:137`, `:149`). By the time the carry runs, `TryGetLiveInstance` returns `null` for every slot, so nothing is carried.

(b) is chosen because it is correct on its own terms: tracking means something only while an instance is live. It also needs no signature change across three callers. For Undo/Redo the check always passes, because only `ReleaseAll` ever frees an instance today. So (a) on top of (b) would add a parameter without adding safety. The one thing (b) relies on is Change 2's placement before `RefreshPreviewLive` (subtlety 4).

### Ordering subtleties

**1. The baseline must be pulled from the live instance, not copied from the old row's `_lastPolledHostedState`.**
- Copying looks natural ("carry the tracking over"), but the old baseline is the state *before* the undo, and the undo just pushed older state into the instance (`ProjectSession.cs:167-168`).
- **Counter-example:**
  1. Pro-Q open. Tweak X, which the poll records: baseline = X, step S1.
  2. Ctrl+Z restores S0 and pushes S0's blob, so the instance is now S0.
  3. The carried baseline is X ≠ S0, so the first poll after the undo reports a change and calls `PushUndoSnapshot()` → `ProjectUndoStack.Push`. That clears the redo stack (`ProjectUndoStack.cs:52`).
- Result: **every Ctrl+Z on a layer with an editor ever opened kills Ctrl+Y within 500 ms**, and adds a spurious undo step identical to the current state. That is worse than the bug.
- Test: `CarriedTracking_DoesNotReportAChange_RightAfterTheUndo`.

**2. The baseline must not be taken from the restored `Params` either, even though `OpenHostedEditor` does exactly that (`LayerRowViewModel.cs:333`).**
- **Counter-example:**
  1. The user opens Pro-Q on a layer whose snapshot predates the plugin's first instance, so that snapshot has `EqHostedState == null`.
  2. After Ctrl+Z back to that snapshot, `PushSavedStateIntoLiveInstances` skips the null state (`MixEngine.cs:119`), and the instance keeps its current state X.
  3. A `Params`-seeded baseline is `null`, and `:366` treats `previous is null` as "changed". The first poll pushes a step and clears redo, the same failure as subtlety 1.
- Even for non-null state, `SetState(b)` followed by `GetState()` is not guaranteed to return byte-identical `b` for a real VST3 plugin.
- The pulled baseline compares like with like. The existing poll already compares consecutive `GetState()` pulls (`:364-366`).
- **One runtime unknown remains.** The carry's pull happens right after the restore's `SetState`, with the editor window open, and the next pull comes about 500 ms later. The fix assumes real Pro-Q returns identical bytes for those two pulls. The existing poll only relies on this in steady state, so it is a slightly stronger assumption.
  - The [human] "No regressions in redo" check below is what would catch a failure: a spurious step would clear the redo branch.
  - If that check fails, the fallback is to re-baseline silently on the first poll after a carry instead of reporting a change. It is not specced here.
- (`OpenHostedEditor` seeds its baseline from `Params`, which is null for a layer with no saved hosted state. In that case its first poll records the plugin's open-time state as a step. That is existing, accepted behaviour, similar to Bug Audit #6 runner-up #3's note, which describes an older null-seeding version. It is harmless at open time because there is no redo branch worth keeping then.)
- Test: `CarriedTracking_WhenRestoredStateIsNull_DoesNotReportAChange`.

**3. The pull must happen after `restore()`, not when `previousRows` is captured.**
- Capturing the rows *before* `restore()` is fine. The rows themselves are not changed by the restore, which only replaces `LayerModel`s in `_layers`.
- Pulling the baseline at capture time would be the pre-undo state, which is subtlety 1's counter-example again.
- Change 2 captures rows first and pulls inside `CarryEditorTracking`, after `restore()`.
- Test: covered by `CarriedTracking_DoesNotReportAChange_RightAfterTheUndo`, which pushes the "restored" state before calling the helper.

**4. Slots with no live instance must be skipped, and the carry must run before `RefreshPreviewLive()`.**
- **Counter-example (skip):**
  1. Pro-Q open on layer 0, then File > Open another project. `ReleaseAll` disposes the instance and closes its window.
  2. If the slot were carried anyway with a `null` baseline, the new project's first chain build creates a fresh `(0, Eq)` instance.
  3. The next poll diffs it against `null` and pushes a step nobody made. That is the Bug Audit #5 §4 artefact, now reintroduced through the carry instead of through the reused row.
- **Counter-example (placement):** placing the carry after `RefreshPreviewLive()` (`async void`, `:864-876`) lets the refresh's chain build (`FxSlot.cs:69`) run between `ReleaseAll` and the carry and create that fresh instance. The live check would then pass for a slot the user never opened in this project. Placing the carry synchronously right after `RestoreTracksFromLayers()` rules this out:
  - Preview is stopped before Open/New (`MainWindow.xaml.cs:1078`, `:1157`).
  - Export is gated (Bug Audit #8).
  - The Record dialog is modal, so it isn't open.
  - Nothing else can create an instance on the UI thread in between.
- The same synchronous placement keeps any 500 ms poll tick from landing between the row rebuild and the carry. Nothing in `restore()` / `RestoreTracksFromLayers()` / the helper pumps the dispatcher: `WpfHostedPluginDispatcher.Invoke` runs inline when `CheckAccess()` is true.
- Test: `CarriedTracking_SkipsSlotsWithNoLiveInstance_AfterReleaseAll`.

**5. (Not a timing rule, but easy to get wrong.) Match by `LayerId`, not by `SlotNumber`/`CellIndex` or list position.**
- Rows are rebuilt per cell (`MainWindow.xaml.cs:753-757`), but instances are keyed by `(LayerId, Stage)` (`HostedPluginInstanceCache.cs:13`).
- `LayerId ≠ CellIndex` is ordinary. For example, per-strip Rec on strip 3 of an empty project creates `LayerId 0` at `CellIndex 2` (`Bug_Audit_2026-09-23_RecordRowReuse.md:290`). The rebuild then puts it in the third row.
- **Counter-example:** matching by position hands layer 1's "Eq opened" tracking to layer 0's row. That row then polls `(0, Eq)`, which the user never opened, while `(1, Eq)`, which is open on screen, goes unpolled again.
- Test: `CarriedTracking_MatchesByLayerId_NotBySlot`.

### Rejected alternatives

- **Move opened-slot tracking into `HostedPluginService`, keyed by `(LayerId, Stage)` like the ARA baseline.** This is the "right home" long-term, because rows would stop owning anything that outlives them. But it changes the engine hosting subsystem, and it still has to solve subtleties 1–2: the service would need to re-baseline inside `PushSavedStateIntoLiveInstances`. That is a larger, cross-project change for the same outcome. It can be revisited if rows ever get rebuilt in more places.
- **Reuse the old row objects in `RestoreTracksFromLayers` and just reassign `Layer`.** The `Layer` setter clears tracking on purpose (`LayerRowViewModel.cs:83-84`, Bug Audit #5). Reusing rows would also need a per-cell reconciliation (rows added or removed by the undo), and would change the "fresh rows on restore" behaviour the gap-row logic (audit B8/B9) relies on.
- **Poll every live instance of every layer, not only the selected row's.** This fixes the symptom without tracking, but it changes the deliberate selected-only design (`MainWindow.xaml.cs:124`). It costs one dispatcher round-trip per instance per tick, and it would record a deselected strip's tweak as an undo step even while the user is working elsewhere. That is a UX decision, not a bug fix.
- **Close or release the editors on Undo so there is nothing to track.** This contradicts Bug Audit #5's written decision, and it reintroduces #5 §5's use-after-free risk on a synchronous path that doesn't stop preview (see `Bug_Audit_2026-09-23_RecordRowReuse.md:299-303`).
- **Re-call `OpenHostedEditor` for carried slots.** It seeds from `Params` (subtlety 2), and `ShowEditor` would re-focus or re-show every carried window on every Ctrl+Z.

---

## Deliberately not changed

- **Selected-only polling.** A tweak made in a deselected strip's editor is *deferred*, not lost. After the fix, the carried baseline catches it when that strip is next selected, just as today without an undo.
- **A layer that an undo drops and a redo brings back gets no carry.** The rows just before the redo don't contain that `LayerId`. Its editor window, if still open, is untracked until it is reopened from the strip. This case sits inside runner-up #3's territory (a stale instance for a dropped id) and should be fixed with it.
- **Runner-up #3 itself**, a stale hosted instance after an undo frees a `LayerId`, stays unchanged and deferred.
- **The one-way null-state valve** (`MixEngine.cs:119`). Restoring a snapshot whose hosted state is `null` still does not revert the plugin. Subtlety 2 only makes sure the carry doesn't turn that into a spurious step.
- **A tweak made less than 500 ms before Ctrl+Z** is still overwritten by the undo's push before any poll has seen it. This is existing polling granularity, not a row-rebuild issue.
- **Melodyne.** It is already tracked per `LayerId` in the service (`HostedPluginService.cs:194-209`) and is unaffected.
- **`OpenHostedEditor`'s `Params` seed** and the open-time step it records for a never-hosted layer (subtlety 2's aside).
- **`RestoreTracksFromLayers` itself.** It still builds fresh rows, and the carry is a separate step.

---

## Tests

### New file: `tests/Acapella.App.Tests/LayerRowEditorTrackingTests.cs`

This can't be red-then-green: `CarryEditorTracking` is a new API, so tests 2–6 don't compile before the fix. Test 1 instead characterizes the bug and passes both before and after the fix.

`App.Tests` references only `Acapella.App` (not `Engine.Tests`), so `FakeHostedPluginFactory` is not available. A small local fake implements the three public interfaces.

`LayerRowViewModel.SharedHostedService` is static, and every `new MainWindow()` in `ExportPlaybackGateTests` / `MixingScreenTests` / `TransportLayoutTests` overwrites it (`MainWindow.xaml.cs:96`). The class therefore runs in its own non-parallel collection, following the pattern of `tests/Acapella.Engine.Tests/Host/JuceHostingCollection.cs:28`, and restores the static in `finally`. This also avoids adding to the known test-parallelism flake.

```csharp
using Acapella.App.ViewModels;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Xunit;

namespace Acapella.App.Tests;

[CollectionDefinition("SharedHostedService", DisableParallelization = true)]
public sealed class SharedHostedServiceCollection { }

/// <summary>Bug audit #9: Undo/Redo rebuilds every row (MainWindow.RestoreTracksFromLayers);
/// CarryEditorTracking keeps a still-open FabFilter editor polled across that rebuild.</summary>
[Collection("SharedHostedService")]
public class LayerRowEditorTrackingTests
{
    private sealed class StatefulFakePlugin : IHostedPlugin
    {
        public byte[] State = Array.Empty<byte>();
        public int LatencySamples => 0;
        public double TailSeconds => 0;
        public void ProcessBlock(float[] inL, float[] inR, float[] outL, float[] outR, int numSamples) { }
        public void Reset() { }
        public byte[] GetState() => (byte[])State.Clone();
        public void SetState(byte[] data) { if (data.Length > 0) State = (byte[])data.Clone(); }
        public int ParameterCount => 0;
        public string GetParameterName(int index) => "";
        public float GetParameterValue(int index) => 0f;
        public void SetParameterValue(int index, float value) { }
        public bool ShowEditorWindow(string title) => true;
        public void CloseEditorWindow() { }
        public void Dispose() { }
    }

    private sealed class FakeFactory : IHostedPluginFactory
    {
        public IHostedPlugin Create(string pluginLabel, double sampleRate, int maxBlockSize) => new StatefulFakePlugin();
    }

    private sealed class EverythingAvailable : IHostedPluginAvailability
    {
        public bool IsAvailable(string pluginLabel) => true;
    }

    private static LayerModel Layer(int layerId, int cellIndex) =>
        new() { LayerId = layerId, Kind = LayerKind.UploadedVideo, SourcePath = $"l{layerId}.mp4", CellIndex = cellIndex };

    /// <summary>Runs body with a fresh fake-backed SharedHostedService, restoring the previous one after.</summary>
    private static void WithService(Action<HostedPluginService> body)
    {
        var saved = LayerRowViewModel.SharedHostedService;
        var service = new HostedPluginService(new EverythingAvailable(), dispatcher: null, factory: new FakeFactory());
        LayerRowViewModel.SharedHostedService = service;
        try { body(service); }
        finally { LayerRowViewModel.SharedHostedService = saved; service.Dispose(); }
    }

    private static StatefulFakePlugin Live(HostedPluginService s, int layerId) =>
        (StatefulFakePlugin)s.TryGetLiveInstance(layerId, FxSlots.Eq.Stage)!;

    /// <summary>Open Eq on a row and settle the open-time poll (OpenHostedEditor seeds from Params,
    /// which is null for these never-hosted layers, so its first poll always reports -- existing
    /// behaviour, not under test here).</summary>
    private static LayerRowViewModel RowWithOpenEq(int slotNumber, LayerModel layer)
    {
        var row = new LayerRowViewModel(slotNumber) { Layer = layer };
        row.OpenHostedEditor(FxSlots.Eq);
        row.PollEditorChanges();
        return row;
    }

    [StaFact]
    public void RebuiltRow_WithoutCarry_DoesNotSeeTweaksInAStillOpenEditor() => WithService(s =>
    {
        RowWithOpenEq(1, Layer(0, 0));
        var rebuilt = new LayerRowViewModel(1) { Layer = Layer(0, 0) };   // what RestoreTracksFromLayers does

        Live(s, 0).State = new byte[] { 7 };                               // tweak in the still-open editor

        Assert.False(rebuilt.PollEditorChanges());                        // characterizes the bug
    });

    [StaFact]
    public void CarriedTracking_SeesTheNextTweakInTheStillOpenEditor() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        var rebuilt = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        Live(s, 0).State = new byte[] { 7 };

        Assert.True(rebuilt.PollEditorChanges());
        Assert.Equal(new byte[] { 7 }, rebuilt.Layer!.MixParameters.EqHostedState);
        Assert.False(rebuilt.PollEditorChanges());                        // baseline advanced: no repeat
    });

    [StaFact]
    public void CarriedTracking_DoesNotReportAChange_RightAfterTheUndo() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        Live(s, 0).State = new byte[] { 1 };
        Assert.True(old.PollEditorChanges());                             // old baseline = {1}

        // Undo: restored layer carries {0}; ProjectSession.Restore pushes it into the live instance.
        var restored = Layer(0, 0);
        restored.MixParameters.EqHostedState = new byte[] { 0 };
        s.PushState(Live(s, 0), new byte[] { 0 });
        var rebuilt = new LayerRowViewModel(1) { Layer = restored };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        Assert.False(rebuilt.PollEditorChanges());   // subtlety 1: a copied {1} baseline would push and kill redo
    });

    [StaFact]
    public void CarriedTracking_WhenRestoredStateIsNull_DoesNotReportAChange() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        Live(s, 0).State = new byte[] { 1 };
        old.PollEditorChanges();

        var restored = Layer(0, 0);                  // snapshot predates the instance: EqHostedState == null,
        Assert.Null(restored.MixParameters.EqHostedState);   // so Restore pushes nothing (MixEngine.cs:119)
        var rebuilt = new LayerRowViewModel(1) { Layer = restored };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        Assert.False(rebuilt.PollEditorChanges());   // subtlety 2: a Params-seeded (null) baseline would push
    });

    [StaFact]
    public void CarriedTracking_SkipsSlotsWithNoLiveInstance_AfterReleaseAll() => WithService(s =>
    {
        var old = RowWithOpenEq(1, Layer(0, 0));
        s.ReleaseAll();                              // File > Open / New
        var rebuilt = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        LayerRowViewModel.CarryEditorTracking(new[] { old }, new[] { rebuilt });

        // First chain build of the new project creates a fresh instance with its own state.
        var fresh = (StatefulFakePlugin)s.GetOrCreateInstance(0, FxSlots.Eq.Stage, FxSlots.Eq.PluginLabel, null);
        fresh.State = new byte[] { 9 };

        Assert.False(rebuilt.PollEditorChanges());   // subtlety 4 / bug audit #5 §4: no spurious step
    });

    [StaFact]
    public void CarriedTracking_MatchesByLayerId_NotBySlot() => WithService(s =>
    {
        var oldA = new LayerRowViewModel(1) { Layer = Layer(0, 0) };
        var oldB = RowWithOpenEq(2, Layer(1, 1));    // Eq opened on layer 1 only
        s.GetOrCreateInstance(0, FxSlots.Eq.Stage, FxSlots.Eq.PluginLabel, null);   // layer 0 has a live, unopened Eq

        // After the restore, layer 1 sits in cell 0 and layer 0 in cell 1.
        var newSlot1 = new LayerRowViewModel(1) { Layer = Layer(1, 0) };
        var newSlot2 = new LayerRowViewModel(2) { Layer = Layer(0, 1) };
        LayerRowViewModel.CarryEditorTracking(new[] { oldA, oldB }, new[] { newSlot1, newSlot2 });

        Live(s, 1).State = new byte[] { 5 };
        Live(s, 0).State = new byte[] { 6 };

        Assert.True(newSlot1.PollEditorChanges());   // layer 1's open editor is tracked
        Assert.False(newSlot2.PollEditorChanges());  // layer 0's editor was never opened
    });
}
```

**Checked facts the tests rely on:**
- `LayerMixParameters.EqHostedState` has a public setter (`LayerMixParameters.cs:57`).
- `PollEditorChanges` → `PollMelodyneStateChanged` → `PollAraStateChanged` returns `false` when there is no ARA session (`HostedPluginService.cs:196`), so Melodyne never affects these assertions.

---

## Acceptance criteria

### [auto]
- The six tests in `tests/Acapella.App.Tests/LayerRowEditorTrackingTests.cs` pass.
- Regression, run once, because `ApplyRestore` and `LayerRowViewModel` are touched:
  - all of `Acapella.App.Tests`, especially `LayerRowRetakeTests`, `MixingScreenTests` and `ExportPlaybackGateTests`;
  - `Acapella.Engine.Tests`' `Project/ProjectSessionTests.cs`, especially `Undo_PushesTheRestoredPluginStateIntoTheStillLiveInstance` (`:88`), `Undo_StillReusesTheSameLiveInstance` (`:106`) and the `Retake_ThenUndo…` test (`:388`).

### [review] (checked by reading the diff, not automated)
- In `ApplyRestore`:
  - `previousRows` is captured before `RestoreTracksFromLayers()` (before `restore()`, in practice).
  - `CarryEditorTracking` is called after `restore()` and `RestoreTracksFromLayers()`, and **before** `RefreshPreviewLive()`.
  - The call is inside the `_applyingHistory` block, and there is no `await` between the row rebuild and the carry.
- `CarryEditorTracking` keys on `Layer.LayerId`. It seeds baselines only from `PullLiveState`, never from the old row's baseline or from `Params`, and it skips slots where `TryGetLiveInstance` is `null`.
- The `Layer` setter's clearing (`LayerRowViewModel.cs:83-84`) and `NotifySourceReplaced` are unchanged.

### [human] (FabFilter Pro-Q 4, no camera)
- **Repro A after the fix:** after Undo → Ctrl+S → tweak in the still-open Pro-Q, the ` •` marker appears within about 1 s, and closing prompts "Save changes?".
- **Repro B after the fix:** after Ctrl+Z → tweak, Ctrl+Y does nothing. Ctrl+Z then reverts only the tweak.
- **No regressions in redo:** open Pro-Q, make two tweaks, Ctrl+Z, wait 2 s **without touching anything**, then Ctrl+Y. The second tweak comes back. This shows subtleties 1–2 held and that Pro-Q returns the same state bytes right after `SetState` (subtlety 2's runtime unknown); a spurious step would have cleared the redo branch.
- **File > Open / New with Pro-Q open:** the window closes (Bug Audit #5 behaviour), and no ` •` appears on the freshly opened project within 2 s.

---

## Runners-up (not specced)

- **Strip and master meters stay lit after ■ Stop.**
  - `UpdateMeters` (`MainWindow.xaml.cs:429-444`) runs every 33 ms regardless of playback state, and reads `GetLayerLevels` / `GetMasterLevels` (`PreviewPlaybackEngine.cs:391-394` → `MixEngine.cs:73-78`).
  - After Stop the taps stop receiving `Read()` calls and freeze at their last block. `MixEngine.cs:39-43` documents that callers "should treat [them] as stale if PositionMs isn't advancing", and `UpdateMeters` doesn't.
  - Cosmetic. The fix is one gate: show 0 when `!_previewEngine.IsPlaying`.
- **Test-parallelism flake (aside only):** the new collection above avoids adding to it. The existing `new MainWindow()` tests still overwrite the static `SharedHostedService` in parallel.
