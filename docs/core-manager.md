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
will drift, so the publish job lives once as a reusable workflow and each
core's `chimera.yml` calls it.

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

Download to a temporary file, hash it, compare against the release's own
digest, then rename into place. GitHub asset URLs redirect, so the client
follows redirects. The SHA1 of the package file is the identity Chimera already
uses everywhere - the cache directory, the movie header - so verification and
identification are the same act.

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
that breaks the generic waterbox adapter would reach users unseen. The matrix
leg closes that: for each core, shallow-checkout its repository (for its tests,
not its sources), download its published package, and run *its own*
`waterbox/tests/run-frontend.sh` against the Chimera under test. No core is
compiled; the packages are cached by SHA1.

It runs in full on main and nightly, over a subset on pull requests, and the
workflow carries a quarantine list so one core that published something broken
cannot block every merge while it is being fixed.

## Licences

`tools/bundle-licenses.py` computes `LICENSES.md` from the packages in the
bundle. A bare bundle carries only the frontend's terms - but the obligations
are real the moment a core is installed: several cores forbid commercial
redistribution, and others are GPL and require identifiable corresponding
source.

So the terms move to install time. The manager shows what a package's licence
demands before it is downloaded, and the licence view is computed from what is
installed rather than from what shipped.
