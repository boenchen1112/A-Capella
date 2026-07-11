# Fable 5 Review — Build Plan + CLAUDE.md

> Evaluation of `Acapella_Build_Plan.md` and `CLAUDE.md` before autonomous execution begins. Issues ordered by severity. Items marked **[BLOCKING]** should be fixed before the first Claude Code session.

## A. Blocking issues

### A1. CLAUDE.md is truncated mid-sentence **[BLOCKING]**
The file ends at `- Do not write test` (verified at byte level). The Testing Approach section is incomplete, so the testing philosophy the session summary describes ("automate objective checks, reserve subjective judgment for human check-ins") is not actually in the file the agent will read. An agent following a truncated instruction file may invent its own testing policy.

**Fix:** restore the ending, e.g.:
> Do not write tests for subjective qualities (does it sound natural, does it look right). Those are verified by human check-ins at phase boundaries (Pause Rule 3). Do not write tests beyond what checks the stated acceptance criteria.

### A2. Plan filename mismatch between disk, git, and CLAUDE.md **[BLOCKING]**
- CLAUDE.md points to `Acapella_Build_Plan_v3_MelodyneVST.md`.
- The file on disk is `Acapella_Build_Plan.md` (renamed without `git mv`).
- Git still tracks the old name and sees the new one as untracked.

First session, the agent will look for a file that doesn't exist. **Fix:** `git add -A && git commit` to record the rename, and update the reference in CLAUDE.md. Also add `Acapella_Session_Summary.md` and `.claude/` (or gitignore the latter) while at it.

### A3. Phase 0's audit target doesn't exist in the repo **[BLOCKING]**
Phase 0 audits "the existing app's sync + FX code", but the repo contains only three markdown files, and no document states where the existing app's code lives. Worse, Pause Rule 1 forbids "any file operation outside this project's repo directory", so the agent cannot legally go find it. Phase 0 dead-ends immediately.

**Fix (pick one):** copy the existing app's source into this repo (e.g. `legacy/`), or add its absolute path to CLAUDE.md with an explicit read-only exemption to Pause Rule 1.

## B. High-priority technical concerns

### B1. Phase 2A's "automatic-only correction, no editor UI" first cut will not work with Melodyne
Melodyne via ARA analyzes audio, but applying pitch correction is user-driven: the Correct Pitch macro lives inside Melodyne's Note Editor, and ARA exposes no host-side "apply correction" call. A host that analyzes and immediately renders gets back essentially unmodified audio. So task 5 (embed Melodyne's editor window), currently marked *optional*, is actually **mandatory** for 2A to deliver any value.

**Fix:** in Phase 2A, promote editor embedding to required; reframe "automatic correction" as exclusively Phase 2B's job. This also further validates building 2B first — it is the only automatic path.

### B2. Acceptance criteria mix agent-verifiable and human-only checks
"Guide track audible", "metronome doesn't leak", "audibly correcting pitch", "synced correctly" require ears. Combined with CLAUDE.md's "done the moment criteria pass once", the agent must either stall at every phase or self-certify things it cannot hear.

**Fix:** tag every acceptance criterion `[auto]` or `[human]`. Phase advances when all `[auto]` pass; `[human]` items batch into the phase-boundary check-in, with the next phase started provisionally. Where possible, convert `[human]` to `[auto]`: the ±20ms sync check can be objective (record a click track once, verify offset via cross-correlation script). A virtual loopback device (VB-Cable / Stereo Mix) would make latency and leak tests fully automatable with no human in the loop.

### B3. Phase acceptance criteria reference features from later phases
- Phase 3 acceptance: "Reopening a saved project restores all per-layer parameters" — that's Phase 5 (persistence).
- Phase 1 acceptance: "Latency offset persists across sessions" — also needs a settings store before Phase 5.

**Fix:** either move those criteria to Phase 5, or explicitly add "minimal settings/project save-load (subset of Phase 5)" as a Phase 1/3 task so the agent doesn't invent persistence ad hoc or flag a false scope conflict against Pause Rule 2.

### B4. Undecided technology choices will trip Pause Rule 2
The plan leaves open: "WPF or WinUI 3", "NAudio or CSCore", "FFmpeg CLI wrapper or FFmpeg.AutoGen". Pause Rule 2 locks technology swaps, but nothing locks the initial pick, so the agent must choose silently and any later reconsideration looks like a scope change.

