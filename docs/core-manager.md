# The core manager

How cores reach a Chimera install, once the frontend stops shipping them.

Chimera used to be one repository that pinned fifteen core submodules, built
them all, and shipped the result as one bundle. That had a real property -
*one chimera commit pins one exact bundle* - and it stopped scaling: the
bundle got large, the release took hours, and bumping any core rebuilt the
world.

The new model: **each core repository builds, packages and publishes itself;
Chimera ships bare and knows how to go and get them.**

## What a core release is

Each core repository publishes to its own GitHub releases, on the same two
kinds of build the frontend already uses (see `.github/workflows/release.yml`):

* **dev** - a rolling prerelease, replaced on every green push to main. One
  link that is always newest.
* **nightly** - an immutable dated prerelease, `nightly-YYYY-MM-DD`, published
  only when main moved. Never deleted. This is the archive a movie replays
  against in five years.

One asset per release: `<coreid>-<version>.chimeraCore`. The version is the
commit the build was made from, which is what the package already stamps into
`waterbox.config` and what a movie already cites (`CoreVersion`).

Fifteen repositories publishing identically is fifteen copies of one job that
will drift, so the logic lives once, here, in two files:

* `tools/publish-core.sh` - reads the version **out of the package**, refuses a
  hand-built one, names the asset, and creates or moves the release.
* `.github/workflows/publish-core.yml` - a `workflow_call` wrapper that
  downloads the gated artifact and runs that script.

A core's own workflow adds one job:

```yaml
  publish:
    needs: [ core-gate, frontend-gate ]
    if: github.event_name != 'pull_request'
    permissions: { contents: write }
    uses: ToolAssisted-run/chimera/.github/workflows/publish-core.yml@main
    with:
      core-id: gpgx
      artifact: gpgx-${{ github.sha }}
      package: gpgx.chimeraCore
```

plus a daily `schedule:` trigger, which is what makes a nightly. A push to main
publishes `dev`; a scheduled run publishes `nightly-YYYY-MM-DD`, and only if
main moved since the last one. Nothing publishes from a pull request, and
nothing publishes that the gates did not pass, because `needs:` is what got it
there.

**The version is read out of the package, never passed in.** It is what the
build stamped into `waterbox.config`, it is what a movie cites, and it is what
the manager checks the download against; anything else is a way for a release
and its package to disagree. A version carrying `+local` or `-dirty` is refused
outright - a hand-built package is nobody else's build, and publishing one would
put a version nothing can reproduce into somebody's movie header.

The `dev` tag is moved by **deleting and recreating the release through the
API**, never by pushing a tag. A workflow's token may not create or update
workflow files, and pushing a tag at a commit whose `.github/workflows` differ
from the default branch's counts as exactly that. The frontend's own pipeline
learned this the hard way; the same comment is in `publish-core.sh`.

### Which cores publish

All fifteen. Ten already had a `chimera.yml` and gained the `publish` job; five
(dosbox-x, eka2l1, rpcs3, xemu, ruffle) had no CI at all and got one.

What each can prove on a public runner is bounded by content, and the five split
three ways:

| | what CI proves |
|---|---|
| **dosbox-x** | the whole gate. DOSBox-X is its own content: the machine boots to a DOS prompt with no disk, and the gate builds the hard disk, floppies, iso and cue/bin it needs as it goes. Frontend gate too. |
| **ruffle** | the whole gate, against upstream Ruffle's own test suite (`extern/ruffle/tests/tests/swfs`), which is free to distribute. |
| **eka2l1** | upstream's 194-case suite, the ARM interpreter against a golden model and dynarmic, determinism, the clock, and native == sandbox. The legs wanting a phone ROM or a game report SKIP. |
| **rpcs3** | starts, deterministic, savestates, native == sandbox over the small PPC programs in `tests/`. Firmware and disc legs report SKIP. |
| **xemu** | that both flavors build and the guest is sandbox-clean. An Xbox has no HLE bios, so nothing here executes an instruction. |

That last one is a smaller claim than the others, and it is the claim that can
be made honestly. It is also most of what actually breaks: a qemu that no longer
builds against the guest toolchain, and a package the frontend cannot read.

