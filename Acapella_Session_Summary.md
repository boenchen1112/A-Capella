# Acapella Rebuild — Session Summary

> Consolidated record of everything decided/researched so far, organized as a briefing you can hand to Fable 5 for its own advice or concerns before work starts. Source files referenced below already live in `D:\VS Code\Acapella`.

## 1. What This Project Is

Rebuilding the core features of Mixcord's **Acapella** app (a mobile app for recording layered, synchronized music-video collages) as a **private, local Windows app**, building on an existing app that already does audio-video sync and slight FX.

## 2. Research Findings on the Original Acapella

- **Core mechanic:** sequential layered recording, not simultaneous multi-mic capture. Record layer 1, play it back as a guide track while recording layer 2 on top, repeat up to ~9–10 parts, then arrange into a grid and fine-tune sync/volume.
- **Latency handling:** Acapella's own docs point to a procedural fix (use wired headphones/mic), implying a fixed/heuristic latency offset rather than true measured calibration — flagged as the single biggest opportunity to do better in a desktop rebuild (loopback measurement + cross-correlation).
- **FX:** per-track volume, pan, EQ, noise gate, a "processor" (compressor-style), built-in metronome. Applied non-destructively — final mix adjustments happen after all layers are recorded.
- **Video:** multi-frame grid/collage layouts (2 up to ~9–10 cells), photos can mix in with video layers, frame art/watermark overlay.
- **Export:** free tier standard resolution; Pro tier HD video + lossless audio, longer recordings.
- **Collaboration** (link-sharing so a project can be handed to another contributor) exists in the original but is **explicitly out of scope** for this rebuild.

## 3. Confirmed Scope for the Rebuild

| Decision | Value |
|---|---|
| Collaboration | None — solo, local-only |
| Layer 1 source | Recorded live **or** uploaded from an existing audio/video file |
| Metronome | Present, user-toggleable on/off |
| Max layers | 4 for v1 (config value, not hardcoded — raise later) |
| FX | EQ, pan, noise gate, metronome, pitch correction |
| Pitch correction | **Melodyne via VST3/ARA hosting** as the target, with a **native fallback** (Rubber Band + pitch tracking) built first as the safety net |
| Grid layout | Fixed 2x2 only |
| Output | MP4 (H.264 + AAC) |
| Platform | Native Windows, C#/.NET (WPF/WinUI) + a native C++/JUCE module for VST3/ARA hosting |

## 4. Files Already Created in the Project Folder

- **`CLAUDE.md`** — governs how Claude Code should behave autonomously on this project:
  - Prime directive: working end-to-end beats polished. No refactor loops, no gold-plating, no acting on `// TODO(polish):` markers unless asked.
  - "Definition of done": a phase is complete the instant its acceptance criteria pass once — no re-verification, no extra edge-case handling.
  - Four pause conditions only: (1) destructive/irreversible actions, (2) real scope changes vs. this plan, (3) input only Daniel can provide (Melodyne tier/ARA check, subjective audio/video quality judgment), (4) otherwise, keep going and report at phase boundaries.
  - Git discipline (frequent small commits, never force-push without asking) and a testing philosophy (automate objective checks, reserve subjective quality judgment for human check-ins).
