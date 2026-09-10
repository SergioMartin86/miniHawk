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

Taken by the user, 2026-09-08:

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
4. **The policy is locality, not a time budget.** SUPERSEDED and retaken,
   2026-09-08. It was "a seek should cost about a second at worst", and that
   promise cannot be kept: churn is not stationary, so a chain sized to hit a
   second on the mean frame overshoots on a busy one - which is exactly the
   scene somebody is scrubbing through when they notice. The history is instead
   dense where the work is and coarse where it is not, and the time a seek takes
   follows from that shape rather than being promised in advance. See "The
   policy: dense near the work" below. Measurement does not go away; it sets the
   defaults per core instead of enforcing a guarantee.
5. **A branch keeps a whole state of its own.** Taken 2026-09-08, settling the
   one question phase 3 could not start without. A branch is somebody's
   alternative route, and restoring one must never walk a delta chain or depend
   on the history it was made in: the history is a cache that coarsens and
   evicts around it, and a branch that became unreachable because of that would
   have lost work. So a branch stores a whole state, as it already does
   (`TasBranch.CoreData`, a `CloneSavestate`), and phase 3 keeps it that way
   rather than making a branch an anchor in the session's history.

   Jumping to a branch may still hand its state to the history to hold as an
   anchor, which is what happens today - but that copy is the history's, and
   losing it changes nothing about the branch. What stays true either way is
   the split the rest of this rests on: a branch's frame, name, input log and
   markers are WORK and live in the project; its state is cache, and losing it
   costs replaying to that frame.
6. **miniBox grows delta states**, and the design goes straight for them rather
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
  captured anyway. Apply it and you go back one epoch. (Removed later - see
  "What rewinding cost, and why it is gone". Every frame is reached forwards.)

The consequences are what make this worth doing:

- A state costs the frame's churn instead of the machine's size, so capturing
  every frame becomes affordable where capturing every fourth frame is not.
- With a delta per frame the greenzone is **complete**: every frame has a
  state, and seeking stops re-emulating anything. A seek becomes "load the
  nearest anchor, apply deltas forward", and the latency budget sets how far
  apart anchors sit, measured in delta applications rather than in emulated
  frames.
- ~~**Rewind stops being a seek.**~~ One frame back was to be one reverse
  delta, costing that frame's churn, instead of restoring the nearest state and
  replaying to get there. This is the one part of the design that did not
  survive contact with the measurement: see "What rewinding cost, and why it is
  gone".

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

**The greenzone sidecar retires.** DONE. `.chimeraGreenZone` beside the project
is now kept in the per-user cache, keyed by the project's id, so a project's
folder holds the project and nothing else.

One sentence here said an old cache could simply be ignored, since that costs
recomputation and never work - and while that is true of the RULE, it is a poor
thing to do on the release that moves the file. It would throw away somebody's
hours of greenzone silently AND leave the gigabyte sitting in the synced folder
that was the whole complaint. So a greenzone found beside a project is moved
into the cache the first time it is opened, and read where it lies if the move
will not happen.

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
- **An edit replayed is an edit typed.** Play to X, seek back to X-n, put
  DIFFERENT input in from there, and carry on past X to X+Y: the machine at
  X+Y must be the machine a straight run of the edited movie reaches. This is
  the promise a tool-assisted run actually rests on, and it is the one leg
  `--seek` cannot stand in for - a seek replays the SAME inputs, so it passes
  with the edit path broken.

  Two things it needs to be worth running. The comparison must be the
  MACHINE - the buses the core publishes, and the picture - and not the
  sandbox's arena state: the arena carries the guest heap as well, and two
  identical runs already differ there, so comparing it proves nothing in either
  direction. And the edit has to matter: an edit whose input the game ignores
  makes the whole comparison vacuous, so the input is calibrated per game
  (press what the game notices, measured, not assumed) and the run is rejected
  when the edited movie and the plain one reach the same machine anyway.

  `chimera-run --play <n> --seek <f> --edit-from <movie> --final-buses <file>`
  is the shape.

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

## The policy: dense near the work

Editing a movie is local. The frames somebody steps through, rewinds over and
re-records are the ones around the playhead; the frames from ten minutes ago are
visited to jump to, not to scrub through. So the history keeps three bands,
measured as distance from the newest captured frame:

| band | spacing | what it is for |
|---|---|---|
| near | every frame | stepping, rewinding, re-recording - the work |
| mid | one in a few | scrubbing back over the recent past |
| far | one in a great many | jumping to somewhere else in the run |

