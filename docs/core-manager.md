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

**The roster** - `cores.json`, beside the executable. For each official core:
its id, display name, the systems it emulates, its `owner/repo`, and the
version this Chimera release's CI matrix passed against.

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

    <bundle>/Cores/            shipped and portable; read-only, scanned first
    <user data>/Cores/         the store the manager owns, one file per version

The per-user store is `%LOCALAPPDATA%/Chimera/Cores` on Windows and
`~/.local/share/chimera/Cores` on Linux. A bundle in Program Files is not
writable, and a downloaded core must survive replacing the bundle.

Versions sit side by side as flat files, `<coreid>-<version>.chimeraCore`.
Discovery already lists several packages from one directory, collapses
duplicates by SHA1, and knows each one's version, so *picking a version* is
just choosing which of the listed entries to open.

**One version of a core per session.** `CoreRegistry.Register` keys on the core
name and has no unregister, so installing a new core mid-session works (that is
the point of discovery being separate from loading), but switching to a
different build of a core already loaded takes a restart.

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
