# Bug Audit #5 — Hosted plugin state survives File > New / File > Open and contaminates the next project

> Audit as of `ff0d6d1` (post the six-commit LayerTimeline/ProjectSession/FxSlot/IHostedPlugin refactor, 183 tests green). One bug, specced in full. Runners-up are listed at the end at one or two lines each, as candidates for a later pass — do not treat them as part of this ticket.
>
> **Confidence: high.** Every link in the chain below is read off the current source, not inferred, and the whole thing reproduces as a pure unit test through `FakeHostedPluginFactory` — no FabFilter install, no WPF, no native DLL. The one judgement call is *how* to fix it (release-on-project-replacement vs. session-unique layer ids); that choice is argued in §Proposed fix.

---

## The bug in one sentence

`LayerId` is a positional index that restarts at 0 for every project, and the shared `HostedPluginInstanceCache` is keyed on `(LayerId, Stage)` and is **never released except at app shutdown** — so after File > New or File > Open, the new project's layer 0 silently inherits the previous project's live Pro-Q/Pro-C/Pro-G/Pro-L/Pro-R instances, and the old project's plugin state gets pulled *into* the new project's parameters and written to its save file.

---

## Root cause

Three independently reasonable decisions compose into a defect.

### 1. Layer ids are positional and restart at 0 per project

**File:** `src/Acapella.Engine/Project/LayerModel.cs:67-81`

```csharp
public LayerModel Add(LayerKind kind, string sourcePath)
{
    ...
    var layer = new LayerModel
    {
        LayerId = _layers.Count,   // line 74
```

`LayerId` is the index in the collection, not a session identity. `ProjectSession.New()` empties the collection (`ProjectSession.cs:71-77`), so the very next `Add` produces `LayerId == 0` again. On the load path it is worse than "again": `ProjectPersistenceService.FromLayerDto` (`ProjectPersistenceService.cs:68-82`) restores `LayerId = dto.LayerId` verbatim, and since every project's layers were numbered 0..3 by the same positional rule, **any two projects collide on ids by construction**.

### 2. The hosted-instance cache is keyed on that id and is never released in production

**Files:** `src/Acapella.Engine/Host/HostedPluginInstanceCache.cs:13,25-34,43-47`, `src/Acapella.Engine/Host/HostedPluginService.cs:88-89,118,138`

```csharp
private readonly record struct Key(int LayerId, string Stage);   // Cache.cs:13
```

`Release(layerId, stage)` exists on both the cache and the service — and `grep -rn "Release(" src --include=*.cs` returns **only the two definitions**. Nothing in `src/Acapella.App` or `src/Acapella.Engine` ever calls it. The same is true of the persistent per-layer ARA sessions (`HostedPluginService.cs:138`, `_araLayerSessions`), whose own comment states the assumption out loud:

> "Never released mid-session: this project has no layer-removal feature yet … these follow the same whole-app-lifetime pattern as `_cache`."

That assumption is correct about *layer removal* and wrong about *project replacement*. `File > New` and `File > Open` remove every layer in the project; the code just doesn't call them removals.

### 3. The two state-sync helpers are asymmetric — one is gated on non-null, the other isn't

**File:** `src/Acapella.Engine/Mix/MixEngine.cs:99-122`

```csharp
public void SyncLiveStateIntoParameters(int layerId, LayerMixParameters parameters)   // 99
{
    foreach (var slot in FxSlots.All)
    {
        var instance = _hostedService.TryGetLiveInstance(layerId, slot.Stage);
        if (instance is not null)
            slot.SetHostedState(parameters, _hostedService.PullLiveState(instance));   // 105 — unconditional
    }
}

public void PushSavedStateIntoLiveInstances(int layerId, LayerMixParameters parameters)   // 113
{
    ...
        if (instance is not null && state is { Length: > 0 })                          // 119 — gated
            _hostedService.PushState(instance, state);
}
```

Push is (correctly, for its own purpose) a no-op when the restored state is null — there is no captured "factory default" blob to push, so it leaves the instance alone. Pull is unconditional. The result is a one-way valve: a live instance can write its state into whatever `LayerMixParameters` object it is asked about, but a layer that has no state of its own can never clear the instance. Cross a project boundary and that valve points the wrong way.

---

## Repro A — File > Open (silent data contamination of the opened project)

