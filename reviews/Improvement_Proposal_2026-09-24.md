# Improvement Proposal — 2026-09-24

> A scan of the app for product improvements, as of `4aac48e`. That commit comes after save affordances, multi-file import, and the Bug Audit #5–#8 fixes. **This doc covers enhancements only.** Bugs are a separate track. Real bugs noticed in passing are listed as one-line asides at the end.
>
> There are four candidates, ranked best-first, and each fits in one focused implementation session. None of them touches a Pause Rule 2 decision: no new layouts, formats, vendors, dependencies or layers. One candidate (#2) adds optional fields to the project file. They are additive and discard nothing, and its scope note says so.

## How this list relates to `Improvement_Proposal_2026-09-23.md`

- **09-23 #1 (save affordances):** done, so it is not re-proposed.
- **09-23 #4 (replace a layer's source):** re-surfaced as **Candidate 1** below and re-scoped against current code. The old "would clobber `media/layerN.mkv`" hazard is gone since Bug Audit #6. Re-record now has other load-bearing details that the old note didn't list.
- **09-23 #3 (set trim in/out from the playhead):** **superseded by Candidate 2.** As written, it would put the cut in the wrong place. Why is explained in Candidate 2's *Why*.
- **09-23 #2 (export progress):** re-surfaced as **Candidate 3**. Bug Audit #8 gives it a stronger argument than it had yesterday.

`UI_Design_Spec.md` is still v2, which is superseded by the shipped v8 single-screen layout. Every claim below comes from the current code, not from that spec.

---

## Candidates

### 1. Retake a layer in place: re-record or replace with a file, keeping the slot and its FX

**What:** Give a populated mixer strip a "Retake" affordance, a small button under the strip name, with two actions:
- **Re-record…** opens the recording dialog for *this* slot.
- **Replace with file…** opens a file picker.

Either action swaps `SourcePath`/`Kind` on the **existing** `LayerModel`. It resets `TrimStartMs`/`TrimEndMs` and `CalibratedOffsetMs`: a re-record gets the freshly measured value and a file replace gets 0. It keeps `LayerId`, `CellIndex`, `Name` and every mix/FX parameter, and it is one undo step.

**Why:**
- **A filled slot is permanent.** The Rec/Upload buttons exist only while `HasSource` is false (`src/Acapella.App/MainWindow.xaml:86-92`). `LayerCollection` has only `Add`, `Restore` and `RemoveLast` (`src/Acapella.Engine/Project/LayerModel.cs:67-100`), and `RemoveLast` has no caller. Its doc comment still points at a `StopRecordButton_Click` that no longer exists (`:94-95`). `HostedPluginService` states outright that "this project has no layer-removal feature yet" (`src/Acapella.Engine/Host/HostedPluginService.cs:124-125`).
- **Undo is the only way out, and it throws away later work.** Undo steps back through whole-project snapshots in order (`ProjectSession.CommitEdit`, called from `MainWindow.xaml.cs:197-201`). Getting rid of a flubbed take 2 means also undoing takes 3–4 and every mix edit made after it.
- **Retakes are the core loop.** The app's whole workflow is "sing it four times", so a bad take with no retake path is the roughest edge in that loop.
- **Retake is also safe to undo now.** Recordings get fresh timestamped names and are never overwritten (`RecordingPathAllocator.Allocate`, `src/Acapella.App/RecordSetupWindow.xaml.cs:140`, Bug Audit #6). Undoing a retake therefore brings back the old take, whose file is still on disk.

**Effort:** Small for Replace-with-file. Medium once Re-record is included.

**Scope note:**
- **Replace with file** is cheap. `Kind` and `SourcePath` are settable (`LayerModel.cs:15-16`). The hosted-plugin cache is keyed on `(LayerId, Stage)`, so the layer's FabFilter chain survives the swap untouched. The Melodyne ARA source re-registers automatically when the audio content changes (`HostedPluginService.cs:146-165`). That drops the old take's pitch edits, which is right for a new take. The auto-pitch cache key includes the path (`LayerModel.cs:52-56`). Because `row.Layer` is the same object after the swap, the row needs an explicit property refresh, for `IconGlyph`, `SourceStateLabel` and the trim fields (`src/Acapella.App/ViewModels/LayerRowViewModel.cs:73-95, 413-438`). The kind detection in `AttachUploadedFile` (`MainWindow.xaml.cs:579-590`) can be reused.
- **Re-record** needs five changes in `RecordSetupWindow`, all visible in current code:
  1. **The guide mix must leave out the layer being replaced.** Today it is built from *all* layers (`:162-164`), so the singer would hear the bad take in their headphones while re-singing it.
  2. **The guide condition must count the other layers.** The `nextLayerId > 0` test (`:154`) should become "any *other* layer exists".
  3. **The cap check needs a replace branch.** Without one, re-record is refused on a full project (`:130`), which is exactly when it's most needed.
  4. **`StopRecording` needs a replace branch.** It calls `_layers.Add` unconditionally (`:312`). In replace mode it should instead hand back the new path and measured offset.
  5. **`OpenRecordSetup` needs a replace path.** It assumes `CreatedLayer` is a new layer (`MainWindow.xaml.cs:512-526`), so it needs a replace path beside the attach path.
- **Deliberately not proposed: "Clear/remove layer."** Clearing a middle slot leaves a gap in the grid, and the compositor compacts gaps. `Layout2x2Provider.GetCellRects` returns the first *N* rects (`src/Acapella.Engine/Composite/Layout2x2Provider.cs:11-20`), as already noted in `Bug_Audit_2026-09-23_RecordRowReuse.md:112`. Clearing also frees a `LayerId` that the next `Add` would duplicate (`LayerId = _layers.Count`, `LayerModel.cs:74`). Replace-in-place avoids both problems.

---

### 2. Song in/out: set a project-wide start and end from the playhead, applied at export

**What:** Add two project-level markers, **In** and **Out**:
- **Set In** and **Set Out** buttons (plus `I`/`O` keys) sit beside the existing −5s / Restart / +5s row, and a **Clear** button resets them.
- A readout shows both points to 0.1 s.
- Export renders only `[In, Out]` of the finished mix and grid.

The cut is made in **project time, after the mix**. It never writes per-layer trims.

**Why:**
- **Every take has dead air at both ends.**
  - Layer 1 starts recording with no guide (`RecordSetupWindow.xaml.cs:186-190`).
  - For later layers the guide and metronome start together with capture (`:179-199`), with no count-in.
  - Each take ends when the singer reaches over and clicks *Stop Recording* in the dialog (`:116-121`).
  - Export always starts at frame 0 (`src/Acapella.Engine/Export/ExportEngine.cs:68-70, 85`) and runs to the longest layer plus its reverb tail (`src/Acapella.Engine/Timeline/LayerTimeline.cs:40-57`).
  
  So every exported video opens with four people waiting and closes with four people reaching for the mouse.
- **The only tool today is per-layer trim, and it's the wrong one.** Trimming means typing ms into eight tiny text boxes (`MainWindow.xaml:110-119`) while the only readout is `h:mm:ss` (`FormatTime`, `MainWindow.xaml.cs:883-887`).
- **09-23 #3's "write the playhead into trim" is also wrong, for two reasons:**
  - Trim is applied *before* sync shift (`LayerTimeline.cs:64-65`). Trimming one layer's head therefore moves it against the others.
  - The playhead is in project time but trim is in source time. The mapping between them depends on the layer's shift (`LayerTimeline.VideoWindowAt`, `:90-97`), and every recorded layer has a nonzero shift (`CalibratedOffsetMs` is measured per take, `RecordSetupWindow.xaml.cs:313`, fed into `GetShiftMs`, `LayerModel.cs:46`). Writing `PositionMs` into `TrimStartMs` would cut in the wrong place.
  
  A post-mix range has neither problem: every layer is cut at the same instant the user is looking at.

**Effort:** Small–medium.

**Scope note:**
- **Engine.** `ExportEngine.Export` gains optional `rangeStartMs`/`rangeEndMs` parameters. Existing tests stay source-compatible.
  - **Audio:** slice the already-fully-rendered `mixedSamples` buffer (`ExportEngine.cs:51-58`).
  - **Video:** start each frame source at In through the existing `positionMs` parameter (`LayerTimeline.FrameSource`, `LayerTimeline.cs:72`, called at `ExportEngine.cs:69`). This makes seeking into the middle of the grid a solved problem.
  - **Frame count:** compute `totalFrames` (`:75`) from the range.
- **Project.** Add two nullable doubles to `ProjectSession` and `ProjectFileDto` (`src/Acapella.Engine/Persistence/ProjectFileDto.cs:9-27`), and return them from `SnapshotForExport` (`ProjectSession.cs:90`).
- **This is a project-file format change, but additive only.** An old file loads with both fields null, meaning "whole song". Nothing is discarded, so Pause Rule 1 is not triggered.
- **Undo and dirty tracking come free**, because setting a marker goes through `PushUndoSnapshot()`.
- **Tests.** An **[auto]** test exports with a range and asserts the probed duration ≈ Out − In.
- **Preview.** Preview doesn't have to enforce the range for v1. The readout is enough. Making Restart/Home jump to In is optional polish.
- **Tails.** A reverb tail past Out is simply cut. That's what the user asked for, so no fade is added.

---

### 3. Export progress in the status bar

**What:** Give `ExportEngine.Export` an optional `IProgress<double>` and show a determinate progress bar, plus an `x / y frames` readout, in the status bar while an export runs. Report a coarse "Mixing audio…" phase first.

**Why:**
- **Export is the app's payoff step, and it's silent.** It writes `"Exporting..."` once (`MainWindow.xaml.cs:1034`) and then nothing until the background task finishes (`:1038-1054`). Behind that one line are two long stretches: a full-length mixdown through every hosted FabFilter chain and any Melodyne render (`ExportEngine.cs:48-58`), then a frame-by-frame decode → composite → pipe loop (`:85-90`). The loop already knows `totalFrames` (`:75`).
- **New since yesterday: the silence now also locks the app.** Bug Audit #8 made everything else refuse to run during an export, each with "Finish the export first.":
  - Play (`:794-798`)
  - Record (`:502-506`)
  - File > Open (`:974-978`)
  - File > New (`:1061-1065`)
  
  A user who presses Play mid-export gets refused with no way to tell how long they'll wait, or whether the export has hung.

**Effort:** Small.

**Scope note:**
- **Threading.** Build the `Progress<double>` on the UI thread *before* `Task.Run` (`MainWindow.xaml.cs:1038`), so its callbacks marshal back automatically with no extra `Dispatcher.Invoke`.
- **Rate.** Report about once per second of output (every `fps` frames), not every frame, so the dispatcher isn't flooded.
- **Placement.** The bar goes in the existing status-bar `Border` (`MainWindow.xaml:263-265`) and collapses when the export finishes.
- **Tests.** An **[auto]** test checks that the reported values are monotonic and end at 1.0.
- **Compatibility.** The optional parameter keeps every existing `ExportEngine` test source-compatible.

---

### 4. Remember the camera, mic and metronome choice between takes

**What:** Pre-select the camera and microphone used for the last take, matched by dshow device *name*, and fall back to index 0 if that device is gone. Carry the Metronome on/off state forward the same way BPM already is.

**Why:**
- **Every take starts from device 0.** A new `RecordSetupWindow` is built for every take (`MainWindow.xaml.cs:508`), and `RefreshDevices` always selects index 0 for both pickers (`RecordSetupWindow.xaml.cs:74-77`).
- **Most singers will need to change it.** On a typical setup the webcam's built-in mic often lists before a USB or interface mic. The singer has to re-pick the mic on each of the four takes of every project.
- **Forgetting ruins the take without warning.** A take recorded on the wrong mic still passes the M6 validity check, which only tests for nonzero duration (`:305`). Nothing flags it until playback.
- **Metronome state is inconsistent.** The Metronome checkbox resets to off every time (`RecordSetupWindow.xaml:34`), while BPM is already carried in and back out (`MainWindow.xaml.cs:508-510`, `RecordSetupWindow.xaml.cs:49`).

**Effort:** Small.

**Scope note:**
- **Minimal version.** Keep the last choice in fields on `MainWindow`, and pass it into the dialog constructor and read it back afterwards, exactly like `Bpm`. This fixes the four-takes-in-a-row case.
- **Optional extension: remember across launches.** Add a `LastCameraName`/`LastMicName` pair to `AppSettings` (`src/Acapella.Engine/Settings/AppSettings.cs:3-9`). This is per-machine `settings.json`, not the project file, and `System.Text.Json` tolerates the missing fields in an older settings file.
- **Why match by name.** Names are exactly what reaches ffmpeg (`video={name}:audio={name}`, `src/Acapella.Engine/Capture/FfmpegCaptureSession.cs:63`), so matching by name is stable across reboots in a way that index order is not.

---

## Also noticed (one line each, not candidates)

- **Runner-up enhancement: the preview frame doesn't match the export frame.** Preview composites at 640×480, which is 4:3 (`MainWindow.xaml.cs:95`, `MainWindow.xaml:295`). Export uses 1280×720, which is 16:9 (`ExportEngine.cs:36`). Both letterbox (`LayerFrameSource.cs:75-77`), so a 16:9 webcam shows bars in the preview but fills its cell in the MP4. A phone video filmed upright is boxed differently in each. Moving the preview to a 16:9 canvas is a small WYSIWYG fix.
- **Rename is gone.** The layer-rename UI was dropped in the v8 redesign (`bd6affe`), although v5 P2 task 1 had approved it (`Acapella_Build_Plan_v5.md:71`). Its handler `LayerNameTextBox_LostFocus` (`MainWindow.xaml.cs:257`) is now orphaned. Names still show on the preview labels, the FX header and the plugin window titles, but they can't be edited.
- **Clicking the timeline doesn't seek.** `TimelineSlider` (`MainWindow.xaml:255-259`) doesn't set `IsMoveToPointEnabled`. A click on the track only moves the value by the default `LargeChange` of 1, which is 1 ms. Only dragging the thumb actually seeks.
- **Possible bug: editing a trim may wipe that layer's Melodyne edits.** The ARA content key is hashed from the *post-trim* samples (`MelodyneAraPitchCorrector.cs:45`), and any change to that key releases and re-registers the audio source (`HostedPluginService.cs:156-161`). Not verified at runtime.
- **Known bug:** the grid-gap compaction is already recorded in `Bug_Audit_2026-09-23_RecordRowReuse.md:112` but was never ticketed.

---

## Recommendation

**Candidate 1 (retake in place).** Every other candidate makes a working flow smoother. This one fills the only hole in the core "sing it four times" loop that has no workaround short of undoing all later work. It is also cheaper than it looks now: timestamped recording files make a retake undoable, and keeping `LayerId` stable carries the slot's FX chain over for free. If a smaller first step is wanted, ship Replace-with-file alone (small), then add Re-record with its guide-mix exclusion.
