# The Chimera project: TAS-only refocus

Decided with the user 2026-08-26. Chimera is a TAS tool, not a frontend for
casual gaming. There is exactly one use case: open a project, work in
TAStudio. Everything below follows from that.

## The entry point

Chimera's entry point is not "load core" nor "load rom": it is the project.
The GUI opens on a start screen - New Chimera Project, Open Project, recent
projects - and nothing else. Creating or opening a project leads directly
into TAStudio; TAStudio is no longer a tool among others, it is the
application. The casual paths are fully removed: no Load ROM menu, no
runtime Emulator > Core picker, no live settings dialogs. Core
development and debugging needs are covered headlessly by chimera-run,
which learns to take a project file.

## What a project is

A single JSON file (extension `.chimeraProject`) that contains literally
everything required to reproduce the work, except the data bytes
themselves, which are named by their SHA1:

- **Core identity**: package name, version, hash - pinned at creation.
- **File manifest**: names + SHA1 + the core-defined slot id each file
  fills (cdrom, floppy, hdd, config, ...), order within a slot = swap
  order, cue-referenced bins auto-added as support, cue closure enforced.
  The generic image/support/savedata role triple retires; opening
  validates the manifest against the core's slot declaration. The
  standalone `.chimeraMultiFile` format does not survive as a GUI-facing
  concept; the engine's `ce_multifile_*` code evolves into this section,
  and headless descriptors remain available during the transition.
- **Firmware**: resolved at creation, pinned by hash.
- **Compiled code**: for a core that translates the machine's code before
  running it, the list of objects it compiled for this game - names and
  SHA1s, never the files, which regenerate from the game and the package
  (docs/compile-cache.md). The wizard will not create a project until they
  all exist.
- **Settings**: defined once, in the creation wizard. There is ONE kind
  of setting: every setting shapes the machine, so all of them are part
  of the reproduction contract. (The BizHawk lineage's "settings"
  vs "non-settings" split does not exist here; a knob that cannot
  desync a run is not worth declaring.)
- **The TAS work itself**: input log, markers, branches. The project IS
  the movie; publishing a finished TAS means handing over the project
  file, and anyone holding files matching the SHA1s can reproduce
  everything.

Not in the project:

- **Paths.** Never stored. Only names and SHA1s. A game that was dumped as a
  FOLDER therefore cannot be named at all - a directory has no SHA1 - and has
  to become one file first, reproducibly, so that everyone who packs it gets
  the same hash (docs/media-maker.md).
- **Greenzone / cached states.** Kept in the per-user cache, keyed by the
  project's id (docs/state-manager.md), not beside the project: a project
  folder is the folder people sync, and a multi-gigabyte sibling there is
  uploaded again on every save. If present it is loaded, otherwise the
  session starts from a clean state-memory slate. A greenzone left beside
  an older project is moved into the cache the first time it is opened.
  Losing it costs recomputation, never work. It records
  which machine made its states (core build, settings, firmware pins, file
  hashes) and is used only when the project still describes that machine:
  a savestate is the memory of one exact machine, and the sandbox checks
  only the core binary when it loads one.
- **Per-user preferences.** Keybinds, window layout, hotkeys stay in user
  config; the project holds only what affects sync and reproduction.

## Creation