Preconditions: FabFilter Pro-Q 4 installed (confirmed present per `CLAUDE.md`). Project B is any project whose first layer never used a hosted EQ, i.e. `EqHostedStateBase64` is absent/null in its `.acapella.json`.

1. Launch the app. Open project A (or build it from scratch). Select layer 0, enable EQ, click **Open Pro-Q 4…** and drag a band somewhere obvious. The `(0, "Eq")` instance is now live in the shared cache with A's curve; the 500ms poll (`MainWindow.xaml.cs:114-123`) has recorded A's bytes in `LayerRowViewModel._lastPolledHostedState`.
2. **File > Open** project B.
3. `ProjectSession.Open` (`ProjectSession.cs:64-68`) runs:
   - `Restore(dto)` → `Layers.Restore(...)` installs B's layers; then for each layer `PushSavedStateIntoLiveInstances(0, B_params)` — B's `EqHostedState` is **null**, so the gate at `MixEngine.cs:119` skips it and A's instance keeps A's curve.
   - `_undoStack.Reset(Snapshot())` → `Snapshot()` (`ProjectSession.cs:37-42`) calls `SyncLiveStateIntoParameters(0, B_params)` → `TryGetLiveInstance(0,"Eq")` returns **A's instance** → `B_params.EqHostedState := A's Pro-Q curve`.

**Observable outcomes, all without the user touching anything:**

- B's in-memory layer 0 now carries A's Pro-Q state, and that is the **undo baseline** — undo cannot get rid of it.
- **File > Save** on B writes A's `EqHostedStateBase64` into B's project file. This is the data-loss/corruption face of the bug: B's file is permanently polluted with a plugin state from a different project.
- The moment the user enables EQ on B's layer 0 (or exports), the chain builds `GetOrCreateInstance(0, "Eq", …)` → cache hit → **B is processed through A's EQ curve**. `FxSlot.Insert` calls `hosted.Reset(instance)` (`FxSlot.cs:70`), which clears DSP buffers but not parameter state, so this is audible, not cosmetic.
- Within 500ms, `LayerRowViewModel.PollHostedStateChanges` (`LayerRowViewModel.cs:335-355`) — whose `_openedHostedSlots` set survives the project swap because only `Layer` is reassigned — pulls A's state again and reports a change, so MainWindow fires `PushUndoSnapshot()` for an edit the user never made.

**Trigger condition, stated precisely:** contamination fires only when the newly opened project's layer has **null/empty** hosted state for that stage while an instance for the same `(LayerId, Stage)` is live. If B's layer 0 *does* have EQ state, `PushSavedStateIntoLiveInstances` corrects the instance first and `Snapshot()` pulls B's own bytes back — no visible bug. A test written without this precondition will not reproduce.

## Repro B — File > New (wrong audio on a brand-new project)

1. Same step 1 as above: A's `(0, "Eq")` instance is live with a custom curve.
2. **File > New** (`MainWindow.xaml.cs:857-865` → `ProjectSession.New()`). Nothing releases the cache. `Snapshot()` over an empty layer set is harmless, so nothing is contaminated *yet*.
3. Add or record a layer. `LayerCollection.Add` assigns `LayerId = 0`.
4. Enable EQ on it. `BuildLayerChain` → `FxSlot.Insert` → `GetOrCreateInstance(0, "Eq", "FabFilter Pro-Q 4", initialState: null, …)`. `HostedPluginInstanceCache.GetOrCreate` (`Cache.cs:25-34`) is a `GetOrAdd`: **`initialState` is ignored on a cache hit by design** ("state is only ever applied once, at creation"). The brand-new project's first EQ therefore opens on the *previous* project's curve.
5. Save. `Snapshot()`'s `SyncLiveStateIntoParameters` bakes it in.

The same applies to the Melodyne path: `_araLayerSessions[0]` survives `New()`, so a fresh layer 0 with Manual2A reattaches to the previous project's ARA document controller (the audio source is re-registered because `ContentKey` differs, but the document, its edits and its `EditGeneration` are the old project's).

## Why the current suite misses it

`ProjectSessionTests.SaveThenOpen_RestoresTheProject_AndStartsFreshUndoHistory` (`tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs:106-127`) is *exactly* this scenario — open a project into a session that already has a layer — but it only asserts on scalar fields (`SourcePath`, `TrimStartMs`, BPM, master volume) and never creates a live hosted instance. `Undo_PushesTheRestoredPluginStateIntoTheStillLiveInstance` (`:88-103`) covers the in-project restore path, where the state being restored is always non-null, so it never exercises the `Length: > 0` gate's failure mode. Nothing in the suite crosses a project boundary with a live instance.

---

## Proposed fix

**Chosen approach: release all hosted resources whenever the project is replaced.** The alternative — making `LayerId` globally unique (a session counter or Guid) — was considered and rejected for this ticket: it would change the persisted `LayerDto.LayerId` semantics, it leaves an unbounded pile of orphaned plugin instances alive for the session, and it does not by itself stop `SyncLiveStateIntoParameters` from reading a stale instance in any other id-reuse path. Releasing is smaller, matches the existing `Release`/`Dispose` machinery, and leaves the persistence format untouched. If session-unique ids are wanted later, do them as a separate change.

### 1. `src/Acapella.Engine/Host/HostedPluginService.cs` — add `ReleaseAll()`

Add a public method that tears down every cached hosted instance *and* every ARA layer session, exactly like `Dispose()` does today (`HostedPluginService.cs:217-223`) but leaving the service usable afterwards:

```csharp
/// <summary>Releases every cached hosted instance and every per-layer ARA session, leaving the
/// service usable for the next project. Called when the whole layer set is replaced (File > New,
/// File > Open): layer ids are positional and restart at 0 per project, so without this the next
/// project's layer 0 would inherit the previous project's live plugin instances and their state.</summary>
public void ReleaseAll() => _dispatcher.Invoke(() =>
{
    _cache.ReleaseAll();
    foreach (var entry in _araLayerSessions.Values)
        entry.Session.Dispose();
    _araLayerSessions.Clear();
});
```

Marshal through `_dispatcher.Invoke` — same rule as every other lifecycle call in this class; native release must run on the JUCE-initialized thread.

Also add `HostedPluginInstanceCache.ReleaseAll()` (`HostedPluginInstanceCache.cs`) that disposes and clears `_instances` without disposing the cache itself — i.e. the body of the current `Dispose()`; have `Dispose()` call it.

**Editor-window safety (checked, no extra work needed):** `aca_release_instance` (`src/Acapella.Host.Native/src/HostBridge.cpp:395-403`) does `h->editorWindow.reset()` before tearing down the processor, and `aca_ara_destroy_session` (`src/Acapella.Host.Native/src/AraBridge.cpp:753-760`) does the same. Releasing while a Pro-Q or Melodyne editor is open closes that window rather than orphaning it. Do **not** add a separate `CloseEditorWindow` pass — it would be redundant.

### 2. `src/Acapella.Engine/Mix/MixEngine.cs` — passthrough

`ProjectSession` holds only a `MixEngine`, not the `HostedPluginService`. Add:

```csharp
/// <summary>Releases every live hosted instance and ARA session — for a whole-project
/// replacement (see ProjectSession.New/Open).</summary>
public void ReleaseAllHostedInstances() => _hostedService.ReleaseAll();
```

### 3. `src/Acapella.Engine/Project/ProjectSession.cs` — call it on project replacement, **before** restoring

This ordering is the load-bearing part of the fix.

```csharp
public void Open(string filePath)
{
    _mixEngine.ReleaseAllHostedInstances();   // <-- FIRST, before Restore
    Restore(_persistence.LoadFromFile(filePath));
    _undoStack.Reset(Snapshot());
}

