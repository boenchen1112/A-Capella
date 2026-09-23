# Bug Audit #6 — Every new recording overwrites an earlier project's take in the shared `media/` folder

> Audit as of `baa9f85`, after tonight's three landings: Bug Audit #5 (`ReleaseAll` on New/Open), save affordances, and multi-file import. One bug is specced in full. The two leads handed to this pass (the stale hosted instance after an undo that reuses a `LayerId`, and Record not reusing empty rows) were both investigated. Both are real, and both rank **below** this one. They are listed with their verdicts under Runners-up at the end. They are not part of this ticket.
>
> **Not a re-report.** `reviews/Improvement_Proposal_2026-09-23.md:59` says `RecordSetupWindow` "would ... clobber `media/layerN.mkv`". It says that only as a hazard of a *hypothetical* re-record-in-place feature. Nobody has reported that today's ordinary Record flow already does this across projects, across app sessions and after undo. That is what this ticket is about.
>
> **Confidence: high on the mechanism, medium-high on how often it bites.** Every link below is read off the current source: the shared folder, the positional filename, ffmpeg's `-y`, and the absolute path stored in the project file. Nothing is inferred. The one part this pass could not run is the live dshow capture: there is no camera in a test run, and CLAUDE.md forbids touching real media. So the end-to-end repro is a **[human]** step. The fix itself is covered by a pure unit test (§Tests). Repro D's "device failed to open" variant depends on the order in which ffmpeg opens its inputs and outputs. That variant is marked *expected; verify*.

---

## The bug in one sentence

Every recording is written to `<exe dir>/media/layer{N}.mkv`, where `N` is simply how many layers the *current* project has. That folder is shared by every project the app has ever recorded, and ffmpeg is started with `-y`. So the first take of every new project silently and permanently overwrites the first take of every earlier project. The same goes for take 2, 3 and 4. Any saved project that pointed at the old file now plays the new one.

---

## Root cause

Four independently reasonable decisions compose into a data-loss defect.

### 1. One media folder for the whole install, not per project

**File:** `src/Acapella.App/MainWindow.xaml.cs:39`

```csharp
private readonly string _mediaDir = Path.Combine(AppContext.BaseDirectory, "media");
```

This was fixed this way for audit L1 (`Bug_Audit_2026-07-12.md:135`) so that it would work wherever the app is launched from. It is passed unchanged into every `RecordSetupWindow` (`MainWindow.xaml.cs:502`). It does not depend on `_session.CurrentFilePath`, on File > New or on File > Open. Every project, in every app session, records into the same directory.

### 2. The filename is positional within the *current* project

**File:** `src/Acapella.App/RecordSetupWindow.xaml.cs:138-140`

```csharp
int nextLayerId = _layers.Layers.Count;
Directory.CreateDirectory(_mediaDir);
string outputPath = Path.Combine(_mediaDir, $"layer{nextLayerId}.mkv");
```

`_layers.Layers.Count` restarts at 0 for:
- every File > New (`ProjectSession.cs:150`),
- every fresh app launch (a `new ProjectSession` starts empty, `MainWindow.xaml.cs:94`),
- any Undo that removes layers (`ProjectSession.Restore`, `:162`).

The name only has to be unique among the current project's layers, and it is. It is *not* unique within the directory it is written to. This is the same positional-id pattern that Bug Audit #5 fixed for the plugin cache, applied here to files on disk.

### 3. ffmpeg is told to overwrite without asking

**File:** `src/Acapella.Engine/Capture/FfmpegCaptureSession.cs:53` (and `:99` in the unused `StartAudioOnly`)

```csharp
psi.ArgumentList.Add("-y");
```

An existing `layer0.mkv` is truncated as soon as ffmpeg opens its output, which is effectively the moment capture starts. Nothing is written to the recycle bin and there is no backup copy. `media/` is gitignored (`.gitignore:3`) exactly as CLAUDE.md's *Recorded Media* section asks, so git history can't recover it either.

### 4. Projects store the absolute path, so the overwrite is silent

**File:** `src/Acapella.Engine/Persistence/ProjectPersistenceService.cs:58, 74`

`SourcePath = layer.SourcePath` is written and read back unchanged. A layer created by `RecordSetupWindow.StopRecording` (`RecordSetupWindow.xaml.cs:304`) stores the full `...\media\layer0.mkv`. When project A is opened later, it finds a perfectly valid file at that path. That file is just someone else's take. No error or warning is shown, and `AudioDecodeCache` (`src/Acapella.Engine/Mix/AudioDecodeCache.cs:13-21`) invalidates on mtime, so it plays the new content correctly. From the app's point of view nothing is wrong.