- **`Acapella_Build_Plan_v3_MelodyneVST.md`** — the phased technical plan:
  - **Phase 0** — Baseline audit of the existing sync+FX code (what's reusable vs. what needs replacing).
  - **Phase 1** — Layer 1 ingestion (record/upload), latency calibration (loopback test), metronome, guide-track recording for layers 2–4, manual sync fine-tune.
  - **Phase 2A** — Melodyne hosting via a JUCE-based native module (VST3 + ARA), C-ABI bridge into C#/.NET via P/Invoke. Flagged as the highest-risk phase in the project.
  - **Phase 2B** — Native pitch-correction fallback (Rubber Band + pitch tracking). Recommended to build **before** 2A so the app works end-to-end without depending on Melodyne.
  - **Phase 3** — Multi-track mixing engine (EQ, pan, noise gate, pitch backend selection).
  - **Phase 4** — 2x2 grid compositing (with placeholder visuals for audio-only layers).
  - **Phase 5** — Project persistence (save/reload everything, including Melodyne's ARA state if used).
  - **Phase 6** — MP4 export pipeline (mixdown + composite + mux via FFmpeg).
  - Suggested order: 0 → 1 → 2B → 3 → 4 → 5 → 6 → 2A (Melodyne last, as an enhancement layer once the rest already works).
- Two earlier draft files (a research docx and an original v2 build plan without the Melodyne path) were superseded by the above and are likely no longer present in the folder — worth a quick check before Fable reads the directory, so it doesn't get confused by stale drafts.

## 5. Key Concerns Already Flagged in This Conversation

- **Melodyne cannot be bundled or redistributed.** The app can only host a copy the user already owns and has licensed (confirmed: already installed/used via FL Studio, so it should be discoverable in the shared VST3 folder at `C:\Program Files\Common Files\VST3` without a fresh install).
- **Melodyne tier/ARA support is unverified.** Bundled/OEM tiers sometimes have reduced ARA note-editing support versus full Editor/Studio. This needs a runtime check once Phase 2A starts, not an assumption now.
- **ARA hosting is genuinely the hardest part of this whole project** — harder than everything else combined. Recommendation stands: build Phase 2B (native fallback) first, timebox an initial spike on Phase 2A before committing further.
- **Perceptual quality (does it sound/look right) cannot be automated**, regardless of how autonomous the workflow is. This is intentionally carved out as Pause Rule 3 in `CLAUDE.md` — batched into short check-ins at phase boundaries, not blocking every step.
- **VST3 SDK licensing/distribution terms from Steinberg may have changed** since this was researched — worth a direct check on steinberg.net before Phase 2A rather than assuming it's a plain unrestricted `git clone`.
- **This Cowork session cannot build or run the app** — no real audio/video hardware, no .NET/C++ toolchain. All actual development has to happen via Claude Code CLI directly on the local Windows machine.

## 6. Environment / Tooling Status

**Already installed:**
- Git
- Visual Studio 2022, "Desktop development with C++" workload, default components (MSVC Build Tools, Windows 11 SDK, CMake tools for Windows, vcpkg, AddressSanitizer, etc.)
- Melodyne (via FL Studio's plugin install — tier/ARA capability not yet verified)

**Recommended addition to the VS installer (not yet confirmed done):** check **C++/CLI support (Latest MSVC)** — not strictly required for the planned P/Invoke bridging approach, but cheap to add now as a fallback option before Phase 2A rather than reinstalling later.

**Still to install (all scriptable via PowerShell/winget):**
- .NET SDK — `winget install Microsoft.DotNet.SDK.8`
- Standalone CMake — `winget install Kitware.CMake`
- FFmpeg — `winget install Gyan.FFmpeg`
- JUCE + VST3 SDK — clone via git, deferred until Phase 2A

## 7. Recommended Operating Workflow (Claude Code CLI)

- Two separate control layers: Claude Code's built-in **permission modes** (`default`/`acceptEdits`/`plan`/`auto`/`dontAsk`/`bypassPermissions`) control whether tool calls need literal approval; `CLAUDE.md`'s four rules control when the agent itself decides to proactively pause. Both need to be configured for a smooth autonomous run.
- Recommended: `.claude/settings.json` with `defaultMode: "acceptEdits"` (or `"auto"` if available on the account/model) plus pre-approved allow-rules for recurring commands (`dotnet build`, `dotnet test`, `cmake`, common `git` commands).
- Recommended sequence: one small supervised interactive run for Phase 0 (sanity-check that Claude Code is reading the plan correctly) → hand off Phases 1 onward as a **background session** (`claude --bg "..."`) → monitor via **Agent View** (`claude agents`, or press `←` on an empty prompt to background/open the dashboard) → step in only when a row shows "needs input" or at scheduled phase-boundary check-ins.
- Phase 2A (Melodyne) recommended as its **own separate** background session rather than folded into the main unattended run, given its risk level.

## 8. Open Items Raised With Fable 5 — Now Resolved

Fable 5 reviewed `CLAUDE.md` and the build plan directly (see `reviews/Fable5_Plan_Review.md`) and answered all five, plus caught real bugs. Both files were revised to v4 (build plan filename is `Acapella_Build_Plan.md`, not `_v3_MelodyneVST` — that suffix never actually landed on disk; see item 3 below) and are now consistent with the answers here.

1. **Defer 2A?** Yes, confirmed — and more strongly than expected: Melodyne's ARA integration exposes no host-side "apply correction" call, so Phase 2A can *only* ever be a manual, editor-driven workflow. Phase 2B (native Rubber Band + pitch tracking) is therefore the app's only automatic-correction path, not just a fallback. Added: a cheap 30-minute de-risk check in Phase 0 (confirm Melodyne's `.vst3` and tier, without attempting integration).
2. **C-ABI/P-Invoke vs. C++/CLI?** 