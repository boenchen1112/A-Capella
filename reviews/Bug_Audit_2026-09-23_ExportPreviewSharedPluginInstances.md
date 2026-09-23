# Bug Audit #8: Export runs while preview (or the record dialog's guide) is still playing through the same live plugin instances

> Audit as of `ddfd741`, after tonight's landings: Bug Audit #7 (Record/Calibrate reuse free rows, `ddfd741`) and everything before it. One new bug is specced in full. It has not been flagged in any earlier `reviews/*.md` doc.
>
> **Runner-up #3 from Bug Audit #7** (a stale hosted instance after an undo frees a `LayerId`) was **not re-specced**, as instructed. Bug Audit #5 wrote down a decision to leave it. My recommendation is unchanged: this bug is more important, because it needs no unusual edit sequence. A user only has to press Export while the song is playing. See *Runner-up #3: still left open* at the end.
>
> **Confidence: high on the mechanism, and on the deterministic symptom.** When Export builds its chain, it calls `Reset()` on the instance the running preview is using, so a ringing reverb tail cuts off at once. Every link below comes from reading current source.
> - **Medium** on how *audible* the second symptom is in a given MP4: export and preview interleave `ProcessBlock` calls on one instance, so each mixes state into the other. How much you hear depends on the plugin: a lot for Pro-R 2's tail or a lookahead limiter, very little for a zero-latency EQ.
> - The third consequence is a data race in native code, and a crash from it is **possible but not demonstrated**. Two threads calling `processBlock` on one VST3 instance at the same time is undefined behaviour.
>
> The fix only touches the App (three gates in `MainWindow.xaml.cs`), and one of its two failure directions has a red-then-green headless test. There are **two ordering rules** in the fix (§Proposed fix, subtleties 1 and 2). Each is spelled out with a counter-example, because a quick implementation gets both wrong.

---

## The bug in one sentence

