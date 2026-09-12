/* See media_maker.hpp. */
#include "media_maker.hpp"

#include "sha1.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <system_error>
#include <vector>

#include "../../../extern/miniz/miniz.h"

namespace chimera {
namespace {

/* 1980-01-01 00:00:00, in the two 16-bit fields MS-DOS gave zip and zip never
 * got rid of. The earliest a zip can express, so nothing has to decide what
 * "now" means. The same date the core packages have always been built with. */
constexpr uint16_t kDosTime = 0;
constexpr uint16_t kDosDate = (0 << 9) | (1 << 5) | 1;

/* Fixed, so a dump copied off a FAT drive and a dump sitting on ext4 pack the
 * same. Regular file, rw-r--r--: what the mode SAYS matters less than that it
 * says the same thing every time. */
constexpr uint32_t kExternalAttrs = 0100644u << 16;

constexpr uint64_t kCopyChunk = 1u << 20;
constexpr uint32_t kU32Max = 0xFFFFFFFFu;

void put16(std::vector<uint8_t> &v, uint16_t x)
{
	v.push_back(static_cast<uint8_t>(x));
	v.push_back(static_cast<uint8_t>(x >> 8));
}

void put32(std::vector<uint8_t> &v, uint32_t x)
{
	for (int i = 0; i < 4; i++) v.push_back(static_cast<uint8_t>(x >> (i * 8)));
}

void put64(std::vector<uint8_t> &v, uint64_t x)
{
	for (int i = 0; i < 8; i++) v.push_back(static_cast<uint8_t>(x >> (i * 8)));
}

/* The output, optionally hashed as it goes past.
 *
 * A writer that only ever moves forward gets its digest free, on the way out. A
 * writer that seeks back to patch a field it could not know in advance - which
 * is what a stored zip does with its CRCs - cannot, so it says so and pays for
 * a pass over the finished file instead. Hashing bytes that are about to be
 * overwritten would be worse than not hashing at all: it would be wrong. */
struct OutFile
{
	std::FILE *f = nullptr;
	Sha1Stream sha;
	bool hashing = true;
	uint64_t at = 0;

