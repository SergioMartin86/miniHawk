/* multifile.cpp - the .chimeraMultiFile descriptor: what a multi-file game
 * is made of, verified byte for byte.
 *
 * The descriptor is a manifest, not a container: bare file names resolved in
 * its own folder, each with the SHA1 recorded at creation, in an order that
 * matters (the images' order IS the swap order). The engine owns the whole
 * format - the rules, the hashing, the canonical movie line - so the C# side
 * keeps only the dialog and the mounts.
 */

#include "chimera/engine.h"

#include "file_io.hpp"
#include "manifest_util.hpp"
#include "sha1.hpp"

#include "../../extern/cjson/cJSON.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

using namespace chimera::manifest;

thread_local std::string g_error;

struct Entry
{
	std::string name;
	std::string role;       // "image" / "support" / "savedata"
	std::string sha1;       // declared (40 uppercase hex)
	std::string actualSha1; // "" while missing
	int32_t status = 1;     // 0 ok, 1 missing, 2 mismatch
	std::vector<uint8_t> data;
};

bool validRole(const std::string &r)
{
	return r == "image" || r == "support" || r == "savedata";
}

/* Structural validation shared by open and save: rules that make a
 * descriptor a descriptor, independent of what is on disk. */
bool checkStructure(const std::vector<Entry> &entries, std::string &err)
{
	if (entries.empty()) { err = "the descriptor lists no files"; return false; }
	int images = 0, savedata = 0;
	for (size_t i = 0; i < entries.size(); i++)
	{
		const Entry &e = entries[i];
		if (!bareName(e.name))
		{
			err = "file name '" + e.name + "' is not a bare name (files live in the descriptor's folder)";
			return false;
		}
		if (!validRole(e.role))
		{
			err = "file '" + e.name + "' has unknown role '" + e.role + "'";
			return false;
		}
		if (e.role == "image") images++;
		if (e.role == "savedata") savedata++;
		for (size_t j = 0; j < i; j++)
		{
			if (entries[j].name == e.name)
			{
				err = "file '" + e.name + "' is listed twice";
				return false;
			}
		}
	}
	if (images == 0) { err = "the descriptor lists no image to load"; return false; }
	if (savedata > 1) { err = "the descriptor lists more than one savedata file"; return false; }
	return true;
}

/* every file a LOADED cue references must itself be listed */
bool checkCueClosure(const std::vector<Entry> &entries, std::string &err)
{
	for (const Entry &e : entries)
	{
		if (!hasCueSuffix(e.name) || e.data.empty()) continue;
		for (const std::string &ref : cueReferences(e.data))
		{
			bool listed = false;
			for (const Entry &other : entries)
			{
				if (other.name == ref) { listed = true; break; }
			}
			if (!listed)
			{
				err = "'" + e.name + "' references '" + ref +
					"', which the descriptor does not list - unlisted bytes would reach the machine unhashed";
				return false;
			}
		}
	}
	return true;
}

} // namespace

struct ce_multifile
{
	std::vector<Entry> entries;
	std::vector<int32_t> imageIndices;
	int32_t savedataIndex = -1;
	std::string recordLine;
};

