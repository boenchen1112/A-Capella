# Bug Audit #11: The metronome output is decided once, at Record time — turning it on mid-take is silent, and a same-dialog retry starts mid-beat

> Audit as of `c2f159b` (Bug Audit #10 landed: `PadToLengthSampleProvider` wired into preview playback). One new bug is specced in full, with two symptoms from one root cause. Neither has been flagged in any earlier `reviews/*.md` doc — confirmed by grepping `reviews/` for `phase|beat|anchor|metronome` and reading every hit; the only prior metronome findings are M4 (`Bug_Audit_2026-07-12.md:111`, "metronome plays outside of an active capture", already fixed) and L5 (`Bug_Audit_2026-07-12.md:139`, "BPM change mid-beat jumps phase", already fixed and regression-tested). Neither is this bug.
>
> **How it differs from L5 (already fixed, `MetronomeEngine.cs:30-43`):** L5 is about the phase *within one take* surviving a BPM edit, and its fix deliberately *preserves* phase across a change. This bug's secondary symptom is about phase *carrying over between two separate takes* recorded through the same open dialog, and its fix deliberately *discards* phase — the opposite of L5's, which is why it needs its own method rather than reusing the `Bpm` setter's rescale.
>
> **Confidence: high on the mechanism, primary symptom needs no forced failure.** `RecordButton_Click` decides once, at the moment it runs, whether a `WasapiOut` for the metronome exists at all for the rest of that take (`RecordSetupWindow.xaml.cs:210`), and neither `MetronomeToggle` nor `BpmTextBox` is disabled while recording, unlike `CameraCombo`/`MicCombo` (`:221-222`). Ticking the box on mid-take is therefore silent — deterministic, audible (silence where a click is expected), and reachable with an ordinary take: no M6 failure needed. The secondary symptom (phase carries over on an M6 retry) needs a deliberately failed take to observe, so it is folded into the same fix rather than given its own doc.

---

## The bug in one sentence

`RecordButton_Click` only creates the metronome's `WasapiOut` if `_metronome.Enabled` is already true at the instant it runs (`RecordSetupWindow.xaml.cs:210-217`); the checkbox that controls `Enabled` is never disabled during a take (unlike the device pickers), so checking it mid-take — after starting a take with it unchecked — has no audible effect at all for the rest of that take. The same never-recreated `MetronomeEngine` instance (`:34`) also never has its beat phase reset, so on the one path that lets a second take start without closing the dialog (an M6-failure retry, `:323-328`), the next take's click resumes wherever the aborted one left off instead of starting on beat 1.

---

## Root cause

Three facts combine to produce the defect.

### 1. Whether a metronome output exists at all is decided once, at Record time

**File:** `src/Acapella.App/RecordSetupWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:210-217` | `if (_metronome.Enabled) { ...create and Play() a WasapiOut... }` runs exactly once per take, inside `RecordButton_Click`'s success path. If `_metronome.Enabled` is `false` at that instant, `_metronomeOutput` stays `null` for the rest of the take — nothing later in the method, or anywhere else, revisits this decision. |
| `:119-120` | `MetronomeToggle_Changed` just writes `_metronome.Enabled = MetronomeToggle.IsChecked == true;` — a live, unconditional write, with no awareness of whether a `WasapiOut` exists to play it through. |
| `:221-222` | `CameraCombo.IsEnabled = false; MicCombo.IsEnabled = false;` disable the device pickers for the duration of the take. There is no equivalent line for `MetronomeToggle` or `BpmTextBox` — both stay live and clickable/editable while `_isRecording` is `true`. |

So a user can check the box after pressing Record, `_metronome.Enabled` flips to `true`, the checkbox visibly shows checked — and nothing plays, because the `WasapiOut` that would stream `_metronome`'s output to the speakers/headphones was never created for this take.

**The reverse direction already works.** If the metronome *was* checked when Record was pressed, `_metronomeOutput` exists and is actively streaming from `_metronome`. Unchecking it mid-take does mute the click, because `Read()` already gates its audible output on the live `_enabled` flag per sample (`MetronomeEngine.cs:61-70`) — the `WasapiOut` keeps pulling silence rather than stopping. Only the off-to-on transition, when the take started with the box unchecked, is broken.

### 2. `RecordSetupWindow` owns exactly one `MetronomeEngine` for the dialog's whole lifetime, and nothing ever resets its phase

**File:** `src/Acapella.Engine/Metronome/MetronomeEngine.cs`

| Line | What it does |
|---|---|
| `:16` | `_sampleIndex` is a private `long`, zeroed only by the implicit default at construction. |
| `:73` | `Read()` increments it once per sample, unconditionally — whether or not `_enabled` is true (`:61` gates only the *audible* output, not the counter). |
| `:25-44` | The `Bpm` setter's L5 rescale is the only other place `_sampleIndex` is touched, and it only runs `if (newBpm != _bpm)` (`:35`), deliberately *preserving* the phase fraction across a BPM change — the opposite of what a reset needs. |

There is no `Reset()`, and nothing else ever sets `_sampleIndex` back to `0` once a `Read()` has advanced it.

**File:** `src/Acapella.App/RecordSetupWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:34` | `private readonly MetronomeEngine _metronome = new();` — one instance, created once, when the dialog is constructed. |
| `:302-313` | `StopRecording` disposes `_activeCapture`, `_guideTrackPlayer` and `_metronomeOutput`, and nulls all three. It does **not** touch `_metronome` itself. |
| `:323-328` | M6 (`MediaProbe.HasNonzeroDuration` fails): status text is set to the failure diagnostics and the method `return`s **without** setting `DialogResult` or calling `Close()`. The dialog stays open, `_isRecording` is already `false` (`:315`), `RecordButton.Content` is already back to `"Record"` (`:316`). |
| `:330-343` | On success, by contrast, the method always ends in `DialogResult = true; Close();` — a successful take never leaves the dialog open for a second press. |

So the only way to press Record twice against the same `_metronome` instance is the M6-retry path — not a hypothetical: the code comment right above it says so explicitly, *"let the user retry from this same dialog instead of leaving a zombie layer or silently closing"* (`:320-322`). It is the documented, intended way to recover from a failed take. While `_isRecording` is `true`, `_metronome.Read()` keeps advancing `_sampleIndex` for the whole attempted (and eventually failed) duration if the metronome was on — e.g. an aborted attempt of roughly 2.7 seconds advances the phase by `2.7 * 44100 = 119070` samples, and `119070 mod 22050 = 8820` at 120 BPM (`22050` samples/beat) — nowhere near a beat boundary, so the retry's first click lands well into a beat instead of on it.

### 3. Fact 1 and fact 2 share one fix, because both are about what `RecordButton_Click`'s metronome block (`:210-217`) decides once and never revisits

Fact 1 needs the block to stop being conditional on a snapshot of `Enabled`. Fact 2 needs the block to start every take from a clean phase. Both point at the same six lines.

---

## Repros

### A. Checking the box mid-take is silent **[human]**, needs a camera and a microphone, no forced failure
1. Open Recording setup (or press ⏺). Leave **Metronome unchecked**. Select a camera and microphone.
2. Press **Record**.
3. While recording, check **Metronome**, at any BPM.
4. **Bug:** no click is heard for the rest of this take, even though the checkbox shows checked.
5. Press **Stop Recording**, then press **Record** again in a *fresh* take (or reopen the dialog). With the box already checked from step 3, the click plays immediately — confirming `_metronome.Enabled` really was `true` during step 3-4, it just had no output stream to reach the speakers.

### B. M6-retry resumes the click mid-beat **[human]**, needs a camera and a microphone, forced M6 failure
1. Check **Metronome**, any BPM. Select a camera and microphone, then press **Record**.
2. Press **Stop Recording** almost immediately — within a few hundred ms, well inside dshow's 0.5-2s device-init window (`FfmpegCaptureSession.cs:28-33`) — so the take is short enough to fail M6 ("Recording failed: ..." appears in `StatusText`).
   - If a quick double-press doesn't reliably fail M6 on a given machine (fast dshow init), disconnect or disable the selected camera/mic for a second before pressing Record instead, then reconnect before the retry.
3. Without closing the dialog, press **Record** again and record a normal take (camera/mic left alone this time).
4. **Bug:** listen for the gap between pressing Record and hearing the first click. Unfixed, `_sampleIndex` resumed from wherever step 2's aborted attempt left it, so the first click is late by up to a full beat (about 0.3s in a 120 BPM, ~2.7s-aborted example) instead of arriving immediately, and can also land partway through its envelope rather than as a clean, full click. (It can't arrive *early* — nothing plays before `_metronomeOutput.Play()` starts pulling.)
5. **Contrast:** close the dialog, reopen it (Tools > Recording setup...), record once with the metronome on. The first click after Record arrives immediately, with no silent gap, because `_metronome` (`:34`) is then a brand-new instance with `_sampleIndex == 0`.