	bool open(const std::string &path)
	{
		f = std::fopen(path.c_str(), "wb");
		return f != nullptr;
	}
	bool write(const void *data, uint64_t len)
	{
		if (len == 0) return true;
		if (std::fwrite(data, 1, static_cast<size_t>(len), f) != len) return false;
		if (hashing) sha.update(static_cast<const uint8_t *>(data), len);
		at += len;
		return true;
	}
	bool write(const std::vector<uint8_t> &v) { return write(v.data(), v.size()); }
	void close()
	{
		if (f) std::fclose(f);
		f = nullptr;
	}
};

/* A zip member, remembered for the central directory. */
struct ZipPlaced
{
	std::string name;
	uint64_t size = 0;
	uint32_t crc = 0;
	uint64_t localOffset = 0;
	bool zip64 = false;
};

bool copyInto(OutFile &out, const MediaEntry &e, uint32_t &crcOut, uint64_t &bytesDone,
	uint64_t bytesTotal, uint64_t filesDone, uint64_t filesTotal, const MediaProgress &progress,
	std::string &error)
{
	std::FILE *in = std::fopen(e.abs.c_str(), "rb");
	if (!in)
	{
		error = "cannot read " + e.rel;
		return false;
	}
	std::vector<uint8_t> buf(static_cast<size_t>(kCopyChunk));
	uint32_t crc = MZ_CRC32_INIT;
	uint64_t left = e.size;
	bool ok = true;
	while (left != 0)
	{
		const size_t want = static_cast<size_t>(std::min<uint64_t>(buf.size(), left));
		const size_t got = std::fread(buf.data(), 1, want, in);
		if (got == 0)
		{
			/* The file shrank under us: the image would not match its own
			 * header, so it is not written at all. */
			error = e.rel + " ended early - it changed while it was being packed";
			ok = false;
			break;
		}
		crc = static_cast<uint32_t>(mz_crc32(crc, buf.data(), got));
		if (!out.write(buf.data(), got))
		{
			error = "cannot write the output (is the disk full?)";
			ok = false;
			break;
		}
		left -= got;
		bytesDone += got;
		if (progress && !progress(e.rel.c_str(), bytesDone, bytesTotal, filesDone, filesTotal))
		{
			error = "cancelled";
			ok = false;
			break;
		}
	}
	std::fclose(in);
	crcOut = crc;
	return ok;
}

} // namespace

bool mediaWriteZipStored(const std::vector<MediaEntry> &files, const std::string &outPath,
	const MediaProgress &progress, std::string &sha1Out, std::string &error)
{
	uint64_t bytesTotal = 0;
	for (const auto &e : files) bytesTotal += e.size;

	OutFile out;
	out.hashing = false; /* this writer patches CRCs behind itself; see OutFile */
	if (!out.open(outPath))
	{
		error = "cannot create " + outPath;
		return false;
	}

	std::vector<ZipPlaced> placed;
	placed.reserve(files.size());
	uint64_t bytesDone = 0;
	bool ok = true;

	for (size_t i = 0; i < files.size() && ok; i++)
	{
		const MediaEntry &e = files[i];
		/* zip64 per member, and only where it is needed: a member under 4 GiB in
		 * an archive that stays under 4 GiB is written the plain way, so the
		 * output opens in anything. */
		const bool big = e.size >= kU32Max || out.at >= kU32Max;

		ZipPlaced p;
		p.name = e.rel;
		p.size = e.size;
		p.localOffset = out.at;
		p.zip64 = big;

		std::vector<uint8_t> hdr;
		put32(hdr, 0x04034B50u);
		put16(hdr, big ? 45 : 20);       /* version needed */
		put16(hdr, 0);                   /* flags: none, so no data descriptor */
		put16(hdr, 0);                   /* method 0: STORED, read by seeking */
		put16(hdr, kDosTime);
		put16(hdr, kDosDate);
		/* The CRC is known only after the bytes have gone past, and a stored
		 * member may not use a data descriptor if it is to stay seekable - so
		 * the header is written, then rewritten in place once the file is
		 * copied. */
		const uint64_t crcFieldAt = out.at + hdr.size();
		put32(hdr, 0);
		if (big)
		{
			put32(hdr, kU32Max);
			put32(hdr, kU32Max);
		}
		else
		{
			put32(hdr, static_cast<uint32_t>(e.size));
			put32(hdr, static_cast<uint32_t>(e.size));
		}
		put16(hdr, static_cast<uint16_t>(p.name.size()));
		put16(hdr, big ? 20 : 0);
		hdr.insert(hdr.end(), p.name.begin(), p.name.end());
		if (big)
		{
			put16(hdr, 0x0001);
			put16(hdr, 16);
			put64(hdr, e.size);
			put64(hdr, e.size);
		}
		if (!out.write(hdr))
		{
			error = "cannot write the output (is the disk full?)";
			ok = false;
			break;
		}

		uint32_t crc = 0;
		ok = copyInto(out, e, crc, bytesDone, bytesTotal, i + 1, files.size(), progress, error);
		if (!ok) break;
		p.crc = crc;

		/* back to fill in the CRC, then on to where we were */
		const uint64_t resume = out.at;
		if (std::fseek(out.f, static_cast<long>(crcFieldAt), SEEK_SET) != 0)
		{
			error = "cannot seek in the output";
			ok = false;
			break;
		}
		uint8_t crcLE[4] = { static_cast<uint8_t>(crc), static_cast<uint8_t>(crc >> 8),
			static_cast<uint8_t>(crc >> 16), static_cast<uint8_t>(crc >> 24) };
		if (std::fwrite(crcLE, 1, 4, out.f) != 4 || std::fseek(out.f, 0, SEEK_END) != 0)
		{
			error = "cannot write the output";
			ok = false;
			break;
		}
		(void)resume;
		placed.push_back(std::move(p));
	}

	/* The CRCs were written by seeking backwards, so the running digest no
	 * longer describes the file. The archive is finished first and hashed by
	 * reading it back - the one place this costs a pass over the output. */
	if (ok)
	{
		const uint64_t cdOffset = out.at;
		std::vector<uint8_t> cd;
		for (const auto &p : placed)
		{
			const bool needOffset = p.localOffset >= kU32Max;
			const bool needSize = p.size >= kU32Max;
			const bool any64 = needOffset || needSize;
			put32(cd, 0x02014B50u);
			put16(cd, (3 << 8) | 45); /* made by: unix, 4.5 */
			put16(cd, any64 ? 45 : 20);
			put16(cd, 0);
			put16(cd, 0);
			put16(cd, kDosTime);
			put16(cd, kDosDate);
			put32(cd, p.crc);
			put32(cd, needSize ? kU32Max : static_cast<uint32_t>(p.size));
			put32(cd, needSize ? kU32Max : static_cast<uint32_t>(p.size));
			put16(cd, static_cast<uint16_t>(p.name.size()));
			const uint16_t extra = any64
				? static_cast<uint16_t>(4 + (needSize ? 16 : 0) + (needOffset ? 8 : 0))
				: 0;
			put16(cd, extra);
			put16(cd, 0); /* comment */
			put16(cd, 0); /* disk */
			put16(cd, 0); /* internal attrs */
			put32(cd, kExternalAttrs);
			put32(cd, needOffset ? kU32Max : static_cast<uint32_t>(p.localOffset));
			cd.insert(cd.end(), p.name.begin(), p.name.end());
			if (any64)
			{
				put16(cd, 0x0001);
				put16(cd, static_cast<uint16_t>((needSize ? 16 : 0) + (needOffset ? 8 : 0)));
				if (needSize)
				{
					put64(cd, p.size);
					put64(cd, p.size);
				}
				if (needOffset) put64(cd, p.localOffset);
			}
		}
		if (!out.write(cd))
		{
			error = "cannot write the output (is the disk full?)";
			ok = false;
		}

		if (ok)
		{
			const uint64_t cdSize = cd.size();
			const bool need64 = cdOffset >= kU32Max || cdSize >= kU32Max || placed.size() > 0xFFFF;
			std::vector<uint8_t> end;
			if (need64)
			{
				const uint64_t z64At = out.at;
				put32(end, 0x06064B50u);
				put64(end, 44); /* size of the rest of this record */
				put16(end, (3 << 8) | 45);
				put16(end, 45);
				put32(end, 0);
				put32(end, 0);
				put64(end, placed.size());
				put64(end, placed.size());
				put64(end, cdSize);
				put64(end, cdOffset);
				put32(end, 0x07064B50u);
				put32(end, 0);
				put64(end, z64At);
				put32(end, 1);
			}
			put32(end, 0x06054B50u);
			put16(end, 0);
			put16(end, 0);
			put16(end, static_cast<uint16_t>(std::min<size_t>(placed.size(), 0xFFFF)));
			put16(end, static_cast<uint16_t>(std::min<size_t>(placed.size(), 0xFFFF)));
			put32(end, cdSize >= kU32Max ? kU32Max : static_cast<uint32_t>(cdSize));
			put32(end, cdOffset >= kU32Max ? kU32Max : static_cast<uint32_t>(cdOffset));
			put16(end, 0);
			if (!out.write(end))
			{
				error = "cannot write the output (is the disk full?)";
				ok = false;
			}
		}
	}

	out.close();
	if (!ok)
	{
		std::error_code ec;
		std::filesystem::remove(std::filesystem::u8path(outPath), ec);
		return false;
	}

	uint64_t len = 0;
	if (!sha1HexOfFile(outPath.c_str(), &len, sha1Out))
	{
		error = "packed, but the output could not be hashed";
		return false;
	}
	return true;
}

bool mediaCollect(const std::string &folder, std::vector<MediaEntry> &out, std::string &error)
{
	namespace fs = std::filesystem;
	std::error_code ec;
	const fs::path root = fs::u8path(folder);
	if (!fs::is_directory(root, ec))
	{
		error = folder + " is not a folder";
		return false;
	}

	out.clear();
	fs::recursive_directory_iterator it(root, fs::directory_options::none, ec);
	if (ec)
	{
		error = "cannot read " + folder + ": " + ec.message();
		return false;
	}
	for (const auto &entry : it)
	{
		/* symlink_status, not status: a symlink is skipped rather than
		 * followed, because what it points at is not in this folder and
		 * following it would make the output depend on something outside. */
		const fs::file_status st = entry.symlink_status(ec);
		if (ec) continue;
		if (!fs::is_regular_file(st)) continue;

		MediaEntry e;
		e.abs = entry.path().u8string();
		std::string rel = fs::relative(entry.path(), root, ec).u8string();
		if (ec || rel.empty()) continue;
		for (char &c : rel)
			if (c == '\\') c = '/';
		e.rel = rel;
		e.size = static_cast<uint64_t>(fs::file_size(entry.path(), ec));
		if (ec) continue;
		out.push_back(std::move(e));
	}

	if (out.empty())
	{
		error = folder + " holds no files";
		return false;
	}

	/* Byte-wise, never the locale's idea of order: a machine set to a Turkish
	 * or Estonian collation must not pack a different file. */
	std::sort(out.begin(), out.end(), [](const MediaEntry &a, const MediaEntry &b) {
		return a.rel.compare(b.rel) < 0;
	});
	return true;
}

bool mediaMake(const std::string &folder, const std::string &outPath, MediaFormat format,
	const MediaProgress &progress, std::string &sha1Out, std::string &error)
{
	std::vector<MediaEntry> files;
	if (!mediaCollect(folder, files, error)) return false;

	switch (format)
	{
		case MediaFormat::ZipStored:
			return mediaWriteZipStored(files, outPath, progress, sha1Out, error);
		case MediaFormat::Iso9660:
			return mediaWriteIso9660(files, outPath, progress, sha1Out, error);
		case MediaFormat::Fat12:
			return mediaWriteFat12(files, outPath, progress, sha1Out, error);
	}
	error = "unknown format";
	return false;
}

} // namespace chimera