One wizard, everything at once. Step one asks project title, emulation
core, and project description. Step two asks for the files - and it is
NOT a blind "add any files" dialog: it is a fully core-informed form.
The core declares categories (CD-ROMs, floppy disks, hard disk,
configuration files, save states, ...), each rendered as empty slots with
a cardinality (exactly 1, 0-N, 1+), the accepted formats, and a small
circled question-mark tooltip explaining what is expected there and in
what format. The form's content is fully decided, populated and formatted
by each core; Chimera only provides the interface and the execution. Files
are hashed as they are added, and order within a category is the swap
order. The form ADAPTS within the step: a slot may carry "exposedWhen"
over the current picks, so providing a Famicom disk greys the cartridge
slot until the disk is unloaded and vice versa - greyed, never hidden,
so the person still sees what the machine could also take. An
unavailable slot's minimum does not bind, which is how two mutually
exclusive min-1 slots together say "exactly one of either". Then the settings - themselves GATED by the files just chosen: a
declaration entry may carry "exposedWhen" (the same condition language
as firmware), so a Game Gear cart exposes its sound chip where a
Genesis cart exposes its own, a Famicom disk exposes the FDS BIOS
choice where a cartridge shows nothing, and a setting may gate further
settings (re-evaluated live as values change). Same-name variants are
separate entries with disjoint conditions, exactly like firmware. The
project records every EXPOSED sync setting explicitly at its effective
value - the settings section is exactly the knobs the decisions
offered, nothing else. And LAST, the firmware - last because everything
before it has a say: the core itself may need a file regardless of
anything else, the chosen game files may trigger a need (a Famicom disk
needs the FDS bios where a cartridge does not; a CD image needs the CD
bios where a cart does not), and the settings may too (a region
choice selects between region bioses; a font-source choice turns a
bundled freebie into a real-BIOS requirement). The core dictates that
logic entirely, as a decision tree in its waterbox.config "firmware"
declaration; the wizard only renders the result and collects the files.
On create, TAStudio opens at frame 0.

### One core, several machines

A package need not be one machine. Genesis Plus GX is a Mega Drive, a Master
System, a Game Gear and an SG-1000 - the same `core.wbx` with different
hardware selected - so its `waterbox.config` declares a `machines` array and
names the setting that picks between them (`machineSetting`). Each machine
carries what makes it a different machine: the system id the movie records,
the controller it has, the picture it draws, and the rom extensions that
belong to it. What they share - the binary, the guest heap, the audio rate -
stays at the top level, and a package with no `machines` is one machine
exactly as before.

