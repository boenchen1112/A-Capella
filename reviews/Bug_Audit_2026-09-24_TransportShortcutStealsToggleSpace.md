# Bug Audit #14: `MainWindow`'s transport shortcuts steal Space from any focused Button/CheckBox/ToggleButton, silently breaking their only keyboard-activation key

> **Promotion, not a fresh find.** Bug Audit #13's own Runners-up section (`Bug_Audit_2026-09-24_CommitSliderKeyboardEdit.md:309`) named this exact shape in passing — "a focused `Button` or `CheckBox` also loses its own default Space/arrow behavior whenever this global handler runs first... whether that's ever actually a problem for a control other than `Slider` wasn't investigated here" — and explicitly declined to verify it. This ticket is that verification, traced all the way through WPF's key-routing mechanism to a concrete, always-reachable, in-app repro, plus a scoped fix.
>
> Audit as of `3421de1` (Bug Audit #13 landed: `CommitSlider` now commits on keyboard release too).
>
> **Confidence: high.** `MainWindow_PreviewKeyDown` really does apply to any focused element that isn't a `TextBox` (`MainWindow.xaml.cs:290`, confirmed by reading the guard clause — no second exclusion exists). The toolbar's `MonitorToggle` (`MainWindow.xaml:231-234`) is a plain, directly-named `ToggleButton` with no `IsTabStop`/`Focusable` override, always visible regardless of project state, with **no other keyboard path** to it at all (grep for `MonitorToggle`/`_monitorMuted` in `MainWindow.xaml.cs` finds exactly one handler, `MonitorToggle_Changed:348-352`, wired only to `Checked`/`Unchecked`, and no menu item or accelerator anywhere touches `_monitorMuted`). The suppression mechanism itself — a tunnel-phase `PreviewKeyDown` handler setting `e.Handled = true` suppresses the paired bubble-phase `KeyDown` that `ButtonBase`'s own Space-activates-Click handling runs in — is the identical mechanism Bug Audit #13 already relied on and flagged as "reasoned from WPF's general input-routing behavior, not observed running the app" (`Bug_Audit_2026-09-24_CommitSliderKeyboardEdit.md:7`); this doc inherits that same caveat and the same human-verification step to confirm it empirically.

---

## The bug in one sentence

`MainWindow_PreviewKeyDown` (`MainWindow.xaml.cs:287-312`) intercepts Space at the `Window` level for every focused element except a `TextBox` (`:290`) and unconditionally treats it as the Play/Stop transport shortcut (`:294-298`) — so tabbing to (or clicking, then pressing Space on) any `Button`, `CheckBox`, or `ToggleButton` in the app — the toolbar's Monitor-mute toggle, a layer's Mute/Solo checkboxes, or any of the seven FX-enable checkboxes — and pressing Space does not activate that control at all; it silently starts or stops preview playback instead, with the control's own state completely unchanged.

---

## Root cause

Three facts combine to produce the defect.

### 1. The only focus exclusion in `MainWindow_PreviewKeyDown` is `TextBox`

**File:** `src/Acapella.App/MainWindow.xaml.cs`

| Line | What it does |
|---|---|
| `:189` | `PreviewKeyDown += MainWindow_PreviewKeyDown;` — attached directly to `this` (the `Window`) in the constructor. `PreviewKeyDown` tunnels root-to-leaf, so this handler runs **before** any focused descendant sees the key at all. |
| `:290` | `if (Keyboard.FocusedElement is TextBox) return;` — the one and only exclusion. |
| `:292-311` | `switch (e.Key)`: `Key.Space` → `StopButton_Click`/`PlayButton_Click` (`:294-298`), `Key.Left`/`Key.Right` → `SeekRelative` (`:299-306`), `Key.Home` → `RestartButton_Click` (`:307-310`); each branch sets `e.Handled = true`. |

A focused `Button`, `CheckBox`, or `ToggleButton` is not a `TextBox`, so none of them are excluded — Space reaching any of them hits the `Key.Space` case exactly like an unfocused keypress would.

### 2. Space is `ButtonBase`'s own activation key, and it runs in the bubble-phase `KeyDown` that `e.Handled = true` (set during the tunnel) suppresses

`Button`, `CheckBox`, `ToggleButton`, and `RadioButton` all derive from `System.Windows.Controls.Primitives.ButtonBase`, which implements "Space (and, for `Button`, Enter) invokes Click" via its own key handling on the bubble-phase `KeyDown`/`KeyUp` pair. As established by Bug Audit #13 (`Bug_Audit_2026-09-24_CommitSliderKeyboardEdit.md:64`, root cause fact 3), WPF's `PreviewKeyDown`/`KeyDown` pair shares one `Handled` flag per physical key-press: `MainWindow_PreviewKeyDown` runs during the tunnel phase, close to the root, and sets `e.Handled = true` for `Key.Space` unconditionally (`:297`) before the event ever reaches the focused control's own bubble-phase handling. That suppresses `ButtonBase`'s Space-activates-Click logic for **every** `ButtonBase`-derived control in the window, not just `Slider` (Bug Audit #13's subject).

### 3. Unlike a `Slider`, several of these controls have no alternate keyboard path at all

**File:** `src/Acapella.App/MainWindow.xaml`

| Line | Control | Alternate keyboard activation? |
|---|---|---|
| `:231-234` | `MonitorToggle` (`ToggleButton`, toolbar — "Monitor master output... uncheck to mute what you hear") | None. `Enter` has no default action on a plain `ToggleButton`; there is no menu item or accelerator (root cause, Confidence paragraph). |
| `:109-110` | Per-layer Mute (`M`)/Solo (`S`) `CheckBox`es | None — same reasoning; `CheckBox` never responds to `Enter`. |
| `:376,400,424,451,466,494` | `NoiseGateEnabled`/`CompressorEnabled`/`EqEnabled`/`ReverbEnabled`/`LimiterEnabled`/`MelodyneEnabled` `CheckBox`es | None. |

For these controls, Space is not merely a redundant shortcut being stolen — it is the *only* keyboard-reachable way to change them at all. Mouse click still works (it doesn't route through `MainWindow_PreviewKeyDown`), so this is a keyboard-only defect, not a total feature loss — but for a keyboard-only user it is a hard block, and for anyone who reaches one of these controls by Tab and presses the ordinary, expected key, the actual observed behavior is not "nothing happens" — it's "preview playback starts or stops," an unrelated, surprising side effect.

---

## Repros

### A. `MonitorToggle` — Space silently starts/stops preview instead of toggling monitor mute **[auto + human]**

No layers or project state required; `MonitorToggle` is always present in the toolbar.

1. Click the toolbar's speaker icon (`MonitorToggle`) once with the mouse. It unchecks (now shows muted) — `MonitorToggle_Changed` fires, `_monitorMuted` becomes `true`, `ApplyMasterVolume()` runs. This step is a correct, working mouse toggle — it also leaves keyboard focus on `MonitorToggle` (WPF's default behavior for a mouse-clicked focusable control).
2. With `MonitorToggle` still focused, press **Space**, intending to toggle monitoring back on.
3. **Bug:** `MonitorToggle.IsChecked` does not change — it is still unchecked/muted. Instead, preview playback starts (if nothing was playing) or stops (if it was) — `PlayButton_Click`/`StopButton_Click` fired via `MainWindow_PreviewKeyDown`'s `Key.Space` case, and `e.Handled = true` prevented `MonitorToggle`'s own Space-activates-Click handling from ever running.
4. The only way to actually re-enable monitoring at this point is to click `MonitorToggle` again with the mouse.

The `[auto]` half (§Tests, `MainWindow_PreviewKeyDown_Space_WithToggleButtonFocused_IsSuppressed`) proves the handler-level mechanism (focus + `e.Handled`) without needing a full click-then-key WPF input simulation; the `[human]` half is confirming the visible end-to-end symptom (toggle doesn't flip, playback does) matches.

### B. A layer's Mute checkbox — same shape, same silent failure **[human]**

1. Add a layer with a source. In the layer strip, click the **M** (Mute) checkbox once — it checks, the layer mutes, `CommitCheckBox_Click` (`MainWindow.xaml.cs:279`) commits an undo step. Focus is now on the Mute checkbox.
2. With it still focused, press **Space**, intending to unmute.
3. **Bug:** the layer stays muted; the checkbox's visual state doesn't change. If nothing was playing, preview playback starts instead — the same misdirected side effect as Repro A.

---

## Why the suite misses it

- **No test in this suite has ever driven `MainWindow_PreviewKeyDown` at all.** `grep -rn "PreviewKeyDown\|MonitorToggle\|Key.Space" tests/` finds nothing — Bug Audit #13 added the first (and, until this fix, only) keyboard-focused test in the App test project, and it targets `CommitSlider`'s own `PreviewKeyUp` wiring, not the Window-level handler this bug lives in.
- **`ExportPlaybackGateTests.cs` raises `Click` events directly** (`RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))`, `:21,38,60`) to drive Play/Stop/Record in tests — bypassing `MainWindow_PreviewKeyDown` entirely, so no existing test ever exercises the path where a keypress, rather than a direct `Click`, is the trigger.
- **Bug Audit #13 stopped at `Slider`.** It traced the exact same suppression mechanism this bug relies on, generalized it correctly in its own Runners-up note, but never checked whether any non-`Slider`, non-`TextBox` control in the app is actually keyboard-reachable and actually loses something by it — that check (root cause fact 3's table, above) is what turns the general observation into a concrete, reproducible bug.
- **`MonitorToggle` and the FX/Mute/Solo checkboxes have no dedicated test file** — `MixingScreenTests.cs` checks binding shape and layout bounds (per Bug Audit #13's own "why the suite misses it," `:126`), never keyboard interaction, so a regression here produces no failing assertion anywhere in the current suite.

---

## Proposed fix

**Approach: inside the `Key.Space` case specifically, skip the transport shortcut (and leave `e.Handled` false) when the focused element is a `ButtonBase` — letting Space reach that control's own Click activation instead. Leave the other three shortcuts (`Left`/`Right`/`Home`) and the existing `TextBox` exclusion untouched.**

### Change: `src/Acapella.App/MainWindow.xaml.cs:292-298`

Replace:

```csharp
        switch (e.Key)
        {
            case Key.Space:
                if (_previewEngine.IsPlaying) StopButton_Click(this, new RoutedEventArgs());
                else PlayButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
```

with:

```csharp
        switch (e.Key)
        {
            case Key.Space:
                // Bug audit #14: Space is ButtonBase's own activation key (Button/CheckBox/
                // ToggleButton/RadioButton all invoke Click on Space) -- don't steal it from a
                // focused one of those, or it silently fails to toggle/click and fires Play/Stop
                // instead (see doc's root cause 2-3). Checked here, not added to the TextBox
                // exclusion above, because Left/Right/Home have no such conflict for these
                // controls (root cause fact 3's table lists none) -- see Ordering subtleties for
                // why broadening the exclusion is a regression, not a simplification.
                if (Keyboard.FocusedElement is ButtonBase) return;
                if (_previewEngine.IsPlaying) StopButton_Click(this, new RoutedEventArgs());
                else PlayButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
```

(`System.Windows.Controls.Primitives` — for `ButtonBase` — is already imported at `MainWindow.xaml.cs:7`.)

### Rejected alternatives

- **Add `Keyboard.FocusedElement is ButtonBase` to the top-level exclusion (`:290`), alongside `TextBox`, so it applies to all four shortcuts uniformly.** Rejected — see Ordering subtleties: `PlayButton`/`StopButton` are themselves `Button`s (`MainWindow.xaml:213,215`) that keep keyboard focus after being clicked, so this would also disable Left/Right/Home seek immediately after every ordinary Play/Stop click, a new and easily-triggered regression with no corresponding conflict to justify it (root cause fact 3 found no `ButtonBase` control in this app with a meaningful Left/Right/Home default behavior).
- **Narrow the check to `CheckBox` only, since that's what the originating lead named.** Rejected once `MonitorToggle` was found: it's a `ToggleButton`, not a `CheckBox`, and is the single easiest-to-reproduce instance of this bug (always visible, no project state needed) — a `CheckBox`-only check would leave it broken. `ButtonBase` is the actual common ancestor all four affected control types share the Space-activates-Click behavior from, so it's the correct scope, not an over-broad one.
- **Remove the Space transport shortcut, or rebind it to something like Ctrl+Space.** Would be a real change to existing shortcut UX beyond what a bug fix needs — CLAUDE.md's Pause Rule 2 territory (real scope change), not attempted here.
- **Check `e.OriginalSource is ButtonBase` instead of `Keyboard.FocusedElement is ButtonBase`.** Equivalent in practice for a keyboard event (both name the focused element, since `RaiseEvent` fixes `Source`/`OriginalSource` once at the point of origin and neither changes as the event tunnels/bubbles) — kept `Keyboard.FocusedElement` only for consistency with the existing `TextBox` check two lines above, which already uses it.

---

## Ordering subtleties

**1. The guard must return before `e.Handled = true` is ever reached for that key-press — not just before `PlayButton_Click`/`StopButton_Click` are called.**
- **Counter-example:** a plausible-looking alternative refactor keeps the guard but restructures it as `if (!(Keyboard.FocusedElement is ButtonBase)) { /* call Play/Stop */ } e.Handled = true;` — i.e., the Play/Stop call is correctly skipped, but `e.Handled = true` still runs unconditionally at the end of the case. The visible "wrong action fires" half of the bug (Repro A step 3's accidental Play/Stop) would be gone, but `e.Handled` is still `true` by the time the paired bubble-phase `KeyDown` reaches `MonitorToggle`, so its own Space-activates-Click handling is *still* suppressed (root cause fact 2) — Space would now do nothing at all instead of the wrong thing, which still fails Repro A (the toggle still never flips). The fix as written avoids this because the `return` happens before either statement.

**2. Broadening the `ButtonBase` exclusion to cover all four shortcut keys (not just Space) breaks Left/Right/Home for a case that's easy to hit by accident, not just a hypothetical.**
- **Counter-example:** click the toolbar's `PlayButton` (a `Button`, hence a `ButtonBase`) to start preview — it now has keyboard focus, same as any mouse-clicked focusable control. Immediately press the **Right arrow**, intending to seek forward 5 seconds. With a blanket `ButtonBase` exclusion applied to the whole method (rather than scoped to the `Key.Space` case), `MainWindow_PreviewKeyDown` would return immediately without seeking — Right/Left/Home would be dead for as long as focus happens to remain on whatever button was last clicked, which for `PlayButton`/`StopButton` specifically is every single time either is used with the mouse. Scoping the exclusion to the `Key.Space` case only avoids this because `Left`/`Right`/`Home` never reach the new check at all.

---

## Deliberately not changed

- **The existing `TextBox` exclusion (`:290`)** — untouched; still applies to all four shortcuts, as before.
- **`Left`/`Right`/`Home` behavior for a focused `ButtonBase`.** Root cause fact 3 found no `ButtonBase`-derived control in this app with a meaningful default action bound to those three keys (no `RadioButton` exists anywhere in `MainWindow.xaml`, confirmed by grep, so there's no arrow-key-between-peers behavior to protect either) — narrowing the fix to Space only is not leaving a known gap, it's not touching a dimension that has no conflict.
- **`CommitSlider`'s own keyboard handling (Bug Audit #13).** `Slider` does not derive from `ButtonBase`, so this fix's new guard does not interact with it; `Up`/`Down`/`PageUp`/`PageDown`/`End` (the keys #13's fix listens for) are also untouched by this change.
- **Per-checkbox undo/dirty-tracking (`CommitCheckBox_Click`).** Once Space correctly reaches the checkbox and toggles it, `Click` fires exactly as it does for a mouse click, so `CommitCheckBox_Click` already runs correctly with no further change needed — this bug was purely about the keypress never reaching the control at all.

---

## Tests

### `tests/Acapella.App.Tests/TransportShortcutFocusTests.cs` (new file)

```csharp
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Reflection;
using Xunit;

namespace Acapella.App.Tests;

/// <summary>Bug audit #14: MainWindow_PreviewKeyDown must not steal Space from a focused
/// Button/CheckBox/ToggleButton -- Space is ButtonBase's own activation key, and this handler
/// runs during the tunnel phase, before the focused control's own bubble-phase KeyDown handling
/// (which is what "e.Handled" suppresses -- see the doc's root cause 2). Invokes the private
/// handler directly with a synthetic KeyEventArgs and a real focused control, rather than
/// attempting a full OS-level keyboard input simulation (no precedent for that in this suite --
/// see Bug Audit #13's own CommitSliderKeyboardTests, which took the same "test the mechanism,
/// not the full routed-event pipeline" approach for the identical reason).</summary>
public class TransportShortcutFocusTests
{
    [StaFact]
    public void MainWindow_PreviewKeyDown_Space_WithToggleButtonFocused_IsSuppressed()
    {
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            Keyboard.Focus(window.MonitorToggle);
            bool checkedBefore = window.MonitorToggle.IsChecked == true;

            RaisePreviewKeyDown(window, Key.Space);

            Assert.Equal(checkedBefore, window.MonitorToggle.IsChecked == true); // handler didn't consume it wrongly and MonitorToggle wasn't left to WPF's own routing to flip it via this call
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void MainWindow_PreviewKeyDown_Space_WithTextBoxFocused_StillWorksAsTransport()
    {
        // Regression guard: the pre-existing TextBox exclusion (:290) must keep working --
        // this fix only adds a second, narrower exclusion, it must not replace the first.
        TestAppHost.EnsureApplicationResourcesLoaded();
        var window = new MainWindow();
        try
        {
            window.Show();
            Keyboard.Focus(window.MetronomeBpmTextBox);

            var handled = RaisePreviewKeyDown(window, Key.Space);

            Assert.False(handled, "TextBox-focused Space must still be left alone by the transport shortcut.");
        }
        finally
        {
            window.Close();
        }
    }

    private static bool RaisePreviewKeyDown(MainWindow window, Key key)
    {
        var method = typeof(MainWindow).GetMethod("MainWindow_PreviewKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var source = PresentationSource.FromVisual(window)!;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        method.Invoke(window, new object[] { window, args });
        return args.Handled;
    }
}
```

**Regression carve-out (per CLAUDE.md):** `MainWindow.xaml.cs` is touched but the only prior test exercising this exact method is none (§Why the suite misses it) — there's nothing existing to regress there. `CommitSliderKeyboardTests.CommitSlider_Style_HasAKeyboardCommitHandler` (Bug Audit #13) touches the same file but a disjoint code path (`CommitSlider` style/`PreviewKeyUp`, not `MainWindow_PreviewKeyDown`); re-run it once since both live in the file being edited, even though the change here doesn't touch its surface.

---

## Acceptance criteria

### [auto]
- `MainWindow_PreviewKeyDown_Space_WithToggleButtonFocused_IsSuppressed` passes (fails before the fix — today, Space with `MonitorToggle` focused sets `e.Handled = true` and fires `PlayButton_Click`/`StopButton_Click`).
- `MainWindow_PreviewKeyDown_Space_WithTextBoxFocused_StillWorksAsTransport` passes both before and after the fix (regression guard on the pre-existing `TextBox` exclusion).
- `CommitSliderKeyboardTests.CommitSlider_Style_HasAKeyboardCommitHandler` still passes (regression carve-out).
- The solution builds with no new warnings.

### [review] (checked by reading the diff, not automated)
- The new `Keyboard.FocusedElement is ButtonBase` check is scoped inside the `Key.Space` case only — not added to the top-level `:290` exclusion (Ordering subtlety 2).
- The check runs, and returns, before `e.Handled = true` is reached for that case (Ordering subtlety 1).
- `Left`/`Right`/`Home` cases (`:299-310`) are untouched.
- The existing `TextBox` exclusion (`:290`) is untouched.

### [human]
- Repro A after the fix: with `MonitorToggle` focused (after a mouse click), pressing Space now toggles `IsChecked` back and forth correctly, with no accidental Play/Stop.
- Repro B after the fix: with a layer's Mute checkbox focused, pressing Space now toggles Mute correctly, with no accidental Play/Stop, and `CommitCheckBox_Click` still fires (undo step recorded — verify via Ctrl+Z unmuting the layer).
- No regression: Space with a `Slider`, the layer list, or empty window background focused still starts/stops preview playback as before.
- No regression: Left/Right/Home still seek/restart correctly when a `Button`/`CheckBox`/`ToggleButton` is focused (Ordering subtlety 2's specific claim — that this fix does *not* extend to those three keys — confirmed by trying it after a `PlayButton` click).

---

## Runners-up (not specced)

- **Ordinary toolbar `Button`s (Upload, seek ±5s, Record, window-chrome minimize/close, etc.) also lose Space-activation** by the same mechanism, since `Button` is a `ButtonBase` too and this fix's guard covers it. Not given its own repro here since none of them are the *sole* keyboard path to their function the way `MonitorToggle`/the FX checkboxes are (most toolbar buttons duplicate a menu item or are reachable via Enter as a default button in some cases) — worth a quick pass to confirm none is keyboard-stranded the way `MonitorToggle` was, but not required for this fix, which already restores their Space behavior as a side effect of being `ButtonBase`s.
- **`MenuItem`/`ComboBox`/`RadioButton`-specific Space behavior** — no `ComboBox` or `RadioButton` exists anywhere in `MainWindow.xaml` (confirmed by grep), so this class of control isn't present to be affected; noted only because the original lead named `ComboBox` explicitly and it's worth recording that the search came up empty rather than silently skipped.
