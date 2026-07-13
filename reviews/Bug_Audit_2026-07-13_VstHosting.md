# Bug Audit #3 — VST3 Hosting Architecture (for Sonnet to fix)

> Audit of the codebase as of commit `eae2c2c` (hosted FabFilter launcher UI). The reported complaint — "VST does not work like that; the media cannot be played in the mixer; FX must work like FL Studio's mixer" — is correct, and the root cause is architectural, not cosmetic: **the plugin instances the user edits are not the instances that process audio.** Section A isolates that chain of defects; Section B is the rest of the audit. Severity-ordered. `Acapella_Build_Plan_v7.md` (written alongside this) is the plan that fixes A structurally; the items here are the precise defect list it must cover.

---

## A. Root causes: why hosted FX are disconnected from what you hear

### A1. Three separate MixEngines = three separate plugin-instance caches **[the core defect]**
**Files:** `MainWindow.xaml.cs:40` (`_mixEngine = new MixEngine(HostedAvailability)`), `PreviewPlaybackEngine.cs:150` (`_mixEngine = new MixEngine(hostedPluginAvailability)`), `ExportEngine.cs:28` (`_mixEngine = new MixEngine(hostedPluginAvailability)`)

`HostedPluginInstanceCache` lives **per MixEngine**, and there are three MixEngines. Sharing `HostedAvailability` (the commit's fix) only makes the three agree on *which* plugins exist; each still creates its **own** Pro-Q/Pro-G/etc. instances:

- The Mixing screen's "Open Pro-Q 4…" button (`LayerRowViewModel.SharedMixEngine` = MainWindow's engine) opens an editor on **instance #1** — which never processes a single sample.
- Preview playback processes audio through **instance #2** inside PreviewPlaybackEngine's engine.
- Export processes through **instance #3**, initialized only from the (stale, see A2) DTO state.

So: tweaking the plugin editor is inaudible, playback ignores the user's FX settings, and export matches neither. The launcher's doc comment ("Edits made there are audible immediately since it's the live instance") describes the intent, not the code — `MixEngine`'s shared *string constants* were mistaken for a shared *cache*.

**Fix (structural):** exactly one hosted-instance cache per app session. Either inject a single shared `HostedPluginInstanceCache` (plus one MixEngine) into PreviewPlaybackEngine and ExportEngine, or extract an `IHostedPluginService` owning cache+availability that all three consume. Export must read the **live** instances' current state (see A2) rather than instantiating a parallel set — or, if separate export instances are kept for isolation, they must be seeded with `GetState()` pulled from the live instances at export time, not from the DTO.

### A2. Plugin state is captured once, *before* the user edits — edits are never persisted anywhere
**File:** `LayerRowViewModel.cs:263+` (`OpenHostedEditor`: "storeState is called once eagerly right after opening")

