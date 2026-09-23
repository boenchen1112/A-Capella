# Bug Audit #10: With the real audio device, preview playback never reaches its end when a layer's video outlasts its audio

> Audit as of `f175a05` (Bug Audit #9 fix landed). One new bug is specced in full. It has not been flagged in any earlier `reviews/*.md` doc.
>
> **How it differs from earlier findings:**
> - It is an **interaction regression between two earlier fixes** in `Bug_Audit_2026-07-12_PreviewPlayback.md`:
>   - A2 (`:20-31`) made the preview's clock "samples actually pulled by the audio sink".
>   - A5 (`:47-50`) made the project's length the ffprobe **container** duration, so video-only layers and video that outlasts its audio count.
>   - Each fix is right on its own. Together, the clock can stop short of the length it is racing toward.
> - It is **not** B13 (`:88`). B13 is about the last WASAPI buffer still playing after the loop ends, which assumes the audio outlasts the video. This is the opposite case: the video (or container) outlasts the audio.
> - It is **not** any known deferred item. #5 runner-up "spurious PlaybackStopped on every seek" is about an extra event; this is about the event **never** firing. #9 runner-up "stale meters after Stop" is cosmetic and unrelated.
>
> **Confidence:**
> - **High on the mechanism.** It is deterministic from the code, and I reproduced it end-to-end against the built engine (`tests/Acapella.Engine.Tests/bin/Debug/net8.0/Acapella.Engine.dll`, built 13:25, after the last `src/Acapella.Engine` commit `bb41f3d` at 12:53). The run used a throwaway console app in `%TEMP%` with the same real-time pull loop as the suite's `SimulatedRealtimeAudioSink`. Results are in §Repros.
> - **Medium on how large the gap is for real webcam takes.** My MKV fixture used the app's own capture codec arguments, but a `lavfi` source, not a live dshow device. Every MKV I produced had decoded audio shorter than the container (3–18 ms). For real takes the gap is probably the same or larger. It does not change the verdict: any gap above zero triggers the bug.

---

## The bug in one sentence

With the real audio device, the preview's frame loop ends only when the **audio-sample clock** reaches the project's **container-derived duration**. When the longest layer's audio is shorter than that duration, the clock stops at the end of the audio and the loop never ends. This happens for:
- every untrimmed recorded take;
- any video-only upload;
- any upload whose video outlasts its audio.

The visible result is that playback never stops on its own:
- ▶ stays highlighted.
- The status stays "Playing preview.".
- Pressing ▶ again is silently ignored.
- The engine keeps compositing frames indefinitely.
- Where the video outlasts the audio, the **picture freezes at the moment the audio ends**, although export renders that video to the end. A video-only layer freezes on its **first frame**.

---

## Root cause

Five facts combine.

### 1. The loop's only natural exit is `PositionMs >= DurationMs`

**File:** `src/Acapella.Engine/Preview/PreviewPlaybackEngine.cs`

| Line | What it does |
|---|---|
| `:257` | `while (!_stopRequested)`: apart from an explicit Stop, the loop runs until the break at `:276`. |
| `:259-261` | `elapsedMs` is `positionTracker.PositionMs` when `useAudioClock`, otherwise the stopwatch. |
| `:262` | `PositionMs = Math.Min(startPositionMs + elapsedMs, DurationMs)` |
| `:274` | `RenderComposite(lastFrames)` runs on **every** pass, so each pass composites a new canvas bitmap and raises `FrameReady` (`:286-298`). |
| `:276` | `if (PositionMs >= DurationMs) break;` is the only natural exit. |
| `:280` | `Thread.Sleep(5)`, then loop. |
| `:283` | `StopInternal(raiseStoppedEvent: true)` runs only after the loop exits. It clears `IsPlaying` (`:352`) and raises `PlaybackStopped` (`:353`). |

### 2. With the real device, the clock is "samples pulled", and it stops when the mix runs out

- `useAudioClock = _audioSink.DrivesRealtime` (`:237`). The app's sink is `WasapiPreviewAudioSink`, whose `DrivesRealtime` is `true` (`:40`). The interface default is `false` (`:29`), which only the test fakes use.
- `StartAudio` (`:314-330`) builds `mix` (`:319`), wraps it in an `OffsetSampleProvider` skip when `positionMs > 0` (`:321-323`), wraps that in `PositionTrackingSampleProvider` (`:327`), and hands it to the sink (`:328`).
- `PositionTrackingSampleProvider.Read` (`PositionTrackingSampleProvider.cs:31-36`) adds **only the samples the source actually returned** (`:33-34`). `PositionMs` is `samplesRead / channels / rate` (`:22-29`).
- The mix is finite:
  - `MixEngine.BuildMixWithMasterVolumeHandle` (`MixEngine.cs:145-171`) is a plain NAudio `MixingSampleProvider` (`:148`, `ReadFully` left at its default `false`). It drops inputs that return 0 and returns 0 once all are gone.
  - Each input ends at its decoded length: `ArraySampleProvider.Read` returns 0 once exhausted (`ArraySampleProvider.cs:20-24`).
  - The volume, limiter (`:166`) and meter-tap (`:168`) stages pass the short read through. `MeterTapSampleProvider.cs:37-42` returns `read` unchanged when it is ≤ 0.

So once the mix is exhausted, `positionTracker.PositionMs` **stops increasing**. This does not depend on what `WasapiOut` does with a 0-length read. Whether it keeps pulling or stops, the tracker counts nothing more.

### 3. The target length comes from the container, not from the audio

- `SetLayersCore` sets `DurationMs = _timeline.DurationMs(_layers, _sampleRate)` (`PreviewPlaybackEngine.cs:210`).
- `LayerTimeline.DurationMs` takes the max over layers (`LayerTimeline.cs:40-41`). Each layer is `rawMs = _probeDurationSeconds(path) * 1000` (`:48`), after trim, shift and tail (`:49-56`).
- `_probeDurationSeconds` is `MediaProbe.GetDurationSeconds` (`LayerTimeline.cs:22`). That is ffprobe `-show_entries format=duration` (`MediaProbe.cs:29-31`), which is the **container** duration: the longer of the video and audio streams, plus container padding.
- The audio comes from a full decode instead: `AudioInput` → `AudioDecodeCache` → `AudioDecoder.DecodeToMonoFloat` (`LayerTimeline.cs:61-67`, `AudioDecoder.cs:9-40`). A file with no audio stream decodes to **zero samples**, and that is treated as normal (`AudioDecoder.cs:31-34`).
- Trim and shift keep the gap between the two lengths:
  - With `TrimEndMs == null`, `TrimHelper` ends the audio at the **decoded** length (`TrimHelper.cs:13-15`). `DurationMs` uses the **container** length (`LayerTimeline.cs:49`).
  - A negative shift (every recorded layer carries `-CalibratedOffsetMs`) removes the same amount from both (`LayerTimeline.cs:52-53` and `:65`).
  - The FX tail is added to both, exactly: `LayerTimeline.cs:55` on the duration side, and `HostedPluginSampleProvider`'s flush on the audio side.

  Only a trim-out set **below** the decoded length makes the two agree, because then both clamp to it.

### 4. Common inputs have decoded audio shorter than their container

These measurements use the app's own decode path (`ffmpeg -i X -f f32le -ac 1 -ar 44100 -`) and `ffprobe format=duration`.

| Fixture | Container (`DurationMs` source) | Decoded audio | Gap |
|---|---|---|---|
| MP4, 2 s video, 2 s AAC @44.1k (the suite's `CreateFixtureClip`) | 2000 ms | 2020 ms | **audio longer**: no bug |
| MKV, capture arguments (`FfmpegCaptureSession.cs:64-72`: libx264 ultrafast crf 23 + AAC), 2 s / 2 s, 48 kHz in | 2021–2023 ms | 2005–2020 ms | **3–16 ms short** |
| MKV, video 2.0 s / audio 2.2 s | 2223 ms | 2205 ms | **18 ms short** |
| MKV, video 2.2 s / audio 2.0 s | 2200 ms | 2020 ms | **180 ms short** |
| MP4, video 3 s / audio 2 s | 3000 ms | 2020 ms | **980 ms short** |
| MP4, video only (`-an`) | 3000 ms | 0 ms | **the whole length** |

Every recorded take is an MKV written with those arguments, so every **untrimmed recorded layer** has a gap. A recorded layer is the project's longest layer whenever no other layer's audio is longer.

### 5. The UI trusts `IsPlaying` / `PlaybackStopped`

**File:** `src/Acapella.App/MainWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:167` | `PlaybackStopped` is the only thing that un-highlights ▶ (`SetPlayStopContent(false)`, `:391-394`). |
| `:892` | `PlayButton_Click`: `if (_previewEngine.IsPlaying) return;` Pressing ▶ does nothing and shows no message. |
| `:907` | Status "Playing preview." is never replaced. |
| `:274-277` | Space checks `IsPlaying`, so it runs Stop+rewind (`StopButton_Click`, `:919-929`) instead of Play. |
| `:153-166`, `:366-384` | Every composited frame from `:274` of the engine is pumped to the UI: the canvas is invalidated and the slider and time readout are updated. |

---

## Repros

### Empirical run (already done, against the built engine)

This was a console app in `%TEMP%` (deleted afterwards). It referenced the built `Acapella.Engine.dll` and used a sink identical to `SimulatedRealtimeAudioSink` (`PreviewPlaybackEngineTests.cs:23-65`). The setup was `new PreviewPlaybackEngine(128, 128, fps: 10, audioSink: sink, NoHostedPluginsAvailable.Instance)` → `SetLayersAsync` → `PlayAsync`, then waiting `DurationMs + 3000` ms for `PlaybackStopped`:

```
control mp4 (audio >= container): DurationMs=2000.0 PositionMs=2000.0 IsPlaying=False stoppedOnOwn=True  framesRendered=131
video 3s / audio 2s mp4:          DurationMs=3000.0 PositionMs=2020.1 IsPlaying=True  stoppedOnOwn=False framesRendered=386
video-only mp4:                   DurationMs=2000.0 PositionMs=0.0    IsPlaying=True  stoppedOnOwn=False framesRendered=322
capture-args mkv (RecordedAV):    DurationMs=2021.0 PositionMs=2005.4 IsPlaying=True  stoppedOnOwn=False framesRendered=323
```

The control stopped by itself at exactly `DurationMs`. The other three were still "playing" 3 s after their end, compositing at the same rate as live playback (about 64 composites/s; `Sleep(5)` rounds up to the Windows timer tick).

### A. [auto] A video-only layer's picture never moves and playback never stops

1. Upload a video file with no audio track (or build one: `ffmpeg -f lavfi -i color=... -an clip.mp4`).
2. Press ▶.
3. **Expected:** the video plays to its end, then ▶ un-highlights.
   **Actual:** the mix has 0 samples, so the tracker stays at 0 and `PositionMs` stays at 0 (`:262`). `targetFrameIndex` stays 0 (`:263`), so no frame after the primed first frame is ever pulled (`:267`). **The picture stays on frame 0**, the time readout stays `0:00:00`, and ▶ stays highlighted forever.

   Export of the same project renders the whole clip (`ExportEngine.cs:75`, `:85-90`), so preview and export disagree.

### B. [auto] Video longer than its audio: the picture freezes when the audio ends

1. Upload a clip whose video runs past its audio (see the 3 s/2 s fixture above; a real phone clip whose audio was cut early behaves the same).
2. Press ▶.
3. **Actual:** at about 2.0 s the tracker stops. The picture **freezes on the frame at about 2.0 s**. The readout stops at `0:00:02` of `0:00:03`. ▶ stays highlighted, the status still says "Playing preview.", and pressing ▶ does nothing.

   Export shows the video running to 3 s.

### C. [auto, plus a human check of the real device] Any untrimmed recorded take: playback never "finishes"

1. Record a layer (any length) and leave its trim-out empty.
2. Press ▶ and let it play to the end.
3. **Actual:** the tracker stops 3–16 ms before `DurationMs`, and the loop never exits. Because `FormatTime` (`MainWindow.xaml.cs:975-979`) truncates to whole seconds, the readout will most likely show the **same** time on both sides, so this case **looks** finished. It is not:
   - ▶ stays highlighted;
   - the status stays "Playing preview.";
   - pressing ▶ to hear it again does nothing;
   - pressing Space rewinds to 0 **without** playing (it takes the `IsPlaying` branch, `:275`), so the user must press Space a second time;
   - the CPU readout shows the preview still compositing at full playback rate while nothing moves.

   A [human] run on the real WASAPI device confirms what the user sees. The engine-level stall is already [auto]-reproduced by the `capture-args mkv` line above.

### D. [auto] A seek or live refresh while stuck starts the stall over

While stuck in A, B or C, any live edit calls `RefreshPreviewLive` → `RefreshAsync` (`MainWindow.xaml.cs:870-882`) → `SeekCore(PositionMs)` (`PreviewPlaybackEngine.cs:197-205`). The same happens with a ◀◀/▶▶ seek or a scrub. `SeekCore` runs `StopCore` + `PlayCore` (`:371-377`).

The new `OffsetSampleProvider` skips past the end of the audio, so the new tracker starts at 0 and stays there. `IsPlaying` is `true` again, and the stall is the same. Measured for the 3 s/2 s case: after `RefreshAsync`, `IsPlaying=True PositionMs=2020.2`.

---

## Why the suite misses it

1. **The one end-of-playback test uses the stopwatch clock.** `PlayThenStop_ReachesEndAndStopsOnItsOwn` (`PreviewPlaybackEngineTests.cs:638-667`) uses `FakeAudioSink` (`:648`), whose `DrivesRealtime` is the interface default `false` (`:12-17`, `PreviewPlaybackEngine.cs:29`). The stopwatch keeps running after the audio ends, so the loop always reaches `DurationMs`.
2. **The shared fixture has audio longer than its container.** `CreateFixtureClip` (`:153-164`) and the other fixture helpers (`:170-183`, `:220-232`) all mux equal-length `lavfi` video with AAC at 44.1 kHz into **MP4**. That decodes to about 2020 ms against a 2000 ms container (§Root cause 4). So even a `SimulatedRealtimeAudioSink` test that ran to the end would pass.
3. **The one real-clock test stops before the end.** `Play_VideoStaysWithinTwoFramesOfAudioPosition...` (`:263-304`) uses `SimulatedRealtimeAudioSink` but stops at about 3 s of a 5 s clip (`:297`).
4. **No preview test uses a video-only or video-longer-than-audio layer.** The only `-an` fixture in the tests is in `ExportEngineTests.cs:216`, and export uses a frame count, not a clock (`ExportEngine.cs:75`). `LayerKind.UploadedVideo` appears in preview-adjacent tests only as a model value, with no media behind it.

---

## Proposed fix

**Pad the preview's post-seek audio stream with silence up to the project duration**, so the audio clock always reaches `DurationMs`.

This is exactly what export already does. `ExportEngine.Export` allocates a zero-filled buffer of `DurationMs` length (`ExportEngine.cs:43-44`, `:51`) and fills only what the mix provides (`:52-58`). The audio it muxes is therefore silence-padded to the same duration the video is rendered to (`:75`). After the fix, preview matches export's model: one timeline length, with the audio clock still the master during the silent tail.

### Change 1: new `PadToLengthSampleProvider`

**New file:** `src/Acapella.Engine/Preview/PadToLengthSampleProvider.cs`

```csharp
using NAudio.Wave;

namespace Acapella.Engine.Preview;

/// <summary>
/// Bug audit #10: passes a finite stream through, then emits silence until at least lengthMs of
/// audio has been produced. PreviewPlaybackEngine's audio clock (PositionTrackingSampleProvider)
/// only advances on samples actually returned, and the project duration comes from container
/// durations (LayerTimeline, audit A5) -- without padding, a layer whose audio is shorter than its
/// container (video-only uploads, video outlasting audio, every untrimmed MKV take) left the clock
/// short of DurationMs forever. Mirrors ExportEngine, which zero-pads its mix buffer to
/// DurationMs. Never truncates a longer source: FrameLoop's own DurationMs break ends playback.
/// </summary>
public sealed class PadToLengthSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly long _minTotalSamples; // interleaved
    private long _emitted;
    private bool _sourceEnded;
    private volatile bool _ended;

    public PadToLengthSampleProvider(ISampleProvider source, double lengthMs)
    {
        _source = source;
        var format = source.WaveFormat;
        // +1 frame: absorbs floating-point error in (DurationMs - startPositionMs) so the clock
        // lands at or past DurationMs, never a hair short (see audit #10, ordering subtlety 3).
        long frames = lengthMs > 0 ? (long)Math.Ceiling(lengthMs * format.SampleRate / 1000.0) + 1 : 0;
        _minTotalSamples = frames * format.Channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>True once Read has returned 0 (source and padding both exhausted).</summary>
    public bool Ended => _ended;

    public int Read(float[] buffer, int offset, int count)
    {
        if (!_sourceEnded)
        {
            int read = _source.Read(buffer, offset, count);
            if (read > 0)
            {
                _emitted += read;
                return read;
            }
            // Only a 0-length read means end of stream (ISampleProvider contract); a short read
            // is passed through as-is, never treated as the end.
            _sourceEnded = true;
        }

        long remaining = _minTotalSamples - _emitted;
        if (remaining <= 0)
        {
            _ended = true;
            return 0;
        }

        int toPad = (int)Math.Min(count, remaining);
        Array.Clear(buffer, offset, toPad);
        _emitted += toPad;
        return toPad;
    }
}
```

### Change 2: insert it between the skip and the clock, and exit on its end

**File:** `src/Acapella.Engine/Preview/PreviewPlaybackEngine.cs`

In `StartAudio` (`:314-330`), replace `:325-329`:

```csharp
        // Bug audit #10: pad the post-seek stream with silence to the rest of the project's length
        // BEFORE the tracker counts it, so the audio clock reaches DurationMs even when every
        // layer's audio ends before its container does (video-only layer, video outlasting audio,
        // untrimmed MKV take). Must sit inside the tracker -- see ordering subtlety 1.
        var padded = new PadToLengthSampleProvider(seeked, DurationMs - positionMs);

        // Wraps the post-seek stream so samples actually pulled by the sink count from zero at
        // this playback's start position -- FrameLoop adds startPositionMs back on top (audit A2).
        var tracked = new PositionTrackingSampleProvider(padded);
        _audioSink.Play(tracked);
        return (tracked, padded);
```

Change the return type of `StartAudio` to `(PositionTrackingSampleProvider Tracker, PadToLengthSampleProvider Padded)`. In `PlayCore` (`:236`, `:243`), write `var (positionTracker, padded) = StartAudio(startPositionMs);` and pass `padded` into `FrameLoop` as a new parameter, `PadToLengthSampleProvider? padded`.

In `FrameLoop`, replace `:259-262`:

```csharp
            // Read the end flag before the position (subtlety 2): if the stream ended, the tracker
            // has counted every padded sample, so the project end has been reached.
            bool audioEnded = useAudioClock && padded is not null && padded.Ended;
            double elapsedMs = useAudioClock && positionTracker is not null
                ? positionTracker.PositionMs
                : clock.Elapsed.TotalMilliseconds;
            PositionMs = audioEnded ? DurationMs : Math.Min(startPositionMs + elapsedMs, DurationMs);
```

The break at `:276` is unchanged. `PositionMs == DurationMs` now always happens with the real device, at the latest on the pass after the padded stream returns 0.

That is the whole fix: one new file of about 55 lines and about 12 changed lines in `PreviewPlaybackEngine.cs`. No App change: `PlaybackStopped` → `SetPlayStopContent(false)` (`MainWindow.xaml.cs:167`) already does the right thing once the engine fires it.

---

## Ordering subtleties

### 1. The padding must sit **inside** the tracker, between the skip and `PositionTrackingSampleProvider`

The tracker counts only what **its** source returns (`PositionTrackingSampleProvider.cs:33-34`).

*Counter-example.* A plausible alternative is to wrap the tracker instead: `sink.Play(new PadToLengthSampleProvider(tracked, ...))`. The sink then hears the silence, but the tracker's source is still the exhausted mix. The tracker stops at 2020 ms in the 3 s/2 s case, and nothing changes. Tests A–C would still fail, and a "sink receives `DurationMs` of audio" test would wrongly pass.

### 2. The length is measured from the **start position**, after the skip, not from zero

The tracker counts from 0 at the start of **this** playback (`PreviewPlaybackEngine.cs:325-326`), and `FrameLoop` adds `startPositionMs` back (`:262`). So the stream the tracker sees must be at least `DurationMs - startPositionMs` long.

*Counter-example (applying it before the skip).* `new OffsetSampleProvider(new PadToLengthSampleProvider(mix, DurationMs)) { SkipOver = pos }` is correct **only** as long as `SkipOver` never skips more than `pos`. That depends on NAudio's `TimeSpan` → sample rounding and on .NET's `TimeSpan.FromMilliseconds` precision; .NET Framework rounded it to whole milliseconds. Take a seek to 1000.6 ms in a 2000 ms project:
- If the skip rounds up to 1001 ms, the stream after the skip is 999 ms of padding.
- The clock ends at 1000.6 + 999 = 1999.6 ms, 0.4 ms short of `DurationMs`, and the stall is back. It now happens only after some seeks, which makes it much harder to diagnose.

Today NAudio's `OffsetSampleProvider` truncates when it converts `SkipOver` to samples, so the before-skip form would happen to work. Padding **after** the skip, to `DurationMs - positionMs`, makes the clock's end independent of that implementation detail.

Padding too **much** is harmless. The loop breaks at `DurationMs` (`:276`) and `StopInternal` stops the sink (`:343`). This is also why the padding never truncates a source that is longer (the 2020 ms MP4 audio over a 2000 ms container plays exactly as it does today).

### 3. The +1 frame and the `Ended` exit each close a floating-point gap

Without either one, the clock's end is `startPositionMs + ceil((DurationMs - startPositionMs) × 44.1) / 44.1`. When the frame count comes out exactly integral, this is `start + (Duration - start)` in double arithmetic.

*Counter-example.* `DurationMs = 3000.1`, `start = 1234.5678` gives `1765.5322`, which multiplied by 44.1 comes very close to an integer. Double rounding can leave `start + tracked` at `3000.0999999999995 < 3000.1`. The `>=` at `:276` then never fires. That is the same stall, with a 0.0000000005 ms gap, and only for some seek positions.

The +1 frame (22.7 µs of margin, far above double error) closes this on its own. The `Ended` check makes the exit independent of the arithmetic altogether: once the padded stream has returned 0, the end has been reached by definition.

In `FrameLoop`, `Ended` is read **before** the position. If `Ended` flips between the two reads, the pass uses the tracker's (full) position and the next pass sees `Ended`. The other order is also safe. Neither order can exit early, because `Ended` is true only after at least `DurationMs - start` of audio has been counted.

### 4. A seek or refresh between the audio's end and `DurationMs` now plays the silent remainder instead of re-stalling (repro D)

The window that matters is `audioEnd <= positionMs < DurationMs`. The following counter-example shows why a test at exactly the end would prove nothing.

*Counter-example (seeking to exactly `DurationMs`).* Today, a seek to exactly `DurationMs` while playing already stops:
- `SeekCore` clamps to `DurationMs` (`:369`).
- The new tracker stays at 0, but `startPositionMs + 0 == DurationMs`, so `:276` breaks on the first pass.

Seeking to 2500 ms in the 3 s/2 s case is different:
- **Before the fix:** the tracker stays at 0 and `PositionMs` stays at 2500 < 3000 forever. That is the re-stall.
- **After the fix:** the padding is 500 ms long, the clock runs 2500 → 3000, and playback stops.

This is why the test below seeks to 2500, not to `DurationMs`. At `positionMs == DurationMs`, `lengthMs` is 0 and the padding adds nothing (`lengthMs > 0 ? … : 0`), so that path behaves exactly as today.

### 5. The stopwatch path is unaffected

`padded.Ended` is gated on `useAudioClock`. The test sinks with `DrivesRealtime == false` (`FakeAudioSink`, `CapturingAudioSink`, `CountingAudioSink`, `GatedAudioSink`) never have the new exit apply, even if a test pulls the captured stream to its end by hand. The master-volume tests that pull from `CapturingAudioSink.LastMix` now pull through the padding. That changes nothing within the first `DurationMs`, which is all they read.

---

## Rejected alternatives

- **Compute `DurationMs` from decoded audio length.** This is what audit A5 removed. It would drop the tail of a video-only layer or of a video that outlasts its audio from preview **and** export (`LayerTimeline` is shared), and a video-only project would have length 0.
- **Fall back to the stopwatch once the audio is exhausted.** This works, but there would be two clocks with a hand-off: the stopwatch would be started when the tracker stops, and its elapsed time added to the tracker's final value. That is more state, and a new drift source during the silent tail. Padding keeps the single audio master clock that A2 set up, and it matches export.
- **Break when `PositionMs` is within a tolerance of `DurationMs`.** The gap is anything from 3 ms (MKV) to the entire length (video-only), so no fixed tolerance works.
- **Break as soon as the unpadded mix ends.** This stops the video-only-tail cases (A, B) early: the picture would stop at the end of the audio instead of at the end of the video. It fixes the stuck state but still disagrees with export.
- **Pad each layer's audio to its container length in `LayerTimeline.AudioInput`.** This fixes it for preview and export alike, but it changes what `MixEngine` and hosted plugins process. Melodyne/ARA would see a longer source, and pitch-correction cache keys would change. That is a larger blast radius for the same visible result. The preview-only wrapper touches nothing shared.

---

## Deliberately not changed

- **Export.** It already pads (`ExportEngine.cs:51-58`) and renders by frame count (`:75`). It is not affected.
- **`LayerTimeline.DurationMs` / `MediaProbe`.** Container duration is the correct project length (A5).
- **What the `PlaybackStopped` handler does after a natural stop.** It still only un-highlights ▶ (`MainWindow.xaml.cs:167`) and leaves `PositionMs` at `DurationMs`. See runner-up 1 for a separate consequence.
- **`FrameLoop`'s `Sleep(5)` pacing and per-pass `RenderComposite`.** Once the loop exits correctly, the constant compositing while nothing moves goes away with it. Rendering only changed frames would be an optimization, not part of this bug.
- **Meter behaviour at the end of the audio.** `MeterTapSampleProvider` zeroes on the one 0-length read (`MeterTapSampleProvider.cs:38-42`), and the padding never calls the source again after that. The meters read silent during the padded tail, which is correct. The stale-meters-after-Stop item (#9 runner-up) is unrelated and stays.
- **`WasapiPreviewAudioSink`.** No sink change is needed. The padding makes the stream long enough, whatever `WasapiOut` does at end of stream.
- **B13** (the last WASAPI buffer still playing after the loop ends). It is unchanged and still harmless.

---

## Tests

All engine-level tests go in **`tests/Acapella.Engine.Tests/Preview/PreviewPlaybackEngineTests.cs`**, using the existing `SimulatedRealtimeAudioSink` (`:23-65`). Its pull loop's `if (read == 0) break;` (`:47-48`) models a real device's end of stream faithfully. The existing `RunFfmpeg` (`:135-151`) and `DeleteWithRetry` (`:118-133`) helpers are reused.

### New fixture helper

```csharp
    /// <summary>Bug audit #10: video and audio of independent lengths (audioSeconds 0 = no audio
    /// stream at all, i.e. a video-only upload), in the given container. The video's red channel
    /// switches 255 -> 0 and blue 0 -> 255 at switchAtSeconds, so a test can tell a frozen early
    /// frame from a late one.</summary>
    private static (string path, string tempDir) CreateMismatchedLengthFixtureClip(double videoSeconds, double audioSeconds, double switchAtSeconds, string extension = "mp4")
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-preview-end-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, $"layer.{extension}");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string sw = switchAtSeconds.ToString(inv);
        string geq = $"r='if(lt(T\\,{sw})\\,255\\,0)':g=0:b='if(gte(T\\,{sw})\\,255\\,0)'";

        var args = new List<string> { "-y", "-f", "lavfi", "-i", $"color=c=black:s=64x64:r=10:d={videoSeconds.ToString(inv)}" };
        if (audioSeconds > 0)
            args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={audioSeconds.ToString(inv)}" });
        args.AddRange(new[] { "-vf", $"geq={geq}", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p" });
        args.AddRange(audioSeconds > 0 ? new[] { "-c:a", "aac" } : new[] { "-an" });
        args.Add(path);
        RunFfmpeg(args.ToArray());

        return (path, tempDir);
    }

    /// <summary>Plays to the natural end with a real-time sink; returns whether PlaybackStopped
    /// fired within DurationMs + 3s, and the centre pixel of cell 0 in the last frame rendered.</summary>
    private static async Task<(bool Stopped, SKColor LastPixel, PreviewPlaybackEngine Engine)> PlayToEndWithRealtimeSink(LayerKind kind, string path, SimulatedRealtimeAudioSink sink, double? seekToMsAfterStart = null)
    {
        var layers = new LayerCollection();
        layers.Restore(new[] { new LayerModel { LayerId = 0, Kind = kind, SourcePath = path } });

        var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
        await engine.SetLayersAsync(layers.Layers);

        object frameLock = new();
        SKColor lastPixel = default;
        var stopped = new ManualResetEventSlim(false);
        engine.FrameReady += frame => { lock (frameLock) { lastPixel = frame.GetPixel(32, 32); } frame.Dispose(); };
        engine.PlaybackStopped += () => stopped.Set();

        await engine.PlayAsync();
        if (seekToMsAfterStart is double seekMs)
        {
            Thread.Sleep(300);
            await engine.SeekAsync(seekMs);
        }
        bool finished = stopped.Wait(TimeSpan.FromMilliseconds(engine.DurationMs + 3000));
        lock (frameLock) return (finished, lastPixel, engine);
    }
```

### New test methods

```csharp
    /// <summary>Bug audit #10, repro B: video 3s, audio 2s. With the audio clock driving the loop,
    /// playback must run the video to its end and stop on its own -- previously the clock stopped
    /// at the audio's end (~2020ms) and the loop never exited.</summary>
    [Fact]
    public async Task Play_WithRealtimeSink_VideoLongerThanAudio_PlaysToVideoEndAndStopsOnItsOwn()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 3, audioSeconds: 2, switchAtSeconds: 2.5);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, lastPixel, engine) = await PlayToEndWithRealtimeSink(LayerKind.UploadedVideo, path, sink);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.False(engine.IsPlaying);
                Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
                Assert.True(lastPixel.Blue > 150, $"Expected the post-audio video (blue, >= 2.5s) to have been shown; last frame was {lastPixel}.");
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, repro A: a video-only layer (no audio stream, 0 decoded samples)
    /// must advance past frame 0 and stop on its own -- previously PositionMs stayed 0 forever.</summary>
    [Fact]
    public async Task Play_WithRealtimeSink_VideoOnlyLayer_AdvancesPictureAndStopsOnItsOwn()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 2, audioSeconds: 0, switchAtSeconds: 1.0);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, lastPixel, engine) = await PlayToEndWithRealtimeSink(LayerKind.UploadedVideo, path, sink);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
                Assert.True(lastPixel.Blue > 150, $"Expected the picture to reach the late (blue) content; last frame was {lastPixel}.");
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, repro C: an MKV written with FfmpegCaptureSession's codec args
    /// (libx264 ultrafast crf 23 + AAC) decodes a few ms shorter than its container duration --
    /// the common case of every untrimmed recorded take.</summary>
    [Fact]
    public async Task Play_WithRealtimeSink_CaptureFormatMkvTake_StopsOnItsOwn()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 2, audioSeconds: 2, switchAtSeconds: 1.0, extension: "mkv");
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, _, engine) = await PlayToEndWithRealtimeSink(LayerKind.RecordedAV, path, sink);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.False(engine.IsPlaying);
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, ordering subtleties 2/3: after a mid-play seek to a fractional
    /// position, the padding is sized from that start position and playback still stops exactly
    /// at DurationMs.</summary>
    [Fact]
    public async Task SeekMidPlay_ToFractionalPosition_WithRealtimeSink_StillStopsAtDuration()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 3, audioSeconds: 2, switchAtSeconds: 2.5);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, _, engine) = await PlayToEndWithRealtimeSink(LayerKind.UploadedVideo, path, sink, seekToMsAfterStart: 1000.6);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own after seek (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, repro D / subtlety 4: a seek (or refresh) while playing to a
    /// position past the audio's end but before DurationMs must play the silent remainder and
    /// stop, not restart an endless "playing" state. Deliberately NOT a seek to exactly
    /// DurationMs -- that already stops today (see subtlety 4's counter-example).</summary>
    [Fact]
    public async Task SeekPastAudioEndWhilePlaying_WithRealtimeSink_StopsInsteadOfReStalling()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 3, audioSeconds: 2, switchAtSeconds: 2.5);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var layers = new LayerCollection();
            layers.Restore(new[] { new LayerModel { LayerId = 0, Kind = LayerKind.UploadedVideo, SourcePath = path } });
            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            await engine.SetLayersAsync(layers.Layers);
            engine.FrameReady += f => f.Dispose();
            var stopped = new ManualResetEventSlim(false);
            engine.PlaybackStopped += () => stopped.Set();

            await engine.PlayAsync();
            await engine.SeekAsync(2500); // audio ends ~2020ms, DurationMs = 3000ms

            Assert.True(stopped.Wait(TimeSpan.FromSeconds(3)), $"Seeking past the audio's end while playing left the engine 'playing' (stuck at {engine.PositionMs:F1}ms).");
            Assert.False(engine.IsPlaying);
            Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }
```

### New unit-test file for the provider

**`tests/Acapella.Engine.Tests/Preview/PadToLengthSampleProviderTests.cs`**

```csharp
using Acapella.Engine.Mix;
using Acapella.Engine.Preview;
using NAudio.Wave.SampleProviders;

namespace Acapella.Engine.Tests.Preview;

public class PadToLengthSampleProviderTests
{
    private static long PullAll(PadToLengthSampleProvider p, out bool allPaddingSilent, int sourceSamples)
    {
        var buf = new float[1000];
        long total = 0;
        allPaddingSilent = true;
        int n;
        while ((n = p.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
                if (total + i >= sourceSamples && buf[i] != 0f) allPaddingSilent = false;
            total += n;
        }
        return total;
    }

    [Fact]
    public void ShortSource_IsPaddedWithSilenceToAtLeastLength_ThenEnds()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Enumerable.Repeat(0.5f, 441).ToArray(), 44100)); // 10ms
        var padded = new PadToLengthSampleProvider(source, lengthMs: 100);

        long total = PullAll(padded, out bool silent, sourceSamples: 882);

        Assert.True(total >= 4410 * 2, $"Expected >= 100ms of stereo samples, got {total}.");
        Assert.True(total <= (4410 + 2) * 2);
        Assert.True(silent);
        Assert.True(padded.Ended);
    }

    [Fact]
    public void EmptySource_IsPaddedToLength()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Array.Empty<float>(), 44100));
        var padded = new PadToLengthSampleProvider(source, lengthMs: 50);
        Assert.True(PullAll(padded, out _, 0) >= 2205 * 2);
    }

    [Fact]
    public void LongerSource_IsPassedThroughUntruncated()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Enumerable.Repeat(0.5f, 4410).ToArray(), 44100)); // 100ms
        var padded = new PadToLengthSampleProvider(source, lengthMs: 10);
        Assert.Equal(4410 * 2, PullAll(padded, out _, 8820));
    }

    [Fact]
    public void ZeroOrNegativeLength_EndsWhenSourceEnds()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Array.Empty<float>(), 44100));
        var padded = new PadToLengthSampleProvider(source, lengthMs: -5);
        Assert.Equal(0, padded.Read(new float[100], 0, 100));
        Assert.True(padded.Ended);
    }
}
```

`MonoToStereoSampleProvider` is only used to get a stereo source like the real mix. If it rejects the `ArraySampleProvider`'s format at build time, use any stereo float `ISampleProvider` of known length instead.

**Regression carve-out (CLAUDE.md):** `PreviewPlaybackEngine.cs` is covered by all of `PreviewPlaybackEngineTests` and `PerformanceBudgetTests`. Re-run both classes once after the change, especially `PlayThenStop_ReachesEndAndStopsOnItsOwn` (stopwatch path, which must be unchanged), the A1/A2 drift test (`:263-304`, now pulling through the padding), and the master-volume tests that pull from `CapturingAudioSink.LastMix`.

---

## Acceptance criteria

### [auto]

1. The five new `PreviewPlaybackEngineTests` methods pass:
   - `Play_WithRealtimeSink_VideoLongerThanAudio_PlaysToVideoEndAndStopsOnItsOwn`
   - `Play_WithRealtimeSink_VideoOnlyLayer_AdvancesPictureAndStopsOnItsOwn`
   - `Play_WithRealtimeSink_CaptureFormatMkvTake_StopsOnItsOwn`
   - `SeekMidPlay_ToFractionalPosition_WithRealtimeSink_StillStopsAtDuration`
   - `SeekPastAudioEndWhilePlaying_WithRealtimeSink_StopsInsteadOfReStalling`
2. The four `PadToLengthSampleProviderTests` pass.
3. On the **unfixed** code, tests 1a, 1b, 1d and 1e fail: playback does not stop on its own. This matches the empirical run in §Repros. For 1e, the re-started tracker sits at 0 with `PositionMs` at 2500 of 3000.
   Test 1c fails on unfixed code only if its fixture's container is longer than its decoded audio. All my capture-argument MKVs were (by 3–16 ms). When adding the test, confirm this once with `ffprobe -show_entries format=duration` against the decoded length. If a particular ffmpeg build ever produces equal lengths, 1c becomes a plain regression guard, and 1a/1b still cover the bug.
4. All existing `PreviewPlaybackEngineTests` and `PerformanceBudgetTests` still pass (regression carve-out).

### [review]

1. `PadToLengthSampleProvider` wraps the **post-skip** stream and sits **inside** `PositionTrackingSampleProvider` (subtleties 1 and 2), with length `DurationMs - positionMs`.
2. Only a 0-length source read switches to padding, never a short read.
3. The `Ended` exit is gated on `useAudioClock`, so the stopwatch path is unchanged.
4. No change to `LayerTimeline`, `MediaProbe`, `MixEngine`, `ExportEngine`, or any App file.

### [human]

1. With the real WASAPI device, record a take (trim-out empty) and press ▶. When it reaches the end, ▶ un-highlights by itself and the status no longer claims it is playing. Restart plays it again. Replaying with ▶ straight from the end is runner-up 2, not this criterion.
2. Upload a video-only clip and press ▶. The picture moves and plays to its end in silence, then stops.
3. Upload a clip whose video outlasts its audio. The picture keeps moving through the silent tail and stops at the total time, matching an export of the same project.

---

## Runners-up (noticed, not specced)

1. **After a natural stop, any live refresh blacks out the preview.** A natural stop leaves `PositionMs == DurationMs`, and the `PlaybackStopped` handler (`MainWindow.xaml.cs:167`) does not rewind. The next `RefreshPreviewLive` (any FX, volume or trim edit), or toggling Show Layer Labels (`:952-958`, `SeekByAsync(0)`), calls `SeekCore(DurationMs)` → `CreateFrameSources(DurationMs)` (`PreviewPlaybackEngine.cs:379-384`). Each `VideoFrameStreamSource` runs `ffmpeg -ss` at or past its layer's end. That yields 0 frames (probe: a 2 s clip with `-ss 1.99`, `2.5` or `5` gives 0 frames). The source falls back to its initial solid-black `_lastFrame` (`LayerFrameSource.cs:41`, `:96-119`).
   - Measured with the built engine: after a natural stop, `RefreshAsync` changed cell 0's centre pixel from `#ff007f00` (the clip's green) to `#ff000000`.
   - The same happens at any position past a **shorter** layer's end. Play, seek or refresh there shows that layer's cell black, while continuous playback and export show its frozen last frame.
   - This affects the stopwatch path too, it is independent of this fix, and today it is partly hidden by this bug (nothing ever reaches the end naturally with the real device).
2. **Pressing ▶ after a natural stop does not replay.** `PlayCore` (`PreviewPlaybackEngine.cs:216-245`) starts at `PositionMs == DurationMs`.
   - It primes the frame sources at the end, which gives black cells (runner-up 1).
   - `FrameLoop`'s first pass breaks immediately and `PlaybackStopped` fires.
   - `PlayButton_Click`'s continuation (`MainWindow.xaml.cs:905-907`) then races that event's `Dispatcher.Invoke` (`:167`). If the continuation runs second, ▶ is left highlighted and the status reads "Playing preview." while nothing plays.

   This already happens today for projects whose audio outlasts the container, the stopwatch path, for example. This fix makes natural stops happen for every project, so it will be hit more often. The usual convention is a one-line "if at end, rewind to 0 before playing" in `PlayButton_Click`.
3. **Identical no-op undo steps.** `CommitSlider` and `TrimTextBox_LostFocus` push an undo snapshot even when the value did not change. That clears redo and marks the project dirty on a click that changed nothing.