Preview playback, MP4 export and the Record dialog's guide mix all process audio through the **same live hosted-plugin instances**, and nothing stops two of them running at once. Export doesn't stop preview, and while an export is running, Play/Space and ⏺ are not blocked. So:
- an export started during playback resets the preview's FabFilter/Melodyne instances mid-song;
- the two audio graphs then feed each other's audio through one plugin state, so the MP4 can get reverb/limiter/filter state from the preview's song position (and the preview from the export's);
- `processBlock` runs on two threads at once on a native object that isn't safe for that.

---

## Root cause

Five facts combine to produce the defect.

### 1. There is one hosted-plugin service, shared by preview, export and the record dialog, by design

**File:** `src/Acapella.App/MainWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:47` | Creates `_hostedService`, the single `HostedPluginService`. |
| `:93` | `_mixEngine = new MixEngine(_hostedService)`. This engine is handed to the record dialog at `:502`. |
| `:95` | `_previewEngine = new PreviewPlaybackEngine(..., hostedService: _hostedService)`. Internally it builds another `new MixEngine(hostedService)` (`PreviewPlaybackEngine.cs:151`). |
| `:1015` | `new ExportEngine(hostedService: _hostedService)`. Internally, `ExportEngine.cs:32` builds `new MixEngine(hostedService)`. |

`HostedPluginInstanceCache` is keyed by `(LayerId, Stage)` (`HostedPluginInstanceCache.cs:13`). `GetOrAdd` (`:25-34`) returns the existing instance if there is one. This is the audit #3 A1 design: one live plugin per layer and stage, so what the user sets in the editor is what they hear *and* what gets exported. The test `MixEnginesSharingOneService_GetTheSameLiveInstance` (`tests/Acapella.Engine.Tests/Mix/HostedChainTests.cs:114-127`) locks that sharing in.

The export snapshot keeps the layer ids (`ProjectPersistenceService.cs:72`, `LayerId = dto.LayerId`, reached through `ProjectSession.SnapshotForExport`, `ProjectSession.cs:90-94`). So the export's deep copy of the layers resolves to **the same native instances** the preview is using. That part is intended. It's only safe if the graphs never run at the same time, and nothing enforces that.

### 2. Every chain build resets the instance it wires in

**File:** `src/Acapella.Engine/Mix/FxSlot.cs:69-70`

```csharp
var instance = hosted.GetOrCreateInstance(layerId, Stage, PluginLabel, GetHostedState(parameters), sampleRate);
hosted.Reset(instance);   // audit A5: don't replay the last run's buffered audio
```

The reset exists so that a second Play doesn't bleed the previous run's buffered audio (audit A5). It goes through `HostedPluginService.Reset` (`HostedPluginService.cs:101`) to `aca_reset` → `instance->reset()` (`HostBridge.cpp:303-307`). **It assumes nobody else is currently using the instance.** Export's `BuildMix` (`ExportEngine.cs:49`) goes through `MixEngine.BuildLayerChain`/`InsertSlots` (`MixEngine.cs:173-240`) and ends up here for every enabled hosted slot. That clears the reverb tail, delay lines and lookahead buffers of the instance the preview is playing through right now.

### 3. After that, both graphs call `ProcessBlock` on the same instance, from different threads

`ProcessBlock` is the only hosted call that isn't marshalled to the WPF dispatcher (`HostedPluginInstance.cs:103-106`, doc comment on `:109`). It is called from `HostedPluginSampleProvider.cs:129`, on whichever thread is pulling that graph:
- **Preview:** the WASAPI render thread, driven by `_audioSink.Play(tracked)` (`PreviewPlaybackEngine.cs:328`, inside `StartAudio` `:314-330`).
- **Export:** the `Task.Run` thread (`MainWindow.xaml.cs:1011`). The mixdown loop (`ExportEngine.cs:51-58`) pulls as fast as the CPU allows, so the whole song passes through the plugins in a few seconds of wall time.
- **Record dialog:** the UI thread, for `RenderMonoReference(_mixEngine.BuildMix(...))` (`RecordSetupWindow.xaml.cs:172`, `:217-230`, a full synchronous render). Also the WASAPI render thread, for the guide mix (`:162-164` builds it, `:182` plays it).

A plugin instance has **one** internal state: its filter memories, reverb tank, lookahead queue and latency delay line. When two graphs feed it alternately, each block one graph gets out is computed from state the *other* graph's audio left behind. On top of that, the native bridge has one set of buffers per instance, not per call:

**File:** `src/Acapella.Host.Native/src/HostBridge.cpp:319-329`

```cpp
auto* l = h->scratch.getWritePointer(0);          // one scratch buffer per instance
auto* r = h->scratch.getWritePointer(1);
memcpy(l, inL, sizeof(float) * (size_t) numSamples);
memcpy(r, inR, sizeof(float) * (size_t) numSamples);
AudioBuffer<float> block(h->scratch.getArrayOfWritePointers(), 2, numSamples);
h->midi.clear();                                  // one MidiBuffer per instance
h->instance->processBlock(block, h->midi);
```

If two threads are inside this function at the same time, each can `memcpy` its input over the other's before `processBlock` runs. Then one caller gets the *other* graph's processed audio back, and `processBlock`/`reset` race inside the plugin (undefined behaviour). The comment near `HostBridge.cpp:166` says `processBlock` has no *message-thread* affinity. That's true, but it says nothing about two audio threads at once, and the VST3 contract doesn't allow it.

### 4. Export doesn't stop preview; New and Open do

**File:** `src/Acapella.App/MainWindow.xaml.cs:995-1028`

```csharp
private void ExportButton_Click(object sender, RoutedEventArgs e)
{
    if (_layers.Layers.Count == 0) { ... return; }                      // :997-1001
    var dialog = new SaveFileDialog { ... };                            // :1003
    if (dialog.ShowDialog() != true) return;                            // :1004
    StatusText.Text = "Exporting...";                                   // :1006
    ExportMenuItem.IsEnabled = false;                                   // :1007  the "export in flight" flag
    var (snapshotLayers, snapshotMasterVolumeDb) = _session.SnapshotForExport();   // :1009
    Task.Run(() => { ... new ExportEngine(hostedService: _hostedService) ... });  // :1011-1027
}
```

There's no `StopAsync`. Compare Open (`:980`) and New (`:1044`), which both `await AwaitPreviewCommand(_previewEngine.StopAsync())` before they touch hosted instances. Bug Audit #5 §5 added those for the release/use-after-free case. Export has the same hazard in a milder form (reset and interleaving rather than release), and it was not included.

### 5. While an export is running, nothing that starts a second graph is blocked

In the whole App, `ExportMenuItem.IsEnabled` (the only "export in flight" signal) is read or written at exactly four places:
- `:962`, the gate in Open;
- `:1007`, where Export sets it;
- `:1025`, where Export clears it;
- `:1034`, the gate in New.

Nothing else checks it:
- **`PlayButton_Click`** (`:786-810`) checks `IsPlaying` (`:788`) and the layer count (`:790-794`), and then starts playback. Space goes to the same handler (`:268-270`).
- **`OpenRecordSetup`** (`:500-521`) opens the record dialog without any check. It is reached from:
  - toolbar ⏺ (`:830` → `RecordingSetupMenuItem_Click` `:528-537`);
  - Tools > Recording setup... and Tools > Calibrate latency... (`:542-546`);
  - a strip's own Rec button (`:476` → `OpenRecordSetupForRow` `:524`).

  The dialog builds a guide mix (reset, plus render-thread `ProcessBlock`) and renders a mono reference (UI-thread `ProcessBlock`), both on the same instances the export is using.

The reverse direction, starting Export while the record dialog is open, can't happen: the dialog is modal (`ShowDialog` with `Owner = this`, `:502-503`), so the main menu can't be reached. Export while preview plays (fact 4) and preview/record while export runs (fact 5) are the two holes.

---

## Repros

All the repros need a FabFilter plugin actually hosted. With `NoHostedPluginsAvailable`, every slot falls back to native or passthrough DSP (`FxSlot.cs:66-67`). Those build fresh per chain and aren't shared, so the bug can't happen there.

### A. Export during playback cuts off the preview's reverb and changes the MP4 **[human], no camera**
1. Upload one audio/video file as layer 1. In its FX panel, enable **Reverb** with the Pro-R 2 backend. Open the editor and set a long decay (4 s or more) and a high mix.
2. Press ▶. Wait for a loud phrase to end so the reverb tail is clearly ringing.
3. While the tail rings, choose File > Export MP4... and save to `a.mp4`.
   - **Observed:** the moment the Save dialog closes, the preview's reverb tail **cuts out** (the export's `Reset`, §Root cause 2). The preview keeps playing, possibly with clicks or odd reverb for the few seconds the mixdown runs.
