# Bug Audit #4 — ARA/Melodyne Hosting + Overall (for Sonnet to fix)

> Audit as of `d566ef1` (2A declared complete, 130/130 + 2/2 green). The session summary's claim that "selecting Melodyne manual now actually routes audio through Melodyne" is technically true and functionally empty: **Manual2A is an expensive passthrough.** Audio goes into a fresh, never-edited, never-analyzed-gated Melodyne session and the original audio comes back out. The flagged follow-up ("Edit in Melodyne… needs a persistent per-layer session") is not a polish item — it is the entire remaining substance of Phase 2A, because ARA has no headless "apply correction" (established in `reviews/Fable5_Plan_Review.md` §B1 at the very start of this project, and re-confirmed by how this code behaves). Section A is the causal chain of the dysfunction; B is native-bridge defects; C is the rest of the audit.

---

## A. Why Manual2A does nothing audible (five stacked causes)

### A1. A fresh, edit-less Melodyne session renders the original audio — by design of ARA
**Files:** `MelodyneAraPitchCorrector.cs` (fresh `AraHostSession` per `Correct()` call), `MelodyneEditorWindow.xaml.cs` (stub; its comment "not required for Manual2A's audio to actually sound like Melodyne's output" is wrong)

Melodyne's playback renderer outputs the audio *as modified by user edits in its model*. A brand-new document with an untouched playback region (`kARAPlaybackTransformationNoChanges`, no note edits, no Correct-Pitch macro run) renders the source unchanged. There is no host API to say "auto-correct this". So the current pipeline — create session → register → add region → render → destroy — is a lossless round-trip through Melodyne plus CPU cost. For Manual2A to ever change sound, the app needs the full loop: **persistent per-layer session → open Melodyne's editor window → user edits → re-render → archive persisted**. None of those four exist in production yet (see A2–A5).

**Fix direction (this is a phase of work, not a patch):** persistent `AraHostSession` per layer owned by `HostedPluginService` (mirroring the per-(layer, stage) plugin cache), editor window via the instance's `createEditorIfNeeded` path (the bound instance already requests `kARAEditorViewRole`), re-render triggered on editor close / state-change poll, and PitchCorrectionCache invalidation keyed by the ARA archive hash (A5). The stub `MelodyneEditorWindow` should be deleted in favor of the same launcher pattern the FabFilter slots use.

### A2. The render is never gated on analysis
**Files:** `MelodyneAraPitchCorrector.RenderWhole` (renders immediately after `AddPlaybackRegion`), `AraAnalyzeRenderLoopTests.cs:42-48` (documents this and works around it)

Even for the passthrough case, rendering starts the instant the region is attached. Melodyne analyzes asynchronously on its own workers; blocks rendered before analysis completes are whatever Melodyne does pre-analysis (implementation-defined: original samples or silence for the not-yet-transferred span). Today's output being "non-silent" (the test's only assertion) does not prove analyzed, model-driven rendering. Once real edits exist (A1), rendering un-analyzed audio will return wrong (pre-edit) audio nondeterministically.

**Fix:** poll analysis to completion before the render loop — which requires A3 first, because progress reporting is currently structurally broken.