The only writeback of hosted state into `LayerMixParameters.XHostedState` happens immediately after opening the editor — i.e. it saves the **pre-edit** state. There is no polling (P3a task 9 unbuilt), no capture on editor close, no capture on project save. Consequently: Save writes stale blobs, Export (A1, instance #3) processes with stale blobs, Undo snapshots (DTO-based) never see plugin edits, and reopening a project reverts every plugin tweak. Even without A1, this alone breaks "what you hear is what you get".

**Fix:** pull `GetState()` from live instances at the moments that matter — project save/ToDto, export snapshot, editor-window close, and (task 9) a debounce-timer poll comparing a cheap state hash to mark the project dirty. Requires an `aca_get_state`-based single-shot API that is safe against concurrent size changes (see B7).

### A3. Plugin lifecycle calls run on the wrong threads, violating the bridge's own contract
**Files:** `HostBridge.cpp:130-141` + `HostedPluginInstance.cs:76-80` (contract: all lifecycle/editor calls on the `aca_initialize` thread, only `ProcessBlock` may cross); `MixEngine.BuildLayerChain` (runs on PreviewPlaybackEngine's command thread); `ExportEngine.Export` (runs in `Task.Run`); `HostedPluginAvailability` (lazy scan on whoever queries first)

`BuildLayerChain → ApplyStage → GetOrCreateHostedInstance → aca_create_instance` executes on the preview engine's background command thread; export creates instances on a threadpool thread; the lazy availability scan (`findAllTypesForFile`, which loads each plugin binary) fires on whichever thread first calls `IsAvailable` — usually also the command thread. JUCE's VST3 hosting expects module scanning, instantiation, editor creation, and destruction on the message thread; off-thread use is exactly the class of instability already chased this session (the CRT heap-assertion hang, commit `10efeb0`). It "works" until it doesn't — intermittent crashes/hangs on Play are expected with this layout, consistent with "media cannot be played".

**Fix:** marshal all lifecycle operations (scan, create, get/set state, editor open/close, release) to the WPF dispatcher inside the shared hosting service from A1; chain building either pre-creates instances via the dispatcher before handing the chain to the audio thread, or blocks on a dispatcher-invoked creation. `ProcessBlock` stays on the audio thread (correct today). Run the availability scan once at startup, async, on the dispatcher, with a "scanning plugins…" status.

### A4. Hosted gate/EQ are always in the chain, with whatever the plugin's default state does
**File:** `MixEngine.cs:122-135` (`NoiseGateStage`/`EqStage` applied unconditionally; only compressor/limiter/reverb have `Enabled` flags)

With FabFilter detected, **every layer is always processed through Pro-G and Pro-Q 4**, even if the user never opened them. Pro-Q's default is transparent-ish; Pro-G's default gate curve is *not* — layers get audibly gated by default, and 4 layers × always-on stages means up to a dozen live plugin instances created on first Play (each ~100+ ms to instantiate, on the wrong thread per A3). This also contradicts the FL Studio model the requirements name: an FL insert slot is **empty until you add something, and bypassable after**. The native path had the same "always in chain" shape, but native defaults were engineered to be transparent; plugin defaults aren't yours to control.

**Fix:** per-stage `Enabled` flags for gate and EQ too (all five stages default **off**; the chain builds only enabled stages). This kills the instance explosion, the unrequested processing, and matches the DAW mental model. (v7 plan formalizes this as the FX-rack model.)

### A5. Reused plugin instances are never reset between plays — stale lookahead audio bleeds into the next run
**Files:** `HostedPluginInstanceCache` (instances live across rebuilds by design), `HostBridge.cpp` (no reset export), `HostedPluginSampleProvider` + `LatencySkipSampleProvider` (assume a cold-start instance)

The latency model assumes each playback starts with an empty plugin pipeline: the provider flushes `LatencySamples` of silence at the end, `LatencySkipSampleProvider` trims `LatencySamples` off the front. On the *second* and subsequent plays of a cached instance, its internal lookahead buffer still holds the previous run's tail: the first samples out are stale audio from last time, and the skip then trims that stale audio *plus* real new audio misaligned by whatever the plugin had buffered. Audible glitch at every replay/seek with a latency-reporting plugin (Pro-L 2 always; Pro-Q 4 in linear phase).

**Fix:** add `aca_reset(handle)` calling `AudioProcessor::reset()` (and/or `releaseResources()`+`prepareToPlay` re-arm) to the bridge; the chain builder calls it on every cached instance it wires into a fresh chain. Marshal per A3.

### A6. The Mixing screen has no playback (the literal "media cannot be played in mixer")
v5 P3 task "in-screen transport" was never built; the current Mixing screen is controls-only. Combined with A1 (edits inaudible anyway), FX adjustment is completely blind. The v7 plan makes the mixer screen playback-first (FL model: the mixer is something you use *while* the track plays); interim fix is simply mounting the existing transport controls on the Mixing screen bound to the same (single, per A1) engine.

---

## B. Remaining findings

### HIGH

**B1. Export instantiates its own plugin set per export, on a background thread, from stale state.** Follows from A1/A2/A3 but worth its own line because the fix differs: export should reuse the shared service (dispatcher-created instances), seed from live state, and dispose only what it created. Note `ExportEngine` is `IDisposable` — verify MainWindow actually disposes it per export (it constructed `new ExportEngine()` inline historically; a leaked engine now leaks native plugin instances, not just memory).

**B2. First-Play stall: lazy plugin scan + up-to-12 instance creations happen inside Play.** `HostedPluginAvailability` loads all six plugin binaries on first query; `BuildLayerChain` then instantiates per (layer × enabled stage). All of it currently sits between the user pressing Play and audio starting. With A4's default-off slots and A3's startup scan this mostly dissolves; keep a `[auto]` budget test (Play start < 1s warm) to hold the line.

**B3. Git repository metadata is corrupted.** `git fsck` reports `improper chunk offset(s) 47c and 37e4` / `multi-pack-index file exists, but failed to parse`. History and worktree appear intact (log/status work), but pack lookups may degrade and some operations can fail oddly. Fix (safe, non-destructive): delete `.git/objects/pack/multi-pack-index` and run `git repack -ad && git multi-pack-index write` (or just leave MIDX off). Do this before the next big commit burst; verify with `git fsck` after. Pause Rule 1 note: this touches only derived index files, not objects — no history rewrite.

### MEDIUM

**B4. `PluginEditorWindow.closeButtonPressed` captures a raw owner pointer into a `callAsync` lambda** (`HostBridge.cpp:90-94`): if `aca_release_instance` runs before the queued lambda (close editor, then delete the layer in the same UI beat), `ownerCopy` dangles → use-after-free. Fix: capture a weak/generation token or have `aca_release_instance` flush pending MessageManager callbacks before `delete h` (e.g. keep a `std::shared_ptr` control block for the owner).

**B5. `aca_get_state_size` + `aca_get_state` is a TOCTOU pair** (`HostBridge.cpp:270-288`): state can change between the two calls (editor tweak on the UI thread while save runs), size mismatch then returns 0 and the caller silently writes an empty blob (`HostedPluginInstance.GetState` returns `Array.Empty`). Fix: single call that allocates/fills in one native round-trip (caller passes buffer + gets required size back; retry loop in C#), and treat "empty state" as an error to surface, not a value to persist.

**B6. `aca_set_parameter_value` uses `setValue`** (`HostBridge.cpp:240`): JUCE requires `setValueNotifyingHost` (with begin/end change gesture) for the plugin's editor and internal processor to observe the change coherently. Currently unused by the UI, but it's a trap for the state-polling/automation work — fix before task 9 relies on it.

**B7. `HostedPluginInstanceCache.GetOrCreate` state semantics vs. project load:** state applies only at first creation; loading a *different* project (or Undo restoring older state) while instances are alive leaves the old audio state live with no code path pushing the new blobs. Fix alongside A2: project load / undo-restore explicitly `SetState` on live instances (via the dispatcher), not just on the DTO.

**B8. `aca_scan_plugin`/`aca_create_instance` call `juceInit()` themselves** — if any code path reaches them before `aca_initialize()` runs on the UI thread (e.g. the lazy availability scan from a background thread, per A3), JUCE's MessageManager binds permanently to that background thread and every *subsequent correct* UI-thread call is then on the "wrong" thread. Fix: make `aca_initialize` mandatory (bridge returns an error state if not initialised) instead of silently self-initialising.

### LOW

- **B9.** Editor `DocumentWindow`s are unowned top-level windows: they sit in the taskbar, don't minimize with the app, and stay open when the layer is deleted (until `Release`). Acceptable for now; v7's mixer work should close editors on layer removal and bring-to-front on relaunch (currently a no-op comment).
- **B10.** `HostedPluginSampleProvider` mono mode feeds L to both plugin channels and keeps only L out: correct for the pre-pan stages, but any plugin doing stereo decorrelation loses information silently. Fine for Pro-G/Pro-C/Pro-Q as used; document the constraint where the stage list is defined.
- **B11.** `MaxReverbTailSeconds = 12` clamp: fine, but the tail extension is computed from `TailSeconds` at chain-build time; a user lengthening decay in the Pro-R editor mid-session won't extend the layer duration until the next rebuild. Note in the mixer work, not urgent.
- **B12.** 103/103 tests green is *consistent with all of Section A being broken*: every MixEngine test uses `NoHostedPluginsAvailable`, and no test asserts that the editor-visible instance and the audio-path instance are the same object, or that save/export capture live state. Add: (i) identity test — `GetOrCreateHostedInstance(layer, stage)` from the "UI" and the instance used by a built chain must be reference-equal; (ii) state round-trip test — set plugin state via the service, save project, assert the DTO blob matches live `GetState()`; (iii) replay-reset test once `aca_reset` exists.

## Verified correct / no action

Bus-layout normalization to plain stereo (sidechain buses disabled) is the right call and correctly motivated; `prepareToPlay` at creation with a fixed max block and variable ≤max process sizes is valid VST3 usage; the latency flush/skip arithmetic is internally consistent *for a cold instance* (A5 is about reuse, not the math); state-size cap and ASCII buffer conventions in the P/Invoke layer are sound; `PositionTrackingSampleProvider` clock plumbing from the last audit's fixes is intact with hosted stages in the chain (latency skip happens inside the layer chain, upstream of the position tracker, so the A/V clock stays truthful).
