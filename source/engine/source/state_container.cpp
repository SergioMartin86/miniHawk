/* state_container.cpp - the zip-of-lumps shape savestates and movies share.
 *
 * Replaces ZipStateSaver/FrameworkZipWriter/ZipStateLoader's container logic;
 * the C# keeps the file plumbing (temp files, backups, renames) and hands
 * whole buffers across. Compatibility is with the FORMAT: archives written
 * here read in old builds and vice versa; the deflate bytes themselves are
 * miniz's rather than System.IO.Compression's, which the format never pinned.
 *
 * Reproduced quirks are marked; the naming rules live in engine.h.
 */

#include "chimera/engine.h"
#include "zstd_dyn.hpp"
#include "progress.hpp"

#include <algorithm>

#include "../../extern/miniz/miniz.h"

#include <cstring>
#include <map>
#include <string>
#include <vector>

namespace {

/* the C# writer's level mapping: .NET's NoCompression/Fastest/Optimal */
int deflateLevelFor(int32_t compressionLevel)
{
	if (compressionLevel == 0) return MZ_NO_COMPRESSION;
	if (compressionLevel < 5) return MZ_BEST_SPEED;
	return MZ_DEFAULT_LEVEL;
}

std::string fileNameOf(const char *name, const char *ext)
{
	std::string fileName(name);
	if (ext != nullptr && ext[0] != '\0')
	{
		fileName.append(1, '.').append(ext);
	}
	return fileName;
}

/* the lookup key the C# loader always used: path up to the first '.' */
std::string keyOf(const std::string &path)
{
	auto dot = path.find('.');
	return dot == std::string::npos ? path : path.substr(0, dot);
}

bool zstdDecompress(const uint8_t *data, size_t len, std::vector<uint8_t> &out, std::string &error)
{
	const char *loadError = nullptr;
	const chimera::ZstdApi *zstd = chimera::zstdApi(&loadError);
	if (zstd == nullptr)
	{
		error = loadError;
		return false;
	}
	void *zds = zstd->createDStream();
	if (zds == nullptr)
	{
		error = "zstd: could not create a decompression stream";
		return false;
	}
	zstd->initDStream(zds);
	chimera::ZstdApi::Buffer input{ data, len, 0 };
	uint8_t chunk[1 << 16];
	bool ok = true;
	size_t reportedAt = 0;
	for (;;)
	{
		chimera::ZstdApi::OutBuffer output{ chunk, sizeof chunk, 0 };
		size_t rc = zstd->decompressStream(zds, &output, &input);
		if (zstd->isError(rc) != 0)
		{
			error = "zstd: corrupt compressed lump";
			ok = false;
			break;
		}
		out.insert(out.end(), chunk, chunk + output.pos);
		if (input.pos - reportedAt >= (4u << 20))
		{
			reportedAt = input.pos;
			chimera::progress("reading the greenzone", input.pos, input.size);
		}
		if (rc == 0 && input.pos >= input.size) break;
		if (output.pos == 0 && input.pos >= input.size)
		{
			error = "zstd: truncated compressed lump";
			ok = false;
			break;
		}
	}
	zstd->freeDStream(zds);
	return ok;
}

} // namespace

/* ---- writer ---- */

struct ce_state_writer
{
	mz_zip_archive zip{};
	int32_t compressionLevel;
	std::string error;
	std::vector<uint8_t> finished;
	bool done = false;
};

