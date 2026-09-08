/* session.cpp - a running waterboxed machine.
 *
 * The transliteration of WaterboxCore.cs's machine half: package + config +
 * rom + settings + firmware in, one frame at a time out. Behavioural notes
 * from the C# are carried over where they matter - especially the ones paid
 * for in blood (no activate/deactivate bracket around save/load state; the
 * frozen post-Init image as the savestate baseline).
 */

#include "chimera/engine.h"
#include <cstdlib>

#include "movie_entry.hpp"
#include "file_io.hpp"
#include "host_dyn.hpp"
#include "progress.hpp"
#include "state_history.hpp"

#include "../../extern/cjson/cJSON.h"

#include <algorithm>
#include <cstring>
#include <map>
#include <string>
#include <vector>

namespace {

thread_local std::string g_openError;

struct ByteStream
{
	const uint8_t *data;
	uint64_t len;
	uint64_t pos = 0;
};

extern "C" intptr_t streamRead(uintptr_t userdata, void *dst, uintptr_t size)
{
	auto *s = reinterpret_cast<ByteStream *>(userdata);
	uint64_t want = size;
	uint64_t avail = s->len - s->pos;
	uint64_t n = want < avail ? want : avail;
	if (n != 0)
	{
		std::memcpy(dst, s->data + s->pos, static_cast<size_t>(n));
		s->pos += n;
	}
	return static_cast<intptr_t>(n);
}

extern "C" int32_t vectorWrite(uintptr_t userdata, const void *src, uintptr_t size)
{
	auto *v = reinterpret_cast<std::vector<uint8_t> *>(userdata);
	const auto *p = static_cast<const uint8_t *>(src);
	v->insert(v->end(), p, p + size);
	return 0;
}

/* the slice of waterbox.config the machine itself needs */
struct SessionConfig
{
	std::string coreName, systemId, romFile;
	uint64_t layoutMiB[5] = { 0, 0, 0, 0, 0 };
	int32_t width = 0, height = 0, virtualWidth = 0, virtualHeight = 0;
	int32_t vsyncNum = 0, vsyncDen = 0;
	std::string getBgra, getAudio;
	int32_t samplesPerFrame = 0, channels = 1;
	std::string inputName, inputWasRead;
	std::vector<std::string> buttons;
	std::vector<chimera::EntryAxis> axes;
	bool deterministic = false;
	std::string defaultsJson; // JSON object: every declared setting at its default
	std::string settingsJson; // the effective settings, serialized for the guest
};

/* effective settings = the declared defaults overlaid with the overrides -
 * shared by open and by a live re-apply */
bool composeSettings(const std::string &defaultsJson, const char *overrides, std::string &out, std::string &error);

const char *strOf(const cJSON *obj, const char *key, const char *fallback = "")
{
	const cJSON *item = cJSON_GetObjectItemCaseSensitive(obj, key);
	return cJSON_IsString(item) ? item->valuestring : fallback;
}

int32_t intOf(const cJSON *obj, const char *key, int32_t fallback = 0)
{
	const cJSON *item = cJSON_GetObjectItemCaseSensitive(obj, key);
	return cJSON_IsNumber(item) ? item->valueint : fallback;
}

/* WHICH MACHINE this session is, for a package that is several (one core.wbx
 * that is a Mega Drive, a Master System, a Game Gear and an SG-1000). The
 * choice is a setting like any other, so it is already in the overrides or in
 * the package's own defaults; what it selects is the machine's system id,
 * controller and picture. A package that is one machine has no machines array
 * and this answers nullptr. */
const cJSON *chooseMachine(const cJSON *root, const char *overrides)
{
	const cJSON *machines = cJSON_GetObjectItemCaseSensitive(root, "machines");
	if (!cJSON_IsArray(machines) || machines->child == nullptr) return nullptr;

	const char *settingName = strOf(root, "machineSetting", nullptr);
	std::string chosen;
	if (settingName != nullptr)
	{
		/* the package's default first, then whatever the caller pinned */
		const cJSON *decls = cJSON_GetObjectItemCaseSensitive(root, "settings");
		const cJSON *decl = nullptr;
		cJSON_ArrayForEach(decl, decls)
		{
			if (std::string(strOf(decl, "name")) != settingName) continue;
			chosen = strOf(decl, "default");
			break;
		}
		cJSON *over = overrides != nullptr && overrides[0] != '\0' ? cJSON_Parse(overrides) : nullptr;
		const cJSON *pinned = cJSON_GetObjectItemCaseSensitive(over, settingName);
		if (cJSON_IsString(pinned)) chosen = pinned->valuestring;
		cJSON_Delete(over);
	}

	const cJSON *machine = nullptr;
	cJSON_ArrayForEach(machine, machines)
	{
		const cJSON *when = cJSON_GetObjectItemCaseSensitive(machine, "when");
		const cJSON *value = nullptr;
		cJSON_ArrayForEach(value, when)
		{
			if (cJSON_IsString(value) && chosen == value->valuestring) return machine;
		}
		if (!cJSON_IsArray(when) && std::string(strOf(machine, "id")) == chosen) return machine;
	}
	/* a value naming no machine is the first one: a session always has a machine */
	return machines->child;
}

bool parseConfig(const char *json, uint64_t len, const char *overrides, SessionConfig &cfg, std::string &error)
{
	cJSON *root = cJSON_ParseWithLength(json, static_cast<size_t>(len));
	if (root == nullptr || !cJSON_IsObject(root))
	{
		cJSON_Delete(root);
		error = "waterbox.config is not readable JSON";
		return false;
	}
	const cJSON *machine = chooseMachine(root, overrides);
	cfg.coreName = strOf(root, "coreName", "Waterbox");
	cfg.systemId = machine != nullptr ? strOf(machine, "id") : strOf(root, "systemId");
	cfg.romFile = strOf(root, "romFile", "rom");
	cfg.deterministic = cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(root, "deterministic"));

	const cJSON *layout = cJSON_GetObjectItemCaseSensitive(root, "memoryLayoutMiB");
	if (!cJSON_IsArray(layout) || cJSON_GetArraySize(layout) != 5)
	{
		cJSON_Delete(root);
		error = "waterbox.config is missing memoryLayoutMiB[5]";
		return false;
	}
	for (int i = 0; i < 5; i++)
	{
		cfg.layoutMiB[i] = static_cast<uint64_t>(cJSON_GetArrayItem(layout, i)->valuedouble);
	}

	const cJSON *video = cJSON_GetObjectItemCaseSensitive(root, "video");
	const cJSON *audio = cJSON_GetObjectItemCaseSensitive(root, "audio");
	/* the controller is the machine's, and the picture may be too; a machine
	 * that declares none shares the package's (dolphin's GameCube and Wii
	 * are one pad) */
	const cJSON *input = machine != nullptr
		? cJSON_GetObjectItemCaseSensitive(machine, "input")
		: nullptr;
	if (!cJSON_IsObject(input))
	{
		input = cJSON_GetObjectItemCaseSensitive(root, "input");
	}
	if (!cJSON_IsObject(video) || !cJSON_IsObject(audio) || !cJSON_IsObject(input))
	{
		cJSON_Delete(root);
		error = "waterbox.config is missing video/audio/input";
		return false;
	}
	cfg.width = intOf(video, "width");
	cfg.height = intOf(video, "height");
	cfg.virtualWidth = intOf(video, "virtualWidth", cfg.width);
	cfg.virtualHeight = intOf(video, "virtualHeight", cfg.height);
	if (machine != nullptr)
	{
		cfg.virtualWidth = intOf(machine, "virtualWidth", cfg.virtualWidth);
		cfg.virtualHeight = intOf(machine, "virtualHeight", cfg.virtualHeight);
	}
	cfg.vsyncNum = intOf(video, "vsyncNumerator");
	cfg.vsyncDen = intOf(video, "vsyncDenominator");
	cfg.getBgra = strOf(video, "getBgra", "GetVideoBgra");
	cfg.samplesPerFrame = intOf(audio, "samplesPerFrame");
	cfg.channels = intOf(audio, "channels", 1);
	cfg.getAudio = strOf(audio, "get", "GetAudio");
	cfg.inputName = strOf(input, "name", "Waterbox Controller");
	const cJSON *buttons = cJSON_GetObjectItemCaseSensitive(input, "buttons");
	const cJSON *item = nullptr;
	if (cJSON_IsArray(buttons))
	{
		cJSON_ArrayForEach(item, buttons)
		{
			if (cJSON_IsString(item)) cfg.buttons.emplace_back(item->valuestring);
		}
	}
	const cJSON *axes = cJSON_GetObjectItemCaseSensitive(input, "axes");
	if (cJSON_IsArray(axes))
	{
		cJSON_ArrayForEach(item, axes)
		{
			chimera::EntryAxis axis;
			axis.name = strOf(item, "name");
			axis.min = intOf(item, "min");
			axis.max = intOf(item, "max");
			axis.neutral = intOf(item, "neutral");
			cfg.axes.push_back(std::move(axis));
		}
	}
	const cJSON *lag = cJSON_GetObjectItemCaseSensitive(root, "lag");
	if (cJSON_IsObject(lag)) cfg.inputWasRead = strOf(lag, "inputWasRead");

	/* every declared setting at its default, kept for open AND for live
	 * re-applies - what the C# EffectiveSettings started from */
	cJSON *defaults = cJSON_CreateObject();
	const cJSON *decls = cJSON_GetObjectItemCaseSensitive(root, "settings");
	if (cJSON_IsArray(decls))
	{
		cJSON_ArrayForEach(item, decls)
		{
			const char *name = strOf(item, "name", nullptr);
			const cJSON *fallback = cJSON_GetObjectItemCaseSensitive(item, "default");
			if (name != nullptr && fallback != nullptr)
			{
				cJSON_AddItemToObject(defaults, name, cJSON_Duplicate(fallback, 1));
			}
		}
	}
	char *printed = cJSON_PrintUnformatted(defaults);
	cfg.defaultsJson = printed != nullptr ? printed : "{}";
	if (printed != nullptr) cJSON_free(printed);
	cJSON_Delete(defaults);
	cJSON_Delete(root);
	return composeSettings(cfg.defaultsJson, overrides, cfg.settingsJson, error);
}

bool composeSettings(const std::string &defaultsJson, const char *overrides, std::string &out, std::string &error)
{
	cJSON *effective = cJSON_Parse(defaultsJson.c_str());
	if (effective == nullptr) effective = cJSON_CreateObject();
	if (overrides != nullptr && overrides[0] != '\0')
	{
		cJSON *over = cJSON_Parse(overrides);
		if (over == nullptr || !cJSON_IsObject(over))
		{
			cJSON_Delete(over);
			cJSON_Delete(effective);
			error = "settings overrides are not readable JSON";
			return false;
		}
		const cJSON *item = nullptr;
		cJSON_ArrayForEach(item, over)
		{
			cJSON *dup = cJSON_Duplicate(item, 1);
			if (cJSON_GetObjectItemCaseSensitive(effective, item->string) != nullptr)
			{
				cJSON_ReplaceItemInObjectCaseSensitive(effective, item->string, dup);
			}
			else
			{
				cJSON_AddItemToObject(effective, item->string, dup);
			}
		}
		cJSON_Delete(over);
	}
	char *printed = cJSON_PrintUnformatted(effective);
	out = printed != nullptr ? printed : "{}";
	if (printed != nullptr) cJSON_free(printed);
	cJSON_Delete(effective);
	return true;
}

} // namespace

