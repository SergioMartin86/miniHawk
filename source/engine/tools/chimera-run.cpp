/* chimera-run - the engine's headless runner.
 *
 * Loads a core package, a rom, and a movie input log; runs the movie; dumps
 * the machine's memory domains. This is the witness gate's Level B without a
 * frontend: no Mono, no WinForms, no display - the same package, the same
 * host, the same inputs, and the dumps must be byte-identical to the goldens
 * the managed frontend produces.
 *
 *   chimera-run <package> <rom> <movie.txt>
 *       [--rerecord] [--seek <frame>] [--play <n>] [--edit-from <movie>] [--stop-at-seek] [--bands n,m,ms,fs,anchor] [--record <out.txt>]
 *       [--settings <json>]
 *       [--dump <domain>=<path>]... [--export-savedata <dir>] [--meta <path>]
 *   chimera-run --project <p.chimeraProject> <package>
 *       [--files <dir>]... [--allow-core-mismatch] [the same run flags]
 *
 * --project runs a .chimeraProject (docs/project.md): the input log and the
 * sync settings come from the project, files resolve from the project's own
 * folder plus any --files dirs, and every hash must match (this tool has no
 * user to knowingly override). The package must match the project's core
 * pin unless --allow-core-mismatch says otherwise; when the package ships
 * file_slots.json, the manifest is validated against it. Mounts: the slot
 * map as "slots", every file under its canonical name, and (transitionally,
 * while cores learn slots) the first file of the first slot as rom/rom.name
 * with that slot's remaining files as rom2..N.
 *
 * --rerecord round-trips the whole machine through save/load state around
 * every frame, which must not change anything - that is the point.
 * --bands sets the history's density at each distance from the playhead
 * (near frames, mid frames, mid stride, far stride, anchor spacing; 0 keeps a
 * default). Its use in a test is to make the bands narrow enough that the
 * history is constantly coarsening, which is what the defaults spend minutes
 * of real play reaching.
 *
 * --stop-at-seek ends the run where the seek landed, so the dumps describe
 * frame N itself rather than the end of a replay from it.
 *
 * --seek plays the movie to its end, seeks BACK to the given frame through
 * the greenzone, and plays to the end again - and that must not change
 * anything either.
 * --record drives RECORD mode instead of playback: the given movie is only an
 * input source (each entry decoded to buttons/axes), the session generates the
 * log itself, and it is written to <out.txt>. Feeding that file back in as an
 * ordinary movie must reach the same machine - which is what witnesses record
 * mode and entry generation, the paths playback never touches.
 * --history-out <file> keeps the state history, and --history-in <file> starts
 * from one kept earlier - which is what reopening a project does, and the only
 * way to witness that a history outlives the process that made it.
 * --export-savedata writes the core's exported save-data tree under <dir>
 * after the run (docs/save-data.md) - the gates diff it like a memory dump.
 * --firmware <id>=<path> mounts a firmware file under the id the core declares
 * (a PS2 bios, a disk-system rom). Repeatable. A project names the ids it needs
 * and the SHA1 it expects; this tool has no user to ask, so it mounts what it
 * is given and the engine checks the hash.
 * --save-state <frame>=<path> writes the whole machine after that frame;
 * --state <path> starts from one instead of from power-on, and --frames <n>
 * stops after n. The three together are for looking at a picture: a state saved
 * near the end of a long movie makes "what does this frame look like" a
 * one-second question instead of a five-minute one. The movie's read position
 * still starts at 0, so the INPUT after a loaded state is the movie's first
 * entries rather than the ones that belong there - fine for a rendering
 * question, wrong for anything about the machine.
 * --screenshot <frame>=<path> writes one frame's picture as a TGA. Repeatable.
 * The run is otherwise undrawn (turbo), so only the frames asked for cost
 * anything to draw - which is what makes "show me frame 1910 of this movie" a
 * cheap question to ask of a two-hour run.
 */

#include "chimera/engine.h"

#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#ifdef _WIN32
#include <direct.h>
#else
#include <sys/stat.h>
#endif

