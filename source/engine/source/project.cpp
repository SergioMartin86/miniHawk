/* project.cpp - the .chimeraProject: chimera's entry point and its movie in
 * one file (docs/project.md).
 *
 * A JSON document holding everything required to reproduce the work except
 * the data bytes, which are named by SHA1: identity, the pinned core, the
 * file manifest (canonical names + SHA1 + the core-defined slot each file
 * fills), firmware pins, sync settings, and the TAS work itself - input
 * log, markers, branches. No paths are ever stored; files are resolved per
 * session from wherever the caller says they are, and saving records the
 * ACTUAL hash of every resolved file so an override leaves a truthful
 * record.
 */

#include "chimera/engine.h"

#include "file_io.hpp"
#include "manifest_util.hpp"
#include "conditions.hpp"
#include "sha1.hpp"

#include "../../extern/cjson/cJSON.h"

#include <cstring>
#include <set>
#include <string>
#include <vector>

namespace {

using namespace chimera::manifest;

thread_local std::string g_error;

struct FileEntry
{
	std::string name;       // canonical, bare
	std::string slot;       // core-defined slot id ("support" reserved)
	std::string sha1;       // recorded (40 uppercase hex)
	std::string actualSha1; // "" while unresolved
	int32_t status = 1;     // 0 resolved+match, 1 unresolved, 2 mismatch
	/* Where these bytes were read from, on THIS machine, in THIS run. Never
	 * serialized: the project is distributable and a path is not. It exists so
	 * a frontend can remember locations in a sidecar of its own. */
	std::string sourcePath;
	/* NOT the bytes. A project's files are read where they lie, when the
	 * machine asks for them (see ce_session_open's file_paths): a PS2 disc is
	 * over four gigabytes and holding one here would be four gigabytes nothing
	 * needed, on the way to a sandbox that reads it from the disk anyway. What
	 * a project knows about a file is its name, its hash and where it is. */
	uint64_t size = 0;
};

/* A cue is a few hundred bytes of text naming its tracks, so it IS read - the
 * point of not holding files is the big ones. Empty when it will not read,
 * which the callers all treat as "no references". */
std::vector<uint8_t> readSmall(const std::string &path)
{
	std::vector<uint8_t> out;
	if (!path.empty()) chimera::readFile(path.c_str(), out);
	return out;
}

struct Marker
{
	int64_t frame = 0;
	std::string text;
	bool keepState = true; // the user's keep-a-state-here choice; serialized only when false
};

struct Branch
{
	std::string name;
	int64_t frame = 0;
	std::string time; // carried verbatim (the frontend's timestamp text); "" = none
	std::string log;
	std::vector<Marker> markers;
};

const char *fail(std::string message, const char **error_out)
{
	g_error = std::move(message);
	if (error_out != nullptr) *error_out = g_error.c_str();
	return nullptr;
}

int32_t failInt(std::string message, const char **error_out)
{
	g_error = std::move(message);
	if (error_out != nullptr) *error_out = g_error.c_str();
	return 1;
}

std::string lowerExt(const std::string &name)
{
	size_t dot = name.find_last_of('.');
	if (dot == std::string::npos || dot + 1 == name.size()) return std::string();
	std::string ext = name.substr(dot + 1);
	for (char &c : ext)
	{
		if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
	}
	return ext;
}

} // namespace

struct ce_project
{
	std::string title;
	std::string description;
	std::string coreName, coreVersion, coreSha1;
	uint64_t rerecords = 0;
	cJSON *settings = nullptr; // always an object
	cJSON *firmware = nullptr; // always an array
	cJSON *coreCache = nullptr; // always an array: {name, sha1} per compiled object
	std::string log;           // the ce_movie_log lump (LogKey + entries)
	std::vector<FileEntry> files;
	std::vector<Marker> markers;
	std::vector<Branch> branches;
	std::vector<std::string> subtitles; // verbatim subtitle lines, in order
	// movie metadata this format does not first-class (Author, emulator
	// version, platform facts): ordered key/value pairs, carried verbatim
	std::vector<std::pair<std::string, std::string>> headers;

	// borrowed-buffer returns
	std::string settingsOut, firmwareOut, coreCacheOut, slotsOut;

	ce_project()
	{
		settings = cJSON_CreateObject();
		firmware = cJSON_CreateArray();
		coreCache = cJSON_CreateArray();
	}
	~ce_project()
	{
		cJSON_Delete(settings);
		cJSON_Delete(firmware);
		cJSON_Delete(coreCache);
	}
};

