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
#include <map>
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

/* ---- ISO ---- */

static std::vector<uint8_t> slurp(const fs::path &p)
{
	FILE *f = std::fopen(p.string().c_str(), "rb");
	assert(f != nullptr);
	std::fseek(f, 0, SEEK_END);
	const long n = std::ftell(f);
	std::fseek(f, 0, SEEK_SET);
	std::vector<uint8_t> v(static_cast<size_t>(n));
	if (n) assert(std::fread(v.data(), 1, v.size(), f) == v.size());
	std::fclose(f);
	return v;
}

static constexpr uint16_t kFixedFatDate = (0 << 9) | (1 << 5) | 1;

static uint32_t rd32le(const uint8_t *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (uint32_t(p[3]) << 24); }

/* Walks the JOLIET tree, which is the one a reader prefers and the one rpcs3
 * decodes, and collects every file as path -> bytes. Small on purpose: enough
 * to prove the image says what the folder said, without a library. */
static void readIsoJoliet(const std::vector<uint8_t> &iso, uint32_t lba, uint32_t len,
	const std::string &prefix, std::map<std::string, std::vector<uint8_t>> &out)
{
	const uint8_t *dir = iso.data() + size_t(lba) * 2048;
	uint32_t at = 0;
	while (at < len)
	{
		const uint8_t *r = dir + at;
		if (r[0] == 0)
		{
			/* padding to the end of a sector: jump to the next one */
			const uint32_t next = ((at / 2048) + 1) * 2048;
			if (next >= len) break;
			at = next;
			continue;
		}
		const uint32_t recLen = r[0];
		const uint32_t childLba = rd32le(r + 2);
		const uint32_t childLen = rd32le(r + 10);
		const bool isDir = (r[25] & 0x02) != 0;
		const uint32_t idLen = r[32];
		std::string name;
		for (uint32_t i = 0; i + 1 < idLen; i += 2)
			name.push_back(static_cast<char>(r[33 + i + 1])); /* UCS-2BE, ASCII here */
		at += recLen;
		if (idLen == 1 && (r[33] == 0 || r[33] == 1)) continue; /* . and .. */
		if (isDir) readIsoJoliet(iso, childLba, childLen, prefix + name + "/", out);
		else
		{
			const uint8_t *data = iso.data() + size_t(childLba) * 2048;
			out[prefix + name] = std::vector<uint8_t>(data, data + childLen);
		}
	}
}

static void anIsoIsReproducibleAndHoldsWhatWentIn()
{
	const fs::path a = workRoot() / "a";
	const fs::path b = workRoot() / "b";
	const fs::path ia = workRoot() / "a.iso";
	const fs::path ib = workRoot() / "b.iso";

	std::string sa, sb, err;
	assert(chimera::mediaMake(a.string(), ia.string(), chimera::MediaFormat::Iso9660, nullptr, sa, err));
	assert(chimera::mediaMake(b.string(), ib.string(), chimera::MediaFormat::Iso9660, nullptr, sb, err));
	assert(sa == sb); /* same content, different dates and modes */

	const std::vector<uint8_t> iso = slurp(ia);
	/* the descriptors rpcs3's loader scans for: PVD, Joliet SVD, terminator */
	assert(iso[16 * 2048] == 1 && std::memcmp(&iso[16 * 2048 + 1], "CD001", 5) == 0);
	assert(iso[17 * 2048] == 2 && std::memcmp(&iso[17 * 2048 + 1], "CD001", 5) == 0);
	assert(iso[18 * 2048] == 255);
	/* and the Joliet escape sequence that says the names are UCS-2 */
	assert(std::memcmp(&iso[17 * 2048 + 88], "%/E", 3) == 0);

	/* the root directory record lives in the descriptor, at offset 156 */
	const uint8_t *rootRec = &iso[17 * 2048 + 156];
	std::map<std::string, std::vector<uint8_t>> got;
	readIsoJoliet(iso, rd32le(rootRec + 2), rd32le(rootRec + 10), "", got);

	assert(got.size() == 6);
	assert(got.count("PS3_GAME/USRDIR/EBOOT.BIN") == 1);
	assert(got["PS3_GAME/USRDIR/EBOOT.BIN"] == std::vector<uint8_t>(300000, 'E'));
	assert(got["PS3_DISC.SFB"] == std::vector<uint8_t>({ 'S', 'F', 'B', ' ', 'p', 'a', 'y', 'l', 'o', 'a', 'd' }));
	/* the real names survive, case and all, which is what Joliet is for */
	assert(got.count("a_first.txt") == 1 && got.count("Z_last.txt") == 1);
}

/* A PlayStation 3 disc keeps a region table in the ISO system area, and rpcs3
 * treats it as mandatory: Loader/ISO.cpp reads a big-endian region count out of
 * the first four bytes and refuses a count below 1 as "non-PS3ISO", which
 * reaches the user as "Invalid file or folder" with nothing said about regions.
 * An image built from a decrypted dump has one region and it is not encrypted.
 * Reported from use: the first ISO this tool made would not boot. */
static void aPs3DiscCarriesItsRegionTable()
{
	const fs::path a = workRoot() / "a"; /* PS3_DISC.SFB at its root */
	const fs::path iso = workRoot() / "ps3.iso";
	std::string sha, err;
	assert(chimera::mediaMake(a.string(), iso.string(), chimera::MediaFormat::Iso9660, nullptr, sha, err));

	const std::vector<uint8_t> img = slurp(iso);
	const auto be32 = [&](size_t at) {
		return (uint32_t(img[at]) << 24) | (uint32_t(img[at + 1]) << 16)
			| (uint32_t(img[at + 2]) << 8) | uint32_t(img[at + 3]);
	};
	assert(be32(0) == 1); /* one region */
	/* ending on the last sector of the image, so it covers all of it */
	assert(be32(12) == (img.size() / 2048) - 1);

	/* and a disc that is not a PS3 one is left alone: the table would mean
	 * nothing there, and the system area is reserved */
	const fs::path plain = workRoot() / "plain";
	fs::remove_all(plain);
	put(plain / "readme.txt", "hello");
	const fs::path plainIso = workRoot() / "plain.iso";
	assert(chimera::mediaMake(plain.string(), plainIso.string(), chimera::MediaFormat::Iso9660,
		nullptr, sha, err));
	const std::vector<uint8_t> other = slurp(plainIso);
	for (size_t i = 0; i < 32; i++) assert(other[i] == 0);
}

/* ---- FAT12 ---- */

static void aFloppyIsReproducibleAndReadable()
{
	const fs::path a = workRoot() / "a";
	const fs::path b = workRoot() / "b";
	const fs::path fa = workRoot() / "a.img";
	const fs::path fb = workRoot() / "b.img";
	std::string sa, sb, err;
	assert(chimera::mediaMake(a.string(), fa.string(), chimera::MediaFormat::Fat12, nullptr, sa, err));
	assert(chimera::mediaMake(b.string(), fb.string(), chimera::MediaFormat::Fat12, nullptr, sb, err));
	assert(sa == sb);

	const std::vector<uint8_t> img = slurp(fa);
	assert(img.size() == 2880u * 512u);
	assert(img[510] == 0x55 && img[511] == 0xAA);
	/* the BPB a 1.44 MB floppy must carry */
	assert(img[11] == 0x00 && img[12] == 0x02); /* 512 bytes per sector */
	assert(img[13] == 1);                        /* one sector per cluster */
	assert(img[21] == 0xF0);                     /* media descriptor */
	/* DOS put the moment of formatting in the volume serial; ours is fixed, or
	 * two identical disks would not hash alike */
	assert(rd32le(&img[39]) == 0);

	/* walk the root directory and find a file we put there */
	const uint32_t rootOff = (1u + 2u * 9u) * 512u;
	bool foundEboot = false, foundDir = false;
	for (uint32_t i = 0; i < 224; i++)
	{
		const uint8_t *e = &img[rootOff + i * 32];
		if (e[0] == 0) break;
		const std::string name(reinterpret_cast<const char *>(e), 11);
		if (e[11] & 0x10) foundDir = true;
		if (name.compare(0, 8, "Z_LAST  ") == 0) foundEboot = true;
		/* every date field is the fixed one */
		assert(rd32le(e + 16) == ((uint32_t(kFixedFatDate) << 16) | kFixedFatDate));
	}
	assert(foundDir);  /* PS3_GAME became a subdirectory */
	assert(foundEboot);
}

static void aFloppyRefusesWhatDoesNotFit()
{
	const fs::path big = workRoot() / "big";
	fs::remove_all(big);
	/* comfortably past 1.44 MB */
	for (int i = 0; i < 4; i++)
		put(big / ("part" + std::to_string(i) + ".bin"), std::string(500000, 'x'));
	std::string sha, err;
	assert(!chimera::mediaMake(big.string(), (workRoot() / "big.img").string(),
		chimera::MediaFormat::Fat12, nullptr, sha, err));
	assert(err.find("does not fit") != std::string::npos);
	assert(!fs::exists(workRoot() / "big.img"));
}

int main()
{
	fs::remove_all(workRoot());
	fs::create_directories(workRoot());

	theSameContentPacksToTheSameBytes();
	whatComesOutIsWhatWentIn();
	collectSkipsWhatIsNotAFile();
	progressCountsEveryByteAndFile();
	anIsoIsReproducibleAndHoldsWhatWentIn();
	aPs3DiscCarriesItsRegionTable();
	aFloppyIsReproducibleAndReadable();
	aFloppyRefusesWhatDoesNotFit();
	aCancelledPackLeavesNothing();
	anEmptyFolderIsRefused();

	fs::remove_all(workRoot());
	std::printf("test_media: ok\n");
	return 0;
}