public void New()
{
    _mixEngine.ReleaseAllHostedInstances();   // <-- FIRST
    Layers.Restore(Enumerable.Empty<LayerModel>());
    LatencyOffsetMsUsed = null;
    MasterVolumeDb = 0f;
    _undoStack.Reset(Snapshot());
}
```

With the cache empty at that point: `PushSavedStateIntoLiveInstances` finds no instance and is a no-op; `Snapshot()`'s `SyncLiveStateIntoParameters` finds no instance and leaves the loaded parameters exactly as the file had them; and the first chain build creates a fresh instance seeded from the new project's own `initialState`. That is the whole correctness argument — put it in the doc comment.

Do **not** add the call to `Undo()`/`Redo()`. Within one project the instance identity is correct and `PushSavedStateIntoLiveInstances` handles it; releasing there would close the user's open editor windows on every undo.

### 4. `src/Acapella.App/ViewModels/LayerRowViewModel.cs` — clear the per-row poll tracking when `Layer` is swapped

After fix 1-3 the state contamination is gone, but one artefact remains: `_openedHostedSlots` and `_lastPolledHostedState` (`LayerRowViewModel.cs:38-39`) are per-row and rows outlive a project swap — only `Layer` is reassigned (`:73-89`). Once the chain builds a *fresh* `(0, "Eq")` instance for the new project, `PollHostedStateChanges` still lists Eq as "opened" and still holds the old project's bytes as `previous`, so the first poll sees a diff and fires `PushUndoSnapshot()` for an edit nobody made. The state it writes is correct, so this is a spurious undo entry rather than corruption — but fix it in the same pass.

In the `Layer` setter, before the `OnPropertyChanged` calls:

```csharp
_openedHostedSlots.Clear();
_lastPolledHostedState.Clear();
```

(Clearing on every `Layer` assignment is right: a row whose layer is replaced has no claim on the old layer's editors either.)

### 5. `src/Acapella.App/MainWindow.xaml.cs` — stop playback before replacing the project (MANDATORY; skipping this ships a crash)

Today nothing ever releases a hosted instance mid-session, which is exactly why `HostedPluginSampleProvider`'s doc comment can say "Does not own the wrapped instance … and never disposes it". Fix 1-3 breaks that standing assumption, so the callers must be sequenced or the fix introduces a use-after-free in native code:

- Preview is playing. `MixEngine.BuildLayerChain` has wired `HostedPluginSampleProvider` objects that hold raw `IHostedPlugin` references, and the WASAPI render thread is calling `ProcessBlock` on them (`ProcessBlock` is by design the one call that does **not** marshal to the dispatcher).
- The user hits File > Open. `OpenProjectButton_Click` (`MainWindow.xaml.cs:800-818`) → `ApplyRestore` → `_session.Open()` → `ReleaseAllHostedInstances()` on the UI thread → `aca_release_instance` → `delete h`.
- `RefreshPreviewLive()` only runs *after* the restore, and goes through the preview command queue. **Nothing stopped playback.** The render thread is now calling into freed native memory.

The same shape applies to File > New during an active export: `ExportButton_Click` (`:820-853`) runs `exportEngine.Export` on `Task.Run` against the *shared* `_hostedService`.

`ProjectSession` cannot solve this itself — it has no handle on the preview engine or on an in-flight export. The ordering constraint belongs to the caller:

- Make `NewProjectMenuItem_Click` and `OpenProjectButton_Click` `async void`, and `await AwaitPreviewCommand(_previewEngine.StopAsync())` **before** calling `_session.New()` / `_session.Open()` — the same shape `StopButton_Click` (`:708-718`) already uses.
- Gate both on an export not being in flight. `ExportMenuItem.IsEnabled` is already used as the in-flight flag (`:832`, `:850`); either reuse it (`if (!ExportMenuItem.IsEnabled) { StatusText.Text = "Finish the export first."; return; }`) or add an explicit `_exporting` bool. Either is fine; just do one.

### 6. `MixEngine._melodyneBackends` — clear it too

`MixEngine.cs:31`'s `Dictionary<int, IPitchCorrectionBackend> _melodyneBackends` is also keyed by `layerId`. A stale `MelodyneAraPitchCorrector` would functionally recover on its own (it re-acquires the session through `GetOrCreateAraLayerSource`, which recreates it after `ReleaseAll()`), so this is not a second bug — but leaving a corrector bound to a destroyed session hanging around is needless. Clear it inside `ReleaseAllHostedInstances()`:

```csharp
public void ReleaseAllHostedInstances()
{
    _melodyneBackends.Clear();
    _layerTaps.Clear();          // stale meter taps from the previous project's layer ids
    _hostedService.ReleaseAll();
}
```

### 7. Tests — `tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs`

All three run against the existing `FakeHostedPluginFactory` fixture already in that class (`_fakes` / `_service` / `_mixEngine`, `ProjectSessionTests.cs:11-30`), which uses the default `InlineHostedPluginDispatcher` — no native DLL, no WPF, no installed plugins.

**Test 1 — `Open_DoesNotLeakThePreviousProjectsPluginStateIntoTheOpenedProject`** (this is the regression that must go red before the fix). Model it on `SaveThenOpen_RestoresTheProject_AndStartsFreshUndoHistory:106`:

```
// project B: saved with NO hosted EQ state
var saved = new ProjectSession(_mixEngine);
saved.Layers.Add(LayerKind.UploadedVideo, "clip.mp4");
string path = TempProjectPath();
saved.Save(path);

