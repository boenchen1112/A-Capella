# Bug Audit #2 — Preview Playback Desync + Post-UI-Redesign Codebase (for Sonnet to fix)

> Audit of the codebase as of commit `f4e17af` (two-screen UI + PreviewPlaybackEngine). Reported symptom: **preview video does not match the audio, and audio ends first**. Root causes for that symptom are isolated in section A (there are several, compounding). Section B covers the rest of the overall audit. Severity-ordered. Do not fix anything not listed without flagging it first.
>
> The previous audit (`Bug_Audit_2026-07-12.md`) items C1–C5/H1–H5/M1–M7 were verified as addressed in commits `26f55e4..1c6d666`; this audit is about the *new* code (preview engine, two-screen UI, dynamics DSP) plus regressions those changes introduced.

---

## A. Root causes of the video/audio desync (preview playback)

The symptom is over-determined — four independent defects all push in the same direction (video late relative to audio). Fix A1+A2 first; they are the architecture problem. A3/A4 are per-layer offset errors on top.

### A1. Open-loop video clock: frames advance one-per-iteration with no drop/catch-up **[primary cause]**
**File:** `src/Acapella.Engine/Preview/PreviewPlaybackEngine.cs:142-175` (`FrameLoop`, `RenderCurrentFrame`)

`PositionMs` advances on a wall-clock `Stopwatch`, but the video frames consumed advance by exactly **one frame per loop iteration** (`GetNextFrame()` once per source per pass). Nothing reconciles the two. Any iteration that takes longer than 33ms makes the video permanently one frame later than the clock; the error only ever accumulates. And iterations *will* exceed 33ms, because each pass does: 4 sequential blocking ffmpeg pipe reads (`_stdout.Read` per layer, `LayerFrameSource.cs:96`) + a full SkiaSharp composite + **a synchronous `Dispatcher.Invoke` to the UI thread inside the render path** (see A2). Meanwhile audio runs at true real time in WASAPI. Net effect: video drifts behind, audio reaches its end first — exactly the reported symptom. The "~230fps spike" cited in the doc comment measured decode+composite only, without UI marshaling or real camera-resolution sources.

**Fix (with A2):** make video chase a clock instead of assuming one iteration == one frame. Compute `targetFrameIndex = (int)(clockPositionMs / 1000.0 * fps)`, track `consumedFrames` per source, and on each pass call `GetNextFrame()` in a loop until `consumedFrames == targetFrameIndex` (dropping intermediate frames), rendering only the last. If the loop is ahead of the clock, skip rendering entirely that pass.

### A2. No shared clock with audio, and the clock starts before playback actually does
**Files:** `PreviewPlaybackEngine.cs:127-140` (`Play`), `:203-218` (`StartAudio`), `src/Acapella.App/MainWindow.xaml.cs:54-62` (FrameReady handler)

Three sub-problems:
1. The `Stopwatch` is *not* the audio clock. `WasapiOut` adds its own start latency and buffer latency; audio actually begins tens of ms after `Play()` returns, and its position thereafter is authoritative. The video should slave to audio position, not to an independent stopwatch.
2. The first `GetNextFrame()` call blocks while each layer's freshly spawned ffmpeg process probes and decodes its first frame (hundreds of ms for 4 processes) — the stopwatch is already running, so the video starts life already late, and per A1 it can never catch up.
3. `FrameReady?.Invoke` is raised on the frame-loop thread, and MainWindow's handler runs `Dispatcher.Invoke` (blocking). The frame loop is therefore throttled by UI-thread availability (slider updates, layout). UI jank becomes video lag becomes permanent desync.

