#!/bin/bash
# The synthetic witness driver - Chimera's own smoke/correctness test.
#
# Chimera is waterbox-only. The shipped core is the waterboxed core.wbx; the
# native C reference exists only to generate/verify the goldens (it is not a core).
# Level A: native/synth-run (the reference) records goldens; the waterboxed
#   core.wbx (via the miniBox host) must reproduce them byte-for-byte, in both
#   simple and per-frame whole-machine-savestate (rerecord) modes.
# Level E (engine): chimera-run - the engine's headless session - replays the
#   same movies with no frontend at all; its dumps must match the goldens too.
# Level B (frontend): Chimera (Mono + Xvfb on Linux) loads the core.wbx package
#   through Chimera's built-in generic waterbox adapter and replays the same
#   movies; the final RAM and VRAM dumps must be byte-identical to the Level A
#   goldens. Audio is Level A-verified only (no scriptable audio tap).
#
# Usage:
#   ./run-witness.sh              # verify (all levels, both modes)
#   ./run-witness.sh --record     # (re)record goldens from the current build
#   ./run-witness.sh --level a    # native level only (no Chimera needed)
#   ./run-witness.sh --level e    # engine level only (no Chimera needed)
set -u

record=0
level=both
while [ $# -gt 0 ]; do
	case "$1" in
		--record) record=1 ;;
		--level) level="$2"; shift ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
	shift
done

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
golden_dir="$here/goldens"
work="$here/work"
mkdir -p "$golden_dir" "$work"
failed=0
ok=0

# ---------- build ----------
gcc -O2 -Wall -Wextra -Werror -o "$here/native/synth-run" \
	"$here/native/synth-run.c" "$here/native/synthcore.c" || exit 1
