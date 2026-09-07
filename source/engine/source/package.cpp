/* package.cpp - the core package container.
 *
 * Replaces the zip/directory reading and identity hashing in
 * CorePackageDiscovery/CorePackageLoader; what a package's entries MEAN is
 * still the caller's business until the session moves in. Package files live
 * at the archive root - that is what the extraction cache hands to the
 * loader, so anything nested is not a package we can load.
 */

#include "chimera/engine.h"
#include "file_io.hpp"
#include "sha1.hpp"

#include "../../extern/miniz/miniz.h"

#include <cstring>
#include <algorithm>
#include <filesystem>
#include <map>
#include <string>
#include <vector>

namespace {

thread_local std::string g_openError;

constexpr const char *WBX_FILE = "core.wbx";
constexpr const char *CONFIG_FILE = "waterbox.config";
constexpr const char *MANIFEST_FILE = "chimera-core.json";
constexpr const char *MANIFEST_FILE_LEGACY = "minihawk-core.json"; // packages built before the rename

} // namespace

struct ce_package
{
	// zip form
	std::vector<uint8_t> zipData;
	mz_zip_archive zip{};
	bool zipOpen = false;
	std::map<std::string, mz_uint> entries;
	std::vector<std::string> assetNames;
	bool assetsBuilt = false;
	std::string sha1;

	// directory form
	std::string dir;

	bool isWaterbox = false;
	std::string error;
	std::vector<uint8_t> entryData;

	~ce_package()
	{
		if (zipOpen) mz_zip_reader_end(&zip);
	}
};

extern "C" {

ce_package *ce_package_open(const char *path, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;

	if (chimera::isDirectory(path))
	{
		auto join = [&](const char *name) { return std::string(path) + "/" + name; };
		bool waterbox = chimera::fileExists(join(WBX_FILE).c_str()) && chimera::fileExists(join(CONFIG_FILE).c_str());
		if (!waterbox && !chimera::fileExists(join(MANIFEST_FILE).c_str())
			&& !chimera::fileExists(join(MANIFEST_FILE_LEGACY).c_str()))
		{
			return nullptr;
		}
		auto *p = new ce_package();
		p->dir = path;
		p->isWaterbox = waterbox;
		return p;
	}

	std::vector<uint8_t> data;
	if (!chimera::readFile(path, data)) return nullptr; // no file, no package
	auto *p = new ce_package();
	p->zipData = std::move(data);
	if (mz_zip_reader_init_mem(&p->zip, p->zipData.data(), p->zipData.size(), 0) == MZ_FALSE)
	{
		/* a file that claims to be a zip but does not parse is BROKEN, not
		 * invisible - a corrupt package must stay listable with its error */
		bool looksLikeZip = p->zipData.size() >= 4 && std::memcmp(p->zipData.data(), "PK\x03\x04", 4) == 0;
		delete p;
		if (looksLikeZip)
		{
			g_openError = "not a readable zip archive";
			if (error_out != nullptr) *error_out = g_openError.c_str();
		}
		return nullptr; // otherwise: not a zip, so not a package - the quiet case
	}
	p->zipOpen = true;
	mz_uint count = mz_zip_reader_get_num_files(&p->zip);
	for (mz_uint i = 0; i < count; i++)
	{
		char nameBuf[512];
		mz_zip_reader_get_filename(&p->zip, i, nameBuf, sizeof nameBuf);
		p->entries.emplace(nameBuf, i); // root-relative names; first wins on the (never-seen) dup
	}
	bool waterbox = p->entries.count(WBX_FILE) != 0 && p->entries.count(CONFIG_FILE) != 0;
	if (!waterbox && p->entries.count(MANIFEST_FILE) == 0 && p->entries.count(MANIFEST_FILE_LEGACY) == 0)
	{
		delete p;
		return nullptr; // an ordinary zip
	}
	p->isWaterbox = waterbox;
	/* only now is the hash worth paying for: it is the identity of a thing we
	 * know to be a package */
	p->sha1 = chimera::sha1Hex(p->zipData.data(), p->zipData.size());
	return p;
}

void ce_package_free(ce_package *p) { delete p; }

const char *ce_package_sha1(const ce_package *p) { return p->sha1.empty() ? nullptr : p->sha1.c_str(); }

int32_t ce_package_is_waterbox(const ce_package *p) { return p->isWaterbox ? 1 : 0; }

/* The package's core-owned asset files: every entry under "assets/". The
 * names are cached on first ask; bytes come through ce_package_entry like
 * any other entry. */
static void buildAssetList(ce_package *p)
{
	if (p->assetsBuilt) return;
	p->assetsBuilt = true;
	if (!p->dir.empty())
	{
		std::error_code ec;
		std::filesystem::path base = std::filesystem::path(p->dir) / "assets";
		if (std::filesystem::is_directory(base, ec))
		{
			for (auto it = std::filesystem::recursive_directory_iterator(base, ec);
			     !ec && it != std::filesystem::recursive_directory_iterator(); it.increment(ec))
			{
				if (!it->is_regular_file(ec)) continue;
				std::error_code rec;
				std::string rel = std::filesystem::relative(it->path(), std::filesystem::path(p->dir), rec).generic_string();
				if (!rec) p->assetNames.push_back(std::move(rel));
			}
		}
	}
	else
	{
		for (const auto &e : p->entries)
			if (e.first.rfind("assets/", 0) == 0 && e.first.back() != '/')
				p->assetNames.push_back(e.first);
	}
	std::sort(p->assetNames.begin(), p->assetNames.end());
}

int32_t ce_package_asset_count(ce_package *p)
{
	buildAssetList(p);
	return static_cast<int32_t>(p->assetNames.size());
}

const char *ce_package_asset_name(ce_package *p, int32_t i)
{
	buildAssetList(p);
	if (i < 0 || static_cast<size_t>(i) >= p->assetNames.size()) return nullptr;
	return p->assetNames[static_cast<size_t>(i)].c_str();
}

int32_t ce_package_has_entry(ce_package *p, const char *name)
{
	if (!p->dir.empty()) return chimera::fileExists((p->dir + "/" + name).c_str()) ? 1 : 0;
	return p->entries.count(name) != 0 ? 1 : 0;
}

const uint8_t *ce_package_entry(ce_package *p, const char *name, uint64_t *len_out)
{
	p->error.clear();
	if (!p->dir.empty())
	{
		std::string full = p->dir + "/" + name;
		if (!chimera::fileExists(full.c_str())) return nullptr;
		if (!chimera::readFile(full.c_str(), p->entryData))
		{
			p->error = std::string("could not read ") + name;
			return nullptr;
		}
	}
	else
	{
		auto it = p->entries.find(name);
		if (it == p->entries.end()) return nullptr;
		size_t rawLen = 0;
		void *raw = mz_zip_reader_extract_to_heap(&p->zip, it->second, &rawLen, 0);
		if (raw == nullptr)
		{
			p->error = std::string("could not extract ") + name;
			return nullptr;
		}
		p->entryData.assign(static_cast<uint8_t *>(raw), static_cast<uint8_t *>(raw) + rawLen);
		mz_free(raw);
	}
	if (len_out != nullptr) *len_out = p->entryData.size();
	return p->entryData.data();
}

const char *ce_package_last_error(ce_package *p) { return p->error.c_str(); }

} // extern "C"