Every one of the five also runs **Chimera's own contract tests against the
package it just built** (`InstalledCorePackagesTests`, with `CHIMERA_CORES_DIR`
pointing at it). For eka2l1, xemu and rpcs3 that IS the frontend half - so those
three are one job rather than two, because splitting them would build the
package twice on two runners for no more proof.

Those tests open a package through the engine, which is `libchimera` - so they
dlopen a native library and go red without one. A workflow that runs them must
build Chimera's natives first. (Found by moving `build/dll` aside and watching
them fail; it would otherwise have been three first-run failures.)

rpcs3 does not build its native reference in CI. That needs LLVM and ffmpeg for
the host on top of `rpcs3_emu` twice - hours beyond the guest build, which is
already the most expensive here - and without firmware it would prove nothing
the guest build does not. `native == sandbox` for that core is run by hand.

What was verified locally rather than assumed: dosbox-x's core gate (18 legs)
and frontend gate (4) both run green with nothing provisioned, and eka2l1's gate
reports 7 pass, 0 fail, 1 skip - the upstream suite's 194 cases, the CPU
difftest's 5508 programs, determinism, the clock, and native == sandbox. ruffle,
xemu and rpcs3 are built from their own scripts but their first CI run is their
first run.

## What Chimera ships

**The roster** - `official-cores.json`, beside the executable, copied into the
bundle by `tools/build-bundle.sh`. For each official core: its id (which is
also the base name of its published asset and of the file in the store), its
display name, the systems it emulates, its `owner/repo`, and the version this
Chimera release's CI matrix passed against. An empty `tested` means the matrix
has not run against that core yet, which is where every core starts; the
manager then offers the newest of the chosen channel.

A missing or malformed roster is an EMPTY roster, never an error: a Chimera
that lost the file should still run every core already installed and simply
say it knows of none to fetch.

The roster is what lets the manager show you that a core *exists* before you
have it, and it is what the first-run offer installs. It is not a catalogue of
versions: versions come from GitHub, on demand.

**No cores.** A fresh bundle has an empty store.

The manager is **File > Core Manager**, and it opens by itself when there is no
core installed at all. That is the one moment where Chimera cannot do anything
useful without help, so it is the one moment worth interrupting: the window
comes up with the roster's tested versions pre-ticked. Once one core exists it
never opens itself again.

## How versions are discovered

Nothing is fetched until the user presses something. There is no background
polling and no phoning home at startup.

* **Download latest** (per core) - one request to
  `GET /repos/<owner>/<repo>/releases`, take the newest of the chosen channel,
  download its asset.
* **Check for updates** - the same request for each *installed* core, and the
  answer is a badge on the Cores menu, not a modal.
* **Download all** - the roster, in one go.

A release is a version only if it is published (not a draft) and carries an
asset for this core. A repository can attach more than one package, and
`quickernes` is a prefix of `quickerneshawk`, so an asset counts as this core's
only when its name is the core's id exactly or the id followed by a hyphen.

The default channel is **nightly**, for the reason nightlies exist: they are
immutable and never deleted, so a movie recorded against one stays replayable.
Dev is one click away per core for somebody chasing a fix.

Being unable to answer is an ordinary answer, not an exception: no network, a
repository that has gone, and a rate limit all come back as a message the
manager shows, with whatever the cache still knows alongside it. An offline
Chimera opens the manager and lists what it saw last time.

Unauthenticated GitHub allows 60 requests an hour per IP. A check over the
cores someone actually has is a handful; the roster in full is about fifteen.
Release asset downloads do not count against that limit. Responses are cached
by ETag so a repeated check costs nothing, a 403 says *rate limited, try again
in N minutes* rather than failing silently, and an optional token in the config
serves anyone who hits the limit for real.

## Where cores live

    <bundle>/Cores/            scanned first; whatever somebody put there by hand
    <store>/                   what the manager downloads, one file per version

The store (`CoreStore.Path`) is per-user, not part of the bundle:

| | |
|---|---|
| `CHIMERA_DATA_HOME` set | `$CHIMERA_DATA_HOME/Cores` |
| Windows | `%LOCALAPPDATA%\Chimera\Cores` |
| elsewhere | `$XDG_DATA_HOME/chimera/Cores`, default `~/.local/share/chimera/Cores` |

