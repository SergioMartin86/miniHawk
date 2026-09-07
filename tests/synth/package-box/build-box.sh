#!/bin/sh
# Builds synth.wbx (the waterboxed flavor (c) Synth core) and synth-run-box (its
# Level A tester) using the miniBox toolchain + host from extern/chimera-common-minibox.
#
# Prereq: build miniBox once. musl (the guest toolchain) and the host both build
# in the meson graph, so a single setup+compile is enough:
#   meson setup extern/chimera-common-minibox/build/meson-linux
#   ninja -C extern/chimera-common-minibox/build/meson-linux
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
mb="$(cd "$here/../../../extern/chimera-common-minibox" && pwd)"
mbuild="$mb/build/meson-linux"

# musl-gcc is installed to the build root; emulibc.o and the host are meson outputs.
musl_gcc="$mbuild/musl-gcc"
[ -x "$musl_gcc" ] || { echo "miniBox not built; run: meson setup $mbuild && ninja -C $mbuild" >&2; exit 1; }
emulibc_o="$mbuild/source/guest/emulibc.c.o"
[ -f "$emulibc_o" ] || { echo "emulibc.c.o missing; run: ninja -C $mbuild" >&2; exit 1; }

cflags="-fvisibility=hidden -I$mb/extern/emulibc -I$mb/source/guest/include -I$mb/extern/jsmn \
	-Wall -mcmodel=large -mstack-protector-guard=global -fno-pic -fno-pie -fcf-protection=none -O2 -DNDEBUG -std=c11"

# guest: the machine (synthcore.c, identical to flavors a/b) + the wbx wrapper
$musl_gcc -c $cflags -o "$here/synthcore.o" "$here/../native/synthcore.c"
$musl_gcc -c $cflags -o "$here/synth_wbx.o" "$here/synth_wbx.c"
$musl_gcc -o "$here/synth.wbx" $cflags -static -no-pie -Wl,--eh-frame-hdr,-O2 \
	-T "$mb/source/guest/linkscript.T" "$here/synth_wbx.o" "$here/synthcore.o" "$emulibc_o" -lgcc
echo "built synth.wbx"

# tester: links the miniBox host (headers live in source/host after the flatten)
host_lib="$mbuild/source/host/libminiboxhost.so"
[ -f "$host_lib" ] || { echo "miniBox host not built; run: ninja -C $mbuild" >&2; exit 1; }
gcc -O2 -g -Wall -I"$mb/source/host" -o "$here/synth-run-box" "$here/synth-run-box.c" \
	"$host_lib" -Wl,-rpath,"$mbuild/source/host"
echo "built synth-run-box"