/* source/engine/source/gl_bridge.cpp - present in every build; a build without
 * CE_GL_BRIDGE answers that it has no context and nothing here happens. */
extern "C" int32_t ce_gl_start(char *error_out, int32_t error_len);
extern "C" uintptr_t ce_cache_dispatch(uintptr_t op, uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d, uintptr_t e);

/* precompile sessions: asked for before open, like the GPU */
static int32_t s_precompileIndex = 0, s_precompileCount = 0, s_precompileFirmware = 1;

extern "C" void ce_precompile_request(int32_t index, int32_t count, int32_t firmware_too)
{
	s_precompileIndex = index;
	s_precompileCount = count;
	s_precompileFirmware = firmware_too;
}
extern "C" const char *ce_gl_description(void);
extern "C" int32_t ce_gl_requested(void);
extern "C" void ce_gl_release(void);
extern "C" uintptr_t ce_gl_dispatch(uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t);

struct ce_session
{
	SessionConfig cfg;
	const chimera::HostApi *host = nullptr;
	void *obj = nullptr;
	bool active = false;
	/* a GPU outside the sandbox drew this session's pictures */
	bool gpuDrew = false;
	/* the compile cache and precompile sessions (optional exports) */
	bool precompile = false;
	uintptr_t fnCacheStored = 0, fnCacheFetched = 0;
	uintptr_t fnPrecompileDone = 0, fnPrecompileDoneCount = 0, fnPrecompileTotal = 0;

	// the mounted byte sources must outlive the mounts
	std::vector<uint8_t> wbxBytes, romBytes, romNameBytes;
	std::string settingsBytes;
	std::vector<std::vector<uint8_t>> firmwareBytes;
	std::vector<std::pair<std::string, std::vector<uint8_t>>> assetFiles;
	std::vector<std::vector<uint8_t>> extraBytes;
	std::vector<ByteStream> streams; // stable addresses: reserved up front

	// guest entry points (already bridged to our convention)
	void (*frameAdvance)(uint64_t) = nullptr;
	uintptr_t (*getVideoBgra)() = nullptr;
	uintptr_t (*getAudio)() = nullptr;
	int32_t (*inputWasRead)() = nullptr;
	int32_t (*getAudioSampleCount)() = nullptr;
	void (*setAxis)(int32_t, int32_t) = nullptr;
	// wide input: buttons past the packed mask's 64 (a DOS keyboard). The
	// caller's ce_session_set_button values persist here like axes do;
	// deltas cross into the guest through its SetButton export.
	void (*setButton)(int32_t, int32_t) = nullptr;
	std::vector<uint8_t> btnState;     // what ce_session_set_button set
	std::vector<uint8_t> btnSent;      // what the guest last received
	std::vector<uint8_t> btnEffective; // scratch: state OR packed mask
	std::vector<uint8_t> movieButtons; // scratch: a parsed entry's buttons
	/* WHICH DECLARED CONTROLS THIS MACHINE ACTUALLY HAS.
	 *
	 * waterbox.config declares the union of every peripheral a package's ports
	 * can hold, because it is a static declaration and cannot know what a
	 * project plugged in. The running core can: it read the port settings
	 * itself and built the machine from them, so it is the one place the answer
	 * is not a duplicate of somebody else's logic.
	 *
	 * A core that exports neither answer has every declared control, which is
	 * what every core did before this existed. */
	/* Drive lights: one per medium the machine actually has, lit on any frame
	 * that drive was read or written. Optional, and all three or none. */
	int32_t (*driveCount)() = nullptr;
	uintptr_t (*driveName)(int32_t) = nullptr;
	int32_t (*driveLight)(int32_t) = nullptr;
	std::vector<std::string> driveNames;

	int32_t (*isButtonActive)(int32_t) = nullptr;
	int32_t (*isAxisActive)(int32_t) = nullptr;
	std::vector<uint8_t> buttonActive;
	std::vector<uint8_t> axisActive;
	void buildControlActivity();
	int32_t (*mdCount)() = nullptr;
	uintptr_t (*mdName)(int32_t) = nullptr;
	uintptr_t (*mdPtr)(int32_t) = nullptr;
	int64_t (*mdSize)(int32_t) = nullptr;
	int32_t (*mdWritable)(int32_t) = nullptr;

	int32_t vsyncNum = 0, vsyncDen = 0;
	// dynamic video size: a DOS machine changes modes; the guest reports the
	// live frame size (clamped to the config's buffer) through optional
	// exports, and the config's width/height stay the buffer's capacity
	/* CHIMERA_TRACE=<n>: every n frames, the machine's own numbers on stderr.
	 * The per-core diagnostic runners print a line of exactly this shape, so a
	 * machine that misbehaves under the frontend and behaves under the runner
	 * can be diffed on one machine instead of guessed at from two. Everything
	 * here is an optional export; a core that answers nothing prints dashes. */
	int32_t traceEvery = 0;
	int64_t traceFrame = 0, traceLag = 0, traceTtySeen = 0;
	int32_t (*traceThreads)() = nullptr;
	int32_t (*traceRunning)() = nullptr;
	uint64_t (*traceDigest)() = nullptr;
	uint64_t (*traceTimeNs)() = nullptr;
	uintptr_t (*traceTty)() = nullptr;
	int64_t (*traceTtySize)() = nullptr;
	void trace(int32_t lag, int32_t render);

	int32_t (*getVideoWidth)() = nullptr;
	int32_t (*getVideoHeight)() = nullptr;
	int32_t vidW = 0, vidH = 0;
	std::vector<uint32_t> videoBuf;
	std::vector<int16_t> audioBuf;
	int32_t sampleCount = 0;
	std::vector<uint8_t> stateBuf;
	std::string error;

	// ---- the optional guest ABI groups, probed once post-Init ----
	// surfaces
	uintptr_t (*renderSurface)(int32_t) = nullptr;
	std::vector<std::string> surfaceNames;
	std::vector<int32_t> surfaceWidths, surfaceHeights;
	std::vector<std::vector<uint32_t>> surfaceBufs;
	// registers
	int64_t (*regValue)(int32_t) = nullptr;
	void (*regSet)(int32_t, int64_t) = nullptr;
	int64_t (*executedCycles)() = nullptr;
	std::vector<std::string> regNames;
	std::vector<int32_t> regBits;
	// buses
	int32_t (*busPeek)(int32_t, int32_t) = nullptr;
	void (*busPoke)(int32_t, int32_t, int32_t) = nullptr;
	std::vector<std::string> busNames;
	std::vector<int64_t> busSizes;
	std::vector<bool> busWritables;
	// savedata export (docs/save-data.md); a snapshot maps engine index ->
	// guest index because entries with unclean paths are dropped here
	int32_t (*sdCount)() = nullptr;
	uintptr_t (*sdName)(int32_t) = nullptr;
	int64_t (*sdSize)(int32_t) = nullptr;
	uintptr_t (*sdBuffer)(int32_t) = nullptr;
	/* The streaming half of the save-data channel, for a file too big to be
	 * addressable all at once - a DOS hard disk that does not live in guest
	 * memory. The guest serves a window into a scratch buffer of its own; the
	 * file's name and size are unchanged, so what comes out is the same file. */
	int64_t (*sdRead)(int32_t, int64_t, int64_t) = nullptr;
	uintptr_t (*sdScratch)() = nullptr;
	std::vector<std::string> sdNames;
	std::vector<int64_t> sdSizes;
	std::vector<int32_t> sdGuestIndex;
	// turbo: the core is told to stop drawing, and the machine must not
	// notice. -1 means "the guest has not been told anything yet", which is
	// also what a state load leaves behind (see ce_session_load_state).
	void (*setRendering)(int32_t) = nullptr;
	int32_t renderingSent = -1;
	// trace
	void (*traceSetEnabled)(int32_t) = nullptr;
	int32_t (*traceLineCount)() = nullptr;
	uintptr_t (*traceBuffer)() = nullptr;
	int32_t (*traceOverflow)() = nullptr;
	int32_t (*traceUsedBytes)() = nullptr;
	void (*traceClear)() = nullptr;
	std::string traceHeader = "Instructions";
	bool traceDesired = false;
	std::vector<uint8_t> traceOut;

	// ---- the session's movie ----
	ce_movie_log *movie = nullptr;
	int32_t movieMode = 0; // 0 none, 1 play, 2 record, 3 finished
	int64_t frame = 0;
	std::string mnemonics; // one char per button, for generated entries
	chimera::EntryLayout layout; // the Bk2 entry order (see movie_entry.hpp)

	// ---- the state history (docs/state-manager.md) ----
	// Anchors and the deltas that walk forward from them, so a stored frame
	// costs what the frame changed rather than what the machine is.
	chimera::StateHistory history;

	void probeOptionalGroups();
	int32_t advanceCore(const uint8_t *buttons, int32_t render);
	void wantRendering(int32_t on);
	void copyVideo();
	const uint8_t *computeEffective(uint64_t mask);
	uint64_t sendButtons(const uint8_t *states);
	void greenzoneCapture();
	/* the half that must run BEFORE the advance: an epoch has to be marked
	 * while the machine is still where the delta will be measured from */
	void greenzoneBeforeAdvance() { history.beforeAdvance(); }

	bool activate(std::string &err)
	{
		if (active) return true;
		chimera::WbxReturn r{};
		host->wbx_activate_host(obj, &r);
		if (!r.ok()) { err = r.errorMessage; return false; }
		active = true;
		return true;
	}

	bool deactivate(std::string &err)
	{
		if (!active) return true;
		chimera::WbxReturn r{};
		host->wbx_deactivate_host(obj, &r);
		if (!r.ok()) { err = r.errorMessage; return false; }
		active = false;
		return true;
	}

	uintptr_t proc(const char *name, int argCount, bool required, std::string &err)
	{
		chimera::WbxReturn r{};
		host->wbx_get_proc_addr(obj, name, &r);
		if (!r.ok()) { err = r.errorMessage; return 0; }
		if (r.data == 0)
		{
			if (required) err = std::string(cfg.coreName) + ": core.wbx exports no " + name;
			return 0;
		}
		uintptr_t bridged = chimera::bridgeGuestCall(r.data, argCount);
		if (bridged == 0) err = "could not bridge a guest call";
		return bridged;
	}
};

/* Asked once, after Init - which is the only moment it can be asked, because
 * before Init the core has not read its settings and after the first frame the
 * answer must not change: a controller that grew a button mid-movie is not a
 * machine anything can replay. */
void ce_session::buildControlActivity()
{
	buttonActive.assign(cfg.buttons.size(), 1);
	axisActive.assign(cfg.axes.size(), 1);
	if (isButtonActive != nullptr)
	{
		for (size_t i = 0; i < buttonActive.size(); i++)
			buttonActive[i] = isButtonActive(static_cast<int32_t>(i)) != 0 ? 1 : 0;
	}
	if (isAxisActive != nullptr)
	{
		for (size_t i = 0; i < axisActive.size(); i++)
			axisActive[i] = isAxisActive(static_cast<int32_t>(i)) != 0 ? 1 : 0;
	}
}

