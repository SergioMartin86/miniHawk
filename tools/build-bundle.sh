#!/bin/bash
# Assembles a ready-to-run Chimera bundle: the frontend, its natives, every core
# package, and the licences of the whole thing.
#
# This is what a release IS, and it is in the repository rather than in someone's
# home directory on purpose: one chimera commit pins one exact bundle. The cores
# are submodules (extern/cores/*), so the commit says which core builds went in,
# and every package stamps its own provenance into build.json.
#
# Usage: tools/build-bundle.sh --platform windows|linux --out <dir>
#                              [--skip-natives] [--skip-cores]
#
#   --skip-natives   the natives and the managed solution are already built
#                    (a rebuild of the same platform)
#   --skip-cores     take the core packages already in build/Cores rather than
#                    rebuilding them. A core.wbx is OS-independent - the host
#                    maps and runs it - so the second platform of a release
#                    ships the SAME packages, byte for byte, and rebuilding
#                    them would only risk them differing.
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
platform=""
out=""
skip_natives=0
skip_cores=0
while [ $# -gt 0 ]; do
	case "$1" in
		--platform) platform="$2"; shift 2 ;;
		--out) out="$2"; shift 2 ;;
		--skip-natives) skip_natives=1; shift ;;
		--skip-cores) skip_cores=1; shift ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
done
[ -n "$platform" ] && [ -n "$out" ] || { echo "usage: build-bundle.sh --platform windows|linux --out <dir>" >&2; exit 2; }
case "$platform" in
	windows|linux) ;;
	*) echo "platform must be windows or linux" >&2; exit 2 ;;
esac

say() { printf "\n== %s\n" "$1"; }

# Every core package builds its guest against a miniBox guest kit; default it to
# the submodule the frontend itself is built against, or a stale sibling clone
# silently produces a core.wbx built against a different ABI.
export MINIBOX_DIR="${MINIBOX_DIR:-$root/extern/tools/chimera-common-minibox}"

native_dir="$root/build/meson-$platform"
if [ "$skip_natives" -eq 0 ]; then
	say "native dependencies ($platform)"
	if [ ! -f "$native_dir/build.ninja" ]; then
		if [ "$platform" = "windows" ]; then
			meson setup "$native_dir" --prefix "$root/build" --libdir dll \
				--cross-file "$root/extern/tools/meson/mingw-w64.ini"
		else
			meson setup "$native_dir" --prefix "$root/build" --libdir dll
		fi
	fi
	meson compile -C "$native_dir"
	# the Linux build installs into build/dll, which is also where the managed
	# build looks for its native neighbours
	[ "$platform" = "linux" ] && meson install -C "$native_dir"

	say "managed solution"
	dotnet build "$root/source/gui/Chimera.sln" -c Release /nodeReuse:false -p:UseSharedCompilation=false -v q --nologo
fi

