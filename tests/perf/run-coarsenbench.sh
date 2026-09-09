#!/bin/bash
# Builds and runs coarsenbench (see its own header, and docs/state-manager.md).
#
# Not part of any gate: it answers a question about cost rather than about
# correctness. It is here so the table in the design log can be rebuilt by
# somebody who doubts it.
#
# Usage:
#   ./run-coarsenbench.sh                            # the sweep the doc reports
#   ./run-coarsenbench.sh <MB> <pages> <merges> <overlap%> <capMB>
#
# STREAM=1  compose through the read-callback path instead of in memory
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
box="$root/extern/chimera-common-minibox"
lib="$box/build/meson-linux/source/host/libminiboxhost.a"
[ -f "$lib" ] || { echo "build miniBox first: meson compile -C $box/build/meson-linux" >&2; exit 1; }
out="${TMPDIR:-/tmp}/coarsenbench"
cc -O2 -o "$out" "$here/coarsenbench.c" \
	-I"$box/source/host" -I"$box/source/include" "$lib" -lpthread

if [ $# -gt 0 ]; then exec "$out" "$@"; fi
for cap in 0 8; do
	for overlap in 99 90 70; do "$out" 2048 256 400 "$overlap" "$cap"; done
done
