#include "state_history.hpp"

#include <algorithm>
#include <cstdio>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <new>

namespace chimera
{

namespace
{

/* a note is a caller's few bytes about a frame; anything claiming more is damage */
const uint64_t kMaxNote = 4096;

/* the stream shims the host's callbacks want */
struct ByteSink { std::vector<uint8_t> *out; };
int32_t sinkWrite(uintptr_t ud, const void *data, uintptr_t len)
{
	auto *s = reinterpret_cast<ByteSink *>(ud);
	const auto *p = static_cast<const uint8_t *>(data);
	s->out->insert(s->out->end(), p, p + len);
	return 0;
}

struct ByteSource { const uint8_t *data; uintptr_t len, pos; };
intptr_t sourceRead(uintptr_t ud, void *out, uintptr_t len)
{
	auto *s = reinterpret_cast<ByteSource *>(ud);
	uintptr_t n = s->len - s->pos;
	if (n > len) n = len;
	if (n == 0) return -1;
	std::memcpy(out, s->data + s->pos, n);
	s->pos += n;
	return static_cast<intptr_t>(n);
}

/* fseek and ftell take a long, which is 32 bits on Windows - and the spill file
 * is the one thing here designed to pass two gigabytes. */
#ifdef _WIN32
bool seekTo(std::FILE *f, uint64_t at) { return _fseeki64(f, static_cast<__int64>(at), SEEK_SET) == 0; }
bool seekBy(std::FILE *f, uint64_t by) { return _fseeki64(f, static_cast<__int64>(by), SEEK_CUR) == 0; }
bool seekEnd(std::FILE *f) { return _fseeki64(f, 0, SEEK_END) == 0; }
bool tellAt(std::FILE *f, uint64_t &at)
{
	const __int64 n = _ftelli64(f);
	if (n < 0) return false;
	at = static_cast<uint64_t>(n);
	return true;
}
#else
bool seekTo(std::FILE *f, uint64_t at) { return fseeko(f, static_cast<off_t>(at), SEEK_SET) == 0; }
bool seekBy(std::FILE *f, uint64_t by) { return fseeko(f, static_cast<off_t>(by), SEEK_CUR) == 0; }
bool seekEnd(std::FILE *f) { return fseeko(f, 0, SEEK_END) == 0; }
bool tellAt(std::FILE *f, uint64_t &at)
{
	const off_t n = ftello(f);
	if (n < 0) return false;
	at = static_cast<uint64_t>(n);
	return true;
}
#endif

bool writeAll(std::FILE *f, const void *data, size_t n)
{
	return n == 0 || std::fwrite(data, 1, n, f) == n;
}

bool readAll(std::FILE *f, void *data, size_t n)
{
	return n == 0 || std::fread(data, 1, n, f) == n;
}

bool writeU64(std::FILE *f, uint64_t v) { return writeAll(f, &v, sizeof v); }
bool readU64(std::FILE *f, uint64_t &v) { return readAll(f, &v, sizeof v); }

/* Reads at most `left` bytes from a file, for handing a spilled anchor or link
 * straight to the sandbox without a copy of it in between. */
struct FileSource
{
	std::FILE *f;
	uint64_t left;
};

intptr_t fileRead(uintptr_t ud, void *out, uintptr_t len)
{
	FileSource *s = reinterpret_cast<FileSource *>(ud);
	if (len > s->left) len = static_cast<uintptr_t>(s->left);
	if (len == 0) return -1;
	const size_t got = std::fread(out, 1, len, s->f);
	if (got == 0) return -1;
	s->left -= got;
	return static_cast<intptr_t>(got);
}

/* Skips forward without reading, for a part of a segment this pass does not
 * want - a note, or the tail of an anchor the sandbox stopped reading early. */
bool skipBy(std::FILE *f, uint64_t n) { return seekBy(f, n); }

/* CHIMERA_HISTORY_TRACE=1 says what the history stored and what it cost. A
 * delta silently falling back to a whole state is the failure mode with no
 * symptom - everything still works, it just costs a hundred times more - so
 * there has to be a way to look. */
double nowSeconds()
{
	/* steady_clock rather than clock_gettime, which mingw does not have - this
	 * is only ever read under CHIMERA_HISTORY_TRACE, but a diagnostic that
	 * fails to link on the platform people run is worse than no diagnostic. */
	const auto t = std::chrono::steady_clock::now().time_since_epoch();
	return std::chrono::duration<double>(t).count();
}

bool historyTrace()
{
	static const int on = [] {
		const char *e = getenv("CHIMERA_HISTORY_TRACE");
		return e != nullptr && e[0] != '\0' && e[0] != '0' ? 1 : 0;
	}();
	return on != 0;
}

} // namespace

void StateHistory::configure(const HostApi *host, void *obj, uint64_t budgetBytes)
{
	m_host = host;
	m_obj = obj;
	m_budget = budgetBytes;
	m_segments.clear();
	m_bytes = 0;
	m_epochOpen = false;
}

void StateHistory::clear()
{
	m_segments.clear();
	m_bytes = 0;
	m_epochOpen = false;
	m_newest = -1;
	m_settle = Settling{};
	dropSpillFile();
}

/* CHIMERA_NO_DELTAS=1 keeps whole states, as the greenzone did before epochs.
 * It is here for the same reason CHIMERA_NO_GPU is: two things that fail
 * differently should be tellable apart on a machine that is not here, and a
 * regression in capture cost or in seek latency wants an A against its B on
 * the same build. */
static bool deltasRefused()
{
	static const int off = [] {
		const char *e = getenv("CHIMERA_NO_DELTAS");
		return e != nullptr && e[0] != '\0' && e[0] != '0' ? 1 : 0;
	}();
	return off != 0;
}

/* One layout, used by the spill file and by a saved history alike - which is
 * what lets a saved history copy a spilled segment through byte for byte
 * instead of rebuilding it:
 *
 *   anchor: length, bytes, note length, note
 *   links:  count, then per link: the frame it lands on, note length, note,
 *           length, bytes
 */
bool StateHistory::writeSegmentBody(std::FILE *f, const Segment &seg)
{
	bool ok = writeU64(f, seg.anchor.size())
		&& writeAll(f, seg.anchor.data(), seg.anchor.size())
		&& writeU64(f, seg.anchorNote.size())
		&& writeAll(f, seg.anchorNote.data(), seg.anchorNote.size())
		&& writeU64(f, seg.links.size());
	for (const Link &l : seg.links)
	{
		if (!ok) break;
		ok = writeU64(f, static_cast<uint64_t>(l.endFrame))
			&& writeU64(f, l.note.size())
			&& writeAll(f, l.note.data(), l.note.size())
			&& writeU64(f, l.bytes.size())
			&& writeAll(f, l.bytes.data(), l.bytes.size());
	}
	return ok;
}

StateHistory::~StateHistory()
{
	dropSpillFile();
}

void StateHistory::spillTo(const char *dir)
{
	const std::string next = dir != nullptr ? dir : "";
	if (next == m_spillDir) return;
	/* Whatever is out there belongs to the old directory, and the segments
	 * pointing at it are now unreadable - so they go, which costs replaying. */
	dropSpillFile();
	for (size_t i = m_segments.size(); i-- > 0; )
	{
		if (m_segments[i].spilled) forgetSegment(i);
	}
	m_spillDir = next;
	m_spillFailed = false;   /* a new directory is a fresh chance at it */
}

/* True when anything between the anchor and the last landing is pinned - the
 * question eviction asks before throwing a stretch away. */
static bool holdsPinned(const std::set<int64_t> &pins, int64_t from, int64_t to)
{
	const auto it = pins.lower_bound(from);
	return it != pins.end() && *it <= to;
}

/* The number given is what the FILE may weigh, because that is the number
 * somebody watching a disk fill up cares about and the only one they can check.
 *
 * What is kept reachable is half of it. A compaction copies every live byte, so
 * doing one per drop would copy the same bytes over and over; waiting until the
 * dead part is the bigger part makes it one copy per byte written, amortised,
 * and means the file sits between the live total and twice it. Half the number
 * for live is what turns that into a promise that can be read off `ls`. */
void StateHistory::diskBudget(uint64_t bytes)
{
	m_diskBudget = bytes / 2;
	evictDisk();
}

/* Drops the oldest stretches in the spill file until it is under its budget,
 * then reclaims the room they held.
 *
 * The newest is spared here as it is in memory - it is where the work is - and
 * a stretch somebody pinned a frame in is spared too, for the same reason
 * eviction spares it: a pin is a promise that frame can still be reached.
 */
void StateHistory::evictDisk()
{
	if (m_diskBudget == 0) return;
	bool dropped = false;
	while (m_spillLive > m_diskBudget)
	{
		size_t victim = m_segments.size();
		for (size_t i = 0; i + 1 < m_segments.size(); i++)
		{
			if (!m_segments[i].spilled) continue;
			if (holdsPinned(m_pinned, m_segments[i].anchorFrame, m_segments[i].lastFrame())) continue;
			victim = i;
			break;
		}
		if (victim == m_segments.size()) break;   /* nothing left that may go */
		forgetSegment(victim);
		dropped = true;
	}
	if (dropped) compactSpill();
}

/* Moves what is still live to the front of the file, so the room the dropped
 * stretches held is actually given back. Only when the dead part is the bigger
 * part: a compaction copies every live byte, and doing it at every drop would
 * copy the same bytes over and over.
 *
 * A failure here is not an error. The file stays as it was and so do the
 * offsets in it; what is lost is the room, until the next drop asks again. */
bool StateHistory::compactSpill()
{
	if (m_spill == nullptr) return false;
	if (m_spillLive == 0)
	{
		/* nothing of it is wanted: the cheapest compaction there is */
		std::rewind(m_spill);
		m_spillBytes = 0;
		return true;
	}
	if (m_spillBytes - m_spillLive < m_spillLive) return false;   /* dead half is the smaller half */

	const std::string path = m_spillDir + "/history-spill.bin";
	const std::string tmp = path + ".compacting";
	std::FILE *out = std::fopen(tmp.c_str(), "w+b");
	if (out == nullptr) return false;

	std::vector<uint8_t> buf;
	uint64_t at = 0;
	for (Segment &seg : m_segments)
	{
		if (!seg.spilled) continue;
		buf.resize(static_cast<size_t>(seg.spillLength));
		if (!seekTo(m_spill, seg.spillAt) || !readAll(m_spill, buf.data(), buf.size())
			|| !writeAll(out, buf.data(), buf.size()))
		{
			std::fclose(out);
			std::remove(tmp.c_str());
			return false;
		}
		seg.spillAt = at;
		at += seg.spillLength;
	}
	if (std::fflush(out) != 0)
	{
		std::fclose(out);
		std::remove(tmp.c_str());
		return false;
	}

	std::fclose(m_spill);
	m_spill = nullptr;
	if (std::rename(tmp.c_str(), path.c_str()) != 0)
	{
		/* the old file is still there and still right; reopen it and give up */
		std::fclose(out);
		std::remove(tmp.c_str());
		m_spill = std::fopen(path.c_str(), "r+b");
		return false;
	}
	std::fclose(out);
	m_spill = std::fopen(path.c_str(), "r+b");
	if (m_spill == nullptr) return false;
	m_spillBytes = at;
	if (historyTrace())
	{
		fprintf(stderr, "[history] compacted the spill file to %llu bytes\n",
			(unsigned long long)m_spillBytes);
		fflush(stderr);
	}
	return true;
}

void StateHistory::dropSpillFile()
{
	if (m_spill != nullptr)
	{
		std::fclose(m_spill);
		m_spill = nullptr;
		const std::string path = m_spillDir + "/history-spill.bin";
		std::remove(path.c_str());
	}
	m_spillBytes = 0;
	m_spillLive = 0;
}

void StateHistory::bands(int64_t nearFrames, int64_t midFrames, int64_t midStride,
	int64_t farStride, int64_t anchorSpacing)
{
	if (nearFrames > 0) m_nearFrames = nearFrames;
	if (midFrames > 0) m_midFrames = midFrames;
	if (midStride > 0) m_midStride = midStride;
	if (farStride > 0) m_farStride = farStride;
	if (anchorSpacing > 0) m_anchorSpacing = anchorSpacing;
	/* A band cannot be denser than the one nearer the playhead: the landings
	 * are a grid per band, and a coarser grid inside a finer one would keep
	 * asking for landings the band before it has already merged away. */
	if (m_farStride < m_midStride) m_farStride = m_midStride;
}

bool StateHistory::composeAvailable() const
{
	return m_host != nullptr && m_host->wbx_compose_delta != nullptr;
}

bool StateHistory::deltasAvailable() const
{
	return !deltasRefused() && m_host != nullptr && m_host->wbx_epoch_begin != nullptr
		&& m_host->wbx_save_delta != nullptr && m_host->wbx_load_delta != nullptr;
}

/* Links land on strictly ascending frames, so both of these are searches
 * rather than walks - a segment near the playhead is thousands of links long. */
int64_t StateHistory::Segment::nearestIn(int64_t f) const
{
	if (f < anchorFrame) return -1;
	if (f >= lastFrame()) return lastFrame();
	const auto it = std::upper_bound(links.begin(), links.end(), f,
		[](int64_t v, const Link &l) { return v < l.endFrame; });
	return it == links.begin() ? anchorFrame : (it - 1)->endFrame;
}

int64_t StateHistory::Segment::stepsTo(int64_t f) const
{
	if (f == anchorFrame) return 0;
	if (f < anchorFrame || f > lastFrame()) return -1;
	const auto it = std::lower_bound(links.begin(), links.end(), f,
		[](const Link &l, int64_t v) { return l.endFrame < v; });
	if (it == links.end() || it->endFrame != f) return -1;
	return static_cast<int64_t>(it - links.begin()) + 1;
}

int64_t StateHistory::count() const
{
	int64_t n = 0;
	for (const Segment &s : m_segments) n += 1 + static_cast<int64_t>(s.links.size());
	return n;
}

const uint8_t *StateHistory::noteFor(int64_t frame, size_t &lenOut) const
{
	lenOut = 0;
	for (const Segment &seg : m_segments)
	{
		if (seg.anchorFrame > frame) break;
		if (seg.anchorFrame == frame)
		{
			lenOut = seg.anchorNote.size();
			return seg.anchorNote.empty() ? nullptr : seg.anchorNote.data();
		}
		const int64_t steps = seg.stepsTo(frame);
		if (steps <= 0) continue;
		const Link &l = seg.links[static_cast<size_t>(steps) - 1];
		lenOut = l.note.size();
		return l.note.empty() ? nullptr : l.note.data();
	}
	return nullptr;
}

int64_t StateHistory::nearest(int64_t frame) const
{
	int64_t best = -1;
	for (const Segment &s : m_segments)
	{
		if (s.anchorFrame > frame) break;              /* ordered: nothing later helps */
		const int64_t here = s.nearestIn(frame);
		if (here > best) best = here;
	}
	return best;
}

void StateHistory::beforeAdvance()
{
	m_epochOpen = false;
	if (!enabled() || !deltasAvailable()) return;
	/* A delta continues the newest segment, and only while there is one with
	 * room. Otherwise the coming capture is an anchor and needs no epoch. */
	if (m_segments.empty()) return;
	const Segment &seg = m_segments.back();
	if (seg.lastFrame() - seg.anchorFrame >= m_anchorSpacing) return;
	WbxReturn r{};
	m_host->wbx_epoch_begin(m_obj, &r);
	m_epochOpen = r.ok();
}

/* A capture allocates - a whole machine for an anchor, a frame's churn for a
 * delta - and on a machine under pressure that allocation is where Chimera
 * meets the end of memory first, because the history is the biggest thing it
 * holds that it does not need.
 *
 * So running out is not fatal here: the budget halves, what that frees is given
 * back, and the capture is tried again. Repeatedly, down to a floor, because
 * one halving of a budget the machine cannot afford is unlikely to be enough.
 * A greenzone that has quietly become half as deep is a run that continues; the
 * alternative is a crash that loses the session.
 *
 * The budget is not written back to the settings. What the machine can spare
 * today is not a decision somebody made, and it should not silently become one.
 */
void StateHistory::capture(int64_t frame, const uint8_t *note, size_t noteLen)
{
	for (;;)
	{
		try
		{
			captureOnce(frame, note, noteLen);
			return;
		}
		catch (const std::bad_alloc &)
		{
			/* Already as small as a history gets: the frame is simply not
			 * stored, which costs replaying to reach it and nothing else. */
			if (!halveBudget()) return;
		}
	}
}

/* Halves what the history may hold and gives back what that frees. False when
 * it is already at the floor - below which it could not hold one anchor, and
 * would be spending allocations to store nothing. */
bool StateHistory::halveBudget()
{
	if (m_budget <= kSmallestBudget) return false;
	const uint64_t was = m_budget;
	m_budget = m_budget / 2 < kSmallestBudget ? kSmallestBudget : m_budget / 2;
	fprintf(stderr, "[history] out of memory with %llu bytes held: the budget goes from"
		" %llu to %llu and the oldest of the run is given up\n",
		(unsigned long long)m_bytes, (unsigned long long)was, (unsigned long long)m_budget);
	fflush(stderr);
	evict();
	evictDisk();
	return true;
}

void StateHistory::captureOnce(int64_t frame, const uint8_t *note, size_t noteLen)
{
	std::vector<uint8_t> carried(note, note + (note != nullptr ? noteLen : 0));
	if (!enabled()) return;
	m_newest = frame;

	/* A capture describes the frame we now stand on. Anything at or after it is
	 * a timeline that no longer happens - which is what recording over an
	 * existing entry means - so it goes before this is stored. */
	invalidateAfter(frame - 1);

	std::vector<uint8_t> bytes;
	ByteSink sink{ &bytes };
	WbxReturn r{};

	const bool wantDelta = m_epochOpen
		&& !m_segments.empty()
		&& m_segments.back().lastFrame() == frame - 1
		&& m_segments.back().lastFrame() - m_segments.back().anchorFrame < m_anchorSpacing;
	m_epochOpen = false;

	if (wantDelta)
	{
		m_host->wbx_save_delta(m_obj, true, sinkWrite, reinterpret_cast<uintptr_t>(&sink), &r);
		if (r.ok())
		{
			const uint64_t added = bytes.size();
			/* the push first, the arithmetic after: an allocation that throws
			 * between them leaves a count describing bytes nobody holds */
			m_segments.back().links.push_back(Link{ std::move(bytes), frame, std::move(carried) });
			m_segments.back().bytes += added;
			m_bytes += added;
			if (historyTrace())
			{
				fprintf(stderr, "[history] frame %lld: delta %llu bytes (segment %zu links, %llu total)\n",
					(long long)frame, (unsigned long long)added,
					m_segments.back().links.size(), (unsigned long long)m_bytes);
				fflush(stderr);
			}
			coarsen(frame);
			evict();
			return;
		}
		bytes.clear();   /* fall through to a whole state rather than lose the frame */
	}

	m_host->wbx_save_state(m_obj, sinkWrite, reinterpret_cast<uintptr_t>(&sink), &r);
	if (!r.ok()) return;   /* a missed capture only costs a longer replay later */
	Segment seg;
	seg.anchorFrame = frame;
	seg.bytes = bytes.size();
	seg.anchor = std::move(bytes);
	seg.anchorNote = std::move(carried);
	m_bytes += seg.bytes;
	if (historyTrace())
	{
		fprintf(stderr, "[history] frame %lld: ANCHOR %llu bytes (%llu total)%s\n",
			(long long)frame, (unsigned long long)seg.bytes, (unsigned long long)m_bytes,
			deltasAvailable() ? "" : " - this host has no epochs, every frame is an anchor");
		fflush(stderr);
	}
	m_segments.push_back(std::move(seg));
	coarsen(frame);
	evict();
	evictDisk();
}

/* Drops one stretch and gives back whatever it was holding - memory, room in
 * the spill file, or neither. Every erase goes through this: the accounting bug
 * that wrapped the budget past zero was one erase that did its own arithmetic. */
void StateHistory::forgetSegment(size_t index)
{
	Segment &seg = m_segments[index];
	releaseBytes(seg.memoryBytes(), "forget");
	if (seg.spilled)
	{
		if (seg.spillLength > m_spillLive) m_spillLive = 0;
		else m_spillLive -= seg.spillLength;
	}
	m_segments.erase(m_segments.begin() + static_cast<std::ptrdiff_t>(index));
}

void StateHistory::releaseBytes(uint64_t n, const char *where)
{
	if (n > m_bytes)
	{
		fprintf(stderr, "[history] %s gave back %llu bytes of the %llu held; "
			"the count was wrong before this\n", where,
			(unsigned long long)n, (unsigned long long)m_bytes);
		fflush(stderr);
		m_bytes = 0;
		return;
	}
	m_bytes -= n;
}

void StateHistory::invalidateAfter(int64_t frame)
{
	while (!m_segments.empty() && m_segments.back().anchorFrame > frame)
	{
		forgetSegment(m_segments.size() - 1);
	}
	if (m_segments.empty()) return;
	Segment &s = m_segments.back();
	while (s.lastFrame() > frame && !s.links.empty())
	{
		/* A spilled segment's links hold nothing here and its `bytes` describes
		 * the file, so there is nothing to give back and nothing to correct.
		 * What it can still answer shrinks, which is the point. */
		if (!s.spilled)
		{
			const uint64_t n = s.links.back().bytes.size();
			s.bytes -= n;
			releaseBytes(n, "invalidateAfter");
		}
		s.links.pop_back();
	}
}

/* ---- the bands ----
 *
 * A landing survives in a band if it sits on that band's grid - the multiples
 * of its stride. Everything else is composed into the landing after it, which
 * is a merge of two stored deltas and needs no machine.
 *
 * Only the landings that have just crossed a boundary are looked at, so this is
 * a couple of merges a frame rather than a sweep. The grid rule is what makes
 * that safe: it does not matter when a landing is examined or in what order,
 * because whether it survives depends only on where it lands.
 */
void StateHistory::pin(int64_t frame, bool isPinned)
{
	if (isPinned) m_pinned.insert(frame);
	else m_pinned.erase(frame);
}

bool StateHistory::pinned(int64_t frame) const
{
	return m_pinned.count(frame) != 0;
}

void StateHistory::unpinAll()
{
	m_pinned.clear();
}


void StateHistory::coarsen(int64_t newestFrame)
{
	if (!composeAvailable()) return;   /* an older host: keep every link */
	tidy(newestFrame - m_nearFrames, m_midStride);
	tidy(newestFrame - m_nearFrames - m_midFrames, m_farStride);
	settleSpilled(newestFrame - m_nearFrames - m_midFrames);
}

void StateHistory::tidy(int64_t frame, int64_t stride)
{
	if (stride <= 1 || frame <= 0) return;
	if (frame % stride == 0) return;   /* on the grid: this band wants it */
	if (pinned(frame)) return;         /* and somebody wants this one whatever the band says */

	for (Segment &seg : m_segments)
	{
		if (seg.spilled) continue;   /* its bytes are on disk and its band is settled */
		if (seg.lastFrame() < frame) continue;
		if (seg.anchorFrame >= frame) break;         /* ordered: nothing later holds it */
		const int64_t steps = seg.stepsTo(frame);
		if (steps <= 0) return;                      /* not a landing, or the anchor */
		const size_t i = static_cast<size_t>(steps) - 1;
		composeInto(seg, i);
		return;
	}
}

bool StateHistory::composePair(const Link &a, const Link &b, uint64_t anchorLen, std::vector<uint8_t> &merged)
{
	/* A merge reads both links and writes their union, so it costs their
	 * combined size - and coarsening merges into a neighbour that KEEPS the
	 * span, so that neighbour accumulates and every later merge re-reads
	 * all of it. Collapsing four hundred landings that way cost four and a
	 * half seconds of pure composition on a machine whose frames overlap
	 * ninety per cent, and thirteen seconds at seventy; measured per frame,
	 * ten to thirty milliseconds spent reclaiming a few per cent.
	 *
	 * So a merge is capped at what fits in about a millisecond of memory
	 * bandwidth. It costs almost nothing: the merges it refuses are the
	 * handful of biggest ones, which are exactly the ones where the union
	 * is closest to the sum and least is reclaimed - a tenth of a per cent
	 * of the work buys back four to fourteen per cent of the memory.
	 * Leaving the landing in place only makes the band denser than asked,
	 * which is safe; the budget is what answers for the memory.
	 *
	 * The anchor is the outer bound on the same thought: a composed link
	 * that already costs what a whole machine costs is not worth composing
	 * further, because the band would be better served by the anchor it is
	 * walking from. */
	static constexpr uint64_t kMergeCap = 8u << 20;
	const uint64_t together = a.bytes.size() + b.bytes.size();
	if (together > kMergeCap) return false;
	if (anchorLen != 0 && together > anchorLen) return false;

	merged.clear();
	/* The merge of two sorted lists is at most both of them, and asking for
	 * that up front is one allocation instead of a dozen doublings with a
	 * copy each - on a delta of megabytes that is most of the write. */
	merged.reserve(a.bytes.size() + b.bytes.size());
	ByteSink sink{ &merged };
	WbxReturn r{};
	if (m_host->wbx_compose_delta_mem != nullptr)
	{
		/* Both are already contiguous here, so the host has no reason to
		 * copy them into buffers of its own to look at them. */
		m_host->wbx_compose_delta_mem(a.bytes.data(), a.bytes.size(), b.bytes.data(), b.bytes.size(),
			sinkWrite, reinterpret_cast<uintptr_t>(&sink), &r);
	}
	else
	{
		ByteSource sa{ a.bytes.data(), a.bytes.size(), 0 };
		ByteSource sb{ b.bytes.data(), b.bytes.size(), 0 };
		m_host->wbx_compose_delta(sourceRead, reinterpret_cast<uintptr_t>(&sa),
			sourceRead, reinterpret_cast<uintptr_t>(&sb),
			sinkWrite, reinterpret_cast<uintptr_t>(&sink), &r);
	}
	return r.ok();   /* a merge that will not happen costs memory, nothing else */
}

bool StateHistory::composeInto(Segment &seg, size_t i)
{
	if (i + 1 >= seg.links.size()) return false;   /* the last link has nothing to merge into */
	Link &a = seg.links[i];
	Link &b = seg.links[i + 1];
	std::vector<uint8_t> merged;
	if (!composePair(a, b, seg.anchor.size(), merged)) return false;

	const uint64_t was = a.bytes.size() + b.bytes.size();
	seg.bytes -= was;
	releaseBytes(was, "tidy");
	seg.bytes += merged.size();
	m_bytes += merged.size();
	if (historyTrace())
	{
		fprintf(stderr, "[history] merged the landing at %lld into %lld: %llu -> %zu bytes\n",
			(long long)a.endFrame, (long long)b.endFrame, (unsigned long long)was, merged.size());
		fflush(stderr);
	}
	b.bytes = std::move(merged);
	seg.links.erase(seg.links.begin() + static_cast<std::ptrdiff_t>(i));
	return true;
}

/* ---- settling what was spilled too early ----
 *
 * The bands are kept by tidy(), which composes a landing into its neighbour as
 * the playhead moves away from it - and skips a spilled stretch, whose bytes
 * are on disk. So a stretch spilled out of the near or mid band, which a budget
 * smaller than those bands does every time, kept every frame's delta on disk
 * for good: six thousand Game Boy frames under a 64MB budget put 1567MB in the
 * file. Once the far boundary has passed such a stretch its links are read
 * back one at a time and composed down to the far grid - the same merge, under
 * the same caps - and what is left is appended to the file; what it was
 * becomes dead room and the compaction takes it back. A stretch already on the
 * far grid when it was spilled is marked settled then and never read.
 *
 * Streamed on purpose. A stretch spilled under a small budget is one that did
 * not fit in memory, so reading it back whole would be the very thing the
 * budget forbids; what is held is one accumulating link, one just read, and
 * the settled result, which is far-band sized.
 */
void StateHistory::settleSpilled(int64_t farFrame)
{
	/* a far stride of one keeps every landing, so there is nothing to settle */
	if (!composeAvailable() || m_spill == nullptr || m_farStride <= 1) return;
	Settling &st = m_settle;
	if (!st.active)
	{
		for (size_t i = 0; i + 1 < m_segments.size(); i++)
		{
			Segment &seg = m_segments[i];
			if (!seg.spilled || seg.settled || seg.lastFrame() >= farFrame) continue;
			if (seg.links.size() <= 1) { seg.settled = true; continue; }   /* nothing to compose */
			/* the head of the body: the anchor's length is the cap, the rest is stepped over */
			uint64_t noteLen = 0, count = 0;
			st = Settling{};
			if (!seekTo(m_spill, seg.spillAt) || !readU64(m_spill, st.anchorLen) || !seekBy(m_spill, st.anchorLen)
				|| !readU64(m_spill, noteLen) || !skipBy(m_spill, noteLen) || !readU64(m_spill, count)
				|| !tellAt(m_spill, st.fileAt))
			{
				seg.settled = true;   /* unreadable: left as it is, and not asked again */
				continue;
			}
			st.active = true;
			st.anchorFrame = seg.anchorFrame;
			/* what the stretch still answers for may be less than the file
			 * holds - an edit truncated it - and the rest is not wanted back */
			st.linkCount = count < seg.links.size() ? static_cast<size_t>(count) : seg.links.size();
			break;
		}
		if (!st.active) return;
	}

	/* a few links, then the rest next frame - reading one back costs what
	 * spilling it cost, and the far boundary moves one frame at a time */
	static constexpr int kLinksPerFrame = 4;
	for (int n = 0; n < kLinksPerFrame && st.linkIndex < st.linkCount; n++)
	{
		uint64_t endFrame = 0, noteLen = 0, len = 0;
		Link next;
		if (!seekTo(m_spill, st.fileAt) || !readU64(m_spill, endFrame)
			|| !readU64(m_spill, noteLen) || noteLen > kMaxNote)
		{
			st.linkCount = 0;   /* unreadable: give up on this one below, without a rewrite */
			st.merges = 0;
			break;
		}
		next.note.resize(static_cast<size_t>(noteLen));
		if (!readAll(m_spill, next.note.data(), next.note.size()) || !readU64(m_spill, len)
			|| !tellAt(m_spill, st.fileAt))
		{
			st.linkCount = 0;
			st.merges = 0;
			break;
		}
		next.bytes.resize(static_cast<size_t>(len));
		if (!readAll(m_spill, next.bytes.data(), next.bytes.size()))
		{
			st.linkCount = 0;
			st.merges = 0;
			break;
		}
		st.fileAt += len;
		next.endFrame = static_cast<int64_t>(endFrame);
		st.linkIndex++;

		if (!st.hasAcc)
		{
			st.acc = std::move(next);
			st.hasAcc = true;
			continue;
		}
		std::vector<uint8_t> merged;
		const bool keep = st.acc.endFrame % m_farStride == 0 || pinned(st.acc.endFrame)
			|| !composePair(st.acc, next, st.anchorLen, merged);
		if (keep)
		{
			/* the grid, a pin, or the caps: this landing stays */
			st.out.push_back(std::move(st.acc));
			st.acc = std::move(next);
			continue;
		}
		st.acc.bytes = std::move(merged);
		st.acc.endFrame = next.endFrame;
		st.acc.note = std::move(next.note);
		st.merges++;
	}
	if (st.linkIndex < st.linkCount) return;
	if (st.hasAcc) st.out.push_back(std::move(st.acc));
	finishSettling();
}

void StateHistory::finishSettling()
{
	Settling st = std::move(m_settle);
	m_settle = Settling{};

	Segment *seg = nullptr;
	for (Segment &s : m_segments)
	{
		if (s.anchorFrame == st.anchorFrame) { seg = &s; break; }
	}
	/* gone meanwhile - dropped, or re-recorded over - or nothing was merged:
	 * either way the file is right as it is */
	if (seg == nullptr || !seg->spilled) return;
	seg->settled = true;
	if (st.merges == 0) return;

	/* an edit may have shortened the stretch while this worked: keep only what
	 * it still answers for. Composition never moves a landing, so the settled
	 * frames are a subset of the ones it had. */
	const int64_t last = seg->lastFrame();
	while (!st.out.empty() && st.out.back().endFrame > last) st.out.pop_back();

	/* the anchor comes across from the old body; it is one machine, which is
	 * what capturing it held in memory in the first place */
	Segment work;
	work.anchorFrame = seg->anchorFrame;
	work.anchorNote = seg->anchorNote;
	work.anchor.resize(static_cast<size_t>(st.anchorLen));
	if (!seekTo(m_spill, seg->spillAt + sizeof(uint64_t)) || !readAll(m_spill, work.anchor.data(), work.anchor.size())) return;
	work.bytes = st.anchorLen;
	for (Link &l : st.out) work.bytes += l.bytes.size();
	work.links = std::move(st.out);

	if (!seekEnd(m_spill)) return;
	const uint64_t at = m_spillBytes;
	const bool ok = writeSegmentBody(m_spill, work) && std::fflush(m_spill) == 0;
	uint64_t end = 0;
	if (!ok || !tellAt(m_spill, end))
	{
		m_spillBytes = at;   /* the half written tail is dead room; the old body stands */
		return;
	}
	const uint64_t was = seg->spillLength;
	m_spillBytes = end;
	seg->spillAt = at;
	seg->spillLength = end - at;
	seg->bytes = work.bytes;
	m_spillLive = m_spillLive - was + seg->spillLength;
	/* the landings it now has: metadata only, as a spilled stretch keeps them */
	seg->links.clear();
	for (Link &l : work.links)
	{
		seg->links.push_back(Link{ std::vector<uint8_t>(), l.endFrame, std::move(l.note) });
	}
	if (historyTrace())
	{
		fprintf(stderr, "[history] settled frames %lld-%lld on disk: %llu -> %llu bytes, %d merges"
			" (%llu live, file %llu)\n",
			(long long)seg->anchorFrame, (long long)last, (unsigned long long)was,
			(unsigned long long)seg->spillLength, st.merges,
			(unsigned long long)m_spillLive, (unsigned long long)m_spillBytes);
		fflush(stderr);
	}
	/* the old body is dead room now; take it back when it is the bigger half */
	if (m_spillBytes - m_spillLive >= m_spillLive) compactSpill();
}

/* ---- spilling ----
 *
 * One file, appended to. A segment that is dropped or re-recorded over leaves
 * its space behind unreclaimed, which is the right trade for a cache: the file
 * is thrown away wholesale when the history is, and the alternative is
 * bookkeeping that buys nothing a user would notice.
 */
bool StateHistory::spill(Segment &seg)
{
	if (seg.spilled || m_spillDir.empty()) return false;

	if (m_spill == nullptr)
	{
		const std::string path = m_spillDir + "/history-spill.bin";
		m_spill = std::fopen(path.c_str(), "w+b");
		if (m_spill == nullptr) return false;
		m_spillBytes = 0;
	}
	if (!seekEnd(m_spill)) return false;

	const uint64_t at = m_spillBytes;
	bool ok = writeSegmentBody(m_spill, seg);
	if (!ok || std::fflush(m_spill) != 0)
	{
		/* a half written segment is unreadable, so the file goes back to where
		 * it was and the caller falls back to dropping */
		m_spillBytes = at;
		return false;
	}

	if (!tellAt(m_spill, m_spillBytes)) return false;
	/* Before the flag, not after: once it is set the segment costs no memory by
	 * definition, and taking its bytes off the count afterwards takes nothing. */
	releaseBytes(seg.memoryBytes(), "spill");
	seg.spilled = true;
	/* on the far grid already if the far boundary has passed it - tidy() did
	 * that as it went - and then settleSpilled() has nothing to read back */
	seg.settled = m_newest >= 0 && seg.lastFrame() < m_newest - m_nearFrames - m_midFrames;
	seg.spillAt = at;
	seg.spillLength = m_spillBytes - at;
	m_spillLive += seg.spillLength;

	/* What it held is now the file's; give the memory back for real. The notes
	 * stay: they are metadata, like the landings, and answering what was stored
	 * with a frame must not touch a disk. */
	std::vector<uint8_t>().swap(seg.anchor);
	for (Link &l : seg.links)
	{
		std::vector<uint8_t>().swap(l.bytes);
	}

	if (historyTrace())
	{
		fprintf(stderr, "[history] spilled frames %lld-%lld: %llu bytes to disk"
			" (%llu in memory, %llu on disk, file %llu)\n",
			(long long)seg.anchorFrame, (long long)seg.lastFrame(),
			(unsigned long long)seg.bytes, (unsigned long long)m_bytes,
			(unsigned long long)m_spillLive, (unsigned long long)m_spillBytes);
		fflush(stderr);
	}
	return true;
}

bool StateHistory::restoreSpilled(const Segment &seg, int64_t steps, std::string &error)
{
	if (m_spill == nullptr || !seekTo(m_spill, seg.spillAt))
	{
		error = "the spilled state history could not be read";
		return false;
	}
	uint64_t anchorLen = 0, linkCount = 0;
	uint64_t noteLen = 0;
	if (!readU64(m_spill, anchorLen))
	{
		error = "the spilled state history could not be read";
		return false;
	}

	WbxReturn r{};
	FileSource anchor{ m_spill, anchorLen };
	m_host->wbx_load_state(m_obj, fileRead, reinterpret_cast<uintptr_t>(&anchor), &r);
	if (!r.ok()) { error = r.errorMessage; return false; }
	/* the sandbox may stop reading before the end - skip whatever it left */
	if (!seekBy(m_spill, anchor.left))
	{
		error = "the spilled state history could not be read";
		return false;
	}

	/* the anchor's note is already in memory; step over the file's copy */
	if (!readU64(m_spill, noteLen) || !skipBy(m_spill, noteLen) || !readU64(m_spill, linkCount))
	{
		error = "the spilled state history could not be read";
		return false;
	}
	for (int64_t i = 0; i < steps; i++)
	{
		uint64_t endFrame = 0, len = 0;
		if (!readU64(m_spill, endFrame) || !readU64(m_spill, noteLen)
			|| !skipBy(m_spill, noteLen) || !readU64(m_spill, len))
		{
			error = "the spilled state history could not be read";
			return false;
		}
		FileSource link{ m_spill, len };
		m_host->wbx_load_delta(m_obj, fileRead, reinterpret_cast<uintptr_t>(&link), &r);
		if (!r.ok()) { error = r.errorMessage; return false; }
		if (!seekBy(m_spill, link.left))
		{
			error = "the spilled state history could not be read";
			return false;
		}
	}
	return true;
}

/* Under budget pressure the history thins from the FAR end of the oldest
 * segment: dropping a trailing delta costs precision back there and orphans
 * nothing, because nothing chains through the end of a chain. The first
 * segment's anchor is never dropped - it is what keeps every frame reachable
 * at all - and neither is the newest segment's, which is where the work is. */
void StateHistory::evict()
{
	while (m_bytes > m_budget)
	{
		/* First choice: put the oldest stretch on disk, oldest to newest. It
		 * costs reading it back rather than replaying to it, and the far end of
		 * the history is where that trade is obviously right. The newest is
		 * never spilled - it is where the work is. */
		bool moved = false;
		for (size_t i = 0; i + 1 < m_segments.size(); i++)
		{
			if (m_segments[i].spilled) continue;
			if (spill(m_segments[i]))
			{
				/* right here, not after the whole memory pass: the file is over
				 * its budget from the moment the write lands, and a limit that
				 * is only true once the loop finishes is a limit somebody
				 * watching a disk fill up cannot read off `ls` */
				evictDisk();
				moved = true;
				break;
			}
			/* Asked to spill, and could not - a full disk, near enough always.
			 * The thinning below carries on, so this costs frames rather than
			 * the session, but it is not something to keep to ourselves. */
			if (!m_spillDir.empty() && !m_spillFailed)
			{
				m_spillFailed = true;
				fprintf(stderr, "[history] could not spill to %s - the far band will be dropped instead"
					" (the disk is full, or the directory has gone)\n", m_spillDir.c_str());
				fflush(stderr);
			}
			break;   /* nowhere to spill: everything after this fails the same way */
		}
		if (moved) continue;

		/* Thin the oldest stretch that still holds anything, and NEVER the
		 * newest - it is where the playhead is, and its last link is the frame
		 * that was captured a moment ago.
		 *
		 * Taking it was a loop: capture a delta, evict it again because the
		 * budget was already unmeetable, then have nothing to chain to next
		 * frame and write a whole anchor instead, spill that, and go round. Six
		 * thousand Game Boy frames under a budget too small for them made two
		 * thousand seven hundred anchors and five gigabytes of spill file. The
		 * drop-a-whole-stretch path below has always spared the newest; this one
		 * did not, and it is the one that runs first. */
		Segment *victim = nullptr;
		for (size_t i = 0; i + 1 < m_segments.size(); i++)
		{
			Segment &s = m_segments[i];
			if (s.links.empty() || s.spilled) continue;
			if (pinned(s.links.back().endFrame)) continue;   /* somebody wants that one */
			victim = &s;
			break;
		}
		if (victim != nullptr)
		{
			uint64_t n = victim->links.back().bytes.size();
			victim->bytes -= n;
			releaseBytes(n, "evict");
			victim->links.pop_back();
			continue;
		}
		/* nothing left to thin: drop the oldest that is neither the first nor
		 * the newest, and give up when only those remain. A spilled segment
		 * costs nothing in memory, so dropping one would not help. */
		size_t drop = 0;
		for (size_t i = 1; i + 1 < m_segments.size(); i++)
		{
			if (m_segments[i].spilled) continue;
			/* a stretch somebody pinned a frame in is spilled, never dropped -
			 * and if it could not be spilled it stays, and the budget is missed
			 * rather than the promise */
			if (holdsPinned(m_pinned, m_segments[i].anchorFrame, m_segments[i].lastFrame())) continue;
			drop = i;
			break;
		}
		if (drop == 0) return;
		forgetSegment(drop);
	}
}

bool StateHistory::restore(int64_t frame, std::string &error)
{
	const Segment *seg = nullptr;
	int64_t steps = -1;
	for (const Segment &s : m_segments)
	{
		const int64_t n = s.stepsTo(frame);
		if (n >= 0) { seg = &s; steps = n; break; }
	}
	if (seg == nullptr)
	{
		error = "no stored state at that frame";
		return false;
	}

	const double t0 = historyTrace() ? nowSeconds() : 0.0;
	if (seg->spilled)
	{
		if (!restoreSpilled(*seg, steps, error)) return false;
		if (historyTrace())
		{
			fprintf(stderr, "[history] restore %lld: from disk, anchor %lld + %lld deltas, %.0f ms\n",
				(long long)frame, (long long)seg->anchorFrame, (long long)steps,
				(nowSeconds() - t0) * 1000);
		}
		m_epochOpen = false;
		return true;
	}

	WbxReturn r{};
	ByteSource anchor{ seg->anchor.data(), seg->anchor.size(), 0 };
	m_host->wbx_load_state(m_obj, sourceRead, reinterpret_cast<uintptr_t>(&anchor), &r);
	if (!r.ok())
	{
		error = r.errorMessage;
		return false;
	}
	const double t1 = historyTrace() ? nowSeconds() : 0.0;
	for (int64_t i = 0; i < steps; i++)
	{
		const std::vector<uint8_t> &d = seg->links[static_cast<size_t>(i)].bytes;
		ByteSource src{ d.data(), d.size(), 0 };
		m_host->wbx_load_delta(m_obj, sourceRead, reinterpret_cast<uintptr_t>(&src), &r);
		if (!r.ok())
		{
			error = r.errorMessage;
			return false;
		}
	}
	if (historyTrace())
	{
		const int64_t chain = steps;
		const double t2 = nowSeconds();
		fprintf(stderr,
			"[history] restore %lld: anchor %lld (%.1f MB) %.0f ms + %lld deltas %.0f ms = %.0f ms\n",
			(long long)frame, (long long)seg->anchorFrame, seg->anchor.size() / 1048576.0,
			(t1 - t0) * 1000, (long long)chain, (t2 - t1) * 1000, (t2 - t0) * 1000);
	}
	/* whatever epoch was marked described the machine we have just left */
	m_epochOpen = false;
	return true;
}

/* ---- persistence ----
 *
 * One file, written and read a segment at a time. Nothing here assembles the
 * history in memory: the old greenzone did, through a managed array that stops
 * near 2GB, and a long run's history therefore failed to save and said nothing.
 */
namespace
{

const char kMagic[] = "ChimeraHistory3";

/* Versions this build can no longer read. A history from one of these is not
 * damage and not the user's doing: it is a cache written by an older build, and
 * the contract for losing a cache is that it costs recomputation and never
 * work. So it is treated exactly as a history of another machine is - dropped,
 * quietly, and rebuilt by playing. 1 had a stride of one frame per link, from
 * before links could span more than a frame; 2 had no room for the caller's
 * note. */
const char *const kSuperseded[] = { "ChimeraHistory1", "ChimeraHistory2" };

/* A note is a frontend's lag flag and its counters - a few bytes, ridden along
 * because keeping them in a table of the caller's own would mean mirroring
 * every eviction this class does. It is not a place to keep things, and a file
 * claiming otherwise is damaged. */

} // namespace

bool StateHistory::saveTo(const char *path, const char *machineId, std::string &error)
{
	std::FILE *f = std::fopen(path, "wb");
	if (f == nullptr)
	{
		error = std::string("could not write the state history: ") + std::strerror(errno);
		return false;
	}
	const std::string id = machineId != nullptr ? machineId : "";
	bool ok = writeAll(f, kMagic, sizeof kMagic - 1)
		&& writeU64(f, id.size())
		&& writeAll(f, id.data(), id.size())
		&& writeU64(f, m_segments.size());
	for (const Segment &seg : m_segments)
	{
		if (!ok) break;
		ok = writeU64(f, static_cast<uint64_t>(seg.anchorFrame));
		if (!ok) break;
		if (seg.spilled)
		{
			/* A spilled segment is already in exactly this shape, minus the
			 * frame just written, so it is copied rather than rebuilt - which
			 * keeps the promise that nothing here is assembled in memory. */
			ok = m_spill != nullptr && seekTo(m_spill, seg.spillAt);
			uint64_t left = seg.spillLength;
			std::vector<uint8_t> chunk(64 * 1024);
			while (ok && left != 0)
			{
				const size_t n = static_cast<size_t>(left < chunk.size() ? left : chunk.size());
				ok = readAll(m_spill, chunk.data(), n) && writeAll(f, chunk.data(), n);
				left -= n;
			}
			continue;
		}
		ok = writeSegmentBody(f, seg);
	}
	if (std::fclose(f) != 0) ok = false;
	if (!ok)
	{
		error = "the state history could not be written in full";
		std::remove(path);   /* half a history is worse than none */
		return false;
	}
	return true;
}

bool StateHistory::loadFrom(const char *path, const char *machineId, std::string &error)
{
	clear();
	std::FILE *f = std::fopen(path, "rb");
	if (f == nullptr) return true;   /* no history yet is not a failure */

	auto give_up = [&](std::string why) {
		std::fclose(f);
		clear();
		error = std::move(why);
		return false;
	};

	char magic[sizeof kMagic - 1];
	if (!readAll(f, magic, sizeof magic)) return give_up("that is not a state history");
	if (std::memcmp(magic, kMagic, sizeof magic) != 0)
	{
		for (const char *old : kSuperseded)
		{
			if (std::memcmp(magic, old, sizeof magic) != 0) continue;
			std::fclose(f);
			clear();
			return true;
		}
		return give_up("that is not a state history");
	}
	uint64_t idLen = 0;
	if (!readU64(f, idLen) || idLen > (1u << 20)) return give_up("the state history is damaged");
	std::string id(static_cast<size_t>(idLen), '\0');
	if (!readAll(f, id.data(), id.size())) return give_up("the state history is damaged");
	if (id != (machineId != nullptr ? machineId : ""))
	{
		/* Not damage, and not an error the user did anything about: these states
		 * belong to a machine with other settings, files or core. */
		std::fclose(f);
		clear();
		return true;
	}

	uint64_t segCount = 0;
	if (!readU64(f, segCount)) return give_up("the state history is damaged");
	for (uint64_t i = 0; i < segCount; i++)
	{
		Segment seg;
		uint64_t anchorFrame = 0, anchorLen = 0, deltaCount = 0, noteLen = 0;
		if (!readU64(f, anchorFrame) || !readU64(f, anchorLen)) return give_up("the state history is damaged");
		seg.anchorFrame = static_cast<int64_t>(anchorFrame);
		seg.anchor.resize(static_cast<size_t>(anchorLen));
		if (!readAll(f, seg.anchor.data(), seg.anchor.size())) return give_up("the state history is damaged");
		if (!readU64(f, noteLen) || noteLen > kMaxNote) return give_up("the state history is damaged");
		seg.anchorNote.resize(static_cast<size_t>(noteLen));
		if (!readAll(f, seg.anchorNote.data(), seg.anchorNote.size())) return give_up("the state history is damaged");
		if (!readU64(f, deltaCount)) return give_up("the state history is damaged");
		seg.bytes = anchorLen;
		int64_t landed = seg.anchorFrame;
		for (uint64_t d = 0; d < deltaCount; d++)
		{
			uint64_t endFrame = 0, len = 0;
			if (!readU64(f, endFrame) || !readU64(f, noteLen) || noteLen > kMaxNote)
			{
				return give_up("the state history is damaged");
			}
			std::vector<uint8_t> note(static_cast<size_t>(noteLen));
			if (!readAll(f, note.data(), note.size())) return give_up("the state history is damaged");
			if (!readU64(f, len)) return give_up("the state history is damaged");
			/* the spans have to tile: a file whose links go backwards or stand
			 * still would offer frames it cannot walk to */
			if (static_cast<int64_t>(endFrame) <= landed) return give_up("the state history is damaged");
			landed = static_cast<int64_t>(endFrame);
			std::vector<uint8_t> delta(static_cast<size_t>(len));
			if (!readAll(f, delta.data(), delta.size())) return give_up("the state history is damaged");
			seg.bytes += len;
			seg.links.push_back(Link{ std::move(delta), landed, std::move(note) });
		}
		m_bytes += seg.bytes;
		m_segments.push_back(std::move(seg));
		/* Per segment, not at the end: a history can be larger than the budget
		 * - that is what spilling is for - and holding all of it at once while
		 * deciding what to keep would be the very thing this design removed. */
		if (enabled()) evict();
	}
	std::fclose(f);
	if (enabled()) evict();
	return true;
}

} // namespace chimera