4. Press ■. Export again, to `b.mp4`, with preview stopped the whole time.
   - **Observed:** `a.mp4` and `b.mp4` sound different around the time the mixdown overlapped with preview. For Pro-R 2 this shows up as reverb that doesn't match the dry signal. Null-testing the two audio tracks in any DAW makes the difference obvious. `b.mp4` is the correct one.
   - **Expected after fix:** step 3 stops preview (▶ goes back to Play) *before* the mixdown starts. `a.mp4` and `b.mp4` then null except for any randomness inside the plugin itself.

### B. Space during an export's mixdown **[human], no camera**
1. The same project as A, with preview stopped. Export to `c.mp4`.
2. While the status says "Exporting...", press Space (or ▶).
   - **Observed:** preview starts. Its chain build resets the instances *the export is in the middle of using*. Depending on timing, `c.mp4` has a reverb/limiter discontinuity at the point where Play landed.
   - **Expected after fix:** status "Finish the export first.", and preview doesn't start.

Doing this with Space and a long song makes the timing easy: the mixdown of a 3-4 minute song with two or three FabFilter slots takes long enough to hit.

### C. ⏺ during an export **[human], camera**
1. A project with one layer that has a hosted FX enabled. Start an export.
2. While it runs, press ⏺ and then Record in the dialog.
   - **Observed:** the dialog's guide mix build resets the export's instances, and `RenderMonoReference` (UI thread) and the guide (render thread) call `ProcessBlock` on them in parallel with the export thread. The MP4 gets the discontinuity and cross-feed.
   - **Expected after fix:** ⏺, Recording setup..., Calibrate latency... and the strip Rec buttons all refuse with "Finish the export first.", and no dialog opens.