extern "C" {

ce_project *ce_project_new(void) { return new ce_project(); }

void ce_project_free(ce_project *p) { delete p; }

ce_project *ce_project_open(const char *path, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;

	std::vector<uint8_t> raw;
	if (!chimera::readFile(path, raw))
	{
		fail(std::string("cannot read ") + path, error_out);
		return nullptr;
	}
	cJSON *root = cJSON_ParseWithLength(reinterpret_cast<const char *>(raw.data()), raw.size());
	if (root == nullptr)
	{
		fail("the project is not valid JSON", error_out);
		return nullptr;
	}
	if (!cJSON_IsObject(root))
	{
		cJSON_Delete(root);
		fail("the project is not a JSON object", error_out);
		return nullptr;
	}

	auto reject = [&](std::string message) -> ce_project *
	{
		cJSON_Delete(root);
		fail(std::move(message), error_out);
		return nullptr;
	};

	/* the format is strict: a key this build does not know is an error, not
	 * something to drop silently on the next save */
	static const char *known[] = { "title", "description", "core", "rerecords",
		"files", "settings", "firmware", "coreCache", "input", "markers", "branches", "subtitles",
		"headers" };
	for (cJSON *item = root->child; item != nullptr; item = item->next)
	{
		bool ok = false;
		for (const char *k : known)
		{
			if (item->string != nullptr && std::strcmp(item->string, k) == 0) { ok = true; break; }
		}
		if (!ok) return reject(std::string("unknown project key \"") + (item->string != nullptr ? item->string : "") + "\"");
	}

	auto *p = new ce_project();
	auto rejectP = [&](std::string message) -> ce_project *
	{
		delete p;
		return reject(std::move(message));
	};

	cJSON *j;
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "title")) != nullptr)
	{
		if (!cJSON_IsString(j)) return rejectP("\"title\" is not a string");
		p->title = j->valuestring;
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "description")) != nullptr)
	{
		if (!cJSON_IsString(j)) return rejectP("\"description\" is not a string");
		p->description = j->valuestring;
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "core")) != nullptr)
	{
		if (!cJSON_IsObject(j)) return rejectP("\"core\" is not an object");
		cJSON *name = cJSON_GetObjectItemCaseSensitive(j, "name");
		cJSON *version = cJSON_GetObjectItemCaseSensitive(j, "version");
		cJSON *sha1 = cJSON_GetObjectItemCaseSensitive(j, "sha1");
		if (!cJSON_IsString(name) || !cJSON_IsString(version) || !cJSON_IsString(sha1))
		{
			return rejectP("\"core\" needs string \"name\", \"version\" and \"sha1\"");
		}
		p->coreName = name->valuestring;
		p->coreVersion = version->valuestring;
		p->coreSha1 = upperHex(sha1->valuestring);
		if (!p->coreSha1.empty() && !validSha1(p->coreSha1))
		{
			return rejectP("the core's sha1 is malformed");
		}
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "rerecords")) != nullptr)
	{
		if (!cJSON_IsNumber(j) || j->valuedouble < 0) return rejectP("\"rerecords\" is not a count");
		p->rerecords = static_cast<uint64_t>(j->valuedouble);
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "files")) != nullptr)
	{
		if (!cJSON_IsArray(j)) return rejectP("\"files\" is not an array");
		for (cJSON *item = j->child; item != nullptr; item = item->next)
		{
			cJSON *name = cJSON_GetObjectItemCaseSensitive(item, "name");
			cJSON *sha1 = cJSON_GetObjectItemCaseSensitive(item, "sha1");
			cJSON *slot = cJSON_GetObjectItemCaseSensitive(item, "slot");
			if (!cJSON_IsString(name) || !cJSON_IsString(sha1) || !cJSON_IsString(slot))
			{
				return rejectP("every file entry needs string \"name\", \"sha1\" and \"slot\"");
			}
			FileEntry e;
			e.name = name->valuestring;
			e.sha1 = upperHex(sha1->valuestring);
			e.slot = slot->valuestring;
			if (!bareName(e.name)) return rejectP("file name '" + e.name + "' is not a bare name");
			if (!validSha1(e.sha1)) return rejectP("file '" + e.name + "' has a malformed sha1");
			if (!validSlot(e.slot)) return rejectP("file '" + e.name + "' has a malformed slot id '" + e.slot + "'");
			for (const FileEntry &prior : p->files)
			{
				if (prior.name == e.name) return rejectP("file '" + e.name + "' is listed twice");
			}
			p->files.push_back(std::move(e));
		}
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "settings")) != nullptr)
	{
		if (!cJSON_IsObject(j)) return rejectP("\"settings\" is not an object");
		cJSON_Delete(p->settings);
		p->settings = cJSON_Duplicate(j, 1);
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "firmware")) != nullptr)
	{
		if (!cJSON_IsArray(j)) return rejectP("\"firmware\" is not an array");
		cJSON_Delete(p->firmware);
		p->firmware = cJSON_Duplicate(j, 1);
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "coreCache")) != nullptr)
	{
		if (!cJSON_IsArray(j)) return rejectP("\"coreCache\" is not an array");
		cJSON_Delete(p->coreCache);
		p->coreCache = cJSON_Duplicate(j, 1);
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "input")) != nullptr)
	{
		if (!cJSON_IsString(j)) return rejectP("\"input\" is not a string");
		p->log = j->valuestring;
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "markers")) != nullptr)
	{
		if (!cJSON_IsArray(j)) return rejectP("\"markers\" is not an array");
		for (cJSON *item = j->child; item != nullptr; item = item->next)
		{
			cJSON *frame = cJSON_GetObjectItemCaseSensitive(item, "frame");
			cJSON *text = cJSON_GetObjectItemCaseSensitive(item, "text");
			cJSON *keep = cJSON_GetObjectItemCaseSensitive(item, "keepState");
			if (!cJSON_IsNumber(frame) || !cJSON_IsString(text))
			{
				return rejectP("every marker needs number \"frame\" and string \"text\"");
			}
			Marker m;
			m.frame = static_cast<int64_t>(frame->valuedouble);
			m.text = text->valuestring;
			if (keep != nullptr)
			{
				if (!cJSON_IsBool(keep)) return rejectP("a marker's \"keepState\" is not a boolean");
				m.keepState = cJSON_IsTrue(keep);
			}
			p->markers.push_back(std::move(m));
		}
		/* kept sorted by frame; a hand-edited file gets the order restored
		 * (stable, so equal frames keep their relative order) */
		for (size_t i = 1; i < p->markers.size(); i++)
		{
			Marker m = std::move(p->markers[i]);
			size_t at = i;
			while (at > 0 && p->markers[at - 1].frame > m.frame)
			{
				p->markers[at] = std::move(p->markers[at - 1]);
				at--;
			}
			p->markers[at] = std::move(m);
		}
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "branches")) != nullptr)
	{
		if (!cJSON_IsArray(j)) return rejectP("\"branches\" is not an array");
		for (cJSON *item = j->child; item != nullptr; item = item->next)
		{
			cJSON *name = cJSON_GetObjectItemCaseSensitive(item, "name");
			cJSON *frame = cJSON_GetObjectItemCaseSensitive(item, "frame");
			cJSON *input = cJSON_GetObjectItemCaseSensitive(item, "input");
			cJSON *time = cJSON_GetObjectItemCaseSensitive(item, "time");
			cJSON *markers = cJSON_GetObjectItemCaseSensitive(item, "markers");
			if (!cJSON_IsString(name) || !cJSON_IsNumber(frame) || !cJSON_IsString(input))
			{
				return rejectP("every branch needs string \"name\", number \"frame\" and string \"input\"");
			}
			Branch b;
			b.name = name->valuestring;
			b.frame = static_cast<int64_t>(frame->valuedouble);
			b.log = input->valuestring;
			if (time != nullptr)
			{
				if (!cJSON_IsString(time)) return rejectP("a branch's \"time\" is not a string");
				b.time = time->valuestring;
			}
			if (markers != nullptr)
			{
				if (!cJSON_IsArray(markers)) return rejectP("a branch's \"markers\" is not an array");
				for (cJSON *m = markers->child; m != nullptr; m = m->next)
				{
					cJSON *mFrame = cJSON_GetObjectItemCaseSensitive(m, "frame");
					cJSON *mText = cJSON_GetObjectItemCaseSensitive(m, "text");
					if (!cJSON_IsNumber(mFrame) || !cJSON_IsString(mText))
					{
						return rejectP("every branch marker needs number \"frame\" and string \"text\"");
					}
					Marker mk;
					mk.frame = static_cast<int64_t>(mFrame->valuedouble);
					mk.text = mText->valuestring;
					b.markers.push_back(std::move(mk));
				}
			}
			p->branches.push_back(std::move(b));
		}
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "subtitles")) != nullptr)
	{
		if (!cJSON_IsArray(j)) return rejectP("\"subtitles\" is not an array");
		for (cJSON *item = j->child; item != nullptr; item = item->next)
		{
			if (!cJSON_IsString(item)) return rejectP("every subtitle is a string");
			p->subtitles.push_back(item->valuestring);
		}
	}
	if ((j = cJSON_GetObjectItemCaseSensitive(root, "headers")) != nullptr)
	{
		if (!cJSON_IsObject(j)) return rejectP("\"headers\" is not an object");
		for (cJSON *item = j->child; item != nullptr; item = item->next)
		{
			if (!cJSON_IsString(item)) return rejectP("every header value is a string");
			p->headers.emplace_back(item->string != nullptr ? item->string : "", item->valuestring);
		}
	}

	cJSON_Delete(root);
	return p;
}