// project A: live in the session, layer 0, EQ tweaked in its editor
var session = new ProjectSession(_mixEngine);
var layerA = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
OpenEqEditorLiveInstance(layerA).TweakInEditor(new byte[] { 9, 9, 9 });
session.CommitEdit();

session.Open(path);

Assert.Null(session.Layers.Layers.Single().MixParameters.EqHostedState);
```

**The statement order in that snippet is load-bearing — do not reorder it.** Project B must be built and saved *before* project A's live instance exists, because `saved.Save(path)` itself calls `Snapshot()` → `SyncLiveStateIntoParameters(0, …)`. If A's instance is already live at that point, B's file is written with A's state and the final assertion becomes a tautology that passes both before and after the fix.

Today that assertion fails with `{9,9,9}`. Add a second assertion on the same test (or a sibling) that the *saved* file is clean too: `session.Save(path2)` then reload via a second `ProjectSession` and assert `EqHostedState` is still null.

**Test 2 — `New_ThenAddLayer_GetsAFreshPluginInstance`**:

```
var session = new ProjectSession(_mixEngine);
var layerA = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
var pluginA = OpenEqEditorLiveInstance(layerA);
pluginA.TweakInEditor(new byte[] { 7 });

session.New();
var layerB = session.Layers.Add(LayerKind.UploadedAudioOnly, "b.wav");
var pluginB = OpenEqEditorLiveInstance(layerB);