### C. Engine-level evidence of the reset mechanism **[auto]**
`Reset_AfterPriorReads_ZeroesThePhase` (below) advances the engine partway into a beat (as an aborted take would), then — after the fix — calls `Reset()` and confirms the click envelope starts again from the top, unlike a same-value `Bpm` re-assignment (the rejected alternative, also tested).

---

## Why the suite misses it

- **`RecordSetupWindow` has zero test coverage.** Bug Audit #7 already established why: every entry point runs through the modal `RecordSetupWindow.ShowDialog()`, which a headless test can't drive, and `RecordButton_Click` additionally needs real dshow devices in `_dshowVideoDevices`/`_dshowAudioDevices` (`RefreshDevices()`, `:79-90`) or it bails at the device-selection guard (`:136-140`) before reaching any of this code. `grep -rln "RecordSetupWindow" tests/` finds no test source file, only compiled DLL bytes.
- **`MetronomeEngineTests` has no concept of "a take" or "mid-take."** The one existing test, `Bpm_ChangedMidBeat_PreservesPhaseFraction` (`tests/Acapella.Engine.Tests/Metronome/MetronomeEngineTests.cs:16`), only exercises a BPM change within one continuous session — never "does checking `Enabled` after playback has started produce sound," which is entirely about `RecordSetupWindow`'s wiring, not the engine.
- **No prior audit toggled the checkbox after pressing Record**, or pressed Record twice in the same dialog. Bug Audit #6/#7's M6-related repros stop at "the failure is surfaced and the layer isn't created" — none continues to a retry.

