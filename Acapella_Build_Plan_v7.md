# Acapella Rebuild — Build Plan (v7, DAW-Grade Mixing: One Live FX Chain)

> Instruction-mode plan continuing from v6 (P3a tasks 0–8/10 done, launcher UI committed at `eae2c2c`). v6's hosting bridge is sound at the C-ABI level, but the integration above it violates the basic VST contract Daniel stated: **a plugin is a function — audio goes in, modified audio comes out — and there must be exactly one such function per (layer, stage) that the editor UI, the preview playback, and the export all flow through.** Today there are three disconnected copies (see `reviews/Bug_Audit_2026-07-13_VstHosting.md`, section A — that audit is this plan's Phase Q0 worklist). v7 restructures mixing around the FL Studio mixer model: per-layer insert slots, playback-first mixer screen, FabFilter as the only visible FX, native DSP as an invisible fallback, and what-you-hear-is-what-you-export as an enforced invariant.
>
> Tagging contract unchanged: `[auto]` decides phase completion; `[human]` batches into phase-boundary check-ins.
>
> CLAUDE.md governs behavior. Scope notes for Pause Rule 2: this plan approves the FX-rack/slot model (per-stage enable, all stages off by default), the single-hosting-service refactor, mixer meters, and mixer-screen transport. Vendor scope unchanged (FabFilter five + Melodyne only). No new dependencies.

## Confirmed scope (delta from v6)

