# Guest Mesa (for the GL-renderer cores)

`mesa-guest/` is a submodule pinned at **mesa-24.0.9**, the Mesa whose softpipe +
OSMesa the OpenGL-renderer cores (pcsx2, flycast, …) link into the sandbox.

It is *source*: the cores link its **cross-built** output, not the tree as-is.
Produce that output with:

```sh
git submodule update --init extern/tools/mesa-guest
export MINIBOX_DIR=$PWD/extern/tools/chimera-common-minibox   # needs its meson-cpp guest kit built
extern/tools/build-guest-mesa.sh
```

This writes `mesa-guest/build-guest2/` (static `*.a` + the gallium osmesa
`target.c.o`). Point a core's package build at it:

```sh
waterbox/setup-guest.sh -m "$MINIBOX_DIR" -- -Dmesa_guest_dir=$PWD/extern/tools/mesa-guest
```

Host prereqs: `bison flex pkg-config`. The script keeps meson/ninja/mako in a
private venv. Mesa's final shared-osmesa `.so` link fails by design (guest is
`-fno-pic`, large model); the cores link the static archives + target object,
which are built before that step.
