# Acapella UI Design Spec (v1)

> Written for Fable 5 to implement in WPF/XAML. This describes layout, behavior, and control placement only — colors/spacing are directional, not pixel-exact. Implement using existing locked stack (WPF, SkiaSharp.Views.WPF for preview compositing). This is a visual/interaction spec, not a scope change to CLAUDE.md's locked product decisions (still 4-layer cap, 2x2 grid, same FX list).

## Overall layout

Single window, dark theme, three regions:

1. **Top bar** — app name left, Open / Save / Export buttons right.
2. **Preview panel** — the 2x2 composite grid, live. No manual "refresh" control: any parameter change re-renders the preview immediately (or at the app's normal preview frame rate — no stale-until-refresh state).
3. **Track panel** — a vertical list of track rows, one per layer, docked beside the preview. See "Dock position" below.

## Track panel (replaces DAW-style mixer)

Each layer is one row in a track list, styled like a video editor's track list, not a mixer channel strip.

- **Collapsed row:** icon (mic if recorded, upload if uploaded, plus-icon if empty/unset), layer name, small source-state label ("recorded" / "uploaded" / none), chevron.
- **Row click behavior:** clicking a row's header expands/collapses it in place (accordion). Expanding one row does not require collapsing others — multiple rows may be open at once.
- **Expanded row, once a source exists:** shows only the tweak controls for that layer — mute, solo, pitch-correction backend dropdown (Native automatic / Melodyne manual), gain, pan, EQ, noise gate. No camera/mic pickers, no record button, no metronome — those only ever appear during initial source setup (see below), never after a source is attached.
- **Expanded row, no source yet (new/empty layer):** shows a two-button choice — "Record" or "Upload". Selecting one does **not** expand further inline. Instead:
  - **Record → opens a separate pop-up/dialog window** ("Recording setup") containing: camera device dropdown, microphone device dropdown, "Calibrate latency" button, metronome toggle + BPM field, and the Record button. This dialog is modal to the row (or to the app) and is the *only* place metronome and device pickers ever appear. Closing/completing the dialog (recording finishes or is confirmed) attaches the result as the layer's source, closes the dialog, and the row collapses back to showing just the tweak controls.
  - **Upload → opens a standard file-picker dialog** (native Windows file dialog is fine — no custom UI needed). On file selection, the row attaches the source and returns to the tweak-controls state.
- **"+ Add layer" row** at the bottom of the list, up to the 4-layer cap. Clicking it inserts a new empty row in the not-yet-set-up state described above.

## Melodyne (manual pitch correction) — pop-up, not inline

When a layer's pitch-correction backend is set to "Melodyne (manual)" and the user opens it for editing, Melodyne's own hosted editor UI opens in its **own separate pop-up/dialog window**, same pattern as the recording setup dialog — not embedded inline in the track row or sidebar. This matches the existing plan requirement that Melodyne's editor must be shown (ARA has no headless "apply" call), and keeps the main track list free of a large embedded plugin window when not in use.

## Dock position (left/right toggle)

The track panel can be docked to either side of the preview panel. A small toggle button in the track panel's header ("dock left" / "dock right") swaps which side it's on. This is a layout preference only — no functional difference between positions. Persist the user's last choice in app settings (minimal, same mechanism as other settings persistence).

## Explicitly not in this design

- No default "4 empty record slots" — layers are added one at a time via "+ Add layer", since a future version may raise the cap above 4.
- No metronome or device selection anywhere outside the Record setup dialog — upload-sourced layers never show them.
- No manual preview refresh step.
- No inline-embedded Melodyne editor or inline-embedded recording controls — both are separate pop-up windows.

## Reference mockups

Three HTML mockups were built and approved incrementally in the design session (not saved as files, described here for context):
1. Initial DAW-mixer-style layout — superseded per feedback (too much like an audio mixer).
2. Video-editor layout with sidebar inspector and inline record/upload setup — superseded per feedback (recording/Melodyne setup should pop up, not sit inline; sidebar should be dockable).
3. Final direction: accordion track list (doubling as the inspector), dockable left/right, with record setup and Melodyne editor as separate pop-ups. This spec describes that final direction.