**Why this matters beyond a normal bug:** CLAUDE.md, Pause Rule 1 says *"Stop and ask before … Deleting recorded media"*. The *Recorded Media* section says this folder "is still protected by Pause Rule 1 … be more careful with it than with tracked code." The app itself is doing the thing the project's own rules treat as most dangerous, with no prompt at all.

---

## Repro A — two projects (the everyday case)

1. Launch the app. Record one take (⏺ → Record → Stop). This writes `media/layer0.mkv`, call it *take A*. Save as `songA.acapella.json`. Its layer 0 has `"SourcePath": "...\\media\\layer0.mkv"`.
2. File > New. Record one take. `RecordButton_Click` computes `nextLayerId = 0` and starts ffmpeg with `-y ... media\layer0.mkv`. **Take A is gone.**
3. Save as `songB`. File > Open `songA`. Layer 0 plays **song B's take**. The composite shows song B's video in song A's cell 1.

It works the same way across app restarts: step 2 can happen next week, in a fresh launch. `layer1..3.mkv` collide the same way for any two projects that each recorded at least 2, 3 or 4 takes.

## Repro B — a cancelled take still destroys the old file

After step 1 of Repro A, File > New → ⏺ → **Record**, then close the dialog straight away with ✕. `Window_Closing` (`RecordSetupWindow.xaml.cs:317-329`) stops ffmpeg, and no layer is added. But ffmpeg had already opened and truncated `layer0.mkv` in step 2. Take A is lost even though the user recorded nothing.

## Repro C — within one project: Save, Undo, re-record

Record two takes (`layer0.mkv`, `layer1.mkv`) and **Save**. Ctrl+Z removes the second layer, so `Count` is 1. Record again, which overwrites `layer1.mkv`. The saved file on disk still points at `layer1.mkv`, which now holds the new take. Reopening the saved project without saving again gives you the new take in the old project. If instead the new take fails or is cancelled (M6 or Repro B), the redo entry for the undone layer points at a truncated file.

## Repro D — the opposite failure: the M6 check accepts an old take as the new one (*expected; verify*)

`StopRecording`'s zombie-layer guard (M6, `RecordSetupWindow.xaml.cs:297`) checks only `MediaProbe.HasNonzeroDuration(_pendingOutputPath)`, meaning "does a file with a duration exist at this path". If ffmpeg exits *before* it opens its output (for example, the camera is busy or unplugged and dshow fails to open), then the previous project's `layerN.mkv` is never truncated. M6 probes it, sees a real duration, and **attaches the other project's take as the "new" recording**, with `DialogResult = true`. This relies on ffmpeg opening its inputs before its outputs, which is its normal order, but this pass hasn't run it. Treat it as "expected, verify during the **[human]** check." The fix below closes it either way, because a freshly allocated path never exists before capture.

---

## Why the current suite misses it

Nothing under `tests/` covers how capture files are named. There is no `tests/Acapella.Engine.Tests/Capture/` folder, `FfmpegCaptureSession`'s only caller is `RecordSetupWindow` (`:142`), and a WPF dialog that needs dshow devices has no automated tests. Every project/persistence test uses fake paths such as `"clip.mp4"`, so none of them ever puts two projects' media in one directory.

---

## Proposed fix

**Chosen approach: give every capture a filename that is guaranteed not to exist yet, allocated by a small pure engine helper.** Two alternatives were rejected:
- **Per-project media folders.** An untitled project has no folder yet, so they don't work until first save. They would also force a decision about moving files on Save As. That is more design than this bug needs, and it can come later.
- **Replacing `-y` with `-n`.** This is not safe on its own. On a collision, `-n` makes ffmpeg exit and leaves the *old* file in place. M6 then probes that old file, passes, and attaches the previous project's take, which is exactly Repro D. `-n` turns silent loss into silent wrong-attach. **Do not** make that change alone. It is also unnecessary once the path is guaranteed fresh. See step 3 for the defensive check to use instead.

The project file format does not change, and **no existing file is renamed, moved or deleted.** Old projects keep working because their `SourcePath`s are absolute and those files are never written to again: the new names can't produce `layerN.mkv`. Deleting or migrating the legacy `layerN.mkv` files would be a Pause Rule 1 action and is explicitly out of scope.

### 1. New file: `src/Acapella.Engine/Capture/RecordingPathAllocator.cs`

