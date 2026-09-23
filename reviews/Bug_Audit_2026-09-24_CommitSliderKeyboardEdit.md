# Bug Audit #13: `CommitSlider` only commits on mouse-up — a keyboard-driven FX-slider edit changes the live mix with no undo step and no dirty mark

> **Promotion, not a fresh find.** Bug Audit #12's own Runners-up section (`Bug_Audit_2026-09-24_GuideTrackDeviceFailureOrphansCapture.md:259`) flagged this exact lead in passing — "a keyboard-only slider edit would update the live parameter... with no undo step and no `MarkDirty`, silently... not investigated past the XAML read" — and explicitly declined to verify it. This ticket is that verification, done in full, plus the fix.
>
> Audit as of `be237af` (Bug Audit #12 landed: guide-track device-open call guarded).
>
> **Confidence: high on the mechanism, verified by reading every link in the chain rather than assuming the runner-up's guess was right.** `CommitSlider`'s style really does wire only `PreviewMouseUp` (`MainWindow.xaml:42-44`), confirmed by grep — no `KeyUp`/`LostKeyboardFocus`/`LostFocus` handler exists anywhere near a `Slider` in this file. `Slider.Value`'s binding has no `UpdateSourceTrigger` override in any of the eleven `CommitSlider`-styled XAML declarations, so it uses the framework default for `RangeBase.ValueProperty`, which is `PropertyChanged` (unlike `TextBox.Text`, which is the one property WPF special-cases to `LostFocus` — the reason `TrimTextBox_LostFocus`/`LayerNameTextBox_LostFocus`, `MainWindow.xaml.cs:261,263`, correctly use `LostFocus` instead and are *not* affected by this bug). The consequence half is grounded by grepping every call site of `MarkDirty` in `src/` (root cause fact 4, below): it is called from exactly three places, none reachable from an FX-parameter mutation — `layer.MixParameters.X = value` (what every `CommitSlider`-bound VM setter does) has no path to it at all except through `ProjectSession.CommitEdit()`. One nuance the original runner-up didn't anticipate and this ticket had to trace separately: `MainWindow`'s own global transport-shortcut handler (`MainWindow_PreviewKeyDown`, `:267-292`) intercepts **Left/Right/Home** (and Space) for seek/restart *before* they ever reach a focused Slider's own key handling, for any focus that isn't a `TextBox` — including a focused `Slider`. That Up/Down/PageUp/PageDown/End are exploitable is grounded directly in the switch statement's case list (`:272-291` has no `Key.Up`/`Key.Down`/`Key.PageUp`/`Key.PageDown`/`Key.End` cases) — not a guess. That Left/Right/Home are *not* exploitable rests on one additional, unverified-in-this-repo step: WPF's documented behavior that a `PreviewKeyDown`/`KeyDown` event pair shares one `Handled` flag, so setting it during the tunnel phase suppresses the slider's own bubble-phase key handling too. This part is reasoned from WPF's general input-routing behavior, not observed running the app — see the seek-regression check in Acceptance criteria, which exists specifically to confirm it empirically before relying on it.

---

## The bug in one sentence

`CommitSlider`'s style (`MainWindow.xaml:42-44`) pushes an undo snapshot and marks the project dirty only on `PreviewMouseUp`; because `Slider.Value` binds `TwoWay` with the framework-default `PropertyChanged` trigger (not `LostFocus`), a focused FX slider (Pan, the per-layer Gain fader, EQ, noise gate, compressor, or limiter — eleven sliders per layer, `MainWindow.xaml:111-475`) already applies Up/Down/PageUp/PageDown/End key presses to the live mix parameter and the live preview audibly — but because none of those keys ever fires `PreviewMouseUp`, the edit never calls `PushUndoSnapshot()`, so it is invisible to Undo/Redo and never marks the project dirty, meaning it can be silently discarded (no "save changes?" prompt) the moment the project is closed, replaced, or the app exits.

---

## Root cause

Four facts combine to produce the defect.

### 1. `CommitSlider`'s only commit path is `PreviewMouseUp`

**File:** `src/Acapella.App/MainWindow.xaml`

| Line | What it does |
|---|---|
| `:39-41` | Comment: "Applied to every FX/mix-parameter slider... pushes an undo snapshot once the drag ends, not per-tick." |
| `:42-44` | `<Style x:Key="CommitSlider" TargetType="Slider"><EventSetter Event="PreviewMouseUp" Handler="CommitSlider_PreviewMouseUp"/></Style>` — exactly one `EventSetter`, exactly one event. |
| `:111-475` | Eleven `CommitSlider`-styled sliders across the FX panel: per-layer Pan and Gain (`:111-114`), noise gate threshold/release (`:382,385`), compressor threshold/ratio (`:406,409`), EQ low/mid/high (`:430,433,436`), limiter ceiling/gain (`:472,475`) — every one of them `Value="{Binding ..., Mode=TwoWay}"`, no `UpdateSourceTrigger` override on any of them. |

**File:** `src/Acapella.App/MainWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:257` | `private void CommitSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => PushUndoSnapshot();` — the only handler wired to `CommitSlider`. |
| `:197-201` | `PushUndoSnapshot()`: `if (_applyingHistory) return; _session.CommitEdit();` — the only path that pushes an undo step *or* marks the project dirty for an FX-parameter edit. |

Grepping this file for `LostFocus|KeyUp|KeyDown|GotFocus|LostKeyboardFocus` finds three `TextBox` `LostFocus` handlers (`:261,263`, plus `MetronomeBpmTextBox_LostFocus` at `:336`) and the one `Window`-level `PreviewKeyDown` handler discussed in fact 3 below — nothing else, and nothing at all attached to a `Slider`'s keyboard events.

### 2. `Slider.Value`'s binding updates live, on every value change, not just on commit

`Slider.Value` (inherited from `RangeBase.ValueProperty`) has no dependency-property metadata forcing `UpdateSourceTrigger.LostFocus` the way `TextBox.Text` does — WPF's framework default for every other property is `PropertyChanged`, and none of the eleven bindings above sets `UpdateSourceTrigger` explicitly. So every `Value` change — mouse drag tick, or a keyboard-driven change — flows straight into the bound `LayerRowViewModel` property immediately.

**File:** `src/Acapella.App/ViewModels/LayerRowViewModel.cs`

| Line | What it does |
|---|---|
| `:183-187` | `Pan` setter: `Params.Pan = value; OnPropertyChanged(...); OnPropertyChanged(nameof(PanDisplay)); LiveParamChanged?.Invoke();` — mutates the live `MixParameters` and fires `LiveParamChanged` unconditionally, with **no call to anything that marks the project dirty or pushes an undo step**. |
| `:453-457` | `GainDb` setter: identical shape. |
| (same shape at every other `CommitSlider`-bound property: `LowShelfGainDb`, `MidBellGainDb`, `HighShelfGainDb`, `NoiseGateThresholdDb`, `NoiseGateReleaseMs`, `CompressorThresholdDb`, `CompressorRatio`, `LimiterCeilingDb`, `LimiterGainDb` — all confirmed by grep, all end in `LiveParamChanged?.Invoke();` and nothing else.) | |

`LiveParamChanged` feeds `DebounceRefreshPreview()` (`MainWindow.xaml.cs:463,661,764` wire it per row) — so a keyboard-driven edit really is audible in the live preview, immediately. The value is not merely displayed differently; it is actually applied to the mix.

### 3. A focused slider doesn't even get all eight of WPF's normal slider keys — the window's own transport shortcuts steal four of them first

**File:** `src/Acapella.App/MainWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:189` | `PreviewKeyDown += MainWindow_PreviewKeyDown;` — attached directly to `this` (the `Window`), inside the constructor. `PreviewKeyDown` tunnels from the root down to the focused element, so this handler runs **before** a focused descendant (including any `CommitSlider`) sees the key at all. |
| `:270` | `if (Keyboard.FocusedElement is TextBox) return;` — the *only* focus exclusion. A focused `Slider` is not excluded. |
| `:272-291` | `switch (e.Key)`: `Key.Space` → play/stop, `Key.Left` → `SeekRelative(-5000)`, `Key.Right` → `SeekRelative(5000)`, `Key.Home` → `RestartButton_Click`; each sets `e.Handled = true`. |

WPF's Preview/bubble event pairs share one `Handled` flag per physical key-press — setting `e.Handled = true` during the tunnel phase suppresses the paired bubble-phase `KeyDown` (where a `Slider`'s own arrow-key value adjustment runs) for every element in the route, including the focused slider itself. So **Left, Right, and Home never reach a focused `CommitSlider`'s own value-changing logic at all** — they're consumed by the transport shortcut first, regardless of what's focused. The keys this bug is actually reachable through are the five WPF gives a `Slider` that this switch statement doesn't list: **Up, Down, PageUp, PageDown, End**.