A Chimera bundle is a zip somebody unpacks, and updating it means unpacking a
newer one. Cores inside the bundle would have to be downloaded again every time
the frontend moved, which for a fifteen-core install is unreasonable - so they
live outside it and outlive any number of Chimeras. `CHIMERA_DATA_HOME` is the
escape hatch for a genuinely portable install that wants everything under one
root; Chimera already uses that variable for the rest of its user data.

The bundle's own `Cores/` is scanned **first**, so a portable install that
carries its own cores wins over whatever else the machine has lying around. The
two collapse into one entry when `CHIMERA_DATA_HOME` points the store back at
the bundle.

### One file per version, and no version is ever replaced

A package in the store is named `<coreid>-<version>.chimeraCore`. Two versions
of one core are two files sitting side by side, and **installing a new version
never removes an older one**: an old build is the only way to replay a movie
recorded on it, so throwing it away to save a few megabytes would be throwing
away the run. Versions go only when the user removes them, one at a time, and
the manager says which movies in the recent list would lose their core.

The version string comes from a git tag, so it is sanitised down to name-safe
characters before it becomes a file name. That is only a NAME - the package's
identity is still the SHA1 of its bytes, which is what discovery, the extract
cache and the movie header all use.

Writing into the store replaces a file only when the name matches exactly, i.e.
the same core at the same version. That is idempotent rather than destructive,
and it is how a truncated file from an interrupted download gets fixed.

### Picking a version

The manager shows one row per core, **newest version first**, with the
installed ones marked. The selector is per core: the roster's tested build, the
newest of the chosen channel, and every nightly still published, in date order,
so choosing an older one is always one click and never requires knowing a tag.

Discovery already lists every package in a directory separately, collapses
duplicates by SHA1, and reads each one's version out of its config, so *picking
a version* is nothing more than choosing which of the listed entries to open.

**One version of a core per session.** `CoreRegistry.Register` keys on the core
name and has no unregister, so installing a new core mid-session works (that is
the point of discovery being separate from loading), but switching to a
different build of a core already loaded takes a restart. The manager says so
rather than appearing to do nothing.

## Verifying a download

Everything arrives over HTTPS from github.com, and that is what says the bytes
came from the right place. What the installer adds is the check that they are
what they claimed to **be**, in this order:

1. the transfer completed - the body is as long as `Content-Length` said;
2. the digest matches, where GitHub published one (`sha256:...` on the asset;
   older releases carry none, and an unknown algorithm is not grounds to refuse
   a file HTTPS already vouched for);
3. discovery can read it as a core package at all;
4. its guest ABI is one this Chimera runs - refused here rather than at the
   moment somebody tries to emulate with it;
5. the version stamped inside the package matches the version the release
   offered. A mismatch means something upstream attached the wrong asset, and
   filing it in the store under a version it is not would put a lie in every
   movie recorded against it.

Only then is the temporary file moved into the store. Nothing is ever written
into the store under a name it has not been verified to deserve, and a failed
install leaves no trace.

GitHub asset URLs redirect to object storage, so the client follows redirects.
The SHA1 of the package file remains the identity Chimera uses everywhere - the
extract cache, the movie header - so verification and identification stay the
same act.

## The window

**File > Core Manager.** One list of cores on the left, one core's versions on
the right.

The left list is every core in the roster plus anything installed that the
roster does not know about - somebody's own build, or a core from elsewhere.
Those are shown, greyed as unofficial, rather than hidden: a window that listed
only what it could fetch would leave somebody unable to see the core they are
actually running.

Every row has a **tick box**, and a **Select all** above the list that says how
many are ticked. Three buttons act on what is ticked, and stay unavailable until
something is:

* **Check for updates** - asks each ticked core's repository and **downloads
  nothing**. It marks the rows that have something newer and names them.
  Deciding to take an update is a separate act.
* **Download latest** - installs the newest published build of each ticked core
  that has not got it. One already holding the newest is left alone rather than
  downloaded again.
* **Remove** - deletes **every installed version** of each ticked core, behind a
  confirmation that says what it costs. An official core keeps its row and goes
  back to reading "not installed"; it can always be fetched again. An external
  one is forgotten entirely.