**Fix:**
- Wrap the built mix in a small `PositionTrackingSampleProvider` (counts samples handed to the sink; `PositionMs = samplesRead / rate`) and use **that** as the master clock in `FrameLoop` instead of the stopwatch. This automatically absorbs WASAPI start latency and any audio hiccups. Keep a stopwatch fallback only for the `FakeAudioSink` test path (sink can expose `SupportsPosition`).
- Prime the pipeline: decode the first frame from every source *before* starting the audio sink and the loop.
- Change MainWindow's handler to `Dispatcher.InvokeAsync` with latest-frame-wins semantics (store the bitmap in a one-slot mailbox, invalidate; dispose any overwritten frame) so the render loop never blocks on the UI thread.

### A3. Negative sync shift is silently dropped in the preview's video path (works in export)
**File:** `PreviewPlaybackEngine.cs:182-201` (`CreateFrameSource`)

`ExportEngine.CreateFrameSource` passes `layer.GetShiftMs()` straight through, so `VideoFrameStreamSource` converts a negative shift into an `-ss` head-skip (`LayerFrameSource.cs:55`). The preview's `CreateFrameSource` instead passes `residualHoldMs` (clamped `>= 0`) as the shift argument and folds position into `effectiveTrimStart = TrimStartMs + Math.Max(0, positionMs - Math.Max(0, shiftMs))` — for a **negative** shift (the normal case: every recorded layer carries `-CalibratedOffsetMs`), the `(-shift)` head-skip appears nowhere. The layer's audio skips its head (via `AudioShiftHelper`) but its video doesn't, so every recorded layer's preview video lags its own audio by the calibration offset (typically 100–300ms). Export is correct; preview is wrong — which also makes the bug confusing to verify by exporting.

**Fix:** in `CreateFrameSource`, include the negative-shift skip in the media-time math: `effectiveTrimStart = TrimStartMs + max(0, -shiftMs) + max(0, positionMs - max(0, shiftMs))`. Add a preview-vs-export parity test: layer with `CalibratedOffsetMs = 200`, assert the first frame rendered at position 0 equals the frame export produces at frame 0.

### A4. Trim-out duration isn't reduced by the negative-shift head-skip (preview *and* export)
**File:** `LayerFrameSource.cs:62-67`

When both a trim-out and a negative shift are set: audio is trimmed to `(trimEnd - trimStart)` then loses `|shift|` from its head → net length `(trimEnd - trimStart) - |shift|`. Video passes `-t (trimEnd - trimStart)` unmodified while `-ss` already includes the `|shift|` skip → video runs `|shift|` **longer** than the layer's audio before freezing. Another "audio ends before video" contributor, and it desyncs the freeze-frame moment.

**Fix:** `durationSeconds = Math.Max(0, (trimEnd - trimStart) / 1000.0 - (shiftMs < 0 ? -shiftMs / 1000.0 : 0))`.

### A5. Project duration is derived from audio only; video-only and audio-less layers break it
**Files:** `PreviewPlaybackEngine.cs:112-125` (`ComputeDurationMs`), `ExportEngine.cs:35-38`

