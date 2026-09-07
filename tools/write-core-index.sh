#!/bin/bash
# Publishes a core's version index: the file Chimera reads INSTEAD of asking
# GitHub's API what a core has published.
#
# Why this exists: api.github.com allows an unauthenticated address 60 requests
# an hour, one per core per question, and a 304 costs one exactly as a 200 does.
# Fifteen cores make a single "check for updates" cost fifteen of those, so four
# presses an hour is the whole budget - unusable for anybody developing. A
# release asset costs NOTHING against that limit: downloading one redirects off
# the API entirely. So each core publishes its own answer as an asset.
#
# Per-core rather than one aggregated index, deliberately: the job that creates
# a release is the job that records it, in the same repository, in the same run.
# There is nothing in between for the index to fall behind, so no dispatch to
# miss and no aggregator to break. A core's index cannot go stale with respect
# to that core's releases.
#
# The file is GitHub's own /releases shape, trimmed to the fields the frontend
# reads (CoreReleases.Parse). Keeping the shape means the frontend needs no
# second parser and no schema to keep in step with this script.
#
# It is REGENERATED from the full release list every time, never appended to, so
# a run that failed halfway leaves nothing to reconcile: the next publish writes
# the whole truth again.
#
# It is published as an asset on a PERMANENT release tagged `index`: a release
# that is created once and thereafter only has its asset replaced, never deleted
# and never re-tagged. That keeps one fixed address for the life of the core:
#
#   https://github.com/<repo>/releases/download/index/releases.json
#
# An Actions artifact would have been the obvious home and is the wrong one:
# artifacts expire, and fetching one needs the API and a token, which is the
# cost this exists to avoid. Asset downloads cost nothing against any limit.
#
# The index release excludes itself for free: the filter below keeps only
# .chimeraCore assets, and this release carries none.
#
# Usage: write-core-index.sh --repo owner/name [--tag index]
# Requires: gh (authenticated) and jq.

set -euo pipefail

repo=""
tag="index"
file="releases.json"

while [ $# -gt 0 ]; do
	case "$1" in
		--repo) repo="$2"; shift 2 ;;
		--tag) tag="$2"; shift 2 ;;
		*) echo "unknown argument: $1" >&2; exit 2 ;;
	esac
done

[ -n "$repo" ] || { echo "--repo is required" >&2; exit 2; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== asking $repo what it has published"
# per_page=100: a core with more nightlies than that is years away, and the
# frontend only ever shows what fits a version selector anyway
gh api "repos/$repo/releases?per_page=100" --paginate > "$work/raw.json"

# Trim to what CoreReleases.Parse reads. A draft is not published to anyone, and
# an asset that is not a core package is not a version anybody can install.
jq '[ .[]
      | select(.draft != true)
      | { tag_name, published_at, created_at,
          assets: [ .assets[]
                    | select(.name | endswith(".chimeraCore"))
                    | { name, browser_download_url, size, digest } ] }
      | select(.assets | length > 0) ]' "$work/raw.json" > "$work/$file"

count=$(jq 'length' "$work/$file")
echo "== $count published version(s)"
if [ "$count" -eq 0 ]; then
	echo "refusing to publish an empty index: a core with no installable version" >&2
	echo "means the release or its asset is missing, which is a failure, not an answer" >&2
	exit 1
fi

: "${GH_TOKEN:?GH_TOKEN is required to publish}"

# Created once, then never again. The release is the container; only its asset
# changes, so the download address is stable for the life of the core. The tag
# stays wherever it was first cut - it points at a commit only because a tag
# must, and nothing reads it.
if ! gh release view "$tag" --repo "$repo" >/dev/null 2>&1; then
	echo "== creating the permanent $tag release"
	gh release create "$tag" \
		--repo "$repo" \
		--title "Version index" \
		--notes "The list of published versions of this core, for Chimera's Core Manager.

Written by tools/write-core-index.sh on every publish. This release is permanent
and carries no core package: it is a fixed address for one generated file, and
Chimera reads it instead of asking GitHub's API, which allows an unauthenticated
address only 60 requests an hour.

Do not delete it - the address is what every Chimera installation asks." \
		--prerelease
fi

# --clobber replaces the asset in place, so the address does not change. There
# is a moment mid-replace where it 404s; a client that misses is told the index
# is absent and succeeds on the next press.
echo "== uploading $file to $repo:$tag"
gh release upload "$tag" "$work/$file" --repo "$repo" --clobber

echo "== published"
echo "   https://github.com/$repo/releases/download/$tag/$file"