A frame does not stay in a band. It is captured into the near band, and as the
playhead moves on it falls through the mid band into the far one, being made
coarser as it goes. Coarsening is driven by distance and not by the budget, so
it is incremental and bounded: each captured frame pushes a couple of points
across a boundary and pays for those, rather than a stall when the budget fills.

The budget then decides only what happens to the far band, and what happens is
that it goes to disk, oldest first, into the project's cache directory. That is
the right thing to spill precisely because it is far: large, rarely touched, and
- if the disk copy is lost - regenerable like everything else here.

Every boundary in that table is a knob with a per-core default, in frames rather
than seconds, because the engine does not know a core's frame rate and the
frontend does.

### When the far band cannot reach the disk

A full disk, near enough always. The history carries on: `evict()` falls back to
thinning in memory - dropping trailing deltas, then whole stretches - which is
the correct fallback and costs frames rather than the session.

It is also completely silent, and from a piano roll a greenzone that stops
growing looks like the history going sparse for no reason anybody can see. So
the history remembers that it happened (`spillFailed()`, cleared when a new
spill directory is set) and `ce_session_greenzone_spill_failed` hands the fact
over. The engine knows; **saying it is the frontend's** - TAStudio asks about
once a second until it is true, then says it once and stops asking.

A message rather than an error because nothing has gone wrong that the history
cannot handle. What has gone wrong is the disk, and that is worth knowing before
the next thing on it fails less gracefully.

### Coarsening is composition, never deletion

This is the part that constrains the implementation, and it is easy to get
wrong. `deltas[i]` walks frame `anchor+i` to `anchor+i+1`. Dropping a link in
the middle of a chain does not thin it - it orphans every frame after it, since
there is then nothing to walk through. That is why the eviction this replaces
could only ever truncate a chain from its end.

Thinning is therefore COMPOSITION: two adjacent deltas are merged into one
delta that spans two frames. On miniBox's format that is an exact byte-level
merge - take the later program break and thread set, union the two page lists
with the later page winning - which is precisely what applying both in order
does, and it needs no sandbox and no running machine. Composed sizes grow
sublinearly, because a frame's churn lands mostly on the pages the frame before
it touched.

Two consequences fall out. A segment's links stop being one frame each, so a
link carries the frame it lands on and the invariant becomes "the spans tile the
segment without gaps" - the same contiguity, stated in the units it actually
holds in. And over a long enough span a composed delta approaches the machine's
whole working set, at which point the far band stops being deltas at all and is
simply anchors; that is a simplification rather than a special case.

### What the bands cost on a heavy core

Cheap cores make any policy look good. xemu, at 2 MB a frame and a 200 MB
anchor, is where the defaults have to be honest. Ten minutes of it:

| band | if it held | cost |
|---|---|---|
| near | 2 s, every frame | 240 MB |
| mid | everything else, one in two | ~54 GB |
| far | anchors every 20 s | 6 GB |

So the mid band's EXTENT, not its spacing, is what the budget actually buys, and
a mid band defined as "everything that is not near or far" is not affordable on
this core. It needs an extent of its own.

The far band's spacing has a latency of its own, too. Landing between two
preserved points 20 s apart on xemu costs either a 1200 frame replay, about 35 s
at 29 ms a frame, or a walk across the mid band's composed deltas if it reaches
that far, about 4 s. Neither is a second. That is the honest cost of giving up
the promise, and it is why the defaults have to come from measurement even
though the guarantee does not.

## What the bands measured

The same xemu run as above, 1200 captured frames, with the defaults the engine
ships (every frame for 2 s, one in three for 30 s, an anchor every 10 s):

| | chain of 200, no bands | the bands | the bands, 512 MB budget, spilling |
|---|---|---|---|
| budget | 4 GB | 4 GB | 512 MB |
| frames it holds | 301 of 300 | 482 of 1300 | 778 of 1300 |
| what it cost | 499 MB | 1557 MB | 2446 MB, mostly on disk |
| a captured frame | 40.8 ms | 47.6 ms | 47.8 ms |
| worst seek | 1.19 s | 0.99 s | 1.38 s |
| mean seek | 0.61 s | 0.54 s | 0.54 s |

Three things worth reading off it.

**The worst seek came in under a second without being promised one.** It
follows from the anchor spacing - a restore walks one stretch and no further -
which is a knob that trades memory for latency directly, rather than a target
the code has to keep hitting as churn moves around.