---

## Proposed fix

**Approach: stop deciding once, at Record time, whether a metronome output exists. Always create and play `_metronomeOutput` when a take starts, reset the engine's phase first, and let `Read()`'s existing live `_enabled` gate (`MetronomeEngine.cs:61`) do all the muting — both for a take that starts with the box unchecked and later checked, and for a take that starts checked and later unchecked (already working today).**

### Change 1: `src/Acapella.Engine/Metronome/MetronomeEngine.cs` — add `Reset()`

```csharp
    /// <summary>Zeros the beat phase back to the start of a fresh beat (bug audit #11) -- call at
    /// the start of every new take, not just the first. Unlike the Bpm setter's phase-preserving
    /// rescale (L5, Bug_Audit_2026-07-12.md), this deliberately discards the phase: a new take is
    /// not a continuation of a previous, unrelated take's beat grid, so there is nothing to
    /// preserve. Safe to call whether or not Enabled is currently true, and whether or not this is
    /// actually the first take in this dialog (a no-op then, since _sampleIndex is already 0).</summary>
    public void Reset() => _sampleIndex = 0;
```

Placed next to the `Enabled` property (`:46-50`), after it.

### Change 2: `src/Acapella.App/RecordSetupWindow.xaml.cs` — always create the output, reset first

Replace `:210-217`:

```csharp
        // Bug audit #11: create the metronome's output unconditionally, not only when Enabled is
        // already true -- MetronomeToggle stays live during a take (unlike CameraCombo/MicCombo,
        // :221-222), so gating creation on a one-time snapshot of Enabled made checking the box
        // mid-take silent for the rest of that take. Read()'s own _enabled check (MetronomeEngine.cs
        // :61) already gates the audible output live, in both directions, so this is now symmetric:
        // checking OR unchecking the box mid-take works the same way checking it before Record
        // always did.
        //
        // Reset() first, and before Play(): _metronome (:34) is one instance for the whole dialog,
        // so without this an M6 retry (:323-328) resumes the click wherever the discarded attempt's
        // Read() calls left the phase, landing mid-beat instead of on beat 1. Reset() must run
        // before Play() starts pulling on its own render thread -- see the doc's ordering
        // subtleties for why resetting after would be worse than not resetting at all.
        _metronome.Reset();
        try
        {
            using var metronomeEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var metronomeDevice = metronomeEnumerator.GetDevice(_outputDevice.Id);
            _metronomeOutput = new WasapiOut(metronomeDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
            _metronomeOutput.Init(_metronome);
            _metronomeOutput.Play();
        }
        catch
        {
            // The take itself (capture, and the guide track if any) already started above and must
            // not be aborted over a monitoring-only nicety. Losing the click for this take is far
            // cheaper than losing the take -- fall back to no metronome output, same as if the
            // user had left the box unchecked the whole time. Dispose first: if `new WasapiOut(...)`
            // succeeded but Init or Play threw, a bare null-out would leak that WasapiOut.
            _metronomeOutput?.Dispose();
            _metronomeOutput = null;
        }
```

### Rejected alternatives

- **Keep the `if (_metronome.Enabled)` gate, and just have `MetronomeToggle_Changed` create/tear down `_metronomeOutput` live.** This works too, but it duplicates the device-open/`Init`/`Play`/`Stop`/`Dispose` sequence in a second call site (the checkbox handler), which then has to know whether a take is even active (`_isRecording`) before touching `_metronomeOutput`, and has to coordinate with `RecordButton_Click`/`StopRecording` over who owns creating and disposing it. Always-create-at-take-start keeps exactly one place that opens the device and exactly one (`StopRecording`, unchanged) that closes it; `Enabled` stays a pure, already-live, already-tested mute flag (`MetronomeEngine.cs:61`).
- **Re-assign `_metronome.Bpm = _metronome.Bpm;` instead of adding `Reset()`.** Looks like it reuses existing machinery, but it is a complete no-op: the setter's rescale is guarded by `if (newBpm != _bpm)` (`MetronomeEngine.cs:35`), and assigning a property to its own current value never satisfies that. `_sampleIndex` is untouched. See the ordering subtleties and the `ReassigningBpmToItself_DoesNotResetThePhase` test.
- **Construct a brand-new `MetronomeEngine` on every `RecordButton_Click` instead of resetting the existing one.** Functionally equivalent (a new instance also starts at `_sampleIndex == 0`), but it means re-copying `Bpm` and `Enabled` from the old instance in the right order every time, for no benefit over one zeroing method, and it multiplies the surface for a copy-order bug. `Reset()` is one line and touches nothing else.
- **Let a device-open failure propagate.** Before this fix, a take with the metronome untouched never opened this device at all, so a bad/removed output device could never fail *this* code path. Always-creating changes that: every take now opens a metronome output, whether or not anyone uses it. Swallowing the failure and falling back to no click (rather than aborting the whole `RecordButton_Click`, which has already started `_activeCapture` and possibly `_guideTrackPlayer` by this point) keeps the failure mode no worse than it was for a take that simply never checked the box.

---

## Ordering subtleties

