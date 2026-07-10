# Acapella Rebuild — Build Plan (v3, Melodyne via VST3/ARA Hosting)

> Instruction-mode plan for implementation by Fable 5. Each phase is a self-contained unit of work with explicit inputs, outputs, and acceptance criteria. Build phases in order — later phases assume earlier ones are working and tested. This version replaces the pitch-correction section of v2 with a VST3/ARA plugin-hosting approach so Melodyne can be used, while keeping a native fallback so the rest of the app isn't blocked on it.

## Confirmed Scope

| Decision | Value |
|---|---|
| Collaboration | None. Solo, local-only workflow. |
| Layer 1 source | Either recorded live, or uploaded from an existing audio/video file. |
| Metronome | Present, user-toggleable on/off per recording session. |
| Max layers | 4 for Phase 0/1. Design the data model so this cap is a config value, not hardcoded. |
| FX | EQ, pan, noise gate, metronome, pitch correction. |
| Pitch correction engine | **Melodyne, hosted via VST3 + ARA**, with a native pitch-correction fallback (Rubber Band + pitch tracking) when Melodyne isn't installed/licensed on the user's machine. |
| Grid layout | Fixed 2x2 (4-cell) grid only. |
| Output format | MP4 (H.264 video + AAC audio), single quality tier. |
| Platform | Native Windows, C#/.NET (WPF or WinUI 3) + a native C++ plugin-hosting module. |

## Legal / Licensing Constraints (read before starting Phase 2A)

- Your app must **never bundle or redistribute** Melodyne. It only hosts a copy the user has separately purchased and already installed as a VST3 plugin, with their own Celemony license (iLok or Celemony Account) already authorized.
- Different Melodyne tiers support different feature depth (Melodyne Essential vs. Assistant vs. Editor vs. Studio) — polyphonic detection and full note-editing require the higher tiers. Your app should not assume a specific tier is present; detect capabilities at runtime.
- Your hosting layer only needs to implement the **ARA host side** of the interface. Celemony does not need to approve or provide anything special for this — ARA is an open, publicly documented host/plugin protocol (originally developed by Celemony and PreSonus), and JUCE's implementation is available under JUCE's standard license terms.

---

## Architecture Summary

```
Capture Engine → Sync Engine (latency-calibrated) → Project Model (4 layers max)
                                                            ↓
                                          Mix Engine (EQ / pan / noise gate / metronome)
                                                            ↓
                                  Plugin Host Bridge (native C++/JUCE: VST3 + ARA hosting)
                                       — hosts Melodyne if installed & licensed —
                                       — falls back to native pitch correction if not —
                                                            ↓
                                            Pitch-corrected audio → merged into mix
                                                            ↓
                                                  Compositor (2x2 grid)
                                                            ↓
                                                Export Pipeline (MP4)
```

Core libraries:
- **Audio I/O:** NAudio (WASAPI) or CSCore
- **Video capture/encode/mux:** FFmpeg (via CLI wrapper or FFmpeg.AutoGen bindings)
- **Compositing:** SkiaSharp (2D canvas, draws 4 decoded video frames into a 2x2 grid per output frame)
- **Plugin hosting (native module):** JUCE (`juce_audio_processors` for VST3 hosting, JUCE's ARA hosting classes for ARA — modeled on JUCE's `AudioPluginHost` example project)
- **Native pitch correction fallback:** Rubber Band Library (shifting, LGPL) + a pitch-tracking library (e.g., pYIN or a small CREPE/ONNX model) for detection
- **Project persistence:** JSON or SQLite project file referencing raw media paths, per-layer parameters, and (if used) Melodyne ARA document state

---

## Phase 0 — Baseline Audit (Do This First)

**Goal:** Understand what the existing app's sync + FX code actually does before extending it.

**Tasks:**
1. Document the current sync method: timestamp-based, manual offset, waveform cross-correlation, or other.
2. Document current FX: exact parameters and where in the pipeline they're applied (capture time vs. mixdown).
3. Document the current project/file model: how audio and video files are referenced and stored today.

**Output:** A short findings note stating what's reusable vs. what needs replacing.

**Acceptance:** Findings note exists and answers all three questions.

---

## Phase 1 — Layer 1 Input + Latency Calibration + Guide-Track Recording

**Goal:** Get the first layer in (recorded or uploaded), and get layers 2–4 recording in sync against a guide track.

**Tasks:**

1. **Layer 1 ingestion (two paths):**
   - Path A — Record: capture webcam + mic directly, no guide track needed.
   - Path B — Upload: accept an existing audio-only or video file as layer 1. If audio-only, that grid cell renders a static placeholder in Phase 4.
