/* test_media.cpp - the reproducible packer: that the same CONTENT packs to the
 * same bytes whatever the machine says about it, that what comes out holds what
 * went in, and that a cancelled pack leaves nothing behind.
 *
 * The first of those is the whole point. A project stores names and SHA1s, so
 * two people with the same dump have to produce the same file or the movie one
 * of them records cannot be verified by the other. Timestamps, permissions and
 * directory order are the three ways the machine leaks into the output, and
 * each gets a test here rather than a comment saying it was thought about.
 */

#include "../source/media_maker.hpp"

#include "../../extern/miniz/miniz.h"

#include <cassert>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

#if !defined(_WIN32)
#include <sys/stat.h>
#include <utime.h>
#endif

namespace fs = std::filesystem;

static fs::path workRoot()
{
	return fs::path("work.test_media");
}

static void put(const fs::path &p, const std::string &bytes)
{
	fs::create_directories(p.parent_path());
	FILE *f = std::fopen(p.string().c_str(), "wb");
	assert(f != nullptr);
	if (!bytes.empty()) std::fwrite(bytes.data(), 1, bytes.size(), f);
	std::fclose(f);
}

/* A small tree with nesting, a big-ish file, and names whose order differs
 * between a byte-wise sort and most locale collations. */
static void makeTree(const fs::path &root)
{
	fs::remove_all(root);
	put(root / "PS3_DISC.SFB", "SFB payload");
	put(root / "PS3_GAME" / "PARAM.SFO", "param");
	put(root / "PS3_GAME" / "USRDIR" / "EBOOT.BIN", std::string(300000, 'E'));
	put(root / "PS3_GAME" / "USRDIR" / "data.bin", std::string(1024, '\x7f'));
	put(root / "Z_last.txt", "z");
	put(root / "a_first.txt", "a");
}

/* Everything about the tree that is NOT its content: what must not reach the
 * output. */
static void disturbMetadata(const fs::path &root)
{
#if !defined(_WIN32)
	for (const auto &e : fs::recursive_directory_iterator(root))
	{
		::chmod(e.path().string().c_str(), fs::is_directory(e.path()) ? 0700 : 0600);
		struct utimbuf t;
		t.actime = 1234567890;
		t.modtime = 1400000000;
		::utime(e.path().string().c_str(), &t);
	}
	::chmod(root.string().c_str(), 0700);
#else
	(void)root;
#endif
}

static std::string packOk(const fs::path &tree, const fs::path &out)
{
	std::string sha, err;
	const bool ok = chimera::mediaMake(tree.string(), out.string(), chimera::MediaFormat::ZipStored,
		nullptr, sha, err);
	if (!ok) std::fprintf(stderr, "pack failed: %s\n", err.c_str());
	assert(ok);
	assert(sha.size() == 40);
	return sha;
}

static void theSameContentPacksToTheSameBytes()
{
	const fs::path a = workRoot() / "a";
	const fs::path b = workRoot() / "b";
	makeTree(a);
	fs::remove_all(b);
	fs::copy(a, b, fs::copy_options::recursive);
	disturbMetadata(b); /* same files, different dates and modes */

	const std::string sa = packOk(a, workRoot() / "a.zip");
	const std::string sb = packOk(b, workRoot() / "b.zip");
	assert(sa == sb);

	/* and again from the same tree, later: no wall clock in the output */
	const std::string sa2 = packOk(a, workRoot() / "a2.zip");
	assert(sa == sa2);
}

static void whatComesOutIsWhatWentIn()
{
	const fs::path a = workRoot() / "a";
	const fs::path zip = workRoot() / "a.zip";
	mz_zip_archive z{};
	assert(mz_zip_reader_init_file(&z, zip.string().c_str(), 0) == MZ_TRUE);

	const mz_uint n = mz_zip_reader_get_num_files(&z);
	assert(n == 6);

	/* names, in the order they were written: byte-wise sorted */
	std::vector<std::string> names;
	for (mz_uint i = 0; i < n; i++)
	{
		mz_zip_archive_file_stat st{};
		assert(mz_zip_reader_file_stat(&z, i, &st) == MZ_TRUE);
		names.push_back(st.m_filename);
		/* STORED, so a core can seek in it rather than decompress */
		assert(st.m_method == 0);
	}
	for (size_t i = 1; i < names.size(); i++) assert(names[i - 1] < names[i]);

	size_t sz = 0;
	void *p = mz_zip_reader_extract_file_to_heap(&z, "PS3_GAME/USRDIR/EBOOT.BIN", &sz, 0);
	assert(p != nullptr);
	assert(sz == 300000);
	assert(std::memcmp(p, std::string(300000, 'E').data(), sz) == 0);
	mz_free(p);

	p = mz_zip_reader_extract_file_to_heap(&z, "PS3_DISC.SFB", &sz, 0);
	assert(p != nullptr && sz == 11 && std::memcmp(p, "SFB payload", 11) == 0);
	mz_free(p);

	mz_zip_reader_end(&z);
	(void)a;
}

static void aCancelledPackLeavesNothing()
{
	const fs::path a = workRoot() / "a";
	const fs::path out = workRoot() / "cancelled.zip";
	fs::remove(out);
	std::string sha, err;
	int calls = 0;
	const bool ok = chimera::mediaMake(a.string(), out.string(), chimera::MediaFormat::ZipStored,
		[&](const char *, uint64_t, uint64_t, uint64_t, uint64_t) {
			calls++;
			return false; /* stop at the first chance */
		},
		sha, err);
	assert(!ok);
	assert(calls == 1);
	assert(err == "cancelled");
	/* a half-written image that looks like an image is worse than none */
	assert(!fs::exists(out));
}

static void progressCountsEveryByteAndFile()
{
	const fs::path a = workRoot() / "a";
	uint64_t lastBytes = 0, lastTotal = 0, lastFiles = 0, totalFiles = 0;
	std::string sha, err;
	const bool ok = chimera::mediaMake(a.string(), (workRoot() / "p.zip").string(),
		chimera::MediaFormat::ZipStored,
		[&](const char *name, uint64_t done, uint64_t total, uint64_t files, uint64_t nfiles) {
			assert(name != nullptr && *name != '\0');
			assert(done >= lastBytes); /* never goes backwards */
			lastBytes = done;
			lastTotal = total;
			lastFiles = files;
			totalFiles = nfiles;
			return true;
		},
		sha, err);
	assert(ok);
	assert(totalFiles == 6);
	assert(lastFiles == 6);
	assert(lastBytes == lastTotal);
	assert(lastTotal == 11 + 5 + 300000 + 1024 + 1 + 1);
}

static void anEmptyFolderIsRefused()
{
	const fs::path empty = workRoot() / "empty";
	fs::remove_all(empty);
	fs::create_directories(empty);
	std::string sha, err;
	assert(!chimera::mediaMake(empty.string(), (workRoot() / "empty.zip").string(),
		chimera::MediaFormat::ZipStored, nullptr, sha, err));
	assert(err.find("no files") != std::string::npos);
}

static void collectSkipsWhatIsNotAFile()
{
	const fs::path a = workRoot() / "a";
	std::vector<chimera::MediaEntry> files;
	std::string err;
	assert(chimera::mediaCollect(a.string(), files, err));
	assert(files.size() == 6);
	for (const auto &e : files)
	{
		assert(!e.rel.empty());
		assert(e.rel.front() != '/');
		assert(e.rel.find('\\') == std::string::npos); /* '/' everywhere, even on Windows */
	}
}

int main()
{
	fs::remove_all(workRoot());
	fs::create_directories(workRoot());

	theSameContentPacksToTheSameBytes();
	whatComesOutIsWhatWentIn();
	collectSkipsWhatIsNotAFile();
	progressCountsEveryByteAndFile();
	aCancelledPackLeavesNothing();
	anEmptyFolderIsRefused();

	fs::remove_all(workRoot());
	std::printf("test_media: ok\n");
	return 0;
}
