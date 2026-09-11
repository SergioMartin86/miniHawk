# Chimera - Project Charter

A minimal, core-agnostic TAS frontend derived from BizHawk. Emulation cores are removed
from the tree and loaded on-the-fly as external, self-contained packages just before ROM load.

## Objectives (testable)

1. **Zero core code in the repo.** Chimera builds and runs with no emulation core in the
   solution. Cores arrive as single-file packages (`.chimeraCore`, a zip: manifest + managed adapter DLL +
   native DLL(s)), discovered from a `Cores/` directory and loaded at ROM-load time.
2. **Published core contract.** The core-facing API (essentially `Chimera.Emulation.Common`
   + `Chimera.NativeInvoke`) becomes a versioned, published contract. A core package must be
   buildable outside the Chimera repo against that contract alone.
3. **Determinism witness.** The QuickerNES regression suite (31 test movies, vendored at
   `tests/suite/` from github.com/TASEmulators/quickerNES) must resync to
   success at every phase boundary, including after the core is evicted to an external
   package. Running the full suite green at every step is paramount to conserving
   correctness. Test ROMs live at `C:\Users\sergiom\Documents\TAS\roms\nes` (file names
   match the `.test` files' `Rom File` entries; SHA1-verified). In-scope counts after
   Phase 0 triage: 28 at Level A (native), 26 at Level B (full-stack).
4. **TAS-only frontend.** Kept: TAStudio, movie record/playback, savestates, rewind, frame
   advance, virtual pads, RAM Watch/Search, Hex Editor, Cheats, Lua console (core-agnostic
   APIs only), A/V dumping + screenshots, core-agnostic debug tools (trace logger, CDL,
   generic disassembler). Everything else - core-specific config dialogs and viewers,
   movie importers, RetroAchievements, per-system Lua libs, etc. - is removed.

## Pillar: reproducibility belongs to the core, not the frontend

Chimera is NOT the keeper or enforcer of reproducibility - it REQUIRES it from
cores. A movie's reproduction contract is the pair (movie, core package): if the
selected core package is exactly the same, the movie must reproduce regardless of
Chimera's version or the versions of any frontend library (SDL, Lua, zstd, cimgui,
...). Consequences: frontend native dependencies may be upgraded freely and are
never pinned for determinism's sake (only for build reproducibility); no frontend
feature may become something a movie depends on to sync; the witness harness proves
the frontend *delivers inputs faithfully* to the core, not that the frontend owns
determinism. (User-stated pillar, 2026-08-10.)

Package identity (same day): like roms, a core package is uniquely and solely
identified by the SHA1 of its file - that hash IS the package; name, version, git
commit, and platform variant are secondary metadata. Implemented: the loader hashes
the zip at load time (directory-form dev packages are explicitly unhashed), the
extraction cache is keyed by that SHA1, the hash is shown on load, recorded into
movie headers as CorePackageSHA1, and compared on playback with a warning on
mismatch - the exact analogue of the rom-hash check.

Elaboration (same day): the pillar bites hardest at the interface points where Chimera
hands data to cores. Audit of those points and their standing:
- ROM data: RomGame is format-agnostic - the core receives the file's exact bytes, no
  header detection/stripping/per-system preprocessing (done in P3.2, and now
  load-bearing for this pillar, not just cleanliness).
- Archive extraction (zip -> rom bytes): bit-exact by the archive format's definition.
- IPS/BPS patching: a frontend transformation of rom bytes. Tolerable only because
  those formats are deterministic by spec; the implementation must stay spec-exact and
  must never grow heuristics.
- Movie input log: the mnemonic format is effectively FROZEN - its parsing is part of
  the reproduction contract and must be spec-stable across Chimera versions.
- Sync settings: opaque JSON round-tripped to the core's types; the frontend must
  never interpret or migrate them.
- Disc data [RESOLVED 2026-08-25]: Chimera.Emulation.DiscSystem is REMOVED. It parsed
  CUE/CCD/etc and synthesized sector streams in the frontend, so a frontend version
  change could alter the byte stream a disc core sees - and it silently claimed
  extensions (.iso and friends) that core packages declare, feeding the factory a
  disc object no waterbox core reads. Now disc images are roms like any other file:
  the raw bytes go to whichever package declared the extension, and all format
  interpretation happens core-side, versioned with the core (the PSP package parses
  ISO/CSO/CHD in-guest). A future CD-based core package bundles its own disc layer;
  the frontend's only disc job, if one ever appears, is gathering the files of a
  multi-file set - never transforming bytes on the emulation path.
- Firmware: database contents removed; anything a core needs ships inside its package.

## Core flavors and their reproducibility profiles

Three "flavors" of emulation core exist from Chimera's perspective (user-stated,
2026-08-11):

- **(a) Native cores** (e.g. QuickerNES): compiled per platform (Windows .dll /
  Linux .so) and run natively on the host. Their reproducibility depends on a
  good implementation of their own serialization/deserialization mechanisms.
  Windows<->Linux inter-reproducibility is desirable but not guaranteed.
  (Empirical note: the QuickerNES witness core has so far proven byte-exact
  across compilers and OSes - the 2026-08-11 Linux gate reproduced the
  Windows-recorded goldens with a Linux-gcc-built core - but that is an
  observed property of this core, not a guarantee of the flavor.)
- **(b) Pure cores** (implemented fully in C#, e.g. NesHawk in upstream
  BizHawk): must equally guarantee reproducible serialization, but are
  universally cross-compatible as long as the system runs C# and the C#
  engines are faithful (very slight chance of desync between engines). They
  require no runtime loading of precompiled native libraries.
- **(c) Waterboxed cores** (e.g. GPGX, DOSBox-X and many others in upstream
  BizHawk): use the waterbox closed system to behave as a universal Linux
  machine whose entire machine state is preserved. These offer the maximum
  level of reproducibility, but depend on the waterboxing machinery to work,
  which requires careful integration and other complications that do not
  exist in native builds.