(`KeyUp`/`PreviewKeyUp` is a separate event pair from `KeyDown`/`PreviewKeyDown` — pressing Left/Right/Home while a slider is focused still generates a `KeyUp` on release, even though the paired `KeyDown` was suppressed. This matters for the fix's key filter — see Rejected alternatives.)

### 4. `MarkDirty()` is called from exactly three places, and none of them is reachable from an FX-parameter mutation

**File:** `src/Acapella.Engine/Project/ProjectSession.cs`

| Line | What it does |
|---|---|
| `:39` | `MetronomeBpm` setter calls `MarkDirty()` on a real change. |
| `:48` | `MasterVolumeDb` setter calls `MarkDirty()` on a real change. |
| `:97-101` | `CommitEdit()` — pushes an undo snapshot, then `MarkDirty()`. |
| `:65` | `private void MarkDirty() => SetSaveState(CurrentFilePath, dirty: true);` |

Grepping all of `src/` for `MarkDirty` finds only those three call sites (plus `Undo()`/`Redo()`, `:107,115`, which also call it). `LayerModel.MixParameters` is a plain field-bag with no dirty-tracking of its own and no path back to `ProjectSession`. So the *only* way an FX-parameter edit reaches `IsDirty` is via `CommitEdit()` — i.e., via `PushUndoSnapshot()` — i.e., via `CommitSlider_PreviewMouseUp`. If that never fires, the edit is invisible to both undo/redo and the dirty flag, permanently, until it is either silently folded into some later, unrelated commit (`Snapshot()` always captures the full live state, `:81-86`) or wiped outright by an unrelated undo/redo restore overwriting the live state wholesale (see Repro B).

**File:** `src/Acapella.App/MainWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:1040-1052` | `ConfirmDiscardUnsavedChanges()`: `if (!_session.IsDirty) return true;` — if `IsDirty` is `false`, the method returns `true` immediately with **no prompt at all**. Called before File > New, File > Open, and window close (`:173,1072,1159`). |

---

## Repros

Undo here is whole-project-snapshot based (`ProjectUndoStack`, see root cause fact 4 and its file) — `Undo()`/`Redo()` fully repopulate the live layers from a stored `ProjectFileDto`, they don't selectively revert one field. So "press Ctrl+Z and nothing happens" is *not* the right symptom to look for: an uncommitted keyboard edit sits only in the live, in-memory `MixParameters`, and any undo/redo restore that runs *for a different, unrelated reason* overwrites the live state wholesale — including silently discarding the uncommitted edit as a side effect. The repros below are written around that.

### A. A keyboard-only EQ edit is audible but invisible to the dirty flag **[human]**

Focus order matters here: getting keyboard focus onto the slider at all normally means clicking *something* first, and several nearby controls have their own commit handlers (`CommitCheckBox_Click`, `TrimTextBox_LostFocus`) that would dirty the project before the keyboard step even starts. The sequence below establishes a saved path up front (so a later `Ctrl+S` saves in place instead of popping a focus-stealing Save dialog, `SaveProject`, `MainWindow.xaml.cs:1002-1013` — `filePath` is null for an as-yet-unsaved project, so `forceDialog: false` still opens `SaveFileDialog`), then clicks the slider itself (its own commit is harmless — nothing has changed yet) and re-cleans with the `Ctrl+S` **key binding**, which — unlike a Save-button click — doesn't move focus away from the slider.

1. Add a layer with a source (upload any audio/video file). Open the Mixing screen and select that layer's strip.
2. Save the project now (File > Save, or Ctrl+S) and pick a path in the dialog that appears — this gives `_session.CurrentFilePath` a value so a *later* Ctrl+S won't reopen that dialog.
3. Click the **High EQ** slider's thumb once to give it keyboard focus, **without dragging**. This fires `PreviewMouseUp` and commits — harmless, since nothing has changed since step 2.
4. Press **Ctrl+S** again. `CurrentFilePath` is set now, so this saves in place with no dialog and no focus change — a clean baseline with the slider still focused.
5. With the slider still focused, press **End** once (jumps to the slider's maximum; **Up** works too, one press). If the thumb doesn't move, the slider never actually had keyboard focus — click it again and repeat from step 4.
6. **Bug:** the slider's thumb visibly moved and (if the preview is playing) the EQ audibly changed — `LiveParamChanged` fired, the mix parameter really changed. But the toolbar/title bar still shows **no** unsaved-changes indicator, even though the live mix no longer matches what's on disk.
7. Close the project (File > New): **no "Save changes?" prompt appears** — `ConfirmDiscardUnsavedChanges` sees `IsDirty == false` and discards silently. The EQ change made in step 5, which was genuinely audible seconds earlier, is gone with no warning.

### B. The stealth edit is wiped by an Undo that looks correct, and can never come back **[human]**

1. Add a layer with a source. Drag the **Pan** slider with the mouse to a distinct value (e.g. hard left) and release — commits normally, giving the undo stack one real entry, call it `S_pan` (Pan=X, EQ=default).
2. Click the **High EQ** slider's thumb once to give it keyboard focus, without dragging (if the thumb doesn't move/highlight, the click missed — retry) — commits again, `S_click` (Pan=X, EQ=default — unchanged from `S_pan`, since nothing was edited between steps 1 and 2).
3. With the slider still focused, press **End** once. EQ jumps to its maximum — live-only, uncommitted (the bug).
4. Press **Ctrl+Z** once. `Undo()` pushes `S_click` onto the redo stack and restores `S_pan` — `Restore()`/`RestoreTracksFromLayers()` repopulate the live `MixParameters` from it, so EQ goes from max back to default. At a glance this *looks* like a correct undo of the End press. It isn't: it's `S_pan`'s entire snapshot being restored (which happens to already have EQ=default, since the End press postdates both `S_pan` and `S_click` and was captured in neither). Pan is unaffected either way (`S_pan` and `S_click` both have Pan=X), so this step alone can't distinguish the bug from correct behavior.
5. Press **Ctrl+Y** (Redo — `MainWindow.xaml:25`; this app does not bind Ctrl+Shift+Z). `S_click`'s state returns (Pan=X, EQ=default). **Bug, now visible:** EQ does **not** go back to max. The End press was never part of `S_pan` or `S_click`, so it isn't part of either stack Redo draws from — it is simply gone, and no further undo/redo can bring it back.

### C. Structural evidence the fix must produce **[auto]**

`CommitSlider_Style_HasAKeyboardCommitHandler` (below) inspects the actual `CommitSlider` style resource loaded from `MainWindow.xaml` and asserts it has an `EventSetter` for a keyboard-commit event. This fails today (red) and passes once the fix adds one (green) — no synthetic keyboard input needs to be raised, so it isn't subject to the focus/route uncertainties that make a full keyboard-simulation test fragile.

### D. Engine-level evidence of the underlying mechanism **[auto]**

`MutatingMixParametersDirectly_NeverMarksDirty` (below) proves, without any WPF involved, that the exact mutation every `CommitSlider`-bound setter performs (`layer.MixParameters.X = value`, with no `CommitEdit()` call) leaves `IsDirty` false — the root cause `CommitSlider`'s missing keyboard handler is supposed to prevent from mattering.

---

## Why the suite misses it

- **No test in this suite has ever raised a keyboard event on a `Slider`.** `tests/Acapella.App.Tests/ExportPlaybackGateTests.cs` raises `RoutedEventArgs(ButtonBase.ClickEvent)` on buttons (`:21,38,60`); `MixingScreenTests.cs` and `TransportLayoutTests.cs` check binding shape and layout bounds, never event wiring. `grep -rn "KeyDown\|KeyUp" tests/` finds nothing.
- **`ProjectSessionTests.cs` exercises `MixParameters` mutations mostly alongside an explicit `CommitEdit()`** (e.g. `UndoAndRedo_StepThroughCommittedEdits`, `:40-59`), and where it does mutate without committing (`SnapshotForExport_IsUnaffectedByLaterEdits`, `:213-227`, which mutates `GainDb` after taking a snapshot) it only asserts the earlier snapshot is unaffected — it never asserts anything about `IsDirty` in that state. No existing test asks "what happens to `IsDirty` if the commit step is skipped," which is exactly what a keyboard-only slider edit does.
- **The prior runner-up (`Bug_Audit_2026-09-24_GuideTrackDeviceFailureOrphansCapture.md:259`) explicitly stopped at the XAML read** and never checked whether `Slider.Value`'s binding actually pushes live on a keyboard change (it does — fact 2 above), never checked `ProjectSessionTests` for the dirty-flag consequence (it's absent — fact 4), and never noticed the window-level transport shortcut narrows the exploitable key set (fact 3) — all three needed tracing through a different file than the one the lead started in.
- **`RecordSetupWindow`'s complete lack of coverage (Bug Audits #7, #11, #12) is a different, unrelated gap** — `MainWindow` itself *does* have `[StaFact]`-based coverage (`Acapella.App.Tests`), so this bug isn't hidden by "the whole window is untestable"; it's hidden because nobody wrote the specific test that exercises a focused slider's keyboard path.

