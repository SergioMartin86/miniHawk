# Porting a core

What one person learned building fifteen of them. This is the practical
companion to [design-principles.md](design-principles.md) (why Chimera is the
way it is) and [waterbox-analysis.md](waterbox-analysis.md) (what the sandbox
is): the order the work actually goes in, and the traps that cost days.

Per-core reasoning lives in each core's own `docs/PLAN.md`. Read the closest
one before starting a new port - a Symbian port and a PS2 port share more than
you would think.

## What you are building

Two builds of the same machine, and the claim that they are the same machine:

- **The native reference** - the emulator and a harness, built for the host, no
  sandbox. It is where you debug: gdb works, printf works, the GPU works.
- **The core** - the same emulator built for the miniBox guest toolchain and
  linked into `core.wbx`, plus the package that declares it.

Everything the gate does is comparing those two. If they agree instruction for
instruction on a real game, the core is deterministic in the only sense that
matters, because the sandbox is the thing that makes it reproducible and the
reference is the thing that says the sandbox did not change the answer.

## The shape of a core repository

    extern/<upstream>        submodule, pinned, never edited in place
    patches/                 numbered series, applied by waterbox/apply-patches.sh
    waterbox/                the adapter, the builds, the gate
      machine.{h,cpp}        the emulator, its clock, and the stepping loop
      wbx-entry.cpp          the guest ABI: Init, FrameAdvance, GetVideoBgra...
      run-native.cpp         the reference harness (flags for every diagnostic)
      run-wbx.c              the host driver that runs core.wbx like the engine does
      run-gate.sh            the legs
      waterbox.config        what the frontend is told about this machine
      file_slots.json        what the project wizard asks for
      package-licenses.json  what the bundle's LICENSES.md is computed from
    docs/PLAN.md             milestones, and the reasoning behind every decision

**Patches are all-or-nothing.** A clean upstream tree gets the whole series; a
dirty one is taken to already have it. Do not try to detect them one by one -
`git apply --reverse --check` on one patch fails the moment a neighbour moves
its lines. Every patch is a build option or a hook, never a deletion.

**Regenerating one patch in the middle of a series is a trap.** `git diff` on a
file two patches touch produces a patch containing both. Snapshot the files
before your edit and `diff -u` against that instead.

**Check the submodule is really there.** `git -C extern/<upstream> status` in an
empty directory answers for the repository ABOVE it, so a clone made without
`--recursive` will happily apply the whole series to your own working tree and
say nothing. Compare `rev-parse --show-toplevel` with the path you meant.

## The order it actually goes in

0. **Skeleton and a native build.** Get upstream building headless with no
   frontend, no host devices, no window system. Usually two or three patches
   (build options) and a stub for whatever backend picks a platform at compile
   time.
1. **Virtual time.** Find the one place the emulator asks the wall what time it
   is and make it a seam. Time must be bought with executed instructions, and
   idle must jump to the next scheduled deadline. The gate leg is: stall the
   host for 300 ms and prove the machine did not notice - and prove the check
   has teeth by running the same thing on the host clock and seeing it fail.
2. **The guest build.** The emulator through the musl guest toolchain. Expect a
   week of link errors and the traps below.
3. **The machine's filesystem.** Almost every emulator wants directories on a
   host. A core has files mounted by name and nothing else. Serving the
   machine's drives out of its own memory is usually less work than teaching
   upstream about a virtual filesystem, and it makes what a game writes part of
   the savestate for free.
4. **The picture.** See below - there are three ways and they are not equal.
5. **Input and audio.** Levels, not events: the host says what is held this
   frame. Audio is pulled at the end of the frame; pulling is not passive, it
   runs the machine's own callbacks, so the reference must pull too or the two
   flavors diverge.
6. **Savestates.** Usually free if everything else was done right.
7. **The package.** Declarations, keybinds, licences.
8. **Speed, if it needs any.** Measure before believing - see below.

## The guest toolchain, and what it refuses

- **No thread-local storage.** `check-wbx.sh` fails a core with TLS symbols or
  `%fs` accesses, and it is right to: a TLS write with no thread pointer does
  not fault, it lands wherever `%fs` happens to point. Build with
  `-Dthread_local= -D_Thread_local= -D__thread=` and fix what breaks.
- **One thread.** The machine's scheduler is the only scheduler. Thread pools,
  lazy background workers and "spawn a thread to do the IO" all have to go or
  become synchronous.
- **No timed waits.** A futex wait with a timeout parks the only thread and
  nothing ever wakes it: miniBox prints `all threads fell asleep. states: t1=W`
  and traps. Any queue an owner drives must have a non-blocking take. This one
  cost an afternoon on a one-microsecond `pop(1)`.
- **Weak symbols resolve to zero.** A library that declares
  `pthread_mutexattr_init` weak as a build workaround gets address zero in a
  static guest link and calls it. Force them with `-Wl,-u,<name>`.
- **musl's `open()` asks the kernel for `O_CLOEXEC` with a second raw syscall**
  that no libc-level definition can shadow. If the sandbox does not implement
  it, wrap `open` and strip the flag before the syscall.
- **Syscall gaps are normal.** `closefrom`, `clock_getres`, `getcwd`, `mkdir`,
  `lstat`, `fcntl`, `sched_getaffinity`... each shows up as an ENOSYS crash the
  first time a new upstream reaches for it. Add them to a `guest-syscalls.cpp`
  as they appear.
- **Arena sizing: grow the MMAP arena, not sbrk.** musl sends every large
  allocation to mmap, so that is the one that runs out; miniBox prints
  `sbrk heap exhausted` when the heap really is the problem, and the ABSENCE of
  that message with an out-of-memory failure means mmap. The layout is
  `{sbrk, sealed, invisible, plain, mmap}` in `waterbox.config`.

## Debugging inside the sandbox

- A guest fault prints a `rip=0x36f0...`. The guest links at `0x36f00000000`;
  `addr2line -f -C -e core.wbx <rip>` names the function.
- gdb works on the host driver, but miniBox uses SIGSEGV for its own memory
  tricks: `handle SIGSEGV nostop noprint pass` first, or you will "catch" the
  sandbox working correctly.
- A guest that hangs rather than faults is usually the timed-wait trap above.
- Build the reference with the same change and reproduce there first. Most
  "sandbox bugs" are ordinary bugs that the sandbox merely made visible.

## Three ways to get a picture, in order of preference

1. **Read the machine's own memory.** If the emulated machine has a
   framebuffer the guest paints (any direct-screen-access console, most
   handhelds), read it and convert. No graphics driver, no GPU, and the
   sandbox's picture is the reference's byte for byte by construction.
2. **Compile an OpenGL into the core.** Mesa's OSMesa front end with the
   **softpipe** driver and LLVM off: plain C, no JIT, no dispatch on host CPU
   features, so every machine draws the same picture. This is what a core needs
   when the emulator composes on the GPU. It costs a few MB of package and is
   slower than a hardware path, and it is the only way a window-server-style
   renderer works in a sandbox at all. Five small patches to Mesa are needed
   (no thread pointer, one CPU, no x86-64 dispatch stubs); see
   the eka2l1 core's `waterbox/setup-mesa.sh`, which pins the tarball by
   SHA256 and carries the patches, for a recipe to copy.
3. **The GPU bridge** - the guest's GL calls leave the sandbox onto a real
   driver. Fast, and [gpu-bridge.md](gpu-bridge.md) explains it, but note what
   it costs: **a savestate made with GPU state alive is good only in the
   session that made it**. For a TAS core that is a serious constraint, not a
   detail.

Whichever you choose: **prove the renderer does not perturb the machine.** Run
the same game with the renderer and without and compare the instruction count.
If it changes, the picture is part of the machine and the core is not portable.

## Savestates

In a waterbox they are arena snapshots, so they usually work the day the core
works. Two legs prove it and both are worth having:

- **Rerecord**: save and load before EVERY frame; the run must be identical to
  one that did not. Anything of the machine living outside the arena shows up
  here.
- **Session**: save in one process, load in another, and land where the
  uninterrupted run did. This is what a movie asks of a core, and it is the leg
  a core holding a graphics context fails.

Things that break them: host pointers stored in guest memory (some emulators do
this deliberately - it is fine inside the arena, but it makes raw memory differ
between flavors), GPU objects, and anything the core caches across `Init`.

## The gate

One command, and every leg says what it compared. Advice earned the hard way:

- **Name the test content.** A leg that takes "the first `.rar` in the folder"
  changes meaning the day the user adds a game, and three legs go red for
  reasons that have nothing to do with the commit. Name the game, fall back to
  a glob, and print which one ran.
- **A game that does not work is a finding about that game**, not a broken
  gate. Keep the gate on content that works and record the rest in PLAN.md.
- **Compare the machine, not just the picture.** Instruction counts catch
  divergence long before pixels do.
- **Upstream's own test suite is a leg.** So is its differential CPU harness if
  it has one: it tests the interpreter you actually ship.

## What the package declares

- **`waterbox.config`** - the machine: name, system id, video (buffer capacity
  and the live size the core reports per frame), audio, vsync as a rational,
  the memory layout, the button list, settings, firmware.
- **`file_slots.json`** - what the wizard asks for. Think about what the
  project IS: usually the game, with the machine's ROM as firmware, because one
  ROM serves every game.
- **Firmware** is declared by the core, resolved and remembered by the
  frontend, and mounted in the guest under its ID - `fopen("sym.rom")`. A
  firmware needed only in some cases carries `requiredWhen`
  (`{"slot": "game", "extension": "blz"}`, a setting's value, `any`/`all`/`not`).
  Optional firmware does not exist; if it is not always needed, spell the
  condition.
- **`package-licenses.json` is not optional.** Every component, its terms, its
  source link, and its licence text travel with the package; the bundle's
  LICENSES.md is computed from them, and a package that declares nothing stops
  the build. If you compile someone's code in - a whole Mesa, say - declare it.
- Settings that shape the machine are part of the reproduction contract. There
  is no such thing as a cosmetic setting here.
- Two places to keep in step: the vsync rate in `waterbox.config` and whatever
  the guest returns from `GetVsyncNumerator`/`Denominator`.

## Optional tooling the frontend will use if you export it

Probed once after `Init`; absent exports simply mean the tool is not offered.

- **Memory domains** - a pointer and a size, published once. If the machine's
  memory is not a block (paged, per-chunk, or not allocated until the game
  runs), export a **bus** instead: `GetBusCount/Name/Size/Writable`,
  `PeekBus`, `PokeBus`, resolved per access. Cache one page of translation and
  a RAM search costs nothing.
- Registers, trace, core-rendered surfaces, save-data export, turbo
  (`SetRenderingEnabled`).

## Publishing it

Chimera ships no cores and builds none: a core is a repository that publishes
itself, and the frontend downloads it (docs/core-manager.md). Joining the
official set is therefore four things:

1. Push the core repository (`ToolAssisted-run/chimera-core-<name>`, branch
   `main`).
2. Give it a `chimera.yml` that gates the package on a public runner and
   uploads it as an artifact named `<name>-${{ github.sha }}`. **This is the
   part to think about**, and it is a content problem before it is a CI
   problem: a gate needing a BIOS or a commercial rom cannot run there, so what
   CI proves has to be designed around what may be distributed. The fifteen
   split three ways, and it is worth checking which one you are in before
   assuming the worst:

   - **the machine is its own content** - DOSBox-X boots to a DOS prompt with
     no disk and its gate builds the disks it needs, so the whole gate runs;
   - **upstream ships a test suite** - Ruffle's own `tests/tests/swfs` are
     vendored and free, so the whole gate runs against those;
   - **nothing may be distributed** - an Xbox has no HLE bios, so xemu's CI
     proves both flavors build and the guest is sandbox-clean, and nothing
     more. That is a small claim, honestly made, and still catches most of what
     breaks.

   Whichever it is, run **Chimera's contract tests against the package** with
   `CHIMERA_CORES_DIR` pointing at it: readable, ABI supported, becomes a
   factory, binds only declared buttons, stamps a version. It costs seconds and
   needs no content at all.
3. Add the `publish` job and a daily `schedule:` trigger, which is three lines
   calling `ToolAssisted-run/chimera/.github/workflows/publish-core.yml@main` -
   see any wired core, or docs/core-manager.md.
4. A row in Chimera's `official-cores.json` and in the README's core table.

**The publish job runs `./waterbox/build-package.sh -r <chimera>` in a fresh
recursive checkout, and nothing else.** Two consequences, both of which have
cost a red release:

- **Everything the package needs, that script must build.** A dependency you
  built by hand on your machine a month ago does not exist on a runner. The PS3
  core's LLVM was like this: the package failed in four tenths of a second with
  "Can't find LLVM libraries", on every release, for weeks.
- **No absolute paths anywhere.** A guest toolchain file with
  `-I/home/<you>/chimera-core-<name>/waterbox` in it works on your machine -
  from whichever clone that path names, not the one being built - and nowhere
  else. Use `${CMAKE_CURRENT_LIST_DIR}` and friends. Grep for your home
  directory before pushing.
- **Do not guess at `$HOME`.** A build step that falls back to
  `$HOME/chimera/extern/tools/...` for the guest toolchain works on the machine
  that wrote the fallback and nowhere else; on a runner `$HOME` is not the
  checkout. Pass the miniBox path down from `build-package.sh` and stop with a
  message if the toolchain is not there.
- **The bundle sends each core's stdout to `/dev/null`.** cmake and autoconf
  write their errors there, so a build that fails for a stdout reason fails
  SILENTLY - a step that takes four tenths of a second and prints nothing is
  this, every time. Reproduce with
  `env HOME=/tmp/empty ./waterbox/build-package.sh -r <chimera> > /dev/null`.
- If a dependency takes hours (an LLVM, a Mesa), give it a cache key of its own
  in `.github/workflows/release.yml`. The broad core cache is keyed on every
  core's pin, so bumping any core throws it away.