| Decision | Value |
|---|---|
| Processing model | **One live FX chain per layer.** A single hosting service owns one plugin instance per (layer, stage); the Mixing screen's editors, the preview engine, and the export engine all use those same instances. Editing a plugin is audible on the next processed block; export captures the same state. |
| FX slots (FL model) | Each layer has five insert slots in fixed chain order: Noise gate → Compressor → EQ → Reverb → Limiter (reverb post-pan per v6's chain note). **All slots default OFF/empty.** Enabling a slot inserts the FX; a slot can be toggled (bypass) without losing its state. |
| Backend visibility | **FabFilter is the FX.** When the matching plugin is installed, enabling a slot creates/uses it and the slot's only UI is "open its editor" + enable toggle. Native DSP is invisible: it silently backs a slot only when the plugin is missing (still no visible selector). Native parameter sliders appear only in that fallback case. |
| Mixer screen | DAW-style: usable **while playing**. Transport (same engine/position as the Editor screen), per-layer and master level meters, the layer's slot rack, pan/volume/mute/solo readouts. Switching layers or screens never interrupts playback. |
| Pitch stages | Unchanged (None / Native automatic / Melodyne manual); Melodyne remains Phase 2A. |
| Everything else | Unchanged: 4-layer cap, 2x2 grid, MP4 export, vendor hard-limit, stack, chain order incl. bus (headroom → master volume → master limiter). |

---

## Phase Q0 — Hosting architecture repair (fix the audit; nothing new user-visible)

**Goal:** Every defect in `Bug_Audit_2026-07-13_VstHosting.md` section A plus B1–B8 fixed, so the layers above can assume: one instance per (layer, stage), correct threads, correct state lifecycle, clean replays.

**Tasks:**
1. **Single hosting service (audit A1):** extract `HostedPluginService` owning the one `HostedPluginInstanceCache` + `HostedPluginAvailability`; MainWindow, PreviewPlaybackEngine, and ExportEngine all receive it. Delete the two extra MixEngine-owned caches (MixEngine takes the service as a dependency).
2. **Dispatcher affinity (A3, B8):** all lifecycle calls (scan, create, state get/set, editor open/close, reset, release) marshal to the WPF dispatcher inside the service; `aca_initialize` becomes mandatory (bridge errors if skipped). Availability scan runs once at startup, async on the dispatcher, with status text.
3. **State lifecycle (A2, B5, B7):** live-state pull into `LayerMixParameters.XHostedState` on: editor close, project save, export snapshot, undo snapshot capture; live-state *push* on: project load, undo/redo restore. Replace the size/get TOCTOU pair with a single-round-trip state call. Empty state surfaces as an error.
4. **Per-slot enable flags (A4):** `NoiseGateEnabled`/`EqEnabled` added (compressor/limiter/reverb already have them); chain builds only enabled slots; all five default off. DTO + migration: projects saved before this default gate/EQ to *enabled* on load (they were always-on before), so old projects sound unchanged.
5. **Replay hygiene (A5):** `aca_reset(handle)` in the bridge; chain build resets every cached instance it wires in.
6. **Bridge hardening:** editor-close dangling-owner fix (B4), `setValueNotifyingHost` (B6), export-engine disposal check (B1).
7. **Repo repair (B3):** remove the corrupt multi-pack-index, repack, `git fsck` clean. (Derived files only — no Pause Rule 1 trigger.)
8. **Task 9 from v6 (state-change polling):** with the single service in place, poll a state hash for instances with open editors on the existing debounce timer → mark project dirty + refresh preview-dependent caches.

**Acceptance:**
- `[auto]` Identity test: the instance returned to the UI for (layer, stage) is reference-equal to the one inside a freshly built chain.
- `[auto]` State round-trip: set state via service → save → DTO blob equals live `GetState()`; load a project → live instance state equals the file's blob.
- `[auto]` Replay test: with a latency-reporting plugin (Pro-L 2) enabled, two consecutive plays produce sample-identical output (reset works, no stale lookahead).
- `[auto]` Thread test: chain build from a background thread completes with all lifecycle calls observed on the dispatcher (assert via service instrumentation); no CRT/JUCE assertion output.
- `[auto]` All-slots-off is the default: a fresh layer's processed output is bit-identical to its unprocessed input (modulo pan/volume).
- `[auto]` `git fsck` reports no errors.
- `[human]` Open Pro-Q on a playing layer, drag a band: the change is audible immediately. Close the app, reopen the project: the tweak survived.

---

## Phase Q1 — DAW-style mixer screen (requirements 1–3 user-visible)

**Goal:** The Mixing screen works like an FL Studio mixer channel: media plays *in* it, slots insert FX, meters move, FabFilter editors are the FX UI.

**Tasks:**
1. **Transport on the Mixing screen:** the same transport row (play/pause, ±5s, restart, position/duration, thin timeline) bound to the single PreviewPlaybackEngine; entering/leaving the Mixing screen never stops or rebuilds playback unless parameters changed. No video rendering on this screen — audio + meters are the feedback.
2. **Slot rack UI:** five fixed slots in chain order per layer, each: enable toggle (power button), FX name ("Pro-G", "Pro-C 3", …), Open-editor button. Missing plugin → slot shows the native fallback's compact sliders inline when enabled (the only place native controls ever appear), still no "backend" wording anywhere.
3. **Level meters:** per-layer post-slot-rack peak/RMS meter and a master bus meter, fed by a tap sample provider (from v5 P3's planned tap work) at ~30Hz UI refresh. Mute/solo/pan/volume readouts mirrored from the sidebar (edit in either place, one source of truth).
4. **Editor window management (B9):** relaunch brings the existing window to front (implement the deferred `toFront`); closing a layer closes its editors; app minimize hides editor windows (JUCE window flag or explicit hide on WPF state change).
5. **Reverb slot polish:** tail-length change in Pro-R's editor picks up on the next rebuild (state-poll from Q0 task 8 triggers it); document the one-rebuild delay (B11).
6. Solo-in-place toggle stays; top strip trimmed to layer identity + solo + meters.

**Acceptance:**
- `[auto]` Transport commands issued from the Mixing screen and Editor screen interleaved under the Q0 stress test: single sink, consistent position.
- `[auto]` Slot enable/disable mid-playback takes effect on the next debounced rebuild without stopping the transport (position preserved within one buffer).
- `[auto]` Meter tap reports levels consistent with a known test signal (0dBFS sine → meter within 0.5dB).
- `[human]` The screen reads like a DAW channel strip: play, open Pro-C, compress, watch the meters respond; missing-plugin fallback sliders look intentional, not like a second product.

---

## Phase Q2 — Parity, persistence, and regression walls

**Goal:** "What you hear is what you export" becomes tested law, and the whole FX system survives save/load/undo.

**Tasks:**
1. Export path consumes the shared service: instances reused (reset first), or fresh instances seeded from live state — either way `[auto]`-proven identical output.
2. Undo/redo covers slot enables and plugin-state changes (state hash snapshots from Q0 task 3; a plugin tweak is one undo step per editor-close or per poll-detected change burst).
3. Latency/tail regression tests with hosted slots: impulse alignment across layers with Pro-L enabled on one layer only (the v6 sync-parity test, now against the real shared service); reverb tail extends layer duration exactly once.
4. Test-suite gap closure (B12): identity, state round-trip, replay-reset, export-parity tests all in `dotnet test` (skip gracefully when a plugin isn't installed; assert-skip is loud in output, not silent).

**Acceptance:**
- `[auto]` Export of a project with live-edited plugin state equals a preview mixdown of the same project, sample-for-sample (within float tolerance), including latency compensation.
- `[auto]` Save → close → reopen → export produces the same file as exporting before closing.
- `[auto]` Undo after a plugin edit restores the previous audible state (state-hash equality), redo re-applies.
- `[human]` One full session: record/upload 4 layers, shape each with 2–3 FabFilter slots, undo/redo across plugin edits, save, reopen, export — everything holds.

---

## Phase Q3 — Quality pass (carried from v5 P4, updated)

Unchanged intent from v5's P4, updated targets: performance budget re-measured with hosted slots (Play < 1s warm cache and pre-created instances; slot toggle refresh < 500ms; first-run plugin scan off the critical path), error surfacing (every bridge error string reaches the status bar), tooltips/tab-order/theme audit on the new mixer UI, media-folder hygiene, and the release-candidate `[human]` check-in before Phase 2A.

---

## Phase 2A — Melodyne VST3/ARA hosting (unchanged)

After Q0–Q3. Now additionally benefits from Q0: the single hosting service, dispatcher marshaling, state lifecycle, and editor-window management are exactly the substrate 2A's ARA work needs. Timebox and Pause Rule 3 conditions unchanged from v4/v6.

---

## Suggested order of work

Q0 → Q1 → Q2 → Q3 → 2A, strictly — Q1's UI is meaningless until Q0 makes edits audible, and Q2's tests must land before Q3 declares quality. Q0 is the phase to be most careful with: it's a refactor of live plumbing, so lean on the regression carve-out in CLAUDE.md (re-run the preview/export test suites after each task, not once at the end).

## Explicitly out of scope for v7

- Everything out of scope in v4–v6 (vendors beyond FabFilter+Melodyne, other layouts/caps/formats, sidechain, multiband).
- Reorderable/free-assignment FX slots (fixed five-slot chain order only; FL-style drag-reorder is a future decision).
- Send/aux buses, per-send reverb (insert-only).
- Plugin parameter automation over time.
- Native-DSP visual panels beyond the compact fallback sliders (the v5 P3 SkiaSharp plugin-style visualizations are dropped — FabFilter's own editors made them redundant).
