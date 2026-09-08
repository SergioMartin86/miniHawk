# The state history

How Chimera remembers where a machine has been: rewind, the greenzone, branch
states, and what survives closing a project.

Status: phase 1 BUILT (miniBox 0c972de, with its measurements below); the rest
is design, decided with the user 2026-09-08.
It replaces the surviving implementations with one, and it is the remaining half of
step 5 of the engine migration (docs/engine-migration.md), which always said
TAStudio's state history would move onto the session's.

## Why this is being redone

There are two answers in the tree to one question - what did this machine look
like at frame N:

- the engine's greenzone (`ce_session_greenzone_*`): budget-bounded, anchored,
  with seek and invalidate. Witnessed by the gates. Only `chimera-run` uses it.
- `PagedStateManager` (C#): what TAStudio actually uses, with the older
  `ZwinderStateManager` still selectable beside it and `ZwinderBuffer`
  underneath that one as its storage.

There is deliberately no third one for rewind, and it is worth being exact
about why, because the name suggests otherwise. `MainForm.Rewind` does nothing
unless a tool claims `WantsToControlRewind`; TAStudio is the only tool that
does, and in a TAS-only frontend TAStudio is always there. Its `Rewind()` is
`WheelSeek` - a seek backwards through the greenzone - and its `CaptureRewind`
is a no-op with a comment saying TAStudio handles this just fine. There is no
rewind buffer and no rewind setting left in config. Rewind is a gesture over
the state history, not a mechanism of its own.

The two that remain are BizHawk-lineage designs, written when a savestate was
kilobytes and a big one was a few megabytes. Chimera's cores are whole sandboxed
machines. Measured on the user's own Prince of Persia run under xemu, a state
is 75 MB at frame 150, 102 MB at 600 and 210 MB at 1200: it grows as the game
touches memory, and it is the whole machine every time.

Everything downstream of that number is now wrong:

- **Saving loses the work silently.** The greenzone is serialized through
  managed `byte[]` buffers - the history into one array, each lump into
  another, the whole archive into a third - and a .NET array is indexed by
  `int`, so it stops near 2 GB. A 3711-frame run is about 5.5 GB of states.
  The write throws, a `catch` swallows it, and the project is saved with no
  states at all and nothing said. Reopening then replays from frame 0, which
  is what the user reported and what started this.
- **Stepping back costs a seek.** Holding rewind asks for the nearest state and
  replays forward to the frame before the one you were on. On a core that
  emulates at 52 ms a frame that is as expensive as jumping anywhere else in
  the run, which is why going back one frame does not feel like the inverse of
  going forward one frame.
- **The right implementation is the unused one.** The engine's greenzone is in
  the right place - linkable by jaffarPlus, testable without Mono or a display
  - and the application never calls it.

A patch to the 2 GB ceiling would leave three implementations, a rewind that
does not work, and a state costing what the machine IS rather than what it DID.

## The decisions

All five taken by the user, 2026-09-08:

1. **The cache lives in a per-user directory**, keyed by project identity, not
   beside the project. BUILT (`fbff1a4`): the project gained an id, minted at
   creation and carried in the file, and `ProjectCache` resolves a directory
   from it. The remembered file locations moved there first - a `.chimeraLocal`
   beside an older project is read once, moved, and taken out of the folder -
   so a project's folder now holds only the project. Projects live in synced folders - the user's are in
   Google Drive - and a multi-gigabyte sidecar there is uploaded on every save,
   with the sync client holding files open mid-write. It follows the core store
   (docs/core-manager.md): `%LOCALAPPDATA%\Chimera` on Windows, the XDG data
   directory elsewhere, `CHIMERA_DATA_HOME` overriding both.
2. **One implementation.** `PagedStateManager`, `ZwinderStateManager`, the
   settings chooser and the type-dispatching converter all go. Settings naming
   a deleted type fall back to defaults, which is safe precisely because the
   cache is regenerable.
3. **Rewind is not a separate thing**, and already is not one. It stays a
   gesture over the history, and the history is what has to make it cheap.
4. **The policy is a time budget, not a frame count.** A seek should cost about
   a second at worst. The same frame count means a fifth of a second on one
   machine and twenty seconds on another, so the engine derives spacing from
   the cost it measures rather than from a number somebody typed.
5. **miniBox grows delta states**, and the design goes straight for them rather
   than shipping content-addressed dedup first.

## What a state costs, and the idea that changes it

Today every stored state is a full snapshot of the machine relative to the
sealed baseline. Two states a few hundred frames apart are mostly the same
bytes, and both are stored whole.

miniBox already knows better than that. It maps writable pages read-only,
faults on first write, snapshots the page's previous contents and marks it
dirty - that is how a savestate carries only what changed since seal. What it
does not do is track change since any LATER moment.

So it grows one: an **epoch**. Marking an epoch re-protects the pages that are
currently writable, so the next write to each faults again and records two
things - that the page changed in this epoch, and what it held before. Both
fall out of machinery that already exists; the guest cannot observe any of it,
so the frozen machine spec is untouched and no movie is affected.

That gives the history two new kinds of thing to store:

- a **forward delta**: the pages changed during an epoch, as they ended. Apply
  it to the machine at the epoch's start and you have the machine at its end.
- a **reverse delta**: the same pages as they *began*, which the fault handler
  captured anyway. Apply it and you go back one epoch.

The consequences are what make this worth doing:

- A state costs the frame's churn instead of the machine's size, so capturing
  every frame becomes affordable where capturing every fourth frame is not.
- With a delta per frame the greenzone is **complete**: every frame has a
  state, and seeking stops re-emulating anything. A seek becomes "load the
  nearest anchor, apply deltas forward", and the latency budget sets how far
  apart anchors sit, measured in delta applications rather than in emulated
  frames.
- **Rewind stops being a seek.** One frame back is one reverse delta, costing
  that frame's churn, instead of restoring the nearest state and replaying
  forward to get there. Going back a frame finally costs about what going
  forward a frame costs, which is what the gesture always implied.

Content-addressed storage still earns its place underneath, because deltas
repeat: a page written with the same bytes every frame is one chunk. Dedup
becomes a property of the store rather than a strategy of its own.

## The shape

**One history, owned by the engine.** Capture, storage, eviction, seek,
invalidate and persistence live in `libchimera`, next to the session that
already owns the movie and the frame position - the same rule as everything
else there: the frontend cannot desync what it cannot reach. TAStudio becomes a
view over it, asking where states exist and asking to be moved.

**Storage separated from policy.** The store knows about chunks, anchors,
deltas, a memory tier and a disk tier, and nothing about TAS. The policy knows
about anchors per second, pins, and budgets, and nothing about bytes on disk.
The current `PagedStateManager` reasons well about density and reservation;
that thinking survives, its storage does not.

**The store is the on-disk format.** Not a zip of lumps assembled in memory:
an append-only chunk file with an index, written as it goes. Saving a project
flushes and writes the index rather than rewriting gigabytes, so saving at
frame 3711 costs what changed since the last save. No archive is ever
materialized whole, in managed memory or otherwise, which is the bug class this
started with.

**The greenzone sidecar retires.** `.chimeraGreenZone` beside the project is
replaced by the per-user cache. The format may change freely and needs no
migration: an old cache is ignored, which costs recomputation and never work -
the rule the greenzone has always been held to.

## The ABI, and not breaking what works

`chimera-run` and every gate drive `ce_session_greenzone_*` today, and the
engine header is additive-only by rule. So the existing entry points stay and
are reimplemented over the new history with their current meanings, and the new
capabilities arrive as new calls. The gates keep passing unchanged, which is
the point: a redesign of the state history must not be witnessed by a rewritten
witness.

## Proving it

The synthetic core cannot exercise any of this - its machine has 4096 bytes of
RAM - which is exactly why none of the existing gates caught a 2 GB ceiling.
**The synth machine gains a settable RAM size**, so a multi-gigabyte history is
something Chimera's own CI can produce in seconds without a real core, a rom or
firmware.

The legs that have to exist:

- **The history changes nothing.** A run with capture on and a run with capture
  off must be byte-identical. This is the one that matters; everything else is
  performance.
- **A delta is a state.** Anchor plus applied deltas must equal the full state
  taken at that frame, byte for byte, at every frame of a run.
- **A reverse delta is the previous frame.** Step back and forward across a run
  and land on the same machine each time.
- **Persistence round-trips across processes.** Write a history, exit, reopen,
  seek into it, and reach the machine a straight replay reaches - the check the
  user's crash and the empty greenzone would both have failed.
- **Eviction keeps every frame reachable.** Under budget pressure the history
  coarsens; no frame may become unreachable, and the anchor never goes.

## What the first phase measured

Phase 1 is built (miniBox `0c972de`), and it turned the design's two open
questions into numbers. Measured on a real xemu boot into Prince of Persia,
1300 frames, null renderer:

| | |
|---|---|
| churn | mean 506 pages a frame (2.0 MB), max 7143 (28.6 MB) |
| a delta against a full state | 2.0 MB against 75 MB at frame 150, 210 MB by frame 1200 |
| cost of an epoch per frame | 49.2s -> 66.8s over the run, +14 ms a frame, 37% |

The saving is about a hundredfold, which is the number this design was betting
on, and it holds. The cost is not free and does not go away: an epoch per frame
means a fault per page written, so it scales with churn - the right shape, and
still 37% on this core. Two consequences for the phases below:

- **The epoch cadence is a policy knob, not a constant.** Per-frame epochs buy
  a complete history and a rewind that costs one frame; every few frames buys
  most of the saving for a fraction of the overhead. The engine picks it the
  same way it picks anchor spacing - from what it measures - and the dense
  cadence belongs near the playhead where somebody is working, not across a
  long unattended seek.
- **A complete per-frame history is still gigabytes.** 2 MB a frame is 2.6 GB
  for 1300 frames. Deltas make density affordable where it was impossible, but
  they do not remove the need to coarsen with distance, so the tiered policy
  stays exactly as important as it was.

One negative result worth keeping: restricting the post-mark protection refresh
to the runs that actually change, instead of walking the arena, was worth about
1% here. The fault path dominates, not the walk. It is kept because a big arena
with little churn - a 2 GB DOS machine writing fifty pages a frame - is the
case it exists for, but it is not where the time goes on a console.

## What a seek costs

Capture was the first half and storage was the second; getting a frame BACK is
the half the one-second decision actually rests on, and it was measured last.

`tests/perf/seekbench.cpp` runs a real xemu boot into Prince of Persia, captures
every frame of a stretch, and then times seeks to frames it has stored. Every
target is a stored frame, so the seek's own replay is zero frames and what the
clock sees is the restore alone. `CHIMERA_NO_DELTAS=1` makes every capture an
anchor, which is the same build, the same run and the same budget against whole
states - the A to this B.

300 captured frames, a 4 GB budget, null renderer, and a bare frame of 29 ms:

| | whole states | deltas |
|---|---|---|
| frames the budget held | 56 | 301 |
| a stored frame | 72.8 MB | 1.66 MB |
| a captured frame | 91.3 ms (+62) | 40.8 ms (+12) |
| worst seek | 19.7 s | 1.19 s |
| mean seek | 8.8 s | 0.61 s |

The old shape is worse at both ends at once, which is the part worth saying
plainly. It is not that whole states buy speed with memory: they cost five times
the capture overhead AND they hold a fortieth of the run, and because they hold
so little of it every seek lands far from a stored frame and turns into a replay
of a hundred-odd frames at 90 ms each. The 19.7 s worst case is 225 frames of
replay, and it grows with the run.

The delta side is linear and legible. From the traced 1200-frame run:

| | |
|---|---|
| anchor load | 30-45 ms, flat from a 68 MB state to a 201 MB one |
| a delta applied | 4.7 ms, steady across chains of 24 to 450 |
| so a restore | about 35 ms + 4.7 ms per link |

Two things follow.

**A delta is six times cheaper than re-running the frame.** 4.7 ms against 29
ms. That is the ratio that justifies deltas over the obvious alternative of
sparse anchors and replay, and it is a ratio to re-measure per core rather than
assume: a core whose frame is cheap and whose churn is heavy could invert it,
and for that core the right chain is short.

**The chain limit is the latency target, written in the wrong units.** 512 links
is 2.4 s, and the run's worst seek measured 2.07 s - so the constant's own
comment, which claimed it sat inside a second, was wrong. It is now 200, which
makes the claim true on this core at a cost of roughly a sixth more memory. It
should not be a constant at all: the number that belongs there is the target
divided by the per-delta cost this core is showing, and that is the next piece
of phase 2.

What is still unmeasured, and should not be guessed at: the cost with the GPU
renderer running rather than the null one, and the same numbers on rpcs3, whose
state and churn are both larger.

## Phasing

Each phase is separately gated and separately landable.

1. **miniBox: epochs and deltas. DONE** (miniBox `0c972de`.) Epoch marking, forward and reverse deltas,
   and the page introspection they need. Gated in miniBox's own suite, plus a
   differential check that a delta chain equals the full state.
2. **The engine: store and history. IN MEMORY, DONE** (`7252029`.) Segments -
   an anchor and the contiguous deltas that walk forward from it - with a byte
   budget and eviction, and the `ce_session_greenzone_*` entry points
   reimplemented over them, so chimera-run and every gate drive it without
   knowing it changed. A host without epochs falls back to anchors, since an
   older libminiboxhost beside a newer libchimera is ordinary. Proven on xemu
   with 200 MB states: a 200-frame run seeking back to 100 through the chain
   dumps System RAM byte-identical to a run that never stopped; the witness is
   40/40 and CHIMERA_HISTORY_TRACE shows its seek legs really do walk deltas.
   Persistence followed (`64a4e16`): the history writes to a file and reads one
   back, streamed a segment at a time so that nothing is ever assembled in
   memory, and the witness gained the leg that could not exist before - one
   process keeps a history, a second starts from it, seeks into states it never
   made, and lands on the goldens. The engine carries a machine id rather than
   deciding what makes two machines the same, since the caller already knows
   about cores, settings and files.
   STILL TO COME in this phase: the measured policy that sets anchor spacing
   and epoch cadence from what the engine observes rather than from the
   provisional constant in the code, and pointing the frontend's saves at the
   per-user cache directory that now exists.
3. **The frontend: one history.** TAStudio onto the session's history, with the
   rewind gesture rebound to a reverse delta rather than a backwards seek;
   `PagedStateManager`, `ZwinderStateManager`, `ZwinderBuffer`, the
   settings chooser and the sidecar deleted; the cache moves to the per-user
   directory.

## Sharp edges to expect

- **Re-protecting an arena is not free.** A 2 GB layout is half a million
  pages. Only pages that are currently dirty and writable need re-protecting,
  and the runs must be coalesced. This has to be measured on xemu and rpcs3
  before committing to an epoch per frame; the fallback is an epoch every few
  frames, which costs seek precision and nothing else.
- **Fault storms.** Per-epoch first-write faults are bounded by the churn, which
  is the thing being paid for anyway - but a game that touches a lot of memory
  every frame is the case to measure, not to assume.
- **A state load rewrites the machine.** Every piece of host-side bookkeeping
  keyed to guest memory has to be re-established afterwards. The session already
  learned this twice, for the wide-input latches and for the trace flag; epoch
  tracking is the third.
- **A GPU core's objects are still its own.** Delta states do not change that a
  renderer's GL object names belong to the session that made them
  (docs/gpu-bridge.md); the rebuild on a moved context stays exactly as it is.
- **Anchors still cost the machine.** Deltas make the frames between anchors
  cheap; an anchor is a full state. The budget is spent mostly on how often
  anchors are taken, which is what the latency target decides.
