/* test_package.cpp - pins the core package container: what counts as a
 * package (zip and directory forms), identity hashing, entry access, and the
 * quiet-vs-broken distinction on refusal.
 */

#include "chimera/engine.h"

#include "../../extern/miniz/miniz.h"

#include <cassert>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#if defined(_WIN32)
#include <direct.h>
#else
#include <sys/stat.h>
#include <sys/types.h>
#endif

static std::string writeTemp(const char *name, const void *data, size_t len)
{
	std::string path = std::string("work.test_package.") + name;
	FILE *f = std::fopen(path.c_str(), "wb");
	assert(f != nullptr);
	if (len != 0) std::fwrite(data, 1, len, f);
	std::fclose(f);
	return path;
}

static std::vector<uint8_t> makeZip(bool waterbox, bool manifest)
{
	mz_zip_archive zip{};
	assert(mz_zip_writer_init_heap(&zip, 0, 0) == MZ_TRUE);
	if (waterbox)
	{
		const char *cfg = "{ \"coreName\": \"testCore\", \"systemId\": \"NES\" }";
		mz_zip_writer_add_mem(&zip, "core.wbx", "\x7f" "ELF-ish", 8, MZ_BEST_SPEED);
		mz_zip_writer_add_mem(&zip, "waterbox.config", cfg, std::strlen(cfg), MZ_BEST_SPEED);
	}
	if (manifest)
	{
		const char *m = "{ \"formatVersion\": 1, \"name\": \"adapter\" }";
		mz_zip_writer_add_mem(&zip, "chimera-core.json", m, std::strlen(m), MZ_BEST_SPEED);
	}
	mz_zip_writer_add_mem(&zip, "build.json", "{}", 2, MZ_BEST_SPEED);
	void *buf = nullptr;
	size_t size = 0;
	assert(mz_zip_writer_finalize_heap_archive(&zip, &buf, &size) == MZ_TRUE);
	std::vector<uint8_t> out(static_cast<uint8_t *>(buf), static_cast<uint8_t *>(buf) + size);
	mz_free(buf);
	mz_zip_writer_end(&zip);
	return out;
}

int main(void)
{
	{ // a waterbox package zip: identified, hashed, entries readable
		auto zip = makeZip(true, false);
		auto path = writeTemp("wbx.zip", zip.data(), zip.size());
		const char *error = nullptr;
		ce_package *p = ce_package_open(path.c_str(), &error);
		assert(p != nullptr && error == nullptr);
		assert(ce_package_is_waterbox(p));
		assert(ce_package_sha1(p) != nullptr && std::strlen(ce_package_sha1(p)) == 40);
		assert(ce_package_has_entry(p, "build.json"));
		assert(!ce_package_has_entry(p, "default_keybinds.json"));
		uint64_t len = 0;
		const uint8_t *cfg = ce_package_entry(p, "waterbox.config", &len);
		assert(cfg != nullptr && std::string(cfg, cfg + len).find("testCore") != std::string::npos);
		assert(ce_package_entry(p, "nope.bin", &len) == nullptr);
		assert(ce_package_last_error(p)[0] == '\0'); // absent, not broken
		ce_package_free(p);
		std::remove(path.c_str());
	}

	{ // a manifest package zip counts too, and is not waterbox
		auto zip = makeZip(false, true);
		auto path = writeTemp("man.zip", zip.data(), zip.size());
		ce_package *p = ce_package_open(path.c_str(), nullptr);
		assert(p != nullptr);
		assert(!ce_package_is_waterbox(p));
		ce_package_free(p);
		std::remove(path.c_str());
	}

	{ // an ordinary zip is quietly not a package
		auto zip = makeZip(false, false);
		auto path = writeTemp("plain.zip", zip.data(), zip.size());
		const char *error = nullptr;
		assert(ce_package_open(path.c_str(), &error) == nullptr);
		assert(error == nullptr);
		std::remove(path.c_str());
	}

	{ // a corrupt zip is BROKEN, not invisible
		auto zip = makeZip(true, false);
		zip.resize(zip.size() / 2); // truncate: PK magic intact, structure gone
		auto path = writeTemp("corrupt.zip", zip.data(), zip.size());
		const char *error = nullptr;
		assert(ce_package_open(path.c_str(), &error) == nullptr);
		assert(error != nullptr);
		std::remove(path.c_str());
	}

	{ // not-a-zip and not-a-path are both quiet refusals
		auto path = writeTemp("notzip.zip", "hello", 5);
		const char *error = reinterpret_cast<const char *>(1);
		assert(ce_package_open(path.c_str(), &error) == nullptr && error == nullptr);
		assert(ce_package_open("no/such/path/anywhere", &error) == nullptr && error == nullptr);
		std::remove(path.c_str());
	}

	{ // directory form: no identity, same entries
		std::remove("work.test_package.dir/core.wbx");
		std::remove("work.test_package.dir/waterbox.config");
#if defined(_WIN32)
		_mkdir("work.test_package.dir");
#else
		mkdir("work.test_package.dir", 0755);
#endif
		writeTemp("dir/core.wbx", "x", 1);
		writeTemp("dir/waterbox.config", "{ \"systemId\": \"NES\" }", 21);
		ce_package *p = ce_package_open("work.test_package.dir", nullptr);
		assert(p != nullptr);
		assert(ce_package_is_waterbox(p));
		assert(ce_package_sha1(p) == nullptr); // nothing to hash
		uint64_t len = 0;
		assert(ce_package_entry(p, "waterbox.config", &len) != nullptr && len == 21);
		ce_package_free(p);
		std::remove("work.test_package.dir/core.wbx");
		std::remove("work.test_package.dir/waterbox.config");
		std::remove("work.test_package.dir");
	}

	std::puts("test_package: all ok");
	return 0;
}