2. **Latency calibration:** one-time loopback test — play a short click/tone through the selected output device, capture it back via the selected input device, measure round-trip latency via cross-correlation. Store per (input device, output device) pair.
3. **Metronome:** toggleable (on/off), adjustable BPM, audible only during recording, never baked into the recorded track.
4. **Guide-track recording (layers 2–4):** play back the mixed composite of prior layers as a guide track while capturing new mic+camera input; apply the calibrated latency offset to align.
5. **Manual fine-tune:** a millisecond offset slider to nudge sync after automatic alignment.

**Acceptance:**
- Layer 1 works from both fresh recording and uploaded file.
- Layers 2–4 record with guide track audible, sample-aligned within a defined tolerance (e.g., ±20ms), tested against a clap/click track.
- Metronome toggles cleanly, doesn't leak into recorded audio.
- Latency offset persists across sessions per device pair.

---

## Phase 2A — Plugin Host Bridge: VST3 + ARA Hosting (Melodyne)

**Goal:** Host Melodyne as a VST3/ARA plugin from within the app for pitch correction, when it's installed and licensed on the user's machine.

**This is the highest-risk phase in the project. Timebox an initial spike (recommend 1–2 weeks) before committing to full integration — validate the core ARA analyze → render loop works before building UI around it.**

**Tasks:**

1. **Native host module setup (C++/JUCE):**
   - Create a JUCE-based native module (compiled as a Windows DLL) modeled on JUCE's `AudioPluginHost` example, including both `juce_audio_processors` (VST3 hosting) and JUCE's ARA hosting support.
   - This module is separate from your main C#/.NET app; it will be called into via a C-ABI (`extern "C"`) interface.

2. **Plugin discovery:**
   - Scan standard VST3 install locations (`C:\Program Files\Common Files\VST3`) for installed plugins.
   - Identify Melodyne specifically by its plugin identifier, and check whether it exposes the ARA factory extension (Melodyne does, for compatible tiers).
   - If not found or ARA unavailable: report this back to the C# layer so it can fall back to Phase 2B's native pitch correction for that session/layer.

3. **ARA session setup:**
   - Create an ARA Document Controller instance via the host bridge.
   - For each layer that needs pitch correction, create an ARA audio source from that layer's recorded audio buffer and register it with Melodyne for analysis.
   - Trigger analysis (this is what causes Melodyne to run its pitch/note detection algorithm).

4. **Retrieving corrected audio:**
   - Use Melodyne's ARA playback renderer to pull back the processed (pitch-corrected) audio for that layer.
   - Feed this into the Mix Engine (Phase 3) in place of the raw layer audio, at mixdown and in live preview.

5. **Optional: native editor UI embedding:**
   - If you want the user to manually correct notes (not just automatic correction), embed Melodyne's own plugin editor window using JUCE's native window embedding, hosted inside your app's UI (e.g., as a child window or a docked panel).
   - This is optional for v1 — automatic-only correction (no manual note editing UI) is a smaller, still-useful first cut.

6. **C-ABI surface exposed to C#/.NET:**
   Design and implement at minimum:
   - `ScanForMelodyne() -> bool` (with capability info: tier, ARA support)
   - `CreateAudioSource(layerId, samples, sampleRate) -> sourceHandle`
   - `AnalyzeSource(sourceHandle) -> bool`
   - `RenderCorrectedAudio(sourceHandle) -> float[] correctedSamples`
   - `ShowEditorWindow(sourceHandle)` (optional, for manual editing)
   - `ReleaseSource(sourceHandle)`

7. **P/Invoke bridge in C#/.NET:** wrap the above C-ABI calls so the Mix Engine can call into the native module transparently.

**Acceptance:**
- App correctly detects whether a licensed, ARA-capable Melodyne install is present.
- A test layer's audio can be sent through Melodyne's ARA analysis and the corrected audio retrieved and played back, audibly correcting pitch on a deliberately off-pitch test recording.
- If Melodyne isn't present, the app cleanly falls back to Phase 2B without crashing or blocking the rest of the pipeline.

---

## Phase 2B — Native Pitch Correction Fallback (Build This Regardless, and First)

**Goal:** A working pitch-correction path that doesn't depend on Melodyne being installed — both as a safety net and as the thing that lets you build/test the rest of the app without owning a Melodyne license yourself.

**Recommendation: build this phase before Phase 2A**, so you have a fully working end-to-end app early, and treat Melodyne hosting as an additive enhancement layered on top later.

**Tasks:**
1. Integrate Rubber Band Library for pitch shifting.
2. Integrate a pitch-detection/tracking method (e.g., pYIN, or a small ONNX-exported CREPE model run via ONNX Runtime) to determine current pitch per frame.
3. Implement basic automatic correction: detect pitch, compute correction amount toward nearest note in a target scale/key, apply via Rubber Band.
4. Expose this as an interchangeable pitch-correction backend so the Mix Engine can use either this or Melodyne (Phase 2A) per layer, selected automatically based on what's available.

