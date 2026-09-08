/* The state history: where a machine has been, along a movie's timeline.
 *
 * One structure serves everything that asks that question - seeking in
 * TAStudio, stepping a frame back, and what survives closing a project (see
 * docs/state-manager.md). It replaces a map of whole savestates, which cost
 * what the MACHINE is: on a real xemu run a state is 75 MB at frame 150 and
 * 210 MB by frame 1200, so a history of any length was gigabytes and a per
 * frame history was impossible.
 *
 * A stored frame costs what the FRAME DID instead, by asking the sandbox what
 * changed since a moment rather than since the seal (miniBox epochs). Measured
 * on that same run: 2.0 MB a frame against a 210 MB state, about a hundredfold.
 *
 * The shape that makes deltas safe is the SEGMENT: one whole state - the
 * anchor - followed by the deltas that walk forward from it, one per frame and
 * contiguous. Contiguity is the invariant the whole file rests on. A delta only
 * means anything applied to exactly the machine it was measured against, so a
 * gap in a segment would make every frame after it unreachable; keeping
 * segments contiguous by construction means any frame the history claims is a
 * frame it can actually produce.
 *
 * A host that does not offer epochs (an older libminiboxhost beside a newer
 * libchimera) is not a failure: every capture is then an anchor, every segment
 * is one frame long, and the history behaves exactly as it did before.
 */
#ifndef CHIMERA_STATE_HISTORY_HPP
#define CHIMERA_STATE_HISTORY_HPP

#include <cstdint>
#include <string>
#include <vector>

#include "host_dyn.hpp"

namespace chimera
{

class StateHistory
{
public:
	/* budget 0 disables and drops everything. */
	void configure(const HostApi *host, void *obj, uint64_t budgetBytes);
	void clear();

	bool enabled() const { return m_budget != 0; }
	uint64_t bytes() const { return m_bytes; }

	/* Frames the history can produce, which is every anchor plus every delta -
	 * NOT the number of stored objects, because a frame reached by walking
	 * deltas is as reachable as one with a state of its own. */
	int64_t count() const;

	/* The greatest frame at or before `frame` that can be produced, or -1. */
	int64_t nearest(int64_t frame) const;

	/* Called immediately BEFORE an advance whose result will be captured. It
	 * decides anchor-or-delta for that frame and, for a delta, marks the epoch
	 * the sandbox will measure. Deciding here rather than after the advance is
	 * forced: an epoch has to be open before the machine moves. */
	void beforeAdvance();

	/* Called immediately after that advance, with the frame now standing at. */
	void capture(int64_t frame);

	/* Drops everything after `frame`. An input edit at a frame makes every
	 * later state a lie, while the state AT it still holds. */
	void invalidateAfter(int64_t frame);

	/* Puts the machine back to `frame`, which must be one nearest() offered.
	 * Restores that frame's segment anchor and walks its deltas forward.
	 * False with `error` set. */
	bool restore(int64_t frame, std::string &error);

private:
	struct Segment
	{
		int64_t anchorFrame = 0;
		std::vector<uint8_t> anchor;               /* a whole machine */
		std::vector<std::vector<uint8_t>> deltas;  /* deltas[i]: anchorFrame+i -> +i+1 */
		uint64_t bytes = 0;

		int64_t lastFrame() const { return anchorFrame + static_cast<int64_t>(deltas.size()); }
		bool covers(int64_t f) const { return f >= anchorFrame && f <= lastFrame(); }
	};

	/* How long a delta chain may get before the next anchor. A seek pays one
	 * delta application per link, so this is the latency budget in disguise;
	 * the measured policy that sets it from the cost the engine observes is
	 * still to come (docs/state-manager.md), and until then it is a constant
	 * chosen to be comfortably inside a second on the slowest core measured. */
	static constexpr size_t kMaxChain = 512;

	bool deltasAvailable() const;
	void evict();

	const HostApi *m_host = nullptr;
	void *m_obj = nullptr;
	uint64_t m_budget = 0;
	uint64_t m_bytes = 0;
	std::vector<Segment> m_segments;   /* ordered by anchorFrame, never overlapping */
	bool m_epochOpen = false;          /* an epoch is marked and a delta is wanted */
};

} // namespace chimera

#endif