`DurationMs = max(shifted audio length)`. An uploaded video with no audio track decodes to 0 samples: preview treats the project as shorter than the visible video (its tail is never played), and if *all* layers are audio-less, `DurationMs = 0` / export throws "No decodable audio". Also relevant when a video stream outlasts its own audio stream inside one file (common with dshow captures stopped mid-frame). **Fix:** per layer, duration = max(audio length after trim/shift, ffprobe'd video duration after trim/shift); extend `MediaProbe` to return duration as a double (it already parses it, then throws the value away returning only a bool).

---

## B. Remaining audit findings

### HIGH

**B1. PreviewPlaybackEngine has no thread-safety, and the UI calls it from three places concurrently.**
`MainWindow` invokes `SetLayers/Seek/Play/Stop` from: debounced `RefreshPreviewLive` (`Task.Run`), transport clicks (`Task.Run`), and the engine's own frame-loop thread calls `StopInternal` when playback ends. None of it is synchronized: two overlapping `Task.Run` refreshes can interleave `Stop→Play`, `_frameSources` can be disposed by `StopInternal` while a not-yet-joined loop is mid-`RenderCurrentFrame` (ObjectDisposedException on the ffmpeg stream), and two audio sinks can end up playing simultaneously (audible doubling). The `_previewRefreshGeneration` counter only guards a UI label. **Fix:** serialize all public engine operations through one lock (or a single-threaded work queue); make `Seek` while playing atomic; have `FrameLoop` re-check `_stopRequested` under the same lock before touching sources. Add a stress test: 50 rapid interleaved `Seek/SetLayers/Play/Stop` calls from parallel tasks must not throw and must end with at most one sink playing.

**B2. `SetLayers`/`ComputeDurationMs` fully decodes every layer's audio via ffmpeg — on every debounced parameter change.**
Every FX slider tweak (300ms debounce) triggers `SetLayers` → N full-file ffmpeg decodes, then `Seek` → N more decodes in `StartAudio` (`PreviewPlaybackEngine.cs:203-210`) plus N ffmpeg video spawns. Multi-second latency per adjustment on real files, plus a spawned-process storm. **Fix:** (a) duration from ffprobe (see A5), not full decode; (b) cache decoded mono audio per `(SourcePath, mtime)` — invalidate on source change, reuse across SetLayers/StartAudio/export; trim/shift are cheap array ops on the cached buffer.

**B3. Automatic pitch correction runs synchronously inside every mix rebuild (deferred L9, now load-bearing).**
`MixEngine.BuildMix` → `AutoPitchCorrector.Correct` re-runs YIN + Rubber Band over the full track on every Play/Seek/param-change rebuild once a layer selects Automatic2B — with B2's rebuild frequency this means seconds of stall per slider move, all uncached. **Fix:** cache corrected audio keyed by `(layerId, source hash/mtime, backend)`; invalidate only when the source or backend selection changes (FX parameters downstream of pitch don't affect it).

**B4. Guide-track recording alignment depends on ffmpeg's dshow startup time, which is unmeasured.**
`RecordSetupWindow.RecordButton_Click:162-164` starts the capture process, then immediately starts the guide. But `FfmpegCaptureSession.Start` returns when the *process* launches, not when capture begins — dshow device init takes 0.5–2s, variable. The recorded file's t=0 is therefore a variable, unmeasured time *after* the guide started, and the stored `CalibratedOffsetMs` (measured over a WASAPI Stereo-Mix loopback, a different path than dshow capture) cannot account for it. Cross-layer alignment error of hundreds of ms, different every take. **Fix options (pick one):** (a) start the guide only after ffmpeg reports capture progress (`-progress pipe:2`, wait for first `frame=`/`out_time=` line — `GetRecentStderrLines` infra already exists); (b) after recording, cross-correlate the recorded mic audio against the guide mix to measure the actual offset per take (the correlator exists: `CrossCorrelator`) and store it in `CalibratedOffsetMs`. (b) is more robust (headphone users defeat it — fall back to (a) when correlation confidence is low).

### MEDIUM

**B5. `WasapiPreviewAudioSink` always plays through the system default device**, ignoring whatever output device the user picked in RecordSetupWindow / calibration. Regression vs the pre-redesign preview. Fix: let MainWindow pass an output device id into the engine/sink.