---

## Proposed fix

**Approach: add a second `EventSetter` to the `CommitSlider` style for `PreviewKeyUp`, filtered to the keys that actually move a `Slider`'s value, mirroring the existing mouse-up handler's unconditional-commit-on-release shape.**

### Change 1: `src/Acapella.App/MainWindow.xaml:42-44`

```xml
        <!-- Applied to every FX/mix-parameter slider (not the timeline or master-volume sliders,
             which have their own commit handlers): pushes an undo snapshot once the drag ends,
             not per-tick (v5 P1 task 2). Bug audit #13: also on PreviewKeyUp, for the same reason --
             Slider.Value binds TwoWay with the framework-default PropertyChanged trigger, so a
             keyboard-driven edit (Up/Down/PageUp/PageDown/End -- Left/Right/Home are already
             claimed by MainWindow's own transport shortcuts, :267-291) already applies live with
             no commit unless this fires too. -->
        <Style x:Key="CommitSlider" TargetType="Slider">
            <EventSetter Event="PreviewMouseUp" Handler="CommitSlider_PreviewMouseUp"/>
            <EventSetter Event="PreviewKeyUp" Handler="CommitSlider_PreviewKeyUp"/>
        </Style>
```

### Change 2: `src/Acapella.App/MainWindow.xaml.cs`, next to `CommitSlider_PreviewMouseUp` (`:257`)