### D. Play is refused during an export **[auto]**
This is the headless side of B, in `tests/Acapella.App.Tests/ExportPlaybackGateTests.cs` (§Tests): with `ExportMenuItem.IsEnabled = false`, raising `PlayButton`'s `Click` must leave `StatusText.Text == "Finish the export first."`. Before the fix it reads "Add at least one layer first." (the empty-project check at `:790-794`), so the test is red-then-green.

### Race-free evidence the mechanism is real (explanation only, not a required test)
This shows the cross-feed without threads. Take two `MixEngine`s on one `FakeHostedPluginFactory.CreateService()` service (`tests/Acapella.Engine.Tests/Host/FakeHostedPlugin.cs`), with a Limiter slot on a fake with latency 64. Feed chain A all ones and chain B all zeros, and read them alternately, one block at a time, on one thread. After B's `LatencySkipSampleProvider` skip, B's output contains ones and A gets runs of zeros. Both chains are pushing through the one shared delay queue. This isn't proposed as a test, because the fix is an App-level "one graph at a time" rule, not an engine change (see *Rejected alternatives*).

---

## Why the suite misses it

- **Engine tests are sequential.** Every test in `HostedChainTests.cs` builds a chain and reads it to the end before building the next one. `MixEnginesSharingOneService_GetTheSameLiveInstance` (`:114-127`) checks that the instance is *shared*, which is intended, but never pulls two live chains at the same time.
- **Export and preview tests use no hosted plugins.** Every `ExportEngine` test (`tests/Acapella.Engine.Tests/Export/ExportEngineTests.cs:81, 132, 177, 221, 266`) and every `PreviewPlaybackEngine` test (`tests/Acapella.Engine.Tests/Preview/PreviewPlaybackEngineTests.cs:275` onward) passes `NoHostedPluginsAvailable.Instance`. With nothing shared, the graphs are independent.
- **No App test drives transport or export.** The App test project has two UI tests (`MixingScreenTests`, `TransportLayoutTests`), and they check layout and bindings. None of them clicks Play, Export or ⏺. Export's `SaveFileDialog` is modal and can't be driven headlessly, so nobody wrote a test that runs both at once.
- **The symptom is audio-only and depends on timing.** A missed reset or a few interleaved blocks don't throw, don't change file sizes or durations, and don't show up in any assertion the suite makes on an exported file.

---

## Proposed fix

**Rule: one audio graph at a time.** While an export is running, nothing else may build or pull a chain on the shared hosted instances. Starting an export stops the preview first. This is the same rule New/Open already follow, and it uses the same flag and the same status text.

The change is App-only, all in `src/Acapella.App/MainWindow.xaml.cs`. There are no engine, native or `RecordSetupWindow` changes.

### Step 1: Export stops preview before it snapshots, and refuses while a Play is starting

```csharp
private async void ExportButton_Click(object sender, RoutedEventArgs e)   // was: void
{
    if (!ExportMenuItem.IsEnabled)                   // defensive; the menu item is disabled anyway
    {
        StatusText.Text = "Finish the export first.";
        return;
    }

    if (_layers.Layers.Count == 0)
    {
        StatusText.Text = "Add at least one layer before exporting.";
        return;
    }

    var dialog = new SaveFileDialog { Filter = "MP4 video|*.mp4", DefaultExt = ".mp4" };
    if (dialog.ShowDialog() != true) return;

    // Bug audit #8, subtlety 2: checked AFTER the Save dialog returns, not before it.
    // PlayButton is disabled only while PlayButton_Click is between SetLayersAsync and PlayAsync.
    if (!PlayButton.IsEnabled)
    {
        StatusText.Text = "Preview is starting; try Export again in a moment.";
        return;
    }

    ExportMenuItem.IsEnabled = false;                           // subtlety 1: BEFORE the await
    await AwaitPreviewCommand(_previewEngine.StopAsync());      // bug audit #8: one audio graph at a time
    StatusText.Text = "Exporting...";                           // after the stop: AwaitPreviewCommand may write "Preview error: ..."

    var (snapshotLayers, snapshotMasterVolumeDb) = _session.SnapshotForExport();

    Task.Run(() =>
    {
        // ... unchanged from :1013-1026, including the finally that re-enables ExportMenuItem ...
    });
}
```

