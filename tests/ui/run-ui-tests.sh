#!/bin/bash
# Runs Chimera's automated tests, including the ones that build real windows.
#
# There are two kinds and they are deliberately separate:
#   - Chimera.Tests.* : logic. No display, no emulator, no core. Everything a
#     machine can decide about the frontend's behaviour should end up here.
#   - Chimera.Tests.Client.GUI : windows. Constructs forms and drives them
#     (tick a row, press a button) to check the wiring between the widgets and
#     that logic. Needs an X display; on a headless box that is Xvfb.
#
# Neither kind can tell you a window LOOKS right. For that, --shots renders the
# windows to PNGs under tests/ui/shots/ for a person to look at.
#
# Usage:
#   ./run-ui-tests.sh              # every test project
#   ./run-ui-tests.sh --shots      # ...and write screenshots to tests/ui/shots
#   ./run-ui-tests.sh --only-ui    # just the window tests
set -u

shots=0
only_ui=0
while [ $# -gt 0 ]; do
	case "$1" in
		--shots) shots=1 ;;
		--only-ui) only_ui=1 ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
	shift
done

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
tests_dir="$repo_root/build/tests"
[ -d "$tests_dir" ] || { echo "no build/tests - run: dotnet build source/gui/Chimera.sln -c Release" >&2; exit 1; }

export LD_LIBRARY_PATH="$repo_root/build/dll:$repo_root/build:${LD_LIBRARY_PATH:-}"
export MONO_CRASH_NOFILE=1 MONO_WINFORMS_XIM_STYLE=disabled
# A project's greenzone and remembered paths live in the per-user cache, so
# without this a test run leaves its states in the real one. XDG_DATA_HOME and
# not CHIMERA_DATA_HOME: the latter moves the whole data directory, tools
# included, which is more than a test wants to change.
export XDG_DATA_HOME="$repo_root/build/tests/data-home"

# A display for the window tests. If the caller already has one (a desktop, or a
# CI step that started Xvfb), use it rather than starting a second.
xvfb_pid=""
cleanup() { [ -n "$xvfb_pid" ] && kill "$xvfb_pid" 2>/dev/null; }
trap cleanup EXIT
# A DISPLAY that is set but does not answer - an ssh session's forwarded
# display with nothing behind it, which is what the WSL dev box has - would
# fail every frontend leg with "Could not open display", fourteen times over.
# Ask it first, and bring up an Xvfb if it says nothing.
if [ -n "${DISPLAY:-}" ] && command -v xdpyinfo >/dev/null && ! timeout 5 xdpyinfo >/dev/null 2>&1; then
	echo "DISPLAY=$DISPLAY does not answer; starting an Xvfb instead" >&2
	unset DISPLAY
fi
if [ -z "${DISPLAY:-}" ]; then
	command -v Xvfb >/dev/null || { echo "Xvfb not found (apt install xvfb)" >&2; exit 1; }
	for n in 80 81 82 83 84 85; do
		if [ ! -e "/tmp/.X11-unix/X$n" ]; then
			# 1920x1200, not 1280x1024: a screenshot is taken by copying the
			# window's rectangle off the screen, so a window wider than the
			# screen fails with XGetImage returned NULL rather than a bad picture
			# - and the Cache Manager is 1460 wide before scaling, the widest
			# window here
			Xvfb ":$n" -screen 0 1920x1200x24 -nolisten tcp >/dev/null 2>&1 & xvfb_pid=$!
			export DISPLAY=":$n"
			break
		fi
	done
	[ -n "${DISPLAY:-}" ] || { echo "could not find a free X display" >&2; exit 1; }
	sleep 1
fi

if [ "$shots" -eq 1 ]; then
	export CHIMERA_UI_SHOTS="$here/shots"
	rm -rf "$CHIMERA_UI_SHOTS"
fi

if [ "$only_ui" -eq 1 ]; then
	projects=(Chimera.Tests.Client.GUI)
else
	projects=(Chimera.Tests.Common Chimera.Tests.Emulation.Common Chimera.Tests.Client.Common Chimera.Tests.Client.GUI)
fi

failed=0
for project in "${projects[@]}"; do
	exe="$tests_dir/$project.exe"
	if [ ! -f "$exe" ]; then
		printf "%-36s SKIP (not built)\n" "$project"
		continue
	fi
	# mono's X11 backend chatters about xkb keysyms on a bare Xvfb; drop that noise
	if out="$(cd "$tests_dir" && timeout 600 mono "$exe" 2>&1 | grep -vE 'xkbcomp|^> *Warning:|^Errors from xkbcomp')"; then
		summary="$(printf '%s\n' "$out" | grep -E '^  (total|failed|succeeded|skipped):' | tr -s ' ' | paste -sd' ' -)"
		printf "%-36s %s\n" "$project" "$summary"
	else
		printf "%-36s FAILED\n" "$project"
		printf '%s\n' "$out" | tail -40
		failed=$((failed + 1))
	fi
done

if [ "$shots" -eq 1 ]; then
	echo ""
	echo "screenshots:"
	ls -1 "$CHIMERA_UI_SHOTS" 2>/dev/null | sed 's/^/  /' || echo "  (none written)"
fi

[ "$failed" -gt 0 ] && exit 1
exit 0
