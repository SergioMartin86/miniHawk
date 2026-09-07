#!/bin/bash
# Publishes one built core package as a GitHub release of the core's OWN
# repository. Called by .github/workflows/publish-core.yml, which every core's
# CI reuses - the logic lives here, in one file, rather than as fifteen copies
# of the same YAML drifting apart.
#
# Two kinds of release, the same two the frontend publishes (see release.yml):
#
#   dev      a ROLLING prerelease, replaced on every green push to main.
#   nightly  an IMMUTABLE dated prerelease, nightly-YYYY-MM-DD, published only
#            when main moved. NEVER deleted: a movie cites the core build that
#            recorded it, and someone replaying that run in five years needs
#            THAT package, because a package's identity is a function of its
#            toolchain and the same sources through a later gcc are different
#            bytes.
#
# The asset is named <core-id>-<version>.chimeraCore, and the version is read
# OUT OF THE PACKAGE rather than passed in: it is what the build stamped into
# waterbox.config, it is what a movie cites, and it is what the core manager
# checks the download against. Anything else is a way for the release and the
# package to disagree.
#
# Usage: publish-core.sh --package <file> --core-id <id> [--kind dev|nightly]
#                        [--sha <commit>] [--notes <file>] [--dry-run]
set -eu

package=""
core_id=""
kind=dev
sha=""
notes=""
dry=0
while [ $# -gt 0 ]; do
	case "$1" in
		--package) package="$2"; shift 2 ;;
		--core-id) core_id="$2"; shift 2 ;;
		--kind) kind="$2"; shift 2 ;;
		--sha) sha="$2"; shift 2 ;;
		--notes) notes="$2"; shift 2 ;;
		--dry-run) dry=1; shift ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
done
[ -n "$package" ] && [ -n "$core_id" ] || {
	echo "usage: publish-core.sh --package <file> --core-id <id> [--kind dev|nightly]" >&2; exit 2; }
[ -f "$package" ] || { echo "no package at $package" >&2; exit 1; }
case "$kind" in
	dev|nightly) ;;
	*) echo "kind must be dev or nightly" >&2; exit 2 ;;
esac

# ---- what this package says it is -------------------------------------------
version="$(python3 - "$package" <<'PY'
import json, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    print(json.loads(z.read("waterbox.config").decode("utf-8")).get("version", ""))
PY
)"
if [ -z "$version" ]; then
	echo "the package stamps no version; the build must set CORE_VERSION" >&2
	exit 1
fi
case "$version" in
	*+local*|*-dirty*)
		# a hand-built package is nobody else's build: publishing one would put a
		# version nothing can reproduce into somebody's movie header
		echo "refusing to publish a hand-built package (version $version)" >&2
		exit 1 ;;
esac

asset="$core_id-$version.chimeraCore"
[ -n "$sha" ] || sha="$(git rev-parse HEAD)"
date="$(date -u +%Y-%m-%d)"

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
cp "$package" "$staging/$asset"

if [ -z "$notes" ]; then
	notes="$staging/NOTES.md"
	{
		echo "Core package for [Chimera](https://github.com/ToolAssisted-run/chimera)."
		echo
		echo "Install it with **File > Core Manager** inside Chimera, which checks the"
		echo "download against what this release says it is. Dropping the file into the"
		echo "Cores folder by hand works too."
		echo
		echo "| | |"
		echo "|---|---|"
		echo "| Core | \`$core_id\` |"
		echo "| Version | \`$version\` |"
		echo "| Built from | \`$sha\` |"
		echo
		if [ "$kind" = dev ]; then
			echo "This is a **development build**: it is replaced on every change, so it"
			echo "may stop being downloadable. For a build that is kept permanently -"
			echo "which is what a movie needs to stay replayable - take a nightly."
		else
			echo "This is a **nightly**: it is never deleted. A movie recorded on this"
			echo "package can be replayed against it for as long as this repository"
			echo "exists."
		fi
	} > "$notes"
fi

echo "publishing $asset ($kind) from $sha"
if [ "$dry" -eq 1 ]; then
	echo "--dry-run: would publish"
	sed 's/^/  | /' "$notes"
	exit 0
fi

if [ "$kind" = dev ]; then
	# ONE TAG, MOVED - through the releases API, never `git push`. A workflow's
	# token may not create or update workflow FILES, and pushing a tag at a
	# commit whose .github/workflows differ from the default branch's counts as
	# exactly that. Deleting the release with its tag and recreating it goes
	# through the API, which has no such restriction.
	gh release delete dev --yes --cleanup-tag 2>/dev/null || true
	gh release create dev --target "$sha" --prerelease \
		--title "Development build ${version:0:8}" --notes-file "$notes"
	gh release upload dev "$staging/$asset" --clobber
else
	tag="nightly-$date"
	if gh release view "$tag" >/dev/null 2>&1; then
		echo "$tag already exists; a nightly is never republished"
		exit 0
	fi
	gh release create "$tag" --target "$sha" --prerelease \
		--title "Nightly $date" --notes-file "$notes"
	gh release upload "$tag" "$staging/$asset"
fi
echo "published $asset"