**B6. Limiter processes interleaved stereo with one envelope state** (`LimiterSampleProvider.cs:33-49`): the gain smoother sees L,R,L,R alternately, so adjacent samples of different channels get different gains — stereo image wobble and added distortion, worst with hard panning (limiter sits after `PanningSampleProvider`). Fix: process in L/R pairs — compute `max(|L|,|R|)`, update the envelope once, apply the same gain to both samples. (Compressor is pre-pan mono, so it's fine.)

**B7. Mixing screen has no transport** — you can't hear the layer while adjusting FX unless you left the Editor screen playing (and B1 makes that racy). The v5 plan makes this a requirement (mixer playback); until then flag as a known gap, not silently fine.

**B8. Sidebar row number ≠ grid cell when rows are filled out of order.** `LayerRowViewModel.SlotNumber` is row creation order; `LayerModel.LayerId/CellIndex` is source-attach order (`_layers.Add`). Fill row 2 before row 1 and "Layer 2" in the sidebar renders in the grid's top-left cell. Fix (interim): assign `row.Layer.CellIndex` from the row's slot, or renumber rows on attach. The real fix is v5's rename + grid-identification work.

**B9. Empty track rows and BPM vs saved projects:** saving a project with un-filled rows silently drops them (only `_layers` is persisted); reopening also renumbers slots 1..N. Acceptable, but nothing tells the user. Also `_metronomeBpm` from a loaded project is only surfaced the next time the record dialog opens — fine, note only.

**B10. `PeakNormalizer` rescales the whole export mixdown after per-layer limiters** (`ExportEngine.cs:58`): if any layer clips the sum, the user's carefully-set limiter ceiling (-0.3dB) is scaled down too, and export loudness ≠ preview loudness (preview never normalizes). Fix: replace with a true master brick-wall limiter applied identically in preview and export (v5 plan puts a master volume + limiter at the mix bus), or at minimum surface "export was normalized by X dB" in the status text.

### LOW / polish

- **B11.** `PreviewPlaybackEngine.cs:46-68` — the class doc comment block is duplicated verbatim (copy-paste artifact; the first copy is orphaned text above the real one).
- **B12.** `Seek` while paused renders one frame by spinning up N ffmpeg processes and killing them (`:258-263`); with scrub-drag this is a process storm. Debounce scrub-seeks (only seek on mouse-up is already the behavior — fine — but `RefreshPreviewLive` also calls `Seek` per debounce tick; combine with B2's caching).
- **B13.** `FrameLoop` end-of-playback: after `PositionMs >= DurationMs` it renders one last frame and stops — but audio may still have up to one WASAPI buffer left; harmless once A2 slaves video to audio position.
- **B14.** `MainWindow.RefreshPreviewLive` disposes `_compositedFrame` on the UI thread while a `FrameReady` dispatch may be about to draw it (`:207-210` vs paint handler) — rare use-after-dispose crash; the A2 mailbox fix should own all frame lifetime.
- **B15.** `TimelineSlider` lives in a 28px-high ScrollViewer docked at the very bottom of the transport stack (`MainWindow.xaml:109-116`); combined with the fixed 640x480 preview it's easy to push off-screen at small window sizes — the "progress bar stuck at the bottom / invisible" complaint. Addressed properly by the v5 transport redesign; interim: give the preview panel `MaxHeight` scaling instead of fixed size.
- **B16.** `TrimEndText` binds with default `UpdateSourceTrigger` (on focus loss) while `TrimStartMs` also updates on focus loss — fine — but invalid text silently means "to end" with no visual cue; add a hint (v5 sidebar rework).
- **B17.** `MetronomeEngineTests`/`PreviewPlaybackEngineTests` never assert A/V correspondence. Add the parity + drift tests from A1/A3 so this class of bug can't pass `[auto]` again: e.g. fixture video whose frame color encodes its timestamp (lavfi `testsrc`), play 5s, assert rendered frame timestamp within 2 frames of audio position, at both t=1s and t=4s.

## Verified correct / no action

Compositor Rgba8888 fix (C1) still in place; export offset application (C2) correct for both signs; guide track now actually plays during recording (C3) with zero added delay (C4); trim math in `TrimHelper`/`AudioShiftHelper` correct in isolation (clamping, empty-array edges); `CompressorSampleProvider` gain computer (hard-knee and quadratic soft-knee branches) matches the standard curve; DTO round-trip includes the new trim + dynamics fields; zombie-layer validation works and surfaces stderr; dshow audio devices now come from the dshow list (H3 fix); `-rtbufsize`/fast-preset capture args present (H4 fix, per commit; `GetRecentStderrLines` exists).
