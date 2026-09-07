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
answers with an identity minted when the context is made (0 means "cannot tell" -
no bridge, or a host older than the question, and a guest must read that as
"assume nothing moved"). A renderer that stores that number beside its objects
can see, at the top of any frame, that the ground has moved - and rebuild.

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

### What the frontend does with that

A core declares `video.gpuStatesSurviveTheContext` when its renderer does this,
and a project records it (`GpuStatesSurvive`) so that the answer is there when
the cached states are opened - by which time there is no core to ask. For a core
that says yes, the greenzone is kept like any other core's. For one that does
not, the states are session-local: the project writes none into its
`.chimeraGreenZone` and uses none from an older one, branch states included, and
says so when it opens. Rewind and branches within a session are untouched either
way - the objects are still there - and a project that loses its cache replays,
which is what an empty greenzone has always meant.

RPCS3 says yes. PCSX2, Flycast, xemu, Dolphin and Ruffle do not yet: the same
split would work for the two that have an `on_exit`/`on_init` pair of this
shape, and Ruffle needs it a layer lower, inside wgpu.
