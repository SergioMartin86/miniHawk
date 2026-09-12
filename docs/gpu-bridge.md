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

The first fix was a WARM-UP: draw the last few frames before a seek's
destination. Drawing the last frame only is 7.29% wrong here, the last two
3.87%, the last five exactly right, so PCSX2 declared ten and the frontend drew
them. **That was wrong, and the next section is why.**

**Dolphin and Ruffle were measured the same way and need nothing** - 0.00%
either way. Dolphin because its XFB always decodes from the machine's own memory
(the fix in the previous section), Ruffle because its rendering-off path skips
only the readback and still runs `Player::render`. That is the shape to copy:
skip what is pure output, never what the renderer will need next frame.

## A warm-up cannot be long enough (2026-09-11)

Marvel vs Capcom 2 is a fighting game. It redraws the whole screen sixty times a
second, so five frames of warm-up really does put its renderer back where a
straight playback would have left it. Take a title screen instead and the floor
falls out.

Flycast, Re-Volt, a seek to frame 1500 - which is its title screen, painted once
somewhere around frame 1350 and then left alone:

| frames drawn before the destination | picture |
|---|---|
| 1, 2, 3, 4, 5, 6, 7 | 72.60% differs - **the SEGA licence screen**, a whole screen behind |
| 8, 12, 30, 60, 120 | the same 72.60% |
| **300** | 0.00% |

Nothing between 1 and 120 is better than 1. The picture is not a slightly wrong
composition; it is the previous screen entirely. What a warm-up assumes - that
a renderer's state decays and a few frames of running rebuilds it - is not what
is happening. **What the renderer draws lives on the far side of the bridge and
STAYS there.** A screen a game paints once is painted by exactly one frame; skip
that frame and no later frame repaints it, because the game has nothing more to
say. Only a warm-up that reaches back past the paint is right, and how far back
that is depends on the game, not the core. There is no number to declare.

The same game at frame 2000 converges at 8, because the screen is fading out and
therefore being redrawn. That is what a warm-up number really measures: how
recently the content happened to change. Gran Turismo 4 on PCSX2, three
different points in its boot, all still 1.3% to 2.8% wrong with the ten frames
PCSX2 had declared.

### So the core keeps drawing, and only the readback is skipped

`video.drawEveryFrame`. The engine reads it at session open and never sends
`SetRenderingEnabled(0)`; `render == 0` then means only that the host does not
copy the picture out. It is read in the engine, not a frontend, because it is a
property of the core and must hold for every host that opens the package.

The reason this is affordable at all is that the drawing was never the expensive
part. 1500 frames on the GTX 1060:

| | turbo | drawing, no readback | drawing AND reading back |
|---|---|---|---|
| PCSX2, Gran Turismo 4 | 8.28 s | **8.30 s** | 9.75 s |
| Flycast, Re-Volt | 14.8 s | **15.0 s** | 17.5 s |

The readback is 0.98 ms a frame on PCSX2 and 1.8 ms on Flycast - a 1.2 MB
`glReadPixels` across the bridge, synchronous. The drawing is 0.017 ms and
within noise respectively. Turbo keeps all of the saving and loses the bug.

Verified with the declaration alone, no command-line flag: Flycast/Re-Volt at
frame 1500 after three rewinds, and PCSX2/Gran Turismo 4 at frames 900, 1500 and
2400 after three rewinds, are all byte-identical to a straight run that drew
every frame.

**Only two cores needed it, and it is the same two that had a turbo patch which
skips DRAWING**: PCSX2 (patch 0016, returns from `GSRenderer::VSync` before
`Merge`) and Flycast (patch 0010, returns from `OpenGLRenderer::Render` before
the pass that reaches a screen). xemu and RPCS3 export no `SetRenderingEnabled`
at all and so were never told to stop; Dolphin has none either; Ruffle has one
that skips only the readback. Every core that was measured clean was clean for
that reason.

### The sweep the rest of this section came from

Every bridged core, with content to run it, on the GTX 1060, 2026-09-11. Two
tests: a straight run of the same movie twice, and three passes of
seek-back-and-replay against it. The picture column is the last frame, drawn.

| core | content | straight run twice | rewind x3: machine | rewind x3: picture |
|---|---|---|---|---|
| PCSX2 | Marvel vs Capcom 2, Gran Turismo 4 | identical | EE RAM identical | 7.29% / 1.3-2.8% before the fix, exact after |
| Flycast | Re-Volt, 240pSuite | identical | System RAM + VRAM identical | 72.60% before the fix, exact after |
| Dolphin | Pro Rally 2002 | identical | System RAM identical | identical |
| Ruffle | New Star Soccer | identical | - (no domains) | identical |
| xemu | Prince of Persia: Sands of Time | identical | System RAM identical | identical |
| RPCS3 | GTA San Andreas | identical | MainRAM identical | identical |

PCSX2 was also put through a savestate round trip before every one of 3000
frames (`chimera-run --rerecord`): picture and EE RAM identical at every
checkpoint. Whatever the bridge does to a rewind, it is not that.

