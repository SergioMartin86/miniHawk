#include "state_history.hpp"

#include <algorithm>
#include <cstdio>
#include <cerrno>
#include <cstdlib>
#include <cstring>

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

/* CHIMERA_HISTORY_TRACE=1 says what the history stored and what it cost. A
 * delta silently falling back to a whole state is the failure mode with no
 * symptom - everything still works, it just costs a hundred times more - so
 * there has to be a way to look. */
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
}

bool StateHistory::deltasAvailable() const
{
	return m_host != nullptr && m_host->wbx_epoch_begin != nullptr
		&& m_host->wbx_save_delta != nullptr && m_host->wbx_load_delta != nullptr;
}

int64_t StateHistory::count() const
{
	int64_t n = 0;
	for (const Segment &s : m_segments) n += 1 + static_cast<int64_t>(s.deltas.size());
	return n;
}

int64_t StateHistory::nearest(int64_t frame) const
{
	int64_t best = -1;
	for (const Segment &s : m_segments)
	{
		if (s.anchorFrame > frame) break;              /* ordered: nothing later helps */
		best = std::min(frame, s.lastFrame());         /* every frame in a segment is reachable */
	}
	return best;
}

void StateHistory::beforeAdvance()
{
	m_epochOpen = false;
	if (!enabled() || !deltasAvailable()) return;
	/* A delta continues the newest segment, and only while there is one with
	 * room. Otherwise the coming capture is an anchor and needs no epoch. */
	if (m_segments.empty() || m_segments.back().deltas.size() >= kMaxChain) return;
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
		&& m_segments.back().deltas.size() < kMaxChain;
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
					m_segments.back().deltas.size() + 1, (unsigned long long)m_bytes);
				fflush(stderr);
			}
			m_segments.back().deltas.push_back(std::move(bytes));
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
	while (s.lastFrame() > frame && !s.deltas.empty())
	{
		uint64_t n = s.deltas.back().size();
		s.bytes -= n;
		m_bytes -= n;
		s.deltas.pop_back();
	}
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
		Segment *victim = nullptr;
		for (Segment &s : m_segments)
		{
			if (!s.deltas.empty()) { victim = &s; break; }
		}
		if (victim != nullptr)
		{
			uint64_t n = victim->deltas.back().size();
			victim->bytes -= n;
			m_bytes -= n;
			victim->deltas.pop_back();
			continue;
		}
		/* nothing but anchors left: drop the oldest that is neither the first
		 * nor the newest, and give up when only those remain */
		if (m_segments.size() <= 2) return;
		m_bytes -= m_segments[1].bytes;
		m_segments.erase(m_segments.begin() + 1);
	}
}

bool StateHistory::restore(int64_t frame, std::string &error)
{
	const Segment *seg = nullptr;
	for (const Segment &s : m_segments)
	{
		if (s.covers(frame)) { seg = &s; break; }
	}
	if (seg == nullptr)
	{
		error = "no stored state at that frame";
		return false;
	}

	WbxReturn r{};
	ByteSource anchor{ seg->anchor.data(), seg->anchor.size(), 0 };
	m_host->wbx_load_state(m_obj, sourceRead, reinterpret_cast<uintptr_t>(&anchor), &r);
	if (!r.ok())
	{
		error = r.errorMessage;
		return false;
	}
	for (int64_t i = 0; i < frame - seg->anchorFrame; i++)
	{
		const std::vector<uint8_t> &d = seg->deltas[static_cast<size_t>(i)];
		ByteSource src{ d.data(), d.size(), 0 };
		m_host->wbx_load_delta(m_obj, sourceRead, reinterpret_cast<uintptr_t>(&src), &r);
		if (!r.ok())
		{
			error = r.errorMessage;
			return false;
		}
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

const char kMagic[] = "ChimeraHistory1";

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
		ok = writeU64(f, static_cast<uint64_t>(seg.anchorFrame))
			&& writeU64(f, seg.anchor.size())
			&& writeAll(f, seg.anchor.data(), seg.anchor.size())
			&& writeU64(f, seg.deltas.size());
		for (const std::vector<uint8_t> &d : seg.deltas)
		{
			if (!ok) break;
			ok = writeU64(f, d.size()) && writeAll(f, d.data(), d.size());
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
	if (!readAll(f, magic, sizeof magic) || std::memcmp(magic, kMagic, sizeof magic) != 0)
	{
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
		for (uint64_t d = 0; d < deltaCount; d++)
		{
			uint64_t len = 0;
			if (!readU64(f, len)) return give_up("the state history is damaged");
			std::vector<uint8_t> delta(static_cast<size_t>(len));
			if (!readAll(f, delta.data(), delta.size())) return give_up("the state history is damaged");
			seg.bytes += len;
			seg.deltas.push_back(std::move(delta));
		}
		m_bytes += seg.bytes;
		m_segments.push_back(std::move(seg));
	}
	std::fclose(f);
	/* what was read may be more than this session's budget allows to be kept */
	if (enabled()) evict();
	return true;
}

} // namespace chimera
