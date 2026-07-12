# Acapella UI Design Spec (v2)

> Written for Fable 5 to implement in WPF/XAML. Describes layout, behavior, and control placement only — colors/spacing are directional, not pixel-exact. Implement using the existing locked stack (WPF, SkiaSharp.Views.WPF for preview compositing). This is a visual/interaction spec, not a scope change to CLAUDE.md's locked product decisions (still 4-layer cap, 2x2 grid, same FX list). Supersedes v1 — see "Changes from v1" at the bottom.

## Overall structure: two screens, not one

The app has two distinct screens. The main screen is a lightweight editor; a separate **Mixing** screen (opened per-layer) holds every FX control. Only one is visible at a time — opening Mixing replaces the main screen's content (same window, not a new OS window), with a "back to editor" control to return.

---

## Screen 1 — Editor (main screen)

Single window, dark theme, top bar (app name left, Open / Save / Export right) plus two regions below:

### Preview panel
- Renders the actual composited 2x2 video output — real video playback, not a static placeholder image.
- Updates in real time: any change made anywhere in the app (trim points, FX in the Mixing screen, etc.) is reflected in the preview immediately, without a manual refresh step.
- Below the preview, a transport bar:
  - **Restart** button (jump to start).
  - **Play/Stop** toggle (single button, swaps icon/state — not two separate buttons).
  - **Timeline** — a horizontal scrubber showing playback progress across the full project duration, draggable to seek, with elapsed/total time readout (e.g. `0:07 / 2:15`).
  - **Zoom in / zoom out** — these zoom the *timeline's* horizontal scale (more/less time visible per pixel, like a video editor's timeline zoom), not the preview video's picture size. Zoom does not affect what's rendered in the preview panel, only how much of the timeline is visible/how precisely it can be scrubbed.

### Track sidebar
Minimal — this screen intentionally shows only enough per layer to get a source in and trimmed. Per layer row:
- Icon + name + source-state label (recorded / uploaded / empty), same as before.
- If no source yet: **Record** / **Upload** buttons.
  - Record opens the existing Recording setup pop-up (camera/mic pickers, calibrate latency, metronome + BPM, record button) — unchanged from v1.
  - Upload opens a standard file picker — unchanged from v1.
- If a source exists: **Trim** control only — start time and end time fields (or a mini in/out range on the row), replacing what v1 called a separate "crop" and "time sync" control. Trimming defines the in/out points used for that layer's playback; a single trim control is sufficient and there is no separate sync adjustment on this screen.
- **Open mixing** button — the only way to reach FX. Clicking it switches the window to Screen 2 for that layer.
- **+ Add layer** row at the bottom, up to the 4-layer cap, same behavior as v1.

No dock-left/dock-right toggle. The sidebar has one fixed position (right of the preview) — this control was in v1 but is removed.

---

## Screen 2 — Mixing (per layer, opened from Editor)

Reached via a track's "open mixing" button; replaces the Editor screen's content until the user backs out. Header shows which layer is being edited and a "back to editor" control.

Below the header, a row of **subtabs**, one per FX stage, each with its own visualization (styled after a hosted-plugin look — a waveform/curve area above knob/slider controls, similar to how a VST plugin window is laid out):

1. **Limiter** — waveform/envelope view with a ceiling line; gain and ceiling controls.
2. **Compressor** — ratio/knee curve view; threshold and ratio controls.
3. **Noise gate** — gate envelope view; threshold and release controls.
4. **EQ** — frequency-response curve with adjustable band points; per-band gain controls.
5. **Melodyne** — not a custom visualization. This subtab is a launcher: a button that opens Melodyne's own hosted ARA editor window (per the existing plan — ARA has no headless "apply," so the real Melodyne UI must be shown). Nothing here duplicates Melodyne's own view.

Only one subtab's panel is visible at a time; switching subtabs does not lose the other tabs' settings.

Pan and mute/solo (simple per-layer controls that don't need a dedicated visualization) can live in a small strip at the top of this screen, outside the subtabs, since they apply regardless of which FX subtab is open.

---

## Explicitly not in this design

- No dock-left/dock-right toggle for the sidebar (removed from v1).
- No inline FX controls (gain/pan/EQ/gate sliders) on the Editor screen — all FX live in the Mixing screen.
- No "crop" as a picture-crop control — "trim" here always means start/end time, not framing.
- No separate time-sync control alongside trim — trim's start/end points serve that purpose.
- No manual preview refresh step, and no static-image preview — the Editor screen plays real video in real time as adjustments are made.
- No metronome or device pickers outside the Recording setup pop-up.
- No inline-embedded Melodyne editor — it opens in its own window from the Melodyne subtab.

## Changes from v1

- Removed the dock-left/dock-right toggle entirely.
- Split what was a single accordion track-list screen into two screens: a minimal Editor (source + trim only) and a separate Mixing screen (all FX, with subtabs).
- Replaced "crop" (screen/picture crop) with "trim" (start/end time) and removed the separate time-sync control, since trim now covers that need.
- "Zoom" is clarified as timeline zoom (time visible per pixel on the scrubber), not preview picture zoom.
- Preview is now specified explicitly as real video playback rendered in real time, not a static per-cell placeholder.
- Transport controls specified explicitly: restart, play/stop toggle, timeline scrubber with progress and elapsed/total time, zoom in/out.
- Mixing screen's subtabs (limiter, compressor, noise gate, EQ, Melodyne) are new — v1 had these as inline sliders in the track row.

## Reference mockups

Four HTML mockups were built and approved incrementally in the design session (not saved as files, described here for context):
1. Initial DAW-mixer-style layout — superseded (too much like an audio mixer).
2. Video-editor layout with inline sidebar inspector — superseded (recording/Melodyne setup should pop up, not sit inline).
3. Accordion track list doubling as inspector, dockable left/right, record/Melodyne as pop-ups — superseded by this version (dock toggle removed, FX moved out of the sidebar entirely into a dedicated Mixing screen, real transport controls added, crop redefined as time trim).
4. Current direction: two-screen model (Editor + Mixing), real video preview and transport, sidebar limited to source + trim, Mixing screen with FX subtabs. This spec describes that direction.
