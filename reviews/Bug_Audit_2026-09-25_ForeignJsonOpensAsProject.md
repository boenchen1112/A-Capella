# Bug Audit #17: File > Open accepts any JSON object as an empty project, and Ctrl+S then overwrites that file with project JSON

> **Promoted from Bug Audit #16's Runners-up** (`Bug_Audit_2026-09-25_FailedOpenReleasesPluginState.md:374-378`). #15 also listed it (`Bug_Audit_2026-09-25_WindowCloseAbandonsExport.md:326`). Re-traced from the code, and run against HEAD and against the proposed fix in a scratch harness.
>
> No doc in `reviews/` specs this. The Save-affordances spec made "Ctrl+S writes back to a plain `*.json` under its own name" deliberate (D5, `Feature_Spec_2026-09-23_SaveAffordances.md:64`). It assumed that the file had been opened *as a project*, and nothing checks that.
>
> Audit as of `99d205f` (Bug Audit #16 landed, including its regression tests).
>
> **Confidence: high.**
> - **Measured.** A scratch harness (Repro C) drives the real `ProjectSession` over the suite's `FakeHostedPluginFactory`. At HEAD, a `package.json`-shaped file opens as a clean 0-layer project pointing at that file. One added layer and one `Save(CurrentFilePath)` later, the file no longer contains `my-app`: it has become project JSON.
> - **Backward compatibility: measured and traced.** A save produced by building the *first* persistence commit (`56a7577`) and running its own serializer opens under the fix. The git history shows that every save the app has ever written contains `"LayoutId": "2x2"` (§Proposed fix, "Every file the app has ever saved still opens").
> - **The #16 dependency is verified.** A rejection thrown from `LoadFromFile` now leaves the live plugin instance, path, dirty flag and layers untouched (measured, Repro C). This fix relies on that.

---

## The bug in one sentence

`ProjectPersistenceService.LoadFromFile` accepts any JSON object as a project (`ProjectPersistenceService.cs:46-51`). `System.Text.Json` ignores unknown properties, and every `ProjectFileDto` property has a default, so `package.json`, a VS Code `settings.json`, or `{}` all "open" as a clean, empty project whose `CurrentFilePath` is that file. From then on, Ctrl+S, File > Save and every "Yes, save" prompt write project JSON straight over the foreign file with no dialog (`MainWindow.xaml.cs:1041-1062`). Opening the wrong file is also a *successful* Open, so it releases the current project's FabFilter instances and every Melodyne edit (Bug Audit #5's release), and a "No" to the unsaved-changes prompt discards the current project's edits.

---

## Current behavior

| Location | What it does |
|---|---|
| `MainWindow.xaml.cs:1111` | `OpenFileDialog { Filter = "Acapella project\|*.acapella.json;*.json" }`. Any `*.json` is offered, deliberately (D5: legitimate projects can be named plain `*.json`, §Proposed fix). |
| `MainWindow.xaml.cs:1123-1128` | `ApplyRestore(() => { _session.Open(...); return true; })`, then `StatusText.Text = "Project opened: <name> (N layer(s))."`. |
| `ProjectSession.cs:136-145` | `Open`: `FromDto(LoadFromFile(filePath))` (`:140`), **then** `ReleaseAllHostedInstances()` (`:141`), `Apply`, `SetSaveState(filePath, dirty: false)` (`:144`). |
| `ProjectPersistenceService.cs:46-51` | `LoadFromFile`: `File.ReadAllText` + `JsonSerializer.Deserialize<ProjectFileDto>(json)`. Throws only on malformed JSON, a non-object root, a wrongly typed known property, or a literal `null`. |
| `ProjectFileDto.cs:13-26` | Every property has a default: `LayoutId = "2x2"`, `MetronomeBpm = 120`, `Layers = new()`. A missing key is indistinguishable from a saved default. |
| `ProjectPersistenceService.cs:29-34` | `FromDto` ignores `LayoutId` entirely. An empty `Layers` list converts to an empty project. |
| `ProjectSession.cs:60`, `:75-78` | `DisplayName` strips only the last extension, so `package.json` shows as "package". |
| `MainWindow.xaml.cs:1039-1071` | `SaveProject(forceDialog: false)`: `filePath = _session.CurrentFilePath` (`:1041`). Non-null, so no dialog. `_session.Save(filePath)` (`:1062`). |
| `MainWindow.xaml.cs:1077-1089` | `ConfirmDiscardUnsavedChanges`: "Yes" → `SaveProject(forceDialog: false)` (`:1085`). Used by File > Open (`:1109`), File > New (`:1196`) and window close (`:182`). |
| `ProjectPersistenceService.cs:36-44` | `SaveToFile`: `File.WriteAllText`. Overwrites without checking what was there. |