Assert.NotSame(pluginA, pluginB);
Assert.True(pluginA.Disposed);          // FakeHostedPlugin.Disposed, FakeHostedPlugin.cs:27
Assert.Empty(pluginB.State);
```

Today `pluginB` *is* `pluginA` and carries `{7}`.

**Test 3 — `Undo_StillReusesTheSameLiveInstance`** (guards against over-fixing by releasing on undo): take the existing `Undo_PushesTheRestoredPluginStateIntoTheStillLiveInstance:88` and add `Assert.False(plugin.Disposed);` plus `Assert.Same(plugin, OpenEqEditorLiveInstance(layer));` after the undo.

**Regression carve-out (per `CLAUDE.md`'s testing rules):** this touches `MixEngine`, `HostedPluginService` and `ProjectSession`, so re-run `tests/Acapella.Engine.Tests/Host/*`, `Mix/HostedChainTests.cs`, `Mix/HostedFxChainTests.cs`, `Mix/FxSlotTests.cs` and `Persistence/*` once after the change.

---

## Runners-up (not part of this ticket)

- **Spurious `PlaybackStopped` on every seek/refresh during playback.** `FrameLoop` exits on `_stopRequested` and then unconditionally calls `StopInternal(raiseStoppedEvent: true)` (`src/Acapella.Engine/Preview/PreviewPlaybackEngine.cs:257-283`), but `SeekCore` reaches that path via `StopCore` before immediately calling `PlayCore` again (`:367-385`). So arrow-key seek, timeline scrub, the layer-labels toggle and any debounced live refresh all fire `PlaybackStopped` mid-playback, and `MainWindow.xaml.cs:160` flips the transport button to "Play" while audio keeps playing. Fix: have `FrameLoop` raise the event only when it exited because `PositionMs >= DurationMs`, not because `_stopRequested` was set.
- **~2s hang on window close during playback.** Same code path: the frame-loop thread raises `PlaybackStopped` through a blocking `Dispatcher.Invoke` (`MainWindow.xaml.cs:160`) while the UI thread is blocked inside `MainWindow.Closing` → `PreviewPlaybackEngine.Dispose()` → `StopAsync().GetAwaiter().GetResult()` (`PreviewPlaybackEngine.cs:399`), whose `StopCore` is sitting in `_frameLoopThread?.Join(2000)` (`:337`). It resolves when the join times out, so it's a two-second freeze rather than a deadlock — and it disappears for free if the runner-up above is fixed.
