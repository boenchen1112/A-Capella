# Bug Audit #15: Closing the main window during an export silently kills it, leaving a playable but truncated MP4 (and destroying any file it overwrote)

> **Fresh find.** The four "Finish the export first." gates Bug Audit #8 added (and the Retake spec moved into `ShowRecordSetupDialog`) cover Play, Record/Re-record, File > Open, and File > New. None covers closing the window. No doc in `reviews/` mentions close-during-export: grep for `close.*export`, `export.*close`, and `Closing` finds only the Save-affordances spec's discard-prompt work and an unrelated ~2s close hang during playback. The two Improvement Proposals' "Export progress" candidate (`Improvement_Proposal_2026-09-24.md:88-100`) is about showing progress. It never considers what happens when the app exits mid-export.
>
> Audit as of `0f09677` (Bug Audit #14 landed, including its regression tests).
>
> **Confidence: high on the mechanism and on the truncated-output result. Medium on the secondary hosted-plugin race.**
> - **No gate.** `MainWindow`'s one `Closing` handler (`MainWindow.xaml.cs:169-187`) never checks `ExportMenuItem.IsEnabled`, the in-flight flag the other four gates read.
> - **The app exits on close.** `App.xaml` sets no `ShutdownMode`, so WPF's default `OnLastWindowClose` applies. `MainWindow` is the only non-modal window, so closing it shuts the application down.
> - **The export thread dies without cleanup.** The export runs on a `Task.Run` thread-pool thread (`:1158`). Thread-pool threads are background threads, and the CLR ends them at process exit without running their `finally` blocks.
> - **ffmpeg finalizes whatever it has.** I checked what the encoder does when its parent dies. I launched ffmpeg with `ExportEngine`'s exact encode arguments from a throwaway PowerShell parent, fed it blank 1280x720 RGBA frames through the redirected stdin plus a 20 s WAV, and force-killed the parent after ~5 s. ffmpeg exited by itself within 4 s. `ffprobe` reported the resulting `out.mp4` as a valid file of **4.566667 s**: the frames received so far, with the audio cut to match by `-shortest`.
> - **Caveat on that experiment:** it force-killed the parent directly, not a CLR exit after `Application.Shutdown`. Both close the parent's end of the stdin pipe when the process ends, which is all the encoder can see, so I expect the same outcome. I have not observed it through the real app.
> - **Secondary race.** Hosted plugins can be released under an in-flight `ProcessBlock` (§Root cause 4). That is reasoned from code only. It needs a close to land during the mixdown loop specifically, so treat it as medium confidence.

---

## The bug in one sentence

While an export is running (`ExportMenuItem.IsEnabled == false`, `MainWindow.xaml.cs:1152-1172`), closing the main window (the chrome ✕, Alt+F4, or the taskbar's Close) is not refused the way Play/Record/Open/New are. The `Closing` handler (`:169-187`) tears down the preview engine and the shared `HostedPluginService` that the export is still using, then the app exits. That kills the background export thread mid-loop, so its cleanup and its "Export complete/failed" status never run. The ffmpeg encoder sees its stdin close and finalizes a **playable, silently truncated MP4** at the chosen path. The user gets no error. If that path held a previous good export, it was already truncated by `-y` when the encoder started, so it is gone too.

---

## Current behavior

| Location | What it does |
|---|---|
| `MainWindow.xaml.cs:537-541` | `ShowRecordSetupDialog`: `if (!ExportMenuItem.IsEnabled) { StatusText.Text = "Finish the export first."; return null; }`. Gate 1 of 4. |
| `MainWindow.xaml.cs:914-918` | `PlayButton_Click`: the same gate. Gate 2. |
| `MainWindow.xaml.cs:1094-1098` | `OpenProjectButton_Click`: the same gate, placed **before** `ConfirmDiscardUnsavedChanges()` (`:1100`). Gate 3. |
| `MainWindow.xaml.cs:1181-1185` | `NewProjectMenuItem_Click`: the same gate, again before `ConfirmDiscardUnsavedChanges()` (`:1187`). Gate 4. |
| `MainWindow.xaml.cs:169-187` | The single `Closing` lambda. It runs `ConfirmDiscardUnsavedChanges()`, which only prompts if dirty. Then it stops the three timers and disposes `_previewEngine`, `_mixEngine` and `_hostedService` unconditionally. **No export check.** |
| `MainWindow.xaml.cs:453` | `CloseButton_Click` → `SystemCommands.CloseWindow(this)`. Every close path (✕, Alt+F4, taskbar) raises the same `Closing` event. |
| `MainWindow.xaml.cs:1152` | `ExportMenuItem.IsEnabled = false;`, the in-flight flag, set before the export starts. |
| `MainWindow.xaml.cs:1154` | `StatusText.Text = "Exporting...";`. This is the only indication an export is running. There is no progress UI (`Improvement_Proposal_2026-09-24.md:88-100` proposes one, not yet built). |
| `MainWindow.xaml.cs:1158-1174` | `Task.Run(...)`: `new ExportEngine(hostedService: _hostedService)` shares the window's one hosted service. `Export(...)` runs, then `Dispatcher.Invoke` sets the complete/failed status (`:1164`/`:1168`) and, in `finally`, re-enables `ExportMenuItem` (`:1172`). |
| `ExportEngine.cs:48-58` | Mixdown. `BuildMix` wires the shared hosted instances in, and `mix.Read(...)` drives `HostedPluginSampleProvider` → `ProcessBlock` on this thread. |
| `ExportEngine.cs:60-61` | Writes the full mix to `%TEMP%\acapella-export-audio-<guid>.wav`. |
| `ExportEngine.cs:77` / `:140` | Starts the encoder with `-y`, so ffmpeg truncates any existing file at `outputPath` immediately. |
| `ExportEngine.cs:85-93` | The long stretch. For each frame it decodes every layer, composites, and writes raw RGBA to the encoder's stdin. Then `stdin.Close()` and `WaitForExit()`. |
| `ExportEngine.cs:104-108` | `finally { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); }`. This is the only cleanup of the temp WAV. |
| `App.xaml` | No `ShutdownMode`, so `OnLastWindowClose`. `App.xaml.cs` has no `Exit`/`SessionEnding`/unhandled-exception handlers. |

---

## Root cause

Four facts combine to produce the defect.

### 1. `Closing` is the one "discard/replace the project" path without the export gate

Bug Audit #8 established `ExportMenuItem.IsEnabled == false` as the app's "export in flight" flag. Every user action that would tear down or compete with the shared audio graph now refuses on it: Play, Record, Open, New (table above, gates 1-4). Closing the window is the most destructive of these actions, since it disposes everything, but it is the only one with no gate. Its handler was last edited by the Save-affordances spec, which added only the dirty-project prompt at the top (`Feature_Spec_2026-09-23_SaveAffordances.md:327-345`). It never consulted the export flag. For a clean project, `ConfirmDiscardUnsavedChanges()` returns `true` without prompting at all (`:1070`), so a mid-export close gets no dialog of any kind.

### 2. Closing the only main window ends the process, and with it the background export thread, without running `finally`

`App.xaml` uses WPF's default `ShutdownMode.OnLastWindowClose`. `RecordSetupWindow` is modal, and the Melodyne/FabFilter editors are native JUCE windows, not WPF `Window`s. So `MainWindow` closing leaves zero WPF windows, `Application.Shutdown()` runs, the dispatcher loop exits, and `Main` returns.

The export body runs on a `Task.Run` thread-pool thread (`:1158`). Thread-pool threads are background threads, and the CLR does not wait for them at process exit or unwind them. They simply stop. Two consequences:
- `ExportEngine.cs:104-108`'s `finally` never runs, so the temp WAV (`:60-61`) stays in `%TEMP%`. At 44.1 kHz stereo float that is ~353 KB per second of song, about 63 MB for a 3-minute track, per abandoned export.
- `MainWindow.xaml.cs:1164`/`:1168` never run, so the user sees neither "Export complete" nor "Export failed". The window is already gone.

### 3. The ffmpeg encoder treats its parent's death as a normal end of input and writes a valid, shorter file

The encoder (`ExportEngine.cs:126-155`) reads raw video from `-i -` (stdin) and audio from the temp WAV, with `-shortest`. When the parent process exits, the OS closes the parent's write end of the stdin pipe. On Windows, ffmpeg's read then returns end-of-file. ffmpeg treats that as the video stream ending normally: it flushes libx264, cuts the audio to the video length (`-shortest`), writes the MP4 index, and exits 0.

**Measured, not just reasoned.** I used a scratch PowerShell parent with `ProcessStartInfo { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false }` and the arguments `-y -f rawvideo -pix_fmt rgba -s 1280x720 -r 30 -i - -i audio.wav -c:v libx264 -pix_fmt yuv420p -c:a aac -b:a 192k -shortest out.mp4`, identical to `ExportEngine.cs:140-152`. The parent wrote zeroed 1280x720x4 frames every 20 ms, with a 20 s sine WAV as `audio.wav`, and was force-killed with `Stop-Process -Force` after 5 s. Results:
- The ffmpeg process was gone 4 s later.
- `ffprobe -show_entries format=duration out.mp4` → `duration=4.566667`.

The file is not corrupt in any way a player would flag. It is just short.

`-y` makes this worse. The encoder is started at `:77`, *after* the mixdown, and ffmpeg opens and truncates `outputPath` right away. So if the user re-exported to the same file name as an earlier good export (the Save dialog's overwrite prompt makes that one click), the earlier file is already destroyed. What's left after the close is only the truncated replacement.

The per-layer decoder processes (`LayerFrameSource.cs:80`) write to stdout pipes whose reader is now dead. They fail on their next write and exit, so they don't linger. This was not measured, but it's the ordinary broken-pipe path, and it isn't the harm this ticket is about.

### 4. Secondary: the teardown disposes the hosted service the export's mixdown is actively using

`Closing` calls `_hostedService.Dispose()` (`:184`) → `HostedPluginService.Dispose` (`HostedPluginService.cs:230-236`) → `_cache.Dispose()` → `ReleaseAll()` → `HostedPluginInstance.Dispose()` (`HostedPluginInstance.cs:154-161`) → native `aca_release_instance`, which `delete`s the instance (`HostBridge.cpp:395-403`). ARA sessions are disposed the same way. The export's mixdown loop (`ExportEngine.cs:53-58`) calls `ProcessBlock` on those same instances from its own thread (`HostedPluginInstance.cs:109-110`), since the `ExportEngine` was built on the shared service at `MainWindow.xaml.cs:1162`.

- **A `ProcessBlock` call already inside native code when `aca_release_instance` runs** is a use-after-free.
- **A call that starts after the release** is harmless. `Dispose` zeroes `_handle`, and `aca_process_block` returns early on `nullptr` (`HostBridge.cpp:316`), so it becomes a silent no-op.

So this is a narrow race window, not a guaranteed crash. It also only exists during the mixdown (`:48-58`), not the much longer frame loop, and only if the project uses hosted FabFilter/Melodyne stages. It is listed for completeness. The fix below closes it as a side effect, since the teardown never runs while an export is in flight.

---

## Repros

### A. Close mid-export → truncated MP4, no error **[human]**

1. Add at least one layer with a video source a minute or more long, so the frame loop runs long enough to interrupt.
2. File > Export MP4..., choose `test.mp4`, OK. The status bar shows "Exporting...".
3. While it still says "Exporting...", click the window's ✕ (or press Alt+F4). With a clean project there is no prompt at all. With a dirty one, answer **No** to "Save changes?".
4. **Bug:** the app closes immediately. Open `test.mp4` in any player. It plays normally but stops after however many seconds had been encoded at the moment of closing, with no indication that it is incomplete. There was no error message.
5. Check `%TEMP%`: an `acapella-export-audio-<guid>.wav` roughly the size of the whole mix is still there, and is never cleaned up.

### B. Re-export over a previous good export, then close → the good export is destroyed **[human]**

1. Export the project to `final.mp4` and let it finish ("Export complete: final.mp4"). Confirm it plays in full.
2. Make any tweak. Export again, choose `final.mp4`, and confirm the Save dialog's "replace?" prompt.
3. Once "Exporting..." has been showing long enough for the mixdown to finish and the frame loop to start (a few seconds for a short project), close the window as in Repro A.
4. **Bug:** `final.mp4` is now the truncated second export. The complete first export from step 1 no longer exists anywhere: `-y` truncated it when the encoder started (`ExportEngine.cs:77,140`), before the close.

### C. Headless: the close is not refused **[auto]**

`Close_WhileExportInFlight_IsCancelledWithStatus` (§Tests) sets `ExportMenuItem.IsEnabled = false`, exactly as `ExportButton_Click` does and as the existing `ExportPlaybackGateTests` already do. It then calls `window.Close()` and asserts the window is still visible with "Finish the export first." in the status bar. Today the window closes, so the test fails.

### D. Encoder behavior on parent death **[auto-able, run once by hand for this audit, not added to the suite]**

This is the scratch experiment described in §Root cause 3. It is recorded as evidence for the truncated-file claim. It is deliberately not proposed as a suite test: it would test ffmpeg's behavior, not this app's, and would need a child-process harness that has no precedent in the suite.

---

## Why the suite misses it

- **No test ever closes a `MainWindow` while the export flag is down in order to assert on it.** `ExportPlaybackGateTests.cs` does set `ExportMenuItem.IsEnabled = false` (`:20`, `:59`) and then calls `window.Close()` in `finally`. But that close is test cleanup, not an assertion, so today's "close is allowed" behavior is silently exercised on every run and never checked.
- **Bug Audit #8's gate tests are per-entry-point.** They cover Play and Record (`ExportPlaybackGateTests.cs`). Open/New were verified by review. There is no test or checklist of "every path that tears down the audio graph", which is where the missing fifth path would have shown up.
- **The export itself runs on a real background thread against real ffmpeg**, and `ExportEngineTests` drives `ExportEngine.Export` directly to completion. Nothing simulates the host process ending mid-export, because that isn't an `ExportEngine` concern. The defect is entirely in `MainWindow`'s lifecycle wiring.
- **The failure leaves nothing an assertion would see.** The app is gone, and the output file is valid enough to pass an `ffprobe` validity check like the one `ExportEngineTests` uses. Only its duration is wrong.

---

## Proposed fix

**Approach: add the same export gate the other four entry points use to the top of the existing `Closing` lambda. Cancel the close with `e.Cancel = true`, show "Finish the export first.", and return before both the unsaved-changes prompt and the teardown.**

### Change: `src/Acapella.App/MainWindow.xaml.cs:169-177`

Replace:

```csharp
        Closing += (s, e) =>
        {
            // Save-affordances spec: prompt FIRST, before anything is stopped or disposed -- a cancelled
            // close (Cancel, or Yes followed by a cancelled/failed save) must leave the app fully running.
            if (!ConfirmDiscardUnsavedChanges())
            {
                e.Cancel = true;
                return;
            }
```

with:

```csharp
        Closing += (s, e) =>
        {
            // Bug audit #15: the fifth "Finish the export first." gate (after Record, Play, Open, New --
            // bug audit #8). Closing is the most destructive of the five: the teardown below disposes
            // _hostedService out from under the export's mixdown, and the app then exits, killing the
            // Task.Run export thread without its finally (temp WAV leaked) -- ffmpeg sees stdin EOF and
            // finalizes a playable but truncated MP4 with no error shown. Checked BEFORE the unsaved-
            // changes prompt (same order as Open/New) so the user is never asked "Save changes?" for a
            // close that's then refused anyway. Must set e.Cancel and return before the teardown --
            // and must stay inside this one handler: a second Closing handler would still run the
            // teardown below on a refused close (Save-affordances spec, :345).
            if (!ExportMenuItem.IsEnabled)
            {
                StatusText.Text = "Finish the export first.";
                e.Cancel = true;
                return;
            }

            // Save-affordances spec: prompt FIRST, before anything is stopped or disposed -- a cancelled
            // close (Cancel, or Yes followed by a cancelled/failed save) must leave the app fully running.
            if (!ConfirmDiscardUnsavedChanges())
            {
                e.Cancel = true;
                return;
            }
```

Nothing else in the handler moves. The teardown (`:179-186`) is unchanged and still runs on every allowed close.

### Rejected alternatives

- **Show a `MessageBox` ("An export is in progress. Quit anyway?") instead of a status-bar refusal.** It would be more visible, but it has two problems. First, a "Yes, quit anyway" answer would still produce exactly the truncated file and leaked WAV this ticket is about: there is no cancellation path in `ExportEngine` to make quitting safe (see the next alternative). Second, a modal box inside `Closing` blocks the STA test thread in any `[StaFact]` that closes a window with the flag down, the hazard `Feature_Spec_2026-09-23_MultiFileImport.md:278` already documents for the dirty-project prompt. That would make this fix's own regression test, and the two existing `ExportPlaybackGateTests`, hang. The status-bar text matches the other four gates word for word.
- **Real export cancellation on close:** a `CancellationToken` into `ExportEngine.Export`, kill the encoder, delete the partial output and the temp WAV. That is the better long-term behavior, but it is a new feature, not a bug fix. It needs a new `ExportEngine` API, a decision about deleting a partial output whose `-y` has already destroyed the previous file, and a UI affordance for cancelling that isn't a window close. See Runners-up.
- **Block inside `Closing` until the export finishes (`exportTask.Wait()`, or pumping until `ExportMenuItem.IsEnabled` flips back).** A plain `Wait()` deadlocks. The export body's own completion calls, `Dispatcher.Invoke(() => StatusText.Text = ...)` (`:1164`/`:1168`) and `Dispatcher.Invoke(() => ExportMenuItem.IsEnabled = true)` (`:1172`), need the UI thread that `Wait()` is blocking, and so does every hosted-plugin lifecycle call during the mixdown (`WpfHostedPluginDispatcher.Invoke` → `Dispatcher.Invoke`). A nested pump avoids the deadlock but leaves a frozen-looking window for minutes, with no progress UI to explain it.
- **Make the export thread a foreground thread so the process outlives the window.** The window's `Closing` teardown would still dispose `_hostedService` under the mixdown (§Root cause 4). After `Dispatcher` shutdown, the export's own `Dispatcher.Invoke` calls and the hosted service's marshaled calls no longer run. The result is an invisible, headless process finishing (or failing) an export the user can no longer see or stop.
- **A new `_exportInFlight` bool instead of reusing `ExportMenuItem.IsEnabled`.** All four existing gates, and `ExportPlaybackGateTests`, already use `ExportMenuItem.IsEnabled` as the flag. A second flag would have to be kept in sync at `:1152` and `:1172` for no gain.

---

## Ordering subtleties

**1. The gate must set `e.Cancel = true` *and* `return` before the teardown, not just one of the two.**
- **Counter-example A (`return` without `e.Cancel`):** the handler exits early, skipping the teardown, but the close proceeds. The window closes and the app exits with the export thread killed, which is today's bug unchanged. Worse, the preview engine, mix engine and hosted service are never disposed (JUCE plugin instances and ARA sessions released only by process death).
- **Counter-example B (`e.Cancel = true` without `return`):** execution falls through to the teardown, disposing `_previewEngine` and `_hostedService` on a window that stays open. The next Play then runs against a disposed preview engine. The export's mixdown loses its hosted instances mid-read (§Root cause 4) even though the close was "refused".

**2. The gate must go in the existing `Closing` lambda, not in a second `Closing` handler.**
- **Counter-example:** `Closing += (s, e) => { if (!ExportMenuItem.IsEnabled) { e.Cancel = true; ... } };` added as a separate subscription. WPF invokes every subscribed `Closing` handler even after one sets `e.Cancel`, and the existing lambda never checks `e.Cancel` before its teardown. The result is Counter-example B above: the close is refused but everything is disposed. `Feature_Spec_2026-09-23_SaveAffordances.md:345` already records this rule for the dirty prompt, and it applies identically here.

**3. The gate must come before `ConfirmDiscardUnsavedChanges()`, not after it.**
- **Counter-example:** with a dirty project mid-export, the user closes. With the gate placed second, they are first asked "Save changes to <name>?".
  - If they answer **No**, meaning "discard my edits and quit", they are then told "Finish the export first." and the app stays open. Their explicit answer was silently ignored, and they'll be asked the same question again on the next close.
  - If they answer **Yes**, a save runs, possibly behind a Save As dialog for an untitled project, for a close that is then refused anyway.
- Putting the gate first matches the order `OpenProjectButton_Click` (`:1094` then `:1100`) and `NewProjectMenuItem_Click` (`:1181` then `:1187`) already use, and means the prompt is only ever shown for a close that will actually happen.

---

## Deliberately not changed

- **The teardown block (`:179-186`) and `ConfirmDiscardUnsavedChanges()`.** Both are untouched and still run, in the same order, for every close that isn't refused.
- **Windows log-off / shutdown mid-export.** `Window.Closing` is not raised when the session ends; WPF routes that through `Application.SessionEnding` instead, which this app doesn't handle. A log-off during an export will still truncate the output. Handling it needs an `App`-level `SessionEnding` handler, a separate, app-wide decision with the same shape as the missing `DispatcherUnhandledException` handler already tracked elsewhere.
- **Ctrl+S / Save As during an export.** Save stays ungated. It only reads live plugin state (`PullLiveState` → `GetState` on the dispatcher thread), which hosts routinely do while an audio thread is processing, and it tears nothing down. It isn't part of this defect.
- **`-y` overwriting the previous file at export start** (Repro B's amplifier). Writing to a temp name and renaming on success would protect the old file from *any* failed export, not just this one. It is a real improvement, but a change to `ExportEngine`'s output contract, and Repro B stops being reachable through a window close once the close is gated. See Runners-up.
- **The temp WAV leak on other abnormal exits** (crash, Task Manager kill). This is the same `finally`-never-runs shape, but with no in-app trigger once the close is gated.

---

## Tests

### `tests/Acapella.App.Tests/ExportCloseGateTests.cs` (new file)

Runs under the shared `TestAppHost` (`ShutdownMode.OnExplicitShutdown`, parallelization disabled). A freshly constructed `MainWindow` has a clean session, so `ConfirmDiscardUnsavedChanges()` returns `true` without a `MessageBox`, and neither test can hang on a modal dialog. No real export is started. The tests set the same flag `ExportButton_Click` sets, following the existing `ExportPlaybackGateTests` precedent.

```csharp
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #15: closing the main window while an export is in flight
/// (ExportMenuItem.IsEnabled == false) must be refused like Play/Record/Open/New are (bug audit #8)
/// -- otherwise the Closing teardown disposes the hosted service under the export and the app exits,
/// killing the export thread and leaving a truncated MP4. See
/// reviews/Bug_Audit_2026-09-25_WindowCloseAbandonsExport.md.</summary>
public class ExportCloseGateTests
{
    [StaFact]
    public void Close_WhileExportInFlight_IsCancelledWithStatus()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            window.ExportMenuItem.IsEnabled = false;   // what ExportButton_Click does while Task.Run is running

            window.Close();

            Assert.True(window.IsVisible, "Closing must be refused while an export is in flight.");
            Assert.Equal("Finish the export first.", window.StatusText.Text);
        }
        finally
        {
            window.ExportMenuItem.IsEnabled = true;    // "export finished" -- lets cleanup actually close it
            if (window.IsVisible) window.Close();      // guarded: before the fix, the Close() above already closed it
        }
    }

    [StaFact]
    public void Close_WithNoExportInFlight_StillCloses()
    {
        // Regression guard: the gate must only refuse while the flag is down.
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        window.Show();

        window.Close();

        Assert.False(window.IsVisible);
    }
}
```

### Edit: `tests/Acapella.App.Tests/ExportPlaybackGateTests.cs`

`PlayButton_WhileExportInFlight_RefusesWithStatus` (`:20`) and `RecordSetup_WhileExportInFlight_RefusesWithStatus` (`:59`) both leave `ExportMenuItem.IsEnabled = false` and then call `window.Close()` in `finally`. After this fix, that close is refused. Both tests would still pass, but each would leak a live `MainWindow`, never disposing its preview engine, hosted service or timers. In both `finally` blocks, replace:

```csharp
        finally
        {
            window.Close();
        }
```

with:

```csharp
        finally
        {
            window.ExportMenuItem.IsEnabled = true;   // bug audit #15: a close with the export flag down is now refused
            window.Close();
        }
```

`PlayButton_WithNoExportInFlight_StillReportsNoLayers` never lowers the flag and needs no edit.

**No engine-level test.** The defect is entirely in `MainWindow`'s `Closing` wiring. `ExportEngine` is untouched, and nothing about its behavior changes. The encoder's behavior on parent death was verified once by hand (Repro D) as evidence, not as a regression surface this fix could break.

**Regression carve-out (per CLAUDE.md):** `MainWindow.xaml.cs` is touched. Re-run the whole `Acapella.App.Tests` project once. Every `[StaFact]` in it ends by closing a `MainWindow`, so every one of them exercises the edited `Closing` handler, and the two `ExportPlaybackGateTests` edited above are the ones whose cleanup this fix directly affects.

---

## Acceptance criteria

### [auto]
- `Close_WhileExportInFlight_IsCancelledWithStatus` passes. It fails before the fix: `window.IsVisible` is `false` after `Close()`.
- `Close_WithNoExportInFlight_StillCloses` passes both before and after the fix.
- All of `Acapella.App.Tests` passes (regression carve-out), including the edited `ExportPlaybackGateTests`.
- The solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- The gate is the first statement inside the **existing** `Closing` lambda, not in a second handler (subtlety 2), and it comes before `ConfirmDiscardUnsavedChanges()` (subtlety 3).
- The gate sets `e.Cancel = true` **and** returns (subtlety 1), and it uses `ExportMenuItem.IsEnabled`, the same flag as the four existing gates.
- The status text is exactly `"Finish the export first."`.
- The teardown block and the dirty-prompt block are otherwise unchanged.
- `ExportEngine.cs` is untouched.
- Both `ExportPlaybackGateTests` `finally` blocks that lower the flag restore it before `Close()`.

### [human]
- Repro A after the fix: pressing ✕ or Alt+F4 while "Exporting..." is showing leaves the app open, with the status bar reading "Finish the export first.". The export then completes normally, the status shows "Export complete: ...", and the MP4 plays for its full length. After that, ✕ closes the app normally, prompting first if the project is dirty.
- Repro B after the fix: the overwrite-then-close sequence no longer happens, since the close is refused. The re-export completes and `final.mp4` is the full new export.
- With a dirty project mid-export, ✕ shows **no** "Save changes?" prompt, only the status-bar refusal (subtlety 3).
- No regression: with no export running, ✕ / Alt+F4 behave exactly as before, including the dirty-project prompt and its Cancel.

---

## Runners-up (not specced)

- **A failed File > Open destroys the current project's live plugin state.** Traced, not specced. `ProjectSession.Open` calls `_mixEngine.ReleaseAllHostedInstances()` *before* `LoadFromFile` (`ProjectSession.cs:137-138`), so a file that fails to parse still releases every FabFilter instance and every Melodyne ARA session of the project that stays open. The Save-affordances spec's failed-Open analysis (`Feature_Spec_2026-09-23_SaveAffordances.md:177`) checked only that path and dirty state are untouched. Two parts of this are real losses:
  - FabFilter tweaks: any change not yet captured by the 500 ms editor poll.
  - Melodyne edits: all of the session's edits, since they are never persisted anywhere (`AraArchiveKey` unused, per `Bug_Audit_2026-07-16_AraMelodyne.md:38-40`).

  The fix direction is to load and parse first, then release, then restore. That has to be argued against Bug Audit #5's "release FIRST" invariant, which is about the restore, not the parse.
- **Opening any non-project `*.json` "succeeds" as an empty project, and Ctrl+S then overwrites that foreign file.** Traced. The Open filter admits `*.json` (`MainWindow.xaml.cs:1102`). `JsonSerializer.Deserialize<ProjectFileDto>` of any JSON *object* returns a DTO with defaults and an empty `Layers` list, and only a literal `null` throws (`ProjectPersistenceService.cs:48-50`). `Open` then sets `CurrentFilePath` to that file, clean (`ProjectSession.cs:140`). A user who opened the wrong file, then built a project in the "empty" session and pressed Ctrl+S, overwrites the foreign JSON with no dialog (`SaveProject`, `:1032`). There is no format marker or version field to reject it.
- **Exporting over one of the project's own source files.** Unverified. If the export path equals an uploaded layer's `.mp4` source, `-y` truncates it while a `VideoFrameStreamSource` decoder may still be reading it. That would destroy the source media and corrupt the output. Whether ffmpeg's Windows file sharing allows the truncate while the decoder holds the file open was not checked.
- **No export cancel / temp-then-rename output.** A real "Cancel export" (kill the encoder, delete the partial file and temp WAV) plus writing to a temp name and renaming on success would make every abnormal export end safe, including crashes and log-off, not just the window close gated here. It is a feature, and it pairs naturally with the export-progress candidate in `Improvement_Proposal_2026-09-24.md:88-100`.
- **Project save is a non-atomic `File.WriteAllText`** (`ProjectPersistenceService.cs:43`). A crash or power loss mid-write leaves a truncated, unparseable project. Low reachability, but it's the user's only copy.
- **Duplicate `CellIndex` in a project file makes `RestoreTracksFromLayers` throw** (`ToDictionary`, `MainWindow.xaml.cs:784`). This happens after `_session.Open` has already replaced the layers, so the UI is left half-restored. Only reachable through a hand-edited or corrupt file. Not traced further.
