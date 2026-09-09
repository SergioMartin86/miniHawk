# The compile cache

A core that recompiles its machine's code, the way RPCS3 turns PowerPC into
x86 through LLVM, pays for it at every boot: minutes for a big game, gone with
the session, because a sandboxed core's files live in memory the session owns.

The cache keeps the compiled objects on the host between sessions. It is the
one kind of file a core keeps that is not machine state, and that decides
everything about it.

## Why it can be implicit

An object is a pure function of three things: the module's bytes, the core
package that compiled it (our patches shape the generated code) and the target
CPU the core pins. A boot that loads a cached object runs the same machine as a
boot that compiles it fresh: the code's meaning is the interpreter's, and the
charges that keep the machine's clock are compiled into it either way. Only
host time changes.

So nothing about determinism, movies or projects depends on whether the cache
was warm, and no user decision is involved. That is the opposite of save data,
which is explicit and bundle-driven on purpose (docs/save-data.md): there the
file changes what the machine does. A project never records cache files; when
they are missing they are made again from the same inputs.

## The channel

miniBox's `source/cache/cache-bridge.h` is the contract. A core that keeps
such files exports `SetCacheBridge(uint64_t)` and receives the host's
dispatcher before Init, through the sandbox's single callback. Two operations
cross it, each as a pointer to an argument block in guest memory:

- `CACHE_OP_FETCH` with a name and a buffer: the host copies the file in, or
  answers 0 when it has none. A core asks with no buffer first to learn the
  size.
- `CACHE_OP_STORE` with a name and bytes: the host keeps them, written beside
  and renamed, so a reader never sees half a file.

Names are relative paths under a directory the host owns for this core and
package; an absolute prefix or a `..` segment is refused. RPCS3's own object
names carry the module hash, its version and the CPU, so the same firmware
library compiles once for every game.

The engine sets the directory with `ce_cache_dir` before opening a session,
the frontend names it `<data home>/CompiledCode/<core>/<package version>/`
(see `CacheStore`; the data home is `%LOCALAPPDATA%\Chimera` or
`$XDG_DATA_HOME/chimera`), and
`ce_session_cache_stored` / `ce_session_cache_fetched` say what a session did.
Every object that crosses is announced on stdout as `[cache] stored <name>
<sha1>` or `[cache] fetched <name> <sha1>`, which is how a frontend watching a
child process learns what a game needs.

One directory per package version, because a different build of the package
generates different code and must not read the old one's objects.

It is not a configurable path and it is emphatically not inside the install.
A Chimera bundle is a zip somebody unpacks, and updating it means unpacking a
newer one: a gigabyte of a PS3 game's compiled code sitting in there would be
lost on every update, and would make the size of the install depend on what
had been played. It used to live in `<exe>/CoreCache`; an older install's is
moved out on the first run that finds it.

## Precompile sessions

Waiting for a first boot to compile is the slow way, and inside the machine's
scheduler only one thread runs at a time. A core that can fill its cache
without running exports `SetPrecompile(index, count, firmware_too)`: the
session boots, never runs, compiles every module part whose name hashes to its
index, and reports when it is done (`IsPrecompileDone`, `GetPrecompileDone`,
`GetPrecompileTotal`). The parallel form is not threads in one sandbox, where
guest threads are green threads on one host thread, but several sandboxes
side by side, each a child process of the frontend compiling its share into
the shared directory.

## Where it happens: a step in the wizard

For a core whose package says `"precompile": true`, creating a project has one
more step, after the firmware and before Create. It says what it is - this
core translates the console's code into the machine's, and doing it once now
is what makes the game fast enough to work on - and it lists the modules:
name, SHA1, and whether the object is on disk and still what it was. What is
already compiled is green when the step opens.

**Compile** runs the sessions: `Chimera --headless --precompile=INDEX/COUNT`,
this same frontend as child processes, as many as half the machine's cores up
to eight, each compiling the modules whose names hash to its index. Rows
appear as objects land, green, so the wait shows its work. The button is
offered exactly while there is compiling left to do.

**Create stays unavailable until every listed module is green.** A project
whose game is not compiled is a project that boots into a minutes-long stall,
so it is not created at all. This is the only place the cache is filled from;
the core's menu offers only "Clear Compiled Code".

What a game needs is remembered beside the objects, in
`<core>/<version>/games/<rom sha1>.json`: the list of names and hashes the
sessions produced. The project records the same list under `coreCache`, so
opening it later can say whether the compiled code is the same code. The
files themselves are never in the project: they regenerate from the game and
the package.

What this guarantees is boot time, not correctness: a module a game generates
at runtime cannot be precompiled, and the core compiles it when it meets it,
as before.

## The proof

`WizardPrecompileTests` holds the step's rules: a core that compiles nothing
is never asked, a game not compiled yet cannot be created, a compiled game
lists its modules in green and can, and a module that changed underneath is
not accepted.

The rpcs3 gate holds that the native and the sandboxed compiler produce
byte-identical objects (`cache:objects`), that a warm run fetches every object
and compiles none (`cache:warm`), and that two precompile sessions store
exactly the boot set between them (`cache:precompile`); the frontend gate does
the same through the frontend's own sessions (`precompile:frontend`). One trap
worth recording: LLVM's X86 backend emits `endbr64` into JIT code whenever the
compiler binary itself was built with CET, which Ubuntu's GCC does by default,
so the native LLVM is built without it or the two flavors' objects differ.
