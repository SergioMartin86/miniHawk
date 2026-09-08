#include "state_history.hpp"

#include <algorithm>
#include <cstdio>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <chrono>

namespace chimera
{

namespace
{

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
	m_segments.erase(
		std::remove_if(m_segments.begin(), m_segments.end(),
			[](const Segment &seg) { return seg.spilled; }),
		m_segments.end());
	m_spillDir = next;
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

void StateHistory::capture(int64_t frame)
{
	if (!enabled()) return;

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
			m_segments.back().bytes += bytes.size();
			m_bytes += bytes.size();
			if (historyTrace())
			{
				fprintf(stderr, "[history] frame %lld: delta %zu bytes (segment %zu links, %llu total)\n",
					(long long)frame, bytes.size(),
					m_segments.back().links.size() + 1, (unsigned long long)m_bytes);
				fflush(stderr);
			}
			m_segments.back().links.push_back(Link{ std::move(bytes), frame });
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
}

void StateHistory::invalidateAfter(int64_t frame)
{
	while (!m_segments.empty() && m_segments.back().anchorFrame > frame)
	{
		m_bytes -= m_segments.back().bytes;
		m_segments.pop_back();
	}
	if (m_segments.empty()) return;
	Segment &s = m_segments.back();
	while (s.lastFrame() > frame && !s.links.empty())
	{
		uint64_t n = s.links.back().bytes.size();
		s.bytes -= n;
		m_bytes -= n;
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
void StateHistory::coarsen(int64_t newestFrame)
{
	if (!composeAvailable()) return;   /* an older host: keep every link */
	tidy(newestFrame - m_nearFrames, m_midStride);
	tidy(newestFrame - m_nearFrames - m_midFrames, m_farStride);
}

void StateHistory::tidy(int64_t frame, int64_t stride)
{
	if (stride <= 1 || frame <= 0) return;
	if (frame % stride == 0) return;   /* on the grid: this band wants it */

	for (Segment &seg : m_segments)
	{
		if (seg.spilled) continue;   /* its bytes are on disk and its band is settled */
		if (seg.lastFrame() < frame) continue;
		if (seg.anchorFrame >= frame) break;         /* ordered: nothing later holds it */
		const int64_t steps = seg.stepsTo(frame);
		if (steps <= 0) return;                      /* not a landing, or the anchor */
		const size_t i = static_cast<size_t>(steps) - 1;
		if (i + 1 >= seg.links.size()) return;       /* the last link has nothing to merge into */

		/* A composed link that already costs what a whole machine costs is not
		 * worth composing further - that is the point at which this band would
		 * be better served by the anchor it is walking from. Leaving the
		 * landing in place only makes the band denser than asked, which is
		 * safe; the budget is what answers for the memory. */
		Link &a = seg.links[i];
		Link &b = seg.links[i + 1];
		if (!seg.anchor.empty() && a.bytes.size() + b.bytes.size() > seg.anchor.size()) return;

		std::vector<uint8_t> merged;
		ByteSink sink{ &merged };
		ByteSource sa{ a.bytes.data(), a.bytes.size(), 0 };
		ByteSource sb{ b.bytes.data(), b.bytes.size(), 0 };
		WbxReturn r{};
		m_host->wbx_compose_delta(sourceRead, reinterpret_cast<uintptr_t>(&sa),
			sourceRead, reinterpret_cast<uintptr_t>(&sb),
			sinkWrite, reinterpret_cast<uintptr_t>(&sink), &r);
		if (!r.ok()) return;   /* a merge that will not happen costs memory, nothing else */

		const uint64_t was = a.bytes.size() + b.bytes.size();
		seg.bytes -= was;
		m_bytes -= was;
		seg.bytes += merged.size();
		m_bytes += merged.size();
		if (historyTrace())
		{
			fprintf(stderr, "[history] merged the landing at %lld into %lld: %llu -> %zu bytes\n",
				(long long)frame, (long long)b.endFrame, (unsigned long long)was, merged.size());
			fflush(stderr);
		}
		b.bytes = std::move(merged);
		seg.links.erase(seg.links.begin() + static_cast<std::ptrdiff_t>(i));
		return;
	}
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
	bool ok = writeU64(m_spill, seg.anchor.size())
		&& writeAll(m_spill, seg.anchor.data(), seg.anchor.size())
		&& writeU64(m_spill, seg.links.size());
	for (const Link &l : seg.links)
	{
		if (!ok) break;
		ok = writeU64(m_spill, static_cast<uint64_t>(l.endFrame))
			&& writeU64(m_spill, l.bytes.size())
			&& writeAll(m_spill, l.bytes.data(), l.bytes.size());
	}
	if (!ok || std::fflush(m_spill) != 0)
	{
		/* a half written segment is unreadable, so the file goes back to where
		 * it was and the caller falls back to dropping */
		m_spillBytes = at;
		return false;
	}

	if (!tellAt(m_spill, m_spillBytes)) return false;
	seg.spilled = true;
	seg.spillAt = at;
	seg.spillLength = m_spillBytes - at;
	m_bytes -= seg.bytes;

	/* what it held is now the file's; give the memory back for real */
	std::vector<uint8_t>().swap(seg.anchor);
	for (Link &l : seg.links) std::vector<uint8_t>().swap(l.bytes);

	if (historyTrace())
	{
		fprintf(stderr, "[history] spilled frames %lld-%lld: %llu bytes to disk (%llu in memory)\n",
			(long long)seg.anchorFrame, (long long)seg.lastFrame(),
			(unsigned long long)seg.bytes, (unsigned long long)m_bytes);
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

	if (!readU64(m_spill, linkCount))
	{
		error = "the spilled state history could not be read";
		return false;
	}
	for (int64_t i = 0; i < steps; i++)
	{
		uint64_t endFrame = 0, len = 0;
		if (!readU64(m_spill, endFrame) || !readU64(m_spill, len))
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
			if (spill(m_segments[i])) { moved = true; break; }
			break;   /* nowhere to spill: everything after this fails the same way */
		}
		if (moved) continue;

		Segment *victim = nullptr;
		for (Segment &s : m_segments)
		{
			if (!s.links.empty() && !s.spilled) { victim = &s; break; }
		}
		if (victim != nullptr)
		{
			uint64_t n = victim->links.back().bytes.size();
			victim->bytes -= n;
			m_bytes -= n;
			victim->links.pop_back();
			continue;
		}
		/* nothing left to thin: drop the oldest that is neither the first nor
		 * the newest, and give up when only those remain. A spilled segment
		 * costs nothing in memory, so dropping one would not help. */
		size_t drop = 0;
		for (size_t i = 1; i + 1 < m_segments.size(); i++)
		{
			if (!m_segments[i].spilled) { drop = i; break; }
		}
		if (drop == 0) return;
		m_bytes -= m_segments[drop].bytes;
		m_segments.erase(m_segments.begin() + static_cast<std::ptrdiff_t>(drop));
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

const char kMagic[] = "ChimeraHistory2";

/* Versions this build can no longer read. A history from one of these is not
 * damage and not the user's doing: it is a cache written by an older build, and
 * the contract for losing a cache is that it costs recomputation and never
 * work. So it is treated exactly as a history of another machine is - dropped,
 * quietly, and rebuilt by playing. 1 had a stride of one frame per link, from
 * before links could span more than a frame. */
const char *const kSuperseded[] = { "ChimeraHistory1" };

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
		ok = writeU64(f, seg.anchor.size())
			&& writeAll(f, seg.anchor.data(), seg.anchor.size())
			&& writeU64(f, seg.links.size());
		for (const Link &l : seg.links)
		{
			if (!ok) break;
			ok = writeU64(f, static_cast<uint64_t>(l.endFrame))
				&& writeU64(f, l.bytes.size())
				&& writeAll(f, l.bytes.data(), l.bytes.size());
		}
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
		uint64_t anchorFrame = 0, anchorLen = 0, deltaCount = 0;
		if (!readU64(f, anchorFrame) || !readU64(f, anchorLen)) return give_up("the state history is damaged");
		seg.anchorFrame = static_cast<int64_t>(anchorFrame);
		seg.anchor.resize(static_cast<size_t>(anchorLen));
		if (!readAll(f, seg.anchor.data(), seg.anchor.size())) return give_up("the state history is damaged");
		if (!readU64(f, deltaCount)) return give_up("the state history is damaged");
		seg.bytes = anchorLen;
		int64_t landed = seg.anchorFrame;
		for (uint64_t d = 0; d < deltaCount; d++)
		{
			uint64_t endFrame = 0, len = 0;
			if (!readU64(f, endFrame) || !readU64(f, len)) return give_up("the state history is damaged");
			/* the spans have to tile: a file whose links go backwards or stand
			 * still would offer frames it cannot walk to */
			if (static_cast<int64_t>(endFrame) <= landed) return give_up("the state history is damaged");
			landed = static_cast<int64_t>(endFrame);
			std::vector<uint8_t> delta(static_cast<size_t>(len));
			if (!readAll(f, delta.data(), delta.size())) return give_up("the state history is damaged");
			seg.bytes += len;
			seg.links.push_back(Link{ std::move(delta), landed });
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
