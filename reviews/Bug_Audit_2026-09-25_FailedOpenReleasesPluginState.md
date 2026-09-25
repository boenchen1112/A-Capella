# Bug Audit #16: A failed File > Open destroys the current project's live FabFilter and Melodyne state, including every Melodyne edit

> **Promoted from Bug Audit #15's Runners-up** (`Bug_Audit_2026-09-25_WindowCloseAbandonsExport.md:321-325`), after re-tracing it from the code and running a harness. The other traced lead from that list, "a non-project `*.json` opens as an empty project and Ctrl+S overwrites it", also holds up. It has a different root cause and a different fix, and its natural fix is only safe once this one lands. It stays in Runners-up below, with its verification notes.
>
> No doc in `reviews/` specs this. Bug Audit #5 (`Bug_Audit_2026-09-23_ProjectSwitchPluginState.md`) introduced the release-first ordering and only reasoned about Opens that succeed. The Save-affordances spec's failed-Open note (`Feature_Spec_2026-09-23_SaveAffordances.md:177`) checked only that path and dirty state stay untouched.
>
> Audit as of `155a829` (Bug Audit #15 landed, including its regression tests).
>
> **Confidence: high.**
> - **Measured.** A scratch harness (Repro D) drives the real `ProjectSession`/`MixEngine`/`HostedPluginService` with the suite's `FakeHostedPlugin`. With files that fail to open in nine different ways, the current project's live instance is disposed every time at HEAD. With the proposed `Open` it survives every time, and a valid file still releases it.
> - **Melodyne half: reasoned from code.** `HostedPluginService.ReleaseAll` disposes ARA sessions in the same dispatcher call as the plugin cache (`HostedPluginService.cs:222-228`). So the fake-instance measurement stands in for both. What the user then hears (§Root cause 3) was not run against a real Melodyne.

---

## The bug in one sentence

`ProjectSession.Open` releases every live hosted-plugin instance and every Melodyne ARA session (`ProjectSession.cs:137`) *before* it reads or parses the chosen file (`:138`). So a File > Open that then fails leaves the user on their current project, with "Open failed: ..." in the status bar, but that project's FabFilter editors have closed, and any tweak not yet copied into the layer's parameters is gone. Every Melodyne note edit on every layer is gone too, permanently. The failure can be a corrupt, truncated, empty or foreign `*.json`, or a project with a bad layer entry. The layer still shows Melodyne enabled, so the next Play or export silently renders the unedited vocal.

---

## Current behavior

| Location | What it does |
|---|---|
| `MainWindow.xaml.cs:1101-1134` | `OpenProjectButton_Click`. This is the only caller of `_session.Open` in the app (grep `\.Open(` in `src/Acapella.App`: `:1125` only). |
| `MainWindow.xaml.cs:1109` | `ConfirmDiscardUnsavedChanges()` runs before the file dialog. It prompts only if `IsDirty` (`:1079`). A clean project gets no prompt at all. |
| `MainWindow.xaml.cs:1111` | `OpenFileDialog { Filter = "Acapella project\|*.acapella.json;*.json" }`. Any `*.json` is offered. |
| `MainWindow.xaml.cs:1121` | Stops preview playback (Bug Audit #5 §5). |
| `MainWindow.xaml.cs:1123-1127` | `ApplyRestore(() => { _session.Open(dialog.FileName); return true; })`. If `Open` throws, `ApplyRestore` (`:214-245`) never reaches `RestoreTracksFromLayers`, so the sidebar rows, their `Layer` references and the FX panel all stay exactly as they were. |
| `MainWindow.xaml.cs:1130-1133` | `catch` → `StatusText.Text = $"Open failed: {ex.Message}"`. This is the only feedback. |
| `ProjectSession.cs:135-141` | `Open`: `ReleaseAllHostedInstances()` (`:137`), **then** `Restore(_persistence.LoadFromFile(filePath))` (`:138`). |
| `ProjectSession.cs:157-170` | `Restore`: `FromDto` (`:161`), then `Layers.Restore` (`:162`), the three setters, and `PushSavedStateIntoLiveInstances` (`:167-168`). |
| `ProjectPersistenceService.cs:46-51` | `LoadFromFile`: `File.ReadAllText`, then `JsonSerializer.Deserialize<ProjectFileDto>`. Throws on I/O errors, on malformed JSON, and on a literal `null`. |
| `ProjectPersistenceService.cs:29-34`, `:68-85`, `:118-144` | `FromDto`: `Enum.Parse<LayerKind>` (`:73`), `Enum.Parse<PitchBackendSelection>` (`:137`), `Convert.FromBase64String` (`:116`), and `LayerCollection.Restore`'s 4-layer cap (`LayerModel.cs:103-104`). Each can throw on a well-formed JSON file. |
| `MixEngine.cs:130-135` | `ReleaseAllHostedInstances`: clears `_melodyneBackends` and `_layerTaps`, then `_hostedService.ReleaseAll()`. |
| `HostedPluginService.cs:222-228` | `ReleaseAll`: disposes every cached FabFilter instance (→ `aca_release_instance`, which closes its editor window first, `HostBridge.cpp:395-403`) and every per-layer `AraHostSession` (→ `aca_ara_destroy_session`, `AraBridge.cpp:753-760`). |
| `HostedPluginService.cs:214-215` | `GetAraEditGeneration` returns `0` for a layer with no session. |
| `MainWindow.xaml.cs:121-130` | The 500 ms editor poll only polls `_selectedLayer` (`:124`). This is deliberate (`Bug_Audit_2026-09-24_UndoDropsOpenEditorTracking.md:272`). |

---

## Root cause

Four facts combine to produce the defect.

### 1. `Open` releases first, and the read and parse can still fail after that

Bug Audit #5 placed `ReleaseAllHostedInstances()` on the first line of `Open` (`ProjectSession.cs:137`). Its correctness argument (`Bug_Audit_2026-09-23_ProjectSwitchPluginState.md:170`) is about what happens *after* the cache is empty:
- `PushSavedStateIntoLiveInstances` finds no instance to (not) push into.
- `Snapshot()`'s `SyncLiveStateIntoParameters` finds no instance to pull stale state from.
- The first chain build creates a fresh instance seeded from the opened project's own state.

All three are steps that run after the file has been loaded. Nothing in that argument needs the release to come before reading the file. It only needs the release to come before the opened project's layers are installed, pushed into, or snapshotted. The Save-affordances spec froze the line in place ("**Do not** move `ReleaseAllHostedInstances()`", `Feature_Spec_2026-09-23_SaveAffordances.md:174`). Three lines later it noted that a failed `Open` throws out of `LoadFromFile` or `FromDto` (`:177`), but it checked only path and dirty state, not the release that had already happened.

Every one of these failure modes throws after the release at HEAD. Repro D measured them, except two: the missing-file row is covered by the existing `FailedOpen_LeavesPathAndDirtyUntouched`, and locked / access denied are reasoned from `File.ReadAllText`.

| File content | Throws from | Exception |
|---|---|---|
| missing / locked / access denied | `LoadFromFile` (`File.ReadAllText`) | `FileNotFoundException` / `IOException` / `UnauthorizedAccessException` |
| truncated JSON, empty file, JSON array | `LoadFromFile` (`Deserialize`) | `JsonException` |
| literal `null` | `LoadFromFile` (`?? throw`) | `InvalidDataException` |
| unknown layer `Kind` | `FromDto` (`:73`) | `ArgumentException` |
| `"Layers": null` | `FromDto` (`:32`) | `ArgumentNullException` |
| `"MixParameters": null` | `FromDto` (`:120`) | `NullReferenceException` |
| bad hosted-state base64 | `FromDto` (`:116`) | `FormatException` |
| five layers | `FromDto` → `LayerCollection.Restore` (`LayerModel.cs:104`) | `InvalidOperationException` |

### 2. The failure leaves the old project in place, so the loss is invisible except where it hurts

`FromDto` builds a separate `LayerCollection` (`ProjectPersistenceService.cs:31-32`), and `Layers.Restore` (`ProjectSession.cs:162`) only runs after it succeeds. So every failure above leaves `session.Layers`, `CurrentFilePath` and `IsDirty` untouched (the last two are pinned by `FailedOpen_LeavesPathAndDirtyUntouched`). On the App side, `ApplyRestore` skips its UI rebuild. The user is back in their project and every row, slider and checkbox looks the same. The only things that changed are the ones they cannot see until later: the live plugin instances and ARA sessions.

### 3. What is actually lost

**Melodyne: all edits on every layer, unrecoverably.**
- An ARA session's edits exist only inside the live session. `AraHostSession.ExportState`/`ImportState` have no production caller, and `AraArchiveKey` is never assigned (grep: only `LayerModel.cs:71` sets it, to `null`). This is the known gap from `Bug_Audit_2026-07-16_AraMelodyne.md` A5. So nothing, whether the project file, the undo stack or the parameters, can bring the edits back.
- After the release, the layer's `PitchBackend` is still `Manual2A`, so the Melodyne checkbox still shows enabled. `GetAraEditGeneration` returns `0` (`HostedPluginService.cs:214-215`), so the next chain build asks `PitchCorrectionCache` for `…-araEdit0` (`MixEngine.cs:200-204`).
- That key is either still cached from before the first edit (the cache keeps 2 keys per layer, `PitchCorrectionCache.cs:23`), which replays the pre-edit render, or it is evicted, in which case a brand-new session with no edits renders the vocal as-is. Either way, the next Play and the next export contain the **unedited** vocal, with no error and nothing in the UI that has changed.
- "Edit in Melodyne..." now reports "Play this layer once with Melodyne manual selected before editing." (`MainWindow.xaml.cs:856`). After a Play, it opens a fresh analysis.
- A dirty-project "Yes, save" before the dialog does not help, because the save does not include Melodyne edits either.

**FabFilter: every tweak not yet copied into `LayerMixParameters`.**
- The release deletes each instance and closes its open editor window (`HostBridge.cpp:400`). The next chain build creates a fresh instance from the layer's `*HostedState` bytes.
- Those bytes are only as recent as the last copy. The poll copies the **selected** layer's editors every 500 ms (`MainWindow.xaml.cs:124`, `LayerRowViewModel.cs:353-373`), and any `Snapshot()` (every `CommitEdit`, Save, Export) copies all layers (`ProjectSession.cs:83-84`).
- Lost: a tweak in the editor of a layer that is not the selected strip, made since the last commit. That is the normal workflow with two plugin windows open side by side. Also any tweak in the last <500 ms.
- A poll-only tweak on a non-selected layer never marks the project dirty. So on a clean project, `ConfirmDiscardUnsavedChanges` doesn't prompt, and nothing warns the user before the Open.

### 4. Failed Opens are easy to trigger

- The dialog offers every `*.json` (`MainWindow.xaml.cs:1111`), and a project folder commonly sits next to other tools' JSON.
- A 0-byte or truncated project is the exact result of the non-atomic save already tracked in #15's Runners-up (`ProjectPersistenceService.cs:43`), and of a cloud-sync placeholder.
- A file written by a later build with a new `LayerKind` or `PitchBackendSelection` value fails in `FromDto`.
- A file another program holds without sharing fails in `ReadAllText`.

None of these is exotic, and the user's reasonable mental model is "the file didn't open, so nothing happened".

---

## Repros

### A. Failed Open wipes Melodyne edits **[human]**

Preconditions: an ARA-capable Melodyne (installed, per `CLAUDE.md`), and a file `broken.acapella.json` in the project folder containing just `{` (Notepad: type `{`, save).

1. Add a sung layer and tick its Melodyne checkbox. Press Play once so the ARA session exists.
2. Click Melodyne's name to open the editor. Drag one note clearly up a semitone. Play, and confirm the change is audible.
3. Ctrl+S, so the project is clean.
4. File > Open..., choose `broken.acapella.json`. The status bar shows `Open failed: ...`. No prompt appeared, because the project was clean.
5. **Bug:** the Melodyne editor window has vanished. Press Play: the note is back at its original pitch. Clicking Melodyne's name shows "Play this layer once with Melodyne manual selected before editing.". After a Play, the editor opens a fresh analysis with no edits. Export the MP4: it has the unedited vocal. The edit cannot be recovered in any way.

### B. Failed Open reverts a non-selected layer's FabFilter tweak **[human]**

1. Two layers. Select layer 1's strip and click `EQ` to open Pro-Q 4. Select layer 2's strip. Layer 1's Pro-Q window stays open.
2. In layer 1's Pro-Q window, add a deep, obvious notch. Wait a few seconds. The project stays clean (the title shows no •), because the poll only watches layer 2.
3. File > Open..., choose `broken.acapella.json`.
4. **Bug:** layer 1's Pro-Q window closes. Select layer 1 and reopen `EQ`: the notch is gone. The curve is whatever was last captured into the layer's parameters.

### C. Headless: a failed Open releases the live instance **[auto]**

`FailedOpen_KeepsTheCurrentProjectsLivePluginInstances` (§Tests) is a nine-case `[Theory]`, one case per failing file from §Root cause 1. Each creates a live fake EQ instance, calls `Open` on the bad file, and asserts the instance is not disposed. Today every case fails on `Assert.False(plugin.Disposed, ...)`.

### D. Harness, HEAD vs. proposed `Open` **[auto-able, run once by hand for this audit, not added to the suite]**

This is a throwaway console project in `%TEMP%\aca16`. It references the built `Acapella.Engine.dll` and `Acapella.Engine.Tests.dll` (for `FakeHostedPluginFactory`), plus a copy of `ProjectSession.cs` renamed `FixedProjectSession` with exactly the §Proposed fix applied. For each file it builds one layer, creates a live EQ instance, tweaks it (live only), calls `Open`, and reports `Disposed`:

```
HEAD  truncated        THREW JsonException                  oldDisposed=True
HEAD  empty file       THREW JsonException                  oldDisposed=True
HEAD  json array       THREW JsonException                  oldDisposed=True
HEAD  literal null     THREW InvalidDataException           oldDisposed=True
HEAD  bad Kind         THREW ArgumentException              oldDisposed=True
HEAD  Layers null      THREW ArgumentNullException          oldDisposed=True
HEAD  MixParams null   THREW NullReferenceException         oldDisposed=True
HEAD  bad base64       THREW FormatException                oldDisposed=True
HEAD  5 layers         THREW InvalidOperationException      oldDisposed=True
HEAD  valid project    OPEN OK layers=1                     oldDisposed=True
FIXED truncated        THREW JsonException                  oldDisposed=False
FIXED empty file       THREW JsonException                  oldDisposed=False
FIXED json array       THREW JsonException                  oldDisposed=False
FIXED literal null     THREW InvalidDataException           oldDisposed=False
FIXED bad Kind         THREW ArgumentException              oldDisposed=False
FIXED Layers null      THREW ArgumentNullException          oldDisposed=False
FIXED MixParams null   THREW NullReferenceException         oldDisposed=False
FIXED bad base64       THREW FormatException                oldDisposed=False
FIXED 5 layers         THREW InvalidOperationException      oldDisposed=False
FIXED valid project    OPEN OK layers=1                     oldDisposed=True
undo=True gain=0 mv=0 redo=True gain=-6
```

The last line checks that Undo/Redo, which now goes through the refactored `Restore` → `Apply`, still restores. It is recorded as evidence. The §Tests section carries the suite version.

---

## Why the suite misses it

- **`FailedOpen_LeavesPathAndDirtyUntouched` goes through a failed Open but has nothing to observe.** It never creates a live instance first, and it asserts only on path and dirty state, following the Save-affordances spec's framing (`:177`). So the release runs on every test run and is never checked. This is the same "exercised, never asserted" shape as #15.
- **Every plugin-lifecycle test around `Open` uses a file that opens.** `Open_DoesNotLeakThePreviousProjectsPluginStateIntoTheOpenedProject` pins Bug Audit #5's guarantee. That guarantee is correct for a successful Open, and it is the reason the release was written as "first line" instead of "before the layers are replaced".
- **The App path is unreachable in `[StaFact]`s.** `OpenProjectButton_Click` shows a modal `OpenFileDialog` (`:1112`), so no App test drives it, and the defect is entirely inside `ProjectSession.Open` anyway.
- **The loss leaves no trace an assertion would see after the fact.** The layers, parameters, path and dirty flag are all unchanged. Only the live instance's identity and state changed, and for Melodyne there is no persisted state to compare against.

---

## Proposed fix

**Approach: in `ProjectSession.Open`, read *and* convert the file (`LoadFromFile` + `FromDto`) before releasing anything. Only then release, and install the already-converted result. Split `Restore`'s body into an `Apply` step so `Open` can install a pre-converted project, while Undo/Redo keep their exact current behavior.**

### Change: `src/Acapella.Engine/Project/ProjectSession.cs:135-141` and `:157-170`

Replace `Open`:

```csharp
    public void Open(string filePath)
    {
        _mixEngine.ReleaseAllHostedInstances();          // unchanged, still FIRST (bug audit #5)
        Restore(_persistence.LoadFromFile(filePath));
        _undoStack.Reset(Snapshot());
        SetSaveState(filePath, dirty: false);            // LAST: Restore() assigns MetronomeBpm/MasterVolumeDb through their dirty-marking setters
    }
```

with:

```csharp
    public void Open(string filePath)
    {
        // Bug audit #16: read AND convert before releasing -- a file that fails either step must
        // leave the current project, including its live plugin instances, untouched.
        var loaded = _persistence.FromDto(_persistence.LoadFromFile(filePath));
        _mixEngine.ReleaseAllHostedInstances();          // still before any layer is replaced (bug audit #5)
        Apply(loaded);
        _undoStack.Reset(Snapshot());
        SetSaveState(filePath, dirty: false);            // LAST: Apply() assigns MetronomeBpm/MasterVolumeDb through their dirty-marking setters
    }
```

Replace `Restore`:

```csharp
    private bool Restore(ProjectFileDto? dto)
    {
        if (dto is null) return false;

        var (layers, bpm, latencyOffset, masterVolumeDb) = _persistence.FromDto(dto);
        Layers.Restore(layers.Layers);
        MetronomeBpm = bpm;
        LatencyOffsetMsUsed = latencyOffset;
        MasterVolumeDb = masterVolumeDb;

        foreach (var layer in Layers.Layers)
            _mixEngine.PushSavedStateIntoLiveInstances(layer.LayerId, layer.MixParameters);
        return true;
    }
```

with:

```csharp
    private bool Restore(ProjectFileDto? dto)
    {
        if (dto is null) return false;
        Apply(_persistence.FromDto(dto));
        return true;
    }

    private void Apply((LayerCollection Layers, double MetronomeBpm, double? LatencyOffsetMsUsed, float MasterVolumeDb) loaded)
    {
        Layers.Restore(loaded.Layers.Layers);
        MetronomeBpm = loaded.MetronomeBpm;
        LatencyOffsetMsUsed = loaded.LatencyOffsetMsUsed;
        MasterVolumeDb = loaded.MasterVolumeDb;

        foreach (var layer in Layers.Layers)
            _mixEngine.PushSavedStateIntoLiveInstances(layer.LayerId, layer.MixParameters);
    }
```

`Apply`'s body is `Restore`'s old body, statement for statement. Also update the `Open` doc comment's first sentence (`:125-126`) from "Releases every live hosted-plugin instance and ARA session FIRST, before restoring" to "Reads and converts the file first (bug audit #16), then releases every live hosted-plugin instance and ARA session before installing it". The rest of that comment (the #5 argument) stays as written. `New()` and `MainWindow.xaml.cs` are unchanged.

### Rejected alternatives

- **Just swap the two lines: `var dto = LoadFromFile(...)`, then release, then `Restore(dto)`.** This covers only the read step. `FromDto` still runs inside `Restore`, after the release. Counter-example: a well-formed project file whose layer says `"Kind": "Bogus"`, or from a later build with a new `LayerKind`. `Deserialize` succeeds, the release runs, `Enum.Parse<LayerKind>` throws, and the plugin state is gone. That is five of the nine failing files in Repro D: `bad Kind`, `Layers null`, `MixParams null`, `bad base64`, and `5 layers` (through `LayerCollection.Restore`). The `[Theory]` includes these cases specifically so this half-fix fails it.
- **Catch in `Open`, then re-create and restore the old instances.** Melodyne makes this impossible: an ARA session's edits cannot be exported or re-imported by any production code (§Root cause 3). FabFilter could only be restored from `LayerMixParameters`, which is exactly the stale copy that loses the unpolled tweaks.
- **Release after `Apply`, or after `Snapshot()` (i.e. "load everything, release last").** That is Bug Audit #5 reintroduced. `PushSavedStateIntoLiveInstances` would push the opened project's non-null state into the *previous* project's instances and skip null state. `_undoStack.Reset(Snapshot())` would then pull the previous project's live bytes into the opened project's baseline and next save (`Bug_Audit_2026-09-23_ProjectSwitchPluginState.md:92`).
- **Validate in `MainWindow` before calling `Open`** (for example, parse once in `OpenProjectButton_Click`). That duplicates `ProjectSession`'s parsing, and it would read the file twice, leaving a window where the file can change between the check and the real read. `Open` is the one place that owns the ordering, and it is where the engine tests can pin it.
- **Pull live state into the parameters (`Snapshot()`) before the release, so FabFilter survives.** This fixes only the FabFilter half. It also changes the successful-Open path for no benefit, and Melodyne is still lost.

---

## Ordering subtleties

**1. `FromDto` must run before the release, not just `LoadFromFile`.**
- **Counter-example:** the naive swap above, with `{"Layers":[{"LayerId":0,"Kind":"Bogus","SourcePath":"x.wav"}]}`. `LoadFromFile` returns a DTO, the release runs, and `FromLayerDto` throws `ArgumentException` at `ProjectPersistenceService.cs:73`. Everything the fix is meant to protect is already gone.

**2. The release must still come before `Apply`, and therefore before `Snapshot()`.**
- **Counter-example:** previous project A has a live EQ on layer 0. Opened project B's layer 0 has no EQ state. If `Apply` runs first, `PushSavedStateIntoLiveInstances` skips the null state (`MixEngine.cs:119`), and `Snapshot()` then pulls A's bytes into B's undo baseline. That is Bug Audit #5 exactly, and `Open_DoesNotLeakThePreviousProjectsPluginStateIntoTheOpenedProject` would go red.

**3. `Apply` must install the object converted before the release, not re-read the file.**
- **Counter-example:** `Open` validates with one `LoadFromFile` + `FromDto`, then releases, then calls `Restore(LoadFromFile(filePath))` again. If the file changes between the two reads (a cloud-sync client replacing it, or a second app instance saving it), the second read can fail after the release, and the bug is back. Keeping `loaded` in memory means no I/O and no parsing happens after the release.

**4. `SetSaveState` stays last, and nothing that can throw is added between the release and it.**
- After the fix, the steps after the release are `Layers.Restore` of an already-capped list, three property setters, `PushSavedStateIntoLiveInstances` (nothing is live, so it's a no-op), `Snapshot()` (nothing live to pull), and `SetSaveState`. None of these can fail on file content. The one remaining post-release throw is in the App, not here (see Deliberately not changed, duplicate `CellIndex`).

---

## Deliberately not changed

- **`New()`.** It has no file to fail on. Releasing first is correct there.
- **Successful Open still discards Melodyne edits.** That is the missing ARA archive persistence (`Bug_Audit_2026-07-16_AraMelodyne.md` A5), not this ticket. The user chose to leave the project in that case, and the dirty prompt runs first when the project is dirty.
- **`OpenProjectButton_Click` stops preview before the Open (`MainWindow.xaml.cs:1121`), even if the Open then fails.** It has to stay before `_session.Open`, because a successful Open still releases instances the WASAPI thread is processing (Bug Audit #5 §5). A failed Open costs the user one Play press, and nothing is lost.
- **The unsaved-changes prompt before the dialog (`:1109`).** A "No" followed by a failed Open now keeps both the unsaved edits and the plugin state. Path and dirty were already pinned by `FailedOpen_LeavesPathAndDirtyUntouched`.
- **A duplicate `CellIndex` in the file still throws in `RestoreTracksFromLayers`** (`ToDictionary`, `MainWindow.xaml.cs:793`). That happens *after* `_session.Open` has succeeded and released, so this fix does not cover it. It is already tracked in #15's Runners-up. Under the new ordering, moving that check into `FromDto` (throwing on a duplicate `CellIndex`) would make it safe automatically, because it would then fail before the release.
- **The editor poll covering only the selected layer (`:124`).** That is why an unpolled FabFilter tweak exists at all, but it is a deliberate design (`Bug_Audit_2026-09-24_UndoDropsOpenEditorTracking.md:272`). With this fix, a failed Open no longer depends on it.
- **Opening a *valid-looking* foreign `*.json`.** It still "succeeds" and releases. See Runners-up.

---

## Tests

### Additions to `tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs`

These use the file's existing fixtures: `_mixEngine` over a `FakeHostedPluginFactory` with Pro-Q registered, `OpenEqEditorLiveInstance`, and `TempProjectPath()`, whose `Dispose` deletes the file and tolerates a file that was never written. They are pure engine tests. No WPF, no dialog, no native DLL.

```csharp
    // ----- Bug audit #16: a failed Open must not release the current project's plugin state -----

    [Theory]
    [InlineData("missing file", null)]
    [InlineData("truncated JSON", "{ \"LayoutId\": \"2x2\", \"Layers\": [")]
    [InlineData("empty file", "")]
    [InlineData("JSON array", "[1, 2]")]
    [InlineData("literal null", "null")]
    [InlineData("unknown layer Kind", "{\"Layers\":[{\"LayerId\":0,\"Kind\":\"Bogus\",\"SourcePath\":\"x.wav\"}]}")]
    [InlineData("null Layers", "{\"Layers\":null}")]
    [InlineData("bad hosted-state base64", "{\"Layers\":[{\"LayerId\":0,\"Kind\":\"RecordedAV\",\"SourcePath\":\"x.wav\",\"MixParameters\":{\"EqHostedStateBase64\":\"!!\"}}]}")]
    [InlineData("five layers", "{\"Layers\":[{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"}]}")]
    public void FailedOpen_KeepsTheCurrentProjectsLivePluginInstances(string description, string? fileContent)
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        var plugin = OpenEqEditorLiveInstance(layer);
        plugin.TweakInEditor(new byte[] { 9, 9, 9 });   // live only: never polled/snapshotted into MixParameters

        string badPath = TempProjectPath();
        if (fileContent is not null) File.WriteAllText(badPath, fileContent);

        Assert.ThrowsAny<Exception>(() => session.Open(badPath));

        Assert.False(plugin.Disposed, $"{description}: a failed Open released the live instance.");
        Assert.Same(plugin, OpenEqEditorLiveInstance(layer));
        Assert.Equal(new byte[] { 9, 9, 9 }, plugin.State);
        Assert.Same(layer, session.Layers.Layers.Single());
    }

    [Fact]
    public void SuccessfulOpen_StillReleasesThePreviousProjectsInstances()
    {
        // Guards against over-fixing #16: bug audit #5's release must still happen on a real Open.
        var saved = new ProjectSession(_mixEngine);   // saved BEFORE the live instance exists (see #5's test note)
        saved.Layers.Add(LayerKind.UploadedVideo, "clip.mp4");
        string path = TempProjectPath();
        saved.Save(path);

        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        var plugin = OpenEqEditorLiveInstance(layer);

        session.Open(path);

        Assert.True(plugin.Disposed);
        Assert.NotSame(plugin, OpenEqEditorLiveInstance(session.Layers.Layers.Single()));
    }
```

Notes:
- **Why the Theory has read-step and convert-step cases.** Five cases fail inside `LoadFromFile`, four inside `FromDto`. The naive-swap alternative passes the first five and fails the last four. The full fix passes all nine. All nine fail today (Repro D).
- **`Assert.ThrowsAny<Exception>`.** Deliberately broad. The exception type differs per case (§Root cause 1 table), and this ticket doesn't change what a failed Open throws, only what it destroys first. `OpenProjectButton_Click` catches `Exception`.
- **The tweak is applied with `TweakInEditor` and never committed.** That makes the live instance the only copy of `{9,9,9}`, which is what a non-selected layer's editor tweak is in the app (§Root cause 3).
- **Melodyne is not asserted directly.** `AraHostSession.Create` needs the native bridge and a real Melodyne. `ReleaseAll` disposes ARA sessions and cache instances in one dispatcher call (`HostedPluginService.cs:222-228`), and nothing else in the fix touches ARA. So "the cache instance survives" means "`ReleaseAll` did not run", and that covers both.

**Checked, not assumed:** the two methods above were compiled and run once, verbatim from this doc, in a scratch xunit project in `%TEMP%\aca16t`. It used the same fixture over the built HEAD DLLs, once against `ProjectSession` and once against Repro D's `FixedProjectSession`.
- **Against HEAD:** all 9 Theory cases failed, each on `"<case>: a failed Open released the live instance."`, and the guard Fact passed.
- **Against the fix:** all 10 tests passed.
- **Warnings:** none.

**No App-level test.** `MainWindow` is untouched, and the only App entry point opens a modal `OpenFileDialog`.

**Regression carve-out (per CLAUDE.md):** `ProjectSession.Restore` is refactored, and Undo/Redo, Retake-undo and Open all go through it. Re-run **both** `Acapella.Engine.Tests` (all of `ProjectSessionTests`, notably the Undo/Redo, Retake and #5 leak tests) and `Acapella.App.Tests` (the Bug Audit #9 editor-tracking carry tests drive `PerformUndo` → `_session.Undo` → `Restore`) once.

---

## Acceptance criteria

### [auto]
- All nine `FailedOpen_KeepsTheCurrentProjectsLivePluginInstances` cases pass. Every one fails before the fix, on `Assert.False(plugin.Disposed, ...)`.
- `SuccessfulOpen_StillReleasesThePreviousProjectsInstances` passes both before and after the fix.
- All of `Acapella.Engine.Tests` and `Acapella.App.Tests` pass (regression carve-out), including `Open_DoesNotLeakThePreviousProjectsPluginStateIntoTheOpenedProject`, `FailedOpen_LeavesPathAndDirtyUntouched`, `UndoAndRedo_StepThroughCommittedEdits` and `Retake_ThenUndo_RestoresTheOldTake_AndKeepsTheSameLivePluginInstance`.
- The solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- In `Open`, `LoadFromFile` **and** `FromDto` both come before `ReleaseAllHostedInstances()` (subtlety 1).
- `ReleaseAllHostedInstances()` still comes before `Apply`, `_undoStack.Reset(Snapshot())` and `SetSaveState` (subtlety 2).
- `Open` reads the file exactly once. `Apply` receives the value converted before the release (subtlety 3).
- `Apply`'s body is `Restore`'s old body, statement for statement. `Restore` still returns `false` on a `null` DTO and gains no explicit `MarkDirty` call (Save-affordances D2).
- `New()`, `ProjectPersistenceService`, `MixEngine`, `HostedPluginService` and `MainWindow.xaml.cs` are untouched. The `Open` doc comment's first sentence is updated, and the #5 argument is kept.

### [human]
- Repro A after the fix: the failed Open shows `Open failed: ...`. The Melodyne editor window stays open, Play still has the edited note, and a subsequent export contains it.
- Repro B after the fix: layer 1's Pro-Q window stays open through the failed Open, and the notch is still there.
- No regression: opening a *valid* project still closes any open FabFilter/Melodyne editor windows and gives the opened project fresh instances with its own saved state (Bug Audit #5's Repro).

---

## Runners-up (not specced)

- **Opening any non-project `*.json` "succeeds" as an empty project, and Ctrl+S then overwrites that foreign file.** This is the other lead from #15's Runners-up. Verified, not specced.
  - **Evidence:** Repro D's harness opened `{"name":"my-app","version":"1.0.0","dependencies":{}}` and `{"editor.fontSize": 14}` at HEAD. Both returned `OPEN OK, layers=0`, with `CurrentFilePath` set to the foreign file and `IsDirty=false`. `System.Text.Json` ignores unknown properties, and every `ProjectFileDto` property has a default (`ProjectFileDto.cs:13-26`). `SaveProject(forceDialog: false)` then writes straight to `CurrentFilePath` with no dialog (`MainWindow.xaml.cs:1041-1062`). Saving an opened plain `*.json` back under its own name is deliberate (Save-affordances D5, `:1054`).
  - **Why not specced here:** it is a different root cause (no format marker checked on load) with a different fix. Reaching it takes three user steps: open the wrong file, not notice "0 layer(s)" and the renamed title, then build a project and press Ctrl+S.
  - **Fix direction:** reject the file on the Open side, never in `SaveProject`, since D5 is intentional. `LayoutId` has been written by every save since the first persistence commit (`56a7577`), so a `LayoutId` that is absent from the JSON is a usable marker. For example, default the DTO's `LayoutId` to `null`, which `ToDto` always overwrites, and throw `InvalidDataException` from `FromDto` when it is `null`.
  - **Dependency on this fix:** that rejection turns the case into a failed Open. So it is only safe *after* this ticket's reordering. Before it, rejecting a foreign file would destroy the current project's plugin state.
- **A released-then-recreated ARA session can replay the destroyed session's cached render.** Reasoned only, not traced further.
  - **Mechanism:** `PitchCorrectionCache` is static, and its Manual2A key is `(layerId, SourceCacheKey()-araEdit{gen}, backend)` (`MixEngine.cs:200-204`). It keeps 2 keys per layer (`PitchCorrectionCache.cs:23`). `ReleaseAll` destroys the sessions but not the cache, and a fresh session's generation restarts at 0 (`HostedPluginService.cs:214-215`).
  - **Trigger after this fix:** a successful Open of the *same* project (for example, to revert to the saved version), or New followed by re-importing the same file into the same slot. In both, the layer keeps the same `LayerId` and `SourceCacheKey()`. The first edit in the new session produces `…-araEdit1`. If the old session had only 1-2 edits, that key is still cached, so Play and export return the old session's render, not the new edit, until a second edit bumps the key.
  - This fix does not touch that path. Before this fix, a failed Open was a third trigger.