say "staging into $out"
rm -rf "$out"
mkdir -p "$out/dll" "$out/Cores" "$out/Lua" "$out/Tools"
cp "$root/build/Chimera.exe" "$root/build/Chimera.exe.config" "$root/build/Chimera.xml" "$out/" 2>/dev/null || true
cp "$root"/build/dll/*.dll "$out/dll/" 2>/dev/null || true   # managed assemblies (IL, portable)
cp -r "$root"/build/Lua/. "$out/Lua/" 2>/dev/null || true
cp -r "$root"/build/Tools/. "$out/Tools/" 2>/dev/null || true

if [ "$platform" = "windows" ]; then
	cp "$native_dir"/*.dll "$out/dll/"
	# luasocket ships .so files on Linux; the Windows modules live in the cross build
	rm -f "$out"/Lua/mime/core.so "$out"/Lua/socket/core.so
	cp "$native_dir/extern/tools/meson/luasocket-mime/core.dll" "$out/Lua/mime/core.dll"
	cp "$native_dir/extern/tools/meson/luasocket-socket/core.dll" "$out/Lua/socket/core.dll"
else
	cp "$root"/build/dll/*.so "$out/dll/" 2>/dev/null || true
	cp "$root/build/ChimeraMono.sh" "$out/" 2>/dev/null || true
fi

# ffmpeg is part of the bundle, not something the frontend asks a person to go
# and find the first time they encode a video.
say "ffmpeg"
"$root/tools/fetch-ffmpeg.sh" "$platform" "$out/dll"

# ---- the cores, from the pinned submodules ----------------------------------
build_core() { # <submodule name> <package file name>...
	name="$1"; shift
	if [ "$skip_cores" -eq 1 ]; then
		for zip in "$@"; do
			[ -f "$root/build/Cores/$zip" ] || { echo "  --skip-cores, but build/Cores/$zip is not there" >&2; return 1; }
			cp "$root/build/Cores/$zip" "$out/Cores/"
		done
		return 0
	fi
	dir="$root/extern/cores/$name"
	if [ ! -f "$dir/waterbox/build-package.sh" ]; then
		echo "  core submodule '$name' is not checked out; run: git submodule update --init --recursive extern/cores/$name" >&2
		return 1
	fi
	say "core package: $name"
	( cd "$dir" && ./waterbox/build-package.sh -r "$root" > /dev/null )
	for zip in "$@"; do
		cp "$root/build/Cores/$zip" "$out/Cores/"
	done
}

build_core quickernes quickernes.chimeraCore
build_core neshawk quickerneshawk.chimeraCore
build_core ppsspp ppsspp.chimeraCore
build_core dosbox-x dosbox-x.chimeraCore
build_core gpgx gpgx.chimeraCore
build_core opera opera.chimeraCore
build_core snes9x snes9x.chimeraCore
build_core stella stella.chimeraCore
build_core flycast flycast.chimeraCore
build_core pcsx2 pcsx2.chimeraCore
build_core xemu xemu.chimeraCore
build_core dolphin dolphin.chimeraCore
build_core rpcs3 rpcs3.chimeraCore
build_core ruffle ruffle.chimeraCore
build_core eka2l1 eka2l1.chimeraCore

say "licences"
# What the bundle as a whole may be used for, computed from what its packages
# declare. A package that declares nothing stops the build rather than shipping
# a binary with no stated terms.
python3 "$root/tools/bundle-licenses.py" "$out" --chimera-root "$root"

say "build stamp"
# Every "it failed" report is only as useful as knowing WHICH build failed. This
# names the exact commits and the exact package hashes.
{
	printf "Chimera %s bundle\n" "$platform"
	printf "chimera:   %s %s\n" \
		"$(git -C "$root" rev-parse HEAD)" \
		"$(git -C "$root" log -1 --format=%s | cut -c1-60)"
	printf "\ncores (submodule commits):\n"
	git -C "$root" submodule status extern/cores/* 2>/dev/null | sed 's/^/  /'
	printf "\nguest kit:\n"
	git -C "$root" submodule status extern/tools/chimera-common-minibox 2>/dev/null | sed 's/^/  /'
	printf "\nfiles (sha1):\n"
	( cd "$out" && sha1sum Chimera.exe Cores/*.chimeraCore 2>/dev/null | sed 's/^/  /' )
} > "$out/BUILD.txt"
cat "$out/BUILD.txt"

say "verifying"
# A truncated dll is indistinguishable from a bug until you check.
bad=0
for f in "$out/Chimera.exe" "$out"/dll/* "$out"/Cores/*.chimeraCore; do
	[ -s "$f" ] || { echo "EMPTY: $f" >&2; bad=1; }
done
for z in "$out"/Cores/*.chimeraCore; do
	python3 -c "import sys,zipfile; zipfile.ZipFile(sys.argv[1]).testzip()" "$z" || { echo "CORRUPT: $z" >&2; bad=1; }
done
[ -s "$out/LICENSES.md" ] || { echo "MISSING: LICENSES.md" >&2; bad=1; }
[ "$bad" -eq 0 ] || { echo "  the bundle is NOT clean" >&2; exit 1; }
printf "  %s in %s\n" "$(du -sh "$out" | cut -f1)" "$out"