Notes:
- The result of `AwaitPreviewCommand` is ignored, the same as in New (`:1044`) and Open (`:980`). If stopping fails, the error is already on screen for a moment, and the export goes ahead as it does today.
- `StopAsync` is harmless when preview is already stopped (New and Open call it unconditionally).
- The stop happens **before** `SnapshotForExport`. The snapshot still pulls live state through `SyncLiveStateIntoParameters` (`ProjectSession.cs:83-84`), which is dispatcher-side and not affected by stopping.
- `SetPlayStopContent(false)` doesn't need to be called here. The engine's `PlaybackStopped` event already does it (`:167`), the same way as for New/Open.
- **Why a finished `StopAsync` means the render thread is out of `ProcessBlock`:** `StopCore` (`PreviewPlaybackEngine.cs:334-339`) calls `StopInternal`, which calls `_audioSink.Stop()` (`:343`). That runs `WasapiOut.Stop()` and then `Dispose()` (`WasapiPreviewAudioSink.Stop`, `:53-58`). NAudio's `WasapiOut.Stop()` joins its playback thread before it returns. Bug Audit #5 §5 relies on the same guarantee for New/Open: stop before `ReleaseAll`, otherwise use-after-free.
- **Why `!PlayButton.IsEnabled` reliably means "a Play is starting":** a grep of `MainWindow.xaml.cs` finds exactly two writes to `PlayButton.IsEnabled`, at `:796` and `:801`, both inside `PlayButton_Click`. `MainWindow.xaml:202` has no `IsEnabled` binding on the button and no trigger in its style. If a later change disables Play for any other reason, this check has to become a dedicated `_playStarting` flag, set and cleared next to `:796`/`:801`.
- **The first gate (`!ExportMenuItem.IsEnabled`) is defensive only.** Grep shows `ExportButton_Click`'s only caller is the menu item's `Click` (`MainWindow.xaml:161`). There's no keyboard shortcut or other caller, so today a disabled menu item already prevents a double export.

### Step 2: Play refuses while an export is running

This is the first statement of `PlayButton_Click` (`:786`), placed **before** the `IsPlaying` and zero-layer checks:

```csharp
if (!ExportMenuItem.IsEnabled)
{
    StatusText.Text = "Finish the export first.";   // bug audit #8
    return;
}
```

Space goes through the same handler (`:268-270`), so it's covered. While an export runs, `IsPlaying` is always false after step 1, so Space always goes to the Play branch.

### Step 3: The record dialog refuses while an export is running

This is the first statement of `OpenRecordSetup` (`:500`), before the dialog is constructed:

```csharp
if (!ExportMenuItem.IsEnabled)
{
    StatusText.Text = "Finish the export first.";   // bug audit #8
    return;
}
```

It's one gate for all four entry points: ⏺, Tools > Recording setup..., Tools > Calibrate latency... and the strip Rec buttons. It is **synchronous, with no `await`**. So Bug Audit #7 step 3 (`LowestFreeCell()` is read in the caller and then `ShowDialog` runs, with no message pumping in between) still holds exactly. When it refuses, `resolveRow` is never called, so no row is claimed, the same as a cancelled dialog.

Calibrate latency is blocked during export as well. That's deliberate: the calibration dialog builds the same guide mix. Calibration takes seconds and export takes seconds, so the user can wait.

### Ordering subtleties

#### Subtlety 1: the flag goes down *before* the `await`, not after

`ExportMenuItem.IsEnabled = false` must be set before `await AwaitPreviewCommand(_previewEngine.StopAsync())`.

