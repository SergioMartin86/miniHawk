#!/bin/bash
# Builds and runs restorebench (see its own header, and docs/state-manager.md).
#
# Not part of any gate: it answers a question about cost rather than about
# correctness, and is here so the numbers in the doc can be reproduced.
#
# Usage:
#   ./run-restorebench.sh                          # the sweep the doc reports
#   ./run-restorebench.sh <MB> <pages> [deltas] [dirty MB]  # one measurement
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
box="$root/extern/chimera-common-minibox"
lib="$box/build/meson-linux/source/host/libminiboxhost.a"
[ -f "$lib" ] || { echo "build miniBox first: meson compile -C $box/build/meson-linux" >&2; exit 1; }
out="${TMPDIR:-/tmp}/restorebench"
cc -O2 -o "$out" "$here/restorebench.c" \
	-I"$box/source/host" -I"$box/source/include" "$lib" -lpthread

if [ $# -gt 0 ]; then exec "$out" "$@"; fi
# an anchor of eight megabytes on arenas of every size, then deltas of every size
for mb in 64 256 1024 2048; do "$out" "$mb" 256 32 8; done
for pages in 16 64 256 1024; do "$out" 2048 "$pages" 32 8; done