int32_t ce_project_save(ce_project *p, const char *path, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;

	/* cue closure at save: every file a RESOLVED cue references must be
	 * listed, or unhashed bytes would reach the machine on the next open */
	for (const FileEntry &e : p->files)
	{
		if (!hasCueSuffix(e.name) || e.status == 1) continue;
		for (const std::string &ref : cueReferences(readSmall(e.sourcePath)))
		{
			bool listed = false;
			for (const FileEntry &other : p->files)
			{
				if (other.name == ref) { listed = true; break; }
			}
			if (!listed)
			{
				return failInt("'" + e.name + "' references '" + ref +
					"', which the project does not list - unlisted bytes would reach the machine unhashed", error_out);
			}
		}
	}

	cJSON *root = cJSON_CreateObject();
	cJSON_AddStringToObject(root, "title", p->title.c_str());
	cJSON_AddStringToObject(root, "description", p->description.c_str());
	cJSON *core = cJSON_AddObjectToObject(root, "core");
	cJSON_AddStringToObject(core, "name", p->coreName.c_str());
	cJSON_AddStringToObject(core, "version", p->coreVersion.c_str());
	cJSON_AddStringToObject(core, "sha1", p->coreSha1.c_str());
	cJSON_AddNumberToObject(root, "rerecords", static_cast<double>(p->rerecords));
	cJSON *files = cJSON_AddArrayToObject(root, "files");
	for (const FileEntry &e : p->files)
	{
		cJSON *item = cJSON_CreateObject();
		cJSON_AddStringToObject(item, "name", e.name.c_str());
		/* a resolved file's ACTUAL hash: proceeding past a mismatch is
		 * allowed, but the project then records what actually ran */
		cJSON_AddStringToObject(item, "sha1", e.status == 1 ? e.sha1.c_str() : e.actualSha1.c_str());
		cJSON_AddStringToObject(item, "slot", e.slot.c_str());
		cJSON_AddItemToArray(files, item);
	}
	cJSON_AddItemToObject(root, "settings", cJSON_Duplicate(p->settings, 1));
	cJSON_AddItemToObject(root, "firmware", cJSON_Duplicate(p->firmware, 1));
	/* What the core compiled for this game, by name and hash: regenerable from
	 * the same inputs, recorded so a later run can be told it is running the
	 * same compiled code (docs/compile-cache.md). */
	cJSON_AddItemToObject(root, "coreCache", cJSON_Duplicate(p->coreCache, 1));
	cJSON_AddStringToObject(root, "input", p->log.c_str());
	cJSON *markers = cJSON_AddArrayToObject(root, "markers");
	for (const Marker &m : p->markers)
	{
		cJSON *item = cJSON_CreateObject();
		cJSON_AddNumberToObject(item, "frame", static_cast<double>(m.frame));
		cJSON_AddStringToObject(item, "text", m.text.c_str());
		if (!m.keepState) cJSON_AddBoolToObject(item, "keepState", 0);
		cJSON_AddItemToArray(markers, item);
	}
	cJSON *branches = cJSON_AddArrayToObject(root, "branches");
	for (const Branch &b : p->branches)
	{
		cJSON *item = cJSON_CreateObject();
		cJSON_AddStringToObject(item, "name", b.name.c_str());
		cJSON_AddNumberToObject(item, "frame", static_cast<double>(b.frame));
		if (!b.time.empty()) cJSON_AddStringToObject(item, "time", b.time.c_str());
		cJSON_AddStringToObject(item, "input", b.log.c_str());
		if (!b.markers.empty())
		{
			cJSON *bm = cJSON_AddArrayToObject(item, "markers");
			for (const Marker &m : b.markers)
			{
				cJSON *mi = cJSON_CreateObject();
				cJSON_AddNumberToObject(mi, "frame", static_cast<double>(m.frame));
				cJSON_AddStringToObject(mi, "text", m.text.c_str());
				cJSON_AddItemToArray(bm, mi);
			}
		}
		cJSON_AddItemToArray(branches, item);
	}
	if (!p->subtitles.empty())
	{
		cJSON *subs = cJSON_AddArrayToObject(root, "subtitles");
		for (const std::string &s : p->subtitles)
		{
			cJSON_AddItemToArray(subs, cJSON_CreateString(s.c_str()));
		}
	}
	if (!p->headers.empty())
	{
		cJSON *hdrs = cJSON_AddObjectToObject(root, "headers");
		for (const auto &kv : p->headers)
		{
			cJSON_AddStringToObject(hdrs, kv.first.c_str(), kv.second.c_str());
		}
	}

	char *text = cJSON_Print(root);
	cJSON_Delete(root);
	if (text == nullptr) return failInt("could not serialize the project", error_out);
	std::string out = text;
	cJSON_free(text);
	out += "\n";

	if (!chimera::writeFile(path, reinterpret_cast<const uint8_t *>(out.data()), out.size()))
	{
		return failInt(std::string("cannot write ") + path, error_out);
	}
	return 0;
}

