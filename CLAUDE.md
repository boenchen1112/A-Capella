# CLAUDE.md — Acapella Rebuild Project

This file governs how Claude Code should operate autonomously on this project. Read this before starting any session. The build plan (see Reference, below) is the source of truth for phases, architecture, and acceptance criteria. This file is the source of truth for *how to behave* while executing it.

## Prime Directive

**Goal right now is a working, end-to-end app — not a polished one.** Get each phase's acceptance criteria met in the simplest way that actually works, then move to the next phase. Do not:
- Refactor working code for style/elegance unless it's blocking the next phase.
- Loop on "improving" a function that already meets its acceptance criteria.
- Add abstractions, configurability, or error handling beyond what's needed to pass the stated acceptance criteria.
- Gold-plate UI. Functional and ugly beats polished and half-finished.

If something works but is rough, leave a one-line `// TODO(polish):` comment and move on. Do not act on those TODOs unless explicitly told to.

Quality passes happen later, as a separate explicit phase, only when asked for.

**Definition of done, strictly applied:** a task or phase is done the moment its stated **[auto]** acceptance criteria (see the build plan's tagging) pass once — not when the code feels solid, not after a second confirming pass, not after handling extra edge cases nobody asked for. The moment they pass, commit and move to the next task immediately, and start the next phase provisionally while any **[human]**-tagged criteria from the current phase wait for the next check-in. Do not re-run, re-verify, or "just double check" something that already passed — with one exception: if a later change touches code an earlier phase's tests depend on, re-run that earlier phase's automated tests once; if they now fail, that earlier phase is no longer done and needs fixing before continuing. Do not spend cycles on: micro-optimizing performance, tweaking code style or naming, refactoring for elegance, adding error handling beyond what's needed for the acceptance criteria, or writing extra tests beyond what's needed to check the stated criteria. Completion of all phases beats quality of any single phase.

## Operating Mode: Continuous, Minimal-Pause

Default behavior is to keep working without stopping to ask permission. The three pause rules below exist to catch rare, genuinely blocking situations — they are not routine checkpoints, and should almost never trigger during normal work. Outside them, pick the most reasonable default, note the assumption in the commit message, and keep going.

### 1. Destructive / Irreversible Actions — PAUSE
Stop and ask before:
- Force-pushing, rewriting git history, or deleting branches.
- Deleting recorded media (see Recorded Media, below), project files, or any other user data — including files in the workspace folder outside the code repo.
- Uninstalling dependencies or SDKs already in use elsewhere on the machine.
- Creating, modifying, or deleting **user data** outside this project's repo directory.
- Dropping/overwriting a database or project file format in a way that discards existing data.

This rule is about user data, not about staying inside the repo path. It does **not** require a pause for: installing packages/SDKs via winget, NuGet, or vcpkg; writing to build caches, `%TEMP%`, or tool-managed directories; or read-only access anywhere on the machine (e.g. scanning `C:\Program Files\Common Files\VST3` for installed plugins). Those are all fine without asking.

Everything *inside* the repo that's tracked by git does not need a pause before editing — commit frequently so changes stay reversible, and treat git history as the safety net.

### 2. Real Scope Changes — PAUSE
Stop and ask before:
- Swapping a chosen library/technology from the plan (e.g., replacing JUCE, Rubber Band, WPF, NAudio, SkiaSharp, or the FFmpeg CLI-plus-pipes approach with an alternative).
- Adding a new dependency not listed in the plan.
- Changing any of the locked-in product decisions: 4-layer cap, 2x2 grid layout, MP4 output, the FX list (see the scope snapshot under Reference), Melodyne-via-VST3/ARA as the primary pitch path with a native fallback.
- Expanding scope to features explicitly marked out-of-scope in the plan (collaboration, other layouts, more layers, other output formats).

Small implementation-detail choices within an already-approved technology do not need a pause. One pre-approved escalation: if piped preview decode via the FFmpeg CLI proves too slow, switching to FFmpeg.AutoGen is allowed without a pause — the plan already anticipates this, so it isn't a scope change.

### 3. Input Only I Can Provide — PAUSE
Stop and ask when:
- The plugin scanner stops finding Melodyne or the FabFilter set listed under Environment. Both are confirmed installed, so a miss means the machine changed, and only I can reinstall.
- A **[human]**-tagged acceptance criterion needs judgment (does this sound in sync, does the pitch correction sound natural, does this look right) — batch these into short check-ins at the end of each phase rather than after every change, per the Definition of Done above.
- Something in the brief is genuinely ambiguous and no reasonable default resolves it.

### 4. Otherwise — KEEP GOING
- Write code, run builds, run tests, fix compile errors, iterate on bugs, commit to git.
- Move to the next task/phase automatically once the current one's **[auto]** acceptance criteria (from the build plan) are met.
- Report only at phase boundaries or when blocked by one of the three pause conditions above — not after every file edit.

## Git Discipline

- Commit after each meaningfully complete unit of work (a function working, a test passing, a phase's acceptance criteria met). Small, frequent commits, not one giant commit per phase.
- Write commit messages that state what now works, not what was attempted.
- Never force-push or rewrite history without asking (see Pause Rule 1).
- Use `git mv` for renames, not delete-and-recreate, so history and tracking stay intact. Whenever you touch a file's name or path, verify with `git status` that git sees a rename, not an add+delete.

## Recorded Media

Test recordings and other captured audio/video are large binaries and don't belong in git history; `media/` and `recordings/` are gitignored. These directories are still protected by Pause Rule 1 (deleting anything in them requires asking) — they're just not backed by git, so be more careful with them than with tracked code.

## Testing Approach

- Write automated tests for anything with an objective, checkable acceptance criterion: latency offset math, project file save/load round-trips, mixdown correctness, frame compositing placement, export file validity. These are the **[auto]**-tagged criteria in the build plan.
- Do not write tests for subjective qualities (does it sound natural, does it look right, does it feel in sync to a listener). Those are the **[human]**-tagged criteria — verified by the person at phase-boundary check-ins (Pause Rule 3), not asserted programmatically.
- Where a criterion looks subjective but can be made objective, prefer that: e.g. a sync-offset check doesn't need a human to listen if a recorded click track's offset is measured via a cross-correlation script instead. A virtual loopback audio device (VB-Cable or Windows' Stereo Mix) makes latency and "does the metronome leak into the recording" checks fully automatable — use one rather than defaulting straight to a human check.
- A phase is "done" when its **[auto]** criteria in the build plan are met, not when the code is maximally clean.
- Regression carve-out: if a later phase's change touches code an earlier phase's automated tests cover, re-run those tests once. A failure means the earlier phase's "done" status is revoked until it's fixed.

## Environment

**Installed:**
- Git
- Visual Studio "18" (2026) Community, C++ workload (MSVC 14.51.36231, Windows 10 SDK 10.0.26100.0)
- Standalone CMake 4.3.4 (`cmake` on PATH)
- Melodyne (via FL Studio's plugin install): `C:\Program Files\Common Files\VST3\Celemony\Melodyne\Melodyne.vst3`, v5.4.1.4, ARA-capable (confirmed at runtime in Phase 2A).
- **All five FabFilter targets, confirmed present** under `C:\Program Files\Common Files\VST3\FabFilter\` (the path `HostedPluginCatalog.cs` expects): Pro-Q 4, Pro-C 3, Pro-L 2, Pro-G, Pro-R 2 (also Pro-DS, Pro-MB present but out of scope).

**Native module:** always build via `build.ps1`, never `cmake -G "Visual Studio 18 2026"` (broken VS toolset file — details in `src/Acapella.Host.Native/CLAUDE.md`).

**Locked technology choices** (do not reconsider without triggering Pause Rule 2):
- UI: WPF
- Audio I/O: NAudio
- Video/audio capture, decode, encode, mux: FFmpeg via CLI + raw pipes (escalate to FFmpeg.AutoGen only if piped preview decode is too slow — pre-approved, see Pause Rule 2)
- Compositing: SkiaSharp (SkiaSharp.Views.WPF for the WPF integration)
- Pitch correction fallback: Rubber Band Library + a pitch-tracking library
- Plugin hosting bridge (FabFilter and Melodyne/ARA alike): JUCE (C++, `juce_audio_processors`' `VST3PluginFormat`) exposing a plain C ABI, called from C# via P/Invoke — not C++/CLI. Own-window (JUCE `DocumentWindow`) native editors, never embedded inline.
- Hosted plugin vendors (hard limit): only FabFilter (Pro-Q 4, Pro-C 3, Pro-L 2, Pro-G, Pro-R 2) and Melodyne. No other vendor without a new explicit decision.

**Canonical commands:**
- Build native module (only needed when `src/Acapella.Host.Native` changes): `powershell -File src/Acapella.Host.Native/build.ps1` — must run *before* `dotnet build` picks up a changed `AcapellaHostNative.dll` (both `Acapella.App.csproj` and `Acapella.Engine.Tests.csproj` copy it into their own output post-build).

## Reference

- Full phased plan, architecture, and per-phase acceptance criteria: `Acapella_Build_Plan_v7.md`, written as a delta on `Acapella_Build_Plan_v6.md`, so read both (v5 and the original `Acapella_Build_Plan.md` are kept for history only).
- Confirmed scope snapshot: 4-layer cap, upload-or-record for layer 1, toggleable metronome, EQ/pan/noise gate/compressor/limiter/pitch-correction/reverb FX (native + FabFilter-hosted backends), fixed 2x2 grid, MP4 output, native Windows C#/.NET + JUCE native module, Melodyne (ARA) + FabFilter (generic VST3) as the only hosted-plugin vendors.