**Fix — lock these now (recommendations):**
- **WPF** (mature, best P/Invoke + native window embedding story for the Melodyne editor, SkiaSharp.Views.WPF exists).
- **NAudio** (WASAPI support, largest community).
- **FFmpeg via CLI + raw pipes** for capture/decode/encode; escalate to AutoGen only if piped preview decode proves too slow (note that as a pre-approved escalation so it doesn't count as a Pause Rule 2 swap).

### B5. Within-layer A/V sync is unaddressed
Phase 1 handles mic-vs-guide-track latency, but not webcam frames vs mic samples inside one recorded layer (different capture APIs, different latencies, different clocks). This is the classic source of lip-sync drift.

**Fix:** add a Phase 1 task: capture audio+video in a single FFmpeg dshow process into one container so both streams share a clock, and add an acceptance criterion "a recorded layer's own audio and video are in sync (clap test)".

### B6. Phase 2A spike timebox is in human units
"1–2 weeks" doesn't map to agent sessions. **Fix:** define it as e.g. "N sessions or M failed build/run attempts at the analyze→render loop, then stop and report", so the agent knows when the timebox expires.

## C. CLAUDE.md fixes (non-blocking but will cause friction)

### C1. Pause Rule 1 is broader than intended
"Any file operation outside this project's repo directory" collides with routine work: `winget install` of .NET SDK/FFmpeg/CMake, NuGet/vcpkg caches, `%TEMP%`, reading `C:\Program Files\Common Files\VST3` for the plugin scan. Read literally, the agent pauses constantly or violates the rule constantly (both bad outcomes for an autonomy contract).

**Fix:** rescope to "creating/modifying/deleting *user data* outside the repo. Package and SDK installs, build caches, temp files, and read-only access anywhere are allowed."

### C2. "Never re-verify" needs a regression carve-out
As written, after Phase 4 modifies code Phase 1 depends on, re-running Phase 1's tests is forbidden. **Fix:** add "when a change touches code an earlier phase depends on, run that phase's automated tests once; if they fail, the earlier phase is no longer done."

### C3. Recorded media in git
Test recordings are large binaries; "git history as the safety net" will bloat the repo fast. **Fix:** `.gitignore` a `media/` (or `recordings/`) directory and note in CLAUDE.md that media is protected by Pause Rule 1 (deletion requires asking), not by git.

### C4. Add an Environment section to CLAUDE.md
State what's installed (VS 2022 + C++ workload, git, Melodyne via FL Studio), what isn't yet (.NET SDK, standalone CMake, FFmpeg, JUCE/VST3 SDK), and canonical build/test commands as they come to exist. Otherwise every session rediscovers this. Update it as installs happen.

### C5. Widen the permission allow-list
`.claude/settings.local.json` currently lacks: `git mv`, `git restore`, `git checkout`, `git branch`, `git init`-adjacent ops, `dotnet run`, `dotnet new`, `winget install`, `ffmpeg`. Missing entries mean approval prompts mid-run, which defeats the background-session plan.

## D. Answers to the five open items (Session Summary §8)

1. **Defer 2A to the end?** Yes, and B1 makes the case stronger: 2B is the *only* automatic-correction path, so it's load-bearing, not just a fallback. One amendment: do a 30-minute de-risk during Phase 0/1 anyway — confirm Melodyne's `.vst3` is present in `C:\Program Files\Common Files\VST3` and record its version/tier. If the tier problem exists, you want to know months early, without front-loading the full spike.

2. **C-ABI/P-Invoke vs C++/CLI?** C-ABI is the right call; C++/CLI drags the CLR into the JUCE build and complicates debugging across the boundary. Two specifics to fix in the plan's proposed surface: `RenderCorrectedAudio(sourceHandle) -> float[]` is not a valid C ABI shape. Use caller-allocated buffers: `GetCorrectedLength(handle) -> int64` then `RenderCorrected(handle, float* buf, int64 capacity) -> int64`. And avoid function-pointer callbacks for analysis progress; poll a `GetAnalysisState(handle)` instead to keep marshaling trivial.

3. **4-layer/2x2 as config values?** Structure is fine if: the project file stores a layout id plus per-layer cell index (not positions hardcoded to 4), mix engine and compositor iterate a layers collection of size N, and only the UI layer assumes 4/2x2. Add that sentence to the plan so it's checkable at review.

4. **Phase 0 audit gaps?** Add: (a) where the existing app's code lives (see A3, blocking), (b) which audio API and sample format/rate it uses, (c) its threading model around capture, (d) licenses of its existing dependencies (matters if code gets reused).

5. **Non-destructive params vs ARA state?** Main risk is chain ordering and cache invalidation, not storage. Define the per-layer chain now (pitch correction first, then gate → EQ → pan → gain) so ARA-rendered audio feeds the same downstream path as 2B output. Persist ARA archives keyed by layer id + source-audio hash, so re-recording a layer automatically invalidates stale Melodyne state.

## E. Verified claims (no change needed)

- **JUCE host-side ARA support exists**: `ARAHostModel` classes (AudioSource, MusicalContext, PlaybackRegion, ManagedARAHandle) are in `juce_audio_processors`, and AudioPluginHost includes ARA hosting files. The plan's approach is viable; note the docs are sparse, so also study the ARA SDK's own host examples during the spike.
- **VST3 SDK licensing**: for a private, non-distributed app, the GPLv3 side of Steinberg's dual license imposes no practical constraint (obligations trigger on distribution). Re-check only if the app is ever shipped to others.
- The layered-recording model, latency-calibration approach, 2B-before-2A ordering, and out-of-scope list are all sound.
