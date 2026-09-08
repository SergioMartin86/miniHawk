# The GPU bridge

A waterboxed core draws on the CPU. That is the point: a picture decided
entirely by code Chimera compiles is a picture every machine agrees on, which
is what a movie needs. For most consoles it is also fast enough.

For a PlayStation 2 it is not. PCSX2's own software rasteriser is quick but
skips what the GL renderer does; PCSX2's GL renderer is the one upstream tests,
and running it against a Mesa softpipe compiled into the sandbox costs about
eleven times the software rasteriser. Accurate and unusable.

The bridge is the way out: the core's OpenGL calls leave the sandbox and land
on a real context on the machine Chimera is running on.

## What it is

The renderer stays in the guest, unmodified. It reaches OpenGL through glad,
glad fills its table from the bridge's `GetProcAddress`, and every entry point
it gets back is a generated wrapper that packs its arguments into a struct and
hands the address to the one callback a guest may call out through. The host
half is in the same address space, so it reads those arguments - and the vertex
data and textures they point at - in place. No copying, no marshalling.

Both halves are generated from one list of entry points in miniBox
(`extern/chimera-common-minibox/source/gl/`). An opcode is an index into
that master list, so every core and this engine mean the same thing by the same
number. A guest asks how long the host's list is and declines a host that knows
fewer entry points than it was built against.

One rule decides everything about safety: **no host pointer is ever handed to
the guest.** A mapped buffer is a pointer into the driver, and a sandbox is
right to refuse it. PCSX2 already had two streaming paths that keep their own
CPU buffer, for drivers where mapping is slow; the bridged build takes one of
those (its patch 0015), so the bridge is a plain pass-through.

## Determinism, and what a movie says

A core drawn this way is **not deterministic**. The GPU is outside the sandbox
and outside the savestate, it differs between machines, and drivers differ
between versions of themselves.

Chimera does not hide that:

- `ce_session_deterministic` returns 0 for a session a GPU drew.
- A movie recorded on one writes a `GpuRenderer` header naming the driver.

What is NOT affected is the machine. The emulated console's memory does not
depend on how its picture was rasterised: EE RAM, IOP RAM, the scratchpad, both
vector units, the audio and the lag count are byte-identical across the
software rasteriser, the softpipe and the bridge. Only the picture differs -
which is exactly what a desync would be made of if a game ever read its own
rendered pixels back, and that case is untested.

Measured through the frontend, on a machine with no GPU at all (llvmpipe - so
this is CPU against CPU, and a real driver should do better):

| core | own rasteriser | softpipe in-guest | bridged |
|---|---|---|---|
| PCSX2, 900 frames of a disc | 37s | 62s | **36s** |
| Flycast, 400 frames of a disc | 26s | 47s | **16s** |

The machine's memory was byte-identical across all of them - EE RAM for PCSX2,
System RAM for Flycast.

## Which cores have it

| core | hardware renderer | why |
|---|---|---|
| PCSX2 | `opengl-hw` | its GL renderer already ran in the sandbox |
| Flycast | `opengl-hw` | the same |
| everything else | no | see below |

A core can only be bridged if its own OpenGL renderer already runs inside the
sandbox - the bridge answers GL calls, it does not create them. PPSSPP, for
instance, compiles only its SOFTWARE GPU backend (`GPU/Software`); its GLES
backend is 9,000 lines that have never been built here and expect a separate
render thread, which a sandbox does not have. Giving PPSSPP a hardware option
means porting that backend first, and that is a port, not a wiring job.

## Turning it on

Two things have to be true, and either may not be:

1. **The project asks.** The core declares a renderer whose value ends in
   `-hw` (PCSX2's is `opengl-hw`, shown in the wizard as "Hardware (OpenGL)"),
   and the project chose it. That suffix is the whole convention: the frontend
   recognises any core's hardware renderer without knowing the core.
2. **This build has a bridge, and a driver gives it a context.** `-Dgl_bridge`
   is on by default and needs EGL on Linux (`libegl-dev`) and `opengl32` on
   Windows.

When either fails the core draws the way it draws without a GPU - the
deterministic way - and the session simply does not claim otherwise. A run that
did get one says so on screen when it starts ("GPU: <driver> - this run is not
deterministic"), which is the only place a person can see WHICH driver drew.
There is a line on standard error either way:

    chimera gl: 4.5 (Core Profile) Mesa 25.2.8 on llvmpipe    <- a context
    chimera gl: no context (...); drawing in software         <- no context

`chimera-run --gpu` makes the same ask from the command line.

## The context is borrowed, never kept

"Current context" is one slot per thread, and the frontend draws its own
picture - the emulated screen, the OSD, everything - through that same slot.
The bridge takes it at the first GL call of a frame and gives back exactly what
was there when the frame ends (`ce_gl_release`, called by the engine after each
frame advance, after Init, and after a savestate load).

Keeping it is not merely rude, it is invisible. Chimera binds its context
through SDL, and `SDL_GL_MakeCurrent` short-circuits on SDL's own cache when it
believes its context is already current - which it does, because
`DisplayManager` deliberately never releases it ("workaround for slow context
switching on intel GPUs"). A raw `wglMakeCurrent` behind SDL's back therefore
makes the frontend go on drawing into the bridge's hidden 64x64 window for the
rest of the session. The symptom is a pitch black screen with working sound and
no OSD, and it is what Windows did the first time this ran against a real
display.

## Telling the two failures apart

A core drawn by a GPU and a core drawn by nobody fail differently, and on a
machine that is not here the difference is otherwise a rebuild away. Two
environment variables answer it without one:

- `CHIMERA_NO_GPU=1` refuses the bridge for the run whatever the project asked
  for. The engine says `chimera gl: refused by CHIMERA_NO_GPU`, the core draws
  in software or not at all, and everything else about the machine is
  unchanged.
- `CHIMERA_TRACE=<n>` prints the machine's own numbers on stderr every n
  frames: cumulative lag, thread count, whether it is running, machine time,
  the main-memory digest, the frame size, a checksum of the picture the engine
  received and how many pixels of it were not black. The per-core diagnostic
  runners print a line of the same shape, so a machine that misbehaves under
  the frontend and behaves under the runner can be diffed on one machine
  instead of guessed at from two. It also forwards whatever the machine wrote
  to its own TTY.

A black picture with a moving digest and a checksum that changes is a picture
lost above the engine; a black picture with a still digest is a machine that
stopped.

## What is not proven

- **Hardware.** Every measurement so far is on a machine with no GPU, against
  llvmpipe; a real driver should do better, and nobody has checked.
- **Windows.** The host half builds against WGL and makes a real context on a
  real driver (`4.6.0 NVIDIA 581.42`, GTX 1060, 2026-09-02); no frame drawn
  through it has been seen on a screen there yet.
- **Readback.** A game that reads rendered pixels into machine state would feed
  GPU output into the savestate, and that is where a desync stops being a
  possibility and becomes a certainty.

## A state is good in the session that made it, and no other

The renderer keeps its OpenGL objects - textures, programs, vertex arrays,
framebuffers - by the NAMES the driver handed it, and those names live in guest
memory. A savestate carries them faithfully, which is exactly the problem: load
one into a later session and every name refers to an object in a context that
no longer exists. The driver refuses each call (`GL_INVALID_VALUE`,
`GL_INVALID_OPERATION`) and the guest is never told, because a GL error raised
out here is invisible to it. The machine runs on - threads alive, memory
changing, audio playing - and draws nothing.

That is what reopening a saved PlayStation 3 project did: a black screen, and
then a crash. Measured on GTA San Andreas, one state saved at frame 150 and
loaded into a fresh process:

    [trace] frame 9 ... digest 0a684eb5... video 1920x1080 sum 0 lit 0
    [ce-gl!] op=649 raised 0x501   (glProgramUniform4f)
    [ce-gl!] op=115 raised 0x502   (glBindVertexArray)

36 GL errors a frame against 1.2 in the same run without the load, and every
failing call one that names an object.

### Noticing, and building them again

A renderer can be told which context its calls are landing on: `GL_OP_CONTEXT_ID`
answers with an identity the bridge mints for each SESSION that takes it (0 means
"cannot tell" - no bridge, or a host older than the question, and a guest must
read that as "assume nothing moved"). A renderer that stores that number beside
its objects can see, at the top of any frame, that the ground has moved - and
rebuild.

Per session, not per context, and the difference is the whole of issue #43. The
GL context is made once per process and shared by every session, because there
is only ever one machine; but the objects a renderer holds belong to the SESSION
that made them, not to the long-lived context. Close a project and open one
again without restarting Chimera and the second session inherits the first's
context - so an id minted per context would not move, the guest would keep the
dead session's object names, and the first draw would bind a gone framebuffer
and abort (the sandbox reports it as unimplemented syscall 200). Minting the id
afresh each time a session takes the bridge is what makes the greenzone reload
on reopen present a changed id, the same signal a fresh process already gets.

RPCS3 does. Its `on_init_thread` and `on_exit` were split into the half that
belongs to the CONTEXT (every GL object) and the half that belongs to the
MACHINE (`rsx::thread::on_exit` sets the thread's exit flag, and must never run
mid-game); the FIFO loop compares the two ids, and on a difference tears the
context's half down, builds it again and marks the whole pipeline dirty. The
objects come back from emulated memory, which is where they came from the first
time: the surface cache reads its render targets out of RSX memory, the texture
cache re-uploads, the program cache recompiles.

One upstream bug had to go first. `gl::glsl::shader::remove()` did not clear
`m_is_compiled`, and `compile()` returns early when it is set ("another thread
compiled this already") - so a shader object destroyed and made again linked
UNCOMPILED, and every uniform lookup on the resulting program failed forever.
Nobody upstream re-creates a shader in one process. A renderer rebuilding after
context loss does, twice, for the two overlay passes.

Measured on the same disc, a state saved at frame 150 and loaded in a fresh
process: the picture returns (1280x720, 20322 lit pixels, against `lit 0`
forever); GL errors are 137 in the rebuild frame and 3 a frame after it, which
is the rate an ordinary run has anyway.

The in-process reopen was measured on xemu (issue #43): a state saved at frame
150, the session freed, another opened over the same GL context, and the state
loaded into it - exactly what closing and reopening a project does. Before the
per-session id it aborted on the first draw (the framebuffer assert above);
after it, the id has moved, the renderer rebuilds, and the picture returns
(640x480, ~305k lit pixels) with 3 GL errors, the ordinary rate. Confirmed on
an NVIDIA GTX 1060 (581.42) and on llvmpipe, so it is the rebuild logic that
was never triggered rather than anything a particular driver does.

### What the frontend does with that

A core declares `video.gpuStatesSurviveTheContext` when its renderer does this,
and a project records it (`GpuStatesSurvive`) so that the answer is there when
the cached states are opened - by which time there is no core to ask. For a core
that says yes, the greenzone is kept like any other core's. For one that does
not, the states are session-local: the project writes none into its greenzone
and uses none from an older one, branch states included, and says so when it
opens. Rewind and branches within a session are untouched either
way - the objects are still there - and a project that loses its cache replays,
which is what an empty greenzone has always meant.

Every core that draws on the host's GPU now says yes. Saying it is not the same
as doing it, so `tests/gpu/run-reopen.sh` asks: open, play, save, close, open
again IN THE SAME PROCESS, load the state, keep playing. A fresh process per run
never asks the question, which is why this went unnoticed for so long.

What "keep playing" has to mean is the whole of it. Not crashing is not the
check: a renderer holding a dead context's objects comes back garbled, or
silent, or stuck on one frame, and every one of those passes "it did not crash
and something was lit". So the reopened run is compared against a run that never
stopped. Where a core exposes memory domains, RAM is the assertion - a hardware
renderer's picture may legitimately wobble, but a TAS is only a TAS because the
same inputs from the same state produce the same MACHINE - and the picture is
measured beside it. Where a core exposes none, as ruffle does, the picture and
the sound are all anyone outside can see, and they become the assertion instead.

Measured 2026-09-08, one game each, on llvmpipe AND on a GTX 1060 (NVIDIA
581.42) - both, because a renderer that mishandles a context change can easily
do it on one driver and not the other:

| core | reopens onto its own states |
|---|---|
| xemu | yes - RAM, audio and picture all identical |
| flycast | yes - identical |
| pcsx2 | yes - identical, once booted far enough to be drawing a game |
| ruffle | yes, once fixed - see below |
| dolphin | no, and it now says so |
| rpcs3 | not known: it will not boot on this machine, failing in its own audio overlay setup |

Both failures reproduced identically on the two drivers, so neither was a driver
quirk. Both were fixed in their own repos; what follows is what they were,
because they are the two shapes this bridge's contract can be got wrong in.

**Ruffle rebuilt its renderer but not its quality.** The stage's quality lives
in the player and is pushed down to the backend only when it is SET
(`Stage::set_quality` ends in `renderer.set_quality`), so a backend built during
a rebuild started at its own default - which for wgpu decides the MSAA sample
count. The movie carried on being drawn with anti-aliasing effectively off:
11.6% of pixels differed at the default `high`, 0% at `low`, where there is
nothing smoothed to lose. Reading the quality back off the player and setting it
again re-pushes it. Verified by rebuilding the core: identical at low, high and
best, and the reopened run now lands on the high-quality pixel count it used to
miss.

**Dolphin's rebuild crashed, and underneath that it desyncs.** The crash was a
null framebuffer: the rebuild cleared `m_current_framebuffer` and the machine
draws through `BPFunctions::SetScissorAndViewport` as soon as it runs, which
dereferences it. Binding the EFB afterwards fixes that, and dolphin now survives
a reopen and draws.

The picture it comes back with is still not the one that was saved, and it does
not recover: 62% of it differs 40 frames after the load, 99.7% after 240.

What differs is worth stating exactly, because it is less bad than "the machine
desynced" and more bad than "a wobble". Of the three memory domains only System
RAM ever differs - ARAM and the L1 cache stay identical throughout. Within it
the difference sits in a few megabyte-sized regions, in tens of thousands of
runs two to eight bytes apart with none longer than about 940: pixel scatter
between two renderings, not a structure diverging. It shrinks rather than
compounds, 807 KB at the first frame down to 318 KB by the 120th, and the one
word outside those regions - a pointer at `0x800000d8` - matches again later.
The GameCube's framebuffer lives in MEM1, which is why RAM sees a rendering
difference at all. So the game's own simulation looks intact and the PICTURE is
what comes back wrong: the same kind of fault ruffle had, an order of magnitude
larger.

Isolated by
building the core without each part of the rebuild in turn: it is
`g_texture_cache->Invalidate()`, and within it the single line
`m_textures_by_address.clear()`. With that one clear left out and everything
else intact the reopen is byte-identical, picture included. Ruled out the same
way - `FlushEFBCopies`, `TMEM::InvalidateAll`, the bounding box, the perf query,
`RecreateEFBFramebuffer`, the shader recompile.

The clear cannot simply go: those entries' textures name a context that is gone
once the process is, and EFB copies read back THROUGH them into RAM, so keeping
dead ones would trade a wrong picture for a quieter fault. What is wanted is a
texture cache that survives a context change with its contents - re-uploading
what came from RAM, preserving what came from rendering - and that is real work
in dolphin's texture cache. Until it exists dolphin declares
`gpuStatesSurviveTheContext: false`: a discarded greenzone costs replaying, and
a kept one means a resumed run renders differently, which is a run that encodes
differently.

Worth keeping in mind for whoever picks this up: the GL context is made once per
PROCESS, so an in-process reopen still has the first session's objects alive -
which is why disabling the rebuild entirely also reads as byte-identical here
and would not be a fix. The harness cannot tell that case apart from a genuine
cross-process reopen, and the id it keys on cannot either.

The isolation that found both is worth keeping in mind: reload into the session
that MADE the state, and reopen with no GPU bridge at all. When those two are
byte-identical and only the reopen-with-bridge differs, the rebuild is the only
thing left.

Dolphin declares that its states survive and has the patch that ought to make
them (`0020-chimera-the-renderer-rebuilds-its-gl-objects-when-the-context-is-gone`),
but the rebuild itself falls over. What was established:

- It is the GPU bridge. With no host context the same reopen works.
- It is not savestates. Reloading into the session that MADE the state works,
  with the bridge live and the OGL backend running.
- It is the rebuild path, which only runs when the context id has changed - the
  one thing that differs between those two cases.
- It dies on the very first frame after the load, at the first instruction of
  `AbstractGfx::ConvertFramebufferRectangle(const Rectangle&, const AbstractFramebuffer*)`,
  which immediately dereferences that pointer for `GetWidth()`. The fault reads
  address 0x3c, so the framebuffer is null.

`ChimeraRebuildGLObjects` ends by setting `m_current_framebuffer = nullptr`, and
something draws before anything binds one again. That is the thing to look at
first in `chimera-core-dolphin`; the fix belongs there and not here, since the
engine's side of the contract - mint an id per session, answer `GL_OP_CONTEXT_ID`
with it - is doing exactly what the other four cores rebuild on.
