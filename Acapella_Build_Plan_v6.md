# Acapella Rebuild — Build Plan (v6, Generic VST3 Hosting + FabFilter Chain) — revised by Fable

> Verified and directly revised by Fable against the codebase at v5-P2-complete (`43b5bac`). The draft's structure and intent are sound and are kept; the revisions fix four technical gaps that would have caused rework — (1) the mix-chain line contradicted the code and itself, (2) **plugin latency compensation was missing entirely** (Pro-L 2's lookahead alone would have re-introduced the layer-desync class of bug this project has now fixed twice), (3) reverb's placement ignored that the layer chain is mono until pan, and (4) P3a assumed JUCE/VST3 SDK infrastructure that isn't installed yet. Each revision is marked **[REV]**. Resolved open questions are answered inline; the two that genuinely need Daniel remain at the bottom.
>
> Continues from v5 (P0–P2 complete; P3 not started). Inserts P3a (generic non-ARA VST3 hosting), reworks P3 around backend-selectable FX stages, keeps P4 and Phase 2A. Melodyne's ARA work now builds on P3a's scaffolding.
>
> Tagging contract unchanged: `[auto]` decides phase completion; `[human]` batches into phase-boundary check-ins.
>
> CLAUDE.md still governs behavior. **Scope note for Pause Rule 2: this plan is the approval for generic VST3 hosting, the five FabFilter plugins listed below, and the reverb stage.** Hosting any plugin not listed here still requires a pause. **[REV]** First task of P3a includes updating CLAUDE.md's locked-decision list (FX list + technology list) to match, so the two sources of truth don't diverge mid-run.

## Confirmed scope (delta from v5)

| Decision | Value |
|---|---|
| New capability | Generic VST3 hosting (no ARA) — scan, instantiate, prepare, process, query latency/tail, show native editor, persist state. Separate from, and simpler than, Melodyne's ARA path. |
| New FX-hosting targets | FabFilter **Pro-Q 4** (EQ), **Pro-C 3** (compressor), **Pro-L 2** (limiter), **Pro-G** (noise gate), **Pro-R 2** (reverb) — already purchased/installed on Daniel's machine, discovered via the standard shared VST3 folder, same "never bundle, host the user's own license" pattern as Melodyne. |
| Vendor scope (hard limit) | **Only FabFilter (the five above) and Melodyne are ever hosted.** No other third-party plugin vendor, now or in a future version, without a new explicit decision — this isn't a "pick the best plugin per category" build, it's these two vendors specifically. Native DSP (EQ/gate/compressor/limiter) is not a plugin and is unaffected by this line — it's your own code, kept as the default/no-dependency backend per stage regardless of vendor scope. |
| Reverb | **New FX stage**, not in any prior locked scope. Optional per layer, off by default. Real scope addition, approved by this plan. |
| Native DSP from P0–P2 | **Kept as an automatic fallback, not a prominent user-facing choice.** FabFilter is the intended/default backend for EQ/gate/compressor/limiter wherever it's detected. Native DSP is what runs when a given FabFilter plugin isn't installed — resilience, not a real either/or decision the user makes day to day. See P3 task 1 for the selection logic. |
| Everything else | Unchanged from v5: 4-layer cap, 2x2 grid, MP4 export, Melodyne-via-ARA as the manual pitch path, WPF/NAudio/FFmpeg/SkiaSharp/JUCE stack. |

**Mix chain order (corrected) [REV]:** the draft's chain line omitted the compressor and both limiters (which have existed since v5 P0/P1) and placed reverb "after EQ and before pan/gate", which contradicts itself and the code. The chain as actually built (`MixEngine.BuildLayerChain` + bus) is:

> pitch correction → noise gate → compressor → EQ → **pan (mono → stereo)** → layer volume (gain/mute/solo) → per-layer limiter → bus sum → master volume → master limiter