**1. `Reset()` must actually zero `_sampleIndex`, not merely round-trip `Bpm`.**
- **Counter-example:** take 1 (120 BPM) is aborted mid-beat, so `_sampleIndex` sits at some non-multiple of `samplesPerBeat`. A "fix" that does `_metronome.Bpm = _metronome.Bpm;` before take 2 changes nothing: `newBpm == _bpm` (both `120`), so the `if` at `MetronomeEngine.cs:35` is `false`, and `_sampleIndex` is exactly what it was. Take 2's first `Read()` still resumes mid-beat.
- Test: `ReassigningBpmToItself_DoesNotResetThePhase` proves the tempting-looking shortcut does nothing, motivating `Reset()` as its own method.

**2. `Reset()` must run *before* `_metronomeOutput` is created and `Play()`d, not after.**
- `WasapiOut.Play()` starts pulling from `_metronome` on its own render-callback thread, independent of the UI thread that called it. If `Reset()` ran *after* `Play()`, the render thread could already have issued one or more `Read()` calls against the stale, carried-over `_sampleIndex` before the UI thread's `Reset()` executes.
- **Counter-example:** `Play()` first, `Reset()` second. The render thread's first callback reads a block starting mid-beat (the bug, still present for that block), then `Reset()` zeroes the index mid-stream — the *following* block jumps backward in phase instead of continuing forward, an audible glitch (a click firing twice in quick succession, or a beat appearing to skip) that is arguably worse than the unfixed bug, because now the discontinuity is mid-take instead of only at the start.
- Change 2 places `_metronome.Reset()` before the `try` block that builds and plays `_metronomeOutput`, so no `Read()` can happen before the reset.

**3. `Reset()` must run unconditionally on every take, not only when "this looks like a retry."**
- There is no existing flag that distinguishes "first take in this dialog" from "a retry after M6 failure" — `_isRecording` is `false` in both cases by the time `RecordButton_Click` runs again (`StopRecording` always clears it, `:315`, whether or not M6 passed).
- **Counter-example:** gating the reset behind a hypothetical `_hasRetried` flag set only inside the M6-failure branch would miss the same shape if `MainWindow` were ever changed to reuse a cached dialog instance across separate Record sessions instead of always constructing a fresh one (`MainWindow.xaml.cs:515`, currently always `new RecordSetupWindow(...)`). Making the reset unconditional costs nothing — resetting an already-zero counter is a no-op — and stays correct even if that assumption ever changes.
- Test: `Reset_OnFreshEngine_IsANoOp` confirms calling `Reset()` before any `Read()` changes nothing observable.

**4. Always-creating the output must not let a device failure abort an otherwise-fine take.**
- Before this fix, a take recorded with the metronome untouched never called `GetDevice`/`WasapiOut(...)`/`Init` at all for the metronome. After always-creating, every take does, whether or not the metronome is ever checked.
- **Counter-example (scoped to a layer-1, no-guide take):** the selected output device (`_outputDevice`, chosen once at dialog construction, `:72`) is unplugged between opening the dialog and pressing Record. For layer 1 (`guideLayers.Count == 0`, the `:204-208` branch), nothing else touches `_outputDevice` before this ticket's block runs, so an unguarded `_metronomeOutput = new WasapiOut(...)` would be the first thing to throw inside `RecordButton_Click`, *after* `_activeCapture.Start(...)` has already begun — an unhandled exception here would leave capture running with `_isRecording` never set to `true` and the UI never updated to "Stop Recording", stranding the take. The `try`/`catch` in Change 2 falls back to a disposed, nulled `_metronomeOutput` instead, exactly the pre-existing state for a take where the metronome was simply never enabled.
- **Not fully general, and not claimed to be:** for layers 2+, `_guideTrackPlayer.Play(_outputDevice.Id, guideMix)` (`:200`) hits the same unplugged device, unguarded, *before* this ticket's block even runs — so a guide-track take is already strandable by this exact failure today, independent of this fix. That pre-existing hazard is out of scope here; this subtlety only guarantees the fix doesn't make the layer-1 case worse than the guide-track case already is.