**Acceptance:**
- Pitch correction audibly works on a deliberately off-pitch test recording without Melodyne installed.
- Backend is swappable — switching between native and Melodyne (when both available) doesn't require changes elsewhere in the Mix Engine.

---

## Phase 3 — Multi-Track Mixing Engine (EQ, Pan, Noise Gate, Metronome Routing)

**Goal:** Non-destructive per-layer audio processing, adjustable after all 4 layers are recorded. Pitch correction (from 2A/2B) plugs into this as one stage.

**Tasks:**
1. Per-layer parameters, stored in the project model, not baked into raw audio:
   - Volume (gain) + mute/solo
   - Pan (stereo position, -1 to +1)
   - EQ (3-band shelf/bell per layer is enough for v1)
   - Noise gate (threshold + release)
   - Pitch correction backend selection + its parameters (target key/scale, correction amount)
2. Real-time mixed preview: play all layers together with current settings applied.
3. Mixer UI: one channel strip per layer (up to 4).

**Acceptance:**
- Changing any parameter updates the live preview mix without re-recording.
- Reopening a saved project restores all per-layer parameters exactly, including which pitch backend was used.

---

## Phase 4 — 2x2 Grid Compositing

**Goal:** Render up to 4 video layers into a fixed 2x2 grid for preview and export.

**Tasks:**
1. Fixed 2x2 layout: top-left, top-right, bottom-left, bottom-right, ordered by recording order (make this configurable later if needed).
2. Audio-only layers (from Phase 1's upload path) render a placeholder visual (static image, waveform, or solid color) in their grid cell.
3. Composite decoded frames from each layer into their assigned cell per output frame, for both live preview and final export.

**Acceptance:**
- Preview shows all 4 layers simultaneously in the 2x2 grid, synced correctly.
- Audio-only layer doesn't break compositing.

---

## Phase 5 — Project Persistence

**Goal:** Save/reopen a project and retain everything, including Melodyne state if used.

**Tasks:**
1. Project file (JSON or SQLite) storing: raw media paths per layer, per-layer mix parameters, latency offset used, metronome BPM, grid assignment, and which pitch backend was used per layer.
2. If Melodyne (ARA) was used on a layer: persist its ARA document/audio-source state (Melodyne's host API provides serialization hooks for this — the archiving/unarchiving of ARA document state) so reopening a project doesn't require re-running analysis from scratch.
3. Load flow: reopening a project restores full editable state — nothing destructively baked in until export.

**Acceptance:** Close and reopen a project; every parameter, every layer's sync alignment, and (if used) Melodyne's correction state is exactly as left.

---

## Phase 6 — MP4 Export Pipeline

**Goal:** Produce a final MP4 file.

**Tasks:**
1. Mix down all 4 audio layers (with EQ/pan/noise gate/pitch correction applied, from whichever backend each layer used) to a single master audio stream.
2. Render the composited 2x2 video (Phase 4) to a video stream.
3. Encode: H.264 video + AAC audio, mux into a single `.mp4` container via FFmpeg.
4. Single quality tier for now (e.g., 1080p / 192kbps AAC); expand to presets later if needed.

**Acceptance:** Export produces a valid, playable `.mp4` file with correctly synced, mixed, pitch-corrected, composited audio-video for all 4 layers.

---

## Explicitly Out of Scope for This Build

- Collaboration / link-sharing / multi-user / cloud sync
- Layouts other than 2x2
- More than 4 layers
- Output formats other than MP4
- Bundling or redistributing Melodyne itself (host-only; user provides their own licensed install)

---

## Suggested Order of Work for Fable 5

1. **Phase 0** (audit) — must happen first.
2. **Phase 1** (recording + sync) — highest-risk *core* mechanic; get this solid before UI polish.
3. **Phase 2B** (native pitch correction fallback) — build this before 2A so the full app works end-to-end without depending on Melodyne being available.
4. **Phase 3** (mixing/FX, using 2B's pitch backend) — full mixing pipeline working.
5. **Phase 4** (compositing) — can run in parallel with Phase 3; only needs decoded video frames.
6. **Phase 5** (persistence) — wraps Phases 1–4's data into a saved/reloadable project.
7. **Phase 6** (MP4 export) — final integration of mix + composite into a single file. **At this point you have a fully working app without Melodyne.**
8. **Phase 2A** (Melodyne VST3/ARA hosting) — layered on afterward as an enhancement track. Timebox the initial spike; if it stalls, the app already works fully on the Phase 2B fallback, so this can be deprioritized without blocking a release.
