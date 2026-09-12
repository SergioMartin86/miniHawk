/* media_fat12.cpp - a 1.44 MB floppy image, written reproducibly.
 *
 * The oldest format here and the least forgiving: 2880 sectors of 512 bytes,
 * two copies of a 12-bit allocation table, a root directory of exactly 224
 * entries, and 8.3 names in a character set that predates lowercase being
 * uncontroversial. What fits, fits; what does not is refused with the number,
 * because a floppy that silently held two thirds of a game would be worse than
 * no floppy.
 *
 * REPRODUCIBLE for the same reasons as the others, and one more of its own: the
 * volume serial number. DOS wrote the time of formatting there, which is
 * exactly the kind of thing that makes two identical disks hash differently, so
 * it is fixed. Dates on entries are fixed, the order is the same byte-wise sort
 * the rest of the packer uses, and the image is written whole - no gaps left
 * with whatever the allocator had lying around.
 *
 * 8.3 AND NOTHING ELSE. Long file names are an extension layered on top of this
 * directory format as a run of hidden entries, and they bring their own
 * checksum and ordering rules; a name that has to be shortened is shortened the
 * same way every time instead, with a ~1 counter that comes from the sorted
 * order rather than from the order a directory happened to be read in.
 */
#include "media_maker.hpp"

#include "sha1.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <map>
#include <memory>
#include <string>
#include <system_error>
#include <vector>

namespace chimera {
namespace {

constexpr uint32_t kSectorSize = 512;
constexpr uint32_t kTotalSectors = 2880; /* 1.44 MB */
constexpr uint32_t kReserved = 1;
constexpr uint32_t kFatCount = 2;
constexpr uint32_t kSectorsPerFat = 9;
constexpr uint32_t kRootEntries = 224;
constexpr uint32_t kRootSectors = kRootEntries * 32 / kSectorSize; /* 14 */
constexpr uint32_t kFirstDataSector = kReserved + kFatCount * kSectorsPerFat + kRootSectors;
constexpr uint32_t kClusterCount = kTotalSectors - kFirstDataSector; /* 1 sector per cluster */

/* 1980-01-01 00:00:00, which is also the earliest a FAT date field can say. */
constexpr uint16_t kFatDate = (0 << 9) | (1 << 5) | 1;
constexpr uint16_t kFatTime = 0;

struct Node
{
	std::string name;
	bool dir = false;
	uint64_t size = 0;
	std::string abs;
	std::vector<std::unique_ptr<Node>> kids;
	Node *parent = nullptr;

	std::string shortName; /* 11 bytes, name and extension, space padded */
	uint32_t firstCluster = 0;
	uint32_t clusters = 0;
};

Node *childDir(Node *parent, const std::string &name)
{
	for (auto &k : parent->kids)
		if (k->dir && k->name == name) return k.get();
	auto n = std::make_unique<Node>();
	n->name = name;
	n->dir = true;
	n->parent = parent;
	Node *raw = n.get();
	parent->kids.push_back(std::move(n));
	return raw;
}

/* NAME    EXT, 11 bytes, uppercase, in the character set the format allows. */
std::string shortNameOf(const std::string &in, bool dir)
{
	std::string base = in, ext;
	if (!dir)
	{
		const size_t dot = in.rfind('.');
		if (dot != std::string::npos && dot != 0)
		{
			base = in.substr(0, dot);
			ext = in.substr(dot + 1);
		}
	}
	auto clean = [](std::string s, size_t width) {
		std::string out;
		for (char c : s)
		{
			unsigned char u = static_cast<unsigned char>(c);
			if (u >= 'a' && u <= 'z') u = static_cast<unsigned char>(u - 'a' + 'A');
			const bool ok = (u >= 'A' && u <= 'Z') || (u >= '0' && u <= '9') ||
				std::strchr("$%'-_@~`!(){}^#&", static_cast<char>(u)) != nullptr;
			out.push_back(ok ? static_cast<char>(u) : '_');
			if (out.size() == width) break;
		}
		while (out.size() < width) out.push_back(' ');
		return out;
	};
	return clean(base, 8) + clean(ext, 3);
}

void put16(std::vector<uint8_t> &v, uint16_t x)
{
	v.push_back(static_cast<uint8_t>(x));
	v.push_back(static_cast<uint8_t>(x >> 8));
}

void put32(std::vector<uint8_t> &v, uint32_t x)
{
	for (int i = 0; i < 4; i++) v.push_back(static_cast<uint8_t>(x >> (i * 8)));
}

/* One 32-byte directory entry. */
void putEntry(std::vector<uint8_t> &v, const std::string &name11, bool dir, uint32_t firstCluster,
	uint32_t size)
{
	const size_t start = v.size();
	v.insert(v.end(), name11.begin(), name11.end());
	v.resize(start + 11, ' ');
	v.push_back(dir ? 0x10 : 0x20); /* attributes: directory, or archive */
	v.push_back(0);                 /* reserved */
	v.push_back(0);                 /* creation time, tenths */
	put16(v, kFatTime);
	put16(v, kFatDate);
	put16(v, kFatDate); /* last access */
	put16(v, 0);        /* high cluster word: always zero on FAT12 */
	put16(v, kFatTime);
	put16(v, kFatDate);
	put16(v, static_cast<uint16_t>(firstCluster));
	put32(v, size);
}

} // namespace

bool mediaWriteFat12(const std::vector<MediaEntry> &files, const std::string &outPath,
	const MediaProgress &progress, std::string &sha1Out, std::string &error)
{
	/* ---- the tree ---- */
	Node root;
	root.dir = true;
	for (const auto &e : files)
	{
		Node *at = &root;
		size_t start = 0;
		for (;;)
		{
			const size_t slash = e.rel.find('/', start);
			if (slash == std::string::npos) break;
			at = childDir(at, e.rel.substr(start, slash - start));
			start = slash + 1;
		}
		auto f = std::make_unique<Node>();
		f->name = e.rel.substr(start);
		f->size = e.size;
		f->abs = e.abs;
		f->parent = at;
		at->kids.push_back(std::move(f));
	}

	/* names and order, settled once */
	std::vector<Node *> dirs{ &root };
	std::vector<Node *> filesFlat;
	for (size_t i = 0; i < dirs.size(); i++)
	{
		Node *d = dirs[i];
		std::sort(d->kids.begin(), d->kids.end(),
			[](const std::unique_ptr<Node> &a, const std::unique_ptr<Node> &b) {
				if (a->dir != b->dir) return a->dir > b->dir;
				return a->name.compare(b->name) < 0;
			});
		std::map<std::string, int> seen;
		for (auto &k : d->kids)
		{
			k->shortName = shortNameOf(k->name, k->dir);
			if (int &n = seen[k->shortName]; ++n > 1)
			{
				/* ~1, ~2 ... taken from the sorted order, so the same folder
				 * always shortens the same way */
				const std::string tail = "~" + std::to_string(n);
				std::string base = k->shortName.substr(0, 8);
				while (!base.empty() && base.back() == ' ') base.pop_back();
				if (base.size() + tail.size() > 8) base.resize(8 - tail.size());
				base += tail;
				base.resize(8, ' ');
				k->shortName = base + k->shortName.substr(8);
			}
			if (k->dir) dirs.push_back(k.get());
			else filesFlat.push_back(k.get());
		}
	}

	/* ---- does it fit ---- */
	if (root.kids.size() > kRootEntries)
	{
		error = "the root of this folder holds " + std::to_string(root.kids.size()) +
			" entries and a floppy's root directory has room for " + std::to_string(kRootEntries) +
			". Put them in subdirectories, or use another format.";
		return false;
	}

	uint32_t needed = 0;
	for (Node *d : dirs)
	{
		if (d == &root) continue;
		/* "." and ".." come first in every subdirectory */
		const uint64_t bytes = (d->kids.size() + 2) * 32;
		d->clusters = static_cast<uint32_t>((bytes + kSectorSize - 1) / kSectorSize);
		needed += d->clusters;
	}
	for (Node *f : filesFlat)
	{
		f->clusters = static_cast<uint32_t>((f->size + kSectorSize - 1) / kSectorSize);
		needed += f->clusters;
	}
	if (needed > kClusterCount)
	{
		const uint64_t have = uint64_t(kClusterCount) * kSectorSize;
		uint64_t want = 0;
		for (const auto &e : files) want += e.size;
		error = "this folder does not fit on a 1.44 MB floppy: " + std::to_string(want) +
			" bytes of files need " + std::to_string(uint64_t(needed) * kSectorSize) +
			" bytes of clusters, and the disk holds " + std::to_string(have) + ".";
		return false;
	}

	/* ---- addresses ---- */
	uint32_t next = 2; /* clusters 0 and 1 are not addressable */
	for (Node *d : dirs)
	{
		if (d == &root) continue;
		d->firstCluster = next;
		next += d->clusters;
	}
	for (Node *f : filesFlat)
	{
		f->firstCluster = f->size ? next : 0; /* an empty file owns no cluster */
		next += f->clusters;
	}

	/* ---- the image, built whole then written ----
	 * 1.44 MB is small enough to assemble in memory, and assembling it means
	 * every byte is set on purpose rather than left as whatever was there. */
	std::vector<uint8_t> img(size_t(kTotalSectors) * kSectorSize, 0);

	{
		std::vector<uint8_t> boot;
		boot.push_back(0xEB); boot.push_back(0x3C); boot.push_back(0x90); /* jmp, nop */
		const char *oem = "CHIMERA ";
		boot.insert(boot.end(), oem, oem + 8);
		put16(boot, kSectorSize);
		boot.push_back(1);              /* sectors per cluster */
		put16(boot, kReserved);
		boot.push_back(kFatCount);
		put16(boot, static_cast<uint16_t>(kRootEntries));
		put16(boot, static_cast<uint16_t>(kTotalSectors));
		boot.push_back(0xF0);           /* media descriptor: 1.44 MB */
		put16(boot, static_cast<uint16_t>(kSectorsPerFat));
		put16(boot, 18);                /* sectors per track */
		put16(boot, 2);                 /* heads */
		put32(boot, 0);                 /* hidden sectors */
		put32(boot, 0);                 /* large sector count */
		boot.push_back(0);              /* drive number */
		boot.push_back(0);              /* reserved */
		boot.push_back(0x29);           /* extended boot signature */
		/* The volume serial number is where DOS wrote the moment of formatting.
		 * Fixed here, or two identical disks would not hash alike. */
		put32(boot, 0x00000000);
		const char *label = "CHIMERA    ";
		boot.insert(boot.end(), label, label + 11);
		const char *fsType = "FAT12   ";
		boot.insert(boot.end(), fsType, fsType + 8);
		boot.resize(510, 0);
		boot.push_back(0x55);
		boot.push_back(0xAA);
		std::memcpy(img.data(), boot.data(), boot.size());
	}

	/* ---- the allocation table ---- */
	std::vector<uint16_t> fat(kClusterCount + 2, 0);
	fat[0] = 0xFF0;
	fat[1] = 0xFFF;
	auto chain = [&](uint32_t first, uint32_t count) {
		for (uint32_t i = 0; i < count; i++)
			fat[first + i] = (i + 1 == count) ? 0xFFF : static_cast<uint16_t>(first + i + 1);
	};
	for (Node *d : dirs)
		if (d != &root) chain(d->firstCluster, d->clusters);
	for (Node *f : filesFlat)
		if (f->size) chain(f->firstCluster, f->clusters);

	{
		std::vector<uint8_t> packed(kSectorsPerFat * kSectorSize, 0);
		for (size_t i = 0; i + 1 < fat.size(); i += 2)
		{
			const uint32_t a = fat[i], b = fat[i + 1];
			const size_t at = (i * 3) / 2;
			if (at + 2 >= packed.size()) break;
			packed[at] = static_cast<uint8_t>(a & 0xFF);
			packed[at + 1] = static_cast<uint8_t>(((a >> 8) & 0x0F) | ((b & 0x0F) << 4));
			packed[at + 2] = static_cast<uint8_t>((b >> 4) & 0xFF);
		}
		for (uint32_t c = 0; c < kFatCount; c++)
			std::memcpy(img.data() + size_t(kReserved + c * kSectorsPerFat) * kSectorSize,
				packed.data(), packed.size());
	}

	auto clusterAt = [&](uint32_t cluster) {
		return img.data() + size_t(kFirstDataSector + (cluster - 2)) * kSectorSize;
	};

	/* ---- directories ---- */
	{
		std::vector<uint8_t> rootDir;
		for (const auto &k : root.kids)
			putEntry(rootDir, k->shortName, k->dir, k->firstCluster,
				k->dir ? 0 : static_cast<uint32_t>(k->size));
		rootDir.resize(size_t(kRootSectors) * kSectorSize, 0);
		std::memcpy(img.data() + size_t(kReserved + kFatCount * kSectorsPerFat) * kSectorSize,
			rootDir.data(), rootDir.size());
	}
	for (Node *d : dirs)
	{
		if (d == &root) continue;
		std::vector<uint8_t> v;
		putEntry(v, ".          ", true, d->firstCluster, 0);
		putEntry(v, "..         ", true, d->parent == &root ? 0 : d->parent->firstCluster, 0);
		for (const auto &k : d->kids)
			putEntry(v, k->shortName, k->dir, k->firstCluster,
				k->dir ? 0 : static_cast<uint32_t>(k->size));
		v.resize(size_t(d->clusters) * kSectorSize, 0);
		std::memcpy(clusterAt(d->firstCluster), v.data(), v.size());
	}

	/* ---- file data ---- */
	uint64_t bytesTotal = 0;
	for (const auto &e : files) bytesTotal += e.size;
	uint64_t bytesDone = 0;
	for (size_t i = 0; i < filesFlat.size(); i++)
	{
		Node *f = filesFlat[i];
		if (f->size == 0) continue;
		std::FILE *in = std::fopen(f->abs.c_str(), "rb");
		if (!in)
		{
			error = "cannot read " + f->name;
			return false;
		}
		const size_t got = std::fread(clusterAt(f->firstCluster), 1, static_cast<size_t>(f->size), in);
		std::fclose(in);
		if (got != f->size)
		{
			error = f->name + " ended early - it changed while it was being packed";
			return false;
		}
		bytesDone += got;
		if (progress && !progress(f->name.c_str(), bytesDone, bytesTotal, i + 1, filesFlat.size()))
		{
			error = "cancelled";
			return false;
		}
	}

	std::FILE *out = std::fopen(outPath.c_str(), "wb");
	if (!out)
	{
		error = "cannot create " + outPath;
		return false;
	}
	const bool wrote = std::fwrite(img.data(), 1, img.size(), out) == img.size();
	std::fclose(out);
	if (!wrote)
	{
		std::error_code ec;
		std::filesystem::remove(std::filesystem::u8path(outPath), ec);
		error = "cannot write the output (is the disk full?)";
		return false;
	}

	Sha1Stream sha;
	sha.update(img.data(), img.size());
	sha1Out = sha.finishHex();
	return true;
}

} // namespace chimera