```csharp
    /// <summary>Bug audit #13: Slider.Value binds TwoWay with the framework-default
    /// UpdateSourceTrigger (PropertyChanged), unlike TextBox.Text -- so a keyboard-driven edit on a
    /// focused CommitSlider already applies live, with nothing to fire PreviewMouseUp. Commits on
    /// key-release (not key-down -- see the doc's ordering subtleties) for the same reason the mouse
    /// handler commits on release, not per-tick: one undo step per discrete edit. Filtered to
    /// Up/Down/PageUp/PageDown/End only -- Left/Right/Home are deliberately excluded: MainWindow's
    /// own transport shortcut (MainWindow_PreviewKeyDown, :267-291) already consumes those three for
    /// seek/restart before they ever reach a focused Slider's Value (root cause fact 3), so including
    /// them here would fire a commit on every seek performed while any FX slider happens to be
    /// focused -- not just a harmless no-op step, but one that clears the redo stack
    /// (ProjectUndoStack.Push, :52) on every such seek, silently destroying whatever the user could
    /// otherwise have redone.</summary>
    private static readonly Key[] SliderCommitKeys =
        { Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.End };

    private void CommitSlider_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (Array.IndexOf(SliderCommitKeys, e.Key) >= 0) PushUndoSnapshot();
    }
```

