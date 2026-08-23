# Acapella Rebuild — Build Plan (v5, Playback Fix + UI Consolidation + Quality Pass)

> Instruction-mode plan for implementation by Sonnet/Fable, continuing from the v4 plan (Phases 0–6 complete) and the UI_Design_Spec v2 implementation. This version covers: fixing the preview-playback desync and everything else in `reviews/Bug_Audit_2026-07-12_PreviewPlayback.md`, the second UI consolidation round (Daniel's 8 requirements, referencing the FL Studio Fruity Limiter / Parametric EQ 2 and Clipchamp transport screenshots), and the first explicit quality pass. Phase 2A (Melodyne VST3/ARA hosting) remains the final phase and is unchanged from v4 — do not start it until this plan's phases are done, since the Mixing screen it plugs into is being reworked here.
>
> **Every acceptance criterion is tagged `[auto]` or `[human]`**, same contract as v4: `[auto]` criteria decide phase completion; `[human]` criteria batch into phase-boundary check-ins, with the next phase started provisionally.
>
> CLAUDE.md still governs behavior. Scope notes for Pause Rule 2: this plan itself is the approval for the additions it contains (master volume, layer rename, undo/redo, transport redesign, menu bar, combined dynamics panel, EQ visualization). Anything beyond these still requires a pause.

## Confirmed Scope (delta from v4)

| Decision | Value |
|---|---|
| Volume model | Three distinct controls: **Master volume** (toolbar, next to transport — applies at the mix bus, preview and export identically), **Layer volume** (sidebar, per layer — replaces the "Gain" slider formerly in the Mixing top strip), **Gain** (inside the Limiter panel only — the limiter's makeup gain, as in Fruity Limiter). |
| Sidebar per-layer controls | Name (renamable), layer volume, pan, mute, solo, compact trim. FX stay on the Mixing screen. |
| Mixing screen | Combined plugin-style panels: **Dynamics** (LIMIT / COMP tabs sharing one visualization area, plus the noise-gate section on the same panel — modeled on Fruity Limiter), **EQ** (frequency-response curve over a live spectrum, band handles — modeled directionally on Parametric EQ 2), **Melodyne** (launcher, unchanged). Playback transport available on the Mixing screen. |
| Transport | Clipchamp-style: undo/redo, −5s, play/pause, +5s, replay/restart, elapsed/total time on the timeline, timeline always visible. Menu bar (File/Edit/View/Tools/Help) replaces the top-bar buttons; window title text "Acapella" removed from the client area. |
| Undo/redo | In scope, app-wide for project edits (layer add/remove/rename, trim, all mix parameters). Not in scope: undoing a recording capture or a file export. |
| Everything else | Unchanged: 4-layer cap, 2x2 grid, MP4 export, FX list, Melodyne-via-ARA as manual pitch path, WPF/NAudio/FFmpeg/SkiaSharp stack. |

---

## Phase P0 — Playback Correctness (fix the desync) + audit bug-down

**Goal:** Preview video and audio are actually in sync, stay in sync, and match export. Work directly from `reviews/Bug_Audit_2026-07-12_PreviewPlayback.md` — items A1–A5 and B1–B6 are this phase; B11–B17 fold in where touched.

**Tasks:**
1. Audio-as-master-clock (audit A2): `PositionTrackingSampleProvider` wraps the mix; `FrameLoop` reads position from it (stopwatch fallback only for the no-audio test sink). Prime all frame sources (decode first frame) before starting audio.
2. Frame drop/catch-up (A1): target frame index derived from clock; consume-to-catch-up, render latest only; never block the loop on the UI thread (`InvokeAsync` + latest-frame mailbox in MainWindow, fixes B14 lifetime too).
3. Negative-shift skip in preview video path (A3) and trim-out duration corrected for head-skip (A4), both with preview↔export parity tests.
4. Duration from max(audio, video) per layer via `MediaProbe.GetDurationSeconds` (A5); video-only layers play full length and export instead of throwing.
5. Engine thread-safety (B1): single lock/work-queue around SetLayers/Play/Stop/Seek; stress test.
6. Decode caching (B2) and pitch-correction result caching (B3), keyed by source path+mtime (+backend for pitch).
7. Guide-track capture-start alignment (B4): wait-for-first-frame via `-progress`, plus post-take cross-correlation fallback writing the measured offset into `CalibratedOffsetMs`.
8. Preview output-device selection (B5); stereo-linked limiter envelope (B6).

**Acceptance:**
- `[auto]` Timestamp-encoded fixture (lavfi testsrc) played for 5s: rendered frame timestamp within 2 frames of audio position at t≈1s and t≈4s (drift test). Same check immediately after a mid-playback Seek.
- `[auto]` Preview frame at position 0 for a layer with `CalibratedOffsetMs=200` is pixel-identical to export frame 0 (parity test).
- `[auto]` Layer with trim-out + negative shift: preview and export both end that layer's motion at the same frame; layer audio and video lengths agree within 1 frame.
- `[auto]` Video-only (no audio track) layer: duration honored, export succeeds.
- `[auto]` Concurrency stress test passes; decode-cache test shows a second SetLayers on unchanged sources performs zero ffmpeg audio decodes.
- `[human]` A real 4-layer project plays in sync to the eye/ear, scrubbing feels responsive, and a real export matches the preview. **This check-in also clears the 5 outstanding `[human]` items from Phases 1–6.**

---

## Phase P1 — Menu bar, transport, and master volume (requirements 5, 6, part of 3)

**Goal:** Top chrome and transport match the Clipchamp-style reference; undo/redo exists and works for project edits.

**Tasks:**
1. **Menu bar** (File / Edit / View / Tools / Help) replacing the top-bar text+buttons; remove the "Acapella" label from the client area (stays in the OS title bar). File → New / Open / Save / Save As / Export MP4. Edit → Undo / Redo. View → placeholder (timeline zoom shortcuts). Tools → Recording setup, Calibrate latency. Help → About.
2. **Undo/redo infrastructure:** command stack over project state — simplest robust approach: snapshot the project DTO (already serializable, used for export snapshots) on every discrete edit (slider commit — not per-tick, use drag-completed/focus-loss), capped depth (e.g. 100). Wire to Edit menu, Ctrl+Z/Ctrl+Y, and the transport's ↩/↪ buttons. Restoring a snapshot must refresh sidebar rows, mixing screen bindings, and the live preview.
3. **Transport redesign** (fixes the invisible progress bar, B15): one transport row directly under the preview — undo, redo | −5s, play/pause (single toggle), +5s, replay (restart) | elapsed/total readout adjacent to the timeline (`0:17 / 2:15` style). Timeline gets proportional layout (star-sized, never scrolled off-screen; preview area shrinks first via `MaxHeight`/viewbox scaling). Keep timeline zoom on a View-menu/kebab control rather than two large buttons.
4. **Master volume**: knob/slider in the toolbar next to the transport buttons; applies a bus gain in `MixEngine.BuildMix` *after* summing (before the headroom/limiting stage), used identically by preview and export; persisted in the project file. Replace export's `PeakNormalizer` behavior per audit B10: a master brick-wall limiter (reuse `LimiterSampleProvider`, stereo-linked) at the bus in both preview and export, so exports sound like the preview.
5. Remove the "Preview" heading text (requirement 7).
6. Keyboard: Space = play/pause, Left/Right = ±5s, Home = restart.

**Acceptance:**
- `[auto]` DTO-level undo/redo round-trip test: apply 10 mixed edits (trim, rename placeholder, gain, mute), undo 10× restores the original DTO byte-identically, redo 10× restores the final state.
- `[auto]` Master volume affects exported audio RMS proportionally, and preview mix and export mixdown of the same project produce matching RMS within tolerance.
- `[auto]` Window resized to MinWidth/MinHeight: transport row and timeline remain within visible bounds (layout test via automation peers or measured element bounds).
- `[human]` Transport feels like the reference (button order, spacing, readout placement); undo/redo behaves as expected while scrubbing/playing.

---

## Phase P2 — Sidebar rework: rename, per-layer volume/pan/mute/solo, compact trim, grid identification (requirements 2, 3, 4, 8)

**Goal:** The sidebar is the per-layer home for identity + level; the user can always tell which grid cell is which layer.

**Tasks:**
1. **Rename:** editable layer name (double-click or pencil icon), stored on `LayerModel.Name` (new field, persisted in `LayerDto`; default "Layer N"). Mixing screen header and status texts use it.
2. **Sidebar row controls:** name row (icon, name, state label) + controls row: volume slider (this is the former Mixing-top-strip Gain → now "Volume", still maps to `GainDb`), pan, mute, solo. Trim compacted to a single line — `In [   ] Out [   ]` with ms (or m:ss.mmm) fields, replacing the verbose "Trim start (ms) / end (ms, blank = to end)" labels (requirement 8). Blank Out = to end, with a subtle "end" placeholder hint (audit B16).
3. **Grid identification (requirement 4):** each layer gets a stable accent color (index-based palette). The sidebar row shows a color chip next to the name; the preview composite draws a thin border + small name tag in that color on the layer's cell. Add a View-menu toggle ("Show layer labels", default on; auto-hidden during export — export output never includes overlays).
4. **Row/cell consistency (audit B8):** attaching a source to a row binds `CellIndex` to the row's position; renumbering/naming stays consistent after project load.
5. Empty-row handling: rows without sources are visually marked and excluded from save with a status hint (audit B9).

**Acceptance:**
- `[auto]` Rename persists through save/reopen; DTO round-trip test includes Name.
- `[auto]` Compositor overlay test: with labels on, each cell's border pixels match the layer's palette color and the correct layer renders in the row-matched cell even when rows were filled out of order; with labels off (and in export output), no overlay pixels present.
- `[auto]` Volume slider on the sidebar drives the same `GainDb` the mix uses (signal-level test), and the Mixing screen no longer exposes a duplicate "Gain" outside the limiter.
- `[human]` Grid↔sidebar correspondence is instantly obvious; trim row reads clean at 340px sidebar width.

---

## Phase P3 — Mixing screen rework: combined dynamics, EQ visualization, in-screen playback (requirement 1)

**Goal:** The Mixing screen looks and works like a hosted plugin, per the FL Studio references: one **Dynamics** panel (Fruity Limiter-style) with LIMIT/COMP tabs plus the noise-gate section, an **EQ** panel with a real response curve over a live spectrum (Parametric EQ 2-style, scaled to our 3 bands), a **Melodyne** launcher, and a transport so you can hear what you're tweaking.

**Tasks:**
1. **Panel restructure:** subtabs become two visual panels + launcher — Dynamics (LIMIT | COMP tab buttons bottom-left, like the reference; noise gate section on the right of the same panel with REL/GAIN/THRES) and EQ. Existing DSP classes are reused; this is layout + visualization, except:
   - Noise gate gains a makeup **Gain** control (reference parity) — small DSP addition to `NoiseGateSampleProvider`.
   - Limiter panel knobs: GAIN (makeup — the only "Gain" on this screen), SAT optional-skip, CEIL, ATT, REL, SUSTAIN — implement GAIN/CEIL/ATT/REL now (ATT/REL already exist as constants; expose them), skip SAT/SUSTAIN (mark visually absent, not disabled).
   - COMP tab: THRES, KNEE, RATIO (knee exists as a constructor arg; expose it).
2. **Live visualizations:**
   - Dynamics: scrolling loudness/envelope history (input level line + post-gain fill + gain-reduction trace) rendered via SkiaSharp from a tap sample-provider inserted around the dynamics stage — only while playback runs; static curve when stopped.
   - EQ: computed frequency-response curve from the three BiQuad transfer functions (exact, cheap) over a live FFT spectrum of the post-EQ signal (reuse the tap provider + a small FFT — NAudio has one). Band handles draggable on the curve (freq fixed for v1, gain draggable; keep sliders as the precise input).
3. **In-screen transport (requirement 1's "playback in mixer"):** reuse the Editor's transport control (play/pause, ±5s, position readout, thin timeline) docked at the bottom of the Mixing screen, driving the same `PreviewPlaybackEngine` instance (P0's thread-safety makes this safe). Preview video is not shown here — audio + meters are the feedback.
4. **Solo-in-place default:** entering the Mixing screen keeps the full mix audible (matches "playback shows current status"); a "Solo this layer" toggle in the top strip flips `Solo` for quick isolation.
5. Top strip cleanup: mute/solo/pan/volume moved to the sidebar in P2 — the Mixing top strip now shows only the layer name/color, the solo-this-layer toggle, and the transport state.

**Acceptance:**
- `[auto]` EQ response curve matches the BiQuad math at spot frequencies (compare curve samples vs `Transform`-measured gain at 150/1k/6k Hz for known settings).
- `[auto]` Exposed ATT/REL/KNEE parameters round-trip through the DTO and audibly change output (signal-level assertions: attack time constant reflected in envelope step response).
- `[auto]` Mixing-screen transport and Editor transport control the same engine without conflict (stress test from P0 extended: alternate transport commands from both screens).
- `[human]` Dynamics and EQ panels read like the FL references (proportions, tab behavior, knob layout); meters/spectrum move convincingly during playback; tweaking FX while playing is audibly live.

---

## Phase P4 — Quality pass (explicit, per CLAUDE.md's "quality happens when asked" — this is the ask)

**Goal:** Sand the rough edges that earlier phases deliberately deferred. No new features.

**Tasks:**
1. DSP correctness sweep: verify EQ band frequencies/Q against reference filter responses `[auto]`; gate release semantics (attack vs release naming, sample-rate-independent envelope decay — old audit L6); document the chain order in one place.
2. Performance: profile Play latency and per-edit refresh with 4 real 1080p layers; budget: Play starts < 1s warm-cache, param-edit refresh < 500ms. Fix the worst offenders (decode cache from P0 should carry most of this).
3. Error surfacing: every ffmpeg failure path shows the stderr tail in the status bar (capture already does; extend to decode/export/preview spawns).
4. UI consistency: dark-theme audit on new controls, focus states, tab order, tooltips on every knob/slider with units.
5. Media hygiene: recordings folder size surfaced in Tools menu; stale `layerN.mkv` from cancelled takes cleaned up on app close (with Pause Rule 1 respected — only files never attached to a layer).
6. Test debt: the parity/drift tests from P0 run in CI-style `dotnet test` without hardware; HardwareChecks gains a "previewsync" mode for the real-hardware version.

**Acceptance:**
- `[auto]` All existing tests plus new P0–P3 tests green in one `dotnet test` run; performance budget script reports within targets on the dev machine.
- `[human]` One full realistic session — record 2 layers, upload 2, trim, mix with dynamics/EQ, undo/redo a few times, export — feels solid end to end. This is the release-candidate check-in before Phase 2A.

---

## Phase 2A — Melodyne VST3/ARA hosting (unchanged from v4)

Deferred until P0–P4 are done. The v4 plan's Phase 2A section remains the source of truth (JUCE native module, C-ABI bridge, editor window launched from the Mixing screen's Melodyne panel — the launcher UI already exists). The timebox and Pause Rule 3 conditions from v4 still apply.

---

## Suggested order of work

1. **P0** — playback correctness first; everything else builds on a trustworthy preview. Also clears the audit and the five outstanding v4 `[human]` items in one check-in.
2. **P1** — chrome/transport/undo (undo infra before P2/P3 so their new edits are undoable from day one).
3. **P2** — sidebar + identity.
4. **P3** — mixing screen (depends on P0's engine safety for the shared transport, P2's control relocation).
5. **P4** — quality pass and release-candidate check-in.
6. **Phase 2A** — Melodyne, as its own session per the v4 plan's advice.

## Explicitly out of scope for v5

- SAT/SUSTAIN limiter stages, sidechain, multiband dynamics (visual placeholders only where the reference shows them).
- More than 3 EQ bands, draggable band frequencies (gain-drag only).
- Waveform thumbnails on the timeline, clip re-ordering, multi-select.
- Any Phase 2A work (Melodyne hosting itself).
- Layouts ≠ 2x2, >4 layers, non-MP4 export (locked, unchanged).