/* ---- identity ---- */

const char *ce_project_title(const ce_project *p) { return p->title.c_str(); }
void ce_project_set_title(ce_project *p, const char *title) { p->title = title != nullptr ? title : ""; }
const char *ce_project_description(const ce_project *p) { return p->description.c_str(); }
void ce_project_set_description(ce_project *p, const char *description) { p->description = description != nullptr ? description : ""; }

const char *ce_project_core_name(const ce_project *p) { return p->coreName.c_str(); }
const char *ce_project_core_version(const ce_project *p) { return p->coreVersion.c_str(); }
const char *ce_project_core_sha1(const ce_project *p) { return p->coreSha1.c_str(); }
void ce_project_set_core(ce_project *p, const char *name, const char *version, const char *sha1)
{
	p->coreName = name != nullptr ? name : "";
	p->coreVersion = version != nullptr ? version : "";
	p->coreSha1 = upperHex(sha1 != nullptr ? sha1 : "");
}

uint64_t ce_project_rerecords(const ce_project *p) { return p->rerecords; }
void ce_project_set_rerecords(ce_project *p, uint64_t count) { p->rerecords = count; }

/* ---- lumps ---- */

const char *ce_project_settings_text(ce_project *p, uint64_t *len_out)
{
	char *text = cJSON_PrintUnformatted(p->settings);
	p->settingsOut = text != nullptr ? text : "{}";
	if (text != nullptr) cJSON_free(text);
	if (len_out != nullptr) *len_out = p->settingsOut.size();
	return p->settingsOut.c_str();
}

int32_t ce_project_set_settings_text(ce_project *p, const char *json, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	cJSON *parsed = cJSON_Parse(json != nullptr ? json : "");
	if (parsed == nullptr || !cJSON_IsObject(parsed))
	{
		cJSON_Delete(parsed);
		return failInt("settings must be a JSON object", error_out);
	}
	cJSON_Delete(p->settings);
	p->settings = parsed;
	return 0;
}

const char *ce_project_core_cache_text(ce_project *p, uint64_t *len_out)
{
	char *text = cJSON_PrintUnformatted(p->coreCache);
	p->coreCacheOut = text != nullptr ? text : "[]";
	if (text != nullptr) cJSON_free(text);
	if (len_out != nullptr) *len_out = p->coreCacheOut.size();
	return p->coreCacheOut.c_str();
}

int32_t ce_project_set_core_cache_text(ce_project *p, const char *json, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	cJSON *parsed = cJSON_Parse(json != nullptr ? json : "");
	if (parsed == nullptr || !cJSON_IsArray(parsed))
	{
		cJSON_Delete(parsed);
		return failInt("coreCache must be a JSON array", error_out);
	}
	cJSON_Delete(p->coreCache);
	p->coreCache = parsed;
	return 0;
}

const char *ce_project_firmware_text(ce_project *p, uint64_t *len_out)
{
	char *text = cJSON_PrintUnformatted(p->firmware);
	p->firmwareOut = text != nullptr ? text : "[]";
	if (text != nullptr) cJSON_free(text);
	if (len_out != nullptr) *len_out = p->firmwareOut.size();
	return p->firmwareOut.c_str();
}

int32_t ce_project_set_firmware_text(ce_project *p, const char *json, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	cJSON *parsed = cJSON_Parse(json != nullptr ? json : "");
	if (parsed == nullptr || !cJSON_IsArray(parsed))
	{
		cJSON_Delete(parsed);
		return failInt("firmware must be a JSON array", error_out);
	}
	cJSON_Delete(p->firmware);
	p->firmware = parsed;
	return 0;
}

const char *ce_project_log_text(const ce_project *p, uint64_t *len_out)
{
	if (len_out != nullptr) *len_out = p->log.size();
	return p->log.c_str();
}

void ce_project_set_log_text(ce_project *p, const char *text, uint64_t len)
{
	if (text == nullptr) p->log.clear();
	else p->log.assign(text, static_cast<size_t>(len));
}

/* ---- markers ---- */

int32_t ce_project_marker_count(const ce_project *p)
{
	return static_cast<int32_t>(p->markers.size());
}

int64_t ce_project_marker_frame(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_marker_count(p)) return -1;
	return p->markers[index].frame;
}

const char *ce_project_marker_text(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_marker_count(p)) return nullptr;
	return p->markers[index].text.c_str();
}

int32_t ce_project_marker_add(ce_project *p, int64_t frame, const char *text, int32_t keep_state)
{
	Marker m;
	m.frame = frame;
	m.text = text != nullptr ? text : "";
	m.keepState = keep_state != 0;
	size_t at = p->markers.size();
	while (at > 0 && p->markers[at - 1].frame > frame) at--;
	p->markers.insert(p->markers.begin() + at, std::move(m));
	return static_cast<int32_t>(at);
}

int32_t ce_project_marker_keep_state(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_marker_count(p)) return 1;
	return p->markers[index].keepState ? 1 : 0;
}

void ce_project_marker_remove(ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_marker_count(p)) return;
	p->markers.erase(p->markers.begin() + index);
}

/* ---- branches ---- */

int32_t ce_project_branch_count(const ce_project *p)
{
	return static_cast<int32_t>(p->branches.size());
}

const char *ce_project_branch_name(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_count(p)) return nullptr;
	return p->branches[index].name.c_str();
}

int64_t ce_project_branch_frame(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_count(p)) return -1;
	return p->branches[index].frame;
}

const char *ce_project_branch_log_text(const ce_project *p, int32_t index, uint64_t *len_out)
{
	if (len_out != nullptr) *len_out = 0;
	if (index < 0 || index >= ce_project_branch_count(p)) return nullptr;
	if (len_out != nullptr) *len_out = p->branches[index].log.size();
	return p->branches[index].log.c_str();
}

const char *ce_project_branch_time(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_count(p)) return nullptr;
	return p->branches[index].time.c_str();
}

void ce_project_branch_add(ce_project *p, const char *name, int64_t frame, const char *time, const char *log_text, uint64_t len)
{
	Branch b;
	b.name = name != nullptr ? name : "";
	b.frame = frame;
	b.time = time != nullptr ? time : "";
	if (log_text != nullptr) b.log.assign(log_text, static_cast<size_t>(len));
	p->branches.push_back(std::move(b));
}

void ce_project_branch_remove(ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_count(p)) return;
	p->branches.erase(p->branches.begin() + index);
}

int32_t ce_project_branch_marker_count(const ce_project *p, int32_t branch)
{
	if (branch < 0 || branch >= ce_project_branch_count(p)) return 0;
	return static_cast<int32_t>(p->branches[branch].markers.size());
}

int64_t ce_project_branch_marker_frame(const ce_project *p, int32_t branch, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_marker_count(p, branch)) return -1;
	return p->branches[branch].markers[index].frame;
}