The right-hand panel still acts on the row you have *selected* rather than
ticked: pick a particular version, **Install** it, or **Remove version** to
delete just that one. Ticking is for doing the same thing to several cores;
selecting is for looking closely at one.

### External cores

**Add external core...** takes the address of a GitHub page - the one you are
looking at, with or without scheme, trailing slash, `.git`, or a deeper path
like `/releases`; a bare `owner/repo` works too. The repository is asked what it
publishes *before* it is remembered, so a wrong address fails there rather than
becoming a row that can never do anything. The core's id and name come from the
newest published asset, since nothing else about it is known here.

Added cores live in the config (`ExternalCores`) and are listed **below the
official ones, under a separator**. Removing one takes it out of the list
entirely, because nothing else was keeping it there. Adding a repository the
roster already carries is dropped rather than listed twice.

The separator is a row rather than a `ListViewGroup`: Mono's ListView ignores
groups in Details view. It carries a tick box it will not let you tick - a row
in a checkbox list has one whether it wants it or not - and it is told apart
from a real row by having no `Tag`, which is also how the list maps rows to
cores now that the indices no longer line up.

An **update** is the newest published version, and only when it is missing.
Any older version that happens not to be installed is not an update - somebody
holding the latest build would otherwise be told forever that there is
something newer, naming a version from last month. A development build is never
an update to a published one.

Development builds are hidden behind a checkbox. A dev release is replaced on
every push, so a movie recorded against one can stop being fetchable; anyone
chasing a fix can still tick the box and take it.

`GitHubToken` in the config raises the request limit for somebody who shares an
address with enough other people to hit it. Nothing else in Chimera uses it and
it is never sent anywhere but api.github.com.

## What the manager shows

Per core: its name, the systems it emulates, and what is installed.

Per version, in the selector: **the publication date and the short commit**,
because those are the two things somebody comparing two builds actually needs.
A core's version IS the commit it was built from, so eight characters of it is
the same identifier the rest of the frontend shows.

A package built by hand rather than published carries `+local` (and `-dirty`
where the tree was not clean) in its stamped version. That is worth knowing -
a local build is nobody else's build, so a movie made on it is replayable only
by whoever made it - and not worth spelling out in full every time the package
is named. So it reads as one trailing word: a published core is `4ed35321`, a
hand-built one is `12d65377 local`. This applies wherever a core is named,
including the project wizard's core picker.

## What replaces "one commit pins one bundle"

Two things, and between them they are stronger than what was lost:

* **The roster's tested versions.** A Chimera release names the core versions
  its CI matrix passed against. Installing that set reproduces a combination
  somebody actually tested.
* **Movies name their core exactly.** A movie header already carries
  `CoreVersion` and `CorePackageSHA1`. The manager can fetch *that* version
  from the nightly archive, which is a better answer than "find the old
  bundle".

## Guarding the guest ABI

While the frontend pinned the cores, a core could never meet a Chimera that did
not understand it. Decoupled, it can.

So a package declares the guest ABI it was built against, `abi` in
`waterbox.config`, and the frontend declares the range it accepts
(`GuestAbi.Current` and `GuestAbi.MinimumSupported`). A package outside that
range is listed and refused with a reason - *built for a newer Chimera* -
rather than crashing somewhere inside the sandbox. A package with no `abi` at
all is ABI 1: everything published before this field existed.

The core declares it by hand, next to `systemId` and `memoryLayoutMiB`, rather
than having the build stamp it. A stamp would have to read the number out of
whichever Chimera checkout happened to be packaging, which is a way to be
confidently wrong; and the number changes about as often as a core's memory
layout does - when it does change, that core needs real work anyway.

Bump the ABI when the guest contract changes in a way an existing `core.wbx`
cannot satisfy - a new required export, a changed signature, a changed meaning.
Adding an OPTIONAL export (the tooling groups work this way) is not a bump: a
core that lacks it is detected and does without.

## Keeping the frontend honest

Nothing in Chimera's own CI builds a real core any more, so a frontend change
could break the generic waterbox adapter for every core at once and nothing
here would notice - it would surface the first time somebody downloaded one.

