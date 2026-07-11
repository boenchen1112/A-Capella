# Acapella Rebuild — Build Plan (v4, Fable 5 Review Incorporated)

> Instruction-mode plan for implementation by Fable 5. Each phase is a self-contained unit of work with explicit inputs, outputs, and acceptance criteria. Build phases in order — later phases assume earlier ones are working and tested.
>
> **Every acceptance criterion below is tagged `[auto]` or `[human]`.** `[auto]` criteria are what the agent checks itself to decide a phase is done (per CLAUDE.md's Definition of Done). `[human]` criteria are batched into the phase-boundary check-in — the next phase starts provisionally while they wait. Where a criterion looks subjective but can be made objective (e.g. via a virtual loopback device or a cross-correlation script), it's tagged `[auto]` with a note on how.
>
> **This is a from-scratch project.** There is no pre-existing app to build on — Phase 0 is scaffolding, not an audit.

## Confirmed Scope

| Decision | Value |
|---|---|
| Collaboration | None. Solo, local-only workflow. |
| Layer 1 source | Either recorded live, or uploaded from an existing audio/video file. |
| Metronome | Present, user-toggleable on/off per recording session. |
| Max layers | 4 for v1. Config value, not hardcoded — see Data Model Rule below. |
| FX | EQ, pan, noise gate, metronome, pitch correction. |
| Pitch correction | **Automatic** correction: Phase 2B's native path (Rubber Band + pitch tracking) — this is the *only* automatic path (see Phase 2A note below). **Manual, Melodyne-assisted** correction: Phase 2A, hosted via VST3/ARA, always requires showing Melodyne's own editor UI. |
| Grid layout | Fixed 2x2 (4-cell) grid only. |
| Output format | MP4 (H.264 video + AAC audio), single quality tier for now. |
| Platform | Native Windows, C#/.NET (WPF) + a native C++/JUCE module for VST3/ARA hosting. |

**Data Model Rule (applies to Phases 3 and 4):** the project file stores a layout id plus a per-layer cell index, not hardcoded positions. The Mix Engine and Compositor iterate a `layers` collection of arbitrary size `N`. Only the UI layer is allowed to assume "4 layers / 2x2 grid" — this is what keeps raising the cap later from being a rework. Check this at review: if the mix/composite code has a literal `4` or `2x2` baked into its logic (not just the UI), that's a bug against this rule.

## Legal / Licensing Constraints

- Your app must **never bundle or redistribute** Melodyne. It only hosts a copy the user has separately purchased and already installed as a VST3 plugin, with their own Celemony license already authorized.
- **VST3 SDK licensing is not a practical constraint for this project.** Steinberg's dual license (GPLv3 / proprietary) imposes obligations that trigger on *distribution* of your app to others. Since this is a private, non-distributed app, there's nothing to comply with beyond normal use. Re-check only if you ever ship this to someone else.
- Different Melodyne tiers support different feature depth. Don't assume a specific tier — verify at runtime (and do an early, cheap check per Phase 0/1's de-risking task, below).

---

## Architecture Summary

```
Capture Engine → Sync Engine (latency-calibrated) → Project Model (N layers, cap=4)
                                                            ↓
                                          Mix Engine (per-layer chain, fixed order:
                                          pitch correction → noise gate → EQ → pan → gain)
                                                            ↓
                                  Plugin Host Bridge (native C++/JUCE: VST3 + ARA hosting)
                                       — hosts Melodyne's editor UI for manual correction —
                                       — Phase 2B provides the only automatic correction —
                                                            ↓
                                                  Compositor (2x2 grid)
                                                            ↓
                                                Export Pipeline (MP4)
```