void ce_session::probeOptionalGroups()
{
	std::string err; // optional probes never fail the session
	auto opt = [&](const char *name, int argCount) { return proc(name, argCount, false, err); };
	auto cstr = [](uintptr_t p) { return p != 0 ? reinterpret_cast<const char *>(p) : nullptr; };

	/* which declared controls this machine has (see buttonActive). Each is
	 * optional on its own: a core whose ports change only its buttons need not
	 * answer for its axes. */
	/* the compile cache's counters and the precompile session's progress */
	fnCacheStored = opt("GetCacheStored", 0);
	fnCacheFetched = opt("GetCacheFetched", 0);
	fnPrecompileDone = opt("IsPrecompileDone", 0);
	fnPrecompileDoneCount = opt("GetPrecompileDone", 0);
	fnPrecompileTotal = opt("GetPrecompileTotal", 0);

	/* the drive lights, all three or none: a count with no way to read a light
	 * is a status bar with dead icons on it */
	{
		auto count = reinterpret_cast<int32_t (*)()>(opt("GetDriveCount", 0));
		auto name = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("GetDriveName", 1));
		auto light = reinterpret_cast<int32_t (*)(int32_t)>(opt("GetDriveLight", 1));
		if (count != nullptr && name != nullptr && light != nullptr)
		{
			driveCount = count;
			driveName = name;
			driveLight = light;
			const int32_t n = count();
			for (int32_t i = 0; i < n; i++)
			{
				const char *nm = cstr(name(i));
				driveNames.push_back(nm != nullptr ? nm : "Drive");
			}
		}
	}

	isButtonActive = reinterpret_cast<int32_t (*)(int32_t)>(opt("IsButtonActive", 1));
	isAxisActive = reinterpret_cast<int32_t (*)(int32_t)>(opt("IsAxisActive", 1));

	// surfaces: all five or nothing
	{
		auto count = reinterpret_cast<int32_t (*)()>(opt("GetSurfaceCount", 0));
		auto name = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("GetSurfaceName", 1));
		auto width = reinterpret_cast<int32_t (*)(int32_t)>(opt("GetSurfaceWidth", 1));
		auto height = reinterpret_cast<int32_t (*)(int32_t)>(opt("GetSurfaceHeight", 1));
		auto render = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("RenderSurface", 1));
		if (count != nullptr && name != nullptr && width != nullptr && height != nullptr && render != nullptr)
		{
			int32_t n = count();
			for (int32_t i = 0; i < n; i++)
			{
				const char *sn = cstr(name(i));
				surfaceNames.emplace_back(sn != nullptr ? sn : ("Surface " + std::to_string(i)));
				surfaceWidths.push_back(width(i));
				surfaceHeights.push_back(height(i));
				surfaceBufs.emplace_back(static_cast<size_t>(width(i)) * height(i), 0);
			}
			if (n > 0) renderSurface = render;
		}
	}

	// registers: count/name/value required; bits, set and cycles optional
	{
		auto count = reinterpret_cast<int32_t (*)()>(opt("GetRegisterCount", 0));
		auto name = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("GetRegisterName", 1));
		auto value = reinterpret_cast<int64_t (*)(int32_t)>(opt("GetRegisterValue", 1));
		if (count != nullptr && name != nullptr && value != nullptr)
		{
			int32_t n = count();
			if (n > 0)
			{
				/* width is per-register and only the core knows it; cores that
				 * don't say get 32 bits, which only shapes the hex display */
				auto bits = reinterpret_cast<int32_t (*)(int32_t)>(opt("GetRegisterBits", 1));
				for (int32_t i = 0; i < n; i++)
				{
					const char *rn = cstr(name(i));
					regNames.emplace_back(rn != nullptr ? rn : ("R" + std::to_string(i)));
					int32_t b = bits != nullptr ? bits(i) : 32;
					regBits.push_back(b > 0 && b <= 64 ? b : 32);
				}
				regValue = value;
				regSet = reinterpret_cast<void (*)(int32_t, int64_t)>(opt("SetRegisterValue", 2));
				executedCycles = reinterpret_cast<int64_t (*)()>(opt("GetExecutedCycles", 0));
			}
		}
	}

	// buses: count/name/peek required; poke, size and writability optional
	{
		auto count = reinterpret_cast<int32_t (*)()>(opt("GetBusCount", 0));
		auto name = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("GetBusName", 1));
		auto peek = reinterpret_cast<int32_t (*)(int32_t, int32_t)>(opt("PeekBus", 2));
		if (count != nullptr && name != nullptr && peek != nullptr)
		{
			auto poke = reinterpret_cast<void (*)(int32_t, int32_t, int32_t)>(opt("PokeBus", 3));
			auto size = reinterpret_cast<int64_t (*)(int32_t)>(opt("GetBusSize", 1));
			auto writable = reinterpret_cast<int32_t (*)(int32_t)>(opt("GetBusWritable", 1));
			int32_t n = count();
			for (int32_t i = 0; i < n; i++)
			{
				const char *bn = cstr(name(i));
				busNames.emplace_back(bn != nullptr ? bn : ("Bus " + std::to_string(i)));
				busSizes.push_back(size != nullptr ? size(i) : 0x10000); // 64K default: the common case
				/* a core with a poke may still have read-only buses; claiming
				 * writable there hands the hex editor a silently discarded edit */
				busWritables.push_back(poke != nullptr && (writable != nullptr ? writable(i) : 1) != 0);
			}
			busPeek = peek;
			busPoke = poke;
		}
	}

	// trace: enable/lineCount/buffer required; the rest optional
	{
		auto setEnabled = reinterpret_cast<void (*)(int32_t)>(opt("TraceSetEnabled", 1));
		auto lineCount = reinterpret_cast<int32_t (*)()>(opt("TraceGetLineCount", 0));
		auto buffer = reinterpret_cast<uintptr_t (*)()>(opt("TraceGetBuffer", 0));
		if (setEnabled != nullptr && lineCount != nullptr && buffer != nullptr)
		{
			traceLineCount = lineCount;
			traceBuffer = buffer;
			traceOverflow = reinterpret_cast<int32_t (*)()>(opt("TraceGetOverflow", 0));
			traceUsedBytes = reinterpret_cast<int32_t (*)()>(opt("TraceGetUsedBytes", 0));
			traceClear = reinterpret_cast<void (*)()>(opt("TraceClear", 0));
			auto header = reinterpret_cast<uintptr_t (*)()>(opt("TraceGetHeader", 0));
			const char *h = header != nullptr ? cstr(header()) : nullptr;
			if (h != nullptr) traceHeader = h;
			traceSetEnabled = setEnabled;
			traceSetEnabled(0); // tracing costs the guest time; off until a sink asks
		}
	}

	// turbo: one export, and a core either has it or draws every frame. The
	// guest is told nothing here - the first advance sends whatever it wants,
	// and until then the core's own default (drawing) stands.
	setRendering = reinterpret_cast<void (*)(int32_t)>(opt("SetRenderingEnabled", 1));

	// savedata export: all four or nothing. Only the POINTERS are kept - the
	// file list is dynamic (a game creates files while it runs), so it is
	// snapshotted at export time, never here.
	{
		auto count = reinterpret_cast<int32_t (*)()>(opt("GetSaveDataFileCount", 0));
		auto name = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("GetSaveDataFileName", 1));
		auto size = reinterpret_cast<int64_t (*)(int32_t)>(opt("GetSaveDataFileSize", 1));
		auto buffer = reinterpret_cast<uintptr_t (*)(int32_t)>(opt("GetSaveDataFileBuffer", 1));
		if (count != nullptr && name != nullptr && size != nullptr && buffer != nullptr)
		{
			sdCount = count;
			sdName = name;
			sdSize = size;
			sdBuffer = buffer;
		}
		/* ...and the streaming pair, which replaces the pointer when a core has
		 * one. Both or neither: a reader that could ask for a window but not
		 * find it would silently export nothing. */
		auto read = reinterpret_cast<int64_t (*)(int32_t, int64_t, int64_t)>(opt("ReadSaveDataFile", 3));
		auto scratch = reinterpret_cast<uintptr_t (*)()>(opt("GetSaveDataScratch", 0));
		if (read != nullptr && scratch != nullptr)
		{
			sdRead = read;
			sdScratch = scratch;
		}
	}
}





/* The effective button states this frame: what ce_session_set_button holds,
 * OR'd with the packed mask's low 64 - callers use one path or the other, and
 * the OR keeps either alone exact. */
const uint8_t *ce_session::computeEffective(uint64_t mask)
{
	size_t n = btnEffective.size();
	for (size_t i = 0; i < n; i++)
	{
		uint8_t v = btnState[i];
		if (i < 64 && ((mask >> i) & 1ull) != 0) v = 1;
		btnEffective[i] = v;
	}
	return btnEffective.data();
}

/* Delivers wide states to the guest (deltas through its SetButton export -
 * only changes cross the boundary) and returns the packed mask for the low
 * 64, which every FrameAdvance still receives. */
uint64_t ce_session::sendButtons(const uint8_t *states)
{
	uint64_t mask = 0;
	size_t n = btnSent.size();
	for (size_t i = 0; i < n; i++)
	{
		uint8_t v = states != nullptr && states[i] != 0 ? 1 : 0;
		if (i < 64 && v != 0) mask |= 1ull << i;
		if (setButton != nullptr && v != btnSent[i])
		{
			setButton(static_cast<int32_t>(i), v);
			btnSent[i] = v;
		}
	}
	return mask;
}

void ce_session::copyVideo()
{
	/* THE FRAME FIRST, THEN ITS SIZE. A core settles what it is about to hand
	 * over while it hands it over - Flycast asks its renderer for the finished
	 * picture inside GetVideoBgra, and only then knows whether the machine is
	 * in a 640x480 mode or a 320x240 one. Asking the size first reads the
	 * PREVIOUS frame's, which is a stride away from the data that follows: the
	 * 240p Test Suite came out as two half-width copies of itself, because a
	 * 320-wide picture was copied 640 wide.
	 *
	 * Both gate runners have always done it in this order, which is the order
	 * the cores were written against; this was the odd one out.
	 *
	 * A machine that has not rendered yet answers null (DOS before its first
	 * mode set); the buffer keeps its previous frame. */
	const void *src = reinterpret_cast<const void *>(getVideoBgra());
	if (getVideoWidth != nullptr && getVideoHeight != nullptr)
	{
		int32_t w = getVideoWidth(), h = getVideoHeight();
		if (w > 0 && h > 0)
		{
			vidW = w < cfg.width ? w : cfg.width;
			vidH = h < cfg.height ? h : cfg.height;
		}
	}
	if (src != nullptr)
	{
		std::memcpy(videoBuf.data(), src, static_cast<size_t>(vidW) * vidH * sizeof(uint32_t));
	}
}

/* One line per traceEvery frames, on stderr, cheap enough to leave in a
 * release build because it costs nothing until the variable is set. The video
 * checksum is what tells a black picture apart from a missing one: a machine
 * drawing a dark scene still moves it. */