extern "C" {

ce_state_writer *ce_state_writer_new(int32_t compression_level, const char *emu_version)
{
	auto *w = new ce_state_writer();
	w->compressionLevel = compression_level;
	if (mz_zip_writer_init_heap(&w->zip, 0, 0) == MZ_FALSE)
	{
		w->error = "could not start a zip archive";
		return w;
	}
	/* every archive says what it is and what wrote it, uncompressed - the
	 * trailing newline matches the old StreamWriter.WriteLine */
	std::string version = std::string(emu_version != nullptr ? emu_version : "") + "\n";
	ce_state_writer_put_lump(w, "ChimeraState 1", "0", 0, reinterpret_cast<const uint8_t *>("3\n"), 2);
	ce_state_writer_put_lump(w, "ChimeraVersion", "txt", 0,
		reinterpret_cast<const uint8_t *>(version.data()), version.size());
	return w;
}

void ce_state_writer_free(ce_state_writer *w)
{
	if (w == nullptr) return;
	mz_zip_writer_end(&w->zip);
	delete w;
}

int32_t ce_state_writer_put_lump(
	ce_state_writer *w, const char *name, const char *ext, int32_t zstd,
	const uint8_t *data, uint64_t len)
{
	/* the old writer collected the first failure and reported it at close */
	if (!w->error.empty()) return 1;

	std::string path = fileNameOf(name, ext);
	if (zstd != 0)
	{
		const char *loadError = nullptr;
		const chimera::ZstdApi *api = chimera::zstdApi(&loadError);
		if (api == nullptr)
		{
			w->error = loadError;
			return 1;
		}
		std::vector<uint8_t> compressed(api->compressBound(static_cast<size_t>(len)));
		/* the 0-9 config level maps onto zstd as 2n+1, as it always did */
		const int level = w->compressionLevel * 2 + 1;
		size_t written;
		if (len >= (8u << 20) && api->createCStream != nullptr && api->compressStream2 != nullptr && chimera::progressWanted())
		{
			/* A greenzone is hundreds of megabytes, and one call that says
			 * nothing for ten seconds is a frozen window. Streamed in blocks,
			 * the same bytes go in and a frame comes out, and each block says
			 * how far along it is. */
			void *zcs = api->createCStream();
			if (zcs == nullptr)
			{
				w->error = "zstd: could not create a compression stream";
				return 1;
			}
			api->initCStream(zcs, level);
			const std::string stage = "compressing " + std::string(name);
			chimera::ZstdApi::Buffer input{ data, static_cast<size_t>(len), 0 };
			chimera::ZstdApi::OutBuffer output{ compressed.data(), compressed.size(), 0 };
			bool failed = false;
			while (input.pos < input.size)
			{
				const size_t blockEnd = std::min(input.size, input.pos + (size_t)(4u << 20));
				chimera::ZstdApi::Buffer block{ data, blockEnd, input.pos };
				const size_t rc = api->compressStream2(zcs, &output, &block, 0);
				input.pos = block.pos;
				if (api->isError(rc) != 0) { failed = true; break; }
				chimera::progress(stage.c_str(), input.pos, input.size);
			}
			if (!failed)
			{
				chimera::ZstdApi::Buffer empty{ data, static_cast<size_t>(len), static_cast<size_t>(len) };
				for (;;)
				{
					const size_t remaining = api->compressStream2(zcs, &output, &empty, 2);
					if (api->isError(remaining) != 0) { failed = true; break; }
					if (remaining == 0) break;
				}
			}
			api->freeCStream(zcs);
			if (failed)
			{
				w->error = "zstd: compression failed for " + path;
				return 1;
			}
			written = output.pos;
		}
		else
		{
			written = api->compress(
				compressed.data(), compressed.size(), data, static_cast<size_t>(len), level);
			if (api->isError(written) != 0)
			{
				w->error = "zstd: compression failed for " + path;
				return 1;
			}
		}
		/* compressed lumps are STORED - deflating zstd is a timesink */
		path += ".zst";
		if (mz_zip_writer_add_mem(&w->zip, path.c_str(), compressed.data(), written, MZ_NO_COMPRESSION) == MZ_FALSE)
		{
			w->error = "could not add " + path;
			return 1;
		}
		return 0;
	}
	if (mz_zip_writer_add_mem(
			&w->zip, path.c_str(), data, static_cast<size_t>(len),
			static_cast<mz_uint>(deflateLevelFor(w->compressionLevel))) == MZ_FALSE)
	{
		w->error = "could not add " + path;
		return 1;
	}
	return 0;
}

const uint8_t *ce_state_writer_finish(ce_state_writer *w, uint64_t *len_out)
{
	if (!w->error.empty()) return nullptr;
	if (!w->done)
	{
		void *buf = nullptr;
		size_t size = 0;
		if (mz_zip_writer_finalize_heap_archive(&w->zip, &buf, &size) == MZ_FALSE)
		{
			w->error = "could not finalize the archive";
			return nullptr;
		}
		w->finished.assign(static_cast<uint8_t *>(buf), static_cast<uint8_t *>(buf) + size);
		mz_free(buf);
		w->done = true;
	}
	if (len_out != nullptr) *len_out = w->finished.size();
	return w->finished.data();
}

const char *ce_state_writer_last_error(ce_state_writer *w) { return w->error.c_str(); }

} // extern "C"

/* ---- reader ---- */

struct ce_state_reader
{
	std::vector<uint8_t> data;
	mz_zip_archive zip{};
	std::map<std::string, mz_uint> entries; // key -> zip file index
	int32_t version = 0;
	std::string error;
	std::vector<uint8_t> lump;
};