const char *ce_project_branch_marker_text(const ce_project *p, int32_t branch, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_marker_count(p, branch)) return nullptr;
	return p->branches[branch].markers[index].text.c_str();
}

void ce_project_branch_marker_add(ce_project *p, int32_t branch, int64_t frame, const char *text, int32_t keep_state)
{
	if (branch < 0 || branch >= ce_project_branch_count(p)) return;
	Marker m;
	m.frame = frame;
	m.text = text != nullptr ? text : "";
	m.keepState = keep_state != 0;
	p->branches[branch].markers.push_back(std::move(m));
}

int32_t ce_project_branch_marker_keep_state(const ce_project *p, int32_t branch, int32_t index)
{
	if (index < 0 || index >= ce_project_branch_marker_count(p, branch)) return 1;
	return p->branches[branch].markers[index].keepState ? 1 : 0;
}

/* ---- the headers map: ordered, verbatim ---- */

int32_t ce_project_header_count(const ce_project *p)
{
	return static_cast<int32_t>(p->headers.size());
}

const char *ce_project_header_key_at(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_header_count(p)) return nullptr;
	return p->headers[index].first.c_str();
}

const char *ce_project_header_value_at(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_header_count(p)) return nullptr;
	return p->headers[index].second.c_str();
}

const char *ce_project_header_get(const ce_project *p, const char *key)
{
	if (key == nullptr) return nullptr;
	for (const auto &kv : p->headers)
	{
		if (kv.first == key) return kv.second.c_str();
	}
	return nullptr;
}

/* value NULL removes the key; a new key appends, keeping order */
void ce_project_header_set(ce_project *p, const char *key, const char *value)
{
	if (key == nullptr) return;
	for (size_t i = 0; i < p->headers.size(); i++)
	{
		if (p->headers[i].first != key) continue;
		if (value == nullptr) p->headers.erase(p->headers.begin() + static_cast<long>(i));
		else p->headers[i].second = value;
		return;
	}
	if (value != nullptr) p->headers.emplace_back(key, value);
}

/* ---- subtitles: verbatim lines, in order ---- */

int32_t ce_project_subtitle_count(const ce_project *p)
{
	return static_cast<int32_t>(p->subtitles.size());
}

const char *ce_project_subtitle_at(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_subtitle_count(p)) return nullptr;
	return p->subtitles[index].c_str();
}

void ce_project_subtitle_add(ce_project *p, const char *line)
{
	p->subtitles.push_back(line != nullptr ? line : "");
}

void ce_project_subtitle_remove(ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_subtitle_count(p)) return;
	p->subtitles.erase(p->subtitles.begin() + index);
}

/* ---- files ---- */

const char *ce_cue_references(const char *cue_bytes, uint64_t cue_len, uint64_t *len_out)
{
	static thread_local std::string g_cueRefs;
	cJSON *arr = cJSON_CreateArray();
	if (cue_bytes != nullptr && cue_len > 0)
	{
		std::vector<uint8_t> bytes(cue_bytes, cue_bytes + cue_len);
		for (const std::string &ref : cueReferences(bytes))
		{
			cJSON_AddItemToArray(arr, cJSON_CreateString(ref.c_str()));
		}
	}
	char *printed = cJSON_PrintUnformatted(arr);
	g_cueRefs = printed != nullptr ? printed : "[]";
	cJSON_free(printed);
	cJSON_Delete(arr);
	if (len_out != nullptr) *len_out = g_cueRefs.size();
	return g_cueRefs.c_str();
}

int32_t ce_project_file_add(ce_project *p, const char *name, const char *slot, const char *source_path, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	FileEntry e;
	e.name = name != nullptr ? name : "";
	e.slot = slot != nullptr ? slot : "";
	if (!bareName(e.name)) return failInt("file name '" + e.name + "' is not a bare name", error_out);
	if (!validSlot(e.slot)) return failInt("'" + e.name + "' has a malformed slot id '" + e.slot + "'", error_out);
	for (const FileEntry &prior : p->files)
	{
		if (prior.name == e.name) return failInt("file '" + e.name + "' is already listed", error_out);
	}
	if (source_path == nullptr || !chimera::sha1HexOfFile(source_path, &e.size, e.actualSha1))
	{
		return failInt("cannot read '" + e.name + "' from " +
			(source_path != nullptr ? source_path : "(null)"), error_out);
	}
	e.sha1 = e.actualSha1;
	e.status = 0;
	e.sourcePath = source_path;
	std::string folder = folderOf(source_path);
	std::string cueName = e.name;
	bool isCue = hasCueSuffix(e.name);
	std::vector<std::string> refs = isCue ? cueReferences(readSmall(source_path)) : std::vector<std::string>();
	size_t before = p->files.size();
	p->files.push_back(std::move(e));

	/* a cue's referenced files join the project automatically, from the
	 * cue's own folder - closure holds from the moment of creation. Any
	 * failure removes the cue and everything it brought: all or nothing. */
	auto failCue = [&](std::string message) -> int32_t
	{
		p->files.resize(before);
		return failInt(std::move(message), error_out);
	};
	for (const std::string &ref : refs)
	{
		bool listed = false;
		for (const FileEntry &other : p->files)
		{
			if (other.name == ref) { listed = true; break; }
		}
		if (listed) continue;
		if (!bareName(ref))
		{
			return failCue("'" + cueName + "': the cue references '" + ref + "', which is not a bare name");
		}
		FileEntry s;
		s.name = ref;
		s.slot = "support";
		if (!chimera::sha1HexOfFile((folder + ref).c_str(), &s.size, s.actualSha1))
		{
			return failCue("'" + cueName + "' references '" + ref + "', which is not next to it - a cue's files are added together");
		}
		s.sha1 = s.actualSha1;
		s.status = 0;
		s.sourcePath = folder + ref;
		p->files.push_back(std::move(s));
	}
	return 0;
}