**Counter-example (flag after the await):**
1. Preview is playing. The user picks a file, and Export runs `StopAsync()`, which enqueues `StopCore` on the preview command thread, and awaits.
2. During the await the dispatcher pumps messages. The user presses Space. `IsPlaying` is still true, because `StopCore` hasn't run yet, so Space goes to `StopButton_Click`. That one is harmless.
3. Or the user presses Space just after `StopCore` finishes but before Export's continuation runs. Now `IsPlaying` is false, and `ExportMenuItem.IsEnabled` is **still true**, so `PlayButton_Click` passes the step 2 gate and enqueues `SetLayersAsync` + `PlayAsync`.
4. Export's continuation then sets the flag, snapshots and starts `Task.Run`. Preview is now playing *during* the export. That's the bug again, through the fix's own await.

With the flag set first, the Space press in step 3 hits the step 2 gate and refuses.

#### Subtlety 2: a Play that's already starting can slip past a Play-side gate

`PlayButton_Click` makes two separate enqueues with an `await` between them (`SetLayersThenPlay`, `:805-809`):

```csharp
await _previewEngine.SetLayersAsync(_layers.Layers);   // enqueue #1
await _previewEngine.PlayAsync();                      // enqueue #2, after a dispatcher hop
```

The step 2 gate is only checked **once, at the top**. So a Play that started before the export can still reach `PlayAsync` after it.

**Counter-example (no `PlayButton.IsEnabled` check in Export):**
1. Preview is stopped. The user presses ▶. The gate passes (no export yet), `PlayButton.IsEnabled = false`, and `SetLayersAsync` is enqueued and awaited.
2. The user immediately opens File > Export MP4... The Save dialog's modal loop keeps the dispatcher pumping.
3. `SetLayersAsync` finishes. Its continuation is posted to the dispatcher but hasn't run yet.
4. The user clicks Save. Export sets the flag and enqueues `StopCore`. Preview isn't playing yet, so that's a no-op. Export awaits, takes the snapshot and starts `Task.Run`.
5. Play's continuation runs and enqueues `PlayAsync`, which isn't checked again. `PlayCore` → `StartAudio` → `BuildMixWithMasterVolumeHandle` resets and then pulls the instances **the export is using**.

`PlayButton.IsEnabled` is false from `:796` until `:801`, which exactly covers the window between the two enqueues. So Export refuses in that state. The check has to come **after** `ShowDialog()` returns (as in step 1), because the Play can start while the Save dialog is open (steps 2-3 above). A check before the dialog would pass and then go stale.

The alternative is to re-check the flag inside `SetLayersThenPlay` before `PlayAsync`. That was rejected for this pass: `SetLayersThenPlay` returns a plain `Task`, so telling the handler "not started" would mean changing how `started` is computed at `:799-803`. The refusal message costs the user one extra click, in a window of about a hundred milliseconds.

### Rejected alternatives

1. **A separate plugin instance for export, seeded from `PullLiveState`.** Export would get its own `HostedPluginService` or a per-graph instance key.
   - It is a change to the engine's hosting architecture, and it goes against the audit #3 A1 single-live-instance decision.
   - ARA/Melodyne sessions can't be cloned (`GetOrCreateAraLayerSource`, `HostedPluginService.cs:146-171`), so pitch correction would still be shared.
   - Opening a second FabFilter instance for every slot on every export costs noticeable time.
   - It is out of proportion for a "don't do two things at once" defect.
2. **A lock around `ProcessBlock`/`Reset`** (in `HostedPluginInstance` or `aca_process_block`).
   - It turns the data race into well-defined behaviour, so no crash.
   - But the two graphs would *still* take turns on one plugin state, so the contamination and the mid-song `Reset` stay exactly as they are.
   - It would also put a lock on the WASAPI render thread, where a blocking export block would cause glitches.
   - It hides the symptom and leaves the bug.
3. **Refusing Export while preview is playing** ("Stop playback first.").
   - It's safe, but it doesn't match New/Open, which stop preview automatically.
   - The user has to press ■ and then Export again, when stopping for them has no downside.

---

## Deliberately not changed