**Spilling holds MORE of the run on an eighth of the memory.** 778 frames
against 482, because the budget no longer has to destroy an old stretch to make
room; it moves it. Seeking into one of those costs about what seeking into
memory costs - the anchor load was always the small half - so the far band being
on disk is close to free until the disk is slow.

**Capture got slightly dearer, not cheaper.** 40.8 ms to 47.6, because
coarsening is real work done every frame. That is the trade the design makes on
purpose: a little per frame, always, instead of a stall when the budget fills.

## What coarsening costs

Thinning a band merges a landing into its neighbour, and the neighbour keeps
the span - so the neighbour ACCUMULATES, and every later merge re-reads all of
it. Collapsing four hundred landings that way is quadratic in the bytes, and
that stayed invisible for as long as the only core measured had frames that
overlapped almost perfectly.

`tests/perf/coarsenbench.c` is that loop with real deltas and no core. The
number that decides everything is how much of a frame's churn lands on the same
pages as the frame before it:

| frames overlap | uncapped | capped at 8 MB |
|---|---|---|
| 99% | 0.16 ms/frame, band 4.1 MB | 0.17 ms/frame, band 4.1 MB |
| 90% | **11.0 ms/frame**, band 40.1 MB | 0.42 ms/frame, band 45.6 MB |
| 70% | **33.6 ms/frame**, band 119.9 MB | 0.63 ms/frame, band 132.6 MB |

Thirty three milliseconds a frame, spent reclaiming a few per cent, on a
machine whose frames overlap seventy per cent. Two fixes, and the second is the
one that matters:

**Compose two deltas where they lie.** The streaming `wbx_compose_delta` reads
both into buffers of its own before it can merge them, because a count is
written before its list and a write callback cannot be seeked back to. That is
the right shape for a delta coming off a disk and the wrong one for the caller
that does this every frame, which holds both contiguously already - it was
copying megabytes to look at megabytes. `wbx_compose_delta_mem` walks them in
place: nothing allocated, each input byte read once, each output byte written
once. Nine times faster on a two megabyte merge. It is bound optionally, and a
host without it falls back to the streaming one for the same answer.

**Cap how large a merge may get** (`kMergeCap`, 8 MB - about a millisecond of
memory bandwidth). This is what turns the quadratic into a bounded per-frame
cost, and it is nearly free: the merges it refuses are the handful of biggest
ones, which are exactly where the union is closest to the sum and least is
reclaimed. Six refusals out of four hundred buy back a twenty-six-fold
speed-up for fourteen per cent more memory in the band. A landing left in place
only makes the band denser than asked, which is safe; the budget answers for
the memory.

The anchor guard stays as the outer bound on the same thought: a composed link
that already costs what a whole machine costs is better served by the anchor it
is walking from.

## What rewinding cost, and why it is gone

Reverse deltas were removed. Every frame is now reached the one way: **load an
anchor and apply the deltas since it.**

They were measured, on the same xemu run, 1200 frames, 4 GB budget:

| | kept | not kept |
|---|---|---|
| a captured frame | 56.9 ms | 48.6 ms |
| one frame back | 5.2 ms | a seek, up to 1.0 s |
| the history on disk | 1557 MB | 1557 MB |

Eight milliseconds on every captured frame, against a gesture that dropped from
a second to five - and that was the honest trade as long as capture was rare.
It stopped being rare. The greenzone captures every frame now, so the eight
milliseconds is paid by everybody all the time, and it bought a gesture that a
faster seek serves well enough.

The saving is larger than the table says, because the price was not only the
second delta. Keeping backwards possible meant the fault handler copied every
page the first time a frame wrote it - a four kilobyte memcpy per page per
frame, inside a signal handler, out of a fixed pool that could be exhausted -
and it meant carrying a pre-image pointer and kind on every page struct, which
made every remaining walk of the page array wider. All of that went with it.

`mb_block_delta_save` refuses the backward direction outright rather than
answering it with zeros: a delta of zeros would wipe memory the machine still
needs, and nothing else would ever say so.

## What a captured frame costs, and what it is proportional to

Capturing every frame only works if a capture costs the frame. It did not.

Measured with `tests/perf/epochbench.c`, which is the epoch machinery alone -
no core, no game - on an arena of a chosen size with a chosen number of pages
written per frame. Milliseconds per frame, before and after:

| arena | written/frame | open | faults | delta | total |
|---|---|---|---|---|---|
| 64 MB | 256 | 0.41 -> 0.39 | 1.20 -> 0.88 | 0.06 -> 0.00 | **1.67 -> 1.27** |
| 256 MB | 256 | 0.70 -> 0.40 | 1.32 -> 0.88 | 0.24 -> 0.00 | **2.26 -> 1.28** |
| 1 GB | 256 | 1.56 -> 0.43 | 1.38 -> 0.89 | 1.11 -> 0.01 | **4.04 -> 1.33** |
| 2 GB | 256 | 3.51 -> 0.44 | 1.70 -> 1.05 | 4.43 -> 0.02 | **9.64 -> 1.51** |
| 2 GB | 16 | 2.78 -> 0.09 | 0.14 -> 0.06 | 3.24 -> 0.01 | **6.15 -> 0.16** |
| 2 GB | 1024 | 5.20 -> 1.66 | 5.86 -> 3.84 | 5.47 -> 0.02 | **16.53 -> 5.51** |

Read the 16-page and 1024-page rows together and the fault is plain: writing
sixteen pages cost 6.1 ms and writing a thousand cost 16.5 - **the work was
proportional to how big the machine could be, not to what it did.** Sixty four
kilobytes of change cost more than a third of a frame at sixty a second, on
every core with a big arena, which is exactly the "everything got slower" that
prompted this.

Every per-frame path walked the page array end to end, several times over:
opening an epoch cleared per-page state and set a flag on every tracked page,
then looked for the runs to re-protect; saving a delta counted the dirty pages,
compared the whole allocation map against a copy of itself taken when the epoch
opened, and then walked the array again for the data. A page struct is forty
bytes, so a two gigabyte arena is twenty one megabytes a pass.

The fix is not cleverness, it is bookkeeping. The sets a frame cares about are
kept as **bitmaps**, one bit a page, sixty four kilobytes for that same arena:

- `epoch_bits` - written during this epoch. Set by the fault that lets the
  write through, which is two words of work inside the handler.
- `stat_bits` + `epoch_status` - pages whose allocation changed, and what it
  was. Recorded as the change happens, which replaced both the half-megabyte
  copy taken per epoch and the comparison that read it back.
- `unheld_bits` - pages mapped writable right now. This is the one that matters
  most: opening an epoch re-protects exactly these, and they are exactly the
  pages written since the last epoch opened, because everything else is still
  protected from that one. It used to re-protect every page the machine had
  ever written, every frame.

Iteration is ascending, which the delta format needs: both its lists are in
page order so that composing two deltas is a merge of sorted runs.

A clean page needs no hold and gets none. It is already read-only for the
baseline's sake, its first write already faults, and that fault records the
epoch's page too - which is why the flag can be set on so few pages without
losing any.

What is left is proportional to the frame: one `mprotect` per run of pages
written last frame, the guest's own write faults, and the delta's own bytes.
The bench scatters its writes as widely as it can, so its runs are one page
long and its numbers are the worst case; a real machine writes in clusters.

The `faults` column fell too, by a quarter to a third, and that is the removal
of reverse deltas showing up: the handler no longer copies a page every time a
frame first writes it.

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
   The band policy followed, in four gated steps, all DONE:
   - **Composition in miniBox** (`40fcdb1`). `mb_delta_compose` merges two
     adjacent forward deltas into one that spans both, taking no host and no
     block, so a history can thin states it is merely storing.
   - **Variable spans in the engine** (`83bd31e`). A link carries the frame it
     lands on instead of an implied stride of one; the file format became
     ChimeraHistory2 and names its predecessor as superseded rather than
     refusing it.
   - **The band policy** (`03e9799`). Distance-driven coarsening on a per-band
     grid, and the anchor spacing in place of the chain limit.
   - **Spill** (`7c9502c`). The oldest stretches to the caller's directory once
     the budget is full, restored by reading only as far along one as the target
     needs.
   The frontend's greenzone moved to the per-user cache (`c595db6`), so a
   project's folder holds the project and nothing else. Phase 2 is closed.