```csharp
namespace Acapella.Engine.Capture;

/// <summary>Bug audit #6: picks the output path for a new capture so that it can never overwrite an
/// existing file. media/ is shared by every project and every app session, and ffmpeg runs with -y,
/// so the old positional "layer{Count}.mkv" name silently destroyed earlier projects' takes.
/// Pure (clock and file-existence are injected) so the never-collide rule is unit-testable.</summary>
public static class RecordingPathAllocator
{
    public static string Allocate(string mediaDir, DateTime now, Func<string, bool> fileExists)
    {
        string stem = $"take-{now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(mediaDir, stem + ".mkv");
        for (int n = 2; fileExists(path); n++)
            path = Path.Combine(mediaDir, $"{stem}-{n}.mkv");
        return path;
    }

    /// <summary>Production overload: local wall clock, real filesystem.</summary>
    public static string Allocate(string mediaDir) => Allocate(mediaDir, DateTime.Now, File.Exists);
}
```

- A timestamp is used (not a Guid) so the Recordings folder stays readable when the user browses it, and so takes sort by when they were recorded. The `-2`, `-3` suffix loop handles two takes in the same second.
- The prefix is `take-`, not `layer{N}-`. The layer number is exactly the thing that is *not* stable across projects, and putting it in the name would suggest an ownership that doesn't exist.
- The custom format `yyyyMMdd-HHmmss` has no `:` or `/` separators, so the name is safe to use as a filename and doesn't depend on the user's locale.

### 2. `src/Acapella.App/RecordSetupWindow.xaml.cs` — use it in `RecordButton_Click`

Replace only the filename line (`:140`):

```csharp
int nextLayerId = _layers.Layers.Count;                           // unchanged: still drives the guide-track branch (:146) and status text (:176, :181)
Directory.CreateDirectory(_mediaDir);
string outputPath = RecordingPathAllocator.Allocate(_mediaDir);   // bug audit #6: never an existing file
```

- Keep `nextLayerId` exactly as it is. It is still the right value for `if (nextLayerId > 0)` ("are there earlier layers to play as a guide", `:146`) and for the "Recording layer N" status text. Only its use as a *filename* was wrong.
- The allocation must stay **inside `RecordButton_Click`**, so it runs on every Record click. M6's "retry from this same dialog" path (`:297-302`) then gets a fresh path each time, instead of re-probing a failed take's leftover file.
- `_pendingOutputPath = outputPath;` (`:200`) is unchanged, and so `StopRecording` → `_layers.Add(LayerKind.RecordedAV, _pendingOutputPath)` (`:304`) needs no edit.

### 3. Defensive check, same method, immediately before either `_activeCapture.Start(...)` call

```csharp
// Bug audit #6: -y would silently destroy an existing take, and without -y M6 would accept the
// old file as the new take. The allocator already guarantees a fresh path; this only guards a race.
if (File.Exists(outputPath))
{
    StatusText.Text = $"Refusing to overwrite existing recording {Path.GetFileName(outputPath)}; try again.";
    return;
}
```

Put it once, right after the allocation and **before** `_activeCapture = new FfmpegCaptureSession();` (`:142`), so no capture object is created on refusal. Leave `-y` in `FfmpegCaptureSession` unchanged. This ticket doesn't touch the engine's capture arguments.

### 4. Deliberately **not** changed

- `_mediaDir` location (`MainWindow.xaml.cs:39`), the project file format, and `ProjectPersistenceService`.
- `FfmpegCaptureSession`'s arguments (see the `-n` note above).
- Existing `media/layer0..3.mkv` files: no rename, migration or cleanup (Pause Rule 1).
- **Leftover files from failed or cancelled takes.** They still accumulate, as today, now under unique names. Tools > Recordings folder size (`MainWindow.xaml.cs:483-495`) already reports them without deleting them, and that stays as it is. Any cleanup would be a separate, user-confirmed ticket.
- The `MainWindow.xaml.cs:479` doc comment that says "recorded layerN.mkv files": update the wording to "recorded take files" in the same commit. It's one line and not a behaviour change.

---

## Tests — `tests/Acapella.Engine.Tests/Capture/RecordingPathAllocatorTests.cs` (new folder, new file)

These are plain `[Fact]`s. No devices, no ffmpeg, no WPF. The pure overload takes a fixed `DateTime` and an `exists` predicate. Test 1 also uses a real temp directory, so the production overload is exercised against a real filesystem. Clean it up in `Dispose`, the same way `ProjectSessionTests` cleans up its `_tempFiles`.

