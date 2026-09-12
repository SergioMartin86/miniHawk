/* media_iso.cpp - ISO 9660 with Joliet, written reproducibly.
 *
 * Why this format and not UDF, which is what a real PlayStation 3 disc is:
 * because it is what the emulator reads. rpcs3's Loader/ISO.cpp walks volume
 * descriptors at 2048-byte steps looking for type 1 (primary) and type 2
 * (Joliet), decodes UCS-2 names from the second, and strips a trailing ";1" or
 * "." from either. So an ISO 9660 image with a Joliet tree is exactly what it
 * expects, and a faithful UDF image would be work spent on a reader that is not
 * there.
 *
 * TWO TREES describe the same files. The primary one carries names ISO 9660
 * allows - uppercase, a short alphabet, a ";1" version - and exists because the
 * standard says a volume has one. The Joliet one carries the real names in
 * UCS-2, and is the tree anything modern actually reads. They are emitted from
 * one node tree so they cannot disagree about what is on the disc.
 *
 * REPRODUCIBLE the same way the zip writer is: one fixed date everywhere, one
 * fixed set of volume identifiers, and an order that comes from the bytes of
 * the names rather than from the filesystem or the locale. Nothing here asks
 * the clock. The output of two people packing the same dump is the same image,
 * which is the only reason to have written it.
 *
 * NOT HANDLED, deliberately and loudly: a file of 4 GiB or more. A directory
 * record's size field is 32 bits, and going past it means multi-extent records
 * that readers support unevenly - including, as far as can be told, that one.
 * Such a file is refused by name rather than written into an image that would
 * be wrong in a way nobody would notice until a game read the end of it.
 */
#include "media_maker.hpp"

#include "sha1.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <cctype>
#include <map>
#include <memory>
#include <string>
#include <system_error>
#include <vector>

namespace chimera {
namespace {

constexpr uint32_t kSector = 2048;
constexpr uint64_t k4GiB = 0xFFFFFFFFull;

/* 1980-01-01 00:00:00 UTC, everywhere a date is asked for. */
const uint8_t kRecDate[7] = { 80, 1, 1, 0, 0, 0, 0 };
const char *kVolDate = "1980010100000000";

uint32_t sectorsFor(uint64_t bytes)
{
	return static_cast<uint32_t>((bytes + kSector - 1) / kSector);
}

void put16le(std::vector<uint8_t> &v, uint16_t x)
{
	v.push_back(static_cast<uint8_t>(x));
	v.push_back(static_cast<uint8_t>(x >> 8));
}

void put32le(std::vector<uint8_t> &v, uint32_t x)
{
	for (int i = 0; i < 4; i++) v.push_back(static_cast<uint8_t>(x >> (i * 8)));
}

void put32be(std::vector<uint8_t> &v, uint32_t x)
{
	for (int i = 3; i >= 0; i--) v.push_back(static_cast<uint8_t>(x >> (i * 8)));
}

/* ECMA-119 stores the numbers that matter twice, once each way round, so a
 * reader of either endianness can pick the half it likes. */
void putBoth32(std::vector<uint8_t> &v, uint32_t x)
{
	put32le(v, x);
	put32be(v, x);
}

void putBoth16(std::vector<uint8_t> &v, uint16_t x)
{
	put16le(v, x);
	v.push_back(static_cast<uint8_t>(x >> 8));
	v.push_back(static_cast<uint8_t>(x));
}

void putStr(std::vector<uint8_t> &v, const std::string &s, size_t width, char pad = ' ')
{
	for (size_t i = 0; i < width; i++) v.push_back(i < s.size() ? static_cast<uint8_t>(s[i]) : static_cast<uint8_t>(pad));
}

/* A name the primary tree is allowed to carry. */
std::string isoName(const std::string &in, bool dir)
{
	std::string out;
	for (char c : in)
	{
		unsigned char u = static_cast<unsigned char>(c);
		if (u >= 'a' && u <= 'z') u = static_cast<unsigned char>(u - 'a' + 'A');
		const bool ok = (u >= 'A' && u <= 'Z') || (u >= '0' && u <= '9') || u == '_' || (!dir && u == '.');
		out.push_back(ok ? static_cast<char>(u) : '_');
	}
	if (out.empty()) out = "_";
	if (out.size() > 30) out.resize(30);
	if (!dir) out += ";1";
	return out;
}

std::u16string jolietName(const std::string &utf8)
{
	/* UTF-8 in, UCS-2 out. Anything outside the basic plane cannot be said in
	 * UCS-2 at all and becomes U+FFFD, which is at least the same every time. */
	std::u16string out;
	size_t i = 0;
	while (i < utf8.size())
	{
		const unsigned char c = static_cast<unsigned char>(utf8[i]);
		uint32_t cp = 0;
		size_t len = 1;
		if (c < 0x80) { cp = c; len = 1; }
		else if ((c & 0xE0) == 0xC0) { cp = c & 0x1Fu; len = 2; }
		else if ((c & 0xF0) == 0xE0) { cp = c & 0x0Fu; len = 3; }
		else { cp = 0xFFFD; len = 4; }
		if (i + len > utf8.size()) { cp = 0xFFFD; len = utf8.size() - i; }
		else
			for (size_t k = 1; k < len && cp != 0xFFFD; k++)
				cp = (cp << 6) | (static_cast<unsigned char>(utf8[i + k]) & 0x3Fu);
		if (cp > 0xFFFF) cp = 0xFFFD;
		out.push_back(static_cast<char16_t>(cp));
		i += len;
	}
	/* Joliet's limit. Truncation is deterministic, and uniqueness is settled
	 * afterwards by the same counter the primary tree uses. */
	if (out.size() > 64) out.resize(64);
	return out;
}

struct Node
{
	std::string name;  /* as it was on disk */
	bool dir = false;
	uint64_t size = 0;    /* file bytes */
	std::string abs;      /* file source */
	std::vector<std::unique_ptr<Node>> kids;
	Node *parent = nullptr;

