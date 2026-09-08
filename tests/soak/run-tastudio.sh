#!/bin/bash
# Play a project forward with TAStudio open, unattended (tastudio-play.lua).
#
# Not part of any gate: it needs a real project, its game and its firmware, and
# it answers "does this survive" rather than "is this right". It is here because
# a fault that only appears with the piano roll open has no other way to be
# reproduced - see docs/state-manager.md.
#
# Usage:
#   ./run-tastudio.sh <project.chimeraProject> [frames]
#
# CHIMERA_EXE      the frontend to drive (default build/Chimera.exe)
# CHIMERA_SOAK_*   passed through to the script (see its header)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
[ $# -ge 1 ] || { sed -n '2,14p' "$0" >&2; exit 2; }
project="$1"
export CHIMERA_SOAK_FRAMES="${2:-${CHIMERA_SOAK_FRAMES:-2000}}"
exe="${CHIMERA_EXE:-$root/build/Chimera.exe}"
[ -f "$exe" ] || { echo "no frontend at $exe (set CHIMERA_EXE)" >&2; exit 1; }

# mono on anything that is not Windows; a .exe runs itself there
runner=()
case "$(uname -s)" in
	MINGW*|MSYS*|CYGWIN*) ;;
	*) runner=(mono) ;;
esac

exec "${runner[@]}" "$exe" --headless \
	"--project=$project" \
	"--lua=$here/tastudio-play.lua"