void ce_project_file_remove(ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return;
	p->files.erase(p->files.begin() + index);
}

int32_t ce_project_file_count(const ce_project *p)
{
	return static_cast<int32_t>(p->files.size());
}

const char *ce_project_file_name(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return nullptr;
	return p->files[index].name.c_str();
}

const char *ce_project_file_slot(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return nullptr;
	return p->files[index].slot.c_str();
}

const char *ce_project_file_sha1(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return nullptr;
	return p->files[index].sha1.c_str();
}

const char *ce_project_file_actual_sha1(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return nullptr;
	return p->files[index].actualSha1.c_str();
}

int32_t ce_project_file_status(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return 1;
	return p->files[index].status;
}

const char *ce_project_file_source_path(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return nullptr;
	return p->files[index].sourcePath.c_str();
}

uint64_t ce_project_file_size(const ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return 0;
	const FileEntry &e = p->files[index];
	return e.status == 1 ? 0 : e.size;
}

int32_t ce_project_file_resolve(ce_project *p, int32_t index, const char *path, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	if (index < 0 || index >= ce_project_file_count(p))
	{
		return failInt("no such file entry", error_out);
	}
	FileEntry &e = p->files[index];
	if (path == nullptr || !chimera::sha1HexOfFile(path, &e.size, e.actualSha1))
	{
		return failInt("cannot read '" + e.name + "' from " + (path != nullptr ? path : "(null)"), error_out);
	}
	e.status = e.actualSha1 == e.sha1 ? 0 : 2;
	e.sourcePath = path;
	return 0;
}

void ce_project_file_unresolve(ce_project *p, int32_t index)
{
	if (index < 0 || index >= ce_project_file_count(p)) return;
	FileEntry &e = p->files[index];
	e.size = 0;
	e.actualSha1.clear();
	e.sourcePath.clear();
	e.status = 1;
}

int32_t ce_project_resolve_dir(ce_project *p, const char *dir)
{
	if (dir == nullptr) return 0;
	std::string base = dir;
	if (!base.empty() && base.back() != '/' && base.back() != '\\') base += '/';
	int32_t resolved = 0;
	for (FileEntry &e : p->files)
	{
		if (e.status != 1) continue;
		const std::string path = base + e.name;
		if (!chimera::sha1HexOfFile(path.c_str(), &e.size, e.actualSha1)) continue;
		e.status = e.actualSha1 == e.sha1 ? 0 : 2;
		e.sourcePath = path;
		resolved++;
	}
	return resolved;
}

int32_t ce_project_files_ok(const ce_project *p)
{
	for (const FileEntry &e : p->files)
	{
		if (e.status != 0) return 0;
	}
	return 1;
}

/* ---- validation against a core's file_slots.json ---- */