3. **The frontend: one history. DONE.** `PagedStateManager`,
   `ZwinderStateManager`, `ZwinderBuffer`, `IStateManager`, the settings chooser
   and the settings page are deleted, and TasMovie drives the engine's history
   through `IStateHistory`: 867 lines added against 4773 removed.

   What was load-bearing C# is now a remote control. The history holds no copy
   of itself up here - not which frames exist, not the lag flags, which ride
   along as the note - because a second copy is a second thing to keep true, and
   it would go wrong quietly on the long runs where nobody could reproduce it.

   Two things this settled. A branch does not go through the history at all: it
   keeps a whole state of its own (decision 5), so reaching one never depends on
   a history that coarsens and evicts around it. And a marker that wants instant
   navigation pins its frame instead, which is cheap to name, impossible for the
   engine to guess, and far cheaper than a whole state each.

   The rewind gesture followed. Going back a frame was a seek: restore the
   nearest stored frame and replay to the target, which costs an anchor load and
   every link taken since it in order to undo one frame's work. It is now a
   reverse delta, which undoes exactly that frame.

   Reverse deltas are free of faults - the pre-images are captured already, by
   the same fault that serves the forward one - so they cost only the write, and
   they are kept only near the playhead, which is where stepping backwards
   happens. `LoadStateAt` tries the walk first and falls through to the restore,
   and the walk never goes further than its window, so a long jump does not pay
   a hundred applications to discover it should have seeked.

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

## What a rewind was blamed for, and was not

A PS2 project crashed on rewind, and a Flycast one crashed on a piano-roll
edit. Both are the state history's paths, so the state history was where the
search started. It was not there, and the way that was established is worth
keeping, because the next report of this shape will look identical.

The measurements, all on the reporter's own machine and their own game:

| what was asked | answer |
| --- | --- |
| does a rewind land where the forward pass was | guest RAM identical at every depth, 1 to 29 steps |
| does a rewound machine still follow the same trajectory | rewind 20, replay 180: identical every frame |
| does `restore` agree with a run that never stopped | identical at every frame |
| is a PS2 savestate even stable to compare | stable, and deterministic across a reload |
| plain playback, 1200 frames, real GPU | survives |
| with the greenzone capturing and spilling | survives |
| with every memory domain swept by raw pointer each frame | survives |
| with the piano roll open, 1500 frames (tests/soak) | survives |

Two wrong turns, kept because each cost real time:

**A savestate is not a machine.** The first comparison digested whole
savestates and reported a mismatch from the fourth rewind step back. It was
bookkeeping: a page restored to the value it already held is DIRTY in one path
and clean in the other, so the state carries it in one and not the other while
the machine is identical. Compare guest memory - `ce_session_domain_read`, or
replay and compare trajectories - and the difference disappears. A savestate
parser that ignores which pages are invisible will also mis-assign every
payload after the first invisible dirty page, which is how the same run
produced a confident, wrong page number.

**The loudest fault is not always the first.** The reported fault was a host
address reading a no-access page, which reads like host code running off the
end of a buffer. It was the second fault of two in the same second: the guest
faulted first on a null pointer, and Windows then dispatched that exception on
the guest's own 1 MiB stack until it reached the guard at the bottom. The
guard-page fault was the symptom of reporting the real one. miniBox now names
the region an address lands in, so that reads as "the guard at the bottom of
the alt stack - this stack overflowed" rather than as a page number.

### What it was

A delta being wrong after all, and in the one place nothing was watching.

`munmap` zeroes the page it takes away, because that is what the guest's next
`mmap` of it is entitled to find - musl hands fresh anonymous memory to malloc
on exactly that promise. None of that goes through the fault handler, so the
epoch never heard about it: the delta recorded the page moving to free and back
and not a byte of content, and a frame rebuilt from it came back holding what
the page held BEFORE it was freed. The guest then handed that out as fresh
memory. A heap does this all day, which is why the damage was neither subtle nor
local, and why it always landed somewhere unrelated - a `std::map` insert, the
microVU dispatch - seventy frames after the seek that caused it.

Finding it needed the crash on demand, and that needed the piano roll, because
the trigger is a seek BACKWARDS and nothing else:

| variant, 1500 frames | result |
| --- | --- |
| plain play, TAStudio open | survives |
| record mode, no jumps | survives |
| jumps back, with or without record | dies within ~70 frames of the first |

Fixed in miniBox a710035: an epoch is told when a page's allocation changes,
not only when one is written. Both crashing variants now survive.

Two things worth keeping from the search. `test_delta` calls its cases from
`run_all` by hand, so adding a function is not adding a test - the three tests
that pin this passed against the broken code the first time they were written,
which is a green run that means nothing. And the 64 GiB `mmap` in the log, which
looked like the cause for most of a day, was a corrupted size read out of a heap
that had already been handed a stale page: a symptom of this, several steps
downstream. miniBox now prints the guest return
addresses on a refusal that large, and a core package ships `core.wbx`
unstripped, so `addr2line` names the caller.
