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
# CHIMERA_SOAK_WINDOWED  1 to drive the real window instead of --headless
# CHIMERA_SOAK_TIMEOUT   seconds before the run is called hung (default 3600)
#
# The timeout is not paranoia. A frontend that will not EXIT is as broken as one
# that crashes and looks nothing like it - the process sits there busy, holding
# a modal question no script can answer - so a run that has to be killed is
# reported as a failure rather than waited on.
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

headless=(--headless)
[ "${CHIMERA_SOAK_WINDOWED:-}" = "1" ] && headless=()

timeout --foreground -k 10 "${CHIMERA_SOAK_TIMEOUT:-3600}" \
	"${runner[@]}" "$exe" "${headless[@]}" \
	"--project=$project" \
	"--lua=$here/tastudio-play.lua"
rc=$?
if [ "$rc" = 124 ] || [ "$rc" = 137 ]; then
	echo "run-tastudio: HUNG - the frontend never exited (killed after ${CHIMERA_SOAK_TIMEOUT:-3600}s)" >&2
	exit 1
fi
exit "$rc"