---

## Deliberately not changed

- **The no-guide branch (layer 1) starting the metronome without `WaitForCaptureStarted` first**, unlike the guide branch (`:198` vs. the fall-through at `:206-208`). The click is monitoring-only and never recorded into the file (`MetronomeEngine.cs:6-7`), so this only affects how many seconds into the take the performer's first click lands relative to a device-init window they can't hear anyway. Separate, narrower change.
- **The click grid has no relation to the guide track's beats, for layers 2+.** The guide (`_guideTrackPlayer`) and the metronome are two independently-started `WasapiOut` streams (`:199-200` vs. this ticket's block), with no shared start reference. Nothing in the product spec or prior audits claims the click is supposed to land on the guide's actual downbeat — a metronome user recording layer 2+ hears two grids that may not agree, but that is an inherent property of two separately-started streams, not something this ticket touches.
- **M4's original guarantee** (metronome never plays outside an active capture, `Bug_Audit_2026-07-12.md:111`) **still holds.** The output is still created inside `RecordButton_Click`'s take-start block and disposed inside `StopRecording` (`:311-313`), unchanged — only *whether it's created* stopped depending on `Enabled`'s value at that instant.
- **`BpmTextBox` remaining editable during a take.** BPM changes mid-take already work correctly (L5's phase-preserving rescale, unaffected by this fix).

---

## Tests

### `tests/Acapella.Engine.Tests/Metronome/MetronomeEngineTests.cs` (append to the existing class)

These can't be red-then-green in the usual sense: `Reset()` is new API, so the tests that call it don't compile before the fix. `Enabled_FlippedTrueMidStream_StartsClickingFromThere` is the exception -- it exercises only existing `MetronomeEngine` surface and already passes today, but nothing currently asserts it, and it is the load-bearing assumption the primary (App-side) fix relies on: that flipping `Enabled` mid-stream, on an already-`Read()`-ing engine, produces clicks from that point on with no extra wiring.

```csharp
    /// <summary>Bug audit #11: the primary fix (RecordSetupWindow always creates _metronomeOutput
    /// and lets Read()'s live _enabled check gate the sound) depends on this already existing:
    /// flipping Enabled from false to true on an engine that's already mid-stream (as checking the
    /// box mid-take does) must start producing clicks from that point on, with no extra call. This
    /// passes today -- it characterizes the assumption the fix relies on, not a new behavior.</summary>
    [Fact]
    public void Enabled_FlippedTrueMidStream_StartsClickingFromThere()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = false, Bpm = 120 };

        // 1.5 beats of silence while Enabled is false (mirrors a take started with the box unchecked).
        var whileDisabled = new float[33075];
        metronome.Read(whileDisabled, 0, whileDisabled.Length);
        Assert.All(whileDisabled, s => Assert.Equal(0f, s));

        metronome.Enabled = true;   // the checkbox, checked mid-take

        // Read past the next beat boundary (22050 samples/beat) without any other call in between.
        var afterEnabled = new float[23000];
        metronome.Read(afterEnabled, 0, afterEnabled.Length);
        Assert.Contains(afterEnabled, s => s != 0f);
    }

    /// <summary>Bug audit #11: Reset() is what RecordButton_Click now calls before every take.
    /// After it, the click envelope starts again from the top of a beat -- proven by finding a
    /// nonzero sample near the start of the very next block, not merely "some nonzero sample
    /// eventually" (a full beat has one click near its start and silence for the rest).</summary>
    [Fact]
    public void Reset_AfterPriorReads_ZeroesThePhase()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = true, Bpm = 120 };

        // Simulate an aborted take: read partway into a beat, deliberately not landing on a
        // multiple of the beat length (22050 samples/beat at 120 BPM).
        var discard = new float[33075]; // 1.5 beats
        metronome.Read(discard, 0, discard.Length);

        metronome.Reset();

        var nextTake = new float[10];
        metronome.Read(nextTake, 0, nextTake.Length);

        // posInBeat == 0 on the very first sample gives sin(0) == 0f, so the envelope's rise shows
        // up over the first few samples, not necessarily at index 0 -- check the block, not one index.
        Assert.Contains(nextTake, s => s != 0f);
    }

    /// <summary>Bug audit #11, ordering subtlety 1: proves the rejected alternative (re-assigning
    /// Bpm to its own current value) does NOT reset the phase, motivating Reset() as its own method
    /// rather than reusing the Bpm setter's rescale path.</summary>
    [Fact]
    public void ReassigningBpmToItself_DoesNotResetThePhase()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = true, Bpm = 120 };

        var discard = new float[33075];
        metronome.Read(discard, 0, discard.Length);

        metronome.Bpm = metronome.Bpm;   // the tempting-looking but wrong "reset"

        var nextTake = new float[10];
        metronome.Read(nextTake, 0, nextTake.Length);

        Assert.All(nextTake, s => Assert.Equal(0f, s));   // still mid-beat: not fixed by this
    }

    /// <summary>Bug audit #11, ordering subtlety 3: Reset() on a never-read engine is a harmless
    /// no-op, so calling it unconditionally on every RecordButton_Click (including the first take
    /// in a dialog) is safe.</summary>
    [Fact]
    public void Reset_OnFreshEngine_IsANoOp()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = true, Bpm = 120 };
        metronome.Reset();

        var firstBlock = new float[10];
        metronome.Read(firstBlock, 0, firstBlock.Length);

        Assert.Contains(firstBlock, s => s != 0f);   // still clicks immediately, same as without the call
    }
```

**Regression carve-out (per CLAUDE.md):** `MetronomeEngine.cs` is touched (additive `Reset()` only; `Read`/`Bpm`/`Enabled` unchanged). Re-run the existing two `MetronomeEngineTests` (`Bpm_ChangedMidBeat_PreservesPhaseFraction`, `Bpm_ClampsToValidRange`) once. `RecordSetupWindow.xaml.cs` is touched but has no automated coverage to re-run (Bug Audit #7's established limitation).

---

## Acceptance criteria

### [auto]
- The four new tests in `MetronomeEngineTests.cs` pass.
- The two existing `MetronomeEngineTests` still pass (regression carve-out).
- The solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- `_metronome.Reset()` sits before the `try` block that constructs `_metronomeOutput`, and both run unconditionally (not gated on `_metronome.Enabled` or any retry-detection flag).
- The `catch` around metronome device setup sets `_metronomeOutput = null` and does not rethrow or abort the take.
- `MetronomeEngine.Reset()` only sets `_sampleIndex = 0` — `Bpm` and `Enabled` are untouched.
- M4 still holds: `_metronomeOutput`, when non-null, is still created inside the take-start block and disposed inside `StopRecording` (`:311-313`) — it cannot outlive an active capture.

### [human] (camera and microphone required)
- Repro A: checking the box mid-take now produces an audible click within about one beat.
- Repro A, reverse direction (already worked, must not regress): unchecking the box mid-take still mutes the click immediately.
- Repro B: after a deliberate M6-failure retry, the first click is heard immediately after the retry's Record press, with no silent gap first — audibly no different from a fresh-dialog baseline.
- No regression: a normal single-take recording (metronome checked before Record, no retry) still clicks immediately after Record, same as before this fix.

---

## Runners-up (not specced)

- **The no-guide branch (layer 1) starts the metronome without waiting for `WaitForCaptureStarted` first.** Low impact (see Deliberately not changed); worth folding into a future ticket if `RecordSetupWindow`'s take-start sequencing is revisited.
- **The click grid and the guide track's beats have no shared reference for layers 2+**, so a metronome user recording over a guide hears two grids that may not agree. This is closer to a product/UX question (should the click even try to line up with the guide?) than a defect, since nothing in the plan or prior audits claims they should align — noted here only so it isn't rediscovered as "new."