namespace {

thread_local std::string g_openError; // concurrent opens must not clobber each other's story

/* the loader's prefix handling: strip a common "<dir>/" prefix (the pre-
 * tarbomb layout); any other common prefix means tarbomb, strip nothing */
std::string commonDirPrefix(const std::vector<std::string> &paths)
{
	if (paths.empty()) return "";
	std::string prefix = paths[0];
	for (const auto &path : paths)
	{
		size_t i = 0;
		while (i < prefix.size() && i < path.size() && prefix[i] == path[i]) i++;
		prefix.resize(i);
	}
	if (prefix.empty() || prefix.back() == '/' || prefix.back() == '\\') return prefix;
	return "";
}

/* the version lump: an integer sub-version, or 1.0.0 when the lump is empty */
bool readVersion(ce_state_reader *r)
{
	uint64_t len = 0;
	const uint8_t *text = ce_state_reader_lump(r, "ChimeraState 1", "0", &len);
	if (text == nullptr) return false;
	if (len == 0)
	{
		r->version = 0;
		return true;
	}
	int32_t value = 0;
	uint64_t i = 0;
	while (i < len && text[i] >= '0' && text[i] <= '9')
	{
		value = value * 10 + (text[i] - '0');
		i++;
	}
	r->version = value;
	return true;
}

} // namespace

extern "C" {

ce_state_reader *ce_state_reader_open(
	const uint8_t *data, uint64_t len, int32_t is_movie, const char **error_out)
{
	if (error_out != nullptr) *error_out = nullptr;
	/* the same four-byte sniff the old loader did */
	if (len < 4 || std::memcmp(data, "PK\x03\x04", 4) != 0) return nullptr;

	auto *r = new ce_state_reader();
	r->data.assign(data, data + len);
	if (mz_zip_reader_init_mem(&r->zip, r->data.data(), r->data.size(), 0) == MZ_FALSE)
	{
		delete r;
		return nullptr;
	}

	mz_uint count = mz_zip_reader_get_num_files(&r->zip);
	std::vector<std::string> paths(count);
	for (mz_uint i = 0; i < count; i++)
	{
		char nameBuf[512];
		mz_zip_reader_get_filename(&r->zip, i, nameBuf, sizeof nameBuf);
		paths[i] = nameBuf;
	}
	std::string prefix = commonDirPrefix(paths);
	for (mz_uint i = 0; i < count; i++)
	{
		std::string name = paths[i].substr(prefix.size());
		for (auto &c : name)
		{
			if (c == '\\') c = '/';
		}
		std::string key = keyOf(name);
		/* the ".zst" marker survives into the key, as it always did */
		if (name.size() >= 4 && name.compare(name.size() - 4, 4, ".zst") == 0) key += ".zst";
		if (!r->entries.emplace(key, i).second)
		{
			g_openError = "Duplicate file found in zip archive: " + key + ". Please delete one.";
			if (error_out != nullptr) *error_out = g_openError.c_str();
			ce_state_reader_free(r);
			return nullptr;
		}
	}

	if (!readVersion(r))
	{
		if (is_movie != 0)
		{
			/* movies predating the version lump load as 1.0.0 */
			r->version = 0;
			r->error.clear();
		}
		else
		{
			ce_state_reader_free(r);
			return nullptr;
		}
	}
	return r;
}

void ce_state_reader_free(ce_state_reader *r)
{
	if (r == nullptr) return;
	mz_zip_reader_end(&r->zip);
	delete r;
}

int32_t ce_state_reader_version(const ce_state_reader *r) { return r->version; }

const uint8_t *ce_state_reader_lump(
	ce_state_reader *r, const char *name, const char *ext, uint64_t *len_out)
{
	r->error.clear();
	bool compressed = false;
	auto it = r->entries.find(name);
	if (it == r->entries.end())
	{
		it = r->entries.find(std::string(name) + ".zst");
		if (it == r->entries.end()) return nullptr;
		compressed = true;
	}

	/* quirk, preserved: version 1.0.2 marked nothing, and compression was
	 * inferred - note "Greenzone" never matches the "GreenZone" lump, and
	 * did not in the C# either */
	if (r->version == 2)
	{
		std::string extStr = ext != nullptr ? ext : "";
		if (extStr == "bin" || extStr == "bmp" || std::strcmp(name, "Greenzone") == 0)
		{
			compressed = true;
		}
	}

	size_t rawLen = 0;
	void *raw = mz_zip_reader_extract_to_heap(&r->zip, it->second, &rawLen, 0);
	if (raw == nullptr)
	{
		r->error = std::string("could not extract ") + name;
		return nullptr;
	}
	r->lump.clear();
	if (compressed)
	{
		bool ok = zstdDecompress(static_cast<uint8_t *>(raw), rawLen, r->lump, r->error);
		mz_free(raw);
		if (!ok) return nullptr;
	}
	else
	{
		r->lump.assign(static_cast<uint8_t *>(raw), static_cast<uint8_t *>(raw) + rawLen);
		mz_free(raw);
	}
	if (len_out != nullptr) *len_out = r->lump.size();
	return r->lump.data();
}

const char *ce_state_reader_last_error(ce_state_reader *r) { return r->error.c_str(); }

} // extern "C"