xemu and RPCS3 were measured for the first time in this round. xemu needed
nothing: its EEPROM is optional and the built-in identity is what a movie wants
anyway. RPCS3 took a fix - it died before its first frame on any disc carrying a
boot jingle, because `play_music_during_boot` hands an overlay to a video source
a headless build does not have and `overlay_audio.cpp` `ensure()`s it.

## The fallback that did not fall back (Windows, 2026-09-11; cause found 2026-09-12)

"When either fails the core draws the way it draws without a GPU" is what this
document says a few sections up, and on Windows it was not true for PCSX2. With
`renderer` set to `opengl` - or to `opengl-hw` with no bridge to be had, which
is the same path - the core ran for about a thousand frames and then took an
access violation. Bisected on Marvel vs Capcom 2: 800 frames fine, 1200 dead,
and dead at the same guest address every time, straight run or re-record.

The address symbolises to `_x86_64_get_dispatch` in Mesa's glapi
(`src/mapi/glapi/gen/glapi_x86-64.S`), which reads the GL dispatch table out of
thread-local storage through **%fs**, and miniBox said in the same log that the
guest's %fs was zero. The explanation first written here - that miniBox repairs
%fs at syscall boundaries, and a GL call between two syscalls gets there first -
had the WHEN wrong, and being wrong about when is what made it look unfixable.

Measured on the Windows box on 2026-09-12 with a five-line program: `wrfsbase`,
then plain arithmetic with no fault, no syscall and no yield of any kind. The
base survived `SwitchToThread`, was gone after `Sleep(1)`, and was gone after
47 ms and 16 million iterations of pure computation - one scheduler quantum.
Windows does not keep a user-mode FS base across a context switch at all; it
restores 0. So the loss has no boundary to be repaired at, and a guest that
reads a thread local often is dead within a second of starting.

The repair now happens at the fault instead (miniBox's Windows VEH): guest code
that faults while %fs holds anything but its thread pointer gets the pointer put
back and the instruction retried, which is safe because it faulted before it had
any architectural effect. A fault that is really the guest's own comes back at
once with %fs correct and is handled as it always was, which bounds the retry to
a single pass.

None of it is the bridge - it happens with no bridge at all, and the bridged
path never touches Mesa's glapi, which is why a GPU run does not see it. It
reaches any core that compiles Mesa into the guest, which since 2026-09-11
includes Ruffle's default software renderer: that core died inside twenty frames
on Windows before the repair, and runs after it.

## A pitch-black picture, looked for and not found (2026-09-11)

Reported from use: "the PS2 OpenGL hardware renderer no longer produces an
image, the picture is pitch black". It does not reproduce, and the flag added
the same day is not what it would be if it did.

`video.drawEveryFrame` was ruled out by an A/B rather than by reading: the
installed PCSX2 package was repacked with the key set to `false`, which puts the
engine back on the old contract (`SetRenderingEnabled(0)` really is sent, turbo
really does skip the drawing), and both packages were driven through the same
frontend, the same movie and the same destination. The two pictures agree. The
flag can only ever add drawing, and the guest is still told `1` once on the
first frame, which is what it was already doing: `renderingSent` starts at -1,
the first `wantRendering` call sends 1, and PCSX2's `chimera_render_enabled`
is 1 before anyone says anything.

What was driven, all on the GTX 1060, all showing a picture:

| path | result |
|---|---|
| `chimera-run --gpu`, turbo with screenshots | GT4 at 900 and 1500, 99.5% lit |
| `chimera-run --gpu --render-every-frame` | identical to the above |
| `chimera-run --gpu --rewind-loop 300,20` | MvC2 still 99.4% lit after 20 passes |
| frontend `--headless`, `opengl-hw`, plain play | identical to `chimera-run` |
| frontend `--headless`, project + piano roll, seek | correct at the seek's end |
| frontend `--headless`, soak with 11 back-jumps | correct |
| frontend in a WINDOW, and fullscreen | correct, photographed from outside |
| frontend, core rebooted twice in one process | correct, 120 fps |

Two of those looked black before the ground truth was checked, and neither was:
frame 702 of Marvel vs Capcom 2 is black **with that project's input** and lit
with a blank movie, and frames 400 and 600 are black under any input. A black
screenshot means nothing until the same frame of the same movie has been drawn
by `chimera-run` beside it.

What the report probably is, then, is the picture being WRONG rather than
absent - issues 55 and 56 - which is what the section above is about, and what
`drawEveryFrame` fixes. PCSX2's `renderer` now defaults to `software` for the
same reason: the deterministic rasteriser is the one that cannot degrade, and on
this box it costs a little over twice the time (2401 frames of Gran Turismo 4,
20.6s against 9.6s) for a picture that is byte-identical run to run, with EE RAM
the same under either renderer.

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
- ~~**Windows.** No frame drawn through it has been seen on a screen there.~~
  Settled 2026-09-11: the frontend was run windowed and fullscreen on the GTX
  1060 (`4.6.0 NVIDIA 581.42`) with `renderer=opengl-hw`, and the window was
  photographed from outside the process showing Gran Turismo 4 at 60 and 120
  fps. What is still unproven there is a LONG session: everything measured is
  thousands of frames, not hours.
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
