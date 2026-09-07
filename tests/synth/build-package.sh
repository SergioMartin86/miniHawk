#!/bin/sh
# Builds the waterboxed Synth core package and the test roms, and installs the
# package into <ChimeraRoot>/build/Cores/.
#
# Chimera is WATERBOX-ONLY: the only shipped core package is the waterboxed
# flavor. A package is just core.wbx (fixed name) + waterbox.config; it is loaded
# by Chimera's built-in generic WaterboxCore adapter through libminiboxhost,
# which ships with the frontend in build/dll (see the root meson.build). There is
# no per-core managed assembly, manifest, or natives list.
#
# native/ (synthcore.c + synth-run) is kept ONLY as the offline golden generator
# for the witness (built by run-witness.sh); it is not a core.
#
# Prereq: the frontend has been built (build/dll has libminiboxhost + the contract
# DLLs including the built-in WaterboxCore).
#
# Usage: ./build-package.sh [-c Configuration] [-r ChimeraRoot]
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
configuration=Release
chimera_root="$here/../.."
while getopts "c:r:" opt; do
	case "$opt" in
		c) configuration="$OPTARG" ;;
		r) chimera_root="$OPTARG" ;;
		*) exit 2 ;;
	esac
done
chimera_root="$(cd "$chimera_root" && pwd)"

# test roms
for src in "$here"/roms/*.sasm; do
	python3 "$here/asm.py" "$src" "${src%.sasm}.testrom"
done

# build the miniBox host + guest toolchain (musl builds in the meson graph), then
# the guest core.wbx.
mb="$chimera_root/extern/tools/chimera-common-minibox"
mbuild="$mb/build/meson-linux"
[ -f "$mbuild/build.ninja" ] || meson setup "$mbuild" "$mb"
ninja -C "$mbuild"
sh "$here/package-box/build-box.sh"

# package = core.wbx (fixed name) + waterbox.config, nothing else.
staging="$here/package-box/bin/package-staging"
rm -rf "$staging"
mkdir -p "$staging"
cp "$here/package-box/synth.wbx" "$staging/core.wbx"
cp "$here/package-box/waterbox.config" "$staging"
# the bindings the package declares for its controller (Chimera ships none of its own)
cp "$here/package-box/default_keybinds.json" "$staging"

# The version a real package stamps, so the test core behaves like one: the
# manager files packages by version, a movie cites it, and a package with none
# cannot be told apart from the next build of itself. Built by hand, so it says
# so (see chimera docs: commit-as-version, stamped by CD).
core_version="${CORE_VERSION:-}"
if [ -z "$core_version" ]; then
	if commit="$(git -C "$chimera_root" rev-parse --short=12 HEAD 2>/dev/null)"; then
		git -C "$chimera_root" diff --quiet HEAD 2>/dev/null || commit="$commit-dirty"
		core_version="$commit+local"
	else
		core_version="unversioned+local"
	fi
fi
python3 - "$staging/waterbox.config" "$core_version" <<'PYVER'
import json, sys
path, version = sys.argv[1], sys.argv[2]
with open(path) as f:
    cfg = json.load(f)
cfg["version"] = version
with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
PYVER

cores_dir="$chimera_root/build/Cores"
mkdir -p "$cores_dir"
zip_path="$cores_dir/synth-box.chimeraCore"
rm -f "$zip_path"
# The package's SHA1 is the core's identity: it is what a movie records to say which
# machine produced it. So the same sources must produce the same bytes - which an
# ordinary zip does not, because it stores each file's mtime and mode. Entries are
# written sorted, with a fixed timestamp, fixed permissions and a pinned compression
# level; the guest ELF is already reproducible on its own.
python3 - "$staging" "$zip_path" <<'PYEOF'
import os, sys, zipfile

staging, zip_path = sys.argv[1], sys.argv[2]

# 1980-01-01 is the earliest a zip can express, and the point is that it never moves
FIXED_DATE = (1980, 1, 1, 0, 0, 0)


def write_package(path):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for root, dirs, files in os.walk(staging):
            dirs.sort()
            for name in sorted(files):
                full = os.path.join(root, name)
                info = zipfile.ZipInfo(os.path.relpath(full, staging), date_time=FIXED_DATE)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.create_system = 3  # unix, so the mode below is what is stored
                info.external_attr = 0o644 << 16
                with open(full, "rb") as f:
                    z.writestr(info, f.read())


write_package(zip_path)

# A self-check, because "identical sources give an identical package" is a promise
# that rots silently: pack a second time and compare.
import hashlib
import tempfile

with tempfile.NamedTemporaryFile(suffix=".zip") as tmp:
    write_package(tmp.name)
    again = hashlib.sha1(open(tmp.name, "rb").read()).hexdigest()
first = hashlib.sha1(open(zip_path, "rb").read()).hexdigest()
if first != again:
    sys.exit(f"packaging is not deterministic: {first} then {again}")
print(f"package sha1 {first}")
PYEOF
# force re-extract on next load
for cache in "$chimera_root"/build/CoreCache/synth-box-*; do
	[ -d "$cache" ] && rm -rf "$cache" || true
done
echo "packaged -> $zip_path"