### A3. `notifyModelUpdates()` is never called, so analysis progress can never be observed
**Files:** `AraBridge.cpp` (no call anywhere), `AcaModelUpdateController` (its map is only ever written from ARA's notifications), `aca_ara_get_analysis_progress` (always returns −1 in practice), test comment "analysis-progress polling never reports"

ARA delivers model-update notifications (including `notifyAudioSourceAnalysisProgress`) **only when the host calls `ARADocumentControllerInterface::notifyModelUpdates()` periodically from its main thread** — that's the documented contract, and it's why the test observed progress never arriving. The bridge implements the receiving side and never installs the pump. The `AcaModelUpdateController` machinery is dead code as shipped.

**Fix:** export `aca_ara_pump_model_updates(sessionHandle)` calling `getDocumentController().notifyModelUpdates()` (JUCE's ARAHostModel exposes it); the C# side calls it in its wait loop (A2) and, later, on the editor-open debounce tick. Note it must run on the hosted/UI thread.

### A4. The whole render runs synchronously on the WPF UI thread — freezing the message pump Melodyne needs
**Files:** `MelodyneAraPitchCorrector.Correct` (`RunOnHostedThread(() => RenderWhole(...))`), `HostedPluginService.RunOnHostedThread` (dispatcher.Invoke onto the WPF UI thread)

`RenderWhole` (session create + registration + full-track block loop) executes inside **one dispatcher frame on the UI thread**. Two consequences: the UI freezes for the full analyze+render duration of the track, and — worse — while blocked inside that frame, the message loop is not pumping, so JUCE timers/async callbacks and any main-thread-dependent part of Melodyne's analysis handshake (and A3's future pump) cannot run. This is a recipe for "renders before analysis forever" at best and a deadlock at worst once A2/A3 gating is added.

**Fix:** keep *lifecycle* calls (create/register/region/archive) dispatcher-marshaled, but run the block-render loop off the UI thread (`processBlock` is explicitly thread-safe per the bridge's own contract), with the analysis wait implemented as dispatcher-friendly polling (pump → check → yield), never a blocking loop inside a single dispatcher frame.

### A5. Archive persistence exists but is connected to nothing
**Files:** `AraHostSession.ExportState/ImportState` (only callers: tests), `LayerModel.AraArchiveKey` (persisted since v4, never written), `PitchCorrectionCache` (key ignores ARA state)

Grep confirms no production code calls `ExportState`/`ImportState`, and `AraArchiveKey` is round-tripped through the DTO but never assigned. So even after A1 builds the editor loop, edits would evaporate on project close. Two additional latent breakers for when it *is* wired:
- **Unstable persistent IDs:** `RegisterAudioSource(..., Guid.NewGuid())` — archives are matched to audio sources **by persistentID**; a new GUID per session means an exported archive can never re-attach on load. The ID must be stable and content-derived: `(layerId, sourceAudioHash)` exactly as the v6 plan specified.
- **Colliding modification IDs:** `AraBridge.cpp:495` hardcodes `"acapella-modification"` for every AudioModification in a document. Restore-by-persistentID cannot distinguish them the moment a session holds more than one source. Derive it from the source's ID (`{sourceId}-mod`).
- **Stale correction cache:** `PitchCorrectionCache`'s key is (layerId, sourceKey, backendType) — after a Melodyne edit, the cached rendered output is stale with no invalidation path. Fold an ARA-state hash (or a monotonic edit counter bumped by the state-change poll) into the key for the Manual2A backend.

---

## B. Native bridge defects (AraBridge.cpp)

### B1. Session teardown order is inverted — the "confirmed-benign" heap assertion is evidence, not noise
**File:** `AraBridge.cpp:256-281` (`AcaAraSession` member order + its own comment)

C++ destroys members in reverse declaration order. `instance` is declared **first**, so it is destroyed **last** — i.e. `documentController` is destroyed while the plugin instance is still bound to it. ARA's contract is the opposite: all plug-in extension bindings (which live until the bound plugin instance is destroyed) must be gone **before** the document controller is destroyed. The session summary's "pre-existing/confirmed-benign heap assertion in Melodyne's own teardown" is exactly the symptom this ordering produces; it should be treated as this bug, not annotated away. **Fix:** declare `instance` *after* `documentController` (destroy instance → then DC), or add an explicit destructor enforcing: audio sources → regionSequence/musicalContext → renderer refs → `instance.reset()` → `documentController.reset()`. Re-run teardown with the CRT heap checks temporarily on; expect the assertion to disappear.

### B2. `aca_ara_render_block` ignores which source you asked for
**File:** `AraBridge.cpp:547-566`

The `audioSourceHandle` parameter is only null-checked; the render is driven purely by the playhead against **every** region added to `playbackRenderer`. With one source per session (today's only caller) it's accidentally correct; the moment a persistent per-layer session (A1) or any multi-source document exists, "render source A" returns the sum of all sources whose regions overlap the playhead. **Fix:** either enforce one-source-per-session at the API level (error on second `add_playback_region`), or place each region at a distinct playback-time offset and render source-relative windows. The first is simpler and matches the per-layer architecture.

### B3. Latency/tail unqueried on the ARA path
`aca_ara_render_block` assumes sample-exact, zero-latency model rendering. That's usually true for ARA playback renderers (they're random-access), but it's unverified here, and the FabFilter path learned this lesson the hard way. Add a one-time `[auto]` check: render a click through an untouched region and assert its sample position is unchanged (also catches pre-analysis weirdness from A2).

### B4. Per-call allocations in the hot loop
`AudioBuffer<float> buffer(2, numSamples)` allocated per `aca_ara_render_block` call, and `RenderBlock` (C#) allocates two arrays per block. Trivial fix (session-owned scratch, caller-provided buffers); do it while touching the file.

---

## C. Other findings (overall audit)

**C1. Tests validate the wrong thing, so 2A "passed" while inert.** `AraAnalyzeRenderLoopTests` asserts only non-silence (passthrough passes); no test asserts *modified* output (impossible without edits — but an archive-import test could do it: capture an archive from a manually-edited session once, commit it as a fixture, assert import+render differs from the source), no test covers teardown order (run session create/destroy 20× under the CRT heap-check flag), and the production dispatcher is never exercised (tests default to `InlineHostedPluginDispatcher`, so A4-class threading issues are invisible to the suite). At minimum add the fixture-archive test and the teardown-loop test; mark the "renders without analysis gating" comment as a known defect rather than adopted behavior.

**C2. `PitchCorrectionCache` grows without bound.** Whole-track float[] per (layer, sourceKey, backend); every trim nudge is a new key and old entries are never evicted — long sessions leak hundreds of MB. Cap it (e.g. keep last 2 keys per (layer, backend)).

**C3. Melodyne UI affordances are misleading while A1 is unbuilt.** The Mixing screen offers "Melodyne manual" and an "Edit in Melodyne…" button that opens a stub explaining itself. Until the real editor loop lands, selecting Manual2A silently costs a full Melodyne render for zero audible change. Interim honesty fix: disable the backend option with a tooltip ("Melodyne editing arrives in the next update") or route it to Automatic2B with a status note — either is better than a placebo.

**C4. `aca_scan_ara_capability` skips the `juceIsInitialized` guard** on the argument that factory-metadata reads are thread-safe; but `findAllTypesForFile` on the VST3 format still loads the plugin module. It's the same call `aca_scan_plugin` guards. Low risk (Melodyne tolerates it, and it's called via `IsAraAvailable` from arbitrary threads by design) — but it means module load/unload can race a UI-thread scan of the same binary. Cheapest fix: route `IsAraAvailable` through the dispatcher like every other scan, drop the "safe from any thread" claim.

**C5. Housekeeping.** (a) `Probe.cpp`/`JuceHostProbe.cpp` are dead scaffolding now — delete or move under a `probes/` folder excluded from the build. (b) The vendored ARA_SDK example trees (pugixml docs samples, cpp-base64 tests) are compiled-adjacent clutter in the repo; confirm they're excluded from the CMake build and consider a sparse checkout. (c) `MelodyneEditorWindow.xaml(.cs)` — remove with A1's real launcher.

## Verified correct / no action

`AraHostSession`'s GCHandle pinning of channel buffers for the session lifetime (correctly fixes the dangling-pointer hazard the bridge's ownership rule creates); the two-call archive export convention and 1MB cap with a consistent-size retry; `aca_ara_create_session`'s error paths and ARA-factory acquisition (synchronous resolution of `createARAFactoryAsync` is correct for VST3 in-process); role-binding with all three roles known+assigned (matches JUCE's reference host, and the narrower attempt demonstrably failed); host-ref-is-the-pointer conventions (audio source, archive) are internally consistent; `HostedPluginService`'s single-cache/dispatcher architecture from Q0 remains sound — the ARA path's mistake was bypassing its *granularity* (whole-render-on-dispatcher) rather than its design; `PitchCorrectionCache` keying by backend type correctly separates Auto vs Manual outputs (staleness aside, see A5); FabFilter slot rack, meters, and export-parity work from Q1/Q2 show no regressions on inspection.
