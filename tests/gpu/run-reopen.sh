#!/bin/bash
# Builds and runs the GPU reopen check (see reopen.cpp).
#
# Not part of any gate: it needs big cores, real games and firmware, and those
# live on the machine somebody tests on rather than in this repo. It is here
# because "a core's states outlive the session that made them" is a claim every
# GPU core's package makes - gpuStatesSurviveTheContext - and a claim nobody can
# check by reading.
#
# Usage:
#   ./run-reopen.sh <core.chimeraCore> <rom> [--settings <json>]
#                   [--firmware <id>=<path>]... [--frames N] [--save-at K] [--after M]
#
# When something fails, three flags say WHICH half:
#   --in-session   reload into the session that made it: savestates alone
#   --no-state     open a second session and load nothing: reopening alone
#   --no-gl        no host GL context: is the bridge involved at all
#   --trace        which frame of the second session it died on
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
lib="$root/build/meson-linux"
[ -f "$lib/libchimera.so" ] || { echo "build libchimera first: ninja -C $lib" >&2; exit 1; }
[ $# -ge 2 ] || { sed -n '2,20p' "$0" >&2; exit 2; }
out="${TMPDIR:-/tmp}/chimera-gpu-reopen"
g++ -O2 -o "$out" "$here/reopen.cpp" -I"$root/source/engine/include" \
	-L"$lib" -lchimera -Wl,-rpath,"$lib"
exec "$out" "$@"