---

## Root cause

Three facts combine to produce the defect.

### 1. Nothing on the load path checks that the file is a project

`LoadFromFile` asks the deserializer for a `ProjectFileDto` and takes whatever comes back. For a JSON object, the deserializer:
- matches property names case-sensitively and skips every one it doesn't know;
- leaves each unmatched DTO property at its initializer.

So the answer is "a default project" for any object whose keys don't collide with the DTO's. The DTO initializes `LayoutId` to `"2x2"` (`ProjectFileDto.cs:13`), which is the one field every save writes. That initializer hides the only signal that could tell a project apart from a foreign file.

Repro C measured these shapes at HEAD:

| File content | At HEAD | Why |
|---|---|---|
| `{"name":"my-app","version":"1.0.0","dependencies":{}}` (package.json) | **opens, 0 layers** | no known keys |
| `{"editor.fontSize": 14}` (settings.json, no comments) | **opens, 0 layers** | no known keys |
| `{}` | **opens, 0 layers** | no keys |
| `{"layoutId":"2x2","layers":[{"kind":"x"}]}` | **opens, 0 layers** | camelCase keys don't match (case-sensitive) |
| `{"Layers":[]}` | **opens, 0 layers** | only a matching, empty key |
| `{"LayoutId":null}` | **opens, 0 layers** | `FromDto` never reads `LayoutId` |
| settings.json with a `//` comment | fails, `JsonException` | comments are rejected by default |
| `{"Layers":[{"name":"x"}]}` | fails, `ArgumentException` | `Enum.Parse<LayerKind>("")` |
| `{"MetronomeBpm":"fast"}` | fails, `JsonException` | wrongly typed known key |
| `[{"a":1}]` | fails, `JsonException` | non-object root |

Every "opens" row set `CurrentFilePath` to the foreign file, with `IsDirty=false`, and released the previous project's live plugin instance (`oldDisposed=True`).

### 2. After that, Save writes to the foreign file by design

`Open` records the path (`ProjectSession.cs:144`). D5 says a project opened as `foo.json` must be saved back to `foo.json`, and `SaveProject(forceDialog: false)` does exactly that, with no dialog and no suffix (`MainWindow.xaml.cs:1041`, `:1054`). That rule is correct for a real project and must stay. The defect is that a non-project got that far.

Save As is safe: it pre-fills `package.json`, and L7 turns that into `package.json.acapella.json` (`:1055-1057`). The overwrite paths are Ctrl+S, File > Save, and "Yes" in the unsaved-changes prompt on New, Open or window close.

### 3. The wrong file "succeeding" costs more than the overwrite

A foreign file that opens is a *successful* Open, so it runs Bug Audit #5's release (`ProjectSession.cs:141`):
- Every FabFilter editor of the current project closes. Its live instances are destroyed.
- Every Melodyne edit is destroyed. Edits are never persisted (`Bug_Audit_2026-07-16_AraMelodyne.md` A5), so reopening the real project does not bring them back.
- If the project was dirty and the user answered "No" to the prompt (`:1109`), expecting to open *their other project*, those unsaved edits are gone too.

The UI then looks like a legitimate empty project:
- Empty grid.
- Title "Acapella — package".
- Status "Project opened: package.json (0 layer(s)).".
- Nothing says "this isn't a project".

After the fix, all of this becomes a failed Open, and Bug Audit #16 makes a failed Open leave everything in place.

---

## Repros

### A. Ctrl+S overwrites package.json **[human]**

Preconditions: any folder with a `package.json` in it (or any `.json` that is a plain JSON object without comments). Keep a copy.

1. Build a project with at least one layer and a Melodyne edit (optional). Ctrl+S so it is clean.
2. File > Open..., choose `package.json`.
3. **Bug (part 1):** no error. Status: `Project opened: package.json (0 layer(s)).`. The grid is empty, the title reads "package", open plugin windows have closed. Reopening the real project shows the Melodyne edit is gone.
4. Import or record a layer (the project is now dirty: "package •"). Press Ctrl+S.
5. **Bug (part 2):** status `Project saved: package.json`. No dialog appeared. Open `package.json` in a text editor: its contents are now `{"LayoutId": "2x2", ...}`. The original is gone.

