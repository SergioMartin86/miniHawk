#!/bin/bash
# Builds and runs epochbench (see its own header, and docs/state-manager.md).
#
# Not part of any gate: it answers a question about cost rather than about
# correctness. It is here because the numbers in docs/state-manager.md have to
# be reproducible by somebody who doubts them.
#
# Usage:
#   ./run-epochbench.sh                      # the sweep the doc table reports
#   ./run-epochbench.sh <MB> <pages> [frames] [pages every frame]  # one measurement
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
box="$root/extern/chimera-common-minibox"
lib="$box/build/meson-linux/source/host/libminiboxhost.a"
[ -f "$lib" ] || { echo "build miniBox first: meson compile -C $box/build/meson-linux" >&2; exit 1; }
out="${TMPDIR:-/tmp}/epochbench"
cc -O2 -o "$out" "$here/epochbench.c" \
	-I"$box/source/host" -I"$box/source/include" "$lib" -lpthread

if [ $# -gt 0 ]; then exec "$out" "$@"; fi
for mb in 64 256 1024 2048; do "$out" "$mb" 256 100; done
for pages in 16 64 256 1024; do "$out" 2048 "$pages" 60; done
