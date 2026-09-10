# The cache manager

What Chimera keeps on disk that it could work out again, how much room it is
allowed, and which of it may not be thrown away.

## The rule the window rests on

**Losing a cache costs recomputation, never work.** That single rule is what
makes a window with a Remove button in it safe to offer at all, and it is what
decides membership: a thing belongs here only if the worst outcome of deleting
it is waiting.

So the window lists four kinds:

* **Project** - a run's state history (the greenzone) and where this machine
  last found the project's files. Losing it means the run replays instead of
  resuming.
* **Unpacked core** - a `.chimeraCore` unzipped so it can be loaded. Losing it
  means unzipping it again.
* **Compiled code** - a core's translation of a game's code
  (`docs/compile-cache.md`). Losing it means minutes on the next first boot.
* **Core versions** - what each core repository last said it had published.
  Losing it means the Core Manager asks again.

And it lists nothing else. Installed cores are the Core Manager's, because a
movie needs the exact build that recorded it; projects, roms and firmware are
not caches at all. A window that mixed those in would be a window where the
rule stops being true, and then no row in it is safe.

The one exception the window enforces itself: **what is open cannot be
removed**. Pulling a greenzone out from under a running session costs work, not
time, so the tick is refused rather than the removal being attempted.

## Where it all is

Everything the window lists is under the user's data directory -
`%LOCALAPPDATA%\Chimera` on Windows, `$XDG_DATA_HOME/chimera` (default
`~/.local/share/chimera`) elsewhere, or `CHIMERA_DATA_HOME` where that is set:

| | |
| --- | --- |
| `Projects/<id>/` | one run's greenzone and what its project is called |
| `UnpackedCores/<name>-<sha1>/` | a `.chimeraCore` unzipped so it can be loaded |
| `CompiledCode/<core>/<version>/` | what cores compiled for a game |
| `Cores/.feed-cache/` | what each core repository last said it published |
| `cache-locks.json` | which entries the auto-clean may not take |

**Nothing cached goes in the install directory.** A Chimera bundle is a zip
somebody unpacks, and updating it means unpacking a newer one - anything that
grows inside it is lost on every update, and makes the size of the install
depend on what has been played. The bundle should be exactly what was
downloaded.

Unpacked cores and compiled code used to share one directory, `<exe>/CoreCache`,
which was wrong twice over: it was inside the install, and one directory holding
two layouts cannot be read back without guessing - a survey walking it for
packages found the compiled-code directories too and listed each core's name as
an unpacked package. They are two roots now, and an older install's `CoreCache`
is moved into them on the first run that finds it (`CacheStore.AdoptLegacy`).
Moved rather than deleted: it is all regenerable, but a PS3 game's compiled code
is an hour of somebody's evening and a rename is free.

## The limit

A cache that grows without bound is a disk that fills up while somebody is
working. So there is a limit - **100 GB by default, on by default** - and when
the cache is over it the oldest entries go until it is under again.

A hundred gigabytes because a single PS2 or PS3 greenzone runs to tens of
them. A smaller default would spend its life evicting the run being worked on,
which is a policy that looks like a fault.

**Oldest first**, by when anything in the entry was last written. It is the one
ordering that approximates "least likely to be wanted next", and it is
occasionally exactly wrong - which is what locks are for.

It runs where a cache has just stopped being needed, not on a timer:

* at startup, after the window is up (it walks every cache directory, and a
  frontend that sat on a black screen counting bytes would look broken);
* when a project closes, which is both the moment its greenzone stops being
  untouchable and the moment the cache has just grown by whatever the session
  added;
* when the Cache Manager closes, so that a limit somebody has just lowered
  means something before the next project ends.

Never while a run is open. Deleting somebody's disk space in the middle of
their frame advance is not housekeeping.

## The disk floor

**The limit bounds Chimera; it does not bound the machine.** A hundred-gigabyte
ceiling on a small SSD is no promise at all, and a long run can reach the end of
the disk with the cache at three gigabytes - the two failures are unrelated. So
the limit that actually applies is the **smaller** of two numbers: what the
setting asks for, and what leaves the disk **20 GB free**
(`Config.CacheFreeSpaceFloorMb`).

Twenty because the floor has to survive one more session of whatever is running:
a console greenzone grows by tens of gigabytes in an afternoon, and a floor that
only just holds today is gone tomorrow.

A machine that will not say how much is free counts as having plenty. An unknown
answer must never be read as "none left", which would empty the cache on a
filesystem nobody could measure.

When the disk is what set the limit, the window says so instead of showing a
number that disagrees with the box beside it.

## The greenzone budgets

The window also owns what a greenzone MAY weigh, which is the other end of the
same question as what the cache does weigh - and the greenzone is what fills the
cache in the first place. Two numbers, because a history costs two things:

* what it may hold in **memory** (4GB), and
* what the spill file that overflow produces may weigh on **disk** (10GB; 0 is
  no limit).