int32_t ce_project_validate(const ce_project *p, const char *slots_json, uint64_t slots_len, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	if (slots_json == nullptr) return failInt("no slot declaration given", error_out);

	cJSON *root = cJSON_ParseWithLength(slots_json, static_cast<size_t>(slots_len));
	if (root == nullptr) return failInt("the slot declaration is not valid JSON", error_out);
	cJSON *slots = cJSON_GetObjectItemCaseSensitive(root, "slots");
	if (!cJSON_IsArray(slots))
	{
		cJSON_Delete(root);
		return failInt("the slot declaration has no \"slots\" array", error_out);
	}

	struct Decl
	{
		std::string id;
		int32_t min = 0, max = -1;
		std::vector<std::string> formats;
	};
	std::vector<Decl> decls;
	for (cJSON *item = slots->child; item != nullptr; item = item->next)
	{
		cJSON *id = cJSON_GetObjectItemCaseSensitive(item, "id");
		if (!cJSON_IsString(id))
		{
			cJSON_Delete(root);
			return failInt("every declared slot needs a string \"id\"", error_out);
		}
		Decl d;
		d.id = id->valuestring;
		cJSON *mn = cJSON_GetObjectItemCaseSensitive(item, "min");
		cJSON *mx = cJSON_GetObjectItemCaseSensitive(item, "max");
		if (cJSON_IsNumber(mn)) d.min = static_cast<int32_t>(mn->valuedouble);
		if (cJSON_IsNumber(mx)) d.max = static_cast<int32_t>(mx->valuedouble);
		cJSON *formats = cJSON_GetObjectItemCaseSensitive(item, "formats");
		if (cJSON_IsArray(formats))
		{
			for (cJSON *f = formats->child; f != nullptr; f = f->next)
			{
				if (cJSON_IsString(f)) d.formats.push_back(f->valuestring);
			}
		}
		decls.push_back(std::move(d));
	}

	auto failV = [&](std::string message) -> int32_t
	{
		cJSON_Delete(root);
		return failInt(std::move(message), error_out);
	};

	/* every file fills a declared slot (support is the engine's own) */
	for (const FileEntry &e : p->files)
	{
		if (e.slot == "support") continue;
		const Decl *found = nullptr;
		for (const Decl &d : decls)
		{
			if (d.id == e.slot) { found = &d; break; }
		}
		if (found == nullptr) return failV("file '" + e.name + "' fills slot '" + e.slot + "', which this core does not declare");
		if (!found->formats.empty())
		{
			std::string ext = lowerExt(e.name);
			bool ok = false;
			for (const std::string &f : found->formats)
			{
				if (f == ext) { ok = true; break; }
			}
			if (!ok) return failV("file '" + e.name + "' is not a format slot '" + e.slot + "' accepts");
		}
	}

	/* a slot may be gated by the OTHER slots' contents (exposedWhen, the
	 * same condition language as firmware): files sitting in a slot the
	 * manifest itself makes unavailable are structurally invalid - a
	 * Famicom disk rules out a cartridge and vice versa. Exposure is
	 * decided FIRST because an unexposed slot's minimum does not bind:
	 * "cart min 1 unless fds" and "fds min 1 unless cart" together mean
	 * exactly one of either. */
	std::set<std::string> unexposed;
	{
		cJSON *slotMap = cJSON_CreateObject();
		for (const FileEntry &e : p->files)
		{
			if (e.slot == "support") continue;
			cJSON *arr = cJSON_GetObjectItemCaseSensitive(slotMap, e.slot.c_str());
			if (arr == nullptr) arr = cJSON_AddArrayToObject(slotMap, e.slot.c_str());
			cJSON_AddItemToArray(arr, cJSON_CreateString(e.name.c_str()));
		}
		for (cJSON *item = slots->child; item != nullptr; item = item->next)
		{
			cJSON *id = cJSON_GetObjectItemCaseSensitive(item, "id");
			cJSON *when = cJSON_GetObjectItemCaseSensitive(item, "exposedWhen");
			if (!cJSON_IsString(id) || when == nullptr) continue;
			/* the project's own settings: a slot gated on the machine setting
			 * (gpgx's per-machine cartridges) is exposed by what the project
			 * records, not evaluated blind */
			if (ceEvalCondition(when, slotMap, p->settings)) continue;
			unexposed.insert(id->valuestring);
			for (const FileEntry &e : p->files)
			{
				if (e.slot != id->valuestring) continue;
				std::string bad = e.name;
				std::string slotName = id->valuestring;
				cJSON_Delete(slotMap);
				return failV("file '" + bad + "' sits in slot '" + slotName
					+ "', which these files make unavailable");
			}
		}
		cJSON_Delete(slotMap);
	}

	/* cardinality per declared slot; an unexposed slot's minimum does not
	 * bind (it cannot be filled) */
	for (const Decl &d : decls)
	{
		int32_t count = 0;
		for (const FileEntry &e : p->files)
		{
			if (e.slot == d.id) count++;
		}
		if (count < d.min && unexposed.find(d.id) == unexposed.end())
		{
			return failV("slot '" + d.id + "' needs at least " + std::to_string(d.min) +
				" file(s), " + std::to_string(count) + " given");
		}
		if (d.max >= 0 && count > d.max)
		{
			return failV("slot '" + d.id + "' takes at most " + std::to_string(d.max) +
				" file(s), " + std::to_string(count) + " given");
		}
	}

	/* the declaration's cross-slot requirement: at least one file across
	 * each listed group */
	cJSON *groups = cJSON_GetObjectItemCaseSensitive(root, "atLeastOneOf");
	if (cJSON_IsArray(groups))
	{
		for (cJSON *group = groups->child; group != nullptr; group = group->next)
		{
			if (!cJSON_IsArray(group)) continue;
			int32_t count = 0;
			std::string names;
			for (cJSON *id = group->child; id != nullptr; id = id->next)
			{
				if (!cJSON_IsString(id)) continue;
				if (!names.empty()) names += ", ";
				names += id->valuestring;
				for (const FileEntry &e : p->files)
				{
					if (e.slot == id->valuestring) count++;
				}
			}
			if (count == 0) return failV("at least one file is needed across: " + names);
		}
	}

	cJSON_Delete(root);
	return 0;
}

const char *ce_project_slots_text(ce_project *p, uint64_t *len_out)
{
	cJSON *root = cJSON_CreateObject();
	for (const FileEntry &e : p->files)
	{
		if (e.slot == "support") continue;
		cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, e.slot.c_str());
		if (arr == nullptr) arr = cJSON_AddArrayToObject(root, e.slot.c_str());
		cJSON_AddItemToArray(arr, cJSON_CreateString(e.name.c_str()));
	}
	char *text = cJSON_PrintUnformatted(root);
	cJSON_Delete(root);
	p->slotsOut = text != nullptr ? text : "{}";
	if (text != nullptr) cJSON_free(text);
	if (len_out != nullptr) *len_out = p->slotsOut.size();
	return p->slotsOut.c_str();
}

} // extern "C"