extern "C" {

ce_multifile *ce_multifile_open(const char *descriptor_path, const char **error_out)
{
	auto fail = [&](std::string message) -> ce_multifile *
	{
		g_error = std::move(message);
		if (error_out != nullptr) *error_out = g_error.c_str();
		return nullptr;
	};
	if (error_out != nullptr) *error_out = nullptr;

	std::vector<uint8_t> raw;
	if (!chimera::readFile(descriptor_path, raw))
	{
		return fail(std::string("cannot read ") + descriptor_path);
	}

	cJSON *root = cJSON_ParseWithLength(reinterpret_cast<const char *>(raw.data()), raw.size());
	if (root == nullptr) return fail("the descriptor is not valid JSON");
	cJSON *files = cJSON_GetObjectItemCaseSensitive(root, "files");
	if (!cJSON_IsArray(files))
	{
		cJSON_Delete(root);
		return fail("the descriptor has no \"files\" array");
	}

	std::vector<Entry> entries;
	for (cJSON *item = files->child; item != nullptr; item = item->next)
	{
		cJSON *name = cJSON_GetObjectItemCaseSensitive(item, "name");
		cJSON *sha1 = cJSON_GetObjectItemCaseSensitive(item, "sha1");
		cJSON *role = cJSON_GetObjectItemCaseSensitive(item, "role");
		if (!cJSON_IsString(name) || !cJSON_IsString(sha1) || !cJSON_IsString(role))
		{
			cJSON_Delete(root);
			return fail("every file entry needs string \"name\", \"sha1\" and \"role\"");
		}
		Entry e;
		e.name = name->valuestring;
		e.sha1 = upperHex(sha1->valuestring);
		e.role = role->valuestring;
		if (!validSha1(e.sha1))
		{
			std::string bad = e.name;
			cJSON_Delete(root);
			return fail("file '" + bad + "' has a malformed sha1");
		}
		entries.push_back(std::move(e));
	}
	cJSON_Delete(root);

	std::string err;
	if (!checkStructure(entries, err)) return fail(std::move(err));

	std::string folder = folderOf(descriptor_path);
	for (Entry &e : entries)
	{
		if (!chimera::readFile((folder + e.name).c_str(), e.data))
		{
			e.status = 1; // missing; the caller sees it per file
			continue;
		}
		hashInto(e.data, e.actualSha1);
		e.status = e.actualSha1 == e.sha1 ? 0 : 2;
	}

	if (!checkCueClosure(entries, err)) return fail(std::move(err));

	auto *m = new ce_multifile();
	m->entries = std::move(entries);
	for (size_t i = 0; i < m->entries.size(); i++)
	{
		if (m->entries[i].role == "image") m->imageIndices.push_back(static_cast<int32_t>(i));
		if (m->entries[i].role == "savedata") m->savedataIndex = static_cast<int32_t>(i);
	}
	return m;
}

void ce_multifile_free(ce_multifile *m) { delete m; }

int32_t ce_multifile_count(const ce_multifile *m)
{
	return static_cast<int32_t>(m->entries.size());
}

const char *ce_multifile_name(const ce_multifile *m, int32_t index)
{
	if (index < 0 || index >= ce_multifile_count(m)) return nullptr;
	return m->entries[index].name.c_str();
}

const char *ce_multifile_role(const ce_multifile *m, int32_t index)
{
	if (index < 0 || index >= ce_multifile_count(m)) return nullptr;
	return m->entries[index].role.c_str();
}

const char *ce_multifile_sha1(const ce_multifile *m, int32_t index)
{
	if (index < 0 || index >= ce_multifile_count(m)) return nullptr;
	return m->entries[index].sha1.c_str();
}

const char *ce_multifile_actual_sha1(const ce_multifile *m, int32_t index)
{
	if (index < 0 || index >= ce_multifile_count(m)) return nullptr;
	return m->entries[index].actualSha1.c_str();
}

int32_t ce_multifile_status(const ce_multifile *m, int32_t index)
{
	if (index < 0 || index >= ce_multifile_count(m)) return 1;
	return m->entries[index].status;
}

int32_t ce_multifile_ok(const ce_multifile *m)
{
	for (const Entry &e : m->entries)
	{
		if (e.status != 0) return 0;
	}
	return 1;
}

const uint8_t *ce_multifile_data(const ce_multifile *m, int32_t index, uint64_t *len_out)
{
	if (len_out != nullptr) *len_out = 0;
	if (index < 0 || index >= ce_multifile_count(m)) return nullptr;
	const Entry &e = m->entries[index];
	if (e.status == 1) return nullptr;
	if (len_out != nullptr) *len_out = e.data.size();
	return e.data.data();
}

int32_t ce_multifile_image_count(const ce_multifile *m)
{
	return static_cast<int32_t>(m->imageIndices.size());
}

int32_t ce_multifile_image_index(const ce_multifile *m, int32_t nth)
{
	if (nth < 0 || nth >= ce_multifile_image_count(m)) return -1;
	return m->imageIndices[nth];
}

int32_t ce_multifile_savedata_index(const ce_multifile *m) { return m->savedataIndex; }

const char *ce_multifile_record_line(const ce_multifile *m, uint64_t *len_out)
{
	if (len_out != nullptr) *len_out = 0;
	auto *mm = const_cast<ce_multifile *>(m);
	mm->recordLine.clear();
	for (const Entry &e : m->entries)
	{
		if (e.status == 1) return nullptr; // a movie records loaded bytes only
		if (!mm->recordLine.empty()) mm->recordLine += ' ';
		mm->recordLine += encodeName(e.name);
		mm->recordLine += '=';
		mm->recordLine += e.actualSha1;
		if (e.role != "image")
		{
			mm->recordLine += ':';
			mm->recordLine += e.role;
		}
	}
	if (len_out != nullptr) *len_out = mm->recordLine.size();
	return mm->recordLine.c_str();
}

int32_t ce_multifile_save(
	const char *descriptor_path,
	const char *const *names, const char *const *roles, int32_t count,
	const char **error_out)
{
	auto fail = [&](std::string message) -> int32_t
	{
		g_error = std::move(message);
		if (error_out != nullptr) *error_out = g_error.c_str();
		return 1;
	};
	if (error_out != nullptr) *error_out = nullptr;

	std::vector<Entry> entries;
	for (int32_t i = 0; i < count; i++)
	{
		Entry e;
		e.name = names[i] != nullptr ? names[i] : "";
		e.role = roles[i] != nullptr ? roles[i] : "";
		e.sha1.assign(40, '0'); // placeholder so checkStructure runs first
		entries.push_back(std::move(e));
	}
	std::string err;
	if (!checkStructure(entries, err)) return fail(std::move(err));

	/* creation is strict where loading is lenient: every file must be
	 * present - an incomplete set is never intentional */
	std::string folder = folderOf(descriptor_path);
	for (Entry &e : entries)
	{
		if (!chimera::readFile((folder + e.name).c_str(), e.data))
		{
			return fail("file '" + e.name + "' is not in the descriptor's folder");
		}
		hashInto(e.data, e.sha1);
	}
	if (!checkCueClosure(entries, err)) return fail(std::move(err));

	cJSON *root = cJSON_CreateObject();
	cJSON *files = cJSON_AddArrayToObject(root, "files");
	for (const Entry &e : entries)
	{
		cJSON *item = cJSON_CreateObject();
		cJSON_AddStringToObject(item, "name", e.name.c_str());
		cJSON_AddStringToObject(item, "sha1", e.sha1.c_str());
		cJSON_AddStringToObject(item, "role", e.role.c_str());
		cJSON_AddItemToArray(files, item);
	}
	char *text = cJSON_Print(root);
	cJSON_Delete(root);
	if (text == nullptr) return fail("could not serialize the descriptor");
	std::string out = text;
	cJSON_free(text);
	out += "\n";

	if (!chimera::writeFile(descriptor_path,
		reinterpret_cast<const uint8_t *>(out.data()), out.size()))
	{
		return fail(std::string("cannot write ") + descriptor_path);
	}
	return 0;
}

} // extern "C"
