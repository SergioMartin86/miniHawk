#!/bin/bash
# Turns a core submodule checkout into a standalone repository outside this tree.
#
# Chimera used to carry the cores as submodules and does not any more
# (docs/core-manager.md). A checkout that predates the split is still a
# submodule checkout: its .git is a FILE pointing into <chimera>/.git/modules,
# its config carries a core.worktree pointing back here, and every nested
# submodule has the same problem one level deeper. So it cannot simply be moved,
# and it must not simply be deleted - these repositories carry local commits
# that exist nowhere else until they are pushed.
#
# This moves one: the working tree to <dest>, its git directory in beside it, and
# every nested .git file repointed. Nothing is deleted and nothing is fetched.
#
# Usage: detach-core-checkout.sh <checkout under extern/cores> <destination dir>
#   e.g. tools/detach-core-checkout.sh extern/cores/gpgx ~/chimera-cores
set -eu

src="${1:-}"
dstroot="${2:-}"
[ -n "$src" ] && [ -n "$dstroot" ] || {
	echo "usage: detach-core-checkout.sh <checkout> <destination dir>" >&2; exit 2; }
[ -d "$src" ] || { echo "no checkout at $src" >&2; exit 1; }
[ -f "$src/.git" ] || { echo "$src is not a submodule checkout (.git is not a file)" >&2; exit 1; }

name="$(basename "$src")"
mkdir -p "$dstroot"
dst="$dstroot/$name"
[ -e "$dst" ] && { echo "$dst already exists" >&2; exit 1; }

mod="$(sed 's/^gitdir: //' "$src/.git")"
moddir="$(cd "$src" && cd "$(dirname "$mod")" && pwd)/$(basename "$mod")"
[ -d "$moddir" ] || { echo "no git directory at $moddir" >&2; exit 1; }
coremod="${mod#*.git/modules/}"

head_before="$(git -C "$src" rev-parse HEAD)"
branch_before="$(git -C "$src" rev-parse --abbrev-ref HEAD)"

mv "$src" "$dst"
rm "$dst/.git"
mv "$moddir" "$dst/.git"

# Edited as TEXT, not with `git config`: git chdirs to core.worktree before it
# will do anything at all, so once the tree has moved it cannot even unset the
# line that is wrong.
sed -i '/^\tworktree = /d' "$dst/.git/config"

python3 - "$dst" "$coremod" <<'PY'
"""Repoints the nested submodules' .git files.

Derived from the old paths rather than recomputed. Nested submodules NEST their
modules directories - a submodule at a/b inside one at a lives at
.git/modules/a/modules/b, not .git/modules/a/b - and getting that subtly wrong
is how a repository ends up pointing at nothing. Every old path contains
".git/modules/<this core's module path>/", and what follows it is the path
INSIDE this core's git directory, which is exactly what survived the move.
"""
import os, sys

repo, coremod = os.path.abspath(sys.argv[1]), sys.argv[2]
marker = ".git/modules/%s/" % coremod.strip("/")
fixed = unmapped = missing = 0
for dirpath, dirnames, filenames in os.walk(repo):
    if ".git" in os.path.relpath(dirpath, repo).split(os.sep):
        dirnames[:] = []
        continue
    if ".git" not in filenames:
        continue
    gitfile = os.path.join(dirpath, ".git")
    with open(gitfile) as f:
        old = f.read().strip()
    if not old.startswith("gitdir: "):
        continue
    target = old[len("gitdir: "):]
    if marker not in target:
        print("  UNMAPPED %s -> %s" % (os.path.relpath(dirpath, repo), target), file=sys.stderr)
        unmapped += 1
        continue
    moddir = os.path.join(repo, ".git", target.split(marker, 1)[1])
    if not os.path.isdir(moddir):
        print("  NO GIT DIR for %s" % os.path.relpath(dirpath, repo), file=sys.stderr)
        missing += 1
        continue
    with open(gitfile, "w") as f:
        f.write("gitdir: %s\n" % os.path.relpath(moddir, dirpath))
    cfg = os.path.join(moddir, "config")
    if os.path.exists(cfg):
        with open(cfg) as f:
            lines = f.readlines()
        kept = [l for l in lines if not l.strip().startswith("worktree = ")]
        if len(kept) != len(lines):
            with open(cfg, "w") as f:
                f.writelines(kept)
    fixed += 1
print("  repointed %d nested submodule(s)%s" % (
    fixed, "" if not (unmapped or missing) else "; %d unmapped, %d without a git dir" % (unmapped, missing)))
PY

head_after="$(git -C "$dst" rev-parse HEAD)"
branch_after="$(git -C "$dst" rev-parse --abbrev-ref HEAD)"
[ "$head_before" = "$head_after" ] || { echo "$name: HEAD changed ($head_before -> $head_after)" >&2; exit 1; }
[ "$branch_before" = "$branch_after" ] || { echo "$name: branch changed" >&2; exit 1; }
git -C "$dst" status --porcelain >/dev/null || { echo "$name: the moved repository is not readable" >&2; exit 1; }

echo "$name -> $dst  ($branch_after ${head_after:0:8})"