	std::string iso;       /* primary identifier */
	std::u16string joliet; /* Joliet identifier */

	uint32_t lba = 0;        /* file data, or primary directory extent */
	uint32_t jolietLba = 0;  /* Joliet directory extent */
	uint32_t dirBytes = 0;
	uint32_t jolietBytes = 0;
	uint32_t pathIndex = 0;  /* 1-based, for the path tables */
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

/* One record's length: the fixed 33 bytes, the identifier, and a pad byte when
 * that lands odd. */
uint32_t recordLen(size_t idLen)
{
	uint32_t n = 33 + static_cast<uint32_t>(idLen);
	if (n & 1) n++;
	return n;
}

uint32_t dirExtentBytes(const Node &d, bool joliet)
{
	/* "." and ".." are one byte of identifier each */
	uint32_t at = 0;
	auto add = [&](uint32_t len) {
		if ((at % kSector) + len > kSector) at += kSector - (at % kSector);
		at += len;
	};
	add(recordLen(1));
	add(recordLen(1));
	for (const auto &k : d.kids)
		add(recordLen(joliet ? k->joliet.size() * 2 : k->iso.size()));
	return sectorsFor(at) * kSector;
}

void emitRecord(std::vector<uint8_t> &v, uint32_t lba, uint64_t size, bool isDir,
	const void *id, size_t idLen)
{
	const uint32_t len = recordLen(idLen);
	/* A record may not straddle a sector: pad to the next one instead. */
	if ((v.size() % kSector) + len > kSector) v.resize(v.size() + (kSector - (v.size() % kSector)), 0);
	const size_t start = v.size();
	v.push_back(static_cast<uint8_t>(len));
	v.push_back(0); /* extended attribute length */
	putBoth32(v, lba);
	putBoth32(v, static_cast<uint32_t>(size));
	for (int i = 0; i < 7; i++) v.push_back(kRecDate[i]);
	v.push_back(isDir ? 0x02 : 0x00); /* flags */
	v.push_back(0);                   /* file unit size */
	v.push_back(0);                   /* interleave gap */
	putBoth16(v, 1);                  /* volume sequence number */
	v.push_back(static_cast<uint8_t>(idLen));
	const uint8_t *p = static_cast<const uint8_t *>(id);
	v.insert(v.end(), p, p + idLen);
	if (v.size() - start < len) v.push_back(0);
}

void emitDirExtent(std::vector<uint8_t> &v, const Node &d, bool joliet, uint32_t selfLba,
	uint32_t parentLba, uint32_t selfBytes, uint32_t parentBytes)
{
	const size_t base = v.size();
	const uint8_t dot = 0x00, dotdot = 0x01;
	emitRecord(v, selfLba, selfBytes, true, &dot, 1);
	emitRecord(v, parentLba, parentBytes, true, &dotdot, 1);
	for (const auto &k : d.kids)
	{
		const uint32_t lba = k->dir ? (joliet ? k->jolietLba : k->lba) : k->lba;
		const uint64_t size = k->dir ? (joliet ? k->jolietBytes : k->dirBytes) : k->size;
		if (joliet)
		{
			std::vector<uint8_t> id;
			for (char16_t c : k->joliet)
			{
				id.push_back(static_cast<uint8_t>(c >> 8));
				id.push_back(static_cast<uint8_t>(c));
			}
			emitRecord(v, lba, size, k->dir, id.data(), id.size());
		}
		else
		{
			emitRecord(v, lba, size, k->dir, k->iso.data(), k->iso.size());
		}
	}
	/* to the end of the extent */
	const size_t want = base + (joliet ? d.jolietBytes : d.dirBytes);
	if (v.size() < want) v.resize(want, 0);
}

} // namespace

bool mediaWriteIso9660(const std::vector<MediaEntry> &files, const std::string &outPath,
	const MediaProgress &progress, std::string &sha1Out, std::string &error)
{
	for (const auto &e : files)
	{
		if (e.size >= k4GiB)
		{
			error = e.rel + " is 4 GiB or larger, which an ISO 9660 directory record cannot "
				"describe in one piece. Pack this dump as a .zip instead, which has no such "
				"limit.";
			return false;
		}
	}

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

	/* A PlayStation 3 disc announces itself with PS3_DISC.SFB beside PS3_GAME,
	 * which is the same shape the rpcs3 core looks for in an archive. */
	bool isPs3Disc = false;
	for (const auto &k : root.kids)
	{
		std::string upper = k->name;
		for (char &c : upper) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
		if (!k->dir && upper == "PS3_DISC.SFB") isPs3Disc = true;
	}

	/* names, then the order the records go in - both settled here so that
	 * nothing downstream depends on how the directory was read */
	std::vector<Node *> dirs;
	std::vector<Node *> filesFlat;
	{
		std::vector<Node *> stack{ &root };
		while (!stack.empty())
		{
			Node *d = stack.back();
			stack.pop_back();
			dirs.push_back(d);

			std::map<std::string, int> seenIso;
			std::map<std::u16string, int> seenJol;
			std::sort(d->kids.begin(), d->kids.end(),
				[](const std::unique_ptr<Node> &a, const std::unique_ptr<Node> &b) {
					if (a->dir != b->dir) return a->dir > b->dir;
					return a->name.compare(b->name) < 0;
				});
			for (auto &k : d->kids)
			{
				k->iso = isoName(k->name, k->dir);
				k->joliet = jolietName(k->name);
				/* A mangled name can collide with another mangled name. The
				 * loser gets a counter, chosen by the order above, so the same
				 * folder always produces the same disambiguation. */
				if (int &n = seenIso[k->iso]; ++n > 1)
				{
					const std::string suffix = "_" + std::to_string(n);
					std::string base = k->iso;
					const size_t semi = base.rfind(";1");
					if (semi != std::string::npos) base.resize(semi);
					if (base.size() + suffix.size() > 30) base.resize(30 - suffix.size());
					k->iso = base + suffix + (k->dir ? "" : ";1");
				}
				if (int &n = seenJol[k->joliet]; ++n > 1)
				{
					const std::u16string suffix = u"_" + std::u16string(1, static_cast<char16_t>('0' + (n % 10)));
					if (k->joliet.size() + suffix.size() > 64) k->joliet.resize(64 - suffix.size());
					k->joliet += suffix;
				}
				if (k->dir) stack.push_back(k.get());
				else filesFlat.push_back(k.get());
			}
		}
		/* Path-table order: by level, then by parent, then by name. Sorting the
		 * directory list the same way every time is what makes the tables - and
		 * so the image - reproducible. */
		std::stable_sort(dirs.begin(), dirs.end(), [](Node *a, Node *b) {
			auto depth = [](Node *n) { int d = 0; for (Node *p = n->parent; p; p = p->parent) d++; return d; };
			const int da = depth(a), db = depth(b);
			if (da != db) return da < db;
			return a->name.compare(b->name) < 0;
		});
		for (size_t i = 0; i < dirs.size(); i++) dirs[i]->pathIndex = static_cast<uint32_t>(i + 1);
	}

	for (Node *d : dirs)
	{
		d->dirBytes = dirExtentBytes(*d, false);
		d->jolietBytes = dirExtentBytes(*d, true);
	}

	/* ---- path tables ---- */
	auto buildPathTable = [&](bool joliet, bool bigEndian) {
		std::vector<uint8_t> t;
		for (Node *d : dirs)
		{
			std::vector<uint8_t> id;
			if (d == &root) id.push_back(0);
			else if (joliet)
				for (char16_t c : d->joliet)
				{
					id.push_back(static_cast<uint8_t>(c >> 8));
					id.push_back(static_cast<uint8_t>(c));
				}
			else
				id.assign(d->iso.begin(), d->iso.end());
			t.push_back(static_cast<uint8_t>(id.size()));
			t.push_back(0);
			const uint32_t lba = joliet ? d->jolietLba : d->lba;
			const uint16_t parent = static_cast<uint16_t>(d->parent ? d->parent->pathIndex : 1);
			if (bigEndian)
			{
				put32be(t, lba);
				t.push_back(static_cast<uint8_t>(parent >> 8));
				t.push_back(static_cast<uint8_t>(parent));
			}
			else
			{
				put32le(t, lba);
				put16le(t, parent);
			}
			t.insert(t.end(), id.begin(), id.end());
			if (id.size() & 1) t.push_back(0);
		}
		return t;
	};

	/* ---- the layout ----
	 * Sizes first, then addresses, then the bytes: a volume descriptor has to
	 * state the size of the whole image, which is only known once everything
	 * has been placed. */
	uint32_t at = 19; /* 16 system + PVD + SVD + terminator */
	const std::vector<uint8_t> ptProbe = buildPathTable(false, false);
	const std::vector<uint8_t> jptProbe = buildPathTable(true, false);
	const uint32_t ptSectors = sectorsFor(ptProbe.size());
	const uint32_t jptSectors = sectorsFor(jptProbe.size());
	const uint32_t ptL = at; at += ptSectors;
	const uint32_t ptM = at; at += ptSectors;
	const uint32_t jptL = at; at += jptSectors;
	const uint32_t jptM = at; at += jptSectors;
	for (Node *d : dirs) { d->lba = at; at += d->dirBytes / kSector; }
	for (Node *d : dirs) { d->jolietLba = at; at += d->jolietBytes / kSector; }
	for (Node *f : filesFlat)
	{
		f->lba = at;
		at += sectorsFor(f->size);
		/* a zero-length file still needs an address; it just occupies nothing */
		if (f->size == 0) f->lba = at;
	}
	const uint32_t totalSectors = at;

	const std::vector<uint8_t> ptLData = buildPathTable(false, false);
	const std::vector<uint8_t> ptMData = buildPathTable(false, true);
	const std::vector<uint8_t> jptLData = buildPathTable(true, false);
	const std::vector<uint8_t> jptMData = buildPathTable(true, true);

	/* ---- write ---- */
	std::FILE *out = std::fopen(outPath.c_str(), "wb");
	if (!out)
	{
		error = "cannot create " + outPath;
		return false;
	}
	Sha1Stream sha;
	uint64_t written = 0;
	bool ok = true;

	auto emit = [&](const void *data, size_t len) {
		if (!ok) return;
		if (len && std::fwrite(data, 1, len, out) != len)
		{
			error = "cannot write the output (is the disk full?)";
			ok = false;
			return;
		}
		sha.update(static_cast<const uint8_t *>(data), len);
		written += len;
	};
	auto padTo = [&](uint64_t bytes) {
		static const uint8_t zeros[kSector] = { 0 };
		while (ok && written < bytes)
			emit(zeros, static_cast<size_t>(std::min<uint64_t>(kSector, bytes - written)));
	};

	/* ---- the system area, and the PS3 region table in it ----
	 *
	 * Sectors 0-15 are reserved by ISO 9660 and mean nothing to it, which is
	 * where a PlayStation 3 disc keeps the table that says which parts of it are
	 * encrypted. rpcs3 does not treat that as optional: Loader/ISO.cpp reads a
	 * big-endian region count out of the first four bytes and refuses anything
	 * with a count below 1 as "non-PS3ISO", which reaches the user as "Invalid
	 * file or folder" with nothing about a region table in it.
	 *
	 * An image built from a decrypted folder dump has exactly one region and it
	 * is not encrypted, so that is what is written: count 1, and one region
	 * ending at the last sector. Everything rpcs3 does after that check is
	 * non-fatal - the Redump and 3k3y probes find nothing, leave the encryption
	 * type NONE, and reads pass through untouched.
	 *
	 * Only for a disc that looks like a PS3 one. Another console's image gets the
	 * zeros it has always had, because this table would be meaningless there. */
	if (isPs3Disc)
	{
		std::vector<uint8_t> sector0(kSector, 0);
		const uint32_t lastSector = totalSectors - 1;
		for (int i = 0; i < 4; i++)
		{
			sector0[i] = static_cast<uint8_t>(1u >> ((3 - i) * 8));           /* region count */
			sector0[12 + i] = static_cast<uint8_t>(lastSector >> ((3 - i) * 8));
		}
		emit(sector0.data(), sector0.size());
	}
	padTo(16ull * kSector); /* the rest of the system area */

	auto volumeDescriptor = [&](uint8_t type, bool joliet) {
		std::vector<uint8_t> v;
		v.push_back(type);
		putStr(v, "CD001", 5);
		v.push_back(1); /* version */
		v.push_back(0); /* unused / volume flags */
		putStr(v, "", 32);             /* system identifier */
		if (joliet)
		{
			/* UCS-2 for the volume identifier too */
			std::vector<uint8_t> id;
			for (char c : std::string("CHIMERA")) { id.push_back(0); id.push_back(static_cast<uint8_t>(c)); }
			id.resize(32, ' ');
			v.insert(v.end(), id.begin(), id.end());
		}
		else
			putStr(v, "CHIMERA", 32);
		for (int i = 0; i < 8; i++) v.push_back(0);
		putBoth32(v, totalSectors);
		if (joliet)
		{
			/* escape sequence: UCS-2 level 3 */
			std::vector<uint8_t> esc(32, 0);
			esc[0] = '%'; esc[1] = '/'; esc[2] = 'E';
			v.insert(v.end(), esc.begin(), esc.end());
		}
		else
			for (int i = 0; i < 32; i++) v.push_back(0);
		putBoth16(v, 1); /* volume set size */
		putBoth16(v, 1); /* volume sequence number */
		putBoth16(v, static_cast<uint16_t>(kSector));
		putBoth32(v, static_cast<uint32_t>(joliet ? jptLData.size() : ptLData.size()));
		put32le(v, joliet ? jptL : ptL);
		put32le(v, 0);
		put32be(v, joliet ? jptM : ptM);
		put32be(v, 0);
		/* the root directory record, in line */
		std::vector<uint8_t> rootRec;
		const uint8_t dot = 0;
		emitRecord(rootRec, joliet ? root.jolietLba : root.lba,
			joliet ? root.jolietBytes : root.dirBytes, true, &dot, 1);
		rootRec.resize(34, 0);
		v.insert(v.end(), rootRec.begin(), rootRec.end());
		putStr(v, "", 128); /* volume set identifier */
		putStr(v, "", 128); /* publisher */
		putStr(v, "", 128); /* data preparer */
		putStr(v, "CHIMERA REPRODUCIBLE MEDIA MAKER", 128);
		putStr(v, "", 37);
		putStr(v, "", 37);
		putStr(v, "", 37);
		for (int d = 0; d < 4; d++)
		{
			putStr(v, kVolDate, 16);
			v.push_back(0);
		}
		v.push_back(1); /* file structure version */
		v.push_back(0);
		v.resize(kSector, 0);
		emit(v.data(), v.size());
	};

	volumeDescriptor(1, false);
	volumeDescriptor(2, true);
	{
		std::vector<uint8_t> term;
		term.push_back(255);
		putStr(term, "CD001", 5);
		term.push_back(1);
		term.resize(kSector, 0);
		emit(term.data(), term.size());
	}

	auto emitPadded = [&](const std::vector<uint8_t> &data, uint32_t sectors) {
		emit(data.data(), data.size());
		padTo(written + (static_cast<uint64_t>(sectors) * kSector - data.size()));
	};
	emitPadded(ptLData, ptSectors);
	emitPadded(ptMData, ptSectors);
	emitPadded(jptLData, jptSectors);
	emitPadded(jptMData, jptSectors);

	for (bool joliet : { false, true })
	{
		for (Node *d : dirs)
		{
			if (!ok) break;
			std::vector<uint8_t> v;
			Node *p = d->parent ? d->parent : d;
			emitDirExtent(v, *d, joliet, joliet ? d->jolietLba : d->lba,
				joliet ? p->jolietLba : p->lba, joliet ? d->jolietBytes : d->dirBytes,
				joliet ? p->jolietBytes : p->dirBytes);
			emit(v.data(), v.size());
		}
	}

	uint64_t bytesTotal = 0;
	for (const auto &e : files) bytesTotal += e.size;
	uint64_t bytesDone = 0;
	std::vector<uint8_t> buf(1u << 20);
	for (size_t i = 0; i < filesFlat.size() && ok; i++)
	{
		Node *f = filesFlat[i];
		std::FILE *in = std::fopen(f->abs.c_str(), "rb");
		if (!in)
		{
			error = "cannot read " + f->name;
			ok = false;
			break;
		}
		uint64_t left = f->size;
		while (left != 0 && ok)
		{
			const size_t want = static_cast<size_t>(std::min<uint64_t>(buf.size(), left));
			const size_t got = std::fread(buf.data(), 1, want, in);
			if (got == 0)
			{
				error = f->name + " ended early - it changed while it was being packed";
				ok = false;
				break;
			}
			emit(buf.data(), got);
			left -= got;
			bytesDone += got;
			if (ok && progress && !progress(f->name.c_str(), bytesDone, bytesTotal,
					i + 1, filesFlat.size()))
			{
				error = "cancelled";
				ok = false;
			}
		}
		std::fclose(in);
		if (ok) padTo(sectorsFor(written) * static_cast<uint64_t>(kSector));
	}

	if (ok) padTo(static_cast<uint64_t>(totalSectors) * kSector);
	std::fclose(out);

	if (!ok)
	{
		std::error_code ec;
		std::filesystem::remove(std::filesystem::u8path(outPath), ec);
		return false;
	}
	sha1Out = sha.finishHex();
	return true;
}

} // namespace chimera
