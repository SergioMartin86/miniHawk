#!/usr/bin/env bash
# Cross-build the guest Mesa (softpipe + OSMesa, static, no LLVM) that the
# GL-renderer cores (pcsx2, flycast, ...) link against via -Dmesa_guest_dir.
#
# Produces:  extern/mesa-guest/build-guest2/  (the *.a archives + the
# gallium osmesa target.c.o that the cores glob and link).
#
# Point a core at it with:
#   setup-guest.sh ... -- -Dmesa_guest_dir=<repo>/extern/mesa-guest
#
# Reproducible: run from a clean checkout after `git submodule update --init
# extern/mesa-guest` and a built miniBox guest kit. Idempotent.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"                 # extern
mesa="$here/mesa-guest"
build="$mesa/build-guest2"
minibox="${MINIBOX_DIR:-$here/chimera-common-minibox}"

# The guest sysroot must be the C++ (meson-cpp) one — it carries libstdc++ and
# the C++ headers the C-only meson-linux sysroot lacks.
sr="$minibox/build/meson-cpp/guest-sysroot"
[ -f "$sr/lib/musl-gcc.specs" ] || { echo "guest sysroot not built at $sr (build miniBox's meson-cpp first)" >&2; exit 1; }
[ -d "$mesa/src/gallium" ] || { echo "mesa submodule not checked out at $mesa (git submodule update --init extern/mesa-guest)" >&2; exit 1; }
gccver="$(basename "$(ls -d "$sr"/include/c++/* | head -1)")"   # e.g. 13.3.0

# meson + its deps, in a private venv so the host Python is left alone. Mesa's
# version check needs `packaging` (py3.12 dropped distutils); mako generates its
# GL dispatch.
venv="${MESA_BUILD_VENV:-$HOME/.cache/chimera-mesa-build-venv}"
[ -x "$venv/bin/meson" ] || { python3 -m venv "$venv"; "$venv/bin/pip" -q install --upgrade pip; "$venv/bin/pip" -q install meson ninja mako packaging; }
meson="$venv/bin/meson"

# single-`-specs` wrapper compilers: passing -specs through meson's *_args
# doubles it and the specs file errors, so the flag rides inside the compiler.
cat > "$mesa/gw-cc"  <<EOF
#!/bin/sh
exec gcc "\$@" -specs $sr/lib/musl-gcc.specs
EOF
cat > "$mesa/gw-cxx" <<EOF
#!/bin/sh
exec g++ "\$@" -specs $sr/lib/musl-gcc.specs
EOF
chmod +x "$mesa/gw-cc" "$mesa/gw-cxx"

# the guest cross-file: large code model, static reloc, no %fs stack guard,
# and the guest's own libstdc++ headers.
cat > "$mesa/guest-cross.ini" <<EOF
[binaries]
c = '$mesa/gw-cc'
cpp = '$mesa/gw-cxx'
ar = 'ar'
strip = 'strip'
pkg-config = 'pkg-config'

[host_machine]
system = 'linux'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'

[properties]
needs_exe_wrapper = true

[built-in options]
c_args = ['-mcmodel=large', '-mstack-protector-guard=global', '-fno-stack-protector', '-fno-pic', '-fno-pie', '-fcf-protection=none']
cpp_args = ['-mcmodel=large', '-mstack-protector-guard=global', '-fno-stack-protector', '-fno-pic', '-fno-pie', '-fcf-protection=none', '-fexceptions', '-I$sr/include/c++/$gccver', '-I$sr/include/c++/$gccver/x86_64-linux-musl']
EOF

# softpipe + gallium OSMesa, static, no LLVM, nothing that pulls a host lib.
# -Dshared-glapi=disabled is ESSENTIAL: otherwise _glapi_tls_Context lands only
# in a .so the cores cannot link. zlib/expat are vendored via wraps.
opts="-Dforce_fallback_for=zlib,expat -Dgallium-drivers=swrast -Dvulkan-drivers= \
  -Dllvm=disabled -Dosmesa=true -Dopengl=true -Dglx=disabled -Degl=disabled \
  -Dgbm=disabled -Dglvnd=false -Dplatforms= -Dgles1=disabled -Dgles2=disabled \
  -Ddefault_library=static -Dbuild-tests=false -Dzstd=disabled -Dshared-glapi=disabled"

if [ -f "$build/build.ninja" ]; then
  "$meson" setup --reconfigure "$build" "$mesa" --cross-file "$mesa/guest-cross.ini" $opts
else
  "$meson" setup "$build" "$mesa" --cross-file "$mesa/guest-cross.ini" $opts
fi

# MESA_BUILD_CONFIGURE_ONLY=1 validates the recipe (cross-file, flags, sysroot)
# without the ~15-minute compile.
[ "${MESA_BUILD_CONFIGURE_ONLY:-}" = 1 ] && { echo "configured OK (configure-only)"; exit 0; }

# Mesa's final shared osmesa .so fails to link for the guest (__dso_handle,
# -fno-pic/large model) — EXPECTED. The cores link the static archives + the
# target's own object, both built before that step. So tolerate the .so failure
# and verify the artifacts the cores actually need.
"$meson" compile -C "$build" || true

target_o="$(find "$build/src/gallium/targets/osmesa" -name 'target.c.o' 2>/dev/null | head -1)"
archives="$(find "$build" -name '*.a' 2>/dev/null | wc -l)"
if [ -n "$target_o" ] && [ "$archives" -gt 0 ]; then
  echo "guest Mesa ready: $archives archives + $target_o"
else
  echo "guest Mesa build did NOT produce the osmesa target.c.o / archives" >&2
  exit 1
fi