Relation to the package contract as it stands: the current contract and the
reference package are flavor (a) (managed adapter + native library). Flavor (b)
is already expressible - a package whose manifest declares no natives. Flavor
(c) would require reintroducing the waterbox host machinery, which was removed
in Phase 2 (recoverable from the transitional fork's history) - a deliberate
open question for whenever a waterboxed core is wanted.

**Side-effect freedom (non-waterboxed cores; user-stated, 2026-08-11).** For
waterboxed cores the sandbox enforces isolation; flavors (a) and (b) must
uphold it by discipline: a core must contain NO side effects. Like the
reproducibility pillar, this rule CANNOT be enforced by Chimera - it is the
core developer's responsibility. Chimera states the requirement in the
contract documentation and trusts core packages to honor it; there is no
sandbox, no auditing machinery, and none will be added for it. Concretely: no
writing to the filesystem; no reading directly from the filesystem (ALL data -
roms, firmware, databases, assets - must arrive through the BizHawk interface);
no direct I/O of any kind (with rendering-only channels such as OpenGL as the
one permissible exception); no syscalls; nothing that causes a change in the
host system that cannot be captured and reverted by a savestate save/load.
The test is exactly that: if any observable effect of running the core is not
round-tripped by save/load state, the core is in violation. (Plain memory
allocation within the core's own lifetime is fine - it is torn down with the
core and carries no cross-run state.)
RESOLVED (same day): the QuickerNES adapter's one violation - the BootGod
cart DB reading NesCarts.xml from its package directory - was reduced to a
compiled-in PAL/Dendy hash list (PalHashList.cs, 370 SHA1s of PRG+CHR,
extracted once from the database with first-entry-wins semantics preserved
for dual-region dumps). The only behavioral use of the database was
rejecting PAL carts (the core is NTSC-only); the good-dump/name metadata it
also produced had no remaining consumers and is gone. The QuickerNES core
now reads nothing from the filesystem at runtime.

## Agreed decisions

- **Core package interface:** managed .NET adapter DLL implementing `IEmulator` (+ service
  interfaces) against the published contract, bundled with its native core DLL(s).
  Not a C ABI, not libretro.
- **Repo strategy:** trim this BizHawk checkout in place on a branch. Keep git history and
  upstream diffability. Rename to Chimera once stable.
- **Reload model:** load-once per process (net48 cannot unload assemblies). Packages load
  lazily at ROM-load time; swapping a loaded core's version requires app restart.
- **Witness core:** QuickNes (backed by the TASEmulators/quickerNES submodule) - single
  native DLL, no waterbox, no discs, no firmware. It stays in-tree until Phase 3.

## Procedure

Each phase ends with: build green -> witness movie syncs -> commit. Never more than one
phase of unverified change.

- **Phase 0 - Baseline & harness.** Build as-is. Stand up the two-level witness harness
  (below), generate golden RAM dumps from unmodified BizHawk, and confirm they agree with
  the native quickerNES tester's ground truth. No product code changes.
- **Phase 1 - Shrink to one core, statically. [DONE 2026-08-09]** All cores except
  QuickNes removed (~312k lines deleted); frontend trimmed (per-core dialogs/tools,
  RetroAchievements, movie importers, per-system Lua libs, Libretro/MAME paths, system
  menus except NES). Core asset payloads, core submodules, and native core source trees
  removed (Assets/dll: 90 -> 28 files, all frontend infra + libquicknes). Kept: Waterbox
  host + waterbox/ native tree (OPEN DECISION pending), quicknes/, SDL2 + rcheevos +
  chd/chimerahash/zstd/blip_buf natives (still referenced by frontend/DiscSystem), gamedb.
  Witness at phase close: 26/26 simple AND 26/26 rerecord, byte-identical goldens,
  after a from-clean rebuild.
- **Phase 2 - Publish the contract. [DONE 2026-08-09]** `CoreInventory`, `CoreNames`,
  and the reflection-attribute construction machinery are gone (per user decision, along
  with waterbox, the 6502 disassembler, and the trace logger - all recoverable from git
  history). In their place:
 - `ICoreFactory` + `CoreCreationContext` + `IRomAsset` in **Chimera.Emulation.Common**
   - the published core contract. A core ships a factory: name, system IDs, core type
    (stable key for persisted settings), settings/sync-settings types, and `Create(ctx)`.
 - `CoreRegistry` (Client.Common) indexes factories by system; RomLoader's
    `MakeCoreFromRegistry` replaces the old inventory path (same preferred/forced-core
    and error semantics).
 - `CorePackageLoader` (Client.Common): at startup, `<exe>/Cores/` is scanned for
    package directories or zips containing `chimera-core.json` (formatVersion 1:
    name, assembly, factoryTypes[]). Zips extract to `Cores/_cache/<name>` keyed by
    zip timestamp; the package dir is prepended to PATH for native DLL resolution;
    an AppDomain.AssemblyResolve hook serves package assemblies so persisted
    settings / movie sync-settings JSON ("Type, Assembly" names) round-trip.
 - QuickNES is registered through `BuiltInCoreFactories` (transitional; Phase 3
    replaces it with a real external package and deletes it).
  Witness at phase close: 26/26 simple AND 26/26 rerecord through the new loader.
- **Phase 3 - Evict the witness. [DONE 2026-08-09]** The QuickNES adapter now lives in
  `chimera-cores/quickernes/` - NOT part of Chimera.sln, built by `build-package.ps1`
  against the contract DLLs from `build/dll` exactly as an out-of-repo package would be,
  and shipped as `build/Cores/quickernes.chimeraCore` (manifest + Chimera.QuickerNES.dll +
  libquicknes natives). `Chimera.Emulation.Cores` is deleted from the solution entirely:
  **Chimera contains zero core code**. Supporting changes: INESPPUViewable and the NES
  palette helpers moved into Emulation.Common (frontend PPU viewers stay core-agnostic);
  BootGod NES cart DB init moved into the package factory; virtual pad schemas are
  discovered from registered factory assemblies; per-core settings dialogs replaced by
  the generic config dialog (`QuickNesConfig` and its FCEUX-palette import are gone - 
  revisit if missed); `emu.setrenderplanes` is a no-op pending a contract story; the
  Debug-only core-poking dev menu was deleted. Witness at phase close: 26/26 simple AND
  26/26 rerecord with the core loaded from the zip package.
  Addendum (same day): per user principle - Chimera must contain NOTHING core-specific
 - the Phase 3 compromises were purged too: INESPPUViewable and the NES palette code
  moved from Emulation.Common into the package; the NES PPU/nametable viewer tools, the
  NES menu, and the QuickNes icon were deleted from the frontend; NesCarts.xml moved
  from gamedb into the package zip (BootGod reads from the extracted package dir);
  NES palette files moved to chimera-cores/quickernes/palettes.
  Second addendum (deeper cleanse, same day): gamedb is GONE entirely (all per-system
  hash DBs, the Database class, DB-entry generation service). ROM->system routing is now
  purely: (1) user extension preference, (2) the extension->system map that core-package
  manifests declare ("extensions" field), (3) the platform-chooser prompt, which lists
  only systems provided by loaded packages. RomGame is format-agnostic (no header
  detection/stripping, no per-system preprocessing; hash = whole file - NOTE: game
  hashes recorded in old movies will no longer match, warning-only). Also removed: the
  FirmwareDatabase CONTENTS (mechanism kept, empty; packages could register records
  later), per-system display-name tables (system ID is the display name), per-system
  default path tables (path sets generated on demand per system), the frame-rate table
  (display fallback is a flat 60/50), the GameShark converter and all per-system
  cheat-code decoders, and the game-DB button in the log window.
  Phase 3 sharp-edge, confirmed in the wild: persisted configs and movie sync-settings
  embed `"Type, AssemblyName"` - entries written before eviction name the deleted
  `Chimera.Emulation.Cores` assembly, and Newtonsoft's `$type` binder then throws
  (silently swallowed -> defaults, first caught as two sync-settings-dependent witness
  failures). Fix: the manifest's `supersedesAssemblies` list - the resolver serves the
  package assembly under its legacy names, keeping old configs AND pre-eviction movies
  round-tripping.
  Third addendum (Assets cleanse, same day): deleted from Assets - 8 orphaned native
  libs (librcheevos.dll/.so [RetroAchievements was removed], freetype.dll, libpng16.dll,
  zlib.dll [nothing P/Invokes them and no remaining native lib imports them - verified
  by PE import-table scan], and the MinGW runtime trio libgcc_s_seh-1/libstdc++-6/
  libwinpthread-1 [only ever needed by MinGW-built cores; every remaining native uses
  msvcrt or the UCRT]); all per-system example Lua scripts (Doom/GBA/Genesis/N64/NDS/
  NES/PCE/SNES dirs); the bsnes-gamma shader (SNES color correction). N3DSHasher.cs
  (3DS-specific) deleted from Emulation.Common. defctrl.json is GONE from the frontend:
  per-system controller defaults are now package-provided - a package may ship its own
  defctrl.json (same DefaultControls shape, keyed by controller-definition name);
  CoreRegistry merges them (first package wins per name) and InputManager.SyncControls
  adopts package defaults for any controller the user's config has never seen. A
  user-saved defctrl.json next to the exe (written by the controller dialog's "save
  defaults") still wins over package defaults. The quickernes package ships the NES
  Controller bindings. [Superseded 2026-08-13, see "Key bindings arrive with the
  core": the packages lost their file in the waterbox-only redesign below, and the
  exe-side defctrl.json - along with the dialog's Save Defaults - is now gone too.] Natives KEPT deliberately: chd_capi (DiscSystem - disc story
  still an open decision), cimgui/SDL2/OpenAL32/lua54/libzstd/libchimerahash/e_sqlite3
  (frontend infrastructure). blip_buf was initially kept as contract-offered audio
  resampling, then REMOVED at user request (wrapper class, natives, C source dir,
  arm64 prebuilt): nothing in the tree used it, and a core that wants it (e.g. a
  future Gambatte package) should bundle its own copy.
  Sharp edge #2, caught by the witness harness itself: the original zip-extraction
  cache (delete stale dir, extract, write stamp file) raced when 8 Chimera instances
  launched simultaneously against a cold cache - ZipFile.ExtractToDirectory threw
  "file already exists", and since extraction ran outside the per-package try/catch it
  reached the top-level exception dialog, which on the hidden desktop blocks invisibly
  until timeout. Fix: cache dirs are keyed by zip timestamp (`<name>-<ticks>`), zips
  extract to a private temp dir followed by an atomic Directory.Move - race losers
  discard their temp and use the winner's dir; stale caches are cleaned best-effort;
  per-zip extraction failures are caught and logged instead of killing startup.
  Fourth addendum (same day, user-directed): Assets/Shaders and the entire .cgp
  retro-shader feature removed (RetroShaderChain/Preset/Pass, RetroShader, hq2x/
  scanlines/bicubic/user chains, TargetDisplayFilter + TargetScanlineFilterIntensity +
  DispUserFilterPath config, the Scaling Filter UI group, the Bicubic final-filter
  option [legacy setting falls back to bilinear], the Chimeraware.Test shader demo app;
  client.get/settargetscanlineintensity are warning no-ops now). The built-in
  hand-coded presentation shaders in Chimeraware.Graphics remain - they ARE the display
  pipeline. DiscoHawk removed entirely (app project, DiscoHawkLogic, sln entry, docs),
  taking the stale upstream release packaging with it: Package.sh, the packaging bats,
  and the whole nix ecosystem (default.nix, Dist/*.nix, docs, CI workflow) - dead
  since they referenced gamedb/waterbox/defctrl anyway. blip_buf removed (see above).
  Eighteenth addendum (2026-08-11, user-directed - TOTAL QUICKERNES EVICTION):
  absolutely nothing from QuickerNES remains in this repository; anything
  important moved to the quickerNES repo's chimera/ dir. Concretely: the
  entire witness apparatus - vendored suite (reversing the earlier vendoring
  decision), Level A+B goldens, replay.lua, bootstrap.lua, both run-level-b
  drivers, hidden-run.ps1, native/dumper.cpp, tests/README - moved to
  quickerNES chimera/tests/ (drivers grew --chimera-root/-ChimeraRoot,
  defaulting to a sibling Chimera or BizHawk checkout); stale QuickNesConfig
  Compile Update items purged from the Chimera csproj. The quickerNES witness
  remains the commit gate, run from its new home, until the synthetic witness
  (see "The synthetic witness" section) replaces it: three synthetic cores
  (one per flavor of the core taxonomy) sharing exact emulation logic, video,
  and audio; a stateful-interpreter emulator whose game logic and assets live
  in .testrom files; tests that win, lose, and reproduce specific video and
  audio outputs, byte-compared across all three cores - starting with the
  native flavor. In the same round the native flavor LANDED in full (see the
  synthetic-witness section status) and run-witness.sh became THE smoke test,
  retiring the short-lived --quick subset from the quickerNES drivers; the
  side-effect-freedom rule for non-waterboxed cores was stated (see the
  core-flavors section; core-dev responsibility, not enforced by Chimera)
  and QuickerNES's NesCarts.xml read - its one violation - was reduced to a
  compiled-in PAL hash list in the adapter. Also fixed: `meson compile
  frontend` no longer blocks ~15 minutes on lingering msbuild node-reuse
  workers (/nodeReuse:false -p:UseSharedCompilation=false). Gate at this
  round: 26/26 simple + 26/26 rerecord, run from the quickerNES repo against
  this tree with the PAL-list package; synthetic witness 12/12.
  Seventeenth addendum (2026-08-11, user-directed - LINUX GATE + HEADLESS + layout;
  the sixteenth - an exec-bit fix and charter note - was authored on the Windows box
  and is pending its push): the witness gate now runs on Linux. tests/run-level-b.sh
  (+ bootstrap.lua) is a faithful port of run-level-b.ps1 - Chimera under Mono on a
  private Xvfb display replacing the hidden Windows desktop, same job protocol and
  the same goldens. First-ever Mono run of this frontend; 26/26 simple + 26/26
  rerecord byte-identical to the Windows-recorded goldens - the reproducibility
  pillar demonstrated across an entirely different OS, runtime (Mono vs .NET
  Framework), and display stack. Sharp edge: the package loader's preload-then-
  dedupe native contract silently assumed Windows LoadLibrary basename-dedupe
  semantics; Linux dlopen only dedupes via DT_SONAME, so libquicknes.so must be
  linked -Wl,-soname,libquicknes.so (fixed in the quickerNES adapter's Makefile,
  which also tracks the upstream cpu.cpp split and jaffarCommon macro rename;
  build-package.sh there is the pwsh-free package builder). The failure mode was
  the invisible-modal-dialog trap again, now closed for good by --headless: a new
  CLI option for unattended runs - every modal dialog funnels through six choke
  points in DialogControllerWinFormsExtensions and in --headless mode logs its
  caption/text to the console and exits with code 64 instead of blocking;
  warning-only dialogs in Program (config version/corrupt config/display fallback/
  superuser) log and continue; fatal run-loop exceptions log and exit nonzero
  (previously they could exit 0 after an invisible dialog). Both witness drivers
  pass --headless, and both gained --quick/-Quick: a six-test smoke subset (~1 min)
  for CI and dev loops - explicitly NOT a substitute for the full commit gate.
  Layout/docs in the same round: CHIMERA.md moved to docs/design-principles.md
  (references updated); LICENSE separates copyright (BizHawk team = inherited
  code, Sergio Martin 2026 = Chimera modifications and new work); accidentally
  committed build output purged (root libchd_capi.so and 490 files under
  extern/libchd-rs-capi/chd-build/, now gitignored); all prose files converted to
  pure ASCII after a committed double-encoding accident (em-dashes had become
  mojibake; ASCII-only prose renders identically under every viewer encoding -
  keep it that way). HISTORY POLICY CHANGE (user, 2026-08-11): the amend-commit-0
  model ends here; from now on each gated round lands as a new commit.
  Fifteenth addendum (2026-08-10, user-directed - MESON, LINUX-HOSTED, DUAL-TARGET):
  the canonical build is now meson on Linux (WSL locally; Linux runners in CI),
  producing BOTH OSes' artifacts: managed IL built once via dotnet (run_target
  'frontend', Linux dir only), natives built per-target - native gcc for .so, and
  mingw-w64 cross (static gcc runtime, so no libgcc/winpthread dlls) for .dll via
  extern/meson/mingw-w64.ini + mingw-toolchain.cmake. Root meson.build: direct
  shared_library targets for chimerahash/lua54/e_sqlite3/cimgui/luasocket (luasocket's
  two same-named "core" modules live in extern/meson/luasocket-*/ subdirs), nested
  upstream builds via extern/meson/nested-build.sh for zstd (its own meson), SDL2 +
  openal (cmake + toolchain file), chd_capi (cargo, --target x86_64-pc-windows-gnu
  for cross). WSL provisioned: mingw-w64 gcc 13, rustup 1.97.1 + windows-gnu target,
  and - after packages.microsoft.com's Ubuntu 24.04 feed turned out to serve a
  source-build SDK (8.0.129) WITHOUT WindowsDesktop targets, confirming the
  UbuntuMispackagedSDKCheck trap firsthand - Microsoft's own SDK binary (8.0.423)
  via dotnet-install.sh into ~/.dotnet. The
  Windows msbuild BuildNativeDeps hook is now Condition Windows_NT - a dev shim.
  Sharp edges found: mingw builds of libusb need our own config.h (msvc/config.h is
  guarded; and _TIMESPEC_DEFINED must NOT be defined - it's mingw's own timespec
  guard); meson passes builddir-relative paths to custom_target commands (absolutize
  before cd-ing tools like cargo); meson target names collide within a directory
  (same-named outputs need subdirs); WSL /tmp is volatile (log to /mnt/c); and the
  nastiest - upstream builds decorate library names per platform convention (mingw
  meson emits libzstd-1.dll + libzstd.dll.a), and a filename glob happily shipped the
  import-library ARCHIVE as "libzstd.dll", which passed everything until LoadLibrary
  returned ERROR_BAD_EXE_FORMAT and the witness went 0/26 - nested-build.sh now
  matches exact/version-infixed names and excludes .a/.d/.def. Witness after fix:
  the Windows gate ran against the Linux-cross-built dlls - the frontend stack
  (SDL2, lua54, zstd, cimgui, chimerahash all mingw-built) byte-exact at 26/26 + 26/26,
  which is also the deferred-reproducibility pillar demonstrated empirically:
  an entirely different compiler family under the frontend, identical emulation.
  Fourteenth addendum (2026-08-10, user-directed): Chimera.sln, Common.props, and
  Directory.Packages.props moved into source/ (sln project paths relativized;
  extern/ gained a thin Directory.Packages.props forwarding to source/'s, since NuGet
  discovers that file by walking up from each project). Root is now four directories
  (build/ extern/ source/ tests/) and five files (.gitignore .gitmodules LICENSE
  CHIMERA.md README.md). STATED DIRECTION (user): the entire build system - Windows
  and Linux targets - should eventually be mediated solely by MESON; the current
  msbuild-invokes-build-natives.ps1 arrangement is a transitional shim that meson
  will absorb (meson orchestrating the dotnet build and every native recipe).
  Thirteenth addendum (2026-08-10, user-directed - NATIVES FROM SOURCE): ExternalProjects
  moved to extern/ at the repo root, and ALL prebuilt native libraries are gone from the
  tree. extern/build-natives.ps1, run automatically by the solution build
  (BuildNativeDeps target, incremental by timestamp), builds every native dependency
  from source into build/dll: libchimerahash + SDL2 (in-repo recipes; clang-cl - chimerahash's
  gcc target-attributes need it, and a .def now provides the exports ELF visibility
  used to), and six NEW pinned submodules under extern/ with new recipes - lua v5.4.8
  (stock, LUA_BUILD_AS_DLL), zstd v1.5.7 (cmake -> libzstd.dll), cimgui 1.90.6 (pinned
  to ImGui.NET 1.90.6.1's binding version), openal-soft 1.24.3, ericsink/cb's sqlite3
  amalgamation with the canonical e_sqlite3 defines (mirrored from cb's generator),
  luasocket v3.1.0 (socket/mime core.dll against our lua54 import lib), plus chd_capi
  built via cargo (rust-toolchain.toml pins 1.97.1; user installed rustup for this).
  Toolchain: VS2022's clang-cl/CMake/Ninja (probed by path, works on GitHub
  windows-latest runners unchanged). Per the reproducibility pillar, all version pins
  are for BUILD reproducibility only - never for movie determinism, which belongs to
  the core package. Assets/ is GONE entirely: ChimeraMono.sh moved into the Chimera
  project (copied to build/ by the PostBuild target), the LuaCATS API doc stubs were
  DELETED outright (pure editor documentation, annotation-only with error() guards,
  never read by the frontend, and partially stale against Chimera's actual API;
  recoverable from history or regenerable from the [LuaMethod] attributes if ever
  wanted), and the Assets/** copy glob was removed. Root is now
  build/ extern/ source/ tests/ + eight files.
  Twelfth addendum (2026-08-10, user-directed - EXPLICIT CORE LOADING + layout): cores
  are no longer discovered. The startup Cores/ directory scan is gone; loading a core
  is an explicit act: File > Open Core... (file prompt for a package dir/zip) or the
  new --core=<path> CLI option (which the witness driver now passes). Open ROM and
  Recent ROM stay DISABLED until a core is loaded - deliberately, so core (sync)
  settings can be configured BEFORE the first rom load (config-before-load principle).
  The Config > Core Settings menu rebuilds on every package load; zip caches now live
  under <exe>/CoreCache. Layout changes in the same round: chimera-cores/ deleted
  (author docs live in this charter + the quickerNES reference package);
  chimera-tests/ renamed tests/; ExternalProjects/ moved under source/; root purged
  to the minimum (removed: .github incl. all CI, .vscode, .config, .editorconfig,
  .global.editorconfig.ini, .stylecop.json + the Common.props StyleCop block,
  appveyor.yml, SECURITY.md, contributing.md [README gained a Contributing section],
  global.json, sln.DotSettings, .git-blame-ignore-revs, "Building Other
  Solutions.txt").
  Eleventh addendum (2026-08-10, user-directed): Chimera is a STANDALONE repository
  (github.com/SergioMartin86/Chimera) with a FRESH history - commit 0 is the
  post-separation state. The BizHawk ancestry (23,722 commits) is deliberately not
  carried; it remains available in the upstream BizHawk repository and in the
  transitional SergioMartin86/BizHawk fork, which is also the recovery source for
  deferred machinery (waterbox, trace logger, disassembler, Gambatte sources for
  Phase 4). Future upstream ports are cherry-picked/rebased without shared ancestry.
  Tenth addendum (2026-08-10, user-directed - FULL SEPARATION): the QuickerNES adapter
  no longer lives in this repo at all. Everything quickerNES-specific
  (chimera-cores/quickernes/ and the quickernes.yml workflow) moved to the quickerNES
  repository itself (github.com/SergioMartin86/quickerNES, branch chimera-adapter,
  `chimera/` dir): managed adapter, native chimerainterface + Makefile (now building from
  that repo's own sources), manifest, bundled data, prebuilt natives, build-package.ps1
  (param -ChimeraRoot, default sibling ../BizHawk checkout; installs quickernes.chimeraCore
  into <ChimeraRoot>/build/Cores), and a native-build CI workflow. The dev loop:
  build Chimera sln -> run ../quickerNES/chimera/build-package.ps1 -> witness gate.
  chimera-cores/ here is reduced to the core-author guide README. The vendored witness
  suite stays in chimera-tests/suite/ (user decision: pinning the gate to what the
  goldens were recorded against is a feature). Gate ran against the
  quickerNES-repo-built package: 26/26 simple + 26/26 rerecord.
  Ninth addendum (same day, user-directed): References/ deleted - no committed managed
  binaries remain. The source generators are ProjectReferences with
  OutputItemType=Analyzer; NLua/ISOParser/HawkQuantizer are plain ProjectReferences
  (built transitively, not sln members); SettingsUtil is built via a
  ReferenceOutputAssembly=false reference from Chimera and copied into build/dll
  (ProvideCoreAuthorKit target) so the core-author kit is contract DLLs + settings
  generator in one directory - the quickernes package's Analyzer path points there.
  Sharp edge #3: solution builds UNSET Configuration/Platform for ProjectReferences
  that aren't solution members - everything above silently built as Debug inside a
  Release build until Common.props set
  ShouldUnsetParentConfigurationAndPlatform=false. Also, LibCommon.props had a
  PostBuild copying outputs into References/, which would have silently resurrected
  the folder; removed. Only native binaries (C/C++ toolchains required) remain
  committed: Assets/dll frontend infra and the package's libquicknes.
  Eighth addendum (same day, user-directed): the entire Dist/ folder deleted - release
  packaging utilities (7za/zip/unzip/upx/fart/vswhere/ILMerge/NuGet binaries), Unix
  build wrapper scripts (two already broken by the rename), upstream release stamping,
  the upstream changelog, arm64 prebuilt natives nothing shipped anymore, and the
  git-hooks commit-message linter together with the InstallGitHooks build target in
  Chimera.Common.csproj that installed it on every build (code-checking, per user
  policy). Unix builds are plain `dotnet build Chimera.sln` now.
  Seventh addendum (same day, user-directed): standard folder nomenclature adopted - 
  `src/` is now `source/` (git mv, sln paths updated; likewise the package's `src/` ->
  `source/` and `native-src/` -> `native-source/`) and the build output dir `output/` is
  now `build/` (MainSlnExecutable.props, package HintPaths/build script, witness driver,
  .gitignore, launch.json, SDL2/libchd build scripts, docs). This charter's earlier
  entries were rewritten to the new names wholesale.
  Sixth addendum (same day, user-directed): ExternalToolProjects deleted entirely
  (incl. HelloWorld; the external-tools mechanism in the frontend stays), and the
  `quicknes/` directory is GONE - Chimera no longer carries the quickerNES submodule.
  Consequences handled: the witness test suite (.test/.sol/.state) is now VENDORED at
  `chimera-tests/suite/` (snapshot of upstream `tests/`; run-level-b.ps1 reads from
  there, making the Level B gate fully self-contained modulo ROMs); the native build
  recipe (chimerainterface.cpp + Makefile) moved into
  `chimera-cores/quickernes/native-source/` with a `QUICKERNES_ROOT` variable pointing
  at an external quickerNES clone; quickernes.yml clones upstream instead of using a
  submodule. The submodule working tree contained untracked files (controller.hpp and
  a vendored copy of the original blargg quickNES) - backed up to
  Documents/ClaudeSessions/quickernes-submodule-backup before deletion. Stale
  submodule.* entries for long-deleted submodules were purged from local .git/config.
  Fifth addendum (same day, user-directed): ExternalProjects dead weight deleted - 
  FlatBuffers.GenOutput (no consumers), LibBizAbiAdapter + the WaterboxAdapter/
  MsHostSysVGuest code in NativeInvoke (waterbox pile), TestromSuiteReportProcessor,
  Chimera.AnalyzersTests, and Chimera.Analyzer itself (user: no code-checking needed;
  References/Chimera.Analyzer.dll + Common.props wiring removed). AnalyzersCommon KEPT:
  despite the name it's shared plumbing imported by the three source generators, which
  generate runtime-necessary code. ExternalToolProjects trimmed to HelloWorld + shared
  props/targets (DATParser/DBMan were gamedb tooling; AutoGenConfig/FakeTemporalAA were
  dev experiments) - the external-tools mechanism itself stays. Stale CI deleted
  (.gitlab-ci.yml, mame/waterbox/release workflows); ci.yml rewritten minimal
  (build sln + package); quickernes.yml and quicknes/make now install the native to
  chimera-cores/quickernes/natives. Still present, deliberately: ExternalProjects/SDL2
  (81 MB of SDL+libusb submodules, only needed to rebuild SDL2.dll from source),
  iso-parser + libchd-rs-capi (fall with DiscSystem if the disc decision goes that way),
  NLua/LibChimeraHash/HawkQuantizer/SrcGens (sources of live References/Assets binaries).
  Also in this round: ExternalProjects/librcheevos (build project + rcheevos submodule)
  removed; stale waterbox/llvm-project gitlink dropped from the index; legacy example/dev
  Lua scripts deleted (ButtonCount, Input_Display, JoypadIntersection, MovieClock,
  migration_helpers, tasjudy, UnitTests - Lua/ keeps only socket/ and mime/, which are
  luasocket binary modules wired into package.cpath, and _docs_luacats, the Lua API type
  stubs); empty leftover directories from earlier phase deletions cleaned from disk.
- **Phase 4 - Harden & prove generality.** Manifest/versioning, package validation,
  error UX, core-author docs. Port a second core to prove the contract isn't
  QuickerNES-shaped.

## THE WATERBOX-ONLY REDESIGN (user-decided, 2026-08-11)

After full analysis of BizHawk's waterbox (docs/waterbox-analysis.md), a
fundamental design change: Chimera allows ONLY waterboxed cores, making
every core reproducible and portable BY FORCE rather than by discipline.
Decisions, all user-confirmed:

1. **Waterbox-only, no exceptions.** The package format knows only guest
   images. Flavors (a) native and (b) pure-C# are no longer expressible as
   packages; the taxonomy above remains as analysis, and the side-effect
   rule's "cannot be enforced" caveat inverts - the sandbox IS the
   enforcement.
2. **The waterbox host lives in Chimera**, compiled and shipped with
   Chimera's OS-dependent artifacts (meson dual-target, like every other
   native). This amends the reproducibility pillar the same way the frozen
   movie-mnemonic format does: the WATERBOX MACHINE IS A FROZEN, VERSIONED
   SPECIFICATION - address layout, syscall semantics, the time constant,
   scheduling order, callback-slot mechanics, everything the guest can
   observe - and Chimera ships a spec-exact implementation. Movies record
   the machine-spec version; the reproduction contract becomes
   (movie + core package + machine-spec version), where implementations of
   a given spec version are interchangeable by definition. The Synth
   SPEC.md/twins exercise is the working proof that spec-first bit-exact
   interchangeability is achievable.
3. **One generic adapter, no per-core managed code.** A core package is a
   single platform-neutral zip: manifest + core.wbx + data files
   (controller definitions, settings schemas, system ids, extensions -
   all DATA, interpreted by Chimera's one universal adapter over a
   standardized guest export ABI). Identical zip and identical package
   SHA1 on every OS; movies therefore carry the same CorePackageSHA1
   cross-platform. (BizHawk's Nyma layer - one data-driven adapter serving
   every mednafen core - is the scale precedent.)
4. **Everything waterbox lives in ONE external repository: miniBox**
   (github.com/ToolAssisted-run/chimera-common-minibox), consumed as a submodule at
   extern/chimera-common-minibox like every other from-source dependency. It carries all
   sides: the runtime (sandbox host - Chimera's meson builds it into the
   frontend's OS-dependent artifacts), the guest toolchain (the core-author
   kit: musl fork, emulibc, libco, libcxx sysroot, linkscript, common.mak),
   the managed host layer, and the waterbox's own conformance tests -
   independently testable without Chimera. Plan of record for the runtime:
   a C/C++ port replacing the imported Rust reference (drops the
   nightly-Rust requirement; validated differentially against it).

Migration order (each step gated by the synthetic witness, plus the
quickerNES suite until its core is migrated):
  (i) import + de-nightly the waterbox host runtime; meson dual-target;
      restore the Windows SysV ABI adapter (MsHostSysVGuest) from the
      transitional fork;
  (ii) define guest ABI v1 + package format v2 (data-driven controller/
      settings declarations); implement the generic adapter;
  (iii) stand up the toolchain repo; build synth.wbx from synthcore.c; the
      waterboxed synth becomes the SHIPPED synth package, while the native
      and pure-C# implementations remain as external ground-truth testers
      (Level A golden generators - an even cleaner witness split);
  (iv) migrate quickerNES to a .wbx guest; re-gate the full suite;
  (v) retire the flavor-(a)/(b) loading paths (manifest v1, natives lists,
      package-assembly resolution) from Chimera.

## The synthetic witness (planned successor to the quickerNES gate)

Plan (user-stated, 2026-08-11), building on the core-flavor taxonomy above:

1. **Three synthetic cores, one per flavor** - native, pure C#, and waterboxed -
   with exactly the same emulation logic, video output, and sound output.
2. **A synthetic game with non-trivial rules** and an input set.
3. **The test plan**: for a series of input movies, ALL of the memory space, the
   video output, and the sound output must match across all three cores.
4. **Each test achieves a different goal**: win the game, lose the game, produce
   a certain video output, produce a certain audio output.

Architecture (user-stated): the emulator is a **stateful interpreter** with the
capacity of printing to screen and emitting audio - nothing more. ALL game
logic, video assets, and audio assets belong to the game, which is provided as
a **`.testrom` file**; there are multiple test roms. (Consequence: the
interpreter is a miniature console, the roms are its games, and the rom-routing
path of the frontend - manifest `extensions` mapping `.testrom` to the
synthetic system - is exercised exactly like a real system's.)

Derivation: the emulator/game logic derives from the JaffarPlus test emulator
and its GridWalker test game (a cursor on a bounded W x H grid with clamped
U/D/L/R moves; goal-cell rules; 2-byte core state), extended with video and
audio output, and with the logic moved out of the emulator into the rom
program per the interpreter architecture.

Sequencing: the machinery for waterboxing and for C# core integration is not
yet defined, so work STARTS WITH THE NATIVE CORE ONLY; the pure-C# and
waterboxed twins follow when their integration stories exist. Until the
synthetic witness is complete enough to be the gate, the quickerNES witness
(now in the quickerNES repository) remains the commit gate.

Status (2026-08-11): the native flavor is fully implemented and passing -
SPEC.md v1; libsynthcore (the reference interpreter); the .sasm assembler;
the gridWalker rom (walls/hazards/goal/step budget, per-move beeps, win/lose
jingles); the synth-native package (adapter + manifest routing .testrom,
both OS natives); and run-witness.sh, the two-level driver: Level A replays
the four goal movies (win / lose / video output / audio output) natively and
checks final RAM plus FULL per-frame video and audio stream hashes against
goldens, with a serialize-every-frame rerecord self-check; Level B replays
the same movies through Chimera (Mono + Xvfb) and byte-compares the final
RAM and VRAM domains against the same goldens, both modes. ~10 seconds wall
clock, and it is THE Chimera smoke test (user decision, same day): the
quickerNES --quick smoke subset is retired; quickerNES's suite is the full
determinism gate only. Audio is Level A-verified only for now (the frontend
has no scriptable audio tap - an acceptable gap while the frontend audio
path stays core-agnostic).

Flavor (b) LANDED (same day): SynthMachine.cs is a from-spec pure-C#
reimplementation sharing no code with the native flavor, shipped as
synth-sharp.chimeraCore - a package whose manifest declares an empty natives list;
nothing is loaded at runtime, upholding side-effect freedom trivially. The
C# core integration story turned out to already exist: the contract and
package loader handle a natives-free package unchanged. Cross-flavor
equivalence is proven by the witness: the C# Level A tester and the
frontend-loaded package both reproduce the NATIVE-recorded goldens
bit-exactly - RAM, full per-frame video stream, full audio stream, all
four goal movies, both replay modes (24/24). Goldens are only ever
recorded from the native reference; other flavors must match, never
re-record.

Flavor (c) LANDED (2026-08-11): synth.wbx is the SAME synthcore.c
compiled for the miniBox waterbox sandbox (see the miniBox repository at
extern/chimera-common-minibox - a from-scratch C/C++ port of BizHawk's waterbox host,
GCC-built guest toolchain, machine spec, all meson) and wrapped in the
waterbox ABI (tests/synth/package-box/). Its whole machine state lives in
guest memory, so the waterbox host savestates it AUTOMATICALLY - no
explicit serialize/deserialize, the defining property of the flavor. The
Level A tester runs it through the miniBox host and reproduces the
native-recorded goldens byte-exactly on all four movies, in both plain
mode AND whole-machine-rerecord mode (the waterbox host round-tripping the
entire guest around every frame). run-witness.sh now checks all THREE
flavors at Level A. The core-flavor taxonomy is thus demonstrated end to
end: three independent implementations (native C, pure C#, waterboxed C),
one frozen spec, bit-identical memory + video + audio. This also validates
the waterbox-only redesign's foundation - the miniBox host is real,
GCC-buildable (no clang/nightly-Rust), and reproduces emulation exactly.

Placement note: the synthetic cores are conformance-test fixtures for the
published contract, not product cores - they live under `tests/` and are built
by the test harness against `build/dll`, never as members of the solution. The
"zero core code in the repo" objective refers to the product: Chimera builds
and ships with no core in the solution; a test fixture proving the contract
works is test machinery, same as the witness drivers.

## Witness harness (two levels, both must pass at every phase boundary)

NOTE (2026-08-11): this section describes the quickerNES witness, which -
together with the vendored suite, goldens, and drivers - now lives in the
quickerNES repository (`chimera/tests/`), per the rule that absolutely
nothing quickerNES-specific remains in this repository. It is still the
commit gate (run it from there, `--chimera-root` pointing here) until the
synthetic witness above replaces it. The section is kept as the harness's
design record.

The quickerNES test format: each `.test` is JSON naming a ROM (+ expected SHA1, optional
initial `.state`, controller types) and a `.sol` input sequence (one line per frame,
jaffar format, e.g. `|..|........|`). The native tester (`quicknes/core/source/tester.cpp`)
replays the sequence and emits a MetroHash of NES low RAM (2KB) as the verdict; cycle
types `Rerecord`/`Full` additionally do a savestate save+load around every frame, which
is exactly the TAS-critical property.

- **Level A - core payload guard.** Build and run the native quickerNES tester over all
  34 tests. Validates that the native DLL we package never drifts. Runs everything,
  including the two tests with initial `.state` files.
- **Level B - full-stack witness (the real one).** Drive Chimera itself: load ROM, feed
  the `.sol` inputs through the frontend input pipeline (Lua harness or generated `.bk2`
  movies), dump final 2KB RAM domain, byte-compare against golden dumps recorded from
  unmodified BizHawk in Phase 0. Also run a per-frame savestate save/load variant
  (mirroring `Rerecord` cycle type) to exercise the frontend statable path.
  `chimerainterface.cpp` already supports the Arkanoid paddle types the arkanoid tests need.

Level B sharp edges, to resolve in Phase 0: input-string -> BizHawk controller mapping
must be validated button-by-button; the two initial-`.state` tests (microMachines,
saiyuukiWorld.lastHalf) use quickerNES-native state format and may remain Level-A-only;
BizHawk power-on state must be confirmed identical to the bare core's.

Witness-set exclusions found in Phase 0 (28 of 31 at Level A; 26 at Level B):
- `castlevania3.playaround`: mapper 5 (MMC5) is deliberately disabled in the
  BizHawk-pinned TASEmulators/quickerNES fork (poor QuickNES MMC5 support) - excluded.
- `novaTheSquirrel.anyPercent`: pinned core segfaults in `Core::serializeState` on
  mapper 30 (UNROM 512) before emulation starts. Pre-existing fork bug, likely affects
  stock BizHawk too - excluded pending separate investigation.
- `arkanoid2.arkFamicomController`: local `Arkanoid II (Japan).nes` dump SHA1 does not
  match the test's expected dump - excluded until the matching dump is available.
- `rcProAmII.race1` / `superOffroad.anyPercent`: upstream's quickNES-vs-quickerNES
  comparison fails, but quickerNES itself runs and hashes fine - IN scope (Chimera
  Level A compares quickerNES hashes against stored goldens, not core-vs-core).
- Note: the native tester build needs `-Wno-unused-but-set-variable` appended to
  `commonCompileArgs` under GCC 13+ (applied to the WSL build copy only, not the
  submodule).

Phase 0 discoveries about frame alignment (validated by per-frame RAM comparison):
- Chimera emulates exactly ONE frame during ROM load, before a `--lua` script's
  first line executes. A naive Lua replay is therefore one frame late relative to a
  power-on input sequence. `replay.lua` compensates with `client.reboot_core()` at
  script start (verified `startframe=0` afterward). Remember this when Chimera later
  aligns `.bk2` movies with tester `.sol` sequences.
- quickerNES `emulate_skip_frame` (rendering disabled, used by the native tester) was
  verified state-equivalent to `emulate_frame` - rendering on/off does not affect RAM.
- Chimera power-on RAM state is byte-identical to the bare core's (no adapter-side
  initialization differences).
- Lua `joypad.set` input passes through the SOCD (opposing-directions) filter
  (`UdlrControllerAdapter`), whose default `Priority` policy silently rewrites
  simultaneous L+R / U+D - which the TAS movies use heavily. The harness config sets
  `OpposingDirPolicy: 2` (Allow). Note for Chimera: `.bk2` movie *playback* bypasses
  this filter (it taps the chain after the SOCD adapter); only Lua/user input is
  affected.
- The native tester parses but IGNORES console Reset/Power flags during replay
  (`input.reset` is dead in `advanceState`); the harness mirrors that.
- Lua `joypad.setanalog` axis sticky-holds never reach the output controller in this
  BizHawk build (axes die between `StickyHoldController` and the final controller);
  axes must be delivered via `joypad.setfrommnemonicstr`, which routes through
  `ButtonOverrideAdapter` -> `Controller.Overrides()`. Worth revisiting when Chimera
  owns the input pipeline.
- Phase 0 witness status: 26/26 Level B tests PASS byte-identical to native ground
  truth in BOTH modes (simple replay AND per-frame savestate rerecord); Level A
  goldens recorded for 28/28.
- Core finding for upstream quickerNES: a full-state savestate round-trip is NOT
  lossless for `gimmick` (Sunsoft FME-7) and `superOffroad` - the native core itself
  perturbs state on deserialize+advance (Chimera mirrors it byte-exactly, so the
  frontend is faithful; the incompleteness is in core serialization). Deterministic,
  so the witness remains sound, but worth an upstream look.
- Core finding for upstream: pinned fork segfaults in `Core::serializeState` on
  mapper 30 (novaTheSquirrel) - see witness-set exclusions.

## Architecture facts informing the plan

- `CoreInventory` already discovers cores via reflection (`[Core]` + `[CoreConstructor]`
  attributes) and its constructor already accepts arbitrary assembly type lists - the
  plugin inversion is small.
- Only `Chimera.Client.Common` references `Emulation.Cores` at the project level; the real
  coupling is ~96 frontend files using concrete core types, mostly deletable for Chimera.
- `GenericCoreConfig` (reflection-based settings UI) already exists; per-core config
  dialogs are not needed.
- Chimera targets `net48`: no assembly unload, hence the load-once model.
- Determinism is sacred: anything touching emulation, input, or timing must preserve
  frame-exact reproducibility or movies desync.

## The waterbox guest ABI, as built (2026-08-12)

The contract a core package implements. Everything below is the *whole* surface:
a package is `core.wbx` + `waterbox.config`, and the one generic adapter in
`Chimera.Emulation.Common` drives it.

**Required exports.** `Init()` (reads the mounted rom and the mounted
`settings` JSON, returns 1 on success), `FrameAdvance(uint64 buttons)`, and the
video/audio getters named by `waterbox.config`. Button *i* of the config's
`input.buttons` is bit *i* of the mask - 64 buttons, which is what a four-player
console with peripherals actually needs.

**Runtime self-description.** Memory domains are queried after `Init`
(`GetMemoryDomain{Count,Name,Ptr,Size,Writable}`), never declared statically:
their count and size depend on the cartridge and on user settings.

**Turbo.** `SetRenderingEnabled(int on)`, optional: while off the core produces
no picture and is otherwise exactly the machine it would have been. See "Turbo:
a frame nobody looks at does not have to be drawn" below.

**Analog controls.** `waterbox.config`'s `input.axes` declares them; the adapter
pushes each value with `SetAxis(index, value)` immediately before the frame it
belongs to. A package that declares axes must export it.

**Optional tooling groups** (surfaces, registers, buses, trace) - the core
renders and formats its own tooling, so the frontend needs no system knowledge.
A missing export is reported by the host as address 0, and the adapter simply
does not register the corresponding service, which greys the tool out. See
`WaterboxCore.Tooling.cs`.

**Settings.** `waterbox.config`'s `settings` block holds package defaults; the
adapter merges the user's sync settings over them and mounts the result as JSON
for the guest to read during `Init`. They shape the machine, so changing one
reboots the core. Note the known wrinkle: sync settings are keyed by adapter
*type*, so every waterbox core currently shares one config key.

## Core packages arrive by themselves (2026-08-13)

Objective 1 said packages are "discovered from a `Cores/` directory"; until now
they were not - a package had to be named on the commandline (`--core`) or picked
from File > Open Core on every launch. `CorePackageDiscovery` closes that: at
startup, `Cores/` beside the executable (plus anything in
`Config.CorePackagePaths`) is scanned, and every readable package found is
loaded.

**Discovery reads, it does not load.** It opens a zip (or directory), reads
`waterbox.config` or `chimera-core.json` for a name, systems and extensions,
and hashes the file - nothing more. That matters because loading is irreversible
in-process: it pins native modules and, for adapter packages, an assembly. The
frontend has to be able to *describe* a package it will never load - a broken
one, a duplicate - and it can only do that if describing is cheap and separate.

**No enable/disable switch, deliberately.** The first cut of the window had a
checkbox per package. It was removed the same day, on the user's challenge, and
the reasoning is worth keeping: loading a package costs a JSON parse (the
`core.wbx` is untouched until a rom loads), arbitration between two cores for one
system is what Preferred Cores is for, and a package that fails to load is caught
and reported rather than fatal. So the switch bought nothing - while costing a
config list, an enable/disable API, and two extra states whose only job was to
explain that unticking a loaded core cannot unload it. The way to not load a core
is to not put it in `Cores/`. The window is a report, not a control panel.

**Ordering trap, found by testing.** The scan must run in the MainForm
*constructor*, not `MainForm_Load`: the commandline rom load happens in the
constructor, so a scan in Load is minutes too late in program terms and the user
gets a platform-picker for a rom the frontend could have opened. The witness has
a `D:box:autodiscovery` case (a rom load with no `--core`) so this cannot regress
silently.

## Testing the interface (2026-08-13)

The frontend's windows are now split so that almost everything about them can be
checked without a person:

- **Logic** lives in `Chimera.Client.Common` and is tested in
  `Chimera.Tests.Client.Common` - no display, no emulator, no core. The states a
  window displays belong here, not in the window.
- **Wiring** is tested in `Chimera.Tests.Client.GUI`, which constructs real
  forms and drives them (tick a row, press a button) under Xvfb. Forms must be
  shown for handles to exist, or selection and click handling silently do
  nothing.
- **Appearance** cannot be asserted, so it is rendered instead: `UiScreenshots`
  writes PNGs when `CHIMERA_UI_SHOTS` is set, CI uploads them every run, and a
  person looks.

`tests/ui/run-ui-tests.sh` runs all of it (`--shots` for the pictures). New tool
windows should follow the same split: if a question about a window can be
answered without looking at it, the answer belongs in a class that a test can
call.

## Core settings are declared, not coded (2026-08-13)

`waterbox.config`'s `settings` was a bag of defaults with no types and no
documentation; the settings dialog rendered it as an uneditable dictionary. It is
now a list of DECLARATIONS - name, display name, description, type, default,
options or range, and whether the setting is sync.

**The declaration is the UI.** There is no per-core settings dialog to write, and
never can be: one adapter serves every package. So `WaterboxSettingsBase`
implements `ICustomTypeDescriptor` and synthesizes a `PropertyDescriptor` per
declaration - which is exactly what WinForms' PropertyGrid asks for names, types,
descriptions, defaults and dropdown values. Every word in that dialog came from
the core. Adding a setting to a core is a `waterbox.config` edit and nothing else.

**Sync vs non-sync is the frontend's question, not the core's.** The guest gets
one flat settings object and reads the keys it knows. The `sync` flag decides
what the FRONTEND does: sync settings are recorded in movie headers and reboot
the core when changed, because they shape the machine.

**Non-sync settings must actually apply.** A "non-sync" setting that only took
effect at Init would be a sync setting wearing a disguise, so there is a fifth
optional guest ABI group: `GetSettingsCapacity` / `GetSettingsBuffer` /
`PutSettings(len)`. The host writes fresh JSON into the guest's own buffer and
calls it; `PutSettings` then returns `None` instead of `RebootCore`. A core
without the group gets a reboot, which is heavier but honest - the setting still
applies. The buffer must live in `ECL_INVISIBLE` memory: settings are not machine
state, and a savestate that captured them would restore an old value and make two
identical runs diverge.

**Storage did not change.** Values are still a flat name -> value map in the
config file and movie headers, so movies and configs written before this still
load.

## A second core for a system (2026-08-13)

QuickerNesHawk - a C++ transliteration of BizHawk's NesHawk - joined quickerNES
as a NES core package, and being the second package to claim a system turned up
three gaps that one core per system had hidden.

**Nothing could choose between them.** `--core` LOADS a package; it does not say
which core a rom opens with. That was decided by `Config.PreferredCores`, which
nothing in the UI ever wrote, so a `.nes` file opened with whichever package
happened to register first - alphabetical accident. The Emulator menu now has a
`Core` submenu listing every core registered for the running system, checked on
the current one; picking another records the preference and reboots. With one
core it is a single checked entry that answers "what am I running", which the
status bar already said - but the menu is where a user goes to change it.

**A script (or a gate) could not ask what it was talking to.** `emu.getsystemid()`
answers "NES", which is now ambiguous. `emu.getcorename()` answers the question
that matters. This is not a nicety: the first version of QuickerNesHawk's
frontend gate silently measured *quickerNES* and passed, because both cores are
accurate enough to agree on the test rom's RAM for 300 frames. A gate that cannot
name its subject is not a gate.

**A package may know its own frame rate.** NesHawk's region (NTSC/PAL/Dendy) is a
user setting, and it changes both the frame rate and the samples per frame -
neither of which a static `waterbox.config` can express. Two more optional guest
exports resolve after `Init` and win over the config when present:
`GetVsyncNumerator`/`GetVsyncDenominator`, and `GetAudioSampleCount`, which is
also what a blip-style resampler needs (it does not produce the same count every
frame). When a core exports the count, `audio.samplesPerFrame` becomes the buffer
CAPACITY rather than the per-frame number.

**A package says what rate it mixes at** (2026-09-04). The frontend's sound path
is built around 44100 Hz and the lineage's cores all produced it, so nothing ever
asked. A PS2's SPU2, a GameCube's DSP and an Xbox's APU mix at 48 kHz, and played
as 44.1 they came out a semitone and a half low with their audio outrunning the
video of every encode (issue #37). `audio.rate` declares the rate (44100 when
absent) and the adapter resamples to 44100 once per frame, on the way to the
sound output and the encoder alike. Presentation only: the guest's bytes, which
the gates hash, are what they were.

The wrinkle noted in the ABI section is now visible rather than theoretical:
sync settings are keyed by adapter *type*, so both NES packages share one config
key and one settings blob. Whichever core is loaded reads the keys it knows and
ignores the rest, so nothing breaks today, but two cores with a same-named
setting of different meaning would collide. Keying by package SHA1 (the identity
the reproduction contract already uses) is the fix when it matters.

## Key bindings arrive with the core (2026-08-13)

BizHawk ships one `defctrl.json` listing the default bindings for every console it
knows about. That only works while the frontend knows every console; Chimera's
cores arrive from outside it, so a monolithic file here would either list consoles
the frontend has no business knowing about, or be empty. It was empty: the
mechanism to read package bindings existed, but the last packages that shipped any
lost their files in the waterbox-only redesign, so a fresh install had no bindings
for any core and no way to get them except binding everything by hand.

**A package that declares a controller declares how it is played.** Each package
ships `default_keybinds.json` beside its `waterbox.config` - the same shape as
BizHawk's file, scoped to the controllers that package declares - and the values
are transcribed from BizHawk's own defaults, so someone arriving from BizHawk
finds the keys where they left them. The frontend ships none of its own.

**And has nowhere to put any.** The exe-side `defctrl.json` went with it:
`Config.ControlDefaultPath`, the seeding of a fresh config from that file, and the
controller dialog's "Save Defaults" item are all gone. Keeping a second source of
defaults would have meant two answers to "what are the defaults for this pad", one
of them a file the frontend can no longer fill in for a core it has never seen.
Your own bindings live in your config, where they always did; the defaults live
with the core that knows what the controller is.

**And "Preferred Cores" went with it** (user, same day). Config > Preferred Cores
had been a dead menu item since the CoreInventory removal - no handler, no
contents, kept only as the anchor the Core Settings submenu is inserted after. I
populated it; the user's call was to delete it instead, and the reasoning
generalises past this menu: a per-system matrix of cores to prefer is a shape that
only makes sense when the frontend ships every core it can name. Here a package
either is installed or is not, and the question is never "which of the cores you
bundle" but "this rom opens with which of the ones I installed".

So the only thing left is that answer, renamed to what it is:
`Config.DefaultCores` - the core a system's roms open with, written only when
someone picks one in Emulator > Core, never pre-populated, and absent entirely for
a system with one core. `RomLoader` reads it and otherwise takes whichever core
registered first.

They are DEFAULTS in the strict sense: they fill in for a controller the user's
config has never seen (`InputManager.SyncControls`), and the controller config's
Defaults button falls back to them. A user's own bindings always win, and the
first package to claim a controller name keeps it - so installing a second NES
core cannot rebind the pad someone is already playing with.

**A broken bindings file costs the bindings, not the core.** `PackageKeybinds.Read`
reports and ignores unreadable JSON: an optional convenience file must never stop
a core from loading.

Gated at both ends: the synthetic witness starts Chimera from a config that has
never heard of the synth controller and requires the package's bindings to be in
the config it writes out (`K:box:keybinds`), and each core repo's frontend gate
does the same for its own package.

## Sweeping out what the bundle left behind (2026-08-13)

With the cores outside the frontend, a lot of BizHawk's furniture had nothing
behind it any more. Removed, on the user's instruction to take out anything no
longer relevant:

- **Basic Bot**, entirely. It is a brute-force input searcher, and searching is
  jaffarPlus's job - that integration is the plan, not a second bot in here.
  Its three `IMainFormForTools` members (`LoadQuickSave`, `Throttle`,
  `Unthrottle`) went with it, each marked "only referenced from BasicBot", and
  `MainForm.Throttle`/`Unthrottle` had no other caller either.
- **Hotkeys for consoles this frontend cannot run**: the SNES, GB and NDS groups
  (layer toggles, screen rotation) and the RetroAchievements group. None had a
  handler - they were tabs in the Hotkeys dialog binding keys to nothing. The
  dialog's hack to hide the RA tab went too.
- **Config members nothing reads**: the ten RetroAchievements settings,
  `GbAsSgb`, `LibretroCore`, `SelectedProfile` (and the `ClientProfile` enum),
  `FirstBoot`, and `N64UseCircularAnalogConstraint` - a knob named for a console
  that cannot exist here, gating an axis constraint no package can declare.
  `TargetZoomFactors` lost its seed values for eight systems that are not here;
  it still remembers what the user sets.
- **The onboarding silhouette** in the status bar, which offered a setup wizard
  that no longer exists and answered a click with "All done!".
- **Dead UI**: an `A7800Hawk` menu item that was never even added to a menu, the
  `RomStatusPicker` dialog nothing opened, and five image files no code named.

Kept deliberately: the Code-Data Logger tool (it greys itself out for a core
without the service, and a future guest ABI group could back it), the "None"
placeholder in External Tools (an empty submenu never opens, so it cannot
repopulate itself - the same trap Preferred Cores fell into), and
`PreferredPlatformsForExtensions`, which the platform chooser still uses for
ambiguous extensions.

### Second pass: tools and per-console corners (2026-08-13)

- **Code-Data Logger**, entirely. It needs `ICodeDataLogger`, and the guest ABI has
  no group that could back it, so it greyed itself out for every core that can
  exist here - along with its drag-and-drop `.cdl` handling and its slot in the
  file-load ordering. If a "which addresses were code" group ever joins the ABI,
  the tool comes back with it.
- **Per-console corners in generic tools**: TAStudio's Doom column-hiding and its
  N64 C-button mnemonic prefixing; the hex editor's Arcade/N3DS exception around
  the rom domain, and its N64 matrix viewer (menu item, handler and the nested
  dialog class). Each was a branch on a system ID no package can claim.
- **Libretro, entirely** (user, same day). It started as the path plumbing -
  `RetroSaveRamAbsolutePath` / `RetroSystemAbsolutePath` and the two
  `ICoreFileProvider` members that handed a libretro core its own directories -
  and then took the rest: the `OpenAdvanced_Libretro` and
  `OpenAdvanced_LibretroNoGame` load types with their `IOpenAdvancedLibretro`
  interface, `RomLoader.LoadRom`'s `launchLibretroCore` parameter (whose one
  caller passed null), the file filter for picking a libretro `.so`/`.dll`, and
  the `Libretro` system ID. A libretro core is a foreign plugin ABI loaded from a
  shared library; Chimera's cores are waterbox packages with a sandbox and a
  reproduction contract, which is the opposite trade, so there was never going to
  be a road back.
- The **AVI writer** is now hidden on non-Windows rather than removed: it is
  Microsoft's AVIFIL32, so on Linux the only thing offering it achieved was a
  `DllNotFoundException` after the user picked a codec. It still works on Windows,
  where Chimera also runs.

Checked and kept: the seven other video writers (all registered, all working - the
per-codec dialogs are theirs, not leftovers), and the Cheats tool, which needs
only `IMemoryDomains` and therefore works with any core package.

### Third pass: MAME, and the core-motivated exceptions (2026-08-13)

The user's instruction was "remove all mame and all core-motivated
exceptions-to-the-rule", and the second half is the interesting one: a
core-agnostic frontend should have no code that exists because of one particular
core.

- **MAME**: the `OpenAdvanced_MAME` load type, the three `RomStatus` values only
  it produced (`Imperfect`, `Unimplemented`, `NotWorking`) and the status-bar
  cases and multi-disk exemption built around them, its icon, and the
  `IsDiscForXML` hack that made a `.chd` not-a-disc "due to MAME wanting CHDs as
  hard drives (bad design, I know!)" - the comment was BizHawk's own.
- **The controller artwork**: a table mapping 26 controller-definition names to
  pictures of consoles, with an "Uberhack" for the C64 keyboard and a special
  case for the ZX Spectrum. A package declares buttons, not artwork, so the
  frontend cannot know what any pad looks like; the picture column is collapsed
  and the images are gone.
- **Firmware, the whole subsystem**: the manager, the (already empty) database,
  the config dialog with its hardcoded table of console names, the missing-
  firmware retry path, the `Firmware` path entry, the movie header's firmware
  hash, and `ICoreFileProvider` itself - which after the libretro removal existed
  only to hand cores their BIOS files. Note this overrides an earlier note in
  `FirmwareDatabase.cs` saying the mechanism would stay for a future
  package-registers-firmware story: with no ABI channel for it, nothing could
  ever resolve, so the config dialog opened an empty window. When packages need
  BIOS files they will mount them like any other data, and the design will be
  made then rather than inherited.
- Smaller: `CGBNotSupportedException` (a Game Boy Color error in a frontend with
  no Game Boy core), and 37 image resources plus 35 image files left orphaned by
  this and the previous passes.

### The menus say what they mean (2026-08-13)

- **Emulation → System.** The menu holds Pause, Reboot Core, the dump status and
  what is loaded: that is the machine, not "emulation" as an activity.
- **Soft Reset and Hard Reset are gone**, menu items and hotkeys both. They only
  ever clicked a controller button named "Reset" or "Power" - buttons a package
  declares like any other, and bindable in the controller config. A frontend
  command that presses a button on your behalf is a leftover from when the
  frontend knew what a console had.
- **Emulator > Core is gone** (user, and rightly - I should not have built it).
  A core is not a setting to pick from a list, it is the machine; you choose it by
  opening its package. So File > Open Core now DOES choose: every system the
  package claims will open its roms with the cores it brought. Startup discovery
  deliberately does not, so what is sitting in `Cores/` can never silently
  reassign what you opened. `CoreChoices` is down to `MakeDefault`.
- **Tools lost Debugger, Trace Logger and Surface Viewer**: they are core-provided,
  and the Emulator menu is where the running core's tools belong. Listing them
  twice implied they were the frontend's.
- **Virtual Pad is gone entirely.** Its schemas come from core factory assemblies
  (BizHawk shipped one per console); the generic waterbox adapter has none, so the
  window could only ever be empty. With it went the eight "Analog" hotkeys that
  nudged values inside it, and `vpads_schemata` from Emulation.Common.

## Firmware is declared by the core, not known by the frontend (2026-08-13)

The frontend deleted its firmware manager along with the rest of the bundle
furniture: a table of every BIOS for every console, in a program that does not
know what cores exist. But some machines genuinely need a file the core cannot
ship - the Famicom Disk System's RAM adapter boots from an 8 KiB rom, and no disk
image will run without it - so the channel came back the other way up, and the
user decided its shape.

- **The package declares what it wants.** `waterbox.config` gains a `firmware`
  list: an id, a display name, a sentence about what breaks without it, the exact
  size, and the SHA1s of the dumps known to be right. Nothing about this lives in
  the frontend, which cannot name a single firmware file on its own.
- **The user provides it once, and it is remembered** under `<core name>/<id>` -
  keyed by core rather than by package hash, so rebuilding a package does not make
  you find your BIOS again.
- **`Emulator > Firmware`**, greyed out when no loaded package expects anything.
  It is the one item in that menu reachable with nothing loaded, because a rom
  that needs a BIOS cannot be loaded until the BIOS is there; opening it after the
  fact would be a door that only unlocks from the inside.
- **A wrong file is refused, an unknown one is not.** Wrong size means the wrong
  file and the core never sees it. A right-sized file whose hash is not on the
  list is used anyway and flagged: a good dump the declaration has never seen is
  likelier than a frontend that should refuse to run.
- **Missing means a sentence, not a stack trace.** `MissingFirmwareException` is
  its own type for exactly this: it is the one load failure the user can fix, and
  the message names the window that fixes it.
- **Config > Firmware surveys every installed core** (2026-09-05, issue #30). Until
  it, firmware was only ever asked about from inside: the Emulator menu's window
  needs a loaded package, the wizard asks per project, and a downloaded project
  that wanted a bios could only say "put it in the Firmware folder". The survey
  reads each package's `waterbox.config` in the Cores folder when the window
  opens, lists one row per declaration entry grouped by core - PCSX2's
  seventy-three bios releases are seventy-three rows, collapsed to the ones on
  hand - and answers each from the Firmware folder and from every path a person
  ever chose, by hash. Nothing is kept: the rows go with the window and a
  deleted package is gone on the next open (the frontend keeps no list of
  firmware, and cannot). What a person chose for it stays in config.ini, keyed
  by the core's name, for the day the package is put back. A chosen file is
  remembered where it lives, under `<core>/<id>#<sha1>` so several releases of
  one id can all be known, and never copied - firmware can be a PlayStation 3
  update or an Xbox disk image, and a second copy is a second thing to drift.
  The wizard and Open Project index every remembered dump of the core, so a
  file located in the survey satisfies them wherever it sits.
- **Delivery is a mount.** The file is handed to the guest as another mounted
  file, under the declared id, next to the rom and the settings - so the guest
  reads it with the same call it reads the rom with, and nothing in the ABI is
  special-cased for firmware.

What is deliberately NOT here: any suggestion that the frontend knows what a
firmware file means. It checks size and hash because the declaration told it what
to check, and hands over bytes. Whether they are the right bytes for the machine
is the core's business.

## Storing progress: cleared to the ground (user-decided, 2026-08-25)

Two designs for "what a machine keeps" have now been removed, and the ground is
deliberately bare while a third is designed.

The first was BizHawk's: a SaveRAM file per rom, an autosave timer, a per-system
save directory, and `StartsFromSaveRam` putting a copy of the save inside the
movie. It was replaced on 2026-08-14 by a core-declared persistent-data channel
(`ICorePersistentData`, guest `GetPersistent*`/`PutPersistent`) plus `.gameBundle`
catalogues that named a rom and its attachments, with a movie citing the bundle
it was recorded against.

That second design is now gone too, at the user's decision: the frontend must not
be the thing that decides when progress reaches the disc. Removed with it: the
persistent-data channel and its guest ABI group, bundles (the format, the engine's
`ce_bundle_*` ABI, the loader path, the compose/write-back UI), the automatic
write-back on rom close, the `Bundle`/`BundleID` movie header keys, and the gates
that witnessed them. The cores lost their `GetPersistent*` exports and the test
runners their `--saveram-in`/`--saveram-out` flags.

What holds until the replacement lands: **a rom boots clean, every time, and
nothing is written beside it.** A machine's progress lives only in savestates,
which are explicit and belong to the user. Nothing in the frontend knows what an
SRAM is - and this time nothing knows what a save is at all.

## Save data: the core keeps it, the user takes it out (user-decided, 2026-08-25)

The third design, specified in full in docs/save-data.md. The short form: a
core with save data keeps it INSIDE the guest machine (in-guest storage is
savestate- and rewind-correct by construction, and the sealed baseline plus
page-dirty tracking makes even a 2 GB disk image cost only its dirtied pages
per state - the proven BizHawk/DOSBox recipe). Getting progress OUT is one
user action: `Emulator > Export Save Data...`, enabled whenever the core
exports the savedata group - the sixth optional guest ABI group
(`GetSaveDataFileCount/Name/Size/Buffer`) - with no change detection and no
automation; the frontend writes the core's own `(path, bytes)` enumeration as
a zip without interpreting a byte. Getting it back IN is a game input: the
exported files return through a multi-file game descriptor (a future design
the user owns), mounted hash-bound and cited by the movie header like any rom
or firmware. Cores whose save data is plain machine memory (NES SRAM) export
nothing and need nothing. First tenant: PPSSPP's RAM memstick.


## Wide input: a controller is as wide as it declares (2026-08-25)

The packed uint64 button mask was an accident of the first cores, not a
design: a DOS machine's keyboard is 101 keys before its mouse and joysticks
exist. The movie format never had the limit - an entry carries one mnemonic
column per declared button, any count - so only the transport was narrow, and
the fix is additive:

- The ENGINE keeps button state as a byte per declared button. The entry
  layer (movie_entry) parses and generates wide; `1ull << index` is gone.
- `ce_session_set_button(index, pressed)` mirrors set_axis: per frame, before
  the advance, values persist until changed. The effective state at an
  advance is the packed mask's low 64 OR'd with these - either path alone is
  exact, and record mode writes the effective state, exactly what the
  machine received.
- The GUEST side stays core-agnostic: a wide core exports
  `SetButton(index, state)` next to SetAxis, and the session delivers only
  CHANGES across the boundary. A config declaring more than 64 buttons
  refuses to open without the export - a controller that silently drops keys
  is worse than one that refuses to load. Loadstate (and greenzone seeks)
  reset the delta tracker to resend everything, because the guest's input
  latches are guest memory and the restore just rewrote them - the same
  lesson the trace flag taught.
- The FRONTEND packs the low 64 exactly as before for narrow cores; a wide
  core's adapter drives every button through set_button and passes mask 0.

First tenant: chimera-core-dosbox-x. Witnessed by the widened
test_movie_entry (a 101-button round trip across the 64 boundary) and by
every existing gate staying green - the packed path's behaviour is pinned
unchanged.

## Opening a core is something you do (2026-08-14)

Packages sitting in `Cores/` used to load themselves at startup. That made the
folder the decision-maker, and it showed: with nothing open at all, the Emulator
menu already had a Firmware item in it, because the descriptors were already
loaded.

- **`File > Open Core...` is the package list** (was "Core Packages..."), with an
  Open button. Discovery scans the search directories and LISTS what it finds;
  opening one is what loads it. Rows read *available*, *loaded* or an error - a
  package that will not parse still appears, because "why is my core not here"
  needs an answer that is not the console.
- **The Emulator menu is empty until a core is loaded**, and disabled when empty.
  Everything in it comes from a core: with a package open it holds that package's
  Firmware (declared by the descriptor, so it is known before any rom); with a
  machine running it also holds Settings, the core's tools, and what the machine
  keeps.
- **The command line still runs a rom in one step.** Naming a rom is an
  instruction to run it, so if no `--core` was given and nothing is open, the
  available packages are loaded to route it. Nothing like that happens in the
  GUI, where the point is that the choice is yours and visible.

## A refusal is a sentence, not a stack trace (2026-08-14)

Opening an FDS image with no BIOS produced a dialog with an exception and a
stack trace in it. Everything needed to say something useful existed - the core
knew precisely what was wrong - but its words went to stdout and died there,
because the only thing crossing the sandbox was "Init returned 0".

- **The core explains itself.** Optional guest export `GetLoadError`: after a
  failed Init the host reads the reason and shows it. The frontend cannot write
  that sentence - it does not know what a disk image is - so it repeats the
  core's.
- **`CoreLoadException`** is the type for a failure the user can act on
  (`MissingFirmwareException` now derives from it). RomLoader shows those as a
  plain message. Anything else still gets its stack trace: that is a bug report,
  not a configuration problem.
- **The same rule for the frontend's own refusals**: "no loaded core can run a
  NES game" now names the window that fixes it instead of arriving as an
  InvalidOperationException.
- **The command line is strict too** (user): naming a rom with no core open and
  no `--core` says so and stops. Choosing a core is never implicit, on either
  side of the GUI.

## The firmware window shows both hashes (2026-08-14)

What a person does in that window is compare what the core expects with what
they have, so the window does that comparison for them and shows its working:

- a **mark per row** - green tick (matches a dump the core names), amber warning
  (right size, hash the core has never seen; used anyway), red cross (wrong size
  or unreadable; refused), empty circle (nothing provided yet);
- **Expected SHA1** and **Actual SHA1** columns, first eight characters - enough
  to see a difference at a glance;
- the **full hashes** for the selected row underneath, because eight characters
  are enough to spot a mismatch but not enough to trust a match.

The hash of a provided file is computed even when the file will be refused: "you
gave me a 4KB file" is much less useful than showing what it actually is.

## What a package IS: reproducible bytes, versioned by a commit (2026-08-14)

A package's SHA1 was the core's whole identity, and it changed on every rebuild -
the zip stores mtimes, so the same sources produced a different "machine" every
time. A movie recorded on Tuesday warned against Wednesday's rebuild of the same
commit. Two changes, and they need each other.

**Deterministic packaging.** Fixed timestamp (1980-01-01), fixed permissions,
sorted entries, pinned compression level. The guest ELF was already reproducible;
only the container was not. `build-package.sh` packs a second time and compares
before it will publish, because this is exactly the kind of promise that rots
without anyone noticing.

**The commit is the version** (user-decided). The automated build that publishes
an artifact passes `CORE_VERSION=<commit>`, which is stamped into the packaged
`waterbox.config` - the repo's copy carries no version, since a file under
version control has no business holding a number that changes with every commit.
A package built by hand stamps `<commit>+local` (or `-dirty`), so it can never be
mistaken for a published one, and CI publishes the zip only from a job whose
gates passed.

**Both, in a movie.** `CoreVersion` is the commit - meaningful, lookup-able, what
a person acts on. `CorePackageSHA1` is the exact bytes - which BUILD of that
commit ran. When the version matches but the hash does not, the frontend says so
in those words ("same core version, but a different build of it"), because that
is the ordinary case of someone building the core themselves.

**Identity is relative to a toolchain**, and that is honest rather than
unfortunate: the same sources through a different gcc are different code and
could in principle emulate differently. Rather than pin a vendored toolchain, the
package SAYS which one it used (user-decided): `build.json` records the source
(origin, commit, dirty), the toolchain (gcc, libstdc++, binutils, musl, target),
the OS it was built on, and the exact compile and link flags. A hash that differs
is then a question with its answer inside the file.

Everything in that record is a function of the INPUTS. No timestamps, no
hostname, no absolute paths - the linkscript is written as a placeholder, because
where a machine keeps the guest kit is not something a rebuilder needs. Anything
time- or machine-dependent would make two builds of one commit differ, which is
the property all of this exists to keep.

## The whole pipeline accounts for itself (2026-08-14)

A run is reproducible only if every binary in the path can say where it came
from. Three could not, in different ways, and a movie recorded none of them.

- **The core package** carries `build.json`: source (origin, commit, dirty),
  toolchain (gcc, libstdc++, binutils, musl, target), the OS it was built on, and
  the exact compile and link flags.
- **The waterbox host** answers `wbx_build_info()` with its own commit, compiler,
  OS and target - compiled in as defines, since two build systems compile those
  sources. The sandbox is meant to change no emulation; that is a claim to be
  checkable, not asserted.
- **The frontend** already stamped its commit (`GIT <branch>#<hash>`), and now
  says so on the console at startup, where a log keeps it.
- **Firmware** is recorded per movie as `<id>=<sha1>` pairs in a canonical order.
  A disk system with a different BIOS is a different machine, so a movie without
  this was never reproducible; the ordering is fixed so replay reports a
  difference only when the machine really differs.

A movie header therefore carries: the frontend version, the core name, its
version (the commit) and package SHA1, the waterbox host's build, the firmware
hashes, and - when the game came from one - the bundle and its content id. Each
mismatch reports itself in its own words, because "something differs" is not
actionable and "your BIOS is not the one this was recorded with" is.

The discipline that makes all of it hold: **everything recorded is a function of
the inputs**. Not a timestamp, not a hostname, not a path. The moment provenance
starts describing the moment rather than the ingredients, two builds of one
commit stop matching and every hash in the chain becomes noise.


## A long wait says what it is waiting for (user-asked, 2026-09-05)

Opening, creating and saving a project could take a long time with nothing on
screen - a PlayStation 3 boot fetching its compiled code, a four-gigabyte disc
being hashed, a greenzone of hundreds of megabytes going through zstd - and a
frozen window is a window a person kills. The work stays on the UI thread, as
it must (the emulator and the engine are not shared across threads), so the
answer is not a worker but a report: the engine gains one progress sink
(`ce_progress_set`), and its slow calls report into it as they go - hashing a
file says the file's name and bytes of its length, the state container says
bytes compressed or read, a boot says its stage, the cache bridge counts the
objects it fetches. The frontend's own steps report into the same stream
(`EngineProgress`), and a small `ProgressDialog` listens while it is up, draws
a bar when the stage has a length and a clock when it does not, pumps the
message loop from those reports, and disables its owner so a click during the
wait cannot start a second operation inside the first. The engine test hashes a
nine-megabyte file with a sink installed and checks the last report reaches the
end, and that nothing is heard when no sink is set.

## Turbo: a frame nobody looks at does not have to be drawn (user-asked, 2026-08-29)

Fast-forwarding to the interesting part of a run spends most of its time drawing
pictures that go past faster than a person can see. The frontend already asked
for those frames with `render: false`, but that only skipped the memcpy out of
the sandbox - the core drew every one of them anyway. Turbo is the other half:
an optional guest export, `SetRenderingEnabled(int on)`, that tells the core
itself to stop.

**The contract is one sentence: while off, the core produces no picture and is
otherwise exactly the machine it would have been.** Nothing else is allowed to
change - not a byte of memory, not a sample of audio, not the lag count. A turbo
that got this wrong would corrupt a TAS silently, which is the one failure this
project cannot ship.

**Every core's gate proves it, the same way.** `run-gate.sh` grew a turbo leg:
run the frames twice, once drawing them all and once with drawing off for the
first half and back on for the second, then compare everything except the
whole-run video hash - memory domains, audio, lag count, and *the pictures of
that second half*. That last part is what makes it a real test rather than a
formality: a core whose skip disturbed the machine would still be caught by a
different picture even when every byte of RAM agreed.

Two cores need a one-frame settle window before that comparison starts, and both
earn it by measurement rather than assertion. A PS2 field is woven with the
field before it, so the first frame drawn after a gap has nothing to weave with;
DOSBox-X redraws a row only when it differs from the last row it drew, so the
frame that resumes is the one that rebuilds the surface. Both converge on the
very next frame, and both runs skip the same one, so nothing else is excused.

**What can be skipped is decided per core, by where the picture stops being the
machine's business.** The answer is different every time and the gate is what
settles it:

| core | what turbo skips | share of a frame |
|---|---|---|
| DOSBox-X | the copy out of the finished surface | 33% |
| snes9x | `IPPU.RenderThisFrame`, upstream's own, which still ORs the sprite range/time-over flags into `PPU.RangeTimeOver` | 24% |
| stella | `TIA::renderPixel` and the line clone. The collision latches are updated elsewhere; the frame-layout detector still gets its colours, because that IS the machine | 14% |
| quickerNES | the PPU's pixel output (upstream's own render-off path, which keeps sprite-zero hit exact through a mini offscreen buffer) | 8% |
| NesHawk | the pixel pipeline, which is the only thing that writes `xbuf`; the sprite-zero hit is decided elsewhere | 8% |
| gpgx | `remap_line` - the VDP's output step. Everything the 68000 notices (sprite overflow and collision, the pattern cache, the sprite parse) happens before it | 6% |
| opera | the VDLP's scanline renderer - the 3DO's display processor, which only reads what the cel engine already wrote into VRAM | 5% |
| PPSSPP | the framebuffer readback and its conversion, and nothing else | 3% |
| PCSX2 | the display stage - the PCRTC merge and the deinterlace. Under the OpenGL renderer that is also where the per-frame readback lives | 0% (software renderer) |
| flycast | the whole rasterisation, refsw and OpenGL alike. A render-to-TEXTURE pass is never skipped: that one is written back into video memory and the game reads it | not measurable here |

The last column is the share of a frame that turned out to be drawing, measured
by running each core's own gate content twice with half the frames undrawn. It
is not a promise about any particular game: flycast's rasteriser runs only when
a program submits a display list, and the gate's test programs submit one, so
that row waits for a real disc. PCSX2's zero is the software renderer, where the
display stage is a reference into GS local memory; the hardware renderer, where
that stage is GL work and a readback, is unmeasured on a box with no GPU.

**The 3D consoles gain the least, and the reason is worth stating.** A PSP or a
PS2 draws into memory the game can read back - VRAM is a memory domain, the
texture cache samples from it, the CPU can DMA out of it. So on those machines
the drawing IS the machine, and skipping it is not a fast-forward but a
different emulation. What turbo can take there is the display stage on top. The
Dreamcast is the exception in the other direction: Flycast's rasteriser writes
nothing back into video memory unless the pass is a render-to-texture, so the
whole of it can go. DOSBox-X was tried the deep way too - upstream has its own
`render.disablerender` switch - and the gate refused it: conventional memory and
physical RAM both moved, because the RENDER layer is wired into the VGA's event
scheduling. That is a measurement, and it is why DOS gets the thin cut.

**Where the flag lives matters.** It is the frontend's policy for the moment,
not part of the machine, so it goes in `ECL_INVISIBLE` memory - the same rule
the live-settings buffer follows, and for the same reason: a state saved while
fast-forwarding must not put the machine back into turbo when it is loaded to be
looked at. Where a core has its own knob inside the savestate (NesHawk's PPU,
snes9x's `IPPU`, PPSSPP's driver), the frame re-asserts it from the invisible
flag rather than trusting what a loaded state left behind, and the engine
forgets what it last sent after every state load.

**The engine sends deltas.** `ce_session_frame_advance`'s `render` argument now
reaches the core, but only when it changes - the seek path advances thousands of
frames and one pointless guest call each would be the wrong kind of thrift.
`chimera-run` has always replayed movies with `render: 0`, so the witness gate
now runs every Level E movie with drawing switched off end to end and still
matches the goldens byte for byte.

**In the frontend, a seek draws only its destination.** Held Turbo keeps the
throttle's one-in-four frames, because the person is watching to see where they
are. A turbo SEEK has a destination and draws it; the frames on the way are
nobody's business, so none of them are drawn - which is where the whole feature
was aimed.

## A button is where it says it is, and something on the machine says so (user-asked, 2026-08-30)

Two reports said PlayStation 2 buttons landed in the wrong places: pressing
Circle gave Triangle, pressing L1 gave R1. That is the kind of claim nothing in
the gates could answer, because every layer between a TAStudio column and the
emulated pad is keyed by the SAME NAMES. A swap in one of them looks correct
from both ends, and the two flavours agree with each other whatever they do.

**So ask the machine.** `tests/own/padtest.elf` is a PlayStation 2 controller
tester (by jbit, free to distribute) that draws every button of a DualShock 2
with the pressure the console is reading. Hold the button the package declares
as "Circle" and exactly one readout may move: the circle. The new `pad:mapping`
gate leg does that for all sixteen, and all sixteen were already right - through
the core, and again through a project and the engine's own movie decode. The
wire was never wrong.

**What WAS wrong was the label.** TAStudio heads each column with the control's
MNEMONIC - the single character a movie log is written with - and the base table
gives L2 the 'L' it gives Left, and R2 the 'R' it gives Right. Two pairs of PS2
columns read identically, and a person clicking one of them has no way to know
which. The Analog button had no entry at all and came out as '!'. PS2 now
overrides those three, and a test holds every shipped controller to the rule
that no two of its controls may share a mnemonic - which is a property of the
movie format as much as of the window, since an ambiguous log cannot be read by
eye either.

Changing a mnemonic changes the character a NEW log is written with. Old logs
still load: an entry is decoded by position and any character but '.' means
pressed, so nothing recorded stops replaying.

**The honest remainder.** Circle-gives-Triangle was not reproduced, here or
anywhere. What was found is real and in the same neighbourhood, and it is not
proof that it was the whole of what someone saw.

## A PlayStation 2 runs programs, not only discs (user-asked, 2026-08-30)

PCSX2 has always been able to start from an executable - it is how homebrew and
every test program in the world ships - and this core could not, because it
handed every file it was given to the disc path. It now looks at the file: an
ELF is run directly with an empty tray (upstream's `elf_override`, which also
forces the fast boot it needs), anything else is a disc image.

**By the first four bytes, not the extension.** Upstream decides on the ".elf"
suffix, which is right for a file picker and wrong here: a project names its
files whatever their author called them and the slot map carries that name into
the guest verbatim. What makes a file a program is that it is an ELF.

**An .irx is refused, and says why.** IOP modules are ELF files too, so the
magic alone would boot one as if it were an EE program and watch it fail
strangely. They carry Sony's own `e_type` of 0xFF80, which makes them
recognisable in the header, and the refusal says what an .irx is for instead of
reporting a crash.

One sharp edge paid for on the way: the sandbox's file system is flat, so
`hostRoot` is always empty, and upstream's `host:` resolver leaves the path
empty when it is - which made the machine unable to read the very ELF it had
just been told to boot. In a flat file system a name IS its path.

## A guard page is not a write barrier on Windows (2026-09-10)

Seeking through the history gave back a machine the plain run never had. Only
on Windows, only with ares, and only through the greenzone: saving and loading a
full state every frame was exact, so the fault was in the page-level dirty
tracking rather than in the state format. Sixty-eight bytes of Game Boy work RAM
were wrong after seeking two hundred frames back over a four hundred frame run,
and further back than about four hundred frames the guest jumped to address
zero.

**What the sandbox was doing.** miniBox tracks writes by protecting memory: a
page that matches the baseline is read-only, the first write faults, the handler
copies the pre-image and lets the write through. Windows cannot do that for the
page the stack pointer is in - it delivers an exception by pushing a context
record onto the faulting thread's own stack, so the kernel's write fails too and
the process dies with no handler having run. ares hands every emulated component
a coroutine stack out of malloc, so its stacks looked like ordinary memory and it
died on its first frame. The fix at the time was to protect every clean page
with the guard bit instead, because the kernel clears that bit BEFORE it raises,
which makes the fault deliverable wherever the stack is.

**Why that was wrong.** A guard bit can also be cleared with NO exception
delivered at all. Instrumenting the arena found a few hundred pages an epoch
that miniBox had told Windows to protect (`VirtualProtect` returned success, and
querying straight afterwards confirmed the bit was set) and that later read back
as plain PAGE_READWRITE with no handler ever having run for them. Every write to
such a page after that is invisible, and the page still says clean - so the
frame's delta does not carry it and the baseline it will be reverted to is the
wrong one. A read-only page cannot fail that way: nothing but the sandbox itself
can make it writable.

**So the contract changed instead: a guest says where its stacks are.** Ordinary
memory is read-only-when-clean on Windows exactly as it is on Linux, and MAP_STACK
is how a guest asks for a stack. ares' libco asks for its coroutine stacks with
mmap now (patch 0015 in that core) instead of taking them from malloc. A stack
page on Windows is then never protected at all, and nothing reports its writes.

**What a stack did is READ, not watched.** The first version of this called every
stack page written whether or not it was, which is correct and costs everything:
ares gives a Game Boy nineteen coroutines a stack of their own, 2.6MB of them,
and all of it went into every delta. Seven hundred frames of history came to
268MB where Linux made 110MB, and capturing a frame cost 74% of the run against
Linux's 20%. Almost none of those pages had changed - almost none of a coroutine
stack is ever touched.

Nothing there can report a write to a stack, but anything can read one. Each
stack page keeps a shadow of what it held when the last frame was described, and
a delta asks memcmp which of them moved; "differs from the sealed image", which
is what a savestate needs, is the same question against the baseline snapshot.
Both are exact, and both are stricter than a fault bit, which stays set when a
page is written and then put back. The same seven hundred frames now cost 107MB -
SMALLER than Linux's 110MB, for exactly that reason - and 32% of the run against
Linux's 19%. What is left is one 2.6MB comparison a frame, so the remaining
difference is set by how much stack a core asks for, not by how big its machine
is.

**Two smaller things fell out of the same measurement.** An epoch marked a stack
page eagerly and then dropped it from the set it re-examines, so a stack was
captured by one frame and never looked at again; and `VirtualQuery` was being
read as if `RegionSize` were the length from the address asked about, which it is
not - it counts from the region's own base, so the walk stepped over the pages
after it.

**What this is not.** Nobody has identified what clears those guard bits. The
exception-dispatch theory - the kernel scribbling its context record onto pages
below the stack pointer - was tested by marking every page near a faulting `rsp`
and does not explain them; not one of the lost pages had ever been near one. It
does not matter for the fix, because the fix is to stop depending on the bit, but
it is not a solved mystery and should not be written up as one.

## A seek costs what changed, not what the machine could be (user-asked, 2026-09-10)

The ask was a deep look at the state history with speed as the only first-class
goal: forward capture, going back to an older frame, and the rerecord that
follows an edit. Memory and disk were welcome but second. Everything below was
measured before it was touched, on ares - Super Mario 64 (an 18 ms frame, some
300 pages written each) and a Game Boy (a 3 ms frame, some 70) - with the two
benches in `tests/perf` for the parts a core would only blur.

**Going back was proportional to the arena.** Restoring a frame is one anchor
loaded and the deltas since it applied, and applying a delta ended by
re-protecting every page there is: half a million lookups and syscalls on the
ares arena, 2.3 ms for a delta of sixteen pages, paid once per link. A restore
of 34 links took 59 ms on the N64 and 54 on the Game Boy, whose deltas are a
quarter the size - the same number, because the cost was not in the delta. The
anchor load walked the same half million pages to find the few that differed.
Both are proportional to the change now: a delta refreshes only the pages in
its two lists whose protection actually moved, and the load compares the
machine's packed status and dirty maps against the state's a word at a time,
skipping eight untouched pages per two loads. The same restores are 3 ms and
2 ms; on the bench a 2 GB seek of 32 links went from 78 ms to 1.7.

**Forward capture was paying a fault for what it already knew.** A page the
machine writes every frame - the framebuffer, the audio ring, the CPU's own
registers - faulted every frame, and was re-protected every frame so that it
could fault again. On the N64 that was two thirds of what a captured frame
cost. A page written a few frames in a row now goes HOT: it stays writable, and
what it did is found by comparing it with a copy taken when the epoch opened.
Four kilobytes copied and compared is a fraction of a microsecond; a fault is
several. A hot page that stops changing cools after a few frames and is held
like any other. The comparison is exact where a fault is not, so a page written
with the bytes it already held stays out of the delta - the Game Boy's frames
came out a third smaller, 700 of them 85 MB where they were 110. The bench's
300 pages written every frame went from 1.75 ms to 0.06. On the real cores the
history's share of the run fell from 9% to 5% (N64) and 14% to 11% (Game Boy);
what is left is the pages a frame writes for the first time in a while, and
those are what the fault is for.

The invariant is the whole design: **a hot page's shadow is the page as the
epoch opened.** Opening an epoch copies every hot page, because nothing in the
sandbox can know whether the guest ran since the last delta was saved, and the
two operations that rewrite pages from outside the guest - a state loaded, a
delta applied - refresh the copy or cool the page as they go. It was tempting to
refresh the shadow only when a delta was saved and skip the copy at open; that
is wrong the first time the engine takes an anchor instead of a delta, because
the frame between ran with no epoch and the shadow is a frame stale, and a page
written back to its older bytes would then be left out of a delta whose start
did not hold them. The copy costs a tenth of a millisecond and the reasoning
costs nothing to keep.

**The client was spending as much on a seek's frame as the core did.** A
TAStudio seek runs the main loop once per frame, and every frame refreshed the
piano roll, presented the picture and pumped the message queue - 5.9 ms a frame
on the Game Boy, of which 3.0 was the machine. The headless mode already served
the host on a wall-clock cadence instead of per frame, for exactly this reason;
a seek now does the same: sixty times a second the window is live, shows where
the seek has got to and takes a click to stop it, and the frames between are
emulated and nothing else. The destination frame is always drawn and shown.
`CHIMERA_LOOP_TRACE=1` prints where each frame's time goes, phase by phase,
because this was found by measuring and the next such thing will be too.

Two sharp edges. The frames a seek passes through get the tools' fast update
whether or not it is a turbo seek, so a Lua script that counts frames during a
seek needs "Run Lua during turbo" now as it already did during one. And on the
Xvfb box this was measured on, a TURBO seek is slower than a plain one - the
present and the message pump cost several milliseconds each there with no frame
drawn, which reads like software GL and a progress bar repainting - and that is
not understood; it is bounded to the sixty services a second now, and the GPU
box has not been measured.

**A bug was found on the way and is fixed:** applying a delta copied a page's
baseline aside AFTER overwriting it, so a page this process had never written -
one from a history file, applied to a machine that had not reached that frame
itself - kept the delta's bytes as its sealed image, and every later return to a
frame where the page was clean put those bytes back. In-process it could not
happen, because every page in a delta had faulted on the recording run and had
its copy already; a reopened project is exactly the case it could.

## What was spilled early is settled, and the seek loop measured where it runs (2026-09-10)

The three loose ends of the round above, taken in turn.

**Density on disk is a temporary condition now.** A stretch spilled before the
far band reached it kept every frame's delta on disk for good, because
`coarsen()` skipped anything spilled - 1567MB of file for six thousand Game Boy
frames under a 64MB budget, the state-manager doc's own example. Once the far
boundary passes a spilled stretch it is now settled: its links are read back
one at a time, composed down to the far grid under the same caps `tidy()` uses,
and what is left is appended to the file; the old body is dead room and the
compaction takes it back. Four links a call, so it is a little work every frame.
The first version read the whole stretch into a working copy and refused any
stretch bigger than the budget - which is every stretch that was spilled under a
small budget, the only ones that need it. Streaming is the point, not an
optimisation: what is held is one accumulating link, one just read, and the
result, which is far-band sized. On a Game Boy under an 8MB budget with tight
bands, each 61-frame stretch went from 9.5MB on disk to 1.9MB, and the file's
live bytes from 112MB to 46MB.

**The turbo-seek anomaly was Xvfb's.** Measured on the Windows box with the real
GPU, driving `Chimera.exe --headless` through interop with the same project and
Lua kicker: a plain Game Boy seek is 3.5 ms a frame and a turbo one 3.1, against
2.7 for the machine alone. The several milliseconds a present and a message pump
cost with no frame drawn were the software GL under Xvfb, and are not a Windows
problem. Two more phases in the loop trace - movie and sound - showed where the
rest of the client's time goes there: the movie's frame handling is the
greenzone capture itself (0.22 ms, the history's share of a Game Boy frame), and
the message pump at sixty services a second is the piano roll repainting, which
is what the person is watching.

**A DISPLAY that does not answer is asked first.** Both gate scripts start an
Xvfb only when DISPLAY is unset, and the dev box has DISPLAY set to an ssh
session's forwarded display with nothing behind it, so every frontend leg failed
with "Could not open display" - fourteen legs and a hundred and two window
tests, all saying the same thing. The scripts probe the display with xdpyinfo
now and bring up their own when it does not answer. The rule stands that a
caller's working display is used rather than a second one; what changed is that
"set" is not taken for "working".

**Not taken: the anchor's two bytes a page.** An anchor carries a status byte and
a dirty byte for every page of the arena - 1.1MB on ares, some 21MB on rpcs3 -
and packing them would be a savestate format change. The stream layout is fixed
in miniBox's machine spec, anchors are one in six hundred frames, and the load
already skips the untouched pages a word at a time, so the saving is a few
per cent of memory on the biggest machines against a format every saved state
and history file would have to keep reading. Left as it is, and written down so
that it is not rediscovered as an easy win.

## A test that cannot fail is not a test (user-asked, 2026-09-10)

Asked to look for bugs and latent runtime failures across the state machinery,
rather than only in the round just written. The method is worth keeping, because
the bugs it found were not ones reading the code had suggested.

**Two randomized differential tests, each against a model.** miniBox's drives
the page tracker with random writes, maps, unmaps, protections and zeroings and
asks whether an anchor plus its deltas reproduces the machine byte for byte,
seeking backwards and continuing from where it lands. The engine's does the same
to the history with random frames, restores, edits, pins, budgets, spills and
save/load round trips. Deterministic per seed, so a failure names its seed and
step and can be re-run.

**Then every fix was checked by putting the bug back.** Eight mutations, one per
fix: the test that was supposed to catch it had to fail, and the tree without it
had to pass. Three of them did NOT fail, and that was the most useful part of
the exercise - it showed two of the things being "fixed" were not reachable:
- The protection run left unclosed when a delta apply fails only ever makes
  pages LESS permissive, and the next epoch re-protects them anyway. Kept for
  the single exit, but it is not a bug fix and is not written up as one.
- The first version of the truncated-load test wrote through an epoch, and
  `epoch_begin` re-protects everything mapped writable, so it healed the damage
  before the test could see it. The exposure is a write BETWEEN the failed load
  and the next epoch, which is exactly what a refused restore leaves the session
  free to do. Written that way, it fails without the fix.

The fuzz's own first failure was a bug in the fuzz: it modelled an input edit by
changing the machine BEFORE opening the epoch, so the change sat in the epoch's
baseline and never entered the delta. A real edit diverges the next frame's
state, inside the epoch. Worth saying because the shape recurs - a model that
does not do what the thing it models does will report the difference as the
subject's fault.

**What the tests are not.** They use a machine of sixty four cells and a merge
of their own, so they say nothing about miniBox's composition or about a real
core. That is what the synthetic witness, the ares gate and the rerecord suites
are for, and all of them were run.

## Going back has to be possible even where the greenzone gave up (user-reported, 2026-09-10)

Reported from use: a rewind to a part of the movie the greenzone no longer
covered did nothing at all, and something crashed at some point.

The cause was one line of policy. The disk budget dropped the oldest stretch in
the spill file whichever one it was, so the stretch holding frame zero went like
any other; and with nothing stored at or before the target, the piano roll's
GoToFrame loaded nothing and then unpaused to seek forward from a frame already
past the one asked for. It cannot arrive. From the outside that is a rewind that
does nothing, with no message.

Three things follow, and the order matters.

**The beginning of the run is not a cache entry.** Everything else the history
holds is an optimisation - lose it and you replay - but frame zero is what makes
replaying possible at all. It is never evicted now, in memory or on disk. The
encode path had already written the invariant down ("Frame zero always has a
state") and would have thrown; the piano roll trusted it silently.

**What survives should be spread, not recent.** Keeping the newest and dropping
the oldest is the obvious policy and it is wrong for a TAS: it empties the far
past first, so the further back you want to go the less there is to go on, until
the middle of a long movie costs a replay from zero. The stretch given up is now
the one whose absence widens the gap between its neighbours least. Applied
repeatedly that thins the whole run evenly and leaves anchors across it - the
breadcrumbs the report asked for - so any frame costs one anchor load and a
bounded replay. It is the same thought the bands already apply to landings,
applied to whole stretches.

**A seek that cannot arrive is not started.** The guard above should make it
unreachable, but "unreachable because of an invariant elsewhere" is how this got
shipped in the first place, so the piano roll now says what is wrong and stays
where it is.

What is NOT done, and is the honest limit: if a project's history is lost or
belongs to another machine, the frontend still has no way to rebuild the machine
from power-on and replay - it starts from wherever the emulator is. Frame zero
being kept means that case does not arise while a session is running, but a
project reopened with no usable history has only the frames it captures from
there. Rebooting the core and replaying the movie is the missing piece.

## A getenv is not free when a renderer makes fifty thousand calls a frame (user-reported, 2026-09-10)

Reported from use: Flash under Ruffle is slow in Chimera and not in vanilla
Ruffle. Measured rather than guessed, and it was two things of about the same
size.

**The GPU bridge asked the environment a question on every crossing.**
`ce_gl_dispatch` called `getenv("CHIMERA_GL_CHECK")` per GL call, on the
reasoning - written down in the code - that a getenv is nothing beside a GL
call. Ruffle's wgpu backend makes SIX THOUSAND GL calls in an average frame and
fifty thousand in a heavy one, so the walk through the environment was being
paid fifty thousand times a frame to answer a question whose answer cannot
change while the process runs. Read once, as everything else in this codebase
that reads an environment variable already does, the machine's own frame went
from 58.8 ms to 32.9.

The lesson is not "getenv is slow". It is that "nothing beside X" is an estimate
of a ratio, and a ratio is only an argument when the count is known. The count
here is now printed by `CHIMERA_GL_TRACE`, per frame, precisely so the next such
claim can be checked rather than believed.

**And the greenzone was capturing twenty megabytes a frame** because the near
band kept every frame whatever it cost. That half is in docs/state-manager.md.

Together: 112 ms a frame to 48, which is nine frames a second to twenty-one.
What remains is the six thousand crossings themselves - each one a call out of
the sandbox - and that is a batching problem, not a constant-factor one.

## A machine's own settings were indexed against the wrong list (user-reported, 2026-09-11)

Reported from use: the Neo Geo shows no settings at all, and it used to.

The settings were there. They are declared in the package, scoped to the machine
with `when`, and `machines.json` and `waterbox.config` both carry all seven of
the Neo Geo's. What went wrong was arithmetic.

The engine answers "which settings are exposed" with a NAME and an INDEX, and
the index counts into the declaration it was handed - the package's own settings
list. The wizard then read that index out of `SettingsFor(machine)`, which is a
different list: the machine's, with the settings belonging to other machines
dropped and everything after them renumbered. A name check caught the mismatch
and threw the entry away, so nothing was ever displayed wrongly - it was simply
not displayed. Every setting a machine narrows disappeared: the Neo Geo's DIP
switches, the Game Boy's Fast Boot, the Super Famicom's PPU revisions, all of
them, on every multi-machine core.

`SettingsByDeclarationIndexFor` answers in the package's own index space - one
slot per package setting, null where the machine does not have it - and the
wizard reads that. The test pins the shape rather than the symptom, because the
symptom (nothing shown) is indistinguishable from "this machine has no
settings", which is exactly why this survived.

**Worth saying about the Neo Geo in particular**, since that is where it was
found: those seven settings ARE its DIP switches - they go straight into
REG_DIPSW - but an **AES never reads that register**. Traced over 900 frames of
two commercial games, it is read zero times, because the AES is a home console
and has no DIP switches on it. The machine that reads them is the MVS, the
arcade board, and the ares core does not offer it yet for a reason recorded in
that core's own PLAN.md: ares has no uPD4990A, so the MVS BIOS waits on a
real-time clock pulse that never comes. The switches are exposed now because
they are declared and a frontend must not silently eat a declaration; what they
do is the core's business and the core says so.