namespace {

bool readWholeFile(const char *path, std::vector<uint8_t> &out)
{
	FILE *f = std::fopen(path, "rb");
	if (f == nullptr) return false;
	uint8_t chunk[1 << 16];
	size_t got;
	out.clear();
	while ((got = std::fread(chunk, 1, sizeof chunk, f)) != 0) out.insert(out.end(), chunk, chunk + got);
	bool ok = std::ferror(f) == 0;
	std::fclose(f);
	return ok;
}

bool writeWholeFile(const std::string &path, const uint8_t *data, size_t len)
{
	FILE *f = std::fopen(path.c_str(), "wb");
	if (f == nullptr) return false;
	bool ok = len == 0 || std::fwrite(data, 1, len, f) == len;
	std::fclose(f);
	return ok;
}

/* One frame as an uncompressed 32-bit TGA. The engine hands over BGRA, which is
 * exactly what a TGA stores, so the rows only have to be written bottom-up. */
bool writeTga(const std::string &path, const uint32_t *bgra, int32_t w, int32_t h)
{
    if (bgra == nullptr || w <= 0 || h <= 0) return false;
    std::vector<uint8_t> out(18 + static_cast<size_t>(w) * h * 4, 0);
    out[2] = 2;  /* uncompressed true-colour */
    out[12] = static_cast<uint8_t>(w & 0xFF);
    out[13] = static_cast<uint8_t>((w >> 8) & 0xFF);
    out[14] = static_cast<uint8_t>(h & 0xFF);
    out[15] = static_cast<uint8_t>((h >> 8) & 0xFF);
    out[16] = 32;
    out[17] = 8;  /* 8 bits of alpha, top-left origin cleared: rows go bottom-up */
    for (int32_t y = 0; y < h; y++)
    {
        std::memcpy(out.data() + 18 + static_cast<size_t>(y) * w * 4,
            bgra + static_cast<size_t>(h - 1 - y) * w, static_cast<size_t>(w) * 4);
    }
    return writeWholeFile(path, out.data(), out.size());
}

/* mkdir -p for a path's PARENT directories (the engine already refused any
 * name with "..", so walking forward is safe) */
void makeParentDirs(const std::string &path)
{
	for (size_t i = 1; i < path.size(); i++)
	{
		if (path[i] != '/') continue;
		std::string dir = path.substr(0, i);
#ifdef _WIN32
		_mkdir(dir.c_str());
#else
		mkdir(dir.c_str(), 0777);
#endif
	}
}

int fail(const std::string &metaPath, const std::string &detail)
{
	if (!metaPath.empty())
	{
		std::string meta = "status=ERROR\ndetail=" + detail + "\nframes=0\nstartframe=-1\n";
		writeWholeFile(metaPath, reinterpret_cast<const uint8_t *>(meta.data()), meta.size());
	}
	std::fprintf(stderr, "chimera-run: %s\n", detail.c_str());
	return 2;
}

} // namespace