Closing the window at step 4 and answering "Yes" to "Save changes to package?" does the same.

### B. Headless: a foreign JSON object opens **[auto]**

`Open_RejectsAJsonObjectThatIsNotAProject` (§Tests) is a four-case `[Theory]`: package.json, settings.json, `{}`, and camelCase keys. Each opens a real project, creates a live fake EQ instance, then opens the foreign file and expects `InvalidDataException`. Today every case fails with `Assert.Throws() Failure: No exception was thrown`.

### C. Harness, HEAD vs. proposed fix **[auto-able, run once by hand for this audit, not added to the suite]**

This is a scratch copy of `src/Acapella.Engine` + `tests/Acapella.Engine.Tests` from `git archive HEAD`, in `%TEMP%\aca17`, with a throwaway `Scratch17.cs` test class. It was run once as-is and once with exactly the §Proposed fix applied.

**Overwrite, end to end.** Open `package.json`, add a layer, `CommitEdit`, then `Save(CurrentFilePath)`, which is what `SaveProject(forceDialog: false)` does:

```
HEAD   after Open: layers=0 dirty=False path=foreign:True
HEAD   after Save(CurrentFilePath): foreignIntact=False startsWith='{   "LayoutId": "2x2",   "Me' containsName=False
FIXED  Open threw InvalidDataException: Not an Acapella project file: <...>-package.json; path=(null)
```

**Shapes** (§Root cause 1 table). `oldDisposed` is the current project's live EQ instance:

```
HEAD   package.json                 OPEN OK layers=0 path=foreign:True dirty=False oldDisposed=True
HEAD   settings.json (no comments)  OPEN OK layers=0 path=foreign:True dirty=False oldDisposed=True
HEAD   empty object                 OPEN OK layers=0 path=foreign:True dirty=False oldDisposed=True
HEAD   camelCase layers             OPEN OK layers=0 path=foreign:True dirty=False oldDisposed=True
HEAD   PascalCase empty Layers      OPEN OK layers=0 path=foreign:True dirty=False oldDisposed=True
HEAD   LayoutId null                OPEN OK layers=0 path=foreign:True dirty=False oldDisposed=True
HEAD   settings.json (// comment)   THREW JsonException          oldDisposed=False
HEAD   Layers of foreign objects    THREW ArgumentException      oldDisposed=False
HEAD   wrong-typed MetronomeBpm     THREW JsonException          oldDisposed=False
HEAD   top-level array              THREW JsonException          oldDisposed=False
FIXED  package.json                 THREW InvalidDataException   oldDisposed=False
FIXED  settings.json (no comments)  THREW InvalidDataException   oldDisposed=False
FIXED  empty object                 THREW InvalidDataException   oldDisposed=False
FIXED  camelCase layers             THREW InvalidDataException   oldDisposed=False
FIXED  PascalCase empty Layers      THREW InvalidDataException   oldDisposed=False
FIXED  LayoutId null                THREW InvalidDataException   oldDisposed=False
FIXED  Layers of foreign objects    THREW InvalidDataException   oldDisposed=False
FIXED  settings.json (// comment)   THREW JsonException          oldDisposed=False
FIXED  wrong-typed MetronomeBpm     THREW JsonException          oldDisposed=False
FIXED  top-level array              THREW JsonException          oldDisposed=False
```

The `oldDisposed=False` on every FIXED row is Bug Audit #16's reordering at work. The new rejection throws from `LoadFromFile`, before the release.

**#16's convert-step cases.** For each, the exception type and the first `Acapella.*` frame it was thrown from:

```
HEAD   #16 original: unknown Kind   THREW ArgumentException          at FromLayerDto
HEAD   #16 original: null Layers    THREW ArgumentNullException      at FromDto
HEAD   #16 original: bad base64     THREW FormatException            at FromBase64
HEAD   #16 original: five layers    THREW InvalidOperationException  at Restore
FIXED  #16 original: unknown Kind   THREW InvalidDataException       at LoadFromFile
FIXED  #16 original: null Layers    THREW InvalidDataException       at LoadFromFile
FIXED  #16 original: bad base64     THREW InvalidDataException       at LoadFromFile
FIXED  #16 original: five layers    THREW InvalidDataException       at LoadFromFile
FIXED  + "LayoutId":"2x2": unknown Kind  THREW ArgumentException          at FromLayerDto
FIXED  + "LayoutId":"2x2": null Layers   THREW ArgumentNullException      at FromDto
FIXED  + "LayoutId":"2x2": bad base64    THREW FormatException            at FromBase64
FIXED  + "LayoutId":"2x2": five layers   THREW InvalidOperationException  at Restore
```