Core libraries (locked — see CLAUDE.md's Pause Rule 2):
- **UI:** WPF
- **Audio I/O:** NAudio (WASAPI)
- **Video/audio capture, decode, encode, mux:** FFmpeg via CLI + raw pipes (escalate to FFmpeg.AutoGen only if piped preview decode proves too slow — pre-approved, not a scope change)
- **Compositing:** SkiaSharp (SkiaSharp.Views.WPF)
- **Plugin hosting (native module):** JUCE (`juce_audio_processors` for VST3 hosting, JUCE's ARA host-model classes — `ARAHostModel`, AudioSource, MusicalContext, PlaybackRegion — for ARA; modeled on JUCE's `AudioPluginHost` example). JUCE's own ARA-hosting docs are sparse — also study the ARA SDK's own host examples during the Phase 2A spike.
- **Native pitch correction fallback:** Rubber Band Library (shifting) + a pitch-tracking library (detection) for automatic correction
- **Project persistence:** JSON project file referencing raw media paths, per-layer parameters, layout id, and (if used) Melodyne ARA document state

---

## Phase 0 — Project Scaffolding

**Goal:** Get a buildable, runnable skeleton in place. There's no existing code to audit — this replaces what would otherwise be a baseline-audit phase.

**Tasks:**
1. Initialize a .NET solution: a WPF app project for the UI, and a class library project for the engine (capture/sync/mix/composite/export logic), referenced by the app project. Keep the split minimal — don't over-architect this before there's anything to architect around.
2. Add `.gitignore` entries for `bin/`, `obj/`, and a `media/` (or `recordings/`) directory for future test recordings (per CLAUDE.md's Recorded Media section).
3. Commit the initial scaffold.
4. **Melodyne de-risk check (cheap, do now — not the full Phase 2A spike):** confirm Melodyne's `.vst3` is present in `C:\Program Files\Common Files\VST3`, and record its reported version/tier in this file's Environment section (in CLAUDE.md) or a short note here. This is a 30-minute check, not an integration attempt — the goal is just to know early if the installed tier might lack ARA support, without front-loading the real spike.

**Acceptance:**
- `[auto]` Solution builds successfully and the WPF app launches to a blank window.
- `[auto]` Melodyne's `.vst3` file is located and its version/tier is recorded. (If it's genuinely not found, that's Pause Rule 3 — stop and ask, per CLAUDE.md.)

---

## Phase 1 — Layer 1 Input + Latency Calibration + Guide-Track Recording

**Goal:** Get the first layer in (recorded or uploaded), and get layers 2–4 recording in sync against a guide track — both across layers (guide-track alignment) and within a single layer (audio/video sharing a clock).

**Tasks:**

1. **Layer 1 ingestion (two paths):**
   - Path A — Record: capture webcam + mic directly, no guide track needed.
   - Path B — Upload: accept an existing audio-only or video file as layer 1. If audio-only, that grid cell renders a static placeholder in Phase 4.
2. **Within-layer A/V sync:** capture audio and video for a single recorded layer through **one** FFmpeg process (e.g. a `dshow` input capturing both the camera and mic into one container), so both streams share a single clock instead of being captured via separate APIs with independent latencies. This is what prevents lip-sync drift within a single layer, independent of the cross-layer guide-track sync in tasks 4–5.
3. **Virtual loopback device setup:** set up a virtual audio loopback device (VB-Cable or Windows' built-in Stereo Mix) so that a click/tone played to output can be captured back automatically without a physical cable or a human listening. This is what makes tasks 4 and the acceptance criteria below fully `[auto]`.
4. **Latency calibration:** one-time loopback test — play a short click/tone through the selected output device, capture it back via the loopback device, measure round-trip latency via cross-correlation. Store per (input device, output device) pair.
5. **Metronome:** toggleable (on/off), adjustable BPM, audible only during recording, never baked into the recorded track.
6. **Guide-track recording (layers 2–4):** play back the mixed composite of prior layers as a guide track while capturing new mic+camera input; apply the calibrated latency offset to align.
7. **Manual fine-tune:** a millisecond offset slider to nudge sync after automatic alignment.
8. **Minimal settings persistence (subset of Phase 5 — scoped narrowly on purpose):** save the calibrated latency offset per device pair to a lightweight local settings file (not the full project format from Phase 5) so it survives app restarts. This is intentionally small — just enough to satisfy the "latency offset persists across sessions" criterion below without inventing project persistence ahead of Phase 5.

**Acceptance:**
- `[auto]` Layer 1 works from both a fresh recording and an uploaded file.
- `[auto]` A recorded layer's own audio and video are in sync — verified via the loopback device: play a click and a simultaneous visual flash, capture both, and measure the offset between the audio transient and the video frame change programmatically. (This replaces a manual "clap test" with an equivalent automated check.)
- `[auto]` Layers 2–4 record with the guide track audible, sample-aligned within ±20ms — verified via the loopback device and a cross-correlation script, no listening required.
- `[auto]` Metronome toggles cleanly and doesn't leak into recorded audio — verified via the loopback device (record with metronome on, confirm no metronome-frequency signal in the captured track).
- `[auto]` Latency offset persists across app restarts for the same device pair (via the minimal settings file from task 8).
- `[human]` Manual fine-tune control actually feels responsive and useful when nudging sync by ear. Batch into the phase-boundary check-in.

---

## Phase 2A — Plugin Host Bridge: VST3 + ARA Hosting (Melodyne, Manual Correction)

**Goal:** Host Melodyne as a VST3/ARA plugin, with its own editor UI embedded, so the user can manually apply Melodyne's pitch correction. **This phase does not attempt automatic correction — ARA exposes no host-side "apply correction" call; Melodyne's correction is driven by the user inside its Note Editor. Automatic correction only ever happens in Phase 2B.** Editor embedding is therefore a required deliverable of this phase, not optional.

**This is the highest-risk phase in the project. Timebox: 3 sessions or 10 failed build/run attempts at the core analyze→render loop, whichever comes first — then stop, report status, and let the human decide whether to continue or deprioritize. Build Phase 2B first (see Suggested Order below) so the app already works end-to-end before this phase starts.**

**Tasks:**

1. **Native host module setup (C++/JUCE):**
   - Create a JUCE-based native module (compiled as a Windows DLL) modeled on JUCE's `AudioPluginHost` example, including both `juce_audio_processors` (VST3 hosting) and JUCE's ARA host-model classes.
   - This module is separate from your main C#/.NET app; it will be called into via a plain C ABI (`extern "C"`).

2. **Plugin discovery:**
   - Scan `C:\Program Files\Common Files\VST3` for installed plugins.
   - Identify Melodyne by its plugin identifier and check whether it exposes the ARA factory extension.
   - If not found or ARA unavailable: report this back to the C# layer so it can rely on Phase 2B for that session/layer instead. (Phase 0's de-risk check should already have told you which case you're in.)

3. **ARA session setup:**
   - Create an ARA Document Controller instance via the host bridge.
   - For each layer the user wants to manually correct, create an ARA audio source from that layer's recorded audio and register it with Melodyne for analysis.
   - Trigger analysis.

4. **Editor UI embedding (required, not optional):**
   - Embed Melodyne's own plugin editor window using JUCE's native window embedding, hosted inside the app's UI (a child window or docked panel).
   - The user performs the actual correction inside Melodyne's Note Editor, exactly as they would in a DAW.

5. **Retrieving corrected audio:**
   - After the user has made their edits in Melodyne's editor, use Melodyne's ARA playback renderer to pull back the processed audio for that layer, and feed it into the Mix Engine (Phase 3) in place of the raw layer audio.

6. **C-ABI surface exposed to C#/.NET.** Use caller-allocated buffers and polling, not returned arrays or callbacks — this keeps marshaling trivial:
   - `ScanForMelodyne() -> bool` (with capability info: tier, ARA support)
   - `CreateAudioSource(layerId, samples, sampleRate) -> sourceHandle`
   - `AnalyzeSource(sourceHandle) -> bool`
   - `GetAnalysisState(sourceHandle) -> int` (poll this instead of using a progress callback)
   - `ShowEditorWindow(sourceHandle)`
   - `GetCorrectedLength(sourceHandle) -> int64`
   - `RenderCorrected(sourceHandle, float* buffer, int64 capacity) -> int64` (caller allocates `buffer` sized from `GetCorrectedLength`; returns the number of samples actually written)
   - `ReleaseSource(sourceHandle)`

7. **P/Invoke bridge in C#/.NET:** wrap the above C-ABI calls so the Mix Engine can call into the native module transparently.

8. **ARA state persistence keying:** persist each layer's ARA archive (Melodyne's own document state) keyed by `(layerId, sourceAudioHash)`. If a layer is re-recorded, its audio hash changes, which automatically invalidates the stale Melodyne state instead of silently reusing corrections that no longer match the audio.

**Acceptance:**
- `[auto]` App correctly detects whether a licensed, ARA-capable Melodyne install is present (pass/fail is checkable without listening).
- `[human]` Melodyne's editor window opens embedded in the app, and a manual correction made there is audibly reflected in the rendered output on a test layer.
- `[auto]` If Melodyne isn't present or lacks ARA, the app falls back to relying on Phase 2B without crashing.
- `[auto]` Re-recording a layer invalidates its stored ARA state (verified by checking the archive key changes, not by listening).

---

## Phase 2B — Native Pitch Correction Fallback (the Only Automatic Path — Build This First)

**Goal:** A fully automatic pitch-correction path that doesn't depend on Melodyne or any manual editing step. This is not just a fallback for when Melodyne is unavailable — it is the **only** automatic-correction path in the whole app, since Melodyne (Phase 2A) is inherently manual. Build this before Phase 2A.

**Tasks:**
1. Integrate Rubber Band Library for pitch shifting.
2. Integrate a pitch-detection/tracking method (e.g. pYIN, or a small ONNX-exported CREPE model via ONNX Runtime).
3. Implement automatic correction: detect pitch, compute correction amount toward the nearest note in a target scale/key, apply via Rubber Band.
4. Expose this as the pitch-correction backend the Mix Engine calls for any layer that isn't using Melodyne's manual path.

**Acceptance:**
- `[auto]` Given a test recording with a known, deliberately-flattened pitch, the corrected output's detected pitch (via the same pitch-tracking method, run again on the output) is measurably closer to the target note than the input was. This is checkable by script, no listening required.
- `[human]` The correction actually sounds musically natural, not robotic/warbly, on a real (not synthetic) test vocal. Batch into the phase-boundary check-in.

---

## Phase 3 — Multi-Track Mixing Engine

**Goal:** Non-destructive per-layer audio processing, adjustable live. Fixed per-layer chain order: **pitch correction → noise gate → EQ → pan → gain.** This fixed order matters because it's what lets Phase 2A's Melodyne-rendered audio and Phase 2B's auto-corrected audio feed into the exact same downstream chain — pitch correction (from either backend) always happens first, before the rest of the mix.

**Tasks:**
1. Per-layer parameters, held in memory during the session (full save/load is Phase 5; see also Phase 1 task 8 for the narrower latency-offset persistence already in place):
   - Volume (gain) + mute/solo
   - Pan (stereo position, -1 to +1)
   - EQ (3-band shelf/bell per layer is enough for v1)
   - Noise gate (threshold + release)
   - Pitch correction backend selection (2A manual-result or 2B automatic) + its parameters
2. Real-time mixed preview: play all layers together with current settings applied.
3. Mixer UI: one channel strip per layer (up to the configured cap — see Data Model Rule).
4. **Minimal round-trip within the session (subset of Phase 5):** changing a parameter and switching between layers doesn't lose earlier changes during the same session. Full save-to-disk-and-reload-later is explicitly Phase 5's job, not this phase's.

**Acceptance:**
- `[auto]` Changing any parameter updates the live preview mix without re-recording (checkable via signal inspection — does the output waveform change when a parameter changes).
- `[auto]` Parameters set on one layer survive switching to another layer and back, within the same running session.
- `[human]` The mixed preview actually sounds like a coherent mix (levels/EQ/gate feel reasonable, not just "technically applying"). Batch into check-in.

---

## Phase 4 — 2x2 Grid Compositing

**Goal:** Render up to the configured layer cap's worth of video into a fixed 2x2 grid for preview and export. Per the Data Model Rule, the compositor iterates a `layers` collection of size `N` — the "2x2" assumption lives in the UI/layout config, not hardcoded into the compositor's loop.

**Tasks:**
1. Fixed 2x2 layout: top-left, top-right, bottom-left, bottom-right, ordered by recording order.
2. Audio-only layers (from Phase 1's upload path) render a placeholder visual (static image, waveform, or solid color) in their grid cell.
3. Composite decoded frames from each layer into their assigned cell per output frame, for both live preview and final export.

**Acceptance:**
- `[auto]` Preview shows all recorded layers simultaneously in the 2x2 grid at the correct cell positions (checkable by inspecting rendered frame output programmatically).
- `[auto]` An audio-only layer renders its placeholder without breaking compositing (no crash, correct cell filled).
- `[human]` The composited preview actually looks synced when watched (layers don't visibly drift against each other over a longer recording). Batch into check-in.

---

## Phase 5 — Project Persistence

**Goal:** Save/reopen a full project and retain everything — this supersedes the narrower, temporary persistence added in Phases 1 and 3.

**Tasks:**
1. Project file (JSON) storing: raw media paths per layer, per-layer mix parameters, layout id + per-layer cell index (per the Data Model Rule), latency offset used, metronome BPM, and which pitch backend was used per layer.
2. If Melodyne (ARA) was used on a layer: persist its ARA archive as already keyed in Phase 2A task 8 (`layerId` + `sourceAudioHash`), so reopening a project doesn't require re-running analysis from scratch, and re-recording a layer correctly invalidates stale state.
3. Load flow: reopening a project restores full editable state — nothing destructively baked in until export.
4. Migrate the narrow settings introduced in Phase 1 (latency offset) and Phase 3 (in-session parameter round-trip) into this full format; the earlier, narrower files/behavior can be retired once this phase's acceptance criteria pass.

**Acceptance:**
- `[auto]` Close and reopen a project; every parameter, every layer's sync alignment, and (if used) Melodyne's correction state is byte-for-byte as left (checkable via automated round-trip comparison).

---

## Phase 6 — MP4 Export Pipeline

**Goal:** Produce a final MP4 file.

**Tasks:**
1. Mix down all layers (with the fixed chain from Phase 3 applied, using whichever pitch backend each layer used) to a single master audio stream.
2. Render the composited 2x2 video (Phase 4) to a video stream.
3. Encode: H.264 video + AAC audio, mux into a single `.mp4` container via FFmpeg.
4. Single quality tier for now (e.g. 1080p / 192kbps AAC); expand to presets later if needed.

**Acceptance:**
- `[auto]` Export produces a valid, playable `.mp4` file (checkable via a media-info/ffprobe validity check and confirming both audio and video streams are present and the expected duration).
- `[human]` The exported file actually sounds and looks right end-to-end when watched. Final check-in before calling the whole build done.

---

## Explicitly Out of Scope for This Build

- Collaboration / link-sharing / multi-user / cloud sync
- Layouts other than 2x2
- More than 4 layers
- Output formats other than MP4
- Automatic (non-Melodyne-editor) use of Melodyne — its correction is inherently manual; don't attempt to script around that

---

## Suggested Order of Work for Fable 5

1. **Phase 0** (scaffolding + Melodyne de-risk check) — must happen first.
2. **Phase 1** (recording + within-layer sync + cross-layer guide-track sync) — highest-risk *core* mechanic; get this solid before UI polish. Set up the virtual loopback device early in this phase — it pays for itself immediately by making the rest of the phase's checks automatic.
3. **Phase 2B** (native automatic pitch correction) — build this before 2A. This is the only automatic-correction path in the app, not just a fallback, so it's load-bearing.
4. **Phase 3** (mixing engine, fixed FX chain order) — full mixing pipeline working.
5. **Phase 4** (compositing) — can run in parallel with Phase 3; only needs decoded video frames.
6. **Phase 5** (full persistence) — wraps Phases 1–4's data into a saved/reloadable project, superseding their narrower interim persistence.
7. **Phase 6** (MP4 export) — final integration. **At this point you have a fully working app, entirely without Melodyne.**
8. **Phase 2A** (Melodyne VST3/ARA hosting with embedded editor UI) — layered on afterward as an enhancement track. Respect the session/attempt timebox; if it stalls, the app already works fully on Phase 2B, so this can be deprioritized without blocking anything.