for src in "$here"/roms/*.sasm; do
	python3 "$here/asm.py" "$src" "${src%.sasm}.testrom" > /dev/null || exit 1
done

report() { # name result detail
	printf "%-28s %-9s %s\n" "$1" "$2" "$3"
	case "$2" in PASS|RECORDED) ok=$((ok+1)) ;; *) failed=$((failed+1)) ;; esac
}

# ---------- Level A ----------
# native/synth-run is the REFERENCE that records the goldens (it is not a core -
# just the offline ground truth). The waterboxed core.wbx must MATCH them, which
# is the equivalence proof for the only shipped flavor.
box_tester="$here/package-box/synth-run-box"
box_wbx="$here/package-box/synth.wbx"
box_host_dir="$here/../../extern/chimera-common-minibox/build/meson-linux/source/host"
if [ "$level" = "both" ] || [ "$level" = "a" ]; then
	for movie in "$here"/movies/*.txt; do
		name="$(basename "$movie" .txt)"
		rom="$here/roms/${name%%.*}.testrom"
		golden="$golden_dir/$name.expected"
		"$here/native/synth-run" "$rom" "$movie" \
			--dump-ram "$work/$name.ram.bin" --dump-vram "$work/$name.vram.bin" \
			> "$work/$name.simple.out" || { report "A:nat:$name" FAIL "runner error"; continue; }
		"$here/native/synth-run" "$rom" "$movie" --rerecord > "$work/$name.rerecord.out" \
			|| { report "A:nat:$name" FAIL "runner error (rerecord)"; continue; }
		if ! cmp -s "$work/$name.simple.out" "$work/$name.rerecord.out"; then
			report "A:nat:$name" FAIL "rerecord diverges from simple"
			continue
		fi
		if [ "$record" -eq 1 ]; then
			cp "$work/$name.simple.out" "$golden"
			cp "$work/$name.ram.bin" "$golden_dir/$name.ram.bin"
			cp "$work/$name.vram.bin" "$golden_dir/$name.vram.bin"
			report "A:nat:$name" RECORDED "$(grep -o 'frames=[0-9]*' "$golden")"
		elif [ ! -f "$golden" ]; then
			report "A:nat:$name" NOGOLDEN ""
		elif cmp -s "$work/$name.simple.out" "$golden"; then
			report "A:nat:$name" PASS "$(grep -o 'frames=[0-9]*' "$golden")"
		else
			report "A:nat:$name" FAIL "metrics differ: $(diff "$golden" "$work/$name.simple.out" | tr '\n' ' ' | head -c 120)"
		fi

		# waterboxed flavor (c): synth.wbx run through the miniBox host, same
		# goldens. --rerecord here round-trips the WHOLE guest machine via the
		# waterbox host's save/load state around every frame.
		if [ -f "$box_tester" ] && [ -f "$box_wbx" ]; then
			LD_LIBRARY_PATH="$box_host_dir" "$box_tester" "$box_wbx" "$rom" "$movie" > "$work/$name.box.simple.out" 2>/dev/null \
				|| { report "A:box:$name" FAIL "runner error"; continue; }
			LD_LIBRARY_PATH="$box_host_dir" "$box_tester" "$box_wbx" "$rom" "$movie" --rerecord > "$work/$name.box.rerecord.out" 2>/dev/null \
				|| { report "A:box:$name" FAIL "runner error (rerecord)"; continue; }
			if ! cmp -s "$work/$name.box.simple.out" "$work/$name.box.rerecord.out"; then
				report "A:box:$name" FAIL "rerecord diverges from simple"
			elif cmp -s "$work/$name.box.simple.out" "$golden"; then
				report "A:box:$name" PASS "matches native goldens (waterboxed)"
			else
				report "A:box:$name" FAIL "diverges from native: $(diff "$golden" "$work/$name.box.simple.out" | tr '\n' ' ' | head -c 120)"
			fi
		else
			report "A:box:$name" SKIP "synth.wbx not built (run package-box/build-box.sh)"
		fi
	done
fi

# ---------- Level E ----------
# The engine's own headless runner (chimera-run): the same package, host and
# movies with NO frontend at all - no Mono, no display. Its dumps must match
# the Level A goldens, in both modes, and the settings channel must reach the
# guest. This is the migration's session (docs/engine-migration.md) proving
# it IS the same machine.
if [ "$level" = "both" ] || [ "$level" = "e" ]; then
	chimera_run="$repo_root/build/meson-linux/chimera-run"
	epkg="$repo_root/build/Cores/synth-box.chimeraCore"
	# the ABI must actually be exported - mingw once silently un-exported it
	# when a vendored library declared its own dllexports
	if [ -f "$repo_root/build/dll/libchimera.so" ] \
		&& ! nm -D "$repo_root/build/dll/libchimera.so" 2>/dev/null | grep -q ' ce_abi_version$'; then
		report "E:exports:linux" FAIL "libchimera.so does not export ce_abi_version"
	fi
	if [ -f "$repo_root/build/dll/libchimera.dll" ] \
		&& ! objdump -p "$repo_root/build/dll/libchimera.dll" 2>/dev/null | grep -q 'ce_abi_version'; then
		report "E:exports:windows" FAIL "libchimera.dll does not export ce_abi_version"
	fi

	if [ ! -x "$chimera_run" ]; then
		report "E:engine" SKIP "chimera-run not built (meson compile -C build/meson-linux)"
	elif [ ! -f "$epkg" ]; then
		report "E:engine" SKIP "package not found: $epkg (run build-package.sh)"
	elif [ "$record" -eq 1 ]; then
		report "E:engine" PASS "(goldens recorded at level A)"
	else
		for movie in "$here"/movies/*.txt; do
			ename="$(basename "$movie" .txt)"
			erom="$here/roms/${ename%%.*}.testrom"
			for emode in simple rerecord; do
				eextra=""
				[ "$emode" = "rerecord" ] && eextra="--rerecord"
				etag="$ename.engine.$emode"
				rm -f "$work/$etag.ram.bin" "$work/$etag.vram.bin" "$work/$etag.meta.txt"
				"$chimera_run" "$epkg" "$erom" "$movie" $eextra 					--dump "RAM=$work/$etag.ram.bin" --dump "VRAM=$work/$etag.vram.bin" 					--meta "$work/$etag.meta.txt" > "$work/$etag.log" 2>&1
				if [ ! -f "$work/$etag.meta.txt" ] || ! grep -q "^status=OK" "$work/$etag.meta.txt"; then
					report "E:$ename:$emode" FAIL "no OK meta (see work/$etag.log)"
				elif cmp -s "$work/$etag.ram.bin" "$golden_dir/$ename.ram.bin" 					&& cmp -s "$work/$etag.vram.bin" "$golden_dir/$ename.vram.bin"; then
					report "E:$ename:$emode" PASS "RAM+VRAM byte-identical to level A goldens"
				else
					report "E:$ename:$emode" FAIL "RAM or VRAM differs from level A goldens"
				fi
			done
		done
		# the greenzone: play to the end, seek BACK mid-movie, invalidate (as an
		# input edit would), replay to the end - the dumps must not change
		for movie in "$here"/movies/*.txt; do
			gname="$(basename "$movie" .txt)"
			grom="$here/roms/${gname%%.*}.testrom"
			gtag="$gname.engine.seek"
			rm -f "$work/$gtag.ram.bin" "$work/$gtag.vram.bin"
			"$chimera_run" "$epkg" "$grom" "$movie" --seek 10 				--dump "RAM=$work/$gtag.ram.bin" --dump "VRAM=$work/$gtag.vram.bin" 				> "$work/$gtag.log" 2>&1
			if cmp -s "$work/$gtag.ram.bin" "$golden_dir/$gname.ram.bin" 				&& cmp -s "$work/$gtag.vram.bin" "$golden_dir/$gname.vram.bin"; then
				report "E:$gname:seek" PASS "greenzone seek+replay byte-identical"
			else
				report "E:$gname:seek" FAIL "RAM or VRAM differs after seek (see work/$gtag.log)"
			fi
		done

		# the history OUTLIVES its process, which is what reopening a project
		# asks of it. One run plays the movie and keeps its history to a file; a
		# second, fresh process starts from that file, seeks back into states it
		# did not make, and replays. The dumps must still be the goldens. This is
		# the leg the old greenzone would have failed silently: it serialized
		# itself through an array that stops near 2GB, so a long history simply
		# did not save and nothing said so.
		for movie in "$here"/movies/*.txt; do
			hname="$(basename "$movie" .txt)"
			hrom="$here/roms/${hname%%.*}.testrom"
			htag="$hname.engine.history"
			rm -f "$work/$htag.ram.bin" "$work/$htag.vram.bin" "$work/$htag.hist"
			"$chimera_run" "$epkg" "$hrom" "$movie" --history-out "$work/$htag.hist" 				> "$work/$htag.save.log" 2>&1
			"$chimera_run" "$epkg" "$hrom" "$movie" --history-in "$work/$htag.hist" --seek 10 				--dump "RAM=$work/$htag.ram.bin" --dump "VRAM=$work/$htag.vram.bin" 				> "$work/$htag.log" 2>&1
			if [ ! -s "$work/$htag.hist" ]; then
				report "E:$hname:history" FAIL "nothing was written (see work/$htag.save.log)"
			elif cmp -s "$work/$htag.ram.bin" "$golden_dir/$hname.ram.bin" 				&& cmp -s "$work/$htag.vram.bin" "$golden_dir/$hname.vram.bin"; then
				report "E:$hname:history" PASS "a history kept across processes seeks to the goldens"
			else
				report "E:$hname:history" FAIL "RAM or VRAM differs after reloading the history (see work/$htag.log)"
			fi
		done

		# THINNED: bands narrow enough that the history is composing links away
		# almost every frame - what minutes of real play reach with the defaults.
		# Seeking then lands on a MERGED link, and the machine it lands on has to
		# be the machine a straight run reaches at that frame.
		#
		# The comparison is against a run stopped at frame 10, not against the
		# goldens at the end of the movie, and that distinction is the whole leg:
		# this core's ending is decided by its inputs, so a replay from a WRONG
		# frame 8 still finishes on the goldens and proves nothing.
		#
		# The target is on the coarsest band's grid on purpose. A frame the bands
		# have merged away is reached by restoring the anchor and replaying, which
		# is correct however broken the merging is - so seeking to one would prove
		# nothing either. The trace is checked as well, since without it the leg
		# would pass on a build that never merged anything at all.
		for movie in "$here"/movies/*.txt; do
			tname="$(basename "$movie" .txt)"
			trom="$here/roms/${tname%%.*}.testrom"
			ttag="$tname.engine.thinned"
			rm -f "$work/$ttag.ram.bin" "$work/$ttag.at8.ram.bin"
			"$chimera_run" "$epkg" "$trom" "$movie" --frames 8 \
				--dump "RAM=$work/$ttag.at8.ram.bin" > "$work/$ttag.ref.log" 2>&1
			CHIMERA_HISTORY_TRACE=1 "$chimera_run" "$epkg" "$trom" "$movie" --seek 8 --stop-at-seek \
				--bands 2,4,2,4,1000 --dump "RAM=$work/$ttag.ram.bin" \
				> "$work/$ttag.log" 2>&1
			if [ ! -s "$work/$ttag.at8.ram.bin" ]; then
				report "E:$tname:thinned" FAIL "the reference run wrote nothing (see work/$ttag.ref.log)"
			elif ! grep -q "merged the landing" "$work/$ttag.log"; then
				report "E:$tname:thinned" FAIL "nothing was ever merged (see work/$ttag.log)"
			elif cmp -s "$work/$ttag.ram.bin" "$work/$ttag.at8.ram.bin"; then
				report "E:$tname:thinned" PASS "seeking a thinned history lands on the real frame 8"
			else
				report "E:$tname:thinned" FAIL "RAM at the seek differs from a straight run (see work/$ttag.log)"
			fi
		done

		# RECORD mode: playback never generates an entry, so the paths that turn
		# machine input back into movie text have no other witness. Each movie is
		# replayed as an INPUT SOURCE into a recording session, which writes its
		# own log; that recording must drive the machine to the same goldens, and
		# replaying the file it wrote must land there too - record and playback
		# agreeing on the format is the whole point.
		for movie in "$here"/movies/*.txt; do
			rname="$(basename "$movie" .txt)"
			rrom="$here/roms/${rname%%.*}.testrom"
			rtag="$rname.engine.record"
			rm -f "$work/$rtag.ram.bin" "$work/$rtag.vram.bin" "$work/$rtag.txt"
			"$chimera_run" "$epkg" "$rrom" "$movie" --record "$work/$rtag.txt" \
				--dump "RAM=$work/$rtag.ram.bin" --dump "VRAM=$work/$rtag.vram.bin" \
				> "$work/$rtag.log" 2>&1
			if [ ! -s "$work/$rtag.txt" ]; then
				report "E:$rname:record" FAIL "no movie recorded (see work/$rtag.log)"
			elif ! cmp -s "$work/$rtag.ram.bin" "$golden_dir/$rname.ram.bin" \
				|| ! cmp -s "$work/$rtag.vram.bin" "$golden_dir/$rname.vram.bin"; then
				report "E:$rname:record" FAIL "recording drove a different machine than playback"
			else
				rm -f "$work/$rtag.replay.ram.bin" "$work/$rtag.replay.vram.bin"
				"$chimera_run" "$epkg" "$rrom" "$work/$rtag.txt" \
					--dump "RAM=$work/$rtag.replay.ram.bin" --dump "VRAM=$work/$rtag.replay.vram.bin" \
					> "$work/$rtag.replay.log" 2>&1
				if cmp -s "$work/$rtag.replay.ram.bin" "$golden_dir/$rname.ram.bin" \
					&& cmp -s "$work/$rtag.replay.vram.bin" "$golden_dir/$rname.vram.bin"; then
					report "E:$rname:record" PASS "recorded log replays to the same goldens"
				else
					report "E:$rname:record" FAIL "the recorded movie replays to a different machine"
				fi
			fi
		done

		# the settings channel, natively: a non-default sync setting must diverge
		"$chimera_run" "$epkg" "$here/roms/gridWalker.testrom" "$here/movies/gridWalker.win.txt" 			--settings '{"initFillByte":171}' --dump "RAM=$work/engine.settings.ram.bin" 			> "$work/engine.settings.log" 2>&1
		if [ ! -f "$work/engine.settings.ram.bin" ]; then
			report "E:settings" FAIL "run failed (see work/engine.settings.log)"
		elif cmp -s "$work/engine.settings.ram.bin" "$golden_dir/gridWalker.win.ram.bin"; then
			report "E:settings" FAIL "RAM identical to golden - setting did NOT reach the guest"
		else
			report "E:settings" PASS "RAM diverged - user setting reached the guest"
		fi
	fi
fi

# ---------- Level B ----------
if [ "$level" = "both" ] || [ "$level" = "b" ]; then
	emu_exe="$repo_root/build/Chimera.exe"

	export LD_LIBRARY_PATH="$repo_root/build/dll:$repo_root/build:/usr/lib/x86_64-linux-gnu"
	export MONO_CRASH_NOFILE=1 MONO_WINFORMS_XIM_STYLE=disabled ALSOFT_DRIVERS=null
	# The frontend keeps a project's greenzone in the per-user cache now, not
	# beside the project, so without this the witness would leave its states in
	# the real one. XDG_DATA_HOME and not CHIMERA_DATA_HOME: the latter moves the
	# WHOLE data directory, tools and all, and the encode leg then cannot find
	# ffmpeg. This moves only where per-user data is kept.
	export XDG_DATA_HOME="$work/data-home"
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
		for n in 90 91 92 93 94; do
			if [ ! -e "/tmp/.X11-unix/X$n" ]; then
				Xvfb ":$n" -screen 0 640x480x24 -nolisten tcp & xvfb_pid=$!
				export DISPLAY=":$n"; break
			fi
		done
		sleep 1
	fi

	config="$work/config.ini"
	if [ ! -f "$config" ]; then
		( cd "$repo_root" && timeout 120 mono "$emu_exe" --headless "--config=$config" \
			"--lua=$here/bootstrap-exit.lua" ) > "$work/bootstrap.log" 2>&1
		[ -f "$config" ] || { echo "config bootstrap failed (see $work/bootstrap.log)" >&2; exit 1; }
	fi
	# Chimera is waterbox-only: the frontend only accepts .wbx cores, so the box
	# flavor is the ONLY Level-B core. native/sharp are Level-A equivalence
	# references (proving synth.wbx matches the goldens), never frontend cores.
	for flavor in box; do
		package="$repo_root/build/Cores/synth-$flavor.chimeraCore"
		[ -f "$package" ] || { echo "package not found: $package (run build-package.sh)" >&2; exit 1; }
		for movie in "$here"/movies/*.txt; do
			name="$(basename "$movie" .txt)"
			rom="$here/roms/${name%%.*}.testrom"
			for mode in simple rerecord; do
				tag="$name.$flavor.$mode"
				job="$work/job.$tag.txt"
				{
					echo "movie=$movie"
					echo "outram=$work/$tag.ram.bin"
					echo "outvram=$work/$tag.vram.bin"
					echo "meta=$work/$tag.meta.txt"
					echo "mode=$mode"
				} > "$job"
				rm -f "$work/$tag.ram.bin" "$work/$tag.vram.bin" "$work/$tag.meta.txt"
				cp "$config" "$work/config.$tag.ini"
				( cd "$repo_root" && CHIMERA_JOB="$job" timeout 300 mono "$emu_exe" --headless \
					"--config=$work/config.$tag.ini" "--core=$package" \
					"--lua=$here/synth-replay.lua" "$rom" ) > "$work/$tag.log" 2>&1
				if [ ! -f "$work/$tag.meta.txt" ] || ! grep -q "^status=OK" "$work/$tag.meta.txt"; then
					report "B:$flavor:$name:$mode" FAIL "no OK meta (see work/$tag.log)"
					continue
				fi
				if [ "$record" -eq 1 ]; then
					report "B:$flavor:$name:$mode" PASS "(goldens recorded at level A)"
				elif cmp -s "$work/$tag.ram.bin" "$golden_dir/$name.ram.bin" \
					&& cmp -s "$work/$tag.vram.bin" "$golden_dir/$name.vram.bin"; then
					report "B:$flavor:$name:$mode" PASS "RAM+VRAM byte-identical to level A goldens"
				else
					report "B:$flavor:$name:$mode" FAIL "RAM or VRAM differs from level A goldens"
				fi
			done
		done
	done

	# --- settings -> guest channel ---
	# Same package + movie, but with initFillByte set non-zero via the core's SYNC
	# settings (injected into the config exactly as the UI/movie would). The guest
	# pre-fills RAM, so the RAM dump MUST diverge from the golden - proving the
	# built-in adapter delivered the user setting to the guest.
	if [ "$record" -eq 0 ]; then
		sname=gridWalker.win
		srom="$here/roms/${sname%%.*}.testrom"
		scfg="$work/config.settings.ini"
		python3 - "$config" "$scfg" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1]))
# the core's own settings key, spelled exactly as the UI writes it
cfg.setdefault("CoreSettings", {})["Chimera.Emulation.Common.Waterbox.WaterboxCore"] = {"Values": {"initFillByte": 171}}
json.dump(cfg, open(sys.argv[2], "w"), indent=2)
PY
		sjob="$work/job.settings.txt"
		{
			echo "movie=$here/movies/$sname.txt"
			echo "outram=$work/settings.ram.bin"
			echo "outvram=$work/settings.vram.bin"
			echo "meta=$work/settings.meta.txt"
			echo "mode=simple"
		} > "$sjob"
		rm -f "$work/settings.ram.bin" "$work/settings.meta.txt"
		( cd "$repo_root" && CHIMERA_JOB="$sjob" timeout 300 mono "$emu_exe" --headless \
			"--config=$scfg" "--core=$repo_root/build/Cores/synth-box.chimeraCore" \
			"--lua=$here/synth-replay.lua" "$srom" ) > "$work/settings.log" 2>&1
		if [ ! -f "$work/settings.meta.txt" ] || ! grep -q "^status=OK" "$work/settings.meta.txt"; then
			report "S:box:initFillByte" FAIL "no OK meta (see work/settings.log)"
		elif cmp -s "$work/settings.ram.bin" "$golden_dir/$sname.ram.bin"; then
			report "S:box:initFillByte" FAIL "RAM identical to golden - setting did NOT reach the guest"
		else
			report "S:box:initFillByte" PASS "RAM diverged - user setting reached the guest"
		fi
	fi

	# --- a real .chimeraProject through the real movie pipeline ---
	# Everything above drives input by script; this composes an actual project
	# file from the win inputs and lets MovieSession play it (the project IS
	# the movie, docs/project.md). The engine parses the entries, the frontend
	# latches them, and the dumps must match the same goldens - the movie
	# pipeline is Level B's reason to exist.
	if [ "$record" -eq 0 ]; then
		mname=gridWalker.win
		mrom="$here/roms/${mname%%.*}.testrom"
		mbk2="$work/$mname.chimeraProject"
		python3 - "$here/movies/$mname.txt" "$mbk2" <<'PY'
import json, sys
entries = [l.rstrip("\r\n") for l in open(sys.argv[1]) if l.startswith("|")]
logkey = "#P1 Up|P1 Down|P1 Left|P1 Right|P1 A|P1 B|P1 Select|P1 Start|"
json.dump({
    "title": "gridWalker.win",
    "core": {"name": "Synth", "version": "", "sha1": ""},
    "headers": {"MovieVersion": "Chimera Project File v1.1", "Platform": "Synth"},
    "input": "[Input]\nLogKey:" + logkey + "\n" + "\n".join(entries) + "\n[/Input]\n",
}, open(sys.argv[2], "w"))
PY
		mjob="$work/job.movie.txt"
		{
			echo "outram=$work/movie.ram.bin"
			echo "outvram=$work/movie.vram.bin"
			echo "meta=$work/movie.meta.txt"
		} > "$mjob"
		rm -f "$work/movie.ram.bin" "$work/movie.vram.bin" "$work/movie.meta.txt"
		cp "$config" "$work/config.movie.ini"
		( cd "$repo_root" && CHIMERA_JOB="$mjob" timeout 300 mono "$emu_exe" --headless \
			"--config=$work/config.movie.ini" "--core=$repo_root/build/Cores/synth-box.chimeraCore" \
			"--movie=$mbk2" "--lua=$here/synth-movie-dump.lua" "$mrom" ) > "$work/movie.log" 2>&1
		if [ ! -f "$work/movie.meta.txt" ] || ! grep -q "^status=OK" "$work/movie.meta.txt"; then
			report "M:box:$mname" FAIL "no OK meta (see work/movie.log)"
		elif cmp -s "$work/movie.ram.bin" "$golden_dir/$mname.ram.bin" \
			&& cmp -s "$work/movie.vram.bin" "$golden_dir/$mname.vram.bin"; then
			report "M:box:$mname" PASS "MovieSession playback byte-identical to goldens"
		else
			report "M:box:$mname" FAIL "RAM or VRAM differs from goldens (see work/movie.log)"
		fi
	fi

	# --- the project entry point: ONE boot, project settings honored ---
	# Chimera's whole point vs its lineage: opening a project must
	# init the core EXACTLY ONCE (rom load, TAStudio open and tasproj load
	# each cost a full boot there), and the boot must run with the
	# project's OWN sync settings - not a config default. One leg pins
	# both: a project carrying initFillByte=171 plays the win movie; the
	# final RAM must DIVERGE from the golden (the setting reached the
	# guest through the project path) and the log must show exactly one
	# "[waterbox] booting" line.
	if [ "$record" -eq 0 ]; then
		pname=gridWalker.win
		prom="$here/roms/${pname%%.*}.testrom"
		pdir="$work/project-leg"
		rm -rf "$pdir" && mkdir -p "$pdir"
		cp "$prom" "$pdir/gridWalker.testrom"
		python3 - "$here/movies/$pname.txt" "$pdir/gridWalker.testrom" "$pdir/$pname.chimeraProject" <<'PY'
import hashlib, json, sys
entries = [l.rstrip("\r\n") for l in open(sys.argv[1]) if l.startswith("|")]
logkey = "#P1 Up|P1 Down|P1 Left|P1 Right|P1 A|P1 B|P1 Select|P1 Start|"
sha1 = hashlib.sha1(open(sys.argv[2], "rb").read()).hexdigest().upper()
json.dump({
    "title": "gridWalker.win",
    "core": {"name": "Synth", "version": "", "sha1": ""},
    "headers": {"MovieVersion": "Chimera Project File v1.1", "Platform": "Synth"},
    "files": [{"name": "gridWalker.testrom", "sha1": sha1, "slot": "rom"}],
    "settings": {"initFillByte": 171},
    "input": "[Input]\nLogKey:" + logkey + "\n" + "\n".join(entries) + "\n[/Input]\n",
}, open(sys.argv[3], "w"))
PY
		pjob="$work/job.project.txt"
		{
			echo "outram=$work/project.ram.bin"
			echo "outvram=$work/project.vram.bin"
			echo "meta=$work/project.meta.txt"
		} > "$pjob"
		rm -f "$work/project.ram.bin" "$work/project.vram.bin" "$work/project.meta.txt"
		cp "$config" "$work/config.project.ini"
		( cd "$repo_root" && CHIMERA_JOB="$pjob" timeout 300 mono "$emu_exe" --headless \
			"--config=$work/config.project.ini" "--core=$repo_root/build/Cores/synth-box.chimeraCore" \
			"--project=$pdir/$pname.chimeraProject" "--lua=$here/synth-movie-dump.lua" ) > "$work/project.log" 2>&1
		boots="$(grep -c "\[waterbox\] booting" "$work/project.log" || true)"
		if [ ! -f "$work/project.meta.txt" ] || ! grep -q "^status=OK" "$work/project.meta.txt"; then
			report "P:box:$pname" FAIL "no OK meta (see work/project.log)"
		elif [ "$boots" != "1" ]; then
			report "P:box:$pname" FAIL "core booted $boots times; a project open boots EXACTLY once"
		elif cmp -s "$work/project.ram.bin" "$golden_dir/$pname.ram.bin"; then
			report "P:box:$pname" FAIL "RAM matches the default golden - the project's initFillByte never reached the guest"
		else
			report "P:box:$pname" PASS "one boot, project settings live in the guest"
		fi
	fi

	# --- a run becomes a video ---
	# Encode Video, end to end: part of a real run reproduced into a real file
	# through the real writer. The checks are the ones that used to be a person's
	# job when this was three separate commands - is the file there, does it hold
	# EXACTLY the frames that were asked for (both ends included), and was the
	# emulator put back where it was found.
	if [ "$record" -eq 0 ]; then
		ename=gridWalker.win
		erom="$here/roms/${ename%%.*}.testrom"
		edir="$work/encode-leg"
		rm -rf "$edir" && mkdir -p "$edir"
		cp "$erom" "$edir/gridWalker.testrom"
		python3 - "$here/movies/$ename.txt" "$edir/gridWalker.testrom" "$edir/$ename.chimeraProject" <<'ENCPY'
import hashlib, json, sys
entries = [l.rstrip("\r\n") for l in open(sys.argv[1]) if l.startswith("|")]
logkey = "#P1 Up|P1 Down|P1 Left|P1 Right|P1 A|P1 B|P1 Select|P1 Start|"
sha1 = hashlib.sha1(open(sys.argv[2], "rb").read()).hexdigest().upper()
json.dump({
    "title": "gridWalker.win",
    "core": {"name": "Synth", "version": "", "sha1": ""},
    "headers": {"MovieVersion": "Chimera Project File v1.1", "Platform": "Synth"},
    "files": [{"name": "gridWalker.testrom", "sha1": sha1, "slot": "rom"}],
    "input": "[Input]\nLogKey:" + logkey + "\n" + "\n".join(entries) + "\n[/Input]\n",
}, open(sys.argv[3], "w"))
ENCPY
		evideo="$work/encode.mkv"
		efrom=10
		eto=49
		eframes=$((eto - efrom + 1))
		ejob="$work/job.encode.txt"
		{
			echo "out=$evideo"
			echo "meta=$work/encode.meta.txt"
			echo "from=$efrom"
			echo "to=$eto"
			# lossless, so a frame that came out wrong cannot be blamed on a codec
			echo "command=-c:a pcm_s16le -c:v ffv1 -pix_fmt bgr0 -level 1 -g 1 -f matroska"
		} > "$ejob"
		rm -f "$evideo" "$work/encode.meta.txt"
		cp "$config" "$work/config.encode.ini"

		# ffmpeg is part of a build now, not something a person is asked to fetch
		# the first time they want a video - so the gate builds it in too.
		if ! "$repo_root/tools/fetch-ffmpeg.sh" linux "$repo_root/build/dll" > "$work/ffmpeg.log" 2>&1; then
			report "V:box:encode" FAIL "no ffmpeg to encode with (see work/ffmpeg.log)"
		else
			( cd "$repo_root" && CHIMERA_JOB="$ejob" timeout 300 mono "$emu_exe" --headless \
				"--config=$work/config.encode.ini" "--core=$repo_root/build/Cores/synth-box.chimeraCore" \
				"--project=$edir/$ename.chimeraProject" "--lua=$here/synth-encode.lua" ) > "$work/encode.log" 2>&1
			# counted with the ffmpeg this build ships rather than a system
			# ffprobe: the gate must not depend on a tool the machine happens to
			# have, and a CI runner does not have one. The progress line ffmpeg
			# writes while decoding to nowhere ends with the frame it reached.
			ecount=""
			if [ -s "$evideo" ]; then
				ecount="$("$repo_root/build/dll/ffmpeg" -hide_banner -nostdin -i "$evideo" -f null - 2>&1 \
					| tr "\r" "\n" | sed -n "s/^frame= *\([0-9][0-9]*\).*/\1/p" | tail -1)"
			fi
			ebefore="$(sed -n "s/^before=//p" "$work/encode.meta.txt" 2>/dev/null)"
			eafter="$(sed -n "s/^after=//p" "$work/encode.meta.txt" 2>/dev/null)"
			if [ ! -f "$work/encode.meta.txt" ] || ! grep -q "^status=OK" "$work/encode.meta.txt"; then
				report "V:box:encode" FAIL "$(sed -n "s/^detail=//p" "$work/encode.meta.txt" 2>/dev/null || echo "run failed") (see work/encode.log)"
			elif [ ! -s "$evideo" ]; then
				report "V:box:encode" FAIL "no video was written (see work/encode.log)"
			elif [ "$ecount" != "$eframes" ]; then
				report "V:box:encode" FAIL "video holds ${ecount:-?} frames, asked for $eframes ($efrom to $eto inclusive)"
			elif [ "$ebefore" != "$eafter" ]; then
				report "V:box:encode" FAIL "emulator was left on frame $eafter, not the $ebefore it was borrowed from"
			else
				report "V:box:encode" PASS "$eframes frames written, emulator handed back on frame $eafter"
			fi
		fi
	fi

	# --- a second project, over a running one ---
	# The shape that used to throw: booting over a live session ran the old
	# machine's close inside the new one's load, where it took the movie the load
	# had just queued. A session has to end before the next one begins, and the
	# machine left behind has to be the SECOND project's.
	if [ "$record" -eq 0 ]; then
		rdir="$work/reopen-leg"
		rm -rf "$rdir" && mkdir -p "$rdir"
		make_project() { # <movie name> <project path>
			cp "$here/roms/${1%%.*}.testrom" "$rdir/${1%%.*}.testrom"
			python3 - "$here/movies/$1.txt" "$rdir/${1%%.*}.testrom" "$2" <<'REOPENPY'
import hashlib, json, sys
entries = [l.rstrip("\r\n") for l in open(sys.argv[1]) if l.startswith("|")]
logkey = "#P1 Up|P1 Down|P1 Left|P1 Right|P1 A|P1 B|P1 Select|P1 Start|"
sha1 = hashlib.sha1(open(sys.argv[2], "rb").read()).hexdigest().upper()
json.dump({
    "title": sys.argv[1],
    "core": {"name": "Synth", "version": "", "sha1": ""},
    "headers": {"MovieVersion": "Chimera Project File v1.1", "Platform": "Synth"},
    "files": [{"name": sys.argv[2].split("/")[-1], "sha1": sha1, "slot": "rom"}],
    "input": "[Input]\nLogKey:" + logkey + "\n" + "\n".join(entries) + "\n[/Input]\n",
}, open(sys.argv[3], "w"))
REOPENPY
		}
		make_project gridWalker.win "$rdir/first.chimeraProject"
		make_project gridWalker.lose "$rdir/second.chimeraProject"
		firstlen="$(grep -c '^|' "$here/movies/gridWalker.win.txt")"
		secondlen="$(grep -c '^|' "$here/movies/gridWalker.lose.txt")"

		rjob="$work/job.reopen.txt"
		{
			echo "second=$rdir/second.chimeraProject"
			echo "meta=$work/reopen.meta.txt"
		} > "$rjob"
		rm -f "$work/reopen.meta.txt" "$work/reopen.ram.bin"
		cp "$config" "$work/config.reopen.ini"
		( cd "$repo_root" && CHIMERA_JOB="$rjob" timeout 300 mono "$emu_exe" --headless \
			"--config=$work/config.reopen.ini" "--core=$repo_root/build/Cores/synth-box.chimeraCore" \
			"--project=$rdir/first.chimeraProject" "--lua=$here/synth-reopen.lua" ) > "$work/reopen.log" 2>&1
		got_first="$(sed -n 's/^firstlength=//p' "$work/reopen.meta.txt" 2>/dev/null)"
		got_second="$(sed -n 's/^secondlength=//p' "$work/reopen.meta.txt" 2>/dev/null)"
		if [ ! -f "$work/reopen.meta.txt" ] || ! grep -q "^status=OK" "$work/reopen.meta.txt"; then
			report "R:box:reopen" FAIL "$(sed -n 's/^detail=//p' "$work/reopen.meta.txt" 2>/dev/null || echo "run failed") (see work/reopen.log)"
		elif [ "$got_first" != "$firstlen" ]; then
			report "R:box:reopen" FAIL "the first project was $got_first frames, not $firstlen"
		elif [ "$got_second" != "$secondlen" ]; then
			report "R:box:reopen" FAIL "after reopening, the movie is $got_second frames; the second project is $secondlen"
		else
			report "R:box:reopen" PASS "a project opened over a running one, and the second is what is loaded"
		fi
	fi

	# --- a core is never loaded implicitly ---
	# Same movie, but NO --core. A package sitting in build/Cores is AVAILABLE, not
	# loaded: opening a core is something the user does (File > Open Core), and the
	# commandline says which one with --core. So this run must NOT produce a machine -
	# if it does, something is loading cores behind the user's back again.
	if [ "$record" -eq 0 ]; then
		dname=gridWalker.win
		djob="$work/job.discovery.txt"
		{
			echo "movie=$here/movies/$dname.txt"
			echo "outram=$work/discovery.ram.bin"
			echo "outvram=$work/discovery.vram.bin"
			echo "meta=$work/discovery.meta.txt"
			echo "mode=simple"
		} > "$djob"
		rm -f "$work/discovery.ram.bin" "$work/discovery.meta.txt"
		cp "$config" "$work/config.discovery.ini"
		( cd "$repo_root" && CHIMERA_JOB="$djob" timeout 300 mono "$emu_exe" --headless \
			"--config=$work/config.discovery.ini" "--lua=$here/synth-replay.lua" \
			"$here/roms/${dname%%.*}.testrom" ) > "$work/discovery.log" 2>&1
		if [ -f "$work/discovery.meta.txt" ] && grep -q "^status=OK" "$work/discovery.meta.txt"; then
			report "D:box:noImplicitCore" FAIL "the rom ran with no core opened and no --core"
		else
			report "D:box:noImplicitCore" PASS "a rom does not load without a core (see work/discovery.log)"
		fi

		# --- keybinds ship with the package ---
		# The frontend has no bindings of its own: a controller it has never seen is played however
		# the package that declared it says (the package's default_keybinds.json). Start from a
		# config that has never heard of this controller - which is what a fresh install is - and
		# the config Chimera writes on exit must hold the package's bindings, or the core arrives
		# unplayable.
		kname=gridWalker.win
		kcfg="$work/config.keybinds.ini"
		python3 "$here/forget-controller.py" "$config" "$kcfg" "Synth Controller"
		kjob="$work/job.keybinds.txt"
		{
			echo "movie=$here/movies/$kname.txt"
			echo "outram=$work/keybinds.ram.bin"
			echo "outvram=$work/keybinds.vram.bin"
			echo "meta=$work/keybinds.meta.txt"
			echo "mode=simple"
		} > "$kjob"
		rm -f "$work/keybinds.meta.txt"
		( cd "$repo_root" && CHIMERA_JOB="$kjob" timeout 300 mono "$emu_exe" --headless \
			"--config=$kcfg" "--core=$package" "--lua=$here/synth-replay.lua" \
			"$here/roms/${kname%%.*}.testrom" ) > "$work/keybinds.log" 2>&1
		if [ ! -f "$work/keybinds.meta.txt" ] || ! grep -q "^status=OK" "$work/keybinds.meta.txt"; then
			report "K:box:keybinds" FAIL "run failed (see work/keybinds.log)"
		elif python3 "$here/check-keybinds.py" "$kcfg" \
			"$here/package-box/default_keybinds.json" "Synth Controller" > "$work/keybinds.txt" 2>&1; then
			report "K:box:keybinds" PASS "$(cat "$work/keybinds.txt")"
		else
			report "K:box:keybinds" FAIL "$(head -1 "$work/keybinds.txt")"
		fi
	fi
fi

echo ""
echo "$ok ok, $failed failed"
[ "$failed" -gt 0 ] && exit 1
exit 0