(`System.Windows.Input` — for `Key`/`KeyEventArgs` — is already imported at `MainWindow.xaml.cs:8`.)

### Rejected alternatives

- **Keep `Left`/`Right`/`Home` in `SliderCommitKeys` as a harmless superset**, on the theory that they simply never fire in practice (fact 3) so including them costs nothing and keeps the filter decoupled from `MainWindow_PreviewKeyDown`'s own key list. Rejected once traced through `ProjectUndoStack.Push` (`:40-54`): `Push` unconditionally calls `_redoStack.Clear()` (`:52`), matching standard undo/redo semantics ("a new edit invalidates the old redo branch"). If the user had just pressed Ctrl+Z (populating the redo stack) and then merely pressed Left arrow to seek the timeline — with an FX slider still focused from an earlier interaction, a very ordinary sequence — releasing Left would call `CommitSlider_PreviewKeyUp`, which (with the superset filter) calls `PushUndoSnapshot()` on completely unchanged mix state, silently wiping the redo stack as a side effect of a seek. That is a new regression, strictly worse than this bug, not a "harmless" no-op. Narrowing to the five keys that actually reach a focused slider's `Value` avoids it entirely.
- **`LostKeyboardFocus` instead of `PreviewKeyUp`.** Would coalesce a whole run of separate key-taps into one commit if focus lingers on the slider between them — but the critical failure mode is the opposite direction: if the user never moves focus away (closes the project, or the app, with the slider still focused), the edit stays uncommitted for the entire window between the edit and whatever eventually steals focus, reproducing repro A's exact "silent discard on close" hazard for an arbitrarily long window. `PreviewKeyUp` commits at the end of *this* key-press, the same way `PreviewMouseUp` commits at the end of *this* drag — not "whenever the user happens to look away next."
- **Unfiltered `KeyUp` (commit on every key).** Rejected: would push a no-op undo step for Tab, Enter, and every other key that passes through a focused slider without ever touching `Value` — a needless multiplication of the already-known, already-deferred no-op-commit issue (Bug Audit #10 runner-up), when the actual value-changing key set is small and fixed.
- **`PreviewKeyDown` instead of `PreviewKeyUp`.** Rejected — see Ordering subtleties 1: `Value` hasn't necessarily finished updating by the time the *tunnel* phase reaches the slider, and this would also fire once per repeated `KeyDown` while a key is held, defeating the "not per-tick" design the mouse handler already established.
- **A window-wide fallback (e.g., `Window.PreviewKeyUp` calling `PushUndoSnapshot()` for any bound control) instead of scoping it to `CommitSlider`.** Over-broad: `TextBox`-based fields (`TrimTextBox`, `LayerNameTextBox`, `MetronomeBpmTextBox`) already commit correctly on `LostFocus`, by design — a blanket keyboard handler would either duplicate that commit (double-pushing an undo step per edit) or need its own exclusion logic to avoid it, for no benefit over scoping the fix to exactly the style that already owns this responsibility.

---

## Ordering subtleties

**1. The handler must be wired to `PreviewKeyUp`, not `PreviewKeyDown` — `Value` is not guaranteed updated at the point a `PreviewKeyDown` handler on the slider itself would run.**
- `PreviewKeyDown` tunnels root-to-leaf; a `Slider`'s own arrow-key value adjustment happens in the *bubble*-phase `KeyDown`, which runs only after the entire tunnel pass (including any `PreviewKeyDown` handler attached to the slider itself) has completed.
- **Counter-example:** if `CommitSlider_PreviewMouseUp`'s keyboard counterpart were instead wired to `PreviewKeyDown`, `PushUndoSnapshot()` would run *before* the slider's bubble-phase `KeyDown` has applied the new value — `Snapshot()` (`ProjectSession.cs:81-86`) would capture the *previous* value, one key-press behind, and the edit that actually changed the value would end up folded into whatever the *next* commit happens to be (the same "silently folded into a later, unrelated commit" shape as root cause fact 4, but self-inflicted by the fix itself). `PreviewKeyUp` is a separate event pair, raised strictly after the paired `KeyDown` (and therefore after `Value` has already been pushed through the `PropertyChanged`-triggered binding) has been fully processed.

**2. Key-repeat must not flood the undo stack — hooking release, not press, is what keeps this "not per-tick."**
- Holding an arrow key down generates many `KeyDown` events (`IsRepeat == true`) but exactly one `KeyUp` when the physical key is released.
- **Counter-example:** a fix wired to (`Preview`)`KeyDown` would call `PushUndoSnapshot()` once per repeat tick for as long as the key is held — for a held Up-arrow run of, say, 15 repeats, that's 15 undo steps for what the user experiences as one continuous edit, exactly the "per-tick" flooding `CommitSlider`'s own doc comment (`MainWindow.xaml.cs:256`, mirrored in the XAML comment above) says the mouse-based design was built to avoid. `PreviewKeyUp` collapses any such run into the one commit that happens on release, matching the mouse-drag behavior exactly.

---

## Deliberately not changed

- **The `TextBox`-based commit handlers** (`TrimTextBox_LostFocus`, `LayerNameTextBox_LostFocus`, `MetronomeBpmTextBox_LostFocus`) — these are correct as-is. `TextBox.Text` is the one WPF property whose framework default really is `UpdateSourceTrigger.LostFocus`, so there is no live-update-without-commit gap for these fields in the first place; this fix doesn't touch them.
- **`MasterVolumeSlider`/`MixerMasterFader`.** They don't use the `CommitSlider` style (`MainWindow.xaml:198-199,334-336` wire `PreviewMouseUp` directly to `MasterVolumeSlider_PreviewMouseUp`), so they share this bug's *undo* half (a keyboard edit still isn't independently undoable) but **not** its *data-loss* half: `MasterVolumeSlider_ValueChanged` (`MainWindow.xaml.cs:303`) writes straight into `ProjectSession.MasterVolumeDb`, whose setter (`ProjectSession.cs:48`) calls `MarkDirty()` on every real change — including a keyboard-driven one. So an unsaved keyboard edit to master volume *does* still trigger the "save changes?" prompt on close; only its own undo-step is missing. Lower severity, narrower, left as a runner-up rather than folded into this fix (different style, different handler, would need its own `EventSetter`).
- **`TimelineSlider`.** Playhead scrub position, not a project mix parameter — never undo-tracked by design, unaffected by any of this.
- **The pre-existing no-op commit on a click-without-drag** (Bug Audit #10 runner-up). `CommitSlider_PreviewMouseUp` already pushes a snapshot unconditionally on any mouse-up, moved or not; the new `CommitSlider_PreviewKeyUp` mirrors that same looseness for its filtered key set rather than trying to detect "did `Value` actually change," for consistency with the existing handler and to avoid scope-creeping this fix into a different, already-deferred issue.

---

## Tests

### `tests/Acapella.App.Tests/CommitSliderKeyboardTests.cs` (new file)

```csharp
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #13: CommitSlider's style must commit on a keyboard-driven edit, not only a
/// mouse-up -- Slider.Value binds TwoWay with the framework-default PropertyChanged trigger, so a
/// keyboard edit already applies live with nothing else to catch it. Inspects the actual style
/// resource rather than simulating a routed keyboard event, so it isn't subject to the focus/route
/// uncertainties a full keyboard-simulation test would carry (no established precedent for raising
/// KeyDown/KeyUp in this suite -- see the doc's "why the suite misses it").</summary>
public class CommitSliderKeyboardTests
{
    [StaFact]
    public void CommitSlider_Style_HasAKeyboardCommitHandler()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();

            var style = (Style)window.FindResource("CommitSlider");
            bool hasKeyboardCommit = style.Setters.OfType<EventSetter>()
                .Any(es => es.Event.Name == "PreviewKeyUp");

            Assert.True(hasKeyboardCommit,
                "CommitSlider only wires PreviewMouseUp -- a keyboard-only slider edit (Up/Down/PageUp/PageDown/End) applies live with no undo step and no dirty mark. Expected a PreviewKeyUp EventSetter specifically (see the doc's rejected alternatives for why not KeyUp/LostKeyboardFocus).");
        }
        finally
        {
            window.Close();
        }
    }
}
```

**Regression carve-out (per CLAUDE.md):** `MainWindow.xaml`/`MainWindow.xaml.cs` are touched but `Acapella.App.Tests` has no prior test asserting anything about `CommitSlider_PreviewMouseUp` itself, so there's nothing existing to regress there. Re-run `tests/Acapella.App.Tests/MixingScreenTests.cs`'s `FxPanel_HasNoSliderBoundToGainDb` once, since it also touches `FxPanel`'s slider set — the fix adds an `EventSetter`, not a new `Slider` or a new binding, so it should be unaffected, but it's the one existing test closest to this surface.

### `tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs` (append)

```csharp
    /// <summary>Bug audit #13: grounds the consequence half of the bug, independent of WPF -- this
    /// is exactly what every CommitSlider-bound LayerRowViewModel setter does (e.g. Pan, GainDb,
    /// LayerRowViewModel.cs:186,456) when its own PreviewMouseUp/PreviewKeyUp handler never fires.
    /// Passes today already (it's the mechanism the fix depends on staying true, not new behavior) --
    /// included so the assumption is pinned down rather than left implicit. Uses Save() (not
    /// CommitEdit()) for the clean baseline: CommitEdit() itself calls MarkDirty() (ProjectSession
    /// .cs:97-101, confirmed by CommitEdit_MarksDirty_SaveClearsItAndRemembersThePath above), so
    /// committing the layer-add would leave IsDirty true and the first assert would fail for a
    /// reason unrelated to what this test characterizes.</summary>
    [Fact]
    public void MutatingMixParametersDirectly_NeverMarksDirty()
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        session.Save(TempProjectPath());
        Assert.False(session.IsDirty);   // clean baseline, mirrors a just-saved project

        layer.MixParameters.Pan = 0.5f;   // exactly what LayerRowViewModel.Pan's setter does

        Assert.False(session.IsDirty);   // characterizes the bug: nothing marks dirty without CommitEdit()
    }
```

---

## Acceptance criteria

### [auto]
- `CommitSlider_Style_HasAKeyboardCommitHandler` passes (fails before the fix, since `CommitSlider` currently has exactly one `EventSetter`).
- `MutatingMixParametersDirectly_NeverMarksDirty` passes (already true today — pins the assumption, doesn't require the fix).
- `MixingScreenTests.FxPanel_HasNoSliderBoundToGainDb` still passes (regression carve-out).
- The solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- The new `EventSetter` is for `PreviewKeyUp`, not `PreviewKeyDown` (Ordering subtlety 1) or bare `KeyUp` (which would miss a slider inside a control that marks the bubble-phase event handled elsewhere first).
- `CommitSlider_PreviewKeyUp` checks `e.Key` against `SliderCommitKeys` before calling `PushUndoSnapshot()` — not an unconditional commit on every key.
- `SliderCommitKeys` contains exactly `{ Up, Down, PageUp, PageDown, End }` — **not** `Left`/`Right`/`Home` (those are claimed by `MainWindow_PreviewKeyDown` for seek/restart, root cause fact 3; including them would clear the redo stack on an ordinary seek — see the rejected "harmless superset" alternative).
- The `TextBox`-based `LostFocus` commit handlers are untouched.
- `MasterVolumeSlider`/`MixerMasterFader`'s separate `PreviewMouseUp`-only wiring is untouched (out of scope, see Deliberately not changed).

### [human]
- Repro A after the fix: following its steps 1-5 (saved path, click-focus, Ctrl+S, then End), the toolbar/title bar now shows the unsaved-changes indicator immediately after step 5, and closing the project (File > New) now prompts "Save changes to ...?" instead of discarding silently.
- Repro B after the fix: following its steps 1-3 (`S_pan`, `S_click`, then End), pressing End now itself commits immediately as its own entry, `S_end` (Pan=X, EQ=max). Ctrl+Z once now moves EQ back to its pre-End position (default, `S_click`) — the same value the unfixed doc's step 4 also produced, but now as a correct, singular undo of the End press rather than an accidental side effect of undoing something else. Ctrl+Y now returns EQ to max (`S_end`) — the recoverability that's missing today.
- Seek-with-a-slider-focused regression check, and a check of root cause fact 3's unverified half in the same pass: with an FX slider focused, press Left or Right to seek the preview timeline. Confirm (a) the seek still works, (b) it does **not** mark the project dirty or clear the redo stack, and (c) the focused slider's thumb does **not** move. If the thumb *does* move, fact 3's claim that `MainWindow_PreviewKeyDown` suppresses the slider's own handling of these keys is wrong, and `SliderCommitKeys` needs to include `Left`/`Right`/`Home` after all (accepting the no-op-commit-on-seek cost the "harmless superset" alternative was rejected over).
- No regression: mouse-drag commits on every `CommitSlider`-styled control still work exactly as before (one undo step per drag-release, not per-tick).

---

## Runners-up (not specced)

- **`MasterVolumeSlider`/`MixerMasterFader` still lack an independent undo step for a keyboard-driven edit**, even though (unlike the FX sliders) they don't lose dirty-tracking (see Deliberately not changed). A narrower follow-up would add the same `PreviewKeyUp` handling to their own wiring, mirroring `MasterVolumeSlider_PreviewMouseUp`.
- **`MainWindow_PreviewKeyDown`'s transport shortcuts (Left/Right/Home/Space) silently steal those keys from any focused control that isn't a `TextBox`**, not just a `Slider` — e.g. a focused `Button` or `CheckBox` also loses its own default Space/arrow behavior whenever this global handler runs first. Whether that's ever actually a problem for a control other than `Slider` wasn't investigated here; noted only because tracing fact 3 surfaced it as a more general shape than this one bug.