void ce_session::trace(int32_t lag, int32_t render)
{
	traceFrame++;
	if (lag != 0) traceLag++;
	if (traceEvery <= 0) return;
	if (traceTty != nullptr && traceTtySize != nullptr)
	{
		const int64_t n = traceTtySize();
		const auto *text = reinterpret_cast<const uint8_t *>(traceTty());
		if (text != nullptr && n > traceTtySeen)
		{
			fwrite(text + traceTtySeen, 1, static_cast<size_t>(n - traceTtySeen), stderr);
			traceTtySeen = n;
		}
		else if (n < traceTtySeen)
		{
			traceTtySeen = n; // a state load rewound the machine's own log
		}
	}
	if ((traceFrame % traceEvery) != 0) return;
	uint64_t vsum = 0;
	uint64_t vlit = 0;
	if (render != 0)
	{
		const size_t px = static_cast<size_t>(vidW) * static_cast<size_t>(vidH);
		for (size_t i = 0; i < px && i < videoBuf.size(); i++)
		{
			const uint32_t p = videoBuf[i];
			vsum = vsum * 131 + p;
			if ((p & 0x00FFFFFFu) != 0) vlit++;
		}
	}
	fprintf(stderr,
		"[trace] frame %lld lag %lld threads %d running %d time %llums digest %016llx "
		"video %dx%d sum %016llx lit %llu audio %d\n",
		(long long)traceFrame, (long long)traceLag,
		traceThreads != nullptr ? traceThreads() : -1,
		traceRunning != nullptr ? traceRunning() : -1,
		(unsigned long long)(traceTimeNs != nullptr ? traceTimeNs() / 1000000ull : 0ull),
		(unsigned long long)(traceDigest != nullptr ? traceDigest() : 0ull),
		vidW, vidH, (unsigned long long)vsum, (unsigned long long)vlit, sampleCount);
	fflush(stderr);
}

/* Turbo. A frame nobody is going to look at does not need to be drawn, and for
 * a machine with a 3D chip in it the drawing is most of the frame. The core is
 * asked to stop producing a picture; it must go on being exactly the machine it
 * would have been, which is the whole contract and what each core's gate
 * checks. Cores without the export simply draw, and the caller pays.
 *
 * The state is a delta: telling a core the same thing every frame would be one
 * pointless guest call per frame on the seek path, which is the path this
 * exists for. */
void ce_session::wantRendering(int32_t on)
{
	if (setRendering == nullptr || renderingSent == on) return;
	setRendering(on);
	renderingSent = on;
}

int32_t ce_session::advanceCore(const uint8_t *buttons, int32_t render)
{
	wantRendering(render != 0 ? 1 : 0);
	frameAdvance(sendButtons(buttons));
	if (render != 0) copyVideo();
	int32_t nsamp = getAudioSampleCount != nullptr ? getAudioSampleCount() : cfg.samplesPerFrame;
	if (nsamp < 0) nsamp = 0;
	if (nsamp > cfg.samplesPerFrame) nsamp = cfg.samplesPerFrame;
	const auto *src = reinterpret_cast<const int16_t *>(getAudio());
	if (cfg.channels == 2)
	{
		std::memcpy(audioBuf.data(), src, static_cast<size_t>(nsamp) * 2 * sizeof(int16_t));
	}
	else
	{
		for (int32_t i = 0; i < nsamp; i++)
		{
			audioBuf[static_cast<size_t>(i) * 2] = src[i];
			audioBuf[static_cast<size_t>(i) * 2 + 1] = src[i];
		}
	}
	sampleCount = nsamp;
	frame++;
	const int32_t lag = inputWasRead != nullptr && inputWasRead() == 0 ? 1 : 0;
	trace(lag, render);
	return lag;
}

void ce_session::greenzoneCapture()
{
	history.capture(frame);
}