**Reverb goes after pan, before layer volume**, processed in stereo: everything before pan is a mono chain (`PanningSampleProvider` requires mono input), and Pro-R 2 is a stereo-in/stereo-out effect — inserting it pre-pan would force a mono downmix of the reverb or a chain restructure. Post-pan also means layer volume/mute scales the reverb tail with the layer (muting a layer mutes its reverb), which is the expected behavior. Final chain with reverb:

> pitch → gate → comp → EQ → pan → **reverb (stereo, optional)** → layer volume → layer limiter → bus → master volume → master limiter

## Legal / licensing

Same posture as Melodyne: never bundle or redistribute any FabFilter plugin; host only the user's own licensed installs. Runtime presence check via the shared plugin scan; a missing FabFilter plugin makes that stage's hosted option unavailable (native backend remains default) — never a blocking condition.

---

## Phase P3a — Generic VST3 Hosting (new, build before the reworked P3)

**Goal:** Extend the project with a JUCE-based native module that loads and drives plain VST3 plugins (no ARA). Deliberately simpler than Phase 2A; built first to de-risk the shared hosting infrastructure before Melodyne's ARA-specific work.

**Tasks:**

0. **[REV] Toolchain + build integration (was implicit, is actually the first real work):** JUCE and the VST3 SDK are *not yet installed* (v4 deferred them to Phase 2A). Install standalone CMake (`winget install Kitware.CMake`), clone JUCE + VST3 SDK, create the native module project (`src/Acapella.Host.Native`, CMake, builds a x64 DLL), and integrate: a canonical build command documented in CLAUDE.md's Environment section (either an MSBuild pre-build step invoking CMake, or a documented two-step `cmake --build` + `dotnet build`), DLL copied to the app output like `librubberband.dll` is today. VST3 SDK licensing: private, non-distributed use — GPLv3 side is fine, no action needed (per `reviews/Fable5_Plan_Review.md`).
1. Plugin scan: given a plugin identifier, locate it under `C:\Program Files\Common Files\VST3` and report found/not-found + name/version, for all five FabFilter plugins and Melodyne (this replaces/absorbs the Melodyne de-risk scan planned for 2A).
2. Instantiate a plain VST3 processor: `CreatePluginInstance(pluginId) -> instanceHandle`.
3. **[REV] Prepare/teardown lifecycle (missing from draft):** `PrepareInstance(handle, sampleRate, maxBlockSize)` before any processing (VST3 processors require setupProcessing/setActive), and re-prepare on sample-rate change.
4. Parameter access: enumerate (name, range, value), get/set by index. (Used for state-change detection and future automation; the primary control surface is the plugin's own editor.)
5. **[REV] Latency + tail queries (missing from draft — without these, hosted stages desync layers):** `GetLatencySamples(handle) -> int` (Pro-L 2 lookahead and Pro-Q 4 linear-phase mode report thousands of samples) and `GetTailSeconds(handle) -> double` (reverb decay).
6. Audio processing: `ProcessBlock(handle, float* inL, float* inR, float* outL, float* outR, int numSamples)` — **[REV]** stereo pair rather than the draft's single in/out buffer, since the chain is stereo from pan onward and mono stages can duplicate the channel; caller-allocated, no callbacks, per the v4 bridge conventions.
7. Native editor window: `ShowEditorWindow(handle)` / `CloseEditorWindow(handle)`. **[REV]** Opens as its **own top-level window** (JUCE DocumentWindow), *not* embedded in a docked panel — the draft said "same embedding approach as Melodyne's", but Melodyne's editor doesn't exist yet and `UI_Design_Spec.md` explicitly specifies the launcher/own-window pattern ("No inline-embedded Melodyne editor"). Own-window is also drastically simpler across the C++/WPF boundary. Both this and 2A use this pattern.
8. State persistence: `GetStateSize/GetState/SetState` (VST3 component+controller state chunks). Stored base64 in the project DTO per hosted stage. (Draft open question 4, answered: FabFilter state chunks are small — kilobytes, not megabytes; base64 in JSON is fine. Add a defensive 1MB-per-stage cap with a clear error.)
9. **[REV] State-change detection:** the app must notice when the user tweaks something in the plugin's editor (to invalidate the preview and mark the project dirty) — no callbacks in the C-ABI, so poll a cheap `GetStateHash(handle)` (or state size + first bytes) on the existing debounce timer while an editor window is open.
10. `ReleaseInstance(handle)`; P/Invoke wrapper in C#, parallel to the planned Melodyne bridge.

**Acceptance:**
- `[auto]` **[REV]** Full build (native module + solution) succeeds from a clean checkout with the two documented commands.
- `[auto]` Each of the five FabFilter plugins, if present, is detected with name/version; a missing plugin reports not-found without error (test skips, doesn't fail, when a plugin is absent).
- `[auto]` Create → prepare → process test signal → release, 100×, no handle/memory leak (handle count returns to zero).
- `[auto]` State round-trip: set parameters, GetState, fresh instance, SetState, parameter values match.
- `[auto]` **[REV]** Latency compensation groundwork: an impulse processed through Pro-L 2 (reports lookahead latency) lands at the same sample index as the input impulse after the caller trims `GetLatencySamples` — the test proves the query is truthful.
- `[human]` A hosted plugin's editor opens in its own window and a manual tweak there is audibly reflected in processed output (and the preview refreshes within the debounce interval without a manual action).

---

## Phase P3 — Mixing screen: backend-selectable FX stages (revised from v5)

**Goal:** Same visual goal as v5's P3, with a per-stage backend selector — Native (v5's SkiaSharp-visualized DSP) or the matching hosted FabFilter plugin.

**Tasks:**
1. **Backend selection — automatic, not a prominent dropdown [REVISED per Daniel]:** per sub-stage (EQ, gate, compressor, limiter — reverb is separate, see task 5), the app auto-selects FabFilter's matching plugin if detected at scan time; native DSP runs only when that stage's FabFilter plugin isn't found. This is not a day-to-day user choice — no equal-weight dropdown. The Mixing screen shows which backend is active as a small status label on each stage's panel (e.g. "Pro-Q 4" or "Native EQ — Pro-Q 4 not found"), not a selector control. An advanced override (force-native, e.g. for lower CPU or testing) can live in a Tools/settings menu rather than inline on the panel — low priority, build only if time allows after the core auto-select path works. Pitch correction's existing None/Native/Melodyne dropdown is unchanged (that one genuinely is a user decision, since Melodyne is manual-only and native is fully automatic — not a detection fallback).
2. **Native backend:** everything from v5's P3 tasks 1–2 (combined Dynamics panel with LIMIT/COMP tabs + gate section, EQ response-curve-over-spectrum) — unchanged.
3. **Hosted backend:** stage panel becomes a launcher (Melodyne-subtab pattern): "Open Pro-Q 4…" button plus an enabled checkbox; no custom visualization for hosted plugins — their own UI is the visualization.
4. **Mix engine integration:** a `HostedPluginSampleProvider` wraps `ProcessBlock` as an `ISampleProvider` so hosted stages slot into the existing pull-based chain at the same positions as their native counterparts (chain order above). Mono stages (pre-pan) feed the same signal to both plugin channels and take the left output. **[REV] Latency compensation is mandatory:** on chain build, sum `GetLatencySamples` across the layer's hosted stages and drop that many samples from the chain head (mirror of the Rubber Band start-delay fix), identically in preview and export.
5. **Reverb stage:** stereo insert post-pan (see chain note), Off by default, only offered when Pro-R 2 is detected; no native fallback. **[REV] Tail handling:** a layer with reverb enabled extends its effective duration by `GetTailSeconds` (feed silence through the chain past source end), so the tail isn't truncated at the layer's last sample — duration math in preview/export must use the extended length.
6. **Instance lifecycle [REV]:** one plugin instance per (layer, stage), created lazily on first selection, state restored from the DTO on project load, released on layer removal/app close. Preview rebuilds reuse live instances (don't recreate per rebuild — editor windows must survive a debounced refresh).
7. In-screen transport, solo-in-place default, top-strip cleanup: unchanged from v5's P3 tasks 3–5.

**Acceptance:**
- `[auto]` A stage auto-selects FabFilter when its plugin is detected and falls back to native when it isn't (test both by toggling a mocked detection result); the active backend and, if hosted, its state blob persist through DTO save/reload.
- `[auto]` Chain-order test: hosted stage lands at the same chain position as its native counterpart (gate-then-EQ vs EQ-then-gate style artifact check, per draft).
- `[auto]` **[REV]** Sync-parity test: a layer processed with Pro-L 2 (lookahead latency) stays sample-aligned with an unprocessed layer, in both preview mixdown and export (impulse-offset check) — this is the test that prevents hosted plugins from re-introducing the desync bug class.
- `[auto]` Reverb enabled adds a measurable decay tail to an impulse **and the layer's rendered duration extends accordingly**; reverb off is bit-identical to never-enabled.
- `[human]` Native panels still read like the FL references; hosted launchers feel consistent with the Melodyne launcher; tweaking a hosted plugin during playback is audibly live.

---

## Phase P4 — Quality pass (unchanged from v5, renumbered position only)

Same content as v5's P4, plus: hosted-backend state round-trips checked in the DSP correctness sweep, and the performance budget (Play < 1s warm, edit-refresh < 500ms) re-measured with hosted stages active, since P/Invoke-per-block adds overhead.

---

## Phase 2A — Melodyne VST3/ARA hosting (builds on P3a, otherwise unchanged from v4/v5)

Deferred until P0–P4 done. v4 Phase 2A tasks 1–2 (module setup, discovery) are absorbed by P3a; the ARA-specific work (Document Controller, audio-source registration, analysis trigger, archive persistence keyed by layer id + source hash) layers on top. Editor uses P3a's own-window pattern. Timebox (3 sessions or 10 failed attempts at the analyze→render loop) and Pause Rule 3 conditions unchanged.

---

## Open questions — resolved vs still Daniel's

**Resolved in this revision (technical calls Fable can make; veto anytime):**
- *Reverb chain position* (draft Q2): post-pan stereo insert, before layer volume — forced by the mono-until-pan chain architecture and by wanting mute/volume to scale the tail. See chain note.
- *State-persistence format* (draft Q4): base64 VST3 chunks in the project JSON, KB-scale, 1MB defensive cap. Fine as designed.

**Still need Daniel (one line each, at the next check-in — not blocking P3a's start, which is backend-agnostic):**
1. **Backend-selector granularity** (draft Q1, sharpened): the revision assumes per-sub-stage selectors (gate/comp/limiter each independently Native-or-FabFilter). If you'd rather have one global "use FabFilter chain" switch, say so before P3 task 1's UI is built — P3a is unaffected either way.
2. **Confirm the five plugins** (draft Q3): Pro-Q 4, Pro-C 3, Pro-L 2, Pro-G, Pro-R 2 — exactly these, with Pro-DS/Pro-MB staying excluded.

---

## Explicitly out of scope for v6

- Everything already out of scope in v4/v5 (collaboration, layouts ≠ 2x2, >4 layers, non-MP4 export, SAT/SUSTAIN stages, sidechain, multiband dynamics, waveform thumbnails, clip reordering).
- Pro-DS (de-esser) and Pro-MB (multiband dynamics) — **deferred to a future version, not permanently excluded.** Not part of this plan's work; revisit as a separate scope decision later, don't build toward them now.
- Any FabFilter plugin not in the five-item list, and any plugin from any vendor other than FabFilter/Melodyne — hard limit, see "Vendor scope" above.
- Building a native reverb DSP fallback.
- **[REV]** Embedding plugin editors inline in the WPF window (own-window launchers only, per UI_Design_Spec) — revisit only if Daniel asks.
- **[REV]** Driving hosted-plugin parameters from custom in-app sliders (parameter API exists for detection/automation-later; v6 UI control surface is the plugin's own editor).