1. **`Allocate_NeverReturnsALegacyPositionalRecording`**: this is the regression. Create a temp dir containing empty files `layer0.mkv` … `layer3.mkv`, which is exactly what every existing install has. Call `RecordingPathAllocator.Allocate(dir)` → `Assert.False(File.Exists(result))`, `Assert.Equal(dir, Path.GetDirectoryName(result))`, `Assert.EndsWith(".mkv", result)`. The old rule `Path.Combine(dir, $"layer{0}.mkv")` returns an existing file for this directory. The test guards against anyone "simplifying" back to it.
2. **`Allocate_SameSecondCollision_AppendsASuffix`**: fixed `now = new DateTime(2026, 9, 23, 22, 15, 30)`. `exists` returns true for `take-20260923-221530.mkv` and `take-20260923-221530-2.mkv` → the result is `take-20260923-221530-3.mkv`.
3. **`Allocate_NoCollision_UsesTheBareTimestampName`**: `exists = _ => false` → `Path.Combine(dir, "take-20260923-221530.mkv")`. This pins the format so that the Recordings folder stays sortable.
4. **`Allocate_ConsecutiveTakes_NeverShareAPath`**: in a real temp dir, call `Allocate(dir, fixedNow, File.Exists)`, then `File.WriteAllBytes(result1, [])`, then call again with the **same** `fixedNow` → `result2 != result1`, and `!File.Exists(result2)`. This mirrors two takes in the same second: M6's retry, or a quick cancel-and-record-again.

**No automated test for the `RecordSetupWindow` wiring, deliberately.** It needs a real camera and microphone through dshow, which a test run doesn't have. The wiring is a one-line call site plus a guard, and it is **[human]**-verified below. The same reasoning is used in `Feature_Spec_2026-09-23_MultiFileImport.md`'s tests section.

**Regression carve-out (per CLAUDE.md):** no engine code already under test changes. The only App-side change is in `RecordSetupWindow`, which has no automated tests. Re-run `Acapella.Engine.Tests` once anyway, because the new test folder sits in that project.

---

## Acceptance criteria

- **[auto]** The 4 `RecordingPathAllocatorTests` pass. The existing `Acapella.Engine.Tests` still pass. The solution builds with no new warnings.
- **[human]** (needs camera and mic; make a note of the Recordings folder's file list before starting, and **do not delete anything in `media/`**):
  - Repro A: record in project A and save. File > New, record, and save as B. Reopen A: **A plays its own take.** `media/` holds two `take-….mkv` files, and every pre-existing `layerN.mkv` is byte-for-byte untouched (same size and modified time as before).
  - Repro B: File > New → ⏺ → Record → close the dialog immediately. No existing file's size or modified time changes.
  - M6 retry: with the camera unplugged (or in use by another app), press Record. You should see "Recording failed: …" and **no layer is attached** (this checks Repro D). Plug the camera back in and press Record again in the same dialog: you get a new `take-…` file and the layer attaches.
  - A normal take still attaches, plays, and exports as it did before. The guide track still plays for take 2 and later.

---

## Runners-up (not part of this ticket)

- **#2 — ⏺ / Tools > Recording setup / Tools > Calibrate latency append a row before the dialog opens and never take it back. This is a functional bug, not only a UX inconsistency.**
  - `RecordingSetupMenuItem_Click` (`MainWindow.xaml.cs:520-533`) does `_tracks.Add(new row)` *before* `OpenRecordSetupForRow`. A cancelled dialog therefore leaves a stray empty row, and nothing pushes undo for it, so it isn't recorded anywhere.
  - `MainWindow.xaml:179` wires **Calibrate latency...** to the same handler. Every calibration therefore adds an empty row.
  - After four cancelled or calibrate-only uses, ⏺, Recording setup and Calibrate all refuse with "Layer cap reached (4)." and "+ Add layer" is disabled, **with zero layers in the project**. The per-strip Rec button, Upload and Import still work.
  - The ⏺ hint "for the next empty layer" (`MainWindow.xaml:207`, and the doc comment at `MainWindow.xaml.cs:800-801`) is inaccurate: it always appends a row and never reuses an empty one.
  - Fix: reuse the lowest empty row, or append only on success. Point Calibrate at a dialog open that doesn't create a row.
- **#3 — Stale hosted instance after an undo frees a `LayerId` (the multi-file spec's known limitation). It's real and reachable, but usually harmless.**
  - `LayerCollection.Add` (`LayerModel.cs:74`) gives the next layer the undone layer's id. Undo never releases the `(LayerId, Stage)` instance (`ProjectSession.cs:104-109`; `ReleaseAllHostedInstances` runs only at `:137` and `:149`).
  - In a normal undo walk-back, the restore of the step recorded by the *first* editor poll pushes that baseline blob into the instance. Because `OpenHostedEditor` seeds `null` (`LayerRowViewModel.cs:321`), the first poll records the plugin's state at open time. The orphaned instance therefore ends up at roughly its default state before any new layer reuses it.
  - Visible contamination needs one of two things:
    - The user keeps tweaking the plugin's editor window, **which stays open after its layer is undone**, before adding a new layer. The upload's `CommitEdit` → `SyncLiveStateIntoParameters` then pulls those tweaks into the new, unrelated layer.
    - The user tweaks before the first 500 ms poll.
  - Fix later: release a `LayerId`'s instances, and its ARA session, when a restore drops that id.
