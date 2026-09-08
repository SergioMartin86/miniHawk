#!/bin/bash
# Builds and runs seekbench (see its own header, and docs/state-manager.md).
#
# Not part of any gate: it needs a big core, a real game and several minutes,
# and it answers a question about cost rather than about correctness. It is here
# because the numbers in docs/state-manager.md have to be reproducible by
# somebody who doubts them.
#
# Usage:
#   ./run-seekbench.sh <core.chimeraCore> <iso> <mcpx> <bios> <hdd> [frames] [budgetMB]
#
# CHIMERA_NO_DELTAS=1  keep whole states instead - the same run, the same
#                      budget, the shape this design replaced
# CHIMERA_HISTORY_TRACE=1  say what each capture stored and what each restore
#                      cost, split into the anchor load and the delta walk
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
lib="$root/build/meson-linux"
[ -f "$lib/libchimera.so" ] || { echo "build libchimera first: ninja -C $lib" >&2; exit 1; }
[ $# -ge 5 ] || { sed -n '2,20p' "$0" >&2; exit 2; }
out="${TMPDIR:-/tmp}/seekbench"
g++ -O2 -o "$out" "$here/seekbench.cpp" -I"$root/source/engine/include" \
	-L"$lib" -lchimera -Wl,-rpath,"$lib"
exec "$out" "$@"