extern "C" {

ce_session *ce_session_open(
	const char *package_path,
	const uint8_t *rom, uint64_t rom_len, const char *rom_path,
	const char *settings_overrides_json,
	const char *const *firmware_ids, const uint8_t *const *firmware_data,
	const uint64_t *firmware_lens, int32_t firmware_count,
	const char *const *extra_names, const uint8_t *const *extra_data,
	const uint64_t *extra_lens, const char *const *extra_paths, int32_t extra_count,
	const char **error_out)
{
	auto fail = [&](std::string message) -> ce_session *
	{
		g_openError = std::move(message);
		if (error_out != nullptr) *error_out = g_openError.c_str();
		return nullptr;
	};
	if (error_out != nullptr) *error_out = nullptr;

	const char *hostError = nullptr;
	const chimera::HostApi *host = chimera::hostApi(&hostError);
	if (host == nullptr) return fail(hostError);

	const char *pkgError = nullptr;
	chimera::progress("reading the core package", 0, 0);
	ce_package *pkg = ce_package_open(package_path, &pkgError);
	if (pkg == nullptr)
	{
		return fail(pkgError != nullptr ? pkgError : std::string(package_path) + " is not a core package");
	}
	if (!ce_package_is_waterbox(pkg))
	{
		ce_package_free(pkg);
		return fail(std::string(package_path) + " is not a waterbox core package");
	}

	auto *s = new ce_session();
	s->host = host;
	auto abort = [&](std::string message) -> ce_session *
	{
		if (pkg != nullptr) ce_package_free(pkg);
		pkg = nullptr;
		ce_session_free(s);
		return fail(std::move(message));
	};

	uint64_t len = 0;
	const uint8_t *entry = ce_package_entry(pkg, "waterbox.config", &len);
	if (entry == nullptr) return abort("package has no waterbox.config");
	std::string cfgError;
	if (!parseConfig(reinterpret_cast<const char *>(entry), len, settings_overrides_json, s->cfg, cfgError))
	{
		return abort(std::move(cfgError));
	}
	entry = ce_package_entry(pkg, "core.wbx", &len);
	if (entry == nullptr) return abort("package has no core.wbx");
	s->wbxBytes.assign(entry, entry + len);
	/* core-owned assets travel in the package and are mounted below with the
	 * "assets" prefix dropped: assets/sys/... becomes /sys/... in the guest */
	const int32_t asset_count = ce_package_asset_count(pkg);
	s->assetFiles.reserve(static_cast<size_t>(asset_count));
	for (int32_t i = 0; i < asset_count; i++)
	{
		const char *aname = ce_package_asset_name(pkg, i);
		if (aname == nullptr) continue;
		const uint8_t *abytes = ce_package_entry(pkg, aname, &len);
		if (abytes == nullptr) return abort(std::string("package asset unreadable: ") + aname);
		s->assetFiles.emplace_back(std::string(aname).substr(6), std::vector<uint8_t>(abytes, abytes + len));
	}
	ce_package_free(pkg);
	pkg = nullptr; /* a later abort() must not free it again */

	const bool romFromDisk = rom_path != nullptr && rom_path[0] != '\0';
	if (!romFromDisk && rom != nullptr && rom_len != 0) s->romBytes.assign(rom, rom + rom_len);
	s->settingsBytes = s->cfg.settingsJson;

	// every mounted stream needs a stable address for the host's callback
	s->streams.reserve(5 + static_cast<size_t>(firmware_count) + static_cast<size_t>(extra_count)
	                   + s->assetFiles.size());

	chimera::WbxLayout layout{};
	layout.sbrkSize = static_cast<uintptr_t>(s->cfg.layoutMiB[0] << 20);
	layout.sealedSize = static_cast<uintptr_t>(s->cfg.layoutMiB[1] << 20);
	layout.invisSize = static_cast<uintptr_t>(s->cfg.layoutMiB[2] << 20);
	layout.plainSize = static_cast<uintptr_t>(s->cfg.layoutMiB[3] << 20);
	layout.mmapSize = static_cast<uintptr_t>(s->cfg.layoutMiB[4] << 20);

	chimera::WbxReturn r{};
	s->streams.push_back({ s->wbxBytes.data(), s->wbxBytes.size() });
	host->wbx_create_host(&layout, "core.wbx", streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), &r);
	if (!r.ok()) return abort(r.errorMessage);
	s->obj = reinterpret_cast<void *>(r.data);

	chimera::progress("mounting the game", 0, 0);
	/* The game itself, which is the one most likely to be enormous: mounted
	 * from where it lies when the caller said where that is. */
	if (romFromDisk)
	{
		host->wbx_mount_file_path(s->obj, s->cfg.romFile.c_str(), rom_path, &r);
	}
	else
	{
		s->streams.push_back({ s->romBytes.data(), s->romBytes.size() });
		host->wbx_mount_file(s->obj, s->cfg.romFile.c_str(), streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
	}
	if (!r.ok()) return abort(std::string("mounting ") + s->cfg.romFile + ": " + r.errorMessage);

	/* The same bytes under the file's real name too, the way a project mounts
	 * its slot files: an extension-driven core (a GameCube .dol vs .iso) can
	 * then boot the name instead of guessing from the fixed mount. */
	std::string romAlias;
	if (rom_path != nullptr && rom_path[0] != '\0')
	{
		const char *base = strrchr(rom_path, '/');
#ifdef _WIN32
		const char *bs = strrchr(rom_path, '\\');
		if (bs != nullptr && (base == nullptr || bs > base)) base = bs;
#endif
		base = base != nullptr ? base + 1 : rom_path;
		romAlias = std::string("/") + base;
		const std::string &alias = romAlias;
		if (alias != s->cfg.romFile)
		{
			if (romFromDisk)
			{
				host->wbx_mount_file_path(s->obj, alias.c_str(), rom_path, &r);
			}
			else
			{
				s->streams.push_back({ s->romBytes.data(), s->romBytes.size() });
				host->wbx_mount_file(s->obj, alias.c_str(), streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
			}
			if (!r.ok()) return abort(std::string("mounting ") + alias + ": " + r.errorMessage);
		}
	}

	/* a directly-opened rom has a name too: when no caller-provided extra
	 * carries "rom.name", it is the name the rom was just MOUNTED under, so a
	 * core can open what rom.name says and find it there. It used to be the
	 * bare basename while the mount carried a leading slash, and every core
	 * that booted rom.name verbatim had to paper over the difference (Dolphin
	 * and PPSSPP both probe two spellings because of it). */
	{
		bool haveName = false;
		for (int32_t i = 0; i < extra_count; i++)
			if (extra_names != nullptr && extra_names[i] != nullptr && strcmp(extra_names[i], "rom.name") == 0)
			{
				haveName = true;
				break;
			}
		if (!haveName && rom_path != nullptr && rom_path[0] != '\0')
		{
			const char *base = strrchr(rom_path, '/');
#ifdef _WIN32
			const char *bs = strrchr(rom_path, '\\');
			if (bs != nullptr && (base == nullptr || bs > base)) base = bs;
#endif
			base = base != nullptr ? base + 1 : rom_path;
			const std::string name = romAlias.empty() ? std::string(base) : romAlias;
			s->romNameBytes.assign(name.begin(), name.end());
			s->streams.push_back({ s->romNameBytes.data(), s->romNameBytes.size() });
			host->wbx_mount_file(s->obj, "rom.name", streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
			if (!r.ok()) return abort(std::string("mounting rom.name: ") + r.errorMessage);
		}
	}

	/* the settings channel: always mounted (empty object when the core has no
	 * settings), so the guest ABI is uniform */
	s->streams.push_back({ reinterpret_cast<const uint8_t *>(s->settingsBytes.data()), s->settingsBytes.size() });
	host->wbx_mount_file(s->obj, "settings", streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
	if (!r.ok()) return abort(r.errorMessage);

	for (int32_t i = 0; i < firmware_count; i++)
	{
		s->firmwareBytes.emplace_back(firmware_data[i], firmware_data[i] + firmware_lens[i]);
		s->streams.push_back({ s->firmwareBytes.back().data(), s->firmwareBytes.back().size() });
		host->wbx_mount_file(s->obj, firmware_ids[i], streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
		if (!r.ok()) return abort(r.errorMessage);
	}

	for (const auto &asset : s->assetFiles)
	{
		s->streams.push_back({ asset.second.data(), asset.second.size() });
		host->wbx_mount_file(s->obj, asset.first.c_str(), streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
		if (!r.ok()) return abort(std::string("mounting asset ") + asset.first + ": " + r.errorMessage);
	}

	/* a multi-file game's additional mounts: rom2..romN, support files,
	 * savedata, rom.name - the engine mounts names, the core gives them
	 * meaning (the same division firmware uses) */
	for (int32_t i = 0; i < extra_count; i++)
	{
		const char *path = extra_paths != nullptr ? extra_paths[i] : nullptr;
		if (path != nullptr && path[0] != '\0')
		{
			host->wbx_mount_file_path(s->obj, extra_names[i], path, &r);
		}
		else
		{
			s->extraBytes.emplace_back(extra_data[i], extra_data[i] + extra_lens[i]);
			s->streams.push_back({ s->extraBytes.back().data(), s->extraBytes.back().size() });
			host->wbx_mount_file(s->obj, extra_names[i], streamRead, reinterpret_cast<uintptr_t>(&s->streams.back()), 0, &r);
		}
		if (!r.ok()) return abort(std::string("mounting ") + extra_names[i] + ": " + r.errorMessage);
	}

	std::string err;
	if (!s->activate(err)) return abort(std::move(err));

	/* The GPU bridge, before Init because Init is where a core picks its
	 * renderer. Three things have to be true and any of them may not be: the
	 * caller asked (ce_gl_request), this build has a bridge and a driver that
	 * will give it a context, and the core knows what to do with one. When any
	 * fails the core draws the way it draws without a GPU, which is the
	 * deterministic way, and the session simply does not claim otherwise.
	 */
	/* CHIMERA_NO_GPU=1 refuses the bridge for this run whatever the project
	 * asked for. A core drawn by a GPU and a core drawn by nobody fail in
	 * different ways, and telling them apart on a machine that is not here is
	 * otherwise a rebuild away. */
	const char *noGpu = getenv("CHIMERA_NO_GPU");
	if (noGpu != nullptr && noGpu[0] != '\0' && noGpu[0] != '0')
	{
		fprintf(stderr, "chimera gl: refused by CHIMERA_NO_GPU\n");
	}
	else if (ce_gl_requested() != 0)
	{
		std::string ignored;
		auto setBridge = reinterpret_cast<void (*)(uint64_t)>(
			s->proc("SetGpuBridge", 1, false, ignored));
		char glerr[256] = "";
		if (setBridge == nullptr)
		{
			/* not a failure: most cores have no GL renderer at all */
		}
		else if (ce_gl_start(glerr, (int32_t)sizeof glerr) == 0)
		{
			fprintf(stderr, "chimera gl: no context (%s); drawing in software\n", glerr);
		}
		else
		{
			chimera::WbxReturn r;
			host->wbx_get_callback_addr(s->obj, reinterpret_cast<void *>(&ce_gl_dispatch), 0, &r);
			if (!r.ok() || r.data == 0)
			{
				fprintf(stderr, "chimera gl: the sandbox would not take the callback\n");
			}
			else
			{
				setBridge(static_cast<uint64_t>(r.data));
				/* The core decides whether it took it - an older core, or one
				 * built against a longer entry-point list, declines - and it
				 * says so through the renderer it then reports using. What is
				 * certain here is that a GPU was offered and a context exists;
				 * the session records that, and the movie says a GPU drew. */
				s->gpuDrew = true;
				fprintf(stderr, "chimera gl: %s\n", ce_gl_description());
			}
		}
	}

	/* The compile cache, before Init because Init is where a core first asks
	 * for compiled objects. A core without the export keeps nothing. */
	if (ce_cache_dir_get()[0] != 0)
	{
		std::string ignored;
		auto setCache = reinterpret_cast<void (*)(uint64_t)>(s->proc("SetCacheBridge", 1, false, ignored));
		if (setCache != nullptr)
		{
			chimera::WbxReturn r;
			host->wbx_get_callback_addr(s->obj, reinterpret_cast<void *>(&ce_cache_dispatch), 1, &r);
			if (!r.ok() || r.data == 0)
				fprintf(stderr, "chimera cache: the sandbox would not take the callback\n");
			else
				setCache(static_cast<uint64_t>(r.data));
		}
	}

	/* A precompile session: the core boots, compiles its share and stops. */
	if (s_precompileCount > 0)
	{
		std::string ignored;
		auto setPre = reinterpret_cast<void (*)(int32_t, int32_t, int32_t)>(s->proc("SetPrecompile", 3, false, ignored));
		if (setPre == nullptr)
		{
			return abort(std::string(s->cfg.coreName) + ": this core has no precompile session");
		}
		setPre(s_precompileIndex, s_precompileCount, s_precompileFirmware);
		s->precompile = true;
	}

	chimera::progress("starting the machine", 0, 0);
	auto init = reinterpret_cast<int32_t (*)()>(s->proc("Init", 0, true, err));
	if (init == nullptr) return abort(std::move(err));
	const int32_t initResult = init();
	/* Init is where a renderer makes its context and compiles its shaders, so
	 * it is also where the bridge first borrows the caller's GL slot. */
	ce_gl_release();
	if (initResult != 1)
	{
		/* the core knows why it refused - GetLoadError is optional, so a core
		 * that says nothing still fails, just less helpfully */
		std::string reason;
		auto getLoadError = reinterpret_cast<uintptr_t (*)()>(s->proc("GetLoadError", 0, false, err));
		if (getLoadError != nullptr)
		{
			const char *text = reinterpret_cast<const char *>(getLoadError());
			if (text != nullptr) reason = text;
		}
		return abort(reason.empty()
			? s->cfg.coreName + " could not load this file."
			: s->cfg.coreName + ": " + reason);
	}

	if (!s->deactivate(err)) return abort(std::move(err));
	chimera::progress("sealing the machine", 0, 0);
	host->wbx_seal(s->obj, &r); // freeze the post-init image as the savestate baseline
	if (!r.ok()) return abort(r.errorMessage);
	if (!s->activate(err)) return abort(std::move(err));

	s->frameAdvance = reinterpret_cast<void (*)(uint64_t)>(s->proc("FrameAdvance", 1, true, err));
	if (s->frameAdvance == nullptr) return abort(std::move(err));
	if (!s->cfg.axes.empty())
	{
		s->setAxis = reinterpret_cast<void (*)(int32_t, int32_t)>(s->proc("SetAxis", 2, true, err));
		if (s->setAxis == nullptr)
		{
			return abort(s->cfg.coreName + ": waterbox.config declares axes but core.wbx exports no SetAxis");
		}
	}
	/* wide input: optional in general, REQUIRED past 64 buttons - without the
	 * export the extra buttons could never reach the machine, and a controller
	 * that silently drops keys is worse than one that refuses to load */
	s->setButton = reinterpret_cast<void (*)(int32_t, int32_t)>(s->proc("SetButton", 2, false, err));
	if (s->cfg.buttons.size() > 64 && s->setButton == nullptr)
	{
		return abort(s->cfg.coreName + ": waterbox.config declares "
			+ std::to_string(s->cfg.buttons.size())
			+ " buttons (more than the packed 64) but core.wbx exports no SetButton");
	}
	s->getVideoBgra = reinterpret_cast<uintptr_t (*)()>(s->proc(s->cfg.getBgra.c_str(), 0, true, err));
	if (s->getVideoBgra == nullptr) return abort(std::move(err));
	s->getAudio = reinterpret_cast<uintptr_t (*)()>(s->proc(s->cfg.getAudio.c_str(), 0, true, err));
	if (s->getAudio == nullptr) return abort(std::move(err));

	/* optional exports: the guest's own answers override the config's */
	s->getAudioSampleCount = reinterpret_cast<int32_t (*)()>(s->proc("GetAudioSampleCount", 0, false, err));
	s->getVideoWidth = reinterpret_cast<int32_t (*)()>(s->proc("GetVideoWidth", 0, false, err));
	s->getVideoHeight = reinterpret_cast<int32_t (*)()>(s->proc("GetVideoHeight", 0, false, err));
	s->vidW = s->cfg.width;
	s->vidH = s->cfg.height;
	auto vsyncN = reinterpret_cast<int32_t (*)()>(s->proc("GetVsyncNumerator", 0, false, err));
	auto vsyncD = reinterpret_cast<int32_t (*)()>(s->proc("GetVsyncDenominator", 0, false, err));
	s->vsyncNum = vsyncN != nullptr ? vsyncN() : 0;
	s->vsyncDen = vsyncD != nullptr ? vsyncD() : 0;
	if (s->vsyncNum <= 0 || s->vsyncDen <= 0)
	{
		s->vsyncNum = s->cfg.vsyncNum;
		s->vsyncDen = s->cfg.vsyncDen;
	}
	if (!s->cfg.inputWasRead.empty())
	{
		s->inputWasRead = reinterpret_cast<int32_t (*)()>(s->proc(s->cfg.inputWasRead.c_str(), 0, true, err));
		if (s->inputWasRead == nullptr) return abort(std::move(err));
	}

	/* the trace's optional exports, resolved only when someone asked for it */
	if (const char *traceEnv = getenv("CHIMERA_TRACE"))
	{
		s->traceEvery = atoi(traceEnv);
		if (s->traceEvery <= 0) s->traceEvery = 100;
		s->traceThreads = reinterpret_cast<int32_t (*)()>(s->proc("GetThreadCount", 0, false, err));
		s->traceRunning = reinterpret_cast<int32_t (*)()>(s->proc("IsRunning", 0, false, err));
		s->traceDigest = reinterpret_cast<uint64_t (*)()>(s->proc("GetMainMemoryDigest", 0, false, err));
		s->traceTimeNs = reinterpret_cast<uint64_t (*)()>(s->proc("GetMachineTimeNs", 0, false, err));
		s->traceTty = reinterpret_cast<uintptr_t (*)()>(s->proc("GetTty", 0, false, err));
		s->traceTtySize = reinterpret_cast<int64_t (*)()>(s->proc("GetTtySize", 0, false, err));
		err.clear(); // every one of them is allowed to be absent
		fprintf(stderr, "[trace] every %d frames; %d firmware file(s)", s->traceEvery, firmware_count);
		for (int32_t i = 0; i < firmware_count; i++)
		{
			fprintf(stderr, " %s=%llu", firmware_ids[i], (unsigned long long)firmware_lens[i]);
		}
		fprintf(stderr, "; rom %s\n", rom_path != nullptr ? rom_path : "(bytes)");
		fflush(stderr);
	}

	/* memory domains are self-described post-Init (size can depend on settings) */
	s->mdCount = reinterpret_cast<int32_t (*)()>(s->proc("GetMemoryDomainCount", 0, true, err));
	s->mdName = reinterpret_cast<uintptr_t (*)(int32_t)>(s->proc("GetMemoryDomainName", 1, true, err));
	s->mdPtr = reinterpret_cast<uintptr_t (*)(int32_t)>(s->proc("GetMemoryDomainPtr", 1, true, err));
	s->mdSize = reinterpret_cast<int64_t (*)(int32_t)>(s->proc("GetMemoryDomainSize", 1, true, err));
	s->mdWritable = reinterpret_cast<int32_t (*)(int32_t)>(s->proc("GetMemoryDomainWritable", 1, true, err));
	if (s->mdWritable == nullptr) return abort(std::move(err));

	s->videoBuf.assign(static_cast<size_t>(s->cfg.width) * s->cfg.height, 0);
	s->audioBuf.assign(static_cast<size_t>(s->cfg.samplesPerFrame) * 2, 0);
	s->btnState.assign(s->cfg.buttons.size(), 0);
	s->btnSent.assign(s->cfg.buttons.size(), 0); // a fresh guest holds nothing down
	s->btnEffective.assign(s->cfg.buttons.size(), 0);
	s->probeOptionalGroups();
	s->buildControlActivity();
	s->layout.build(s->cfg.buttons, s->cfg.axes, &s->buttonActive, &s->axisActive);
	return s;
}

void ce_session_free(ce_session *s)
{
	if (s != nullptr && ce_cache_dir_get()[0] != 0 && (s->fnCacheStored != 0 || s->fnCacheFetched != 0))
	{
		fprintf(stderr, "chimera cache: %llu stored, %llu fetched\n",
			(unsigned long long)ce_session_cache_stored(s), (unsigned long long)ce_session_cache_fetched(s));
	}
	if (s == nullptr) return;
	if (s->movie != nullptr) ce_movie_log_free(s->movie);
	if (s->obj != nullptr)
	{
		std::string err;
		s->deactivate(err); // tearing down anyway
		chimera::WbxReturn r{};
		s->host->wbx_destroy_host(s->obj, &r);
	}
	delete s;
}

const char *ce_session_core_name(const ce_session *s) { return s->cfg.coreName.c_str(); }
const char *ce_session_system_id(const ce_session *s) { return s->cfg.systemId.c_str(); }
int32_t ce_session_width(const ce_session *s) { return s->cfg.width; }
int32_t ce_session_height(const ce_session *s) { return s->cfg.height; }
int32_t ce_session_virtual_width(const ce_session *s) { return s->cfg.virtualWidth; }
int32_t ce_session_virtual_height(const ce_session *s) { return s->cfg.virtualHeight; }
int32_t ce_session_vsync_numerator(const ce_session *s) { return s->vsyncNum; }
int32_t ce_session_vsync_denominator(const ce_session *s) { return s->vsyncDen; }
int32_t ce_session_samples_per_frame(const ce_session *s) { return s->cfg.samplesPerFrame; }
int32_t ce_session_channels(const ce_session *s) { return s->cfg.channels; }
/* A machine a GPU drew is not the deterministic one, whatever its config says:
 * the GPU is outside the sandbox, outside the savestate, and different on every
 * machine it runs on. */
int32_t ce_session_deterministic(const ce_session *s)
{
	return (s->cfg.deterministic && !s->gpuDrew) ? 1 : 0;
}

/* Whether a GPU outside the sandbox drew this session's pictures. A movie
 * records it, so a replay that desyncs elsewhere can be understood. */
int32_t ce_session_gpu_drew(const ce_session *s) { return s->gpuDrew ? 1 : 0; }
int64_t ce_session_button_count(const ce_session *s) { return static_cast<int64_t>(s->cfg.buttons.size()); }

const char *ce_session_button_name(const ce_session *s, int64_t index)
{
	if (index < 0 || index >= static_cast<int64_t>(s->cfg.buttons.size())) return nullptr;
	return s->cfg.buttons[static_cast<size_t>(index)].c_str();
}

/* Whether a declared control is one this machine HAS. A package declares the
 * union of every peripheral its ports can hold; the project decides which of
 * them are plugged in, and the core is the only thing that knows. A control
 * that is not active is not in the frontend's controller, not a column in
 * TAStudio, and not a character in a movie entry - but its index on the wire
 * never moves, so nothing else has to care. */
/* The drive lights. Names are settled at load - a machine does not grow a
 * drive - and the light itself is asked every frame. */
int32_t ce_session_drive_count(const ce_session *s)
{
	return static_cast<int32_t>(s->driveNames.size());
}

const char *ce_session_drive_name(const ce_session *s, int32_t index)
{
	if (index < 0 || static_cast<size_t>(index) >= s->driveNames.size()) return nullptr;
	return s->driveNames[static_cast<size_t>(index)].c_str();
}

int32_t ce_session_drive_light(const ce_session *s, int32_t index)
{
	if (s->driveLight == nullptr || index < 0
		|| static_cast<size_t>(index) >= s->driveNames.size()) return 0;
	return s->driveLight(index) != 0 ? 1 : 0;
}

int32_t ce_session_button_active(const ce_session *s, int64_t index)
{
	if (index < 0 || index >= static_cast<int64_t>(s->buttonActive.size())) return 0;
	return s->buttonActive[static_cast<size_t>(index)];
}

int64_t ce_session_axis_count(const ce_session *s) { return static_cast<int64_t>(s->cfg.axes.size()); }

int32_t ce_session_axis_active(const ce_session *s, int64_t index)
{
	if (index < 0 || index >= static_cast<int64_t>(s->axisActive.size())) return 0;
	return s->axisActive[static_cast<size_t>(index)];
}

const char *ce_session_axis_name(const ce_session *s, int64_t index)
{
	if (index < 0 || index >= static_cast<int64_t>(s->cfg.axes.size())) return nullptr;
	return s->cfg.axes[static_cast<size_t>(index)].name.c_str();
}

void ce_session_set_axis(ce_session *s, int32_t index, int32_t value)
{
	if (s->setAxis != nullptr) s->setAxis(index, value);
}

void ce_session_set_button(ce_session *s, int32_t index, int32_t pressed)
{
	if (index >= 0 && static_cast<size_t>(index) < s->btnState.size())
	{
		s->btnState[static_cast<size_t>(index)] = pressed != 0 ? 1 : 0;
	}
}

int32_t ce_session_frame_advance(ce_session *s, uint64_t buttons, int32_t render)
{
	s->wantRendering(render != 0 ? 1 : 0);
	s->frameAdvance(s->sendButtons(s->computeEffective(buttons)));
	if (render != 0) s->copyVideo();
	/* a core that reports its own count may produce a different number every
	 * frame (blip resamplers do); the declared samplesPerFrame is the buffer
	 * we must not overrun */
	int32_t nsamp = s->getAudioSampleCount != nullptr ? s->getAudioSampleCount() : s->cfg.samplesPerFrame;
	if (nsamp < 0) nsamp = 0;
	if (nsamp > s->cfg.samplesPerFrame) nsamp = s->cfg.samplesPerFrame;
	const auto *src = reinterpret_cast<const int16_t *>(s->getAudio());
	if (s->cfg.channels == 2)
	{
		std::memcpy(s->audioBuf.data(), src, static_cast<size_t>(nsamp) * 2 * sizeof(int16_t));
	}
	else
	{
		for (int32_t i = 0; i < nsamp; i++)
		{
			s->audioBuf[static_cast<size_t>(i) * 2] = src[i];
			s->audioBuf[static_cast<size_t>(i) * 2 + 1] = src[i];
		}
	}
	s->sampleCount = nsamp;
	/* The frame is over and the caller draws next: whatever GL context the
	 * bridge borrowed goes back before it does. */
	ce_gl_release();
	const int32_t lag = s->inputWasRead != nullptr && s->inputWasRead() == 0 ? 1 : 0;
	s->trace(lag, render);
	return lag;
}

const uint32_t *ce_session_video(const ce_session *s) { return s->videoBuf.data(); }

int32_t ce_session_video_width(const ce_session *s) { return s->vidW; }
int32_t ce_session_video_height(const ce_session *s) { return s->vidH; }

const int16_t *ce_session_audio(const ce_session *s, int32_t *sample_count)
{
	if (sample_count != nullptr) *sample_count = s->sampleCount;
	return s->audioBuf.data();
}

const uint8_t *ce_session_save_state(ce_session *s, uint64_t *len_out)
{
	s->error.clear();
	s->stateBuf.clear();
	chimera::WbxReturn r{};
	/* no deactivate/activate bracket: the host activates itself for the
	 * duration - bracketing costs four full guest-address-space remaps per
	 * state, which at rewind's one state per frame was a 10x slowdown */
	s->host->wbx_save_state(s->obj, vectorWrite, reinterpret_cast<uintptr_t>(&s->stateBuf), &r);
	if (!r.ok())
	{
		s->error = r.errorMessage;
		return nullptr;
	}
	if (len_out != nullptr) *len_out = s->stateBuf.size();
	if (s->traceEvery > 0)
	{
		fprintf(stderr, "[trace] save state: %zu bytes at frame %lld\n", s->stateBuf.size(), (long long)s->traceFrame);
		fflush(stderr);
	}
	return s->stateBuf.data();
}

int32_t ce_session_load_state(ce_session *s, const uint8_t *data, uint64_t len)
{
	s->error.clear();
	ByteStream stream{ data, len };
	chimera::WbxReturn r{};
	s->host->wbx_load_state(s->obj, streamRead, reinterpret_cast<uintptr_t>(&stream), &r); // see save re: no bracket
	ce_gl_release(); /* a restore can run guest code, and guest code can draw */
	if (s->traceEvery > 0)
	{
		fprintf(stderr, "[trace] load state: %llu bytes, %s, machine now: threads %d running %d digest %016llx\n",
			(unsigned long long)len, r.ok() ? "ok" : r.errorMessage,
			s->traceThreads != nullptr ? s->traceThreads() : -1,
			s->traceRunning != nullptr ? s->traceRunning() : -1,
			(unsigned long long)(s->traceDigest != nullptr ? s->traceDigest() : 0ull));
		fflush(stderr);
	}
	if (!r.ok())
	{
		s->error = r.errorMessage;
		return 1;
	}
	/* a savestate is guest memory, and the guest's wide-input latches are
	 * guest memory too: the load just rewrote what the guest believes is
	 * held, so the delta tracker must forget its history and resend every
	 * button's current state on the next advance */
	std::fill(s->btnSent.begin(), s->btnSent.end(), uint8_t{ 0xFF });
	/* the turbo flag is host policy rather than machine state and belongs in
	 * memory the state does not cover - but a core that kept it in ordinary
	 * memory would have just had it rewritten, so forget what we told it */
	s->renderingSent = -1;

	/* a savestate is guest memory, and the guest's "tracing on" flag is guest
	 * memory too: re-assert the desired flag, and discard whatever lines the
	 * restored buffer holds - they were traced before the load and would
	 * appear out of order */
	if (s->traceSetEnabled != nullptr)
	{
		s->traceSetEnabled(s->traceDesired ? 1 : 0);
		if (s->traceClear != nullptr) s->traceClear();
	}
	return 0;
}

int32_t ce_session_domain_count(const ce_session *s) { return s->mdCount(); }

const char *ce_session_domain_name(const ce_session *s, int32_t index)
{
	return reinterpret_cast<const char *>(s->mdName(index));
}

int64_t ce_session_domain_size(const ce_session *s, int32_t index) { return s->mdSize(index); }

int32_t ce_session_domain_writable(const ce_session *s, int32_t index) { return s->mdWritable(index); }

int64_t ce_session_domain_read(const ce_session *s, int32_t index, int64_t offset, uint8_t *buf, int64_t len)
{
	int64_t size = s->mdSize(index);
	if (offset < 0 || offset >= size || len <= 0) return 0;
	int64_t n = len < size - offset ? len : size - offset;
	const auto *src = reinterpret_cast<const uint8_t *>(s->mdPtr(index));
	if (src == nullptr) return 0;
	std::memcpy(buf, src + offset, static_cast<size_t>(n));
	return n;
}

const char *ce_session_last_error(ce_session *s) { return s->error.c_str(); }

const char *ce_host_build_info(void)
{
	const char *error = nullptr;
	const chimera::HostApi *host = chimera::hostApi(&error);
	return host != nullptr ? host->wbx_build_info() : nullptr;
}

uint64_t ce_session_domain_ptr(const ce_session *s, int32_t index)
{
	return static_cast<uint64_t>(s->mdPtr(index));
}

int32_t ce_session_apply_settings(ce_session *s, const char *overrides_json)
{
	s->error.clear();
	std::string err;
	auto capacity = reinterpret_cast<int32_t (*)()>(s->proc("GetSettingsCapacity", 0, false, err));
	auto buffer = reinterpret_cast<uintptr_t (*)()>(s->proc("GetSettingsBuffer", 0, false, err));
	auto apply = reinterpret_cast<void (*)(int32_t)>(s->proc("PutSettings", 1, false, err));
	if (capacity == nullptr || buffer == nullptr || apply == nullptr) return 1;

	std::string json;
	if (!composeSettings(s->cfg.defaultsJson, overrides_json, json, s->error)) return 2;
	int32_t cap = capacity();
	if (static_cast<int32_t>(json.size()) > cap)
	{
		s->error = s->cfg.coreName + ": settings JSON is " + std::to_string(json.size())
			+ " bytes but the core's buffer holds " + std::to_string(cap);
		return 2;
	}
	uintptr_t dest = buffer();
	if (dest == 0) return 1;
	std::memcpy(reinterpret_cast<void *>(dest), json.data(), json.size());
	apply(static_cast<int32_t>(json.size()));
	return 0;
}

int32_t ce_session_surface_count(const ce_session *s)
{
	return static_cast<int32_t>(s->surfaceNames.size());
}

const char *ce_session_surface_name(const ce_session *s, int32_t index)
{
	if (index < 0 || index >= static_cast<int32_t>(s->surfaceNames.size())) return nullptr;
	return s->surfaceNames[static_cast<size_t>(index)].c_str();
}

int32_t ce_session_surface_width(const ce_session *s, int32_t index)
{
	return index >= 0 && index < static_cast<int32_t>(s->surfaceWidths.size()) ? s->surfaceWidths[static_cast<size_t>(index)] : 0;
}

int32_t ce_session_surface_height(const ce_session *s, int32_t index)
{
	return index >= 0 && index < static_cast<int32_t>(s->surfaceHeights.size()) ? s->surfaceHeights[static_cast<size_t>(index)] : 0;
}

const uint32_t *ce_session_surface_render(ce_session *s, int32_t index)
{
	if (s->renderSurface == nullptr || index < 0 || index >= static_cast<int32_t>(s->surfaceBufs.size())) return nullptr;
	uintptr_t p = s->renderSurface(index);
	if (p == 0) return nullptr;
	auto &buf = s->surfaceBufs[static_cast<size_t>(index)];
	std::memcpy(buf.data(), reinterpret_cast<const void *>(p), buf.size() * sizeof(uint32_t));
	return buf.data();
}

int32_t ce_session_register_count(const ce_session *s)
{
	return static_cast<int32_t>(s->regNames.size());
}

const char *ce_session_register_name(const ce_session *s, int32_t index)
{
	if (index < 0 || index >= static_cast<int32_t>(s->regNames.size())) return nullptr;
	return s->regNames[static_cast<size_t>(index)].c_str();
}

int32_t ce_session_register_bits(const ce_session *s, int32_t index)
{
	return index >= 0 && index < static_cast<int32_t>(s->regBits.size()) ? s->regBits[static_cast<size_t>(index)] : 32;
}

int64_t ce_session_register_value(const ce_session *s, int32_t index)
{
	return s->regValue != nullptr ? s->regValue(index) : 0;
}

int32_t ce_session_register_set(ce_session *s, int32_t index, int64_t value)
{
	if (s->regSet == nullptr) return 1;
	s->regSet(index, value);
	return 0;
}

int32_t ce_session_has_executed_cycles(const ce_session *s) { return s->executedCycles != nullptr ? 1 : 0; }

int64_t ce_session_executed_cycles(const ce_session *s)
{
	return s->executedCycles != nullptr ? s->executedCycles() : 0;
}

int32_t ce_session_bus_count(const ce_session *s) { return static_cast<int32_t>(s->busNames.size()); }

const char *ce_session_bus_name(const ce_session *s, int32_t index)
{
	if (index < 0 || index >= static_cast<int32_t>(s->busNames.size())) return nullptr;
	return s->busNames[static_cast<size_t>(index)].c_str();
}

int64_t ce_session_bus_size(const ce_session *s, int32_t index)
{
	return index >= 0 && index < static_cast<int32_t>(s->busSizes.size()) ? s->busSizes[static_cast<size_t>(index)] : 0;
}

int32_t ce_session_bus_writable(const ce_session *s, int32_t index)
{
	return index >= 0 && index < static_cast<int32_t>(s->busWritables.size()) && s->busWritables[static_cast<size_t>(index)] ? 1 : 0;
}

int32_t ce_session_bus_peek(const ce_session *s, int32_t index, int32_t addr)
{
	return s->busPeek != nullptr ? s->busPeek(index, addr) : 0;
}

void ce_session_bus_poke(ce_session *s, int32_t index, int32_t addr, int32_t value)
{
	if (s->busPoke != nullptr && ce_session_bus_writable(s, index) != 0) s->busPoke(index, addr, value);
}

// ---- savedata export (docs/save-data.md) ----

namespace {

/* relative and clean, or it does not leave the sandbox: no absolute paths,
 * no backslashes, no "." or ".." components, no empty ones */
bool savedataNameClean(const char *name)
{
	if (name == nullptr || name[0] == '\0' || name[0] == '/') return false;
	const char *p = name;
	while (*p != '\0')
	{
		const char *end = p;
		while (*end != '\0' && *end != '/') { if (*end == '\\') return false; end++; }
		size_t n = static_cast<size_t>(end - p);
		if (n == 0) return false;                                   // "//" or trailing '/'
		if (n == 1 && p[0] == '.') return false;
		if (n == 2 && p[0] == '.' && p[1] == '.') return false;
		p = *end == '/' ? end + 1 : end;
	}
	return true;
}

} // namespace

int32_t ce_session_savedata_available(const ce_session *s) { return s->sdCount != nullptr ? 1 : 0; }

int32_t ce_session_savedata_count(ce_session *s)
{
	s->sdNames.clear();
	s->sdSizes.clear();
	s->sdGuestIndex.clear();
	if (s->sdCount == nullptr) return 0;
	int32_t n = s->sdCount();
	for (int32_t i = 0; i < n; i++)
	{
		const auto *name = reinterpret_cast<const char *>(s->sdName(i));
		if (!savedataNameClean(name))
		{
			std::fprintf(stderr, "%s: savedata entry %d has an unclean path%s%s%s - dropped\n",
				s->cfg.coreName.c_str(), i,
				name != nullptr ? " (\"" : "", name != nullptr ? name : "", name != nullptr ? "\")" : "");
			continue;
		}
		int64_t size = s->sdSize(i);
		if (size < 0) size = 0;
		s->sdNames.emplace_back(name);
		s->sdSizes.push_back(size);
		s->sdGuestIndex.push_back(i);
	}
	return static_cast<int32_t>(s->sdNames.size());
}

const char *ce_session_savedata_name(const ce_session *s, int32_t index)
{
	if (index < 0 || static_cast<size_t>(index) >= s->sdNames.size()) return nullptr;
	return s->sdNames[static_cast<size_t>(index)].c_str();
}

int64_t ce_session_savedata_size(const ce_session *s, int32_t index)
{
	if (index < 0 || static_cast<size_t>(index) >= s->sdSizes.size()) return 0;
	return s->sdSizes[static_cast<size_t>(index)];
}

int64_t ce_session_savedata_read(ce_session *s, int32_t index, int64_t offset, uint8_t *buf, int64_t len)
{
	if (index < 0 || static_cast<size_t>(index) >= s->sdGuestIndex.size()) return 0;
	int64_t size = s->sdSizes[static_cast<size_t>(index)];
	if (offset < 0 || offset >= size || len <= 0) return 0;
	int64_t n = len < size - offset ? len : size - offset;
	const int32_t guest = s->sdGuestIndex[static_cast<size_t>(index)];

	/* A file the guest cannot hand over whole is served a window at a time. It
	 * is the same file - same name, same size, same bytes - and this loop is
	 * the only thing that knows the difference. */
	if (s->sdRead != nullptr)
	{
		int64_t done = 0;
		while (done < n)
		{
			const int64_t got = s->sdRead(guest, offset + done, n - done);
			if (got <= 0) break;
			const auto *win = reinterpret_cast<const uint8_t *>(s->sdScratch());
			if (win == nullptr) break;
			std::memcpy(buf + done, win, static_cast<size_t>(got));
			done += got;
		}
		return done;
	}

	/* fetched per call rather than kept: the guest owns the allocation and a
	 * snapshot only promises the file, not the address */
	const auto *src = reinterpret_cast<const uint8_t *>(s->sdBuffer(guest));
	if (src == nullptr) return 0;
	std::memcpy(buf, src + offset, static_cast<size_t>(n));
	return n;
}

int32_t ce_session_trace_available(const ce_session *s) { return s->traceSetEnabled != nullptr ? 1 : 0; }

const char *ce_session_trace_header(const ce_session *s) { return s->traceHeader.c_str(); }

void ce_session_trace_enable(ce_session *s, int32_t on)
{
	s->traceDesired = on != 0;
	if (s->traceSetEnabled != nullptr) s->traceSetEnabled(on != 0 ? 1 : 0);
}

const uint8_t *ce_session_trace_drain(
	ce_session *s, uint64_t *len_out, int32_t *line_count_out, int32_t *overflow_out)
{
	s->traceOut.clear();
	int32_t lines = 0;
	if (s->traceLineCount != nullptr && s->traceBuffer != nullptr)
	{
		int32_t reported = s->traceLineCount();
		if (reported > 0)
		{
			/* one bulk copy of the whole used region beats a call per line when
			 * a frame can produce tens of thousands; cores that don't report the
			 * byte count fall back to walking line by line */
			int32_t used = s->traceUsedBytes != nullptr ? s->traceUsedBytes() : -1;
			const auto *base = reinterpret_cast<const uint8_t *>(s->traceBuffer());
			if (used > 0)
			{
				s->traceOut.assign(base, base + used);
				for (int32_t i = 0; i < used; i++)
				{
					if (s->traceOut[static_cast<size_t>(i)] == 0) lines++;
				}
			}
			else if (base != nullptr)
			{
				const uint8_t *p = base;
				for (int32_t i = 0; i < reported; i++)
				{
					size_t n = std::strlen(reinterpret_cast<const char *>(p));
					if (n == 0) break;
					s->traceOut.insert(s->traceOut.end(), p, p + n + 1);
					p += n + 1;
					lines++;
				}
			}
		}
		int32_t overflow = s->traceOverflow != nullptr && s->traceOverflow() != 0 ? 1 : 0;
		if (overflow_out != nullptr) *overflow_out = overflow;
		if (s->traceClear != nullptr) s->traceClear();
	}
	else if (overflow_out != nullptr)
	{
		*overflow_out = 0;
	}
	if (len_out != nullptr) *len_out = s->traceOut.size();
	if (line_count_out != nullptr) *line_count_out = lines;
	return s->traceOut.data();
}

int32_t ce_session_movie_load(ce_session *s, const ce_movie_log *log)
{
	if (s->movie == nullptr) s->movie = ce_movie_log_new();
	ce_movie_log_clear(s->movie);
	int64_t n = ce_movie_log_count(log);
	for (int64_t i = 0; i < n; i++) ce_movie_log_add(s->movie, ce_movie_log_entry(log, i));
	const char *key = ce_movie_log_key(log);
	ce_movie_log_set_key(s->movie, key);
	s->movieMode = 1;
	return 0;
}

void ce_session_movie_record(ce_session *s, const char *mnemonics)
{
	if (s->movie == nullptr) s->movie = ce_movie_log_new();
	if (mnemonics != nullptr)
	{
		s->mnemonics = mnemonics;
	}
	else
	{
		/* neutral fallback: the button name's first character past any player
		 * prefix - the frontend supplies its real per-system vocabulary */
		s->mnemonics.clear();
		for (const auto &b : s->cfg.buttons)
		{
			std::string bare = b;
			if (chimera::playerNumberOf(bare) != 0) bare.erase(0, bare.find(' ') + 1);
			s->mnemonics.push_back(bare.empty() ? '!' : bare[0]);
		}
	}
	s->movieMode = 2;
}

int32_t ce_session_movie_mode(const ce_session *s) { return s->movieMode; }

int64_t ce_session_movie_length(const ce_session *s)
{
	return s->movie != nullptr ? ce_movie_log_count(s->movie) : 0;
}

int64_t ce_session_frame(const ce_session *s) { return s->frame; }

const ce_movie_log *ce_session_movie_log(const ce_session *s) { return s->movie; }

int32_t ce_session_movie_entry_decode(
	const ce_session *s, const char *entry, uint64_t *buttons_out, int32_t *axes_out)
{
	if (entry == nullptr) return -1;
	std::vector<uint8_t> states;
	std::vector<int32_t> values;
	if (!s->layout.parse(entry, states, values)) return -1;
	if (buttons_out != nullptr)
	{
		uint64_t mask = 0;
		for (size_t i = 0; i < states.size() && i < 64; i++)
		{
			if (states[i] != 0) mask |= 1ull << i;
		}
		*buttons_out = mask;
	}
	if (axes_out != nullptr)
	{
		for (size_t i = 0; i < values.size(); i++) axes_out[i] = values[i];
	}
	return 0;
}

int32_t ce_session_movie_entry_decode_wide(
	const ce_session *s, const char *entry, uint8_t *states_out, int32_t *axes_out)
{
	if (entry == nullptr) return -1;
	std::vector<uint8_t> states;
	std::vector<int32_t> values;
	if (!s->layout.parse(entry, states, values)) return -1;
	if (states_out != nullptr)
	{
		for (size_t i = 0; i < states.size(); i++) states_out[i] = states[i];
	}
	if (axes_out != nullptr)
	{
		for (size_t i = 0; i < values.size(); i++) axes_out[i] = values[i];
	}
	return 0;
}

int32_t ce_session_movie_advance(ce_session *s, uint64_t buttons, const int32_t *axes, int32_t render)
{
	s->error.clear();
	if (s->movieMode == 0 || s->movie == nullptr)
	{
		s->error = "no movie is loaded";
		return -1;
	}

	/* the epoch the coming frame's delta is measured against, marked while the
	 * machine is still where it starts */
	s->greenzoneBeforeAdvance();

	int32_t lag;
	if (s->movieMode == 1 && s->frame < ce_movie_log_count(s->movie))
	{
		std::vector<int32_t> movieAxes;
		const char *entry = ce_movie_log_entry(s->movie, s->frame);
		if (!s->layout.parse(entry, s->movieButtons, movieAxes))
		{
			s->error = std::string("unparseable movie entry at frame ") + std::to_string(s->frame);
			return -1;
		}
		for (size_t i = 0; i < movieAxes.size(); i++)
		{
			if (s->setAxis != nullptr) s->setAxis(static_cast<int32_t>(i), movieAxes[i]);
		}
		lag = s->advanceCore(s->movieButtons.data(), render);
	}
	else
	{
		if (s->movieMode == 1) s->movieMode = 3; // the log ran out: input is the caller's again
		if (s->movieMode == 2 && s->frame < ce_movie_log_count(s->movie))
		{
			/* recording over existing entries IS the rerecord: everything from
			 * here on described a timeline that no longer happens */
			ce_movie_log_truncate(s->movie, s->frame);
		}
		for (size_t i = 0; i < s->cfg.axes.size(); i++)
		{
			int32_t value = axes != nullptr ? axes[i] : s->cfg.axes[i].neutral;
			if (s->setAxis != nullptr) s->setAxis(static_cast<int32_t>(i), value);
		}
		/* the caller's input: the packed mask OR'd with the set_button states,
		 * so a wide controller records exactly what the machine receives */
		const uint8_t *effective = s->computeEffective(buttons);
		if (s->movieMode == 2)
		{
			std::string entry = s->layout.generate(effective, axes, s->mnemonics);
			ce_movie_log_add(s->movie, entry.c_str());
		}
		lag = s->advanceCore(effective, render);
	}
	s->greenzoneCapture();
	return lag;
}

void ce_session_greenzone_enable(ce_session *s, uint64_t budget_bytes)
{
	s->history.configure(s->host, s->obj, budget_bytes);
	if (budget_bytes != 0) s->greenzoneCapture(); // the anchor: the frame we stand on now
}

int64_t ce_session_greenzone_count(const ce_session *s)
{
	return s->history.count();
}

int64_t ce_session_greenzone_nearest(const ce_session *s, int64_t frame)
{
	return s->history.nearest(frame);
}

void ce_session_greenzone_invalidate(ce_session *s, int64_t after_frame)
{
	s->history.invalidateAfter(after_frame);
}

int32_t ce_session_history_save(ce_session *s, const char *path, const char *machine_id)
{
	s->error.clear();
	return s->history.saveTo(path, machine_id, s->error) ? 0 : 1;
}

int32_t ce_session_history_load(ce_session *s, const char *path, const char *machine_id)
{
	s->error.clear();
	return s->history.loadFrom(path, machine_id, s->error) ? 0 : 1;
}

int32_t ce_session_seek(ce_session *s, int64_t frame)
{
	s->error.clear();
	if (s->movieMode == 0 || s->movie == nullptr)
	{
		s->error = "seeking needs a movie";
		return 1;
	}
	if (frame > ce_movie_log_count(s->movie))
	{
		s->error = "cannot seek past the movie's end";
		return 1;
	}

	int64_t base = ce_session_greenzone_nearest(s, frame);
	if (base >= 0 && (base > s->frame || s->frame > frame))
	{
		if (!s->history.restore(base, s->error)) return 1;
		if (s->traceSetEnabled != nullptr)
		{
			s->traceSetEnabled(s->traceDesired ? 1 : 0);
			if (s->traceClear != nullptr) s->traceClear();
		}
		/* same as ce_session_load_state: the restore rewrote the guest's
		 * wide-input latches, so resend every button on the next advance */
		std::fill(s->btnSent.begin(), s->btnSent.end(), uint8_t{ 0xFF });
		s->renderingSent = -1; // see ce_session_load_state
		s->frame = base;
	}
	else if (s->frame > frame)
	{
		s->error = "no stored state at or before the target frame";
		return 1;
	}

	/* replay the log to the target - a seek is a replay, never a guess */
	while (s->frame < frame)
	{
		std::vector<int32_t> movieAxes;
		const char *entry = ce_movie_log_entry(s->movie, s->frame);
		if (entry == nullptr || !s->layout.parse(entry, s->movieButtons, movieAxes))
		{
			s->error = std::string("unparseable movie entry at frame ") + std::to_string(s->frame);
			return 1;
		}
		for (size_t i = 0; i < movieAxes.size(); i++)
		{
			if (s->setAxis != nullptr) s->setAxis(static_cast<int32_t>(i), movieAxes[i]);
		}
		s->greenzoneBeforeAdvance();
		s->advanceCore(s->movieButtons.data(), 0);
		s->greenzoneCapture();
	}
	if (s->movieMode == 3 && s->frame < ce_movie_log_count(s->movie)) s->movieMode = 1;
	return 0;
}

} // extern "C"

/* ---- the compile cache and precompile sessions -------------------------- */

extern "C" uint64_t ce_session_cache_stored(const ce_session *s)
{
	if (s == nullptr || s->fnCacheStored == 0) return 0;
	return reinterpret_cast<uint64_t (*)()>(s->fnCacheStored)();
}

extern "C" uint64_t ce_session_cache_fetched(const ce_session *s)
{
	if (s == nullptr || s->fnCacheFetched == 0) return 0;
	return reinterpret_cast<uint64_t (*)()>(s->fnCacheFetched)();
}

extern "C" int32_t ce_session_precompile_done(const ce_session *s)
{
	if (s == nullptr || !s->precompile || s->fnPrecompileDone == 0) return -1;
	return reinterpret_cast<int32_t (*)()>(s->fnPrecompileDone)() ? 1 : 0;
}

extern "C" int32_t ce_session_precompile_progress(const ce_session *s, uint32_t *done_out, uint32_t *total_out)
{
	if (s == nullptr || !s->precompile || s->fnPrecompileDoneCount == 0 || s->fnPrecompileTotal == 0) return -1;
	const uint32_t done = reinterpret_cast<uint32_t (*)()>(s->fnPrecompileDoneCount)();
	const uint32_t total = reinterpret_cast<uint32_t (*)()>(s->fnPrecompileTotal)();
	if (done_out) *done_out = done;
	if (total_out) *total_out = total;
	/* Said here, on the same stream the object lines use: whoever started this
	 * session is watching it, and a caller's own writes may sit in a buffer
	 * this one does not control. */
	static uint32_t lastDone = UINT32_MAX, lastTotal = UINT32_MAX;
	if (done != lastDone || total != lastTotal)
	{
		lastDone = done;
		lastTotal = total;
		printf("Precompiled %u/%u modules\n", done, total);
		fflush(stdout);
	}
	return 0;
}
