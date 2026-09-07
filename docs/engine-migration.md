# The C++ engine migration

2026-08-24. Decision: everything functional moves to C++ ("the engine"); C# remains
only for the WinForms GUI. Mono then carries windows and dialogs, not emulation.

## Why

- **Testing.** The witness gate today needs Mono + WinForms + Xvfb to test *emulation
  determinism*. An engine is testable as a binary: no display, no runtime, fast CI.
- **Linkability.** Solvers and bots (jaffarPlus) can link the engine as a library
  instead of puppeteering a GUI process.
- **Performance where it counts.** Not realtime play (cores are already native) but
  the multiplied paths: unthrottled runs, savestate churn, scripting. Lua moves from
  reflection-based NLua to the real thing.
- **One toolchain.** Engine, miniBox, and cores all build from the same meson tree.

## The boundary

The engine owns exactly **what a headless movie run touches**: waterbox host
management, core/package loading, firmware resolution, rom identification,
input log, movie record/playback/rerecord, savestates, memory domains, AV dumping,
Lua. The GUI keeps windows, rendering, input devices, and config presentation.

Rules, in the same spirit as the waterbox boundary:

1. **The ABI is a frozen spec.** Flat C ABI in `source/engine/include/chimera/engine.h`,
   prefix `ce_`, versioned by `CE_ABI_VERSION` / `ce_abi_version()`. Additive changes
   only; anything else bumps the version and the C# side checks it at load.
2. **Bulk data crosses as buffers.** Never a call per byte/cell/frame. Borrowed
   pointers are documented with their invalidation rule at the declaration.
3. **The engine owns movie-correctness state.** The GUI cannot desync what it cannot
   reach.
4. **Formats do not change.** Movies, packages, configs written before and
   after a component moves are byte-compatible. Where the old code had a quirk, the
   engine reproduces the quirk and a comment names it.

## The method (per component)

Move one component at a time, smallest verifiable seam first. For each: implement in
`source/engine/` with meson-run C++ tests carrying byte-exact fixtures; wire the C# call
sites through the ABI (NativeInvoke, same pattern as libminiboxhost); keep a C# parity
test comparing engine behaviour against the legacy behaviour where the witness gate
does not reach; witness gate + full test suite green before the next component starts.
The C# implementation is deleted in the same change that wires its replacement - no
long-lived dual paths.

## Order

1. **Movie input log** (pilot: proves build, ABI, dlopen, parity mechanics) - DONE
2. **Movie text lumps** (Header.txt, Comments.txt, subtitle lines) - DONE
   (the zip container itself is shared with savestates and moves with them)
3. **The zip-of-lumps container** (ZipStateSaver/Loader; miniz vendored in
   extern/miniz, zstd loaded from beside the engine) - DONE. The engine owns
   the format; C# keeps the temp-file/backup plumbing and hands buffers
   across. What a savestate CONTAINS (SavestateFile's lump set, the greenzone
   Zwinder ring) is session policy and moves with step 5.
4. **Rom identification + firmware** - DONE. The engine owns SHA1 identity
   hashing (`ce_sha1_hex`), the firmware verdict, and the canonical
   "<id>=<sha1>" movie line. Config, registry and the filesystem stay C#.
   (This step also moved the .gameBundle format, with cJSON vendored in
   extern/cjson; bundles were removed wholesale on 2026-08-25 - see
   design-principles, "Storing progress: cleared to the ground" - and cJSON
   stays for waterbox.config.)
5. Core/package loading + session (the frame loop; the witness gate moves onto the
   engine binary here, dropping Mono/Xvfb from Level B)
   - 5a **package container** - DONE. `ce_package_*`: what makes a path a
     package (zip or dev directory), its identity hash, entry access; the
     engine's first (read-only) filesystem capability, which the native
     session needs anyway. A corrupt zip stays listable with its error; an
     ordinary zip stays quietly invisible. waterbox.config's MEANING still
     parses in C# and moves with 5b.
   - 5b **native session, first half** - DONE. `ce_session_*`: package +
     waterbox.config (parsed engine-side) + rom + effective settings +
     firmware in; frame advance (buttons/axes/video/audio/lag), whole-machine
     save/load state, memory domains out. libminiboxhost is loaded from
     beside the engine; on Windows guest calls bridge through the host's
     departN trampolines (host_dyn.cpp, the WaterboxAbiShim transliterated).
     `chimera-run` replays movies headlessly, and the witness gate's new
     Level E proves it byte-identical to the Level A goldens - no Mono, no
     Xvfb, no frontend.
   - 5b **second half: WaterboxCore rewired onto ce_session** - DONE. The C#
     adapter is now a thin frontend shell: the machine (host, mounts, Init,
     frame loop, savestates, domains, live settings) is the engine's session,
     the SAME one chimera-run drives - LibMiniBoxHost.cs and WaterboxAbiShim.cs
     are deleted. The optional tooling groups still reach the guest through
     the session's TRANSITIONAL guest-proc bridge
     (ce_session_guest_proc) until they migrate as their own components.
   - 5b **tooling into the session** - DONE. The four tooling groups
     (surfaces, registers, buses, trace) are probed and driven by the session
     (`ce_session_surface_*`/`_register_*`/`_bus_*`/`_trace_*`); the
     transitional guest-proc bridge is gone. (This step also moved the
     persistent-data channel, removed wholesale on 2026-08-25.) The session owns the trace
     flag and re-asserts it inside load_state (movie-correctness state
     belongs to the engine). Known gap: the synth core exports none of the
     optional groups, so the gate witnesses only the groups-absent path -
     the groups-present path rides the 1:1 transliteration and real cores.
   - 5b **movie + greenzone in the session** - DONE (engine side). The
     session carries the movie itself: the input log lives in the engine,
     entries are parsed and generated there (the Bk2 entry structure -
     groups by player, axes before buttons, positional parse; mnemonic
     CHARACTERS stay the frontend's per-system vocabulary, passed in as
     data), the frame position is session state, play flips to finished at
     the log's end, and recording over existing entries truncates (the
     rerecord). The greenzone is a budget-bounded state history with an
     always-kept anchor: seek = restore nearest + replay, invalidate drops
     what an input edit falsified. chimera-run --seek and four new gate
     checks (play to end, seek back, invalidate, replay - dumps must not
     change) witness it. The C# MovieSession/TAStudio still run their own
     movie and Zwinder greenzone - rewiring them onto the session is next.
   - 5b **the entry format, witnessed** - DONE. The two gaps the step above
     left are closed. The Bk2 entry layout moved out of the session into
     `source/engine/source/movie_entry.{hpp,cpp}` as a pure function of a
     controller's declaration, so `test_movie_entry` can exercise it against
     controllers no core in the tree declares - AXES above all (padding,
     sign, non-zero neutral, axes-before-buttons, multi-player grouping, the
     round trip, and every refusal), plus the empty console group C# emits
     and the hand-written movie fixtures omit. Record mode gained an
     end-to-end witness: `chimera-run --record` drives RECORD with a movie as
     an input source only, the session generates its own log, and the gate
     checks both that the recording drove the machine to the goldens and that
     replaying the file it wrote lands there too (`E:*:record`, four more
     checks). One new ABI call carries it, `ce_session_movie_entry_decode` -
     which the frontend needs anyway to display input once MovieSession
     moves onto the session.
   - 5b **movie log storage unified** - DONE. IStringLog has ONE
     implementation, EngineStringLog, a list-shaped view over the engine's
     ce_movie_log (which grew set/insert/remove_range/assign for the
     editors): every movie's log - bk2, tasproj, branches, the undo
     history's snapshots - is engine-side data, and movie file I/O runs
     engine-to-engine with no marshalling round trip. ListStringLog,
     StreamStringLog and the MoviesOnDisk config option are deleted (the
     disk-backed variant was a RAM-saving relic; engine storage is compact).
   - remaining in 5: the session-driven frame loop - MovieSession's
     latch/record path and TAStudio's state history onto ce_session's
     movie + greenzone. This is one entangled surgery (the input chain,
     LoadState-position sync, IStateManager) and comes as its own step.
6. Memory domains + tooling services
7. Lua (real Lua replaces NLua; script API preserved)
8. AV dumping

The GUI is untouched throughout; at the end it is a WinForms shell over `ce_` calls,
and porting it (or not) is a separate, optional decision.