The disk number is never below the memory one, and raising memory raises disk
with it: memory fills first and its overflow is what goes to disk, so a smaller
disk budget is a file that is full the moment memory is. Both are defaults, and
either can be set for **one project** - kept beside that project's greenzone
rather than in its `.chimeraProject`, because a budget is a fact about the
machine the work is being done on and the project file is the one thing that
gets handed to somebody else.

Neither is a reservation, and the memory one is not a promise the machine has to
keep: running out halves it and carries on (docs/state-manager.md).

## What it will never take

Three things, for three different reasons:

* **What is open.** Removing it costs work rather than time.
* **What is locked.** Somebody said so.
* **The newest entry**, whoever asks. This is the durable half of "do not evict
  the work of the last ten minutes": a greenzone big enough to break the limit
  on its own is also, once everything older has gone, the oldest thing left, and
  closing a run must not be how it gets deleted. It also means the cache can
  never empty itself - one run that breaks the limit alone is something to
  **say**, not something to delete.

The frontend additionally spares, for that one pass, the project it has just
closed. The newest rule covers that case on its own almost always; the explicit
spare covers the almost.

## When it cannot get under

Then it says so, which is the whole of the answer. `CacheCleanResult` reports
what is holding it apart, because the two kinds are not the same problem:

* **held by locks** - waits for a person. Said on screen, once a session,
  because it will be just as true at the next close.
* **held by what is open** - resolves itself when the project closes and the
  next pass runs. Logged, never announced: telling somebody their open project
  is in the way is telling them off for working.
* **held by the newest** - the rule above, working.

Everything goes to the log either way. The one thing that must not happen is
what used to: the cache quietly staying over a limit somebody set, with the
answer computed and thrown away.

## Locks

A lock says *the auto-clean may not take this one*. It says nothing else: a
locked entry is still removed by Remove, because ticking a row and pressing a
button is not something anybody does by accident. A padlock that argued with a
deliberate press would be a lock on the wrong thing.

The defaults follow which way round the mistake would matter:

| Kind | Starts | Why |
| --- | --- | --- |
| Project (greenzone) | **unlocked** | it is the room, and the thing a limit exists to bound |
| Unpacked core | locked | small; evicting it frees nothing and stalls the next boot |
| Compiled code | locked | same |
| Core versions | locked | same |

So the limit falls where the room actually goes, and the furniture stays put
unless somebody says otherwise.

Locks live in `cache-locks.json` in the data home - beside the caches, never
inside them, because an unpacked core directory has to stay exactly what its
package said it was. They are keyed by location, which is machine-local, which
is what the caches themselves are. **Only deliberate exceptions are written
down**: an entry that matches its kind's default is absent from the file, so
changing a default later takes effect for everything nobody has overruled, and
the book stays a short list rather than a second copy of the cache. An entry
that is removed takes its lock with it, or a location reused later would
inherit an answer nobody gave about it.

One file with one writer. Two Chimeras open at once could talk over each other
and the later save would win, which is a lost padlock and not a lost run - not
worth a lock file to prevent.

## The window

`Tools > Cache Manager`. Two kinds of act, the same division the Core Manager
draws:

* **Ticking** is for doing the same thing to several rows: Remove Ticked, Lock
  / Unlock, Select all orphans.
* **Selecting** is for looking closely at one: Open Folder, and the detail
  lines under the list.

Lock / Unlock is one button rather than two. What it will do is visible in the
padlocks it is pointed at, and the mixed case has an obvious right answer -
somebody who ticks a locked row and an unlocked one and presses it meant to
keep both.

Clean Now applies the limit by hand, and asks first, because it is a press
rather than a rule and it names things nobody ticked. Changing the limit itself
removes nothing: a number being typed passes through 1 on its way to 100, and a
window that emptied the cache mid-keystroke is one nobody would dare open.

**Orphans** are caches whose project file is not where it was last seen -
deleted, or moved and not opened since. They are perfectly good caches; they
are simply the ones nothing is asking for, which makes "select all orphans" the
one selection worth making on somebody's behalf. A project that is OPEN is
never called an orphan, whatever the note says: it was opened from somewhere.

## Where the code is

* `CacheSurvey` - what is listed, what each row costs, what the auto-clean
  would take and in what order. No UI, so all of it is tested without one.
* `CacheLocks` - the lock book and the per-kind defaults.
* `CacheStore` - where the unpacked cores and the compiled code live, and the
  one-time move out of the install directory.
* `ProjectCache` - the per-project directories, the data home they all hang
  under, and what the window shows instead of sixteen hex digits.
* `CacheManagerForm` - arranges the above. Thin, like the firmware windows are
  over their surveys.
* `Config.CacheAutoClean`, `Config.CacheSizeLimitMb` - the setting. In
  megabytes, like `MovieConfig.GreenzoneBudgetMb`, because that is the unit the
  config file keeps sizes in.
