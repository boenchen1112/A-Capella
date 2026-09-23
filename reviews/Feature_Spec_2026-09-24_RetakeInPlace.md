# Feature Spec — Retake a layer in place: re-record it, or replace it with a file, keeping its slot, name and FX

> Spec as of `7a38713` (after Bug Audit #8's fix + its headless tests, and `Improvement_Proposal_2026-09-24.md`). This is **Candidate 1** of that proposal. Every citation below was re-read from the current source tonight; the proposal's citations were checked and are accurate, with one correction called out in D5 (`IconGlyph`/`SourceStateLabel` are not bound in XAML). Where this spec departs from the proposal, the departure is argued once, in the decision that makes it.
>
> **Scope check (Pause Rule 2):** no new dependency (`ContextMenu`/`MenuItem`/`OpenFileDialog` are stock WPF / `Microsoft.Win32`). **The 4-layer cap is untouched:** `LayerCollection.MaxLayers` (`src/Acapella.Engine/Project/LayerModel.cs:61`) and the throw in `LayerCollection.Add` (`:69-70`) stay exactly as they are. A retake never calls `Add`, so it cannot exceed the cap; it only replaces the source of a layer that already counts towards it. The project file format does not change (every field touched is already serialized, `ProjectPersistenceService.cs:57-81`). The locked FX list, the 2x2 grid and MP4 output are untouched. **"Clear / remove layer" is explicitly out of scope** (see Known limitations). The one layout judgement — a small ↻ button in each populated strip's name row — is a **[human]** check.
>
> **Prior decisions this spec must not contradict, and how it honours them:**
> - **Bug Audit #5** (poll tracking cleared in the `LayerRowViewModel.Layer` setter; `ReleaseAll` only on Open/New): the retake **never** reassigns `row.Layer` (D5) and never releases hosted instances. Same `LayerId` ⇒ same live `(LayerId, Stage)` instances, exactly what `Undo_StillReusesTheSameLiveInstance` already pins.
> - **Bug Audit #7** (target cell read BEFORE `ShowDialog`; resolve the row only on success): the new-layer path keeps its #7 body byte-for-byte. The retake path reads its target (a `LayerId`) before `ShowDialog` and never calls `LowestFreeCell`/`RowForCell`/`Add`, so #7's temporary-`CellIndex` hazard cannot arise (subtlety (d)).
> - **Bug Audit #8** (export gate, synchronous before `ShowDialog`; one audio graph at a time): Re-record goes through the **same, single** gated helper as every other Recording-setup entry point (D4). Replace-with-file is ungated, argued like Upload (D7). No `await` is added anywhere in these paths (subtlety (c)).

---

## The feature in one sentence

Each populated mixer strip gets a small **↻ (Retake)** button that offers **Re-record...** (opens Recording setup in replace mode: the guide mix is every *other* layer, the cap check is skipped, and the new take replaces this layer's source) and **Replace with file...** (a single-file picker whose file replaces this layer's source). In both cases the layer keeps its `LayerId`, grid cell, name and entire FX/mix state (including live FabFilter instances), its trims and latency offset are reset because they described the old take, and the whole change is **one** undo step.

---

## Current behaviour (verified)

| Area | Where | What it does today |
|---|---|---|
| No way to change a layer's source | `src/Acapella.App/MainWindow.xaml:86-92` | Rec/Upload exist only in the `HasSource == false` panel (`:87`). Once a strip has a source, nothing in the UI can change it. There is no removal feature either (`HostedPluginService.cs:124-126`: "no layer-removal feature yet"; `LayerCollection.RemoveLast`, `LayerModel.cs:96-100`, has no caller, and its doc `:94-95` still points at a `StopRecordButton_Click` that no longer exists). The only way to redo a bad take is Undo back past it (losing every edit made since) or File > New. |
| Layer identity | `LayerModel.cs:14` (`LayerId` init-only), `:67-81` (`Add`) | `LayerId = CellIndex = _layers.Count` at `Add`; callers then override `CellIndex` to the row's slot. With no removal, live `LayerId`s are always exactly `{0..Count-1}` and unique; undo restores an earlier state with the same property. |
| Source fields are settable | `LayerModel.cs:15-18, 29-30, 39` | `Kind`, `SourcePath`, `CalibratedOffsetMs`, `ManualOffsetMs`, `TrimStartMs`, `TrimEndMs`, `AraArchiveKey` all have public setters. `MixParameters` is get-only (`:41`) — the object is owned by the layer for its lifetime. `SourceCacheKey()` (`:52-56`) = `path\|mtime\|trimStart\|trimEnd\|shift`. |
| Hosted FX instance identity | `HostedPluginInstanceCache.cs:25-34`, `FxSlot.cs:62-75`, `MixEngine.cs:99-122` | Instances are keyed `(LayerId, Stage)`. `GetOrCreate` applies `initialState` **only on creation** and ignores it on a hit (`:20-24` doc). `FxSlot.Insert` calls `GetOrCreateInstance` then `Reset` on every chain build. `ProjectSession.Snapshot` pulls live state into `MixParameters` (`MixEngine.SyncLiveStateIntoParameters :99-107`); undo/redo pushes it back into live instances (`PushSavedStateIntoLiveInstances :113-122`). ⇒ **Keeping the `LayerId` keeps the FX chain, live editor windows included.** |
| Melodyne ARA source | `HostedPluginService.cs:146-171` (`GetOrCreateAraLayerSource`), `MelodyneAraPitchCorrector.cs:39-80`, `PitchCorrectionCache.cs:23, 31-45`, `MixEngine.cs:200-204` | The ARA session is per layer and re-registers its audio source only when the content key (FNV hash of the samples, `:45, :82-92`) changes (`:156-168`). That code runs only inside `Correct()`, which runs only on a `PitchCorrectionCache` miss. The Manual2A cache key is `SourceCacheKey() + "-araEdit{gen}"` (`MixEngine.cs:200-204`); the static cache keeps **2 entries per (layer, backend)** (`:23`). ARA edit state is **not persisted**: `AraArchiveKey` is never written by the engine and `ImportState` is never called. |
| Row refresh | `src/Acapella.App/ViewModels/LayerRowViewModel.cs:73-95` | The `Layer` setter raises every source-derived property **and** clears `_openedHostedSlots` / `_lastPolledHostedState` (`:83-84`, Bug #5). `TrimStartMs` (`:149-153`) and `TrimEndText` (`:157-167`) are bound to the strip's In/Out TextBoxes (`MainWindow.xaml:112, 117`). `IconGlyph` (`:99-105`) and `SourceStateLabel` (`:137-143`) are **not bound anywhere in XAML** (grep hits only the VM). |
| Recording-setup entry points | `MainWindow.xaml.cs:500-527` (`OpenRecordSetup`), `:530` (per-strip Rec), `:534-543` (⏺ / Tools > Recording setup), `:548-552` (Tools > Calibrate latency) | All go through `OpenRecordSetup(resolveRow)`: #8 gate `:502-506` → `new RecordSetupWindow(...)` `:508` (the only construction in the app) → `ShowDialog` `:509` → BPM write-back `:510` → on success resolve the row, set `CellIndex`, `row.Layer`, name, button state, preview, status, `PushUndoSnapshot` (`:512-526`). |
| Dialog: cap check | `src/Acapella.App/RecordSetupWindow.xaml.cs:130-134` | `if (_layers.Layers.Count >= LayerCollection.MaxLayers)` → "Layer cap reached (4)." Unconditional: on a full project a retake would be refused. |
| Dialog: guide track | `RecordSetupWindow.xaml.cs:138, 154-185` | `nextLayerId = _layers.Layers.Count` (`:138`). `if (nextLayerId > 0)` (`:154`) reads the calibrated latency from settings (`:156-159`), builds `mixInputs` from **all** layers (`:162-163`), plays the guide (`:164, :181-182`) and renders a mono reference for post-take correlation (`:172`). Else branch (`:186-190`): no guide, offset 0. A retake as-is would play the old take of the layer being replaced into the singer's headphones. |
| Dialog: end of a take | `RecordSetupWindow.xaml.cs:284-317` (`StopRecording`) | M6 duration probe (`:305-310`, failure keeps the dialog open for a retry). Then **unconditionally** `_layers.Add(LayerKind.RecordedAV, path)` (`:312`), `CalibratedOffsetMs = MeasureCalibratedOffsetMs(...)` (`:313`), `CreatedLayer = layer` (`:314`), `DialogResult = true; Close()` (`:315-316`). `MeasureCalibratedOffsetMs` (`:238-273`) returns the settings fallback when the guide reference is empty (`:240-241`). `_pendingLayerId` (`:207, :212`) is written but never read. |
| Upload kind rule | `MainWindow.xaml.cs:579-590` (`AttachUploadedFile`), `:652-655` | `IsAudioOnlyExtension(ext) ? UploadedAudioOnly : UploadedVideo` (`:581-583`), inline in `AttachUploadedFile`. `LayerTimeline.FrameSource` (`LayerTimeline.cs:72-79`) shows the audio-only placeholder by `Kind`, so `Kind` must follow the new file. |
| Undo funnel | `MainWindow.xaml.cs:197-201`, `ProjectSession.cs:97-117, 157-170` | `PushUndoSnapshot` → `CommitEdit` pushes one snapshot and marks dirty. Undo/Redo `Restore` builds **new** `LayerModel` objects from the DTO (same `LayerId`s) and then pushes saved FX state into live instances. `ApplyRestore` (`:205-230`) rebuilds rows and reselects the previously selected strip **by `LayerId`** (`:217`). |
| Timer re-entrancy | `MainWindow.xaml.cs:121-130` | `_hostedStatePollTimer` (500 ms) can call `PushUndoSnapshot()` on any dispatcher pump, including inside modal loops. |
| Export snapshot timing | `MainWindow.xaml.cs:1032-1038` | The flag is cleared at `:1032`, then `await StopAsync()` (`:1033`), and only then `SnapshotForExport()` (`:1036`, a deep copy, M7) before `Task.Run` (`:1038`). |
| Preview holds live objects | `PreviewPlaybackEngine.cs:189-212` | `SetLayersAsync` stores `layers.ToList()` — a snapshot of **references** to the same `LayerModel` objects, not a deep copy. `RefreshAsync` = `SetLayersCore` + `SeekCore`; while stopped it renders video and asks `GetTailSeconds` (→ `GetOrCreateInstance`, no `Reset`/`ProcessBlock`). |
| Strip name row | `MainWindow.xaml:80-84` | A horizontal `StackPanel`: 8×8 color chip + `DisplayName` TextBlock with `TextTrimming` (inert inside a horizontal StackPanel, which measures with infinite width). The strip is `Width="76" Padding="6"` (`:65`); the populated panel already carries two 150 px vertical controls (`:103, :106`); the window's `MinHeight` is 600. |

---

## Desired behaviour

1. Every **populated** strip shows a small **↻** button at the right end of its name row. Hovering it shows the hint "Retake: re-record this layer or replace it with a file (keeps its FX; Ctrl+Z undoes)." Empty strips don't show it (they already have Rec/Upload).
2. Clicking ↻ opens a two-item menu: **Re-record...** and **Replace with file...**.
3. **Replace with file...** opens a single-select `OpenFileDialog` (same media filter as Upload). On OK, the layer's `SourcePath` becomes the file, its `Kind` follows the same audio-only rule as Upload, `TrimStartMs = 0`, `TrimEndMs = null`, `CalibratedOffsetMs = 0`, `ManualOffsetMs = 0`, `AraArchiveKey = null`. `LayerId`, `CellIndex`, `Name`, and all of `MixParameters` (gain, pan, mute, solo, every FX setting and hosted-state blob) are unchanged, and the live FabFilter instances keep running. The In/Out boxes show `0` / blank. The preview refreshes. Status: `Replaced {name} with {file}.` One undo step; the project turns dirty. Cancel changes nothing.
4. **Re-record...** opens Recording setup titled `Re-record {name}`. Record works on a full (4-layer) project. The guide track is the mix of **every other layer** (respecting their mute/solo); if there are no other layers, no guide plays and the take gets offset 0, exactly like a first take. When Stop succeeds (M6), the take replaces this layer's source as in point 3, except `Kind = RecordedAV` and `CalibratedOffsetMs` = the measured offset. Status: `Re-recorded {name}.` One undo step, which also carries any BPM change made in the dialog (as a new recording does today).
5. Re-record while an export is in flight refuses with `Finish the export first.` (Bug #8). Replace with file is allowed during an export.
6. Undo restores the old take (source, kind, trims, offset) in the same strip, still selected, with the same live FX instances; Redo re-applies the retake. The old take's file is never deleted (Pause Rule 1) — that is what makes undo work.
7. Toolbar ⏺, Tools > Recording setup, Tools > Calibrate latency and the per-strip Rec on an empty strip behave exactly as today.

---

## Design decisions (argued once, so the implementer doesn't re-decide)

**D1 — Mutate the existing `LayerModel` in place, through one new method `LayerModel.ReplaceSource(kind, path, calibratedOffsetMs)`. Do not swap in a new `LayerModel` object.** Everything the user wants kept — `LayerId` (which keys the hosted FX instances, the Melodyne backend and the meter tap), `CellIndex`, `Name`, the `MixParameters` object — lives on the existing object, so mutating it keeps all of it for free. Rejected alternatives:
- **(a) A new `LayerModel` via a `LayerCollection.Replace(index, newLayer)`.** `MixParameters` is get-only and there is no `Clone`; the new object would need a hand-written deep copy of ~25 mix fields including 5 hosted-state blobs, which silently goes stale the next time a field is added. And the row would have to be told via `row.Layer = newLayer`, which clears the #5 poll tracking (D5's counter-example).
- **(b) "Record as a new layer, then swap it in".** At the cap `Add` throws (`LayerModel.cs:69-70`), so the most important case — a full project — fails. Below the cap it mints a new `LayerId`, orphaning the FX chain and ARA session, and "swap" needs a removal operation that doesn't exist (and is out of scope).

`ReplaceSource` resets **every field that describes the old take and nothing else**. Two resets go beyond the proposal's list (trims + calibrated offset), argued here once:
- `ManualOffsetMs = 0`: it is a per-take nudge in the sync model (`GetShiftMs`, `:46`). v2 has no UI to set it (`LayerRowViewModel.cs:17-21`), so it is 0 in practice and the reset is a no-op today — but a nudge that aligned the old take has no meaning for a new one.
- `AraArchiveKey = null`: documented as keyed by `(layerId, sourceAudioHash)` (`LayerModel.cs:36-38`). A key for the old audio must not travel with the new audio. Unused today, so again a no-op, but correct if Melodyne persistence ever lands.

**D2 — The two new recording rules are pure engine functions in `Acapella.Engine.Project.RecordTakeRules`, parameterised by `int? retakeLayerId` (null = new layer).** The cap rule and the guide-layer rule are the objective, error-prone parts, so they are unit-testable without WPF, devices or FFmpeg (§1). The guide excludes the target **by `LayerId`**, which is sound because live `LayerId`s are unique (table row "Layer identity"). The null case is written as an explicit branch, not a lifted `l.LayerId != retakeLayerId` (which is also correct for null, but reads as if it might not be).

**New-layer mode is provably unchanged**, which is what protects #7 and every existing recording flow:
- Cap: `retakeLayerId is null && count >= Max` ≡ `count >= Max` when null.
- Guide condition: `GuideLayers(all, null)` is every layer, so `guideLayers.Count > 0` ≡ `_layers.Layers.Count > 0` ≡ the old `nextLayerId > 0`.
- Guide mix: same layers, same order, so the same `mixInputs`.
- `StopRecording`: the new-layer branch keeps the three original lines verbatim.

**D3 — In retake mode the dialog never touches the model. It returns `RetakeResult = (SourcePath, CalibratedOffsetMs)`; MainWindow applies it with `ReplaceSource` and pushes the undo step in one synchronous block.** The dialog already has to know retake-vs-new (for the cap and guide rules), but it doesn't know the row, and applying the take in the caller keeps "mutate + notify row + refresh + one push" in one place, next to the stale-target guard (D8). Rejected:
- **(a) The dialog calls `target.ReplaceSource` itself, for symmetry with `_layers.Add` at `:312`.** The model would change inside the modal loop, where the poll timer can push an undo snapshot between the mutation and MainWindow's push — an intermediate state as its own undo step (the invariant from the multi-file spec, §4). And a stale target would be mutated before anyone could check it.
- **(b) Also move `_layers.Add` out of the dialog for the new-layer path.** Arguably cleaner, but it rewrites the flow #7 just fixed, for no user-visible gain. Scope creep; not in this ticket.

**D4 — One gated helper, `ShowRecordSetupDialog(retakeRow)`, is the only place `RecordSetupWindow` is constructed.** It runs the #8 gate (synchronously), constructs the dialog with `retakeRow?.Layer?.LayerId`, sets the retake title, calls `ShowDialog`, writes BPM back, and returns `(dialog, ShowDialog() == true)`, or null when the gate refuses. `OpenRecordSetup` (new layers, #7 body unchanged) and `RetakeRecord` (new) both call it. This makes "#8's gate covers every way to open Recording setup" true by construction — a [review] item is simply "grep finds exactly one `new RecordSetupWindow(`". Rejected: `RetakeRecord` copying the gate and the construction (two gates drift; the next entry point copies the wrong one). Rejected: reusing `OpenRecordSetup(() => row)` for the retake — its success block assigns `CellIndex`, calls `row.Layer = dialog.CreatedLayer` and adds a layer, all wrong for a retake (subtlety (d)). The helper returns `ShowDialog()`'s value rather than having callers read `dialog.DialogResult` after the window has closed.

**D5 — The row is refreshed through a new `LayerRowViewModel.NotifySourceReplaced()`, never through the `Layer` setter.** The obvious implementation, `row.Layer = row.Layer;` "to refresh the bindings", is wrong. **Counter-example:** the user has Pro-Q 4 open on layer 2 (so `FxSlot.Eq ∈ _openedHostedSlots`), replaces layer 2's source, then tweaks a band in the still-open Pro-Q window. With the setter, `_openedHostedSlots` was cleared (`:83`), so `PollHostedStateChanges` no longer looks at that slot: the tweak neither refreshes the preview nor becomes an undo step, until the user happens to reopen the editor from the strip. With `NotifySourceReplaced()` the tracking is untouched and the poll keeps working. It raises exactly the source-derived properties: `TrimStartMs` and `TrimEndText` (**these two are the visible ones** — the In/Out boxes would otherwise keep showing the old take's trims), plus `IconGlyph` and `SourceStateLabel` (currently unbound — this corrects the proposal, which listed them as the visibly stale fields — raised anyway so a future binding is correct). `Name`, `DisplayName`, `CellColor` and all mix properties are unchanged by `ReplaceSource`, so they are not raised.

**D6 — UI: one ↻ button in the strip's name row that opens a two-item menu built in code.** The name row becomes a `DockPanel` with the button docked right, so the strip gains **no height** (MinHeight 600 is already tight, with two 150 px controls per strip). A side effect: `DisplayName`'s `TextTrimming` starts working, because a DockPanel's fill child gets a finite width. The menu is built in the Click handler, with lambdas capturing the row, instead of a XAML `ContextMenu` — a XAML ContextMenu lives in its own visual tree, and its `DataContext` / `Click="..."` wiring inside a `DataTemplate` is a known WPF trap. Building it in code has no binding at all. Rejected:
- **(a) Two buttons ("Re-rec", "Replace") in the populated panel.** They add ~50 px of height per strip, at a MinHeight that already just fits.
- **(b) A right-click context menu on the strip.** Nothing else in the app uses right-click, and the hint system is hover-based, so it would be undiscoverable.
- **(c) Edit-menu items acting on the selected strip.** The selection may be Master, and "which layer will this replace?" becomes invisible state.

The exact look is a **[human]** check.

**D7 — Replace-with-file reuses Upload's kind rule (extracted to `UploadedKindFor`), has no export gate, and doesn't touch the add-layer buttons.**
- **Kind rule.** Extracting it keeps one kind rule for Upload, Import and Replace, so point 3 of Desired behaviour ("Kind follows the same rule as Upload") holds by construction.
- **No export gate, the same argument the multi-file spec made for Upload.** This is a model edit plus `RefreshPreviewLive()`. With the preview stopped, `RefreshAsync` only renders video and asks `GetTailSeconds` (no `Reset`/`ProcessBlock` on shared instances). The preview must be stopped, because Play is gated during export and Export stops it at `:1033`. Also, the export works from a deep copy (M7).
- **One precision the proposal lacked.** The copy is taken at `:1036`, *after* the `await StopAsync()` at `:1033`. So a Replace landing during that await is **included** in the export, and one landing after `:1036` is not. That is the same (benign) behaviour as a trim edit or an Upload in the same window: the snapshot is synchronous, so it can never be torn.
- **No `UpdateAddLayerButtonState()`.** The layer count doesn't change.

**D8 — Stale-row guard: the target must still be a live row holding a live layer, checked when a menu item is clicked and again after any dialog, and the re-record target is compared by reference.** `IsLiveRow(row) = _tracks.Contains(row) && row.Layer is not null && _layers.Layers.Contains(row.Layer)`. The failure it prevents:
- Undo/Open/New run `RestoreTracksFromLayers`, which builds **new** rows and **new** `LayerModel` objects.
- If that happens between the ↻ click and the apply, `ReplaceSource` would mutate a detached object, the retake would silently vanish, and `PushUndoSnapshot` would record a no-op undo step (and mark the project dirty for nothing).

Can it actually happen?
- **During `RecordSetupWindow`:** no. It is modal and disables its owner, so no Undo can run. The post-dialog check is therefore defensive, like #7's "unreachable in practice" comment at `MainWindow.xaml.cs:515`.
- **While the ↻ menu is open:** it can't be ruled out read-only. Whether Ctrl+Z routes to the window's CommandBindings through the `ContextMenu`'s `PlacementTarget` is WPF routing behaviour this spec did not verify.
- **Hence** the guard is cheap and placed at both points. It is not presented as a fix for an observed bug.

---

## Ordering and timing subtleties (each with the counter-example that motivates it)

**(a) The guide condition and the guide mix must come from the same filtered list.** Take a retake of the **only** layer, with the exclusion applied to `mixInputs` but the old `nextLayerId > 0` condition kept (`nextLayerId` = 1):
- The guide branch runs with **zero** inputs. `BuildMix` returns an empty `MixingSampleProvider` (headroom `1f`, `MixEngine.cs:160`), and `RenderMonoReference` returns `float[0]`.
- `MeasureCalibratedOffsetMs` hits `:240-241` and returns the **settings fallback**, e.g. 180 ms.
- Result: a take recorded against nothing gets `CalibratedOffsetMs = 180`. `GetShiftMs()` is then −180, so 180 ms of the singer's first note is trimmed off. The status line also says "with guide track" while nothing plays.

With `if (guideLayers.Count > 0)` over the same list, it takes the no-guide branch and gets offset 0, exactly like a first take. Test: `GuideLayers_RetakeOfTheOnlyLayer_IsEmpty`; objective human check: the saved JSON shows `"CalibratedOffsetMs": 0`.

**(b) Exclude the target from the input list; don't mute it.** "Build the guide from all layers with the target muted" looks equivalent and isn't:
- If the target is the only soloed layer, `anySolo` is computed over the inputs (`MixEngine.cs:147`), so it stays `true`.
- Every non-soloed layer is therefore muted by solo logic. The target is muted by us, and mute beats solo: `effectiveMute = Mute || (anySolo && !Solo)`, `MixEngine.cs:220`. The guide is silent.

With exclusion, the target isn't an input, `anySolo` is false, and the others play. Test: `GuideMix_RetakeOfTheOnlySoloedLayer_IsNotSilent` (it also asserts that the "mute instead" variant **is** silent, so the test fails if someone "simplifies" to muting). The same logic means a *different* soloed layer still solos correctly within the guide — which is today's behaviour for new takes.

**(c) No `await` anywhere between the #8 gate and `ShowDialog`, and none in the retake handlers.** The proposal's natural "stop the preview first, since the bad take is audible in it" (#8 runner-up 1) is the trap:
- Put `await AwaitPreviewCommand(_previewEngine.StopAsync())` in `RetakeRecord` after the gate.
- During that await the dispatcher pumps, and the user clicks Export.
- In the common case the stop finishes while Export's `SaveFileDialog` (`:1021-1022`) is still open, which is harmless.
- The collision needs a **slow** stop. Example: the `StopAsync` is queued on the serialized preview command thread behind an in-flight rebuild (a debounced FX refresh, or a Melodyne `Correct()`). The user then confirms the Save dialog before it completes. `ExportButton_Click` clears the flag (`:1032`), snapshots, and `Task.Run` starts processing through the shared `(LayerId, Stage)` instances.
- The retake continuation then resumes and opens the dialog without re-checking the gate. Record builds the guide via `BuildMix` → `FxSlot.Insert` → `Reset` + `ProcessBlock` on those **same instances**, concurrently with the export thread. That is exactly the Bug #8 corruption.

The rule: if a preview stop is ever added (a separate ticket), the gate must be **re-checked after the await**. This spec adds no stop; preview-plays-into-recording stays the inherited #8 runner-up 1.

**(d) The Re-record target is fixed BEFORE `ShowDialog`, as a `LayerId` plus an object reference; nothing about it is resolved after.** The guide exclusion needs the id when Record is clicked, inside the dialog, so it has to be passed in at construction. #7's hazard was that `StopRecording`'s `_layers.Add` gives the new layer a temporary `CellIndex = Count`, which `LowestFreeCell` then misreads if it is called after the dialog. The retake path never calls `Add`, `LowestFreeCell` or `RowForCell`, and it never assigns `CellIndex`, so the hazard has nothing to act on. What *would* reintroduce a #7-shaped bug is routing the retake through `OpenRecordSetup`'s success block. That block computes `CellIndex = row.SlotNumber - 1` and `row.Layer = dialog.CreatedLayer`, and `CreatedLayer` is null in retake mode. At best it silently does nothing; if someone "fixes" that by also setting `CreatedLayer`, it adds a fifth layer at the cap and throws. Hence D4: a separate caller over the shared helper.

**(e) No dispatcher pumping from `ReplaceSource` to `PushUndoSnapshot`.** This is the same invariant as the multi-file import (§4 of that spec). The poll timer can push on any pump. A `MessageBox` or an `await` after `ReplaceSource` but before the push would let the timer record the retake as its own step, attributed to "a plugin tweak" (harmless). Worse, a pump after `NotifySourceReplaced` but before `RefreshPreviewLive` would show a half-applied UI. Both handlers do: mutate → notify → `RefreshPreviewLive()` (an `async void` that returns at its first await without pumping) → status → push, in one synchronous run. A modal dialog *before* the mutation (the file picker, Recording setup) is fine: the snapshot the timer might push there is the pre-retake state, the same as for Upload.

**(f) Melodyne re-registration is lazy, and undo can leave the ARA session holding the wrong take (pre-existing mechanism, newly easy to reach).**
- **Forward (verified):** the new path changes `SourceCacheKey()`, so the next Manual2A chain build misses `PitchCorrectionCache`, and `Correct()` runs. The content hash differs, so `GetOrCreateAraLayerSource` re-registers the new audio (`HostedPluginService.cs:156-168`). This happens at the next audio chain build: the next Play, a refresh *while playing*, or an export. It does **not** happen at the moment of the retake, and not on a refresh while stopped, which only renders video and asks `GetTailSeconds` (`PreviewPlaybackEngine.cs:197-205`). Opening the Melodyne editor before any rebuild shows the old take until then.
- **Undo (the gap):** the cache keeps 2 entries per (layer, backend), so the old take's key is very likely still cached. `Correct()` is then skipped, and the ARA session keeps the **new** take's audio. Playback and export use the cached old-take render, while the Melodyne editor shows the new take. The user's first edit in that editor bumps the edit generation and forces a miss. `Correct()` then re-registers the old audio, **discarding that edit**.
- The same sequence exists today with trim → undo. This ticket just makes "change the audio, then undo" a routine flow. It does **not** block this ticket: the FabFilter half is fully verified, and Melodyne state isn't persisted at all yet. It is listed under Known limitations with a fix shape.

**(g) The preview may briefly read a half-replaced layer.** `PreviewPlaybackEngine` holds references to the live `LayerModel` objects (`:189-193`). A chain build already running on the preview command thread when `ReplaceSource` runs on the UI thread can read, for example, the new `SourcePath` with the old `TrimStartMs`. That build is superseded by the `RefreshAsync` that `RefreshPreviewLive()` queues right after. Trim and name edits have exactly the same exposure today. It is accepted, not fixed here (a fix would be the preview snapshotting DTOs, which is a separate change).

---

## Proposed implementation

### 1. `src/Acapella.Engine/Project/LayerModel.cs`: `ReplaceSource` (+ fix a stale doc)

Add to `LayerModel`, after `SourceCacheKey()`:

```csharp
    /// <summary>Retake in place (retake spec D1): points this layer at a new take while keeping its
    /// slot identity (LayerId, CellIndex), Name and MixParameters -- so its hosted FX instances,
    /// keyed by (LayerId, Stage), and their saved state carry over untouched. Resets everything that
    /// described the OLD take: trims (ms into the old file), the manual nudge and calibrated latency
    /// (both per-take), and the ARA archive key (keyed by source-audio hash).</summary>
    public void ReplaceSource(LayerKind kind, string sourcePath, double calibratedOffsetMs)
    {
        Kind = kind;
        SourcePath = sourcePath;
        CalibratedOffsetMs = calibratedOffsetMs;
        ManualOffsetMs = 0;
        TrimStartMs = 0;
        TrimEndMs = null;
        AraArchiveKey = null;
    }
```

Optional one-line doc fix, allowed because the file is touched anyway. `RemoveLast`'s summary (`:94-95`) names `MainWindow.StopRecordButton_Click`, which no longer exists. Replace it with: `/// <summary>Removes the most recently added layer. Currently unused: M6 probes before Add (RecordSetupWindow.StopRecording), so no zombie layer is ever added.</summary>`. **Don't** delete `RemoveLast` or its tests (`LayerCollectionTests`); removal is out of scope.

### 2. New file: `src/Acapella.Engine/Project/RecordTakeRules.cs`

```csharp
namespace Acapella.Engine.Project;

/// <summary>Pure rules for starting a take in the Recording setup dialog (retake spec D2).
/// retakeLayerId == null means "record a new layer" and must reproduce the pre-retake behaviour
/// exactly: same cap check, guide = every layer in collection order.</summary>
public static class RecordTakeRules
{
    /// <summary>A new take is refused at the cap; a retake replaces an existing layer's source,
    /// so it never adds one and is never blocked by the cap.</summary>
    public static bool IsBlockedByLayerCap(int layerCount, int? retakeLayerId) =>
        retakeLayerId is null && layerCount >= LayerCollection.MaxLayers;

    /// <summary>The layers the singer hears as the guide: every layer for a new take; every layer
    /// EXCEPT the one being replaced for a retake. Excluded from the input list, not muted --
    /// muting a soloed target would keep anySolo true and silence the whole guide (spec (b)).
    /// The caller must use this same list for BOTH "is there a guide?" and the guide mix (spec (a)).</summary>
    public static IReadOnlyList<LayerModel> GuideLayers(IEnumerable<LayerModel> layers, int? retakeLayerId)
    {
        if (retakeLayerId is not int target)
            return layers.ToList();
        return layers.Where(l => l.LayerId != target).ToList();
    }
}
```

### 3. `src/Acapella.App/RecordSetupWindow.xaml.cs`

**Class doc (`:17-23`):** append the sentence: "In retake mode (retakeLayerId set) it never touches the model: the take is exposed via RetakeResult and MainWindow applies it to the existing layer (retake spec D3)."

**Fields / properties / constructor** (`:44`, `:51-65`):

```csharp
    public LayerModel? CreatedLayer { get; private set; }

    /// <summary>Retake mode only (retake spec D3): the new take's file and measured latency offset,
    /// for MainWindow to apply to the existing layer via LayerModel.ReplaceSource. Null in new-layer
    /// mode, and in retake mode until a take passes M6.</summary>
    public (string SourcePath, double CalibratedOffsetMs)? RetakeResult { get; private set; }

    /// <summary>Null = record a new layer (the pre-retake behaviour). Otherwise the LayerId whose
    /// source this take will replace: excluded from the guide, and exempt from the layer cap.</summary>
    private readonly int? _retakeLayerId;
```

```csharp
    public RecordSetupWindow(DeviceCatalog deviceCatalog, SettingsService settingsService, LayerCollection layers, MixEngine mixEngine, string mediaDir, double initialBpm, int? retakeLayerId = null)
    {
        InitializeComponent();
        _deviceCatalog = deviceCatalog;
        _settingsService = settingsService;
        _calibrator = new LatencyCalibrator(_settingsService);
        _layers = layers;
        _mixEngine = mixEngine;
        _mediaDir = mediaDir;
        _retakeLayerId = retakeLayerId;
        _outputDevice = _deviceCatalog.GetDefaultRenderDevice();
        _metronome.Bpm = initialBpm;
        BpmTextBox.Text = initialBpm.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        RefreshDevices();
    }
```

**`RecordButton_Click`**: replace `:130-138` and `:154-190`. Everything else is unchanged, including the device check `:124-128`, the allocator and overwrite guard `:139-148`, capture start/wait order `:179-182`, the metronome `:192-199` and the UI state `:201-204`.

```csharp
        if (RecordTakeRules.IsBlockedByLayerCap(_layers.Layers.Count, _retakeLayerId))   // retake spec D2
        {
            StatusText.Text = "Layer cap reached (4).";
            return;
        }

        var videoDevice = _dshowVideoDevices[CameraCombo.SelectedIndex];
        var dshowAudioDevice = _dshowAudioDevices[MicCombo.SelectedIndex];
        int takeLayerId = _retakeLayerId ?? _layers.Layers.Count;           // status text only (+ the unused _pendingLayerId)
        string takeVerb = _retakeLayerId is null ? "Recording" : "Re-recording";
        // Retake spec (a): ONE list drives both "is there a guide?" and the guide mix. For a new take
        // it is every layer, so Count > 0 is exactly the old `nextLayerId > 0`.
        var guideLayers = RecordTakeRules.GuideLayers(_layers.Layers, _retakeLayerId);
        Directory.CreateDirectory(_mediaDir);
        string outputPath = RecordingPathAllocator.Allocate(_mediaDir);   // bug audit #6: never an existing file
```

(`:142-152` unchanged.)

```csharp
        if (guideLayers.Count > 0)
        {
            var loopbackDevice = FindLoopbackDevice();
            calibratedOffsetMs = loopbackDevice is not null
                ? _settingsService.GetLatencyOffsetMs(loopbackDevice.Id, _outputDevice.Id) ?? 0
                : 0;

            const int sampleRate = 44100;
            var timeline = new LayerTimeline(_mixEngine);
            var mixInputs = guideLayers.Select(l => timeline.AudioInput(l, sampleRate)).ToList();
            var guideMix = _mixEngine.BuildMix(mixInputs, sampleRate);

            // (existing comment :166-171 unchanged)
            _pendingGuideReferenceMono = RenderMonoReference(_mixEngine.BuildMix(mixInputs, sampleRate));

            // (existing comment :174-178 unchanged)
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            _activeCapture.WaitForCaptureStarted(TimeSpan.FromSeconds(3));
            _guideTrackPlayer = new GuideTrackPlayer();
            _guideTrackPlayer.Play(_outputDevice.Id, guideMix);

            StatusText.Text = $"{takeVerb} layer {takeLayerId} with guide track (offset {calibratedOffsetMs:F1}ms)...";
        }
        else
        {
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            StatusText.Text = $"{takeVerb} layer {takeLayerId}...";
        }
```

Then at `:207`: `_pendingLayerId = takeLayerId;`. The status keeps today's zero-based `LayerId` numbering. Changing it would change new-take status text and is out of scope (`TODO(polish)` if desired).

**`StopRecording`**: replace `:312-314`. The M6 block above and `DialogResult = true; Close();` below are unchanged.

```csharp
        if (_retakeLayerId is null)
        {
            var layer = _layers.Add(LayerKind.RecordedAV, _pendingOutputPath);                              // unchanged
            layer.CalibratedOffsetMs = MeasureCalibratedOffsetMs(_pendingOutputPath, _pendingCalibratedOffsetMs); // unchanged
            CreatedLayer = layer;                                                                            // unchanged
        }
        else
        {
            // Retake spec D3: never mutate the model from inside the modal loop -- MainWindow applies
            // this to the existing layer and pushes the undo step in one synchronous block.
            RetakeResult = (_pendingOutputPath, MeasureCalibratedOffsetMs(_pendingOutputPath, _pendingCalibratedOffsetMs));
        }
        DialogResult = true;
        Close();
```

The dialog's in-window heading (`RecordSetupWindow.xaml:11`, unnamed TextBlock "Recording setup") is left alone; the window title carries the retake context (§5). No XAML change in this file.

### 4. `src/Acapella.App/ViewModels/LayerRowViewModel.cs`: `NotifySourceReplaced`

Add after the `Layer` property (`:95`):

```csharp
    /// <summary>Retake spec D5: the wrapped LayerModel's source was replaced IN PLACE (same object,
    /// same LayerId, same MixParameters). Raises only the source- and trim-derived properties.
    /// Deliberately NOT `Layer = Layer`: the setter clears _openedHostedSlots/_lastPolledHostedState
    /// (bug audit #5), which would silently stop polling a FabFilter editor still open on this layer.</summary>
    public void NotifySourceReplaced()
    {
        OnPropertyChanged(nameof(TrimStartMs));        // visible: the strip's In box
        OnPropertyChanged(nameof(TrimEndText));        // visible: the strip's Out box
        OnPropertyChanged(nameof(IconGlyph));          // not bound today; raised so a future binding is right
        OnPropertyChanged(nameof(SourceStateLabel));   // not bound today; same
    }
```

### 5. `src/Acapella.App/MainWindow.xaml.cs`

**(i) The single gated dialog helper, and `OpenRecordSetup` on top of it** (replaces `:497-527`):

```csharp
    /// <summary>The ONLY place RecordSetupWindow is constructed (retake spec D4), so bug audit #8's
    /// export gate covers every way into Recording setup. Returns null when the gate refuses
    /// (StatusText already set); otherwise the closed dialog and whether a take was accepted.
    /// Invariant (bug audit #8): no await / dispatcher pumping between the gate and ShowDialog.</summary>
    private (RecordSetupWindow Dialog, bool Recorded)? ShowRecordSetupDialog(LayerRowViewModel? retakeRow)
    {
        if (!ExportMenuItem.IsEnabled)
        {
            StatusText.Text = "Finish the export first.";   // bug audit #8
            return null;
        }

        var dialog = new RecordSetupWindow(_deviceCatalog, _settingsService, _layers, _mixEngine, _mediaDir, _session.MetronomeBpm,
                                           retakeLayerId: retakeRow?.Layer?.LayerId) { Owner = this };
        if (retakeRow is not null) dialog.Title = $"Re-record {retakeRow.DisplayName}";
        bool recorded = dialog.ShowDialog() == true;
        _session.MetronomeBpm = dialog.Bpm;
        return (dialog, recorded);
    }

    /// <summary>Opens the capture-setup dialog for a NEW layer. If a take is recorded, attaches it to
    /// the row that resolveRow returns. resolveRow runs ONLY on success, AFTER the dialog closes, so a
    /// cancelled or calibrate-only session never creates or claims a row (bug audit #7).</summary>
    private void OpenRecordSetup(Func<LayerRowViewModel?> resolveRow)
    {
        var shown = ShowRecordSetupDialog(retakeRow: null);
        if (shown is null) return;
        var (dialog, recorded) = shown.Value;

        if (recorded && dialog.CreatedLayer is not null)
        {
            // (body :514-525 unchanged, byte for byte)
        }
    }
```

**(ii) The ↻ button handler, the two retake actions and the guard.** Put these right after `CalibrateLatencyMenuItem_Click` (`:552`):

```csharp
    // ----- Retake a layer in place (retake spec) -----

    /// <summary>Retake spec D8: the row is still on screen and still wraps a layer that is in the live
    /// collection (Undo/Open/New rebuild rows AND LayerModels, so a captured row can go stale).</summary>
    private bool IsLiveRow(LayerRowViewModel row) =>
        _tracks.Contains(row) && row.Layer is not null && _layers.Layers.Contains(row.Layer);

    /// <summary>A populated strip's ↻ button: a two-item menu built in code (spec D6 -- a XAML
    /// ContextMenu inside a DataTemplate has its own DataContext/visual-tree pitfalls).</summary>
    private void RetakeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not LayerRowViewModel row || !IsLiveRow(row)) return;

        var reRecord = new MenuItem { Header = "Re-record..." };
        reRecord.Click += (_, _) => RetakeRecord(row);
        var replace = new MenuItem { Header = "Replace with file..." };
        replace.Click += (_, _) => RetakeReplaceWithFile(row);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(reRecord);
        menu.Items.Add(replace);
        menu.IsOpen = true;
    }

    /// <summary>Re-record this layer in place: guide = the OTHER layers, not blocked by the cap, same
    /// LayerId/cell/name/FX; one undo step. Goes through the single #8-gated helper (spec D4).
    /// Never calls LowestFreeCell/RowForCell/Add (spec (d)); no await anywhere (spec (c)).</summary>
    private void RetakeRecord(LayerRowViewModel row)
    {
        if (!IsLiveRow(row)) return;
        var target = row.Layer!;                                          // fixed BEFORE ShowDialog (spec (d))

        var shown = ShowRecordSetupDialog(row);
        if (shown is null) return;                                        // #8 gate refused; status already set
        var (dialog, recorded) = shown.Value;
        if (!recorded || dialog.RetakeResult is not { } take) return;     // cancelled / closed: nothing changes

        // Defensive (spec D8): unreachable in practice -- the dialog is modal and disables this window.
        if (!IsLiveRow(row) || !ReferenceEquals(row.Layer, target))
        {
            StatusText.Text = $"The layer changed while recording; the take was kept as {Path.GetFileName(take.SourcePath)} but not attached.";
            return;
        }

        // Invariant (spec (e)): no await / MessageBox / dialog from here to PushUndoSnapshot().
        target.ReplaceSource(LayerKind.RecordedAV, take.SourcePath, take.CalibratedOffsetMs);
        row.NotifySourceReplaced();                                       // NOT row.Layer = ... (spec D5)
        RefreshPreviewLive();
        StatusText.Text = $"Re-recorded {row.DisplayName}.";
        PushUndoSnapshot();                                               // ONE step: source + trims + offset (+ any BPM change)
    }

    /// <summary>Replace this layer's source with a file, in place: same kind rule as Upload, trims and
    /// offsets reset, LayerId/cell/name/FX kept; one undo step. No export gate, like Upload (spec D7).</summary>
    private void RetakeReplaceWithFile(LayerRowViewModel row)
    {
        if (!IsLiveRow(row)) return;

        var dialog = new OpenFileDialog { Filter = MediaFileFilter, Title = $"Replace {row.DisplayName} with file" };
        if (dialog.ShowDialog() != true) return;
        if (!IsLiveRow(row)) return;                                      // defensive (spec D8)

        // Invariant (spec (e)): no await / MessageBox / dialog from here to PushUndoSnapshot().
        row.Layer!.ReplaceSource(UploadedKindFor(dialog.FileName), dialog.FileName, calibratedOffsetMs: 0);
        row.NotifySourceReplaced();                                       // NOT row.Layer = ... (spec D5)
        RefreshPreviewLive();
        StatusText.Text = $"Replaced {row.DisplayName} with {Path.GetFileName(dialog.FileName)}.";
        PushUndoSnapshot();
    }
```

(`Button`, `ContextMenu`, `MenuItem` come from `System.Windows.Controls`, `PlacementMode` from `System.Windows.Controls.Primitives`, `OpenFileDialog` from `Microsoft.Win32`. All already imported, `MainWindow.xaml.cs:6-7, 19`.)

**(iii) Share the kind rule** (replaces `:579-583`):

```csharp
    /// <summary>The single "what LayerKind is an uploaded file" rule -- Upload, Import and Replace with
    /// file all use it (retake spec D7).</summary>
    private static LayerKind UploadedKindFor(string filePath) =>
        IsAudioOnlyExtension(Path.GetExtension(filePath)) ? LayerKind.UploadedAudioOnly : LayerKind.UploadedVideo;

    private void AttachUploadedFile(LayerRowViewModel row, string filePath)
    {
        var layer = _layers.Add(UploadedKindFor(filePath), filePath);
        // (rest of the body :585-589 unchanged)
    }
```

### 6. `src/Acapella.App/MainWindow.xaml`: the strip's name row

Replace `:81-84`:

```xml
                    <!-- Name row. DockPanel (not a horizontal StackPanel) so the ↻ button docks right
                         without adding height, and DisplayName's TextTrimming actually trims. -->
                    <DockPanel Margin="0,0,0,4" LastChildFill="True">
                        <Button DockPanel.Dock="Right" Content="↻" FontSize="10" Padding="2,0" MinWidth="0" Margin="2,0,0,0"
                                Visibility="{Binding HasSource, Converter={StaticResource BoolToVis}}"
                                Click="RetakeButton_Click"
                                MouseEnter="Hint_MouseEnter" Tag="Retake: re-record this layer or replace it with a file (keeps its FX; Ctrl+Z undoes)."/>
                        <Border DockPanel.Dock="Left" Width="8" Height="8" CornerRadius="2" Background="{Binding CellColor}" Margin="0,0,4,0" VerticalAlignment="Center"/>
                        <TextBlock Text="{Binding DisplayName}" FontSize="10" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                    </DockPanel>
```

Clicking ↻ goes through the same button-in-strip mouse routing as the existing Rec/Upload buttons in this template (`:88-91`). Whether the strip also becomes selected is whatever those buttons already do, and nothing here depends on it.

### 7. Deliberately **not** changed

- `LayerCollection`, `MaxLayers`, the `Add` throw. `RemoveLast` stays (doc comment fix only, optional).
- `ProjectSession`, `ProjectPersistenceService`, and the DTO: no new API and no format change (D1).
- `HostedPluginService`, `HostedPluginInstanceCache`, `MixEngine`, `PitchCorrectionCache`: no release, no eviction. The (f) fix is a separate ticket.
- `OpenRecordSetup`'s success block, `RecordingSetupMenuItem_Click`, `CalibrateLatencyMenuItem_Click`, `LowestFreeCell`, `RowForCell`: #7's flow is untouched (D2's equivalence argument, D3(b)).
- `ApplyRestore`, `RestoreTracksFromLayers`, `PushUndoSnapshot`: undoing a retake is an ordinary snapshot restore.
- `ExportButton_Click`: no new gate on Replace (D7). Recording setup keeps the existing #8 gate, now in the helper.
- No preview stop before re-recording (subtlety (c)). The in-window heading of `RecordSetupWindow` is unchanged.
- The old take's file is never deleted (Pause Rule 1).

---

## Tests

### `tests/Acapella.Engine.Tests/Project/LayerRetakeTests.cs` (new)

Plain `[Fact]`s. No WPF, no native DLL, no devices. Fake path strings are fine except in test 3, which only needs the path string to differ: `SourceCacheKey` uses mtime 0 for missing files.

1. **`ReplaceSource_SwapsKindAndPath_AndResetsTakeSpecificTiming`**
   - Setup: `Add(UploadedVideo, "old.mp4")`, then set `TrimStartMs = 300`, `TrimEndMs = 9000`, `CalibratedOffsetMs = 120`, `ManualOffsetMs = 15`, `AraArchiveKey = "k"`.
   - Act: `ReplaceSource(RecordedAV, "new.mkv", 40)`.
   - Expect: `Kind == RecordedAV`, `SourcePath == "new.mkv"`, `TrimStartMs == 0`, `TrimEndMs == null`, `CalibratedOffsetMs == 40`, `ManualOffsetMs == 0`, `AraArchiveKey == null`, `GetShiftMs() == -40`.
2. **`ReplaceSource_KeepsSlotIdentityNameAndMix`**
   - Setup: two layers, then on layer 1 set `CellIndex = 3`, `Name = "Alto"`, `MixParameters.GainDb = -6`, `Pan = 0.5`, `Solo = true`, and one hosted-state blob (`MixParameters.EqHostedState = new byte[]{7}`, `LayerMixParameters.cs:57`).
   - Capture `var mix = layer.MixParameters`.
   - Act: `ReplaceSource(UploadedAudioOnly, "x.wav", 0)`.
   - Expect: `LayerId == 1`, `CellIndex == 3`, `Name == "Alto"`, `Assert.Same(mix, layer.MixParameters)`, and all four values unchanged.
3. **`ReplaceSource_ChangesSourceCacheKey`**: the key before is not equal to the key after (this is what forces the Manual2A / `PitchCorrectionCache` miss in (f), forward direction).
4. **`ReplaceSource_OnAFullProject_LeavesTheLayerCountAndIdsUnchanged`**: 4 × `Add`, then `ReplaceSource` on layer 2 → `Layers.Count == 4`, the ids are `[0,1,2,3]`, and `Assert.Same` holds for every element (no object swapped).
5. **`IsBlockedByLayerCap_NewTakeAtTheCap_IsBlocked`**: `(MaxLayers, null)` → true.
6. **`IsBlockedByLayerCap_RetakeAtTheCap_IsNotBlocked`**: `(MaxLayers, 2)` → false.
7. **`IsBlockedByLayerCap_NewTakeBelowTheCap_IsNotBlocked`**: `(MaxLayers - 1, null)` → false, and `(0, null)` → false.
8. **`GuideLayers_NewTake_IsEveryLayerInOrder`**: 3 layers, null → the same three objects in collection order. This is the D2 equivalence with today's `_layers.Layers`.
9. **`GuideLayers_Retake_ExcludesExactlyTheTarget`**: 3 layers, retake id 1 → `[layer0, layer2]`, in order.
10. **`GuideLayers_RetakeOfTheOnlyLayer_IsEmpty`**: 1 layer, retake id 0 → empty. This pins subtlety (a).
11. **`GuideMix_RetakeOfTheOnlySoloedLayer_IsNotSilent`**
    - Setup:
      - `var engine = new MixEngine(NoHostedPluginsAvailable.Instance)` (as in `MixEngineTests`).
      - Two layers; layer 0 has `MixParameters.Solo = true`.
      - Inputs: `new MixLayerInput(l.LayerId, Sine(440, 0.3f, 1 s), 44100, l.MixParameters)` for each layer in `GuideLayers(layers, retakeLayerId: 0)`.
      - Use a local sine helper and a local `ReadAll`, copied from `MixEngineTests.cs:22`.
    - Assert: the peak of the rendered buffer is `> 0.01`.
    - **Contrast half:** build from *both* layers with layer 0 additionally `Mute = true` → peak `== 0` (±1e-6). This proves "mute instead of exclude" would silence the guide, so a future "simplification" fails this test.

### `tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs` (append one test)

12. **`Retake_ThenUndo_RestoresTheOldTake_AndKeepsTheSameLivePluginInstance`**, using the existing fixture (`_mixEngine`, `OpenEqEditorLiveInstance`).
    - **Setup:**
      - `session = new ProjectSession(_mixEngine)`, `layer = session.Layers.Add(UploadedVideo, "take1.mp4")`, `layer.TrimStartMs = 300`, `session.CommitEdit()`.
      - `plugin = OpenEqEditorLiveInstance(layer)`, then `plugin.TweakInEditor(new byte[]{4,2})`, then `session.CommitEdit()`.
    - **Retake:** `layer.ReplaceSource(LayerKind.RecordedAV, "take2.mkv", 40)`, then `session.CommitEdit()`.
      - Assert the same `LayerId`.
      - `Assert.Same(plugin, OpenEqEditorLiveInstance(layer))`.
      - `session.Snapshot().Layers.Single().MixParameters.EqHostedStateBase64 == Base64({4,2})`.
    - **Undo:** `session.Undo()`, then `var restored = session.Layers.Layers.Single()` (a new object, per `Restore`).
      - `SourcePath == "take1.mp4"`, `Kind == UploadedVideo`, `TrimStartMs == 300`, `CalibratedOffsetMs == 0`.
      - `Assert.False(plugin.Disposed)`.
      - `Assert.Same(plugin, OpenEqEditorLiveInstance(restored))`.
      - `plugin.State` equals `{4,2}`.
    - **Redo:** `session.Redo()` → `SourcePath == "take2.mkv"`, `CalibratedOffsetMs == 40`, `TrimStartMs == 0`.

### `tests/Acapella.App.Tests/LayerRowRetakeTests.cs` (new)

13. **`[StaFact] NotifySourceReplaced_RaisesTheSourceAndTrimProperties_ButNotLayer`**
    - Setup: `var row = new LayerRowViewModel(1) { Layer = new LayerCollection().Add(LayerKind.UploadedVideo, "a.mp4") }`, then subscribe `PropertyChanged` into a `List<string>`.
    - Act: `row.Layer!.ReplaceSource(LayerKind.UploadedAudioOnly, "b.wav", 0)`, then `row.NotifySourceReplaced()`.
    - Assert the list contains `TrimStartMs`, `TrimEndText`, `IconGlyph` and `SourceStateLabel`, and does **not** contain `nameof(LayerRowViewModel.Layer)` or `HasSource`. Also `row.SourceStateLabel == "uploaded (audio)"`.
    - Needs no `MainWindow`, so no `Closing` / `MessageBox` hazard.
    - Safe standalone: the `Layer` setter and `RefreshMixDisplayProperties` (`:413-438`) only raise events, and nothing reads `SharedHostedService` unless a getter is invoked.

**No `[StaFact]` drives `RetakeRecord` or `RetakeReplaceWithFile` end to end, deliberately.** Both block on a modal dialog (`RecordSetupWindow.ShowDialog`, `OpenFileDialog.ShowDialog`) that would hang the STA test thread. After a successful retake the session is dirty, so `MainWindow`'s `Closing` handler shows a `MessageBox` in the test's `finally` (the multi-file spec explains this). A narrow headless test *is* possible and cheap, mirroring `ExportPlaybackGateTests`. Add it:

14. **`[StaFact] ExportPlaybackGateTests.RecordSetup_WhileExportInFlight_RefusesWithStatus`** (append to `tests/Acapella.App.Tests/ExportPlaybackGateTests.cs`).
    - Setup: `new MainWindow()`, `Show()`, `ExportMenuItem.IsEnabled = false`.
    - Act: `window.RecordButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))`. The toolbar ⏺ is already named `RecordButton` (`MainWindow.xaml:206`). Its handler goes `RecordButton_Click` (`MainWindow.xaml.cs:842`) → `RecordingSetupMenuItem_Click`. On an empty project `LowestFreeCell()` is 0, so the call reaches `OpenRecordSetup` → `ShowRecordSetupDialog`.
    - Assert `StatusText.Text == "Finish the export first."`.
    - This pins that the #8 gate moved into `ShowRecordSetupDialog` intact. Re-record shares that helper, and the [review] grep below pins that it is the only construction site. The gate returns before any dialog is constructed, so nothing blocks. Nothing is dirtied, so `Close()` doesn't prompt. No XAML change is needed for the test.

**Regression carve-out (per CLAUDE.md):** this touches `RecordSetupWindow.RecordButton_Click` / `StopRecording`, `OpenRecordSetup`, `AttachUploadedFile` and the strip template in `MainWindow.xaml`. Re-run the whole `Acapella.App.Tests` project once: `ExportPlaybackGateTests` (the #8 gate now lives in the helper), `MixingScreenTests`, and `TransportLayoutTests` (it tests an empty project, so the name-row change can't move its bounds, but it constructs the template). Re-run `Acapella.Engine.Tests` `Project/*` (`LayerCollectionTests`, `ProjectSessionTests`) and `Mix/*` once.

---

## Acceptance criteria

- **[auto]**
  - The 11 `LayerRetakeTests`, the new `ProjectSessionTests` test, `LayerRowRetakeTests` and the new `ExportPlaybackGateTests` test pass.
  - `Acapella.App.Tests`, `Project/*` and `Mix/*` still pass.
  - The solution builds with no new warnings.
- **[review]** (checked by reading the diff; each is a one-line grep or inspection)
  - `grep -n "new RecordSetupWindow("` over `src/` finds **exactly one** hit, inside `ShowRecordSetupDialog` (D4).
  - `ShowRecordSetupDialog` contains no `await` between the `ExportMenuItem.IsEnabled` check and `ShowDialog()`. `RetakeRecord`, `RetakeReplaceWithFile` and `RetakeButton_Click` contain no `await` at all (subtlety (c)), and none of them is `async`.
  - `RecordButton_Click` uses **one** `guideLayers` variable for both the `if` condition and `mixInputs`. `nextLayerId` and the `_layers.Layers.Select(... AudioInput ...)` over all layers are gone (subtlety (a)).
  - `StopRecording`'s `_layers.Add` appears only inside `if (_retakeLayerId is null)`. The retake branch mutates no `LayerModel` (D3).
  - Neither retake handler assigns `row.Layer`. Both call `NotifySourceReplaced()` (D5).
  - Neither retake handler calls `LowestFreeCell`, `RowForCell`, `_layers.Add`, `UpdateAddLayerButtonState` or assigns `CellIndex` (subtlety (d), D7).
  - In both retake handlers, `ReplaceSource` … `PushUndoSnapshot()` is a straight-line block with no dialog, `MessageBox` or `await` (subtlety (e)).
- **[human]** (modal dialogs and real devices block automation). Use a project with a whole-number BPM (e.g. 120) for every "nothing changes" check. With a fractional BPM, #8 runner-up 2 (the `F0` BPM round-trip on cancel) marks the project dirty on its own and would make these checks fail for an unrelated reason.
  - **Replace with file:**
    - On a 3-layer project, set layer 2's gain to −6 dB, pan it left, open Pro-Q 4 on it and cut a band, and set In = 500 on it. Then Replace layer 2 with another video. It lands in the same cell with the same name and color. Gain, pan and the EQ cut still apply, audibly. In shows `0` and Out is blank. `•` appears. Status reads `Replaced Layer 2 with <file>.`
    - One Ctrl+Z brings back the old video, In = 500, and the same strip still selected. One Ctrl+Y re-applies the retake.
    - Replace a video layer with a `.wav`: the grid cell shows the audio-only placeholder, and the audio plays.
    - Cancel the picker: no `•`, no undo entry, status unchanged.
  - **Re-record (headphones on):**
    - On a 3-layer project, Re-record layer 2. The dialog title reads `Re-record Layer 2`. The guide carries layers 1 and 3 and **not** the old layer-2 take. After Stop, the take lands in strip 2 / cell 2 with its FX intact. Ctrl+Z restores the old take.
    - **Full project (4 layers):** ⏺ still refuses with `Layer cap reached (4).` Re-record on any strip records normally and leaves 4 layers.
    - **Only layer:** Re-record it. No guide plays, and the dialog status has no "with guide track". Save, and the project JSON shows `"CalibratedOffsetMs": 0` for that layer (objective check of subtlety (a)).
    - **Soloed target:** solo layer 2 of 3, then Re-record layer 2. Layers 1 and 3 are audible in the guide (subtlety (b)).
    - **Cancel / close the dialog** without recording: nothing changes (no `•`, same source).
    - **M6 failure** (e.g. unplug the camera mid-take): the dialog stays open with `Recording failed: ...`. Cancelling then changes nothing.
  - **Export interplay:**
    - Start an export, then click ↻ → Re-record... on any strip. Status reads `Finish the export first.` and no dialog opens.
    - During the same export, ↻ → Replace with file... is allowed. The finished MP4 contains the **old** take (the snapshot was taken before the click; see D7 for the StopAsync window).
  - **Open editor stays live:** open Pro-Q 4 on layer 1, Replace layer 1's source, then tweak a band in the still-open window. The preview follows, and Ctrl+Z undoes the tweak as its own step. This is the D5 counter-example, observed working.
  - **Melodyne (if available):** set layer 1's pitch mode to Manual (Melodyne), Replace its source, press Play, then open the Melodyne editor. It shows the **new** audio (forward re-registration, (f)).
  - **Layout:** the ↻ button sits at the right of each populated strip's name row. It is clickable, doesn't push the strip taller, and a long layer name ellipsizes instead of hiding the button. Empty strips show no ↻. At the window's minimum size (MinHeight 600) nothing is clipped that wasn't before.

---

## Known limitations (accepted, not part of this ticket)

- **Melodyne after undoing a retake (subtlety (f)).** The ARA session can keep the undone take's audio while playback uses the cached render of the restored take. The first Melodyne edit then re-registers and loses that edit. The same mechanism already exists with trim → undo. There are two fix shapes for a separate ticket:
  - fold the ARA session's currently-registered content key into the Manual2A cache key (`MixEngine.cs:200-204`), so a cache hit is impossible when the session holds different audio;
  - or have `ReplaceSource` / undo evict that layer's `PitchCorrectionCache` entries.
  Melodyne edits are also not persisted at all yet (`AraArchiveKey` is unused, `ImportState` never called), so an edit made on the old take does not come back with Undo in any case.
- **Preview keeps playing into a recording (#8 runner-up 1, inherited).** Re-record, like every Recording-setup entry, does not stop a playing preview. If a stop is added, re-check the #8 gate after its await (subtlety (c)).
- **BPM write-back on cancel (#8 runner-up 2, inherited).** Cancelling Re-record on a fractional-BPM project still marks it dirty. The [human] checks use a whole-number BPM for this reason.
- **Transient torn read by the preview thread (subtlety (g)).** A chain build already running during `ReplaceSource` can see the new path with the old trims. It is superseded by the queued refresh, the same exposure trim edits have today.
- **Old takes accumulate in `media/`.** A retake never deletes the replaced file. That is required for undo and by Pause Rule 1. Tools > Recordings folder size already reports the growth. Cleanup is a separate, user-confirmed feature.
- **No media validation on Replace with file, same as Upload.** A non-media file picked through "All files" becomes an `UploadedVideo` and fails later as `Preview error: ...`. The `MediaProbe.HasNonzeroDuration` pre-check would change Upload too, so it belongs in a separate ticket.
- **Status numbering in the dialog.** "Recording layer N" / "Re-recording layer N" uses the zero-based `LayerId`, as new takes already do. Leave a `TODO(polish)`.
- **Clear / remove layer: out of scope.** `LayerCollection.RemoveLast` remains uncalled. Removal would need `LayerId` reuse rules and `HostedPluginService.Release` wiring (`:118, :124-126`), and the proposal lists it separately.
