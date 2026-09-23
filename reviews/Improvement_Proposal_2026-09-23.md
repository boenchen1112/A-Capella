# Improvement Proposal — 2026-09-23

> Product-improvement scan of the app as it stands at `0d6f443` (after tonight's `ReleaseAll()` refactor and Bug Audit #5's fix). **Enhancements only** — bugs are a separate track; nothing here overlaps `reviews/Bug_Audit_2026-09-23_ProjectSwitchPluginState.md` or its runners-up.
>
> Four candidates, each sized for one focused implementation session, ranked best-first. None touches a Pause Rule 2 decision: no new layouts, formats, vendors, dependencies, or layers.

## Note on the spec baseline

`UI_Design_Spec.md` is **v2 and superseded** by what actually shipped — the "v8 redesign" single merged screen (`src/Acapella.App/MainWindow.xaml:59-60`, `src/Acapella.App/MainWindow.xaml.cs:71-75`), where mixer-strip selection replaced the two-screen Editor/Mixing split. Claims below are read off the current code, not off v2. In particular, v2's timeline zoom was **deliberately dropped** in v8 (`MainWindow.xaml.cs:748-751`) and is not re-proposed here.

---

## Candidates

### 1. Real save affordances: Ctrl+S, Save vs Save As, and a dirty marker

**What:** Track the current project's file path and a modified flag; make `Ctrl+S` save straight back to that path, add a separate "Save As…", show the project name plus a `•` when modified, and prompt before File > New / File > Open / window close discard unsaved edits.

**Why:** There is no notion of a current file or a modified state anywhere in the app: `SaveProjectButton_Click` (`MainWindow.xaml.cs:779-798`) always opens a `SaveFileDialog`, the only key bindings are Undo/Redo (`MainWindow.xaml:23-26`), and the title is the literal string `"Acapella"` (`MainWindow.xaml:12`) whether the project is untouched or twenty edits deep. The plumbing is already there — `ProjectSession.CommitEdit()` (`ProjectSession.cs:53`) is the single funnel every discrete edit passes through, and v6/v7 both wrote tasks assuming a "mark project dirty" concept exists (`Acapella_Build_Plan_v6.md:51`, `Acapella_Build_Plan_v7.md:34`) that was never built.

**Effort:** Small–medium.

**Scope note:** One flag on `ProjectSession` (set in `CommitEdit`, cleared in `Save`/`Open`/`New`), one `_currentPath` field, two `KeyBinding`s, a title binding, and one shared confirm-discard helper. The close prompt goes in `Closing` (`MainWindow.xaml.cs:162-172`) ahead of the dispose calls — note that a separate close-path hang during playback is already logged as a bug-audit runner-up; this doesn't touch it.

---

### 2. Export progress in the status bar

**What:** Give `ExportEngine.Export` an optional `IProgress<double>` and drive a determinate progress bar (plus an `x / y frames` readout) next to the status text while an export runs.

**Why:** Export is the app's payoff step and it is completely silent — `ExportButton_Click` writes `"Exporting..."` once and then nothing until the whole thing finishes (`MainWindow.xaml.cs:844-865`), while behind that one line a frame-by-frame loop decodes every layer and pipes raw RGBA into ffmpeg (`ExportEngine.cs:85-90`). For a few minutes of 4-layer video that is a long stretch with no way to tell "working" from "hung", and the loop already computes `totalFrames` (`ExportEngine.cs:75`).

**Effort:** Small.

**Scope note:** The audio mixdown (`ExportEngine.cs:52-58`) runs *before* the frame loop, so the bar sits at 0% for that stretch — report a coarse "mixing audio…" phase first, or accept it. An optional `IProgress<double>?` parameter keeps every existing `ExportEngine` test source-compatible.

---

### 3. Set trim in/out from the playhead

**What:** Add "Set in" / "Set out" buttons (and a readable time display) for the selected layer, writing the current preview position into that layer's trim points.

**Why:** Trimming today means typing raw milliseconds into two tiny text boxes (`MainWindow.xaml:108`, `MainWindow.xaml:113`) while the only time readout in the app is `h:mm:ss` (`FormatTime`, `MainWindow.xaml.cs:763-767`) — the user watches the preview, sees `0:01:07`, and converts to `67000` by hand. The playhead is already in `_previewEngine.PositionMs` and the trim setters (`LayerRowViewModel.cs:149-167`) already fire `LiveParamChanged`, so the preview reflects the new in/out point instantly.

**Effort:** Small.

**Scope note:** The mixer strip is `Width="76"` (`MainWindow.xaml:61`) and already carries M/S, meter, pan, fader, gain and both trim boxes — the buttons belong in the FX panel's header for the selected layer, not on the strip. In/out points only; a separate time-sync control is explicitly out of the design (`UI_Design_Spec.md:61`).

---

### 4. Replace a layer's source without starting over

**What:** On a populated mixer strip, offer "Replace…" — pick a new file, swap `SourcePath`/`Kind` on the existing `LayerModel`, reset trim, keep `LayerId` and `CellIndex`.

**Why:** Once a slot has a source it is permanent — the Rec/Upload buttons are bound to `HasSource == false` and vanish (`MainWindow.xaml:83-88`), and `LayerCollection` exposes only `Add`, `Restore` and `RemoveLast` (`Project/LayerModel.cs:69-100`). For an app whose core loop is "sing it four times", a bad take with no retake path is the roughest edge in the workflow.

**Effort:** Small scoped to upload-replace; medium if re-record-in-place is included.

**Scope note:** Keeping `LayerId` stable is what makes the upload path cheap — the hosted-plugin cache is keyed on `(LayerId, Stage)`, so the layer's FX chain survives the swap untouched. Re-record-in-place is the medium half: `RecordSetupWindow` derives both the next id and the output filename from `_layers.Layers.Count` (`RecordSetupWindow.xaml.cs:138-140`) and `StopRecording` calls `_layers.Add(...)` unconditionally (`:304`), so as written it would append a fifth layer and clobber `media/layerN.mkv` instead of replacing.

---

## Recommendation

**Candidate 1 (save affordances).** It turns the most-repeated action in the app — save, confirm, save again — from a file-dialog round trip into a keystroke with a visible modified marker, and the discard guard comes along for free with the same flag.