This is why §Tests edits four of #16's `InlineData` (§Ordering subtleties 3).

---

## Why the suite misses it

- **Every test that opens a file opens one the app saved.** `Open_RemembersThePath_AndEndsClean`, `SaveThenOpen_...`, the #5 leak test and `HostedFxChainTests.SaveCloseReopenExport_...` all `Save` first. So `LayoutId` is always present, and the "is this a project?" question never comes up.
- **#16's Theory feeds hand-written JSON, but only JSON that fails.** Its four convert-step cases happen to lack `LayoutId`, and they are *meant* to throw. No case is a well-formed non-project that is supposed to throw and doesn't.
- **`ProjectPersistenceTests` round-trips `ToDto` output only** (`ProjectPersistenceTests.cs:47-49`, `:98-100`), which always carries `LayoutId`.
- **The App half (Ctrl+S with no dialog) is D5, intended and untested at the App level.** `OpenProjectButton_Click` shows a modal `OpenFileDialog`, so no `[StaFact]` drives it. The defect is entirely in what `LoadFromFile` accepts.

---

## Proposed fix

**Approach: make a missing `LayoutId` detectable, and reject it in `LoadFromFile`, the one place file content enters. Drop the DTO's `LayoutId` initializer so "absent from the JSON" stays `null` after deserializing, then throw `InvalidDataException("Not an Acapella project file: ...")` from `LoadFromFile` when it is `null`. `FromDto`, `ProjectSession`, the undo stack and `MainWindow` are unchanged.**

### Change 1: `src/Acapella.Engine/Persistence/ProjectFileDto.cs:11-13`

Replace:

```csharp
    /// <summary>Locked to "2x2" for v1 -- stored explicitly per the Data Model Rule rather than
    /// assumed, so raising the layout options later doesn't require a format migration.</summary>
    public string LayoutId { get; set; } = "2x2";
```

with:

```csharp
    /// <summary>Locked to "2x2" for v1 -- stored explicitly per the Data Model Rule rather than
    /// assumed, so raising the layout options later doesn't require a format migration.
    /// No initializer (bug audit #17): null after a load means the file had no LayoutId, i.e. isn't a project.</summary>
    public string? LayoutId { get; set; }
```

Nothing reads `LayoutId` (grep `src/`: only `ToDto` writes it, `ProjectPersistenceService.cs:21`), so the nullable change has no consumers to update. `ToDto` still writes `"2x2"` on every save and every undo snapshot.

### Change 2: `src/Acapella.Engine/Persistence/ProjectPersistenceService.cs:46-51`

Replace:

```csharp
    public ProjectFileDto LoadFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<ProjectFileDto>(json)
            ?? throw new InvalidDataException($"Could not parse project file: {filePath}");
    }
```

with:

```csharp
    public ProjectFileDto LoadFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var dto = JsonSerializer.Deserialize<ProjectFileDto>(json)
            ?? throw new InvalidDataException($"Could not parse project file: {filePath}");
        // Bug audit #17: every save since the first (56a7577) writes LayoutId; a JSON object without it isn't a project.
        return dto.LayoutId is not null ? dto
            : throw new InvalidDataException($"Not an Acapella project file: {Path.GetFileName(filePath)}");
    }
```

