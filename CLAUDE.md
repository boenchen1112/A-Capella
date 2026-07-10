# CLAUDE.md — Acapella Rebuild Project

This file governs how Claude Code should operate autonomously on this project. Read this before starting any session. The full technical plan is in `Acapella_Build_Plan_v3_MelodyneVST.md` (Melodyne VST3/ARA path) — that file is the source of truth for phases, architecture, and acceptance criteria. This file is the source of truth for *how to behave* while executing it.

## Prime Directive

**Goal right now is a working, end-to-end app — not a polished one.** Get each phase's acceptance criteria met in the simplest way that actually works, then move to the next phase. Do not:
- Refactor working code for style/elegance unless it's blocking the next phase.
- Loop on "improving" a function that already meets its acceptance criteria.
- Add abstractions, configurability, or error handling beyond what's needed to pass the stated acceptance criteria.
- Gold-plate UI. Functional and ugly beats polished and half-finished.

If something works but is rough, leave a one-line `// TODO(polish):` comment and move on. Do not act on those TODOs unless explicitly told to.

Quality passes happen later, as a separate explicit phase, only when asked for.

**Definition of done, strictly applied:** a task or phase is done the moment its stated acceptance criteria pass once — not when the code feels solid, not after a second confirming pass, not after handling extra edge cases nobody asked for. The moment it passes, commit and move to the next task immediately. Do not re-run, re-verify, or "just double check" something that already passed. Do not spend cycles on: micro-optimizing performance, tweaking code style or naming, refactoring for elegance, adding error handling beyond what's needed for the acceptance criteria, or writing extra tests beyond what's needed to check the stated criteria. Completion of all phases beats quality of any single phase.

## Operating Mode: Continuous, Minimal-Pause

Default behavior is to keep working without stopping to ask permission. These four rules exist to catch rare, genuinely blocking situations — they are not routine checkpoints, and should almost never trigger during normal work. **If in doubt, do NOT pause.** Pick the most reasonable default, note the assumption in the commit message, and keep going. Only pause in these four cases:

### 1. Destructive / Irreversible Actions — PAUSE
Stop and ask before:
- Force-pushing, rewriting git history, or deleting branches.
- Deleting recorded media, project files, or any user data — including files in the workspace folder outside the code repo.
- Uninstalling dependencies or SDKs already in use elsewhere on the machine.
- Any file operation outside this project's repo directory.
- Dropping/overwriting a database or project file format in a way that discards existing data.

Everything *inside* the repo that's tracked by git does not need a pause before editing — commit frequently so changes stay reversible, and treat git history as the safety net.

### 2. Real Scope Changes — PAUSE
Stop and ask before:
- Swapping a chosen library/technology from the plan (e.g., replacing JUCE, Rubber Band, FFmpeg, NAudio, SkiaSharp with an alternative).
- Adding a new dependency not listed in the plan.
- Changing any of the locked-in product decisions: 4-layer cap, 2x2 grid layout, MP4 output, the FX list (EQ/pan/noise gate/metronome/pitch correction), Melodyne-via-VST3/ARA as the primary pitch path with native fallback.
- Expanding scope to features explicitly marked out-of-scope in the plan (collaboration, other layouts, more layers, other output formats).

Small implementation-detail choices within an already-approved technology (e.g., which specific EQ filter shape to use with NAudio) do not need a pause.

### 3. Input Only I Can Provide — PAUSE
Stop and ask when:
- Phase 2A's plugin scanner can't find an ARA-capable Melodyne install. Note: Melodyne is already installed and used as a plugin via FL Studio, so it should be sitting in the standard shared VST3 folder and discoverable without any new install — this pause condition is a fallback in case the scan doesn't find it or the installed tier turns out not to expose ARA, not an expected blocker.
- A decision requires subjective audio/video quality judgment (does this sound in sync, does the pitch correction sound natural, does this look right) — these require me to actually listen/watch. Batch these into short check-ins at the end of each phase rather than after every change.
- Something in the brief is genuinely ambiguous and no reasonable default resolves it.

### 4. Otherwise — KEEP GOING
- Write code, run builds, run tests, fix compile errors, iterate on bugs, commit to git.
- Move to the next task/phase automatically once the current one's acceptance criteria (from the build plan) are met.
- Report only at phase boundaries or when blocked by one of the three pause conditions above — not after every file edit.

## Git Discipline

- Commit after each meaningfully complete unit of work (a function working, a test passing, a phase's acceptance criteria met). Small, frequent commits, not one giant commit per phase.
- Write commit messages that state what now works, not what was attempted.
- Never force-push or rewrite history without asking (see Pause Rule 1).

## Testing Approach

- Write automated tests for anything with an objective, checkable acceptance criterion: latency offset math, project file save/load round-trips, mixdown correctness, frame compositing placement, export file validity.
- Do not write test