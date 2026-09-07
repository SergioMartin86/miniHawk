#!/bin/bash
# Downloads the core packages the official cores have published, into
# build/Cores.
#
# Chimera ships no cores and builds none: it is the frontend, and the cores are
# fifteen other repositories that publish themselves (docs/core-manager.md).
# That leaves a hole its own CI has to close - a frontend change could break the
# generic waterbox adapter for every core at once and nothing here would notice
# - so CI fetches what the cores actually published and runs the package
# contract tests against them (InstalledCorePackagesTests).
#
# It is also the quickest way for a developer to get a working set of cores into
# a fresh checkout without opening the frontend.
#
# A core that has published nothing yet is SKIPPED, not an error: cores gain
# their release pipeline one at a time, and a frontend checkout is not broken by
# a core that has not caught up.
#
# Usage: fetch-cores.sh [--kind nightly|dev] [--out <dir>] [--core <id>]... [--list]
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
kind=nightly
out="$root/build/Cores"
only=""
list=0
while [ $# -gt 0 ]; do
	case "$1" in
		--kind) kind="$2"; shift 2 ;;
		--out) out="$2"; shift 2 ;;
		--core) only="$only $2"; shift 2 ;;
		--list) list=1; shift ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
done
case "$kind" in
	nightly|dev) ;;
	*) echo "kind must be nightly or dev" >&2; exit 2 ;;
esac
command -v gh >/dev/null || { echo "gh is not installed" >&2; exit 1; }

roster="$root/official-cores.json"
[ -f "$roster" ] || { echo "no $roster" >&2; exit 1; }

mkdir -p "$out"
fetched=0
skipped=""
failed=""

while IFS='|' read -r id repo; do
	[ -n "$id" ] || continue
	if [ -n "$only" ] && ! printf '%s\n' $only | grep -qx "$id"; then continue; fi

	# The newest release of the chosen kind. `dev` is one moving tag; a nightly
	# is dated, and the tags sort lexically because the date does.
	if [ "$kind" = dev ]; then
		tag=$(gh release list --repo "$repo" --limit 30 --json tagName --jq '.[].tagName' 2>/dev/null | grep -x dev || true)
	else
		tag=$(gh release list --repo "$repo" --limit 30 --json tagName --jq '.[].tagName' 2>/dev/null | grep '^nightly-' | sort -r | head -1 || true)
	fi
	if [ -z "$tag" ]; then
		skipped="$skipped $id"
		continue
	fi
	if [ "$list" -eq 1 ]; then
		echo "$id  $repo  $tag"
		continue
	fi

	# published as <id>-<version>.chimeraCore; the frontend finds packages by
	# extension, so the name it lands under is the name it keeps
	if gh release download "$tag" --repo "$repo" --pattern "$id-*.chimeraCore" --dir "$out" --clobber 2>/dev/null; then
		fetched=$((fetched + 1))
		echo "$id: $tag"
	else
		failed="$failed $id"
	fi
done <<EOF
$(python3 -c "
import json
for c in json.load(open('$roster'))['cores']:
    print(c['id'] + '|' + c['repo'])
")
EOF

[ "$list" -eq 1 ] && exit 0
echo
echo "fetched $fetched core packages into $out"
[ -n "$skipped" ] && echo "no release yet:$skipped"
[ -n "$failed" ] && { echo "failed to download:$failed" >&2; exit 1; }
exit 0