The `published-cores` job closes that. `tools/fetch-cores.sh` downloads what
each core last published into `build/Cores`, and the tests in
`InstalledCorePackagesTests` run against the real packages:

* every package can be read by discovery;
* every package's guest ABI is one this build runs;
* every package becomes a working `WaterboxCoreFactory` - which is where a
  package's machines, settings and controller are validated against each other,
  and so the real test of whether the frontend still understands what the cores
  are saying;
* every package's default keybinds name only buttons its controller declares;
* every package stamps a version, without which it cannot be filed in the
  store or cited by a movie.

`MnemonicUniquenessTests` runs against them too, so a controller that grows a
button is covered the day it does.

No rom, no firmware, no emulation, no core build: this is the CONTRACT, and it
takes seconds. **Whether the emulation is still right is each core
repository's own gate**, which checks out Chimera's main and replays real
movies against it - the same check from the other side, run by the repository
that has the content to run it.

A core that has published nothing is skipped rather than failing the job: the
cores gain their release pipeline one at a time, and a frontend checkout is not
broken by a core that has not caught up.

Both halves read the same directory, so `tools/fetch-cores.sh` is also the
quickest way to get a working set of cores into a fresh checkout by hand.
`CHIMERA_CORES_DIR` points the tests somewhere else where a job needs it.

## Licences

This used to be a build-time question. The bundle carried every core, and
`tools/bundle-licenses.py` computed one `LICENSES.md` from what they declared -
which is how the release notes came to say, correctly, that the whole
distribution was non-commercial.

A bare bundle is not. `LICENSES.md` now states the frontend's own terms and says
plainly that **installing a core changes them**: several cores (Genesis Plus GX,
Opera, Snes9x) forbid commercial use and that binds whatever they are installed
into, while others are GPL and require their corresponding source to stay
identifiable.

So the terms move to install time. Every package carries
`licenses/licenses.json`, put there at package time; `CoreLicence` reads it and
the manager shows what an installed core demands - commercial use first when it
is forbidden, because that is the part that binds everything around it - rather
than leaving somebody to open the zip.

`bundle-licenses.py` still reads `Cores/`, because a bundle assembled WITH
packages (a developer's, a downstream packager's) must still state their terms.

## What the frontend no longer carries

`extern/cores/*` is gone: out of the index, out of `.gitmodules`, and off the
disk. The checkouts were **moved**, not deleted - each became a standalone
repository under `~/chimera-cores/<name>`, keeping every local commit, which
mattered because all fifteen were carrying unpushed work at the time.

Moving a submodule checkout out of its superproject is not a `mv`. Its `.git` is
a FILE pointing into `<super>/.git/modules/`, its config carries a `core.worktree`
pointing back, and every nested submodule has the same problem one level deeper -
so `tools/detach-core-checkout.sh` moves the git directory in beside the tree,
strips `core.worktree` as text (git chdirs to it before it will do anything, so
it cannot unset the line that is wrong), and repoints the nested `.git` files.
There were 243 of those across the sixteen; dolphin alone has 38, and they nest
their `modules/` directories, so a submodule at `a/b` inside one at `a` lives at
`.git/modules/a/modules/b`. Deriving each new path from the old one is what makes
that rule the repository's problem rather than the script's.

Chimera's own checkout went from about 26 GB to 4.9 GB. The moved repositories
still work from where they landed: a core's `build-package.sh` looks for
`../chimera` and then `$HOME/chimera`, so it still finds the frontend. Verified
by running dosbox-x's gate from its new home.

With `extern/cores` went:

* `build_core` and `--skip-cores` from `tools/build-bundle.sh`, and the core
  hashes from `BUILD.txt`;
* the core pin file, the core build cache and the PS3 core's LLVM cache from
  `release.yml` - which is most of what made a release take hours;
* the "archive every distinct core package" step. The
  [`cores`](https://github.com/ToolAssisted-run/chimera/releases/tag/cores)
  release stays, because movies recorded before the split cite packages in it,
  and it no longer grows: each core archives its own nightlies now;
* `submodules: recursive` from both workflows' checkouts. They check out
  `extern/tools` explicitly, which is what the frontend is actually built from.

A bare Linux bundle is 117 MB, nearly all of it ffmpeg and the native
libraries.
