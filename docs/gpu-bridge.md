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

## A seek does not draw, and one renderer noticed

Reported from use: a PlayStation 2 project's picture degrades as you re-record,
without desyncing, and playing the movie from the beginning puts it right.

It is none of the things that sounds like. Measured on a GTX 1060, Marvel vs
Capcom 2, 3000 frames:

| | |
|---|---|
| rewind to frame 1500 and replay, once | 7.29% of the picture differs |
| the same, five times | **the same 7.29%** |
| the same, twenty times | **the same 7.29%** |
| EE RAM, every time | identical |
| save and reload the state every frame for 3000 frames | identical, picture and RAM |
| **a straight run with no rewind at all, composing only the last frame** | **the same 7.29%** |
| the same on the SOFTWARE renderer | 7.56% |

The last two rows are the answer. It is not cumulative, not the savestate, and
not the GPU. **A seek replays with drawing off** - correctly, nobody is looking
at the frames on the way - and PCSX2's turbo patch implements "off" by returning
from `GSRenderer::VSync` before `Merge`. Merge is not only composition: it
decrements a scanmask countdown, advances the deinterlace phase, and leaves the
device holding the frame a blend deinterlacer needs next time. Skip it for
fifteen hundred frames and the one frame that IS composed is composed from state
that never saw them.

So the fix is a WARM-UP, and it is bounded. Drawing the last frame only is 7.29%
wrong, the last two 3.87%, and **the last five exactly right**. A core says how
many frames its renderer needs (`video.renderWarmupFrames`, zero for almost all
of them) and the frontend starts drawing that many before a seek's destination.
PCSX2 declares ten - five with margin, and composing costs 0.75 ms a frame there,
so the whole warm-up is under 8 ms per seek.

Verified end to end: a run that rewinds three times with a five-frame warm-up is
BYTE-IDENTICAL to a straight run. With one frame it is the 7.29% above.

**Dolphin and Ruffle were measured the same way and need none** - 0.00% either
way. Dolphin because its XFB always decodes from the machine's own memory (the
fix in the previous section), Ruffle because its rendering-off path skips only
the readback and still runs `Player::render`. That is the shape to copy: skip
what is pure output, never what the renderer will need next frame.

### The sweep the rest of this section came from

Every bridged core with content to run it, on the GTX 1060, 2026-09-11. "Rewind"
is three passes of seek-back-and-replay against a straight run of the same
movie; "warm-up" is the every-frame-composed against last-frame-only test that
isolates a renderer's frame-to-frame display state.

| core | straight run twice | rewind x3: machine | rewind x3: picture | needs a warm-up |
|---|---|---|---|---|
| PCSX2 (Marvel vs Capcom 2) | identical | EE RAM identical | 7.29% before the fix | **yes, 5 frames** |
| Dolphin (Pro Rally 2002) | identical | System RAM identical | identical | no (0.00%) |
| Ruffle (New Star Soccer) | identical | - (no domains) | identical | no (0.00%) |
| xemu | - | - | - | not run: no valid eeprom.bin here |
| Flycast | - | - | - | not run: no dc_boot.bin here |
| RPCS3 | - | - | - | not run |

PCSX2 was also put through a savestate round trip before every one of 3000
frames (`chimera-run --rerecord`): picture and EE RAM identical at every
checkpoint. Whatever the bridge does to a rewind, it is not that.

## The fallback that does not fall back (Windows, 2026-09-11)

"When either fails the core draws the way it draws without a GPU" is what this
document says a few sections up, and on Windows it is not true for PCSX2. With
`renderer` set to `opengl` - or to `opengl-hw` with no bridge to be had, which
is the same path - the core runs for about a thousand frames and then takes an
access violation. Bisected on Marvel vs Capcom 2: 800 frames fine, 1200 dead,
and dead at the same guest address every time, straight run or re-record.

The address symbolises to `_x86_64_get_dispatch` in Mesa's glapi
(`src/mapi/glapi/gen/glapi_x86-64.S`), which reads the GL dispatch table out of
thread-local storage through **%fs** - and miniBox says, in the same log, that
the guest's %fs was lost: "guest %fs was 0000000000000000 ... something outside
a fault took it". It repairs %fs at syscall boundaries, which is where it
notices; a GL call between two syscalls gets there first and dereferences zero.

So it is not the bridge - it happens with no bridge at all, and the bridged path
never touches Mesa's glapi, which is why a GPU run does not see it.
`renderer=software` (PCSX2's own rasteriser, no Mesa) is fine. What it costs is
exactly the promise above: a machine that cannot have a GPU cannot use this
core's OpenGL renderer either, and falls back only as far as the software one.

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
| dolphin | yes, once fixed - see below |
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

**Dolphin needed two fixes, and the second one was the interesting one.** The
crash was a null framebuffer: the rebuild cleared `m_current_framebuffer` and
the machine draws through `BPFunctions::SetScissorAndViewport` as soon as it
runs, which dereferences it. Binding the EFB afterwards fixes that.

Underneath was a picture that came back wrong and never recovered - 62% of the
pixels 40 frames after the load, 99.7% after 240. `GetXFBTexture`, asked for the
picture, prefers a copy of the XFB kept in VRAM: the crisp one, rendered at
internal resolution and never round-tripped through the console's YUV
framebuffer. That copy lives in a GL context and nowhere in the machine, so no
savestate carries it, and a reopened run had only the RAM decode to fall back
on. Dolphin now always decodes the XFB from the machine's own memory.

That is the rule the core already applied to the copies themselves
(`SKIP_XFB_COPY_TO_RAM` off, "the machine's video memory stays machine state");
presenting from VRAM while hashing RAM was the half that had been left out, and
it meant what you watched was not what the machine held. The check that it is
the right way round: the GL renderer now agrees with the software renderer,
which has no VRAM to prefer and is the machine's own answer. It costs the
upscaled presentation - a fidelity a TAS has no business depending on.

A reopened dolphin run is now byte-identical to one that never stopped, across
RAM, ARAM, the L1 cache, audio and every pixel, at 40, 120 and 240 frames past
the load. Its package declares `gpuStatesSurviveTheContext: true` again.

Getting there took three wrong explanations - the EFB framebuffer, then EFB
copies, then the texture cache as a whole - each of them reasoned rather than
measured, and each disproved by building the core without the part it blamed.
What finally pointed at the XFB was counting what the cache actually held at
rebuild time: ten entries, none of them EFB copies, and keeping only the single
XFB one made the reopen identical.

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