- **Seek, scrub and refresh while stopped during an export.** With preview stopped, `SeekCore` (`PreviewPlaybackEngine.cs:367-385`, stopped branch `:379-384`) and `RefreshAsync` (`:197-205`) only render video frames. `TailSeconds` (`FxSlot.cs:78-82`) only calls `GetOrCreateInstance`, and never `Reset` or `ProcessBlock`. They share no audio state with the export and stay allowed. Only `PlayCore` (`:216-245`) builds audio (`StartAudio` `:236`), and it's reached from stopped only through `PlayButton_Click`, which step 2 blocks.
- **Plugin editors, FX panel edits and Undo/Redo during an export.** Polling `PullLiveState` and pushing state with `SetState` on undo run on the dispatcher, and the host changing parameters while the plugin processes audio is normal VST3 behaviour. The effect is that a tweak made during the few seconds of mixdown can reach the export. That comes from the live-instance design (audit #3 A1), not from this bug, and it's left as it is.
- **`RecordSetupWindow`.** Once the dialog is open it is modal, so Export can't start (§Root cause 5). The gate in `OpenRecordSetup` covers every way into it.
- **Preview still playing when a recording starts.** This is a related problem with the same root: the guide mix and the preview share instances. It's listed as runner-up 1 below, not fixed here.
- **Engine, hosting and native code.** `FxSlot.Insert`'s reset stays: it's correct for one graph at a time, which is what this fix enforces. `HostBridge.cpp`'s per-instance scratch buffer stays: with the fix, it's never entered by two threads at once.
- **A side benefit, not a goal.** The fix also stops export and preview from rendering Manual2A ARA/Melodyne at the same time (`PitchCorrectionCache`/`GetOrCreateAraLayerSource`, `MixEngine.cs:191-206`). The same one-graph rule covers it, and this doc doesn't spec it separately.

---

## Tests

### New file: `tests/Acapella.App.Tests/ExportPlaybackGateTests.cs`

Same headless style as `MixingScreenTests`: `[StaFact]`, `TestAppHost.EnsureApplicationResourcesLoaded()`, `new MainWindow()`, and x:Name fields accessed through the `InternalsVisibleTo` in `Acapella.App.csproj`. Add `using System.Windows.Controls.Primitives;` for `ButtonBase`.

1. **`PlayButton_WhileExportInFlight_RefusesWithStatus`**
   ```csharp
   [StaFact]
   public void PlayButton_WhileExportInFlight_RefusesWithStatus()
   {
       TestAppHost.EnsureApplicationResourcesLoaded();
       var window = new MainWindow();
       try
       {
           window.Show();
           window.ExportMenuItem.IsEnabled = false;   // what ExportButton_Click does while Task.Run is running
           window.PlayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
           Assert.Equal("Finish the export first.", window.StatusText.Text);
       }
       finally
       {
           window.Close();
       }
   }
   ```
   Red before the fix: the text is "Add at least one layer first." from `:790-794`. Green after it.

   The gate must come before the zero-layer check, which is why an empty project is enough. It makes the test independent of media and devices. The gate is synchronous, so the text is already set when `RaiseEvent` returns, even though the handler is `async void`.
2. **`PlayButton_WithNoExportInFlight_StillReportsNoLayers`.** Same setup, but `ExportMenuItem` is left enabled. Assert `"Add at least one layer first."`. This guards against a gate that is always on, such as one that reads the wrong flag or has its test inverted.

**Deliberately not tested headlessly:**
- **The ⏺ / `OpenRecordSetup` gate (step 3).** If it regressed, `RecordSetupWindow.ShowDialog()` would open, and the headless test would **hang** instead of failing. It's covered by [human] repro C.
- **Export's own stop-first order (steps 1 and 2).** `ExportButton_Click` opens a modal `SaveFileDialog` before any of the new code runs, and there's no seam to skip it. It's covered by [human] repros A and B. (The #6 and #7 docs made the same call about modal dialogs.)

**Regression:** run `Acapella.App.Tests` and `Acapella.Engine.Tests` once after the change. There are no engine changes, so the engine suite should be unaffected. It's run because the fix is in the transport path that `PreviewPlaybackEngineTests` covers indirectly.

---

## Acceptance criteria

### [auto]
1. `PlayButton_WhileExportInFlight_RefusesWithStatus` passes. It fails on `ddfd741`.
2. `PlayButton_WithNoExportInFlight_StillReportsNoLayers` passes.
3. `Acapella.App.Tests` and `Acapella.Engine.Tests` are fully green.
### [review] (checked by reading the diff, not automated)
1. `ExportButton_Click` sets `ExportMenuItem.IsEnabled = false` **before** its first `await`.
2. Its `!PlayButton.IsEnabled` check comes **after** `dialog.ShowDialog()`.
3. `OpenRecordSetup`'s gate has no `await` before `ShowDialog`.

### [human]
1. **Repro A:**
   - The main check: exporting while a Pro-R 2 tail rings stops preview (▶ goes back to Play) before the mixdown, and the tail no longer cuts off mid-ring because of the export.
   - Secondary: the exported MP4 matches one made with preview stopped, allowing for any randomness in the plugin itself, such as reverb modulation.
2. **Repro B:** Space or ▶ during "Exporting..." shows "Finish the export first.", and preview doesn't start.
3. **Repro C:** ⏺, Tools > Recording setup..., Tools > Calibrate latency... and a strip Rec button during an export all show "Finish the export first." with no dialog. After the export completes, they work normally, and ⏺ still targets the lowest free cell (#7 unchanged).
4. After an export completes, ▶ plays normally, with no stale disabled state.

---

## Runners-up (not specced)

1. **Preview keeps playing into a recording.**
   - `OpenRecordSetup` (`MainWindow.xaml.cs:500-503`) doesn't stop preview. So the singer hears the preview and the dialog's guide (`RecordSetupWindow.xaml.cs:162-164`, `:182`) at the same time, out of sync with each other.
   - Speaker bleed of the preview can skew `MeasureCalibratedOffsetMs` during Calibrate.
   - It also has the same concurrent-`ProcessBlock` problem as this doc, between preview and guide.
   - **Watch out when fixing it:** a naive `await StopAsync()` in `RecordingSetupMenuItem_Click` would break #7 step 3. An await between `LowestFreeCell()` (`:530`) and `ShowDialog` gives Import or Undo a chance to change which cells are free. Either refuse synchronously ("Stop playback first.") or stop *before* reading the free cell.
2. **The metronome BPM changes when you open and close the record dialog.**
   - `RecordSetupWindow.xaml.cs:62` shows the BPM with `ToString("F0")`, so it is rounded, and `MetronomeEngine.cs:34` clamps it to 20-300.
   - `MainWindow.xaml.cs:504` writes `dialog.Bpm` back even on Cancel, which marks the project dirty (`ProjectSession.cs:39`). The toolbar BPM box is never refreshed, so a later focus-out of that box (`:330-336`) reverts to the stale text.
   - For example, a project at 92.5 BPM becomes 92 or 93 after opening and cancelling ⏺. This contradicts `Feature_Spec_2026-09-23_SaveAffordances.md:57`'s claim that cancelling "re-assigns the unchanged BPM".
3. **The static `PitchCorrectionCache` survives `ReleaseAll` on New/Open.**
   - `PitchCorrectionCache` is static (`PitchCorrectionCache.cs:31-45`), and its key includes the ARA edit generation (`MixEngine.cs:200-204`).
   - That generation restarts at 0 for a new session (`HostedPluginService.cs:214-215`, `:222-228`).
   - So after Open or New, a layer with the same id and source can get a cache hit from the *previous* project's render. `Correct()` is then skipped, no ARA session is created, and the Melodyne launcher says "Play this layer once..." even though the user already did. Or a stale Melodyne edit is replayed.

## Runner-up #3 (from Bug Audit #7): still left open

The stale hosted instance after an undo frees a `LayerId` was **not re-specced here**, as instructed. Bug Audit #5 records a decision not to fix it, and nothing in `ddfd741` changes the analysis. My recommendation is **to keep it deferred**. The bug in this doc is more urgent: it happens with a normal workflow (pressing Export during playback) and changes the exported file. Runner-up #3 needs a specific undo-then-reuse sequence.
