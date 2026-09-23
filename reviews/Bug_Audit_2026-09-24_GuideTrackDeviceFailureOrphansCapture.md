# Bug Audit #12: A failed guide-track device open on layer 2+ crashes the app and orphans the capture process

> **Promotion, not a fresh find.** Bug Audit #11's own §Ordering subtleties, subtlety 4 (`Bug_Audit_2026-09-24_MetronomeOutputDecidedAtRecord.md:179`) *names* this exact call site in passing -- "a guide-track take is already strandable by this exact failure today" -- but explicitly declines to spec it ("This pre-existing hazard is out of scope here"), and it is not listed in that doc's own Runners-up section either. It was never given its own repro, root-cause chain, or fix. This ticket is that: the first full spec of the hazard #11 named and deferred, in the style of Bug Audit #7's own promotion of a one-line runner-up.
>
> Audit as of `3b7a1fa` (Bug Audit #11 landed: metronome output always created, phase reset on every take).
>
> **Confidence: high on the mechanism and on the orphaned-process half; high (not just medium) on the crash claim after checking the call chain.** `RecordSetupWindow`'s `_outputDevice` is a one-time snapshot (`:72`) that the guide-track call (`:200`) uses unguarded. `GuideTrackPlayer.Play` (`GetDevice`, the `WasapiOut` constructor, or `Init` -- see §Root cause 1 for which) throws when the captured endpoint is no longer usable, exactly the class of failure bug audit #11 added a `try`/`catch` around two lines later for the metronome's own call into the same device (`:224-241`). `App.xaml.cs` registers no `Application.DispatcherUnhandledException` handler, and I traced every frame between `RecordButton_Click` and the app's top-level dispatcher loop (`ShowRecordSetupDialog`, `OpenRecordSetup`/`RetakeRecord`, and their own callers in `MainWindow.xaml.cs` -- none has a `try`/`catch`, confirmed by grep, §Root cause 2) -- so an exception here really does propagate all the way out uncaught. I have not reproduced this end-to-end on a physical machine (it needs a camera, a microphone, and a way to invalidate the system default output device mid-session), so treat "crashes" as high-confidence-from-code-reading rather than observed. The *orphaned-process* half of the bug does not depend on whether the process actually terminates: the exception unwinds past `_isRecording = true` (`:243`) either way, so `_activeCapture` (already running) is abandoned the instant the exception is thrown, crash or no crash.

---

## The bug in one sentence

`RecordSetupWindow.RecordButton_Click`, for any take (new or retake) recorded while at least one other layer exists to serve as a guide, starts the ffmpeg capture process and then calls `GuideTrackPlayer.Play(_outputDevice.Id, guideMix)` with **no exception handling** (`RecordSetupWindow.xaml.cs:199-200`); if the one specific render endpoint captured once at dialog-construction time (`:72`) has since become unusable (unplugged, disabled), that call throws, the exception is unhandled (no `DispatcherUnhandledException` hookup anywhere in the app), and by that point the already-running `_activeCapture` (a plain child `ffmpeg.exe` process with no job-object tie to the parent) is never stopped, disposed, or even referenced again — orphaned, still holding the camera and microphone devices, for as long as it keeps running.

---

## Root cause

Three facts combine to produce the defect.

### 1. The guide-track device-open call is unguarded, unlike its twin two lines below it

**File:** `src/Acapella.App/RecordSetupWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:72` | `_outputDevice = _deviceCatalog.GetDefaultRenderDevice();` — resolved **once**, in the constructor, and never refreshed for the dialog's lifetime. `DeviceCatalog.cs:35-40`'s own doc comment confirms there is no output-device picker anywhere in the UI to let the user change or re-resolve it. |
| `:197-198` | `_activeCapture.Start(...)` then `WaitForCaptureStarted(TimeSpan.FromSeconds(3))` — by the time execution reaches the next line, ffmpeg is started and (barring a 3s timeout) already producing frames from the live camera/mic. |
| `:199-200` | `_guideTrackPlayer = new GuideTrackPlayer(); _guideTrackPlayer.Play(_outputDevice.Id, guideMix);` — **no `try`/`catch`**. |
| `:224-241` | The metronome's own device-open block, two lines later in the same method, wraps the *identical* `MMDeviceEnumerator.GetDevice(_outputDevice.Id)` call (via `new WasapiOut(metronomeDevice, ...)`) in a `try`/`catch` — added by bug audit #11, specifically because this exact API is known to throw on a stale device ID. |

**File:** `src/Acapella.Engine/GuideTrack/GuideTrackPlayer.cs:18-26`

```csharp
public void Play(string outputDeviceId, ISampleProvider guideAudio)
{
    using var enumerator = new MMDeviceEnumerator();
    var outputDevice = enumerator.GetDevice(outputDeviceId);   // throws E_NOTFOUND if the id is no longer registered at all

    _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 50);   // can throw activating IAudioClient on a disabled/unplugged endpoint
    _output.Init(guideAudio);                                                        // can throw AUDCLNT_E_DEVICE_INVALIDATED
    _output.Play();
}
```

Three different lines in this one method can throw, and which one fires depends on *why* `outputDeviceId` no longer works: `GetDevice` throws only if the ID is entirely gone from the endpoint registry (a genuinely removed device); an endpoint that's merely disabled or unplugged but still enumerable typically makes `GetDevice` succeed while `new WasapiOut(...)` (activating `IAudioClient` on it) or `_output.Init(...)` (`AUDCLNT_E_DEVICE_INVALIDATED`) throws instead. The distinction doesn't matter for this bug — none of the three is caught, and all three sit on the far side of `_activeCapture.Start()` (`:197`) either way. This is precisely the scenario bug audit #11 built its own metronome fix's counter-example around (`Bug_Audit_2026-09-24_MetronomeOutputDecidedAtRecord.md:176-179`), for the *same* `_outputDevice.Id`, resolved at the *same* construction-time snapshot, going into the *same* `WasapiOut` construction/`Init` pair (`RecordSetupWindow.xaml.cs:227-230`, guarded there) that `GuideTrackPlayer.Play` also does (unguarded, here).

### 2. An unhandled exception on the UI thread crashes the whole app; nothing between here and the dispatcher catches it

**File:** `src/Acapella.App/App.xaml.cs` (full file, 10 lines of code) — no `Startup` handler, no `DispatcherUnhandledException` subscription, no `AppDomain.CurrentDomain.UnhandledException` hookup. A grep of `src/Acapella.App/*.cs` for any of those three finds nothing.

WPF's default behavior for an exception thrown inside a `Click`/`RoutedEventHandler` invoked from the dispatcher's message loop, with no `DispatcherUnhandledException` handler registered, is to let the exception propagate out of `Dispatcher.Run()` and terminate the process. `RecordSetupWindow.ShowDialog()` pumps its own nested message loop for the modal dialog, but that nested loop is still serviced by the same dispatcher and offers no exception boundary of its own — an exception from a modal dialog's own event handler propagates *out of* `ShowDialog()` to its caller, the same way any other exception would.

Every frame in that call chain was checked (`grep -n "catch" src/Acapella.App/MainWindow.xaml.cs`, then read in context): `ShowRecordSetupDialog` (`MainWindow.xaml.cs:507-521`, the sole place `RecordSetupWindow` is constructed, per Bug Audit #7) calls `dialog.ShowDialog()` at `:518` with no surrounding `try`. Its own callers, `OpenRecordSetup` (`:526-549`) and `RetakeRecord` (`:601-624`), have none either. Those are themselves plain `Click`-wired event handlers with nothing above them. The four `catch` blocks that do exist in `MainWindow.xaml.cs` (at `:854`, `:1029`, `:1093`, `:1138`) are all in unrelated methods (export, save-as, and similar), nowhere near this call chain. So the exception really does reach the dispatcher with nothing in between to intercept it.

### 3. The child ffmpeg process has no lifetime tie to the parent, so a crash does not clean it up

**File:** `src/Acapella.Engine/Capture/FfmpegCaptureSession.cs:74-76`

```csharp
_process = Process.Start(psi);
if (_process is not null)
    _stderrTail = FfmpegProcessUtil.DrainStderrKeepingTail(_process);
```

Plain `Process.Start`, no Windows Job Object association (`JOBOBJECT_LIMIT_KILL_ON_JOB_CLOSE` or equivalent) anywhere in this class or its callers. A child process started this way is not automatically terminated when its parent process dies — it is an independent process from the OS's point of view. `Stop()` (`:113-122`) and `Dispose()` (`:124-128`) are the *only* code paths that ever ask it to exit, and both require a live reference to the `FfmpegCaptureSession` object holding `_process`.

**Why the reference is lost, not just "not yet stopped":** `_activeCapture` (the field holding that `FfmpegCaptureSession`) is assigned at `RecordSetupWindow.xaml.cs:168`, *before* the guide-track block. When `Play()` throws at `:200`, the exception unwinds past `:243` (`_isRecording = true`) without ever reaching `StopRecording()` or `Window_Closing`'s cleanup (`:376-388`, itself gated on `_isRecording`, which was never set). If the process doesn't crash outright for some reason (§Confidence), the *next* thing that can happen to `_activeCapture` is whatever runs next on this `RecordSetupWindow` instance — and the only write to that field elsewhere is `RecordButton_Click`'s own `_activeCapture = new FfmpegCaptureSession();` (`:168`) on a subsequent press, which **overwrites the field**, discarding the only reference to the first, still-running process. Either way — crash, or a swallowed exception followed by a retry — the first `ffmpeg.exe` is never asked to stop.

---

## Repros

### A. Guide-track device failure on a layer-2+ take **[human]**, needs a camera, a microphone, and control over the system default output device

1. Record layer 1 normally (no guide exists yet for the first layer, so this step is unaffected).
2. Open Recording setup for layer 2 (or press ⏺ again). The dialog resolves `_outputDevice` in its constructor (`:72`).
3. Before pressing Record, invalidate the *specific* endpoint `_outputDevice` captured in step 2 — not just "the system default," since `_outputDevice.Id` names one fixed endpoint and a later default change to some *other*, still-present device doesn't touch it. Unplug the physical device behind that endpoint (a USB headset, or a Bluetooth headset disconnecting), or disable it in Windows Sound settings.
4. Select a camera and microphone, then press **Record**.
5. **Bug:** the app either crashes outright (a "stopped working" dialog / silent exit, no confirmation prompt, no save prompt even for a dirty project) or — if some hosting quirk intercepts it — the dialog is left in a broken state with `_isRecording` still `false` and `RecordButton.Content` still "Record", while capture is silently still running.
6. Check Task Manager / Process Explorer: an `ffmpeg.exe` process is still running, its window title/command line showing `-f dshow -i video=...:audio=...` for the same camera/mic just selected, with **no** Acapella window to stop it. It keeps writing frames to `outputPath` until manually killed, and holds the camera and microphone devices open — a second recording attempt (even after relaunching Acapella) cannot acquire the same camera/mic while this orphan still has them.

### B. Same trigger on a retake, not just a "layer 2" new take **[human]**, same requirements

`RecordTakeRules.GuideLayers` (`RecordTakeRules.cs:17-22`) returns every *other* layer as the guide for a retake, exactly like a new take — so `guideLayers.Count > 0` (and therefore the vulnerable branch at `:172-203`) is reached by **any** take, new or retake, recorded while at least one sibling layer exists. Re-recording layer 1 in a 2-layer project, with layer 2 already present, hits the identical code path and the identical failure. This is not specific to "the second layer you ever record."

### C. Engine-level evidence of the safe-cleanup assumption **[auto]**

`Play_WithNonexistentDeviceId_ThrowsAndLeavesNoOutputToDispose` (§Tests) proves, without any WPF or dialog involved, that `GuideTrackPlayer.Play` can throw (using a device ID that was never registered at all, so it fails at the `GetDevice` line specifically — a different line from a real unplugged/disabled endpoint, §Root cause 1, but the same overall shape: `Play()` throws before completing) and that after that throw, `Dispose()` is still safe to call. That second half is the one the fix's catch block actually depends on, regardless of which of the three lines in `Play()` throws in a given real-world case.

---

## Why the suite misses it

- **`RecordSetupWindow` has zero test coverage**, for the same reason established by Bug Audit #7 and reconfirmed by Bug Audit #11: every entry point runs through the modal `ShowDialog()`, which a headless test can't drive, and `RecordButton_Click` additionally needs real dshow devices in `_dshowVideoDevices`/`_dshowAudioDevices` or it bails at the device-selection guard (`:136-140`) before reaching any of this code.
- **`GuideTrackPlayer` has no test file at all** (`find tests -iname "*GuideTrack*"` finds nothing) — it has never been exercised even in isolation, unlike `MetronomeEngine`, which had at least one pre-existing test before bug audit #11 added more.
- **No prior audit tried recording a second (or later) layer with the default output device made invalid mid-dialog.** Bug audit #11's own subtlety 4 used exactly this setup as a *counter-example for the metronome's fix*, but stopped there — it never asked what happens on the guide-track call two lines above the block it was patching.
- **A process-level leak plus an app crash leaves no artifact in the test suite to catch.** There's no automated check anywhere in this codebase for "is there an orphaned ffmpeg.exe after a failed take," because until now no failure mode has produced one silently.

---

## Proposed fix

**Approach: wrap only the guide-track device-open call in a `try`/`catch`, and on failure, abort the take — stop and dispose the already-running capture and any partially-created guide player, leave `_isRecording` false, and tell the user to fix their output device and reopen the dialog. Do not fall back to "record anyway, without a guide," unlike the metronome's own catch.**

### Change: `src/Acapella.App/RecordSetupWindow.xaml.cs`, inside the `guideLayers.Count > 0` branch (`:172-203`)

Replace `:199-200`:

```csharp
            // C4: capture starts first so the guide is never delayed by it; but the guide must
            // not start until dshow capture is actually producing frames (audit B4) -- otherwise
            // the recorded file's t=0 begins at an unmeasured, variable point (0.5-2s, dshow
            // device init) after the guide already started, and CalibratedOffsetMs (measured over
            // a WASAPI Stereo-Mix loopback, a different path) can't account for that.
            _activeCapture.Start(videoDevice.Name, dshowAudioDevice.Name, outputPath);
            _activeCapture.WaitForCaptureStarted(TimeSpan.FromSeconds(3));

            // Bug audit #12: _outputDevice (:72) is a one-time snapshot of one specific render
            // endpoint, taken when this dialog was constructed and never refreshed -- there is no
            // output-device picker to let the user redo that (DeviceCatalog.cs:35-40). If that
            // exact endpoint has since gone away (unplugged, disabled -- a headset disconnecting
            // is enough; a later default-device CHANGE to some other still-present device is not),
            // GuideTrackPlayer.Play throws (at GetDevice, the WasapiOut constructor, or Init,
            // depending on why the endpoint stopped working -- see the doc's root cause 1). By
            // this point _activeCapture is already running (Start + WaitForCaptureStarted just
            // above), and this app has no Application.DispatcherUnhandledException handler
            // (App.xaml.cs), so letting that exception propagate crashes the whole process and
            // orphans ffmpeg.exe -- a plain child process (no job-object tie, FfmpegCaptureSession
            // .cs) that survives the crash and keeps the camera/mic devices open. Unlike the
            // metronome's own device-open failure below (already guarded, bug audit #11), the
            // guide track is the entire reason a take against existing layers would be usable --
            // continuing silently without it produces an unsynced take nobody can salvage, so this
            // aborts instead of swallowing the failure the way the metronome catch does.
            try
            {
                _guideTrackPlayer = new GuideTrackPlayer();
                _guideTrackPlayer.Play(_outputDevice.Id, guideMix);
            }
            catch (Exception ex)
            {
                _guideTrackPlayer?.Dispose();
                _guideTrackPlayer = null;
                _activeCapture.Stop();
                _activeCapture.Dispose();
                _activeCapture = null;
                StatusText.Text = $"Couldn't start the guide track (output device unavailable: {ex.Message}). Close and reopen this dialog after checking your output device, then try again.";
                return;
            }
```

Notes:
- `_guideTrackPlayer?.Dispose()` is safe even when `Play()` threw before `_output` was ever assigned (`GuideTrackPlayer.cs:16`, `:33-37`): `Dispose()` → `Stop()` → `_output?.Stop()`/`_output?.Dispose()`, all null-conditional. §Tests proves this directly.
- `_activeCapture.Stop()` writes `"q"` to ffmpeg's stdin and waits up to 5s before `Kill()` (`FfmpegCaptureSession.cs:113-122`) — the same graceful-then-forced shutdown every other Stop path in this codebase already uses; nothing new is introduced here.
- The `catch` is scoped to exactly the two guide-track lines, not the whole `guideLayers.Count > 0` branch — see §Ordering subtleties, subtlety 3.

### Rejected alternatives

- **Swallow the exception and continue recording without a guide, mirroring the metronome's catch (`:224-241`).** Rejected: the metronome is a monitoring nicety (M4's own framing, `Bug_Audit_2026-07-12.md:111`) whose absence doesn't change whether the take is usable. The guide track is not optional in the same sense — for a take recorded against existing layers, it's the only thing letting the performer stay in time. A take recorded in silence, with the performer unaware the guide never started, is not a take anyone would want to keep; failing loudly and letting the user retry (after fixing their device) is strictly better than producing an unusable file that looks like a normal successful take right up until playback.
- **Re-resolve `_outputDevice` fresh at the top of every `RecordButton_Click`, instead of catching the failure.** This would prevent the exception outright for the common "device changed" case, and is a reasonable follow-up improvement. It is not proposed as *the* fix here because it doesn't fully close the gap (there's still a window between the fresh resolve and the `Play()` call, and a machine with *no* valid render endpoint at all would make `GetDefaultRenderDevice()` itself throw, a new failure mode this ticket doesn't need to introduce) and it changes device-resolution behavior mid-dialog beyond what this crash fix requires. Noted as a possible future improvement, not bundled here.
- **Add the `DispatcherUnhandledException` handler application-wide instead of fixing this call site.** A global handler is good defense-in-depth (and arguably overdue regardless of this ticket — see Runners-up), but on its own it only prevents the *crash*; without also stopping `_activeCapture` in the catch, the orphaned-process half of the bug (§Root cause 3) would still happen every time, just without taking the app down with it. The targeted `try`/`catch` here fixes the actual resource leak; a global handler is a separate, complementary safety net.

---

## Ordering subtleties

**1. Cleanup inside the catch must actually call `_activeCapture.Stop()`/`Dispose()`, not just discard the reference.**
- **Counter-example:** a fix that catches the exception and simply does `_activeCapture = null; StatusText.Text = "...";` without calling `Stop()`/`Dispose()` first *looks* fixed (no crash, dialog stays usable) but reproduces the exact orphaned-process half of the original bug — the running `ffmpeg.exe` is still never asked to exit, and the only reference to it is gone.
- Change: the catch block calls `_activeCapture.Stop()` then `_activeCapture.Dispose()` before nulling the field.

**2. The `try` must be scoped to the guide-track lines only, not widened to also cover `_activeCapture.Start()`/`WaitForCaptureStarted()` just above it.**
- Those two calls happen *before* the guide-track block specifically so that "the guide is never delayed by [capture]" (the existing comment at `:192-193`, unchanged by this fix). Wrapping them in the same `try` would change that sequencing rationale and is unnecessary: `FfmpegCaptureSession.Start()` failing via a thrown exception is a much rarer, different failure shape (a missing/broken `ffmpeg.exe`, not a stale device ID) that this ticket doesn't newly introduce or make worse — see Deliberately not changed.
- Change: the `try` opens right at `_guideTrackPlayer = new GuideTrackPlayer();`, after both capture-start calls.

**3. The `try` must not be widened to also cover `BuildMix`/`RenderMonoReference` above it (`:180-190`), even though they're in the same branch.**
- Not because calling `_activeCapture.Stop()`/`Dispose()` on a never-started session would crash — it wouldn't: `Stop()` null-checks `_process` first (`FfmpegCaptureSession.cs:115`, `if (_process is null || _process.HasExited) return;`), so it's a harmless no-op on a `FfmpegCaptureSession` that was `new`'d but never `.Start()`ed.
- The real reasons to keep the `try` narrow: (a) `BuildMix`/`RenderMonoReference` can throw for reasons that have nothing to do with the output device (a corrupt source file mid-decode, for instance) — folding them into the same catch would report every such failure as "output device unavailable," misleading the user about what actually needs fixing. (b) Those calls run at `:180-190`, strictly *before* `_activeCapture.Start()` at `:197` — so a failure there has nothing to clean up in the first place; widening the `try` to cover them would make the catch do work (`Stop`/`Dispose` a session that's harmless to touch but was never running) for a case the fix doesn't need to handle at all.
- Change: the catch begins after `:198`, scoped to exactly the guide-track two lines, as shown above.

---

## Deliberately not changed

- **The metronome's own device-open catch (`:224-241`).** It already does the right thing for its own failure mode (swallow and continue, no take to lose). Unaffected by this fix.
- **`_outputDevice` staying a one-time, dialog-construction-time snapshot** (`:72`), and the fact that a same-dialog retry after this fix's error message will hit the *same* stale, still-invalid device ID and fail again with the same message. The status text explicitly tells the user to close and reopen the dialog (which re-resolves `_outputDevice` fresh in a new constructor call) rather than just "try again" in place. Re-resolving live is the rejected alternative above, deferred.
- **`_activeCapture.Start()` itself being unguarded** (`:197` in the guide branch, `:206` in the no-guide branch). `Process.Start` for a present, valid `ffmpeg.exe` essentially never throws in practice (unlike `MMDeviceEnumerator.GetDevice` on a removed device, which is a documented, expected failure this codebase has already had to guard against twice). If `ffmpeg.exe` itself goes missing mid-session, that's a much larger and different problem than this ticket's scope.
- **No `Application.DispatcherUnhandledException` handler is added.** A global safety net is a reasonable follow-up (see Runners-up) but doesn't by itself fix the orphaned-process leak this ticket targets, and adding one changes crash behavior application-wide, which is a bigger decision than this ticket needs to make.
- **`RecordingPathAllocator`'s already-allocated `outputPath`** is left on disk after an aborted take (an empty or partial `.mkv` from the few hundred ms of capture before the guide-track failure). This is the same "old/failed takes accumulate in `media/`" shape already called out as a known limitation elsewhere in this project's docs, not something this ticket introduces or needs to clean up.

---

## Tests

### New file: `tests/Acapella.Engine.Tests/GuideTrack/GuideTrackPlayerTests.cs`

No WPF, no dialog, no real capture device needed — this only needs an audio output enumerator, which is present on any machine this suite already runs `WasapiOut`-touching engine code on.

```csharp
using Acapella.Engine.GuideTrack;
using Acapella.Engine.Mix;
using Xunit;

namespace Acapella.Engine.Tests.GuideTrack;

/// <summary>Bug audit #12: RecordSetupWindow's new catch around GuideTrackPlayer.Play relies on
/// Dispose() being safe to call after a failed Play() -- this proves that directly, without any
/// WPF or dialog involved, using a device id that was never registered at all (so this throws at
/// the GetDevice line specifically; a real unplugged/disabled endpoint can instead throw one line
/// later, at the WasapiOut constructor or Init -- see the doc's root cause 1). Either way,
/// GuideTrackPlayer's _output field is never assigned, so Dispose() -> Stop() is a safe no-op.</summary>
public class GuideTrackPlayerTests
{
    [Fact]
    public void Play_WithNonexistentDeviceId_ThrowsAndLeavesNoOutputToDispose()
    {
        var player = new GuideTrackPlayer();
        var guideAudio = new ArraySampleProvider(new float[10], 44100);

        Assert.ThrowsAny<Exception>(() => player.Play("{00000000-0000-0000-0000-000000000000}", guideAudio));

        // Characterizes the assumption RecordSetupWindow's catch block relies on (§Proposed fix):
        // cleanup after a failed Play() must not itself throw.
        player.Dispose();
    }
}
```

**No automated test for the `RecordSetupWindow` wiring**, for the same reason established by Bug Audit #7 and #11: every path goes through the modal `ShowDialog()`, which a headless test can't drive, and this fix doesn't add a seam that would change that. The wiring is **[human]**-verified below.

**Regression carve-out (per CLAUDE.md):** `RecordSetupWindow.xaml.cs` is touched but has no automated coverage to re-run (the established limitation). `GuideTrackPlayer.cs` itself is untouched by this fix (only its caller gets a `try`/`catch`); no other test currently exercises it, so there's nothing to regress there beyond the new test above.

---

## Acceptance criteria

### [auto]
- `Play_WithNonexistentDeviceId_ThrowsAndLeavesNoOutputToDispose` passes.
- The solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- The `try` opens at `_guideTrackPlayer = new GuideTrackPlayer();`, strictly after `_activeCapture.Start()` and `WaitForCaptureStarted()` (subtlety 2), and strictly after the `BuildMix`/`RenderMonoReference` calls above it (subtlety 3) — not wrapping either.
- The `catch` block calls `_activeCapture.Stop()` then `_activeCapture.Dispose()` before setting `_activeCapture = null` (subtlety 1), and `_guideTrackPlayer?.Dispose()` before setting `_guideTrackPlayer = null`.
- The `catch` block `return`s without ever setting `_isRecording = true`, `RecordButton.Content`, or disabling `CameraCombo`/`MicCombo`.
- `StatusText.Text` in the catch tells the user to close and reopen the dialog, not just to press Record again.
- The metronome's own `try`/`catch` (`:224-241`, now renumbered) is unchanged.

### [human] (camera, microphone, and a way to invalidate the default output device required)
- Repro A after the fix: with the default output device made invalid before pressing Record on layer 2, the app does **not** crash. `StatusText` shows the "Couldn't start the guide track" message. `RecordButton` still reads "Record". No orphaned `ffmpeg.exe` remains in Task Manager after this (check within a few seconds of the failed press).
- Restore a valid default output device, close and reopen the Recording setup dialog, and record layer 2 normally: the guide track plays and the take completes exactly as before this fix.
- Repro B after the fix: the same failure/recovery sequence works for a retake (↻ → Re-record...) of a layer with siblings present, not just a brand-new layer 2.
- No regression: a normal take recorded with a healthy, unchanged output device (layer 1, or layer 2+ with nothing touched) behaves identically to before this fix.

---

## Runners-up (not specced)

- **No `Application.DispatcherUnhandledException` handler anywhere in the app.** This ticket's fix removes the one currently-known trigger for an unhandled exception mid-take, but any *other* uncaught exception on the UI thread — in this method or any other — still takes the whole app down with no save prompt, no diagnostics surfaced to the user beyond whatever Windows Error Reporting shows, and no chance to recover a dirty project. A global handler that at minimum logs and shows a "something went wrong" dialog before exiting (or, more ambitiously, tries to preserve unsaved project state) is worth its own ticket; it's app-wide behavior, not a one-call-site fix, and deliberately out of scope here.
- **`_activeCapture.Start()` itself is unguarded in both branches** (`:197` and `:206`). Lower priority than the guide-track call (see Deliberately not changed) but the same shape of hazard if `ffmpeg.exe` ever becomes unavailable mid-session (e.g. deleted or blocked by antivirus between app launch and Record press).
- **Possibly unrelated lead, not checked in depth:** the `CommitSlider` style (`MainWindow.xaml:39-43`) commits an undo step only on `PreviewMouseUp`. A WPF `Slider` also responds to arrow keys/PageUp/PageDown/Home/End when focused, which raises no mouse-up event — if nothing else commits on that path (no `KeyUp`/`LostKeyboardFocus` handler was found near the style), a keyboard-only slider edit would update the live parameter (`LiveParamChanged` fires) with no undo step and no `MarkDirty`, silently. Not investigated past the XAML read; flagged for whoever picks up undo/FX-parameter edge cases next.