The choice belongs with the core, on step one, because everything after it
depends on it: the files it takes (a Sega CD drive is not a Master System's),
the settings it has, the values those settings may take (a Mega Drive port
takes a mouse, an Activator and a Team Player; a Master System port takes a
pad or nothing - a machine may narrow a setting with `settingOverrides`), and
the firmware it wants. Because the machine IS a setting, the project pins it
and the movie cites it like every other structural choice, and the condition
language already understood it: `{"setting": "systemHardware", "is": "sms"}`
gates slots, settings and firmware alike.

Loading a plain rom with no project has no pin, so the extension decides: the
package's machines claim their own extensions, and a `.sms` opened with this
package is a Master System.

The alternative - one package per machine - was tried and is worse. It ships
copies of one binary, and since a package registers its core by name, four
packages all called "Genesis Plus GX" meant three were silently dropped when
all four were installed.

### The firmware decision tree

A firmware entry in waterbox.config may carry "requiredWhen", a
condition over the slot map and the EFFECTIVE (defaults-overlaid)
settings:

```json
{ "id": "bios_cd_us", "display": "Sega CD BIOS (US)",
  "requiredWhen": { "all": [
    { "slot": "cd" },
    { "setting": "region", "is": "us" }
  ] } }
```

- `{"slot": id}`: the slot holds at least one file;
  `{"slot": id, "extension": "cue"}`: ...a file with that extension.
- `{"setting": name, "is": v}`: the setting has that value (compared by
  its declared type); `{"setting": name, "in": [v...]}`: one of those.
- `{"all": [...]}`, `{"any": [...]}`, `{"not": {...}}` combine freely.
- A conditional entry is required when its condition holds and absent
  otherwise; an entry without a condition keeps its legacy "required"
  flag (false = shown as optional). A malformed condition evaluates
  false - a core that misdeclares asks for nothing, never for
  everything.

The engine evaluates (ce_firmware_evaluate), everywhere the question is
asked: the wizard's last page, and the core factory at load time - a
required file the user never provided stops the load with its name.

The decisions nail each requirement to ONE exact file, or to nothing.
There is no optional firmware and nothing to choose between: variants
of one file are separate declaration entries sharing an id, with
disjoint conditions a setting selects between ("FDS BIOS to Use:
Nintendo", "System Fonts: sony") - a choice that needs no firmware
simply produces no requirement ("System Fonts: bundled"). An entry
names its one dump openly: {"sha1", "name", "label"} - the SHA1 is the
identity, the name only a hint of what the file is usually called. An
entry may omit the hash only for a file no one can pin (a dump only
the user can own); the chosen file's actual hash is recorded.

The wizard's firmware page inspects the Firmware folder (a path entry)
FIRST, so a dump dropped there once satisfies wizards forever: every
plausible file is hashed and matched, and a satisfied requirement
shows its checkmark and the file in use. Config > Firmware is the same
question asked of every installed core at once, before any project: it
surveys the packages present, says what the folder already answers, and
takes a file for the rest - remembered where it lives, never copied. A per-item Select File button
covers what the folder could not - allowed even when something was
found - and only the exact file satisfies. Create stays DISABLED until
every requirement is satisfied.

The project records the chosen files as pins ("firmware": [{"id",
"sha1"}]), actual hashes as always; the frontend remembers paths
per-user in its config, never in the project. REOPENING is stricter
than creation: each pin must be matched exactly, by hash, from the
Firmware folder or the remembered paths - whichever file matches is
what the session mounts - and a pin nothing satisfies takes a severe
are-you-sure (different firmware is a different machine: expect
desync) to get past. "Plausible" is by size alone, and the bar is a
gigabyte: a PS3 system software update is 206 MB and is firmware, and
the bar it replaced (64 MiB) let no PS3 project ever find its own,
while a disc image is bigger still. That check is also the one that
does the mounting: for a while it existed but nothing called it, so a
project whose config remembered no path booted its disc with no system
software and no word about it - a machine that ran, polled nothing and
drew nothing (2026-09-03).

The declaration lives in a static file generated at core build time and
shipped in the core package - the same pattern as `waterbox.config` for
settings and `default_keybinds.json` for binds. The wizard reads it with
no guest boot, and the declaration is versioned with the core.

### The slot declaration: file_slots.json

One file per core package, beside `waterbox.config`. Read by the wizard
to build the form, and by `ce_project_*` to validate a manifest:

```json
{
  "slots": [
    {
      "id": "floppy",
      "title": "Floppy disks",
      "min": 0,
      "max": -1,
      "formats": ["img", "ima", "xdf", "fdi", "hdm", "nfd", "d88"],
      "help": "what belongs here and in which format (the tooltip text)"
    }
  ],
  "atLeastOneOf": [["floppy", "cdrom", "hdd", "conf"]]
}
```

- `id`: bare lowercase token, unique within the core. This is what a
  manifest entry records as its slot, and what mount naming derives
  from.
- `title`: the category heading the form shows.
- `min` / `max`: cardinality; `max` -1 means unbounded. Exactly one =
  1/1, zero or more = 0/-1, one or more = 1/-1.
- `formats`: accepted file extensions, lowercase without the dot; the
  wizard filters file pickers by them and validation checks membership.
  An extension may legitimately appear in several slots (`.img` is a
  floppy or a hard disk - the user's slot choice resolves what
  sniffing had to guess).
- `help`: the circled-question-mark tooltip text.
- `exposedWhen` (optional): the condition language above, evaluated over
  the CURRENT slot map (settings do not take part - the files come
  first in the wizard). The form re-evaluates after every add and
  remove, greying slots the condition rules out; `ce_project_validate`
  enforces the same rule (a file in an unexposed slot is a structural
  error), and exposure is decided before cardinality so an unexposed
  slot's `min` does not bind.
- `atLeastOneOf` (optional): groups of slot ids; across each group at
  least one file must be present, for requirements no single slot's
  `min` can express ("some bootable medium").
- `support` is a reserved id: files referenced by a listed cue are
  auto-added to the manifest with slot `support`; a core never declares
  it.

## Editing

Anything in a project can be changed after it is opened, with one
exception: the core. The core selection is fixed at creation - a
different core means a new project. A **structural** change - one that
affects sync: settings, the file manifest - first asks: "this will
erase the greenzone and possibly desync your current inputs, are you
sure?". On yes: the greenzone is cleared, the core restarts, and the
frame selector returns to frame 0. The input log is kept (it may no
longer sync; that is the user's informed call). Non-structural changes
(title, description, markers, ...) apply freely.

## Opening

Opening a project resolves its files in three steps, and only asks for
what is left: beside the project file first, then where THIS machine last
found them, then the file-resolution dialog. As each file is provided its
SHA1 is checked and divergence is warned about immediately, in the dialog.
A differing file NAME on disk is fine - the project's recorded name is the
canonical label and mount name, the hash is the identity.

### Where this machine last found the files

The project carries names and hashes and never a path, because it is meant
to be handed to someone else and a path is true of one machine only. But
the machine that made it knows where those files are, and asking it again
every time you open your own work is friction for nothing.

So the paths are remembered: `{"files": {"<name>": "<path>"}, "firmware":
{"<id>": "<path>"}}`, written whenever the project is saved and whenever
one is opened. They are never distributed, and never authority - HINTS.
Every path is checked: the file must still be there and still hash to what
the project records, and a path that now holds something else is put back
to unresolved so the dialog asks rather than mounting the wrong bytes
quietly. Losing them costs nothing but the asking, and a machine that has
never seen the project behaves exactly as before.

They live in the PER-USER CACHE, keyed by the project's id
(docs/state-manager.md), not in a file beside the project. A
`.chimeraProject` is the one file that exists as far as anyone else is
concerned: it is what gets handed over, and what a cloud folder syncs, so
nothing that can be recomputed belongs in the folder with it. The key is
the id rather than the path because a path stops being true the moment the
file is renamed, moved, or opened on another machine through a synced
folder - and because two attempts at the same game must not share a cache,
which anything derived from the contents would have got wrong. A project
written before ids existed is given one when it opens and keeps it from
its next save; a `<project>.chimeraLocal` left beside an older project is
read once, moved into the cache, and taken out of the folder.

The engine keeps each file's source path in memory (`ce_project_file_source_path`)
so a frontend can write one, and never serializes it: the distributable
file stays free of paths by construction, not by convention.

Core pin mismatch (installed package differs from the pinned
version+hash): refuse by default with a clear pinned-vs-installed message,
with a knowing override - and the project then records what actually ran,
mirroring the file-hash posture.

Opening boots the machine EXACTLY ONCE. The movie (which is the project)
is queued before the rom load, so the single boot already runs with the
project's core and its recorded settings - never a throwaway boot
on config settings followed by a reboot, and never a re-init when
TAStudio engages (the BizHawk lineage cost three full core inits: rom
load, TAStudio open, tasproj load - seconds each on a machine with a
2GB hard disk image). The witness gate's P leg counts the boots and
checks a project-recorded setting is live in the guest. Headless runs
skip the TAStudio landing (it opens paused, and nobody is there to
operate it): they just play the project.

## Architecture decisions

- **Serializer in the engine**: `ce_project_*` in libchimera reads,
  writes and validates the project (manifest, core pin, settings, input
  log, markers, branches), evolving `ce_multifile_*` and the
  engine-resident input log. chimera-run and every gate consume projects
  for free; C# only renders UI over it.
- **Movie code is absorbed**: the engine's input-log/movie code becomes
  the project's input-log section and the standalone movie format retires
  from the GUI. Headless chimera-run and the gates keep old movie files
  working during the transition, then migrate to projects.

## Status

- Decided: everything above.
- DONE: file_slots.json in all four core packages; ce_project_* in the
  engine (open/save/resolve/validate/slots map) with test_project;
  chimera-run --project (resolution, core pin, declaration validation,
  transitional rom mounts); cores consume the "slots" mount through the
  guest kit's waterbox_slots.h - DOSBox-X mounts mixed media (floppies +
  CDs + hard disk + conf, gated: the slots leg, 12/12), PPSSPP boots the
  disc slot, both NES cores read the rom slot.
- TODO: the frontend (start screen, creation wizard rendering
  file_slots.json, resolution dialog, TAStudio-as-application, removal
  of the casual paths); PPSSPP memstick-zip import and NES SRAM import
  as future savedata-consuming slots; retiring the transitional
  rom/rom.name/rom2..N mounts once nothing legacy needs them.