int main(int argc, char **argv)
{
	const char *packagePath = nullptr, *romPath = nullptr, *moviePath = nullptr;
	const char *settings = nullptr;
	std::string metaPath;
	std::vector<std::pair<std::string, std::string>> dumps; // domain -> path
	std::map<int64_t, std::string> shots; // frame -> TGA path
	std::vector<std::pair<std::string, std::string>> firmwareArgs; // id -> path
	std::map<int64_t, std::string> stateOuts; // frame -> state path
	std::string stateIn;
	int64_t frameLimit = -1;
	bool rerecord = false;
	int64_t seekFrame = -1;
	bool stopAtSeek = false;
	std::string bands;
	std::string recordPath;
	std::string savedataDir;
	std::string projectPath;
	std::string historyIn, historyOut;
	/* --play/--edit-from/--final-state: the re-recording shape. Play a while,
	 * seek back, put a DIFFERENT movie in from that frame on, and carry on past
	 * where the first pass reached. What comes out has to be what a straight run
	 * of the edited movie produces - that is the whole promise a tool-assisted
	 * run rests on, and nothing else here tests it, because --seek replays the
	 * same inputs and can pass with the edit path broken. */
	int64_t playFrames = -1;
	std::string editFrom, finalStatePath, finalShot, finalBuses;
	std::vector<std::string> fileDirs;
	bool allowCoreMismatch = false;
	bool wantGpu = false;

	for (int i = 1; i < argc; i++)
	{
		std::string arg = argv[i];
		if (arg == "--rerecord") rerecord = true;
		else if (arg == "--seek" && i + 1 < argc) seekFrame = std::atoll(argv[++i]);
		else if (arg == "--play" && i + 1 < argc) playFrames = std::atoll(argv[++i]);
		else if (arg == "--edit-from" && i + 1 < argc) editFrom = argv[++i];
		else if (arg == "--final-state" && i + 1 < argc) finalStatePath = argv[++i];
		else if (arg == "--final-screenshot" && i + 1 < argc) finalShot = argv[++i];
		else if (arg == "--final-buses" && i + 1 < argc) finalBuses = argv[++i];
		else if (arg == "--bands" && i + 1 < argc) bands = argv[++i];
		else if (arg == "--stop-at-seek") stopAtSeek = true;
		else if (arg == "--record" && i + 1 < argc) recordPath = argv[++i];
		else if (arg == "--settings" && i + 1 < argc) settings = argv[++i];
		else if (arg == "--export-savedata" && i + 1 < argc) savedataDir = argv[++i];
		else if (arg == "--meta" && i + 1 < argc) metaPath = argv[++i];
		else if (arg == "--project" && i + 1 < argc) projectPath = argv[++i];
		else if (arg == "--files" && i + 1 < argc) fileDirs.push_back(argv[++i]);
		else if (arg == "--allow-core-mismatch") allowCoreMismatch = true;
		else if (arg == "--gpu") wantGpu = true;
		else if (arg == "--history-in" && i + 1 < argc) historyIn = argv[++i];
		else if (arg == "--history-out" && i + 1 < argc) historyOut = argv[++i];
		else if (arg == "--firmware" && i + 1 < argc)
		{
			std::string spec = argv[++i];
			auto eq = spec.find('=');
			if (eq == std::string::npos) return fail(metaPath, "--firmware wants <id>=<path>");
			firmwareArgs.emplace_back(spec.substr(0, eq), spec.substr(eq + 1));
		}
		else if (arg == "--save-state" && i + 1 < argc)
		{
			std::string spec = argv[++i];
			auto eq = spec.find('=');
			if (eq == std::string::npos) return fail(metaPath, "--save-state wants <frame>=<path>");
			stateOuts[std::atoll(spec.substr(0, eq).c_str())] = spec.substr(eq + 1);
		}
		else if (arg == "--state" && i + 1 < argc) stateIn = argv[++i];
		else if (arg == "--frames" && i + 1 < argc) frameLimit = std::atoll(argv[++i]);
		else if (arg == "--screenshot" && i + 1 < argc)
		{
			std::string spec = argv[++i];
			auto eq = spec.find('=');
			if (eq == std::string::npos) return fail(metaPath, "--screenshot wants <frame>=<path>");
			shots[std::atoll(spec.substr(0, eq).c_str())] = spec.substr(eq + 1);
		}
		else if (arg == "--dump" && i + 1 < argc)
		{
			std::string spec = argv[++i];
			auto eq = spec.find('=');
			if (eq == std::string::npos) return fail(metaPath, "--dump wants <domain>=<path>");
			dumps.emplace_back(spec.substr(0, eq), spec.substr(eq + 1));
		}
		else if (packagePath == nullptr) packagePath = argv[i];
		else if (projectPath.empty() && romPath == nullptr) romPath = argv[i];
		else if (projectPath.empty() && moviePath == nullptr) moviePath = argv[i];
		else return fail(metaPath, "unexpected argument: " + arg);
	}
	bool projectMode = !projectPath.empty();
	if (projectMode ? packagePath == nullptr : moviePath == nullptr)
	{
		std::fprintf(stderr, "usage: chimera-run <package> <rom> <movie.txt> [--rerecord] [--seek <frame>] [--play <n>] [--edit-from <movie>] [--stop-at-seek] [--bands n,m,ms,fs,anchor] [--record <out.txt>] [--settings <json>] [--dump <domain>=<path>]... [--firmware <id>=<path>]... [--state <path>] [--frames <n>] [--save-state <frame>=<path>]... [--screenshot <frame>=<path>]... [--export-savedata <dir>] [--meta <path>] [--gpu]\n"
			"       chimera-run --project <p.chimeraProject> <package> [--files <dir>]... [--allow-core-mismatch] [the same run flags]\n");
		return 1;
	}
	if (projectMode && settings != nullptr)
	{
		return fail(metaPath, "--settings and --project do not mix: the project IS the settings");
	}

	std::vector<uint8_t> rom, movieText;
	if (!projectMode && !readWholeFile(moviePath, movieText)) return fail(metaPath, std::string("could not read movie ") + moviePath);

	/* A .chimeraMultiFile rom is a multi-file game: the first image mounts as
	 * the rom (rom.name carrying its real name), further images as rom2..N,
	 * support files and savedata under their fixed names - the exact mounts
	 * the frontend makes. Everything hash-verified; refuse on any mismatch
	 * (this tool has no user to ask). */
	ce_multifile *multi = nullptr;
	ce_project *project = nullptr;
	std::vector<const char *> extraNames;
	std::vector<const uint8_t *> extraData;
	std::vector<uint64_t> extraLens;
	/* per extra: a path to read it from, or null to use the bytes above */
	std::vector<const char *> extraPaths;
	std::string romPathStore;
	std::vector<std::string> extraNameStore;
	std::string settingsStore, slotsStore;
	if (projectMode)
	{
		const char *perr = nullptr;
		project = ce_project_open(projectPath.c_str(), &perr);
		if (project == nullptr) return fail(metaPath, perr != nullptr ? perr : "bad project");

		/* resolution: the project's own folder first (the convenience), then
		 * every --files dir; whatever is still missing or mismatched fails -
		 * this tool has no user to knowingly override */
		size_t cut = projectPath.find_last_of("/\\");
		std::string projFolder = cut == std::string::npos ? std::string(".") : projectPath.substr(0, cut);
		ce_project_resolve_dir(project, projFolder.c_str());
		for (const std::string &dir : fileDirs) ce_project_resolve_dir(project, dir.c_str());
		if (ce_project_files_ok(project) == 0)
		{
			for (int32_t i = 0; i < ce_project_file_count(project); i++)
			{
				if (ce_project_file_status(project, i) != 0)
					return fail(metaPath, std::string("project: '") + ce_project_file_name(project, i)
						+ (ce_project_file_status(project, i) == 1
							? "' was not found in the project's folder or any --files dir"
							: "' does not match its recorded hash"));
			}
		}

		/* the core pin, against the actual package; and the manifest against
		 * the package's slot declaration when it ships one */
		const char *kerr = nullptr;
		ce_package *pkg = ce_package_open(packagePath, &kerr);
		if (pkg != nullptr)
		{
			const char *pin = ce_project_core_sha1(project);
			const char *actual = ce_package_sha1(pkg);
			if (pin[0] != '\0' && actual != nullptr && std::strcmp(pin, actual) != 0 && !allowCoreMismatch)
			{
				std::string detail = std::string("the package is not the project's pinned core (pinned ")
					+ pin + ", given " + actual + "); --allow-core-mismatch to run anyway";
				ce_package_free(pkg);
				return fail(metaPath, detail);
			}
			uint64_t declLen = 0;
			const uint8_t *decl = ce_package_entry(pkg, "file_slots.json", &declLen);
			if (decl != nullptr &&
				ce_project_validate(project, reinterpret_cast<const char *>(decl), declLen, &perr) != 0)
			{
				std::string detail = perr != nullptr ? perr : "the manifest does not fit the core's slots";
				ce_package_free(pkg);
				return fail(metaPath, detail);
			}
			ce_package_free(pkg);
		}

		uint64_t len = 0;
		settingsStore = ce_project_settings_text(project, &len);
		settings = settingsStore.c_str();
		const char *lump = ce_project_log_text(project, &len);
		movieText.assign(reinterpret_cast<const uint8_t *>(lump),
			reinterpret_cast<const uint8_t *>(lump) + len);

		/* mounts: the slot map, every file under its canonical name, and the
		 * transitional rom/rom.name/rom2..N view of the first slot */
		slotsStore = ce_project_slots_text(project, &len);
		extraNames.push_back("slots");
		extraData.push_back(reinterpret_cast<const uint8_t *>(slotsStore.data()));
		extraLens.push_back(slotsStore.size());
		extraPaths.push_back(nullptr);
		int32_t primary = -1;
		for (int32_t i = 0; i < ce_project_file_count(project); i++)
		{
			/* Where it lies, not what it holds: the engine mounts the file
			 * from disk and the guest reads it as it goes. */
			extraNames.push_back(ce_project_file_name(project, i));
			extraData.push_back(nullptr);
			extraLens.push_back(0);
			extraPaths.push_back(ce_project_file_source_path(project, i));
			if (primary < 0 && std::strcmp(ce_project_file_slot(project, i), "support") != 0) primary = i;
		}
		if (primary >= 0)
		{
			/* pointers into extraNameStore go out as they are made, so the
			 * vector must never reallocate: reserve the worst case */
			extraNameStore.reserve(static_cast<size_t>(ce_project_file_count(project)) + 1);
			romPathStore = ce_project_file_source_path(project, primary);
			extraNameStore.push_back(ce_project_file_name(project, primary));
			extraNames.push_back("rom.name");
			extraData.push_back(reinterpret_cast<const uint8_t *>(extraNameStore.back().data()));
			extraLens.push_back(extraNameStore.back().size());
			extraPaths.push_back(nullptr);
			int n = 2;
			std::string primarySlot = ce_project_file_slot(project, primary);
			for (int32_t i = primary + 1; i < ce_project_file_count(project); i++)
			{
				if (primarySlot != ce_project_file_slot(project, i)) continue;
				extraNameStore.push_back("rom" + std::to_string(n++));
				extraNames.push_back(extraNameStore.back().c_str());
				extraData.push_back(nullptr);
				extraLens.push_back(0);
				extraPaths.push_back(ce_project_file_source_path(project, i));
			}
		}
	}
	std::string romPathStr = projectMode ? std::string() : romPath;
	if (romPathStr.size() > 17 &&
		romPathStr.compare(romPathStr.size() - 17, 17, ".chimeraMultiFile") == 0)
	{
		const char *merr = nullptr;
		multi = ce_multifile_open(romPath, &merr);
		if (multi == nullptr) return fail(metaPath, merr != nullptr ? merr : "bad descriptor");
		if (ce_multifile_ok(multi) == 0)
		{
			for (int32_t i = 0; i < ce_multifile_count(multi); i++)
			{
				if (ce_multifile_status(multi, i) != 0)
					return fail(metaPath, std::string("multifile: '") + ce_multifile_name(multi, i)
						+ (ce_multifile_status(multi, i) == 1 ? "' is missing" : "' does not match its recorded hash"));
			}
		}
		int32_t primary = ce_multifile_image_index(multi, 0);
		uint64_t len = 0;
		const uint8_t *data = ce_multifile_data(multi, primary, &len);
		rom.assign(data, data + len);
		extraNameStore.push_back("rom.name");
		extraNameStore.push_back(ce_multifile_name(multi, primary));
		for (int32_t n = 1; n < ce_multifile_image_count(multi); n++)
		{
			extraNameStore.push_back("rom" + std::to_string(n + 1));
		}
		// mounts: rom.name first, then rom2..N, then the rest by name
		size_t store = 0;
		extraNames.push_back(extraNameStore[store++].c_str());
		{
			const std::string &nm = extraNameStore[store++];
			extraData.push_back(reinterpret_cast<const uint8_t *>(nm.data()));
			extraLens.push_back(nm.size());
		}
		for (int32_t n = 1; n < ce_multifile_image_count(multi); n++)
		{
			int32_t idx = ce_multifile_image_index(multi, n);
			extraNames.push_back(extraNameStore[store++].c_str());
			extraData.push_back(ce_multifile_data(multi, idx, &len));
			extraLens.push_back(len);
		}
		for (int32_t i = 0; i < ce_multifile_count(multi); i++)
		{
			const char *role = ce_multifile_role(multi, i);
			if (std::strcmp(role, "support") == 0)
			{
				extraNames.push_back(ce_multifile_name(multi, i));
				extraData.push_back(ce_multifile_data(multi, i, &len));
				extraLens.push_back(len);
			}
			else if (std::strcmp(role, "savedata") == 0)
			{
				extraNames.push_back("savedata");
				extraData.push_back(ce_multifile_data(multi, i, &len));
				extraLens.push_back(len);
			}
		}
	}
	else if (!projectMode)
	{
		/* THE PATH, not the bytes. The engine mounts a rom from where it lies
		 * when it is told where that is, and reads it lazily from there; hand
		 * it a byte array instead and the file is copied three times over -
		 * once here, once into the session, once into the guest's file system -
		 * before the machine has read a sector of it. A 2 GB DOS hard disk cost
		 * 8.3 GB that way, which is the frontend's own behaviour nowhere: it
		 * has always passed the path. */
		romPathStore = romPath;
	}

	ce_movie_log *movie = ce_movie_log_new();
	if (ce_movie_log_parse(movie, reinterpret_cast<const char *>(movieText.data()), movieText.size()) != 0)
	{
		return fail(metaPath, std::string("movie: ") + ce_movie_log_last_error(movie));
	}

	/* --gpu is the same ask the frontend makes for a renderer named *-hw:
	 * an offer, not a promise. Without a bridge in this build, without a
	 * driver, or with a core that has no GL renderer, the software path
	 * draws and ce_session_deterministic still says 1. */
	ce_gl_request(wantGpu ? 1 : 0);

	/* firmware, read whole: a bios is small and the engine wants the bytes */
	std::vector<std::vector<uint8_t>> fwBytes(firmwareArgs.size());
	std::vector<const char *> fwIds;
	std::vector<const uint8_t *> fwData;
	std::vector<uint64_t> fwLens;
	for (size_t i = 0; i < firmwareArgs.size(); i++)
	{
		if (!readWholeFile(firmwareArgs[i].second.c_str(), fwBytes[i]))
		{
			return fail(metaPath, "could not read firmware " + firmwareArgs[i].second);
		}
		fwIds.push_back(firmwareArgs[i].first.c_str());
		fwData.push_back(fwBytes[i].data());
		fwLens.push_back(fwBytes[i].size());
	}

	const char *error = nullptr;
	ce_session *session = ce_session_open(
		packagePath, rom.data(), rom.size(), romPathStore.empty() ? nullptr : romPathStore.c_str(),
		settings, fwIds.data(), fwData.data(), fwLens.data(), static_cast<int32_t>(fwIds.size()),
		extraNames.data(), extraData.data(), extraLens.data(), extraPaths.data(),
		static_cast<int32_t>(extraNames.size()), &error);
	if (session == nullptr) return fail(metaPath, error != nullptr ? error : "session open failed");

	int64_t frames = ce_movie_log_count(movie);

	/* the movie is the SESSION's from here: the engine parses entries, tracks
	 * the frame position, and (with --seek) keeps the greenzone */
	if (ce_session_movie_load(session, movie) != 0) return fail(metaPath, "could not load the movie into the session");

	/* --frames shortens a run. While RECORDING it may also lengthen one: the
	 * source movie is an input source there and not the timeline, so a run can
	 * outlast it and the tail is idle input. That is what makes "record N
	 * frames of nothing" expressible, which is the only way to ask a core what
	 * its entries LOOK like before owning a movie of the right shape.
	 *
	 * Settled HERE, before the record block below decodes that many entries -
	 * a limit applied after it would leave the decoded input shorter than the
	 * run. */
	if (frameLimit >= 0 && (frameLimit < frames || !recordPath.empty())) frames = frameLimit;
	if (seekFrame >= 0 || !historyIn.empty() || !historyOut.empty())
	{
		/* Bands before enabling: enabling captures the anchor, and the anchor
		 * spacing decides whether it is the only one. */
		if (!bands.empty())
		{
			int64_t v[5] = { 0, 0, 0, 0, 0 };
			size_t at = 0;
			for (int64_t &slot : v)
			{
				if (at > bands.size()) break;
				const size_t comma = bands.find(',', at);
				slot = std::atoll(bands.substr(at, comma == std::string::npos ? comma : comma - at).c_str());
				if (comma == std::string::npos) break;
				at = comma + 1;
			}
			ce_session_greenzone_bands(session, v[0], v[1], v[2], v[3], v[4]);
		}
		ce_session_greenzone_enable(session, 256ull << 20);
	}
	/* A history kept from a previous run, which is the thing a reopened project
	 * lives on. The machine id is this tool's own convention; a frontend passes
	 * whatever it knows about cores, settings and files. */
	if (!historyIn.empty() && ce_session_history_load(session, historyIn.c_str(), "chimera-run") != 0)
	{
		return fail(metaPath, std::string("history: ") + ce_session_last_error(session));
	}

	/* Record mode: decode the source movie's entries into machine input and
	 * hand THAT to the session, which generates its own log. The source is an
	 * input source only - none of its text reaches the recorded movie.
	 * The WIDE decode + set_button path is used for every controller - exact
	 * at any width, and identical to the packed path below 64 buttons. */
	int64_t buttonCount = ce_session_button_count(session);
	std::vector<std::vector<uint8_t>> recButtons;
	std::vector<std::vector<int32_t>> recAxes;
	if (!recordPath.empty())
	{
		int64_t axisCount = ce_session_axis_count(session);
		const int64_t sourceFrames = ce_movie_log_count(movie);
		for (int64_t i = 0; i < frames; i++)
		{
			std::vector<uint8_t> states(static_cast<size_t>(buttonCount), 0);
			std::vector<int32_t> axes(static_cast<size_t>(axisCount), 0);
			/* past the source movie's end the input is idle, which is what the
			 * two vectors already hold. A run may outlast its input source
			 * while recording (see --frames above); it may not read past it. */
			if (i < sourceFrames && ce_session_movie_entry_decode_wide(
					session, ce_movie_log_entry(movie, i),
					buttonCount != 0 ? states.data() : nullptr,
					axisCount != 0 ? axes.data() : nullptr) != 0)
			{
				return fail(metaPath, "could not decode entry " + std::to_string(i));
			}
			recButtons.push_back(std::move(states));
			recAxes.push_back(std::move(axes));
		}
		/* NULL mnemonics: the generated characters are the engine's fallback
		 * rather than a frontend vocabulary. Parsing ignores the character, so
		 * a replay of this file lands on the same machine either way. */
		ce_session_movie_record(session, nullptr);
	}

	/* --state: begin from a machine somebody saved earlier. The movie's read
	 * position is NOT wound forward with it (see the header comment): this is
	 * for looking at pictures, not for replaying a run. */
	if (!stateIn.empty())
	{
		std::vector<uint8_t> saved;
		if (!readWholeFile(stateIn.c_str(), saved)) return fail(metaPath, "could not read state " + stateIn);
		if (ce_session_load_state(session, saved.data(), saved.size()) != 0)
		{
			return fail(metaPath, ce_session_last_error(session));
		}
	}

	std::vector<uint8_t> state;
	if (rerecord)
	{
		uint64_t len = 0;
		const uint8_t *p = ce_session_save_state(session, &len);
		if (p == nullptr) return fail(metaPath, ce_session_last_error(session));
		state.assign(p, p + len);
	}

	const int64_t firstPass = playFrames >= 0 && playFrames < frames ? playFrames : frames;
	for (int64_t i = 0; i < firstPass; i++)
	{
		if (rerecord && ce_session_load_state(session, state.data(), state.size()) != 0)
		{
			return fail(metaPath, ce_session_last_error(session));
		}
		if (!recordPath.empty())
		{
			const auto &states = recButtons[static_cast<size_t>(i)];
			for (size_t b = 0; b < states.size(); b++)
			{
				ce_session_set_button(session, static_cast<int32_t>(b), states[b]);
			}
		}
		const int32_t *axes = recordPath.empty() || recAxes[static_cast<size_t>(i)].empty()
			? nullptr
			: recAxes[static_cast<size_t>(i)].data();
		auto shot = shots.find(i);
		if (ce_session_movie_advance(session, 0, axes, shot != shots.end() ? 1 : 0) < 0)
		{
			return fail(metaPath, ce_session_last_error(session));
		}
		if (shot != shots.end()
			&& !writeTga(shot->second, ce_session_video(session),
				ce_session_video_width(session), ce_session_video_height(session)))
		{
			return fail(metaPath, "could not write " + shot->second);
		}
		auto st = stateOuts.find(i);
		if (st != stateOuts.end())
		{
			uint64_t len = 0;
			const uint8_t *p = ce_session_save_state(session, &len);
			if (p == nullptr) return fail(metaPath, ce_session_last_error(session));
			if (!writeWholeFile(st->second, p, static_cast<size_t>(len)))
			{
				return fail(metaPath, "could not write " + st->second);
			}
		}
		if (rerecord)
		{
			uint64_t len = 0;
			const uint8_t *p = ce_session_save_state(session, &len);
			if (p == nullptr) return fail(metaPath, ce_session_last_error(session));
			state.assign(p, p + len);
		}
	}

	if (seekFrame >= 0)
	{
		/* back through the greenzone, then forward again: the dumps at the end
		 * must be the dumps a straight run produces. The invalidate mimics an
		 * input edit and forces the forward seek to actually REPLAY - otherwise
		 * it would just restore the cached end state and prove nothing. */
		if (ce_session_seek(session, seekFrame) != 0) return fail(metaPath, ce_session_last_error(session));
		if (ce_session_frame(session) != seekFrame) return fail(metaPath, "seek landed on the wrong frame");
		/* --stop-at-seek dumps the machine the seek ARRIVED AT, rather than the
		 * machine at the end of a replay from it. It is the difference between
		 * asking whether the run still finishes correctly and asking whether
		 * the history actually holds frame N - and a core whose ending is
		 * decided by its inputs answers the first question yes either way. */
		if (stopAtSeek) frames = seekFrame;
		/* The edit itself. A frontend changes the entries a person retyped and
		 * throws away everything the old ones produced; this puts the whole
		 * edited movie in, which is the same thing said in one go. The
		 * invalidate is what makes the forward seek REPLAY rather than restore
		 * the ending it already had. */
		if (!editFrom.empty())
		{
			std::vector<uint8_t> editText;
			if (!readWholeFile(editFrom.c_str(), editText))
			{
				return fail(metaPath, "could not read " + editFrom);
			}
			ce_movie_log *edited = ce_movie_log_new();
			if (ce_movie_log_parse(edited, reinterpret_cast<const char *>(editText.data()),
					editText.size()) != 0)
			{
				return fail(metaPath, std::string("edit movie: ") + ce_movie_log_last_error(edited));
			}
			if (ce_movie_log_count(edited) < frames)
			{
				return fail(metaPath, "the edit movie is shorter than the run");
			}
			if (ce_session_movie_load(session, edited) != 0)
			{
				return fail(metaPath, "could not load the edited movie");
			}
			ce_movie_log_free(edited);
		}
		ce_session_greenzone_invalidate(session, seekFrame);
		if (!stopAtSeek)
		{
			if (ce_session_seek(session, frames) != 0) return fail(metaPath, ce_session_last_error(session));
			if (ce_session_frame(session) != frames) return fail(metaPath, "replay landed on the wrong frame");
		}
	}

	if (!recordPath.empty())
	{
		/* the session's own log, one entry per line - the shape chimera-run
		 * reads back in, so a recorded movie can simply be replayed */
		const ce_movie_log *log = ce_session_movie_log(session);
		std::string text;
		for (int64_t i = 0; i < ce_movie_log_count(log); i++)
		{
			text += ce_movie_log_entry(log, i);
			text += '\n';
		}
		if (!writeWholeFile(recordPath, reinterpret_cast<const uint8_t *>(text.data()), text.size()))
		{
			return fail(metaPath, "could not write " + recordPath);
		}
		if (ce_movie_log_count(log) != frames)
		{
			return fail(metaPath, "recorded " + std::to_string(ce_movie_log_count(log))
				+ " entries for " + std::to_string(frames) + " frames");
		}
	}

	for (const auto &dump : dumps)
	{
		int32_t count = ce_session_domain_count(session);
		int32_t found = -1;
		for (int32_t d = 0; d < count; d++)
		{
			if (dump.first == ce_session_domain_name(session, d))
			{
				found = d;
				break;
			}
		}
		if (found < 0) return fail(metaPath, "no memory domain named " + dump.first);
		int64_t size = ce_session_domain_size(session, found);
		std::vector<uint8_t> bytes(static_cast<size_t>(size));
		ce_session_domain_read(session, found, 0, bytes.data(), size);
		if (!writeWholeFile(dump.second, bytes.data(), bytes.size()))
		{
			return fail(metaPath, "could not write " + dump.second);
		}
	}

	if (!savedataDir.empty())
	{
		if (ce_session_savedata_available(session) == 0)
		{
			return fail(metaPath, "this core exports no save data");
		}
		int32_t files = ce_session_savedata_count(session);
		std::vector<uint8_t> chunk(1 << 20); // ranged reads: a big file streams
		for (int32_t f = 0; f < files; f++)
		{
			std::string path = savedataDir + "/" + ce_session_savedata_name(session, f);
			makeParentDirs(path);
			FILE *out = std::fopen(path.c_str(), "wb");
			if (out == nullptr) return fail(metaPath, "could not write " + path);
			int64_t size = ce_session_savedata_size(session, f);
			bool ok = true;
			for (int64_t off = 0; off < size && ok;)
			{
				int64_t got = ce_session_savedata_read(session, f, off, chunk.data(), static_cast<int64_t>(chunk.size()));
				if (got <= 0) { ok = false; break; }
				ok = std::fwrite(chunk.data(), 1, static_cast<size_t>(got), out) == static_cast<size_t>(got);
				off += got;
			}
			std::fclose(out);
			if (!ok) return fail(metaPath, "could not write " + path);
		}
		std::printf("savedata=%d\n", files);
	}

	if (!finalShot.empty()
		&& !writeTga(finalShot, ce_session_video(session),
			ce_session_video_width(session), ce_session_video_height(session)))
	{
		return fail(metaPath, "could not write " + finalShot);
	}

	if (!finalBuses.empty())
	{
		/* Every bus the core publishes, end to end, where the run finished.
		 * This is the machine's own memory rather than the sandbox's arena -
		 * two runs that reach the same machine agree here byte for byte, which
		 * the arena does not promise. */
		std::string text;
		for (int32_t b = 0; b < ce_session_bus_count(session); b++)
		{
			int64_t size = ce_session_bus_size(session, b);
			text += ce_session_bus_name(session, b);
			text += ' ';
			uint64_t h = 1469598103934665603ull;
			for (int64_t a = 0; a < size; a++)
			{
				h ^= (uint64_t)(uint8_t)ce_session_bus_peek(session, b, (int32_t)a);
				h *= 1099511628211ull;
			}
			char line[64];
			std::snprintf(line, sizeof line, "%016llx %lld\n", (unsigned long long)h, (long long)size);
			text += line;
		}
		if (!writeWholeFile(finalBuses, reinterpret_cast<const uint8_t *>(text.data()), text.size()))
		{
			return fail(metaPath, "could not write " + finalBuses);
		}
	}

	if (!finalStatePath.empty())
	{
		/* The whole machine where the run actually ended - after any seek and
		 * replay, which is where --save-state cannot reach. */
		uint64_t len = 0;
		const uint8_t *p = ce_session_save_state(session, &len);
		if (p == nullptr) return fail(metaPath, ce_session_last_error(session));
		if (!writeWholeFile(finalStatePath, p, static_cast<size_t>(len)))
		{
			return fail(metaPath, "could not write " + finalStatePath);
		}
	}

	if (!historyOut.empty() && ce_session_history_save(session, historyOut.c_str(), "chimera-run") != 0)
	{
		return fail(metaPath, std::string("history: ") + ce_session_last_error(session));
	}

	if (!metaPath.empty())
	{
		std::string meta = "status=OK\ndetail=\nframes=" + std::to_string(frames) + "\nstartframe=0\n";
		writeWholeFile(metaPath, reinterpret_cast<const uint8_t *>(meta.data()), meta.size());
	}
	std::printf("frames=%lld\n", static_cast<long long>(frames));
	ce_movie_log_free(movie);
	ce_session_free(session);
	if (multi != nullptr) ce_multifile_free(multi);
	if (project != nullptr) ce_project_free(project);
	return 0;
}