The user then sees `Open failed: Not an Acapella project file: package.json` (`MainWindow.xaml.cs:1132`), and stays on their project with its plugin state intact (#16).

### Every file the app has ever saved still opens

**The marker is the presence of `LayoutId`, and every save has had it.**
- `ProjectFileDto` and `ProjectPersistenceService` have six revisions: `56a7577`, `c106f51`, `1ab4511`, `8bb76cf`, `1968b19`, `4df6ddb`. At every one, `ToDto` sets `LayoutId = "2x2"`, and `JsonOptions` is `{ WriteIndented = true }`. There is no naming policy and no null-skipping, so the key is always written as `"LayoutId"` (PascalCase).
- `git log -G LayoutId -- src` touches only `56a7577`. The line has never changed.
- `ToDto` is the only constructor of a `ProjectFileDto` in `src/`, ever (`git log -S "new ProjectFileDto" -- src`: `56a7577` only).
- Every save path in history goes through it:
  - `56a7577`: `MainWindow` → `ToDto` → `SaveToFile`.
  - Until `fb539a7`: `CurrentProjectDto()` → `ToDto`.
  - Since then: `ProjectSession.Save` → `Snapshot()` → `ToDto`.
  - `git log -S SaveToFile` lists no other caller.

**The oldest format opens under the fix, measured.** `56a7577` was checked out with `git archive` into `%TEMP%\aca17old`, and its own `ToDto` + `SaveToFile` produced the file. Its output is embedded verbatim in the §Tests constants (`SavedBy56a7577Empty`, `SavedBy56a7577OneLayer`). It predates `MasterVolumeDb`, `Name`, trim, all hosted-state fields and every `*Enabled` flag, and it opens, with the fix applied, with the right layer count and BPM.

**An empty but real project opens.** File > New → Ctrl+S writes `{"LayoutId": "2x2", "MetronomeBpm": 120, ..., "Layers": []}`. This is why the check is "has `LayoutId`", not "has content". `Open_AcceptsAnEmptyProjectSavedByTheApp` pins it.

**Plain `*.json` project names stay openable.** Before L7 (`541bb77`), a user who typed `foo.json` into the Save dialog got `foo.json`, because `DefaultExt` doesn't append to a name that already has an extension. D5 keeps such files under their own name. The fix does not look at the file name, so these still open, and the `*.json` filter stays.

**Not covered: a hand-edited project with its `LayoutId` line deleted.** It is now rejected. No app build ever wrote such a file. The one-line remedy is to add `"LayoutId": "2x2"` back.

**No real saves on this machine to spot-check.** A read-only scan of the user folders and `D:\VS Code` found no `*.acapella.json` (the test suite deletes its temp saves). No project fixtures are committed either (`git ls-files '*.json'`: only `.claude/settings.local.json`).

### Rejected alternatives

- **Throw from `FromDto` when `LayoutId` is null** (#16's suggested direction). `FromDto` also runs for every Undo/Redo (`ProjectSession.Restore`, `:164`), and `ProjectPersistenceTests` calls it on in-memory DTOs. Neither is foreign input. `LoadFromFile` is the file boundary, and the check belongs there.
- **`[JsonRequired]` on `LayoutId`.** It keeps the initializer and gets the deserializer to enforce presence. But the user then sees `Open failed: JSON deserialization for type 'Acapella.Engine.Persistence.ProjectFileDto' was missing required properties, including the following: LayoutId`, which reads like corruption, not "wrong file". It also applies to the undo stack's own `Deserialize` (`ProjectUndoStack.cs:82-83`).
- **Keep the initializer and probe the raw JSON with `JsonDocument` for a `LayoutId` key.** It is equivalent, but parses the file twice. And `{"LayoutId":null}` would pass the probe, then open as an empty project, because nothing reads the value.
- **Reject "empty-looking" DTOs** (no layers and all defaults). The counter-example is File > New then Ctrl+S: that legitimate save is exactly `LayoutId` + defaults + `"Layers": []`, and this would reject it.
- **Add a `FormatVersion` field and require it.** Every existing save lacks it, so it would have to be optional for old files. An optional field can't tell an old save from `package.json`. `LayoutId` already does that job.
- **Narrow the Open filter to `*.acapella.json`.** It hides legitimate pre-L7 and D5 plain-`*.json` projects, and a user can still type any name into the dialog. It doesn't fix the load path.
- **Guard in `SaveProject` (check the target before overwriting, or always show a dialog for non-`.acapella.json` paths).** That reverses D5. It also leaves the first half of the bug in place: the wrong file still "opens", and the current project's plugin state and unsaved edits are still discarded.
- **Also require `LayoutId == "2x2"`.** The field exists so later builds can add layouts without a migration (`ProjectFileDto.cs:11-12`). Rejecting other values is a forward-compatibility decision, not this bug (see Deliberately not changed).

---

## Ordering subtleties

**1. This fix depends on Bug Audit #16, which has landed.**
- The rejection turns a foreign file into a failed Open.
- Before #16, `Open` released first (`ReleaseAllHostedInstances()`, then `LoadFromFile`), so the new throw would have destroyed the current project's plugin state as it rejected the file.
- At `99d205f`, `LoadFromFile` and `FromDto` both run at `ProjectSession.cs:140`, before the release at `:141`.
- `ApplyRestore` (`MainWindow.xaml.cs:214-245`) only records `previousRows` before calling `restore()`, and its `finally` resets `_applyingHistory`. So a throw leaves the UI untouched too.
- Repro C's FIXED rows measured `oldDisposed=False`. `Open_RejectsAJsonObjectThatIsNotAProject` asserts it, together with path, dirty and layers.

**2. The check must be in `LoadFromFile`, not after the release.**
- **Counter-example:** a check added in `ProjectSession.Open` after `ReleaseAllHostedInstances()`, or in `Apply`, rejects the file and destroys the plugin state anyway. That is #16's bug, reintroduced for this one input.
- `LoadFromFile` is on the pre-release line (`:140`), so any check inside it is automatically safe. `Open` needs no edit.

**3. Four of #16's Theory cases must gain `"LayoutId":"2x2"`, or #16's regression guard silently weakens.**
- `unknown layer Kind`, `null Layers`, `bad hosted-state base64` and `five layers` (`ProjectSessionTests.cs:457-460`) were chosen to fail inside `FromDto`, so that #16's "naive swap" half-fix fails them (#16 §Rejected alternatives).
- None of them has a `LayoutId`. After this fix, they throw at the new check in `LoadFromFile` instead (Repro C). The Theory still passes, but a regression that moves `FromDto` back after the release would pass it too.
- With the key added, they throw from `FromLayerDto`/`FromDto`/`FromBase64`/`Restore` again (Repro C, last four rows).

---

## Deliberately not changed

- **D5: Ctrl+S writes a `*.json` project back under its own name.** Correct for real projects, and still how every legitimate plain-`*.json` save works.
- **The Open dialog's `*.json` filter** (`MainWindow.xaml.cs:1111`). It is needed for pre-L7/D5 files (§Proposed fix).
- **`LayoutId`'s value is still not validated.** A hypothetical future `"3x3"` file would open as 2x2. No build writes anything but `"2x2"`, and deciding how an older build should treat a newer layout belongs with whichever feature adds one.
- **JSON with comments or trailing commas.** It already fails with `JsonException`, and the app never writes either.
- **The error text for other failures** (truncated file, bad `Kind`). Those messages are unchanged. Only the new not-a-project case gets the friendlier wording.
- **Other hand-edit hazards on load** (duplicate `CellIndex`, non-atomic save). Already tracked in #15's Runners-up.

---

## Tests

### Additions to `tests/Acapella.Engine.Tests/Project/ProjectSessionTests.cs`

These go after `SuccessfulOpen_StillReleasesThePreviousProjectsInstances` (`:479-494`). They use the file's existing fixtures: `_mixEngine` over a `FakeHostedPluginFactory` with Pro-Q registered, `OpenEqEditorLiveInstance`, and `TempProjectPath()`, whose `Dispose` deletes each path. They are pure engine tests. No WPF, no dialog, no native DLL.

```csharp
    // ----- Bug audit #17: a JSON file that is not a project must not open as an empty one -----

    // Verbatim output of the first persistence build (56a7577), the oldest format ever saved.
    private const string SavedBy56a7577Empty = """
        {
          "LayoutId": "2x2",
          "MetronomeBpm": 120,
          "LatencyOffsetMsUsed": null,
          "Layers": []
        }
        """;

    private const string SavedBy56a7577OneLayer = """
        {
          "LayoutId": "2x2",
          "MetronomeBpm": 96,
          "LatencyOffsetMsUsed": 162.5,
          "Layers": [
            {
              "LayerId": 0,
              "CellIndex": 0,
              "Kind": "RecordedAV",
              "SourcePath": "take1.mkv",
              "CalibratedOffsetMs": 0,
              "ManualOffsetMs": 0,
              "AraArchiveKey": null,
              "MixParameters": {
                "GainDb": 0,
                "Mute": false,
                "Solo": false,
                "Pan": 0,
                "LowShelfGainDb": 0,
                "MidBellGainDb": 0,
                "HighShelfGainDb": 0,
                "NoiseGateThresholdDb": -60,
                "NoiseGateReleaseMs": 100,
                "PitchBackend": "None"
              }
            }
          ]
        }
        """;

    [Theory]
    [InlineData("package.json", "{\"name\":\"my-app\",\"version\":\"1.0.0\",\"dependencies\":{}}")]
    [InlineData("VS Code settings", "{\"editor.fontSize\": 14}")]
    [InlineData("empty object", "{}")]
    [InlineData("camelCase keys", "{\"layoutId\":\"2x2\",\"layers\":[]}")]
    public void Open_RejectsAJsonObjectThatIsNotAProject(string description, string fileContent)
    {
        var session = new ProjectSession(_mixEngine);
        var layer = session.Layers.Add(LayerKind.UploadedAudioOnly, "a.wav");
        string projectPath = TempProjectPath();
        session.Save(projectPath);
        var plugin = OpenEqEditorLiveInstance(layer);

        string foreignPath = TempProjectPath();
        File.WriteAllText(foreignPath, fileContent);

        Assert.Throws<InvalidDataException>(() => session.Open(foreignPath));

        Assert.Equal(projectPath, session.CurrentFilePath);   // so Ctrl+S still targets the real project
        Assert.False(session.IsDirty, description);
        Assert.Same(layer, session.Layers.Layers.Single());
        Assert.False(plugin.Disposed, $"{description}: the rejected Open released the live instance.");
    }

    [Theory]
    [InlineData(SavedBy56a7577Empty, 0, 120)]
    [InlineData(SavedBy56a7577OneLayer, 1, 96)]
    public void Open_AcceptsAProjectSavedByTheFirstPersistenceBuild(string json, int layerCount, double bpm)
    {
        var session = new ProjectSession(_mixEngine);
        session.Layers.Add(LayerKind.UploadedAudioOnly, "other.wav");
        string path = TempProjectPath();
        File.WriteAllText(path, json);

        session.Open(path);

        Assert.Equal(layerCount, session.Layers.Layers.Count);
        Assert.Equal(bpm, session.MetronomeBpm);
        Assert.Equal(path, session.CurrentFilePath);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Open_AcceptsAnEmptyProjectSavedByTheApp()
    {
        var saved = new ProjectSession(_mixEngine);
        saved.New();
        string path = TempProjectPath();
        saved.Save(path);

        var session = new ProjectSession(_mixEngine);
        session.Layers.Add(LayerKind.UploadedAudioOnly, "other.wav");
        session.Open(path);

        Assert.Empty(session.Layers.Layers);
        Assert.Equal(path, session.CurrentFilePath);
        Assert.False(session.IsDirty);
    }
```

### Edit to Bug Audit #16's Theory, `ProjectSessionTests.cs:457-460`

Prefix each of the four convert-step cases' JSON with `"LayoutId":"2x2",` (§Ordering subtleties 3):

```csharp
    [InlineData("unknown layer Kind", "{\"LayoutId\":\"2x2\",\"Layers\":[{\"LayerId\":0,\"Kind\":\"Bogus\",\"SourcePath\":\"x.wav\"}]}")]
    [InlineData("null Layers", "{\"LayoutId\":\"2x2\",\"Layers\":null}")]
    [InlineData("bad hosted-state base64", "{\"LayoutId\":\"2x2\",\"Layers\":[{\"LayerId\":0,\"Kind\":\"RecordedAV\",\"SourcePath\":\"x.wav\",\"MixParameters\":{\"EqHostedStateBase64\":\"!!\"}}]}")]
    [InlineData("five layers", "{\"LayoutId\":\"2x2\",\"Layers\":[{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"},{\"Kind\":\"RecordedAV\"}]}")]
```

The other five cases are unchanged. They all fail before the new check can run: in `ReadAllText` or `Deserialize`, or on the existing `null` check. `truncated JSON` already carries `LayoutId`.

Notes:
- **`Assert.Throws<InvalidDataException>`, exact type.** It pins that the rejection is the new check, not an incidental parse failure. At HEAD there is no exception at all.
- **The foreign file is written through `TempProjectPath()`, with a `.acapella.json` name.** The check doesn't look at the name, and the fixture cleans it up. Repro C used real `package.json`/`foreign.json` names with the same results.
- **`Assert.Equal(projectPath, session.CurrentFilePath)` is the overwrite guard.** Ctrl+S writes to `CurrentFilePath` (`MainWindow.xaml.cs:1041`), so as long as it still names the real project, the foreign file can't be written.
- **The live-instance assert covers the #16 dependency** for this new failure (§Ordering subtleties 1).
- **The two historical constants are the `56a7577` build's own output, not hand-typed.** Newer formats are supersets that the suite already round-trips (`SaveThenOpen_...`, `SaveCloseReopenExport_...`).

**Checked, not assumed:** the tests above were compiled and run, verbatim from this doc, in the `%TEMP%\aca17` scratch copy.
- **At HEAD:** all 4 `Open_RejectsAJsonObjectThatIsNotAProject` cases failed, each with `Assert.Throws() Failure: No exception was thrown`. The rest of `ProjectSessionTests` passed (34/38), including `Open_AcceptsAProjectSavedByTheFirstPersistenceBuild` (2 cases), `Open_AcceptsAnEmptyProjectSavedByTheApp`, and #16's Theory with the four edited cases.
- **With the fix:** all of `ProjectSessionTests`, `ProjectPersistenceTests`, `ProjectUndoStackTests` and the scratch harness passed (46/46). The full `Acapella.Engine.Tests` run, with `AcapellaHostNative.dll` copied in, passed 257/257, including the 3 scratch-only harness tests.
- **Warnings:** no new warnings. The only ones are the existing SkiaSharp `CS0618` and xUnit1031.

**No App-level test.** `MainWindow` is untouched, and the only App entry point opens a modal `OpenFileDialog`.

**Regression carve-out (per CLAUDE.md):** `ProjectFileDto` is shared with the undo stack and every save. Re-run all of `Acapella.Engine.Tests` once, notably `ProjectPersistenceTests`, `ProjectUndoStackTests`, `ProjectSessionTests` and `HostedFxChainTests.SaveCloseReopenExport_ProducesSameOutputAsExportBeforeClosing`. Also build `Acapella.App` to confirm the `string?` change raises no nullable warning there (grep: no App reference to `LayoutId`).

---

## Acceptance criteria

### [auto]
- All four `Open_RejectsAJsonObjectThatIsNotAProject` cases pass. Every one fails before the fix, with "No exception was thrown".
- Both `Open_AcceptsAProjectSavedByTheFirstPersistenceBuild` cases and `Open_AcceptsAnEmptyProjectSavedByTheApp` pass, both before and after the fix.
- All nine `FailedOpen_KeepsTheCurrentProjectsLivePluginInstances` cases pass, with the four convert-step cases carrying `"LayoutId":"2x2"`.
- All of `Acapella.Engine.Tests` passes (regression carve-out), and the solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- The check is in `LoadFromFile`, after the `null` check and before `return`. `FromDto`, `ProjectSession`, `ProjectUndoStack` and `MainWindow.xaml.cs` are untouched (§Ordering subtleties 2).
- The check tests only that `LayoutId` is present, not its value.
- `ToDto` still sets `LayoutId = "2x2"`.
- `ProjectFileDto.LayoutId` has no initializer. Its doc comment keeps the Data Model Rule sentence and gains one line.
- The four edited #16 `InlineData` differ from the originals only by the `"LayoutId":"2x2",` prefix.

### [human]
- Repro A after the fix: File > Open on `package.json` shows `Open failed: Not an Acapella project file: package.json`. The current project, its open plugin windows and its Melodyne edit are all still there, and the title is unchanged. Ctrl+S saves the real project, and `package.json` is untouched.
- No regression: a project saved before this change (any `*.acapella.json`, or a plain `*.json` project) still opens.

---

## Runners-up (not specced)

- **Save As on a project opened as plain `foo.json` proposes `foo.json.acapella.json`.** Reasoned from code, not run.
  - **Mechanism:** `SaveProject` pre-fills `dialog.FileName = "foo.json"` (`MainWindow.xaml.cs:1048`). L7 then appends the suffix to any name that doesn't end in `.acapella.json` (`:1055-1057`).
  - **Effect:** accepting the default writes a second file with a doubled extension next to the original, and `CurrentFilePath` moves to it.
  - Cosmetic, with no data loss. A fix would strip `.json` before appending, or pre-fill the stem.
- **An out-of-range `CellIndex` in a loaded project creates more than four sidebar rows.** Reasoned only.
  - **Mechanism:** `LayerCollection.Restore` checks only the layer *count* (`LayerModel.cs:103-104`). `RestoreTracksFromLayers` then creates `max(CellIndex)+1` rows (`MainWindow.xaml.cs:791-802`). So `"CellIndex": 6` yields 7 rows in a 2x2 app.
  - It is reachable only through a hand-edited or corrupt file.
  - This is a sibling of the duplicate-`CellIndex` item already in #15's Runners-up. Both would be fixed by validating `CellIndex` (unique, 0-3) in `FromDto`, and that validation is now safe, because `FromDto` runs before the release (#16).
