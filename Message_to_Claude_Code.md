Before continuing v6 Phase P3, there's an unresolved crash from the end of the last session that primer.md doesn't mention — it happened after you reported the bridge built/tested/committed at 9553d56, so you may not have seen it.

**What happened:** running the test suite (likely during the 100x create/process/release stress test's process teardown, after all 96 tests had already reported green) triggered a Windows debug CRT assertion dialog: `_CrtIsValidHeapPointer(block)` failed in `debug_heap.cpp`. That's heap corruption, not a test failure — and it's a modal GUI dialog, which means it silently hung whatever shell call was waiting on `dotnet test`, since a non-interactive session can't click a dialog. I ended up having to Abort the process manually to get unstuck, which is likely what ended the session.

**Two things need fixing before P3 integration continues, in this order:**

1. **Silence the blocking dialog first**, independent of root cause — otherwise the next heap bug (in this code or anything touching the native bridge) hangs a session the same way again with no visibility into why. Add `_CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE)` and `_CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR)` (or equivalent) near `AcapellaHostNative.dll`'s entry point so assertion failures go to stderr and fail the test process loudly instead of popping a modal window.

2. **Then root-cause the actual corruption.** Nothing failed logically — all 96 tests passed — so this is likely a buffer/lifetime bug that only manifests on teardown. Ranked by likelihood given what P3a actually touches:
   - A `GetState`-style caller-allocated buffer write exceeding the capacity the C# side allocated (check every native function that writes into a caller-supplied buffer actually clamps to the passed capacity, and that C# allocates exactly what `GetStateSize` reported, not a stale size).
   - Double-free or cross-boundary free in `ReleaseInstance` — check nothing frees a JUCE `AudioPluginInstance`/editor through two separate paths, and nothing frees memory a plugin's own DLL allocated using your host's `delete`.
   - Thread-affinity violation: JUCE `AudioProcessor` instances generally expect create/destroy on the same thread — check whether the 100x stress test's release calls happen on the same thread as creation.
   - Turn on `_CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_CHECK_ALWAYS_DF)` temporarily to catch the corrupting write at the point it happens instead of at exit — post-hoc detection is lying about *where* this is, not just *that* it is.

Add a regression test once found (mirror the existing 100x create/process/release test, but assert clean process exit / no CRT report, not just no exceptions during the loop body — that's exactly the gap that let this through green).

Once both are done, resume the plan as written: `HostedPluginSampleProvider` wrapping `ProcessBlock`, mandatory latency compensation via `GetLatencySamples`, `GetTailSeconds` clamped before any duration math (confirmed `inf` for Pro-R 2 and Pro-Q 4 in probe 2), per v6's corrected chain order.
