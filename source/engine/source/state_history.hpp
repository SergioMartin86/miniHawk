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
#include <cstdio>
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

	/* How dense the history is at each distance from the newest frame it holds,
	 * all in FRAMES - the engine does not know a core's frame rate and the
	 * caller does. Any value left at 0 keeps the current one.
	 *
	 * Editing a movie is local: the frames somebody steps through and re-records
	 * are the ones around the playhead, while the frames from ten minutes ago
	 * are jumped to rather than scrubbed through. So a frame is captured into
	 * the near band and coarsened as the playhead leaves it behind, which is a
	 * thing that can be done to a stored delta without a machine.
	 *
	 * `anchorSpacing` is the one that decides what a seek costs, since a
	 * restore walks the links of one anchor's stretch and no further. */
	void bands(int64_t nearFrames, int64_t midFrames, int64_t midStride,
	           int64_t farStride, int64_t anchorSpacing);

	/* Where the far band goes once the budget is full, or nullptr for nowhere.
	 *
	 * The oldest stretches are the right thing to put on disk: large, rarely
	 * touched, and - if the file is lost - regenerable like everything else
	 * here. Without a directory the budget can only DROP them, which costs the
	 * frames themselves rather than the time to read them back. */
	void spillTo(const char *dir);

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

	/* Called immediately after that advance, with the frame now standing at.
	 *
	 * `note` is the caller's own bookkeeping for this frame, stored with it and
	 * handed back by noteFor. The engine never looks inside it. It exists
	 * because a frontend has side-band state a savestate does not carry - a lag
	 * flag, a lag count, its own frame number - and keeping that in a table of
	 * the caller's own would mean mirroring every invalidation, eviction,
	 * coarsening and spill this class does. Riding along is the only way it
	 * stays true. */
	void capture(int64_t frame, const uint8_t *note = nullptr, size_t noteLen = 0);

	/* What was stored with `frame`, or nullptr. Borrowed, and invalidated by the
	 * next capture. */
	const uint8_t *noteFor(int64_t frame, size_t &lenOut) const;

	/* Drops everything after `frame`. An input edit at a frame makes every
	 * later state a lie, while the state AT it still holds. */
	void invalidateAfter(int64_t frame);

	/* Puts the machine back to `frame`, which must be one nearest() offered.
	 * Restores that frame's segment anchor and walks its deltas forward.
	 * False with `error` set. */
	bool restore(int64_t frame, std::string &error);

	/* Writes the history to a file, and reads one back.
	 *
	 * Streamed, one segment at a time, straight to and from the file: no part of
	 * this may be assembled in memory first. That is not an optimisation, it is
	 * the bug this design exists to remove - the greenzone used to serialize
	 * itself through a managed byte[], which stops near 2GB, and a history of
	 * any real length silently failed to save and took the work with it.
	 *
	 * `machineId` describes the machine the states belong to - the caller's
	 * business, since it is the caller that knows about cores and settings and
	 * files. Loading a history that names a different machine refuses and leaves
	 * the history empty: a state is the memory of one exact machine, and the
	 * sandbox only checks the core binary. An absent or unreadable file is that
	 * same empty answer, because losing this costs recomputation and never work.
	 * False with `error` set. */
	bool saveTo(const char *path, const char *machineId, std::string &error);
	bool loadFrom(const char *path, const char *machineId, std::string &error);

private:
	/* One step along a segment: the bytes that walk the machine from wherever
	 * the link before it landed to `endFrame`.
	 *
	 * A link spans a single frame when it is captured, and more than one once
	 * the history has thinned it - two adjacent links compose into one that
	 * spans both (docs/state-manager.md). That is why a link carries the frame
	 * it lands ON rather than being counted from the anchor: the stride is no
	 * longer a constant, and a frame in the middle of a span is not a frame
	 * this segment can produce.
	 *
	 * The contiguity invariant survives in the units it actually holds in: the
	 * spans tile the segment without gaps, so whatever the strides, every frame
	 * the history OFFERS is one it can walk to exactly. */
	struct Link
	{
		std::vector<uint8_t> bytes;
		int64_t endFrame = 0;
		std::vector<uint8_t> note;   /* the caller's, for the frame this lands on */
	};

	struct Segment
	{
		int64_t anchorFrame = 0;
		std::vector<uint8_t> anchor;   /* a whole machine, unless spilled */
		std::vector<uint8_t> anchorNote;
		std::vector<Link> links;
		uint64_t bytes = 0;

		/* Spilled: the bytes are in the spill file at spillAt, and `anchor` and
		 * the links' bytes are empty. What frames it holds stays in memory -
		 * that is metadata, it is small, and answering "can you reach frame N"
		 * must not touch a disk. */
		bool spilled = false;
		uint64_t spillAt = 0;
		uint64_t spillLength = 0;

		int64_t lastFrame() const { return links.empty() ? anchorFrame : links.back().endFrame; }

		/* The greatest frame this segment can produce at or before `f`, or -1.
		 * Not the same as being inside the segment: with strides above one,
		 * most frames between two links are not stored anywhere. */
		int64_t nearestIn(int64_t f) const;

		/* How many links to apply to land exactly on `f`, or -1 when `f` is
		 * not one of this segment's frames. */
		int64_t stepsTo(int64_t f) const;
	};

	bool deltasAvailable() const;
	bool composeAvailable() const;
	void evict();

	/* Moves one segment out to the spill file, freeing what it held in memory.
	 * False when there is nowhere to put it or the write failed, which is not
	 * an error - the budget then falls back to dropping frames. */
	static bool writeSegmentBody(std::FILE *f, const Segment &seg);
	bool spill(Segment &seg);
	void dropSpillFile();

	/* Restores from a segment that is on disk, reading only as far along it as
	 * the target frame needs. Nothing about the segment is assembled in memory:
	 * the anchor and each link go straight from the file into the sandbox. */
	bool restoreSpilled(const Segment &seg, int64_t steps, std::string &error);

	/* Thins the bands the newest frame has just pushed a landing out of. Runs
	 * after every capture and does at most one merge per boundary, because the
	 * playhead moves one frame at a time and so exactly one landing crosses
	 * each boundary per frame. That is what keeps this off the critical path:
	 * coarsening follows DISTANCE, not the budget, so it is a little work every
	 * frame rather than a stall when the budget fills. */
	void coarsen(int64_t newestFrame);

	/* Drops the landing at `frame` if the band it has fallen into does not want
	 * one there, by composing its link into the one after it. The landings a
	 * band keeps are the multiples of its stride, which makes this idempotent
	 * and the result independent of the order frames arrive in. */
	void tidy(int64_t frame, int64_t stride);

	const HostApi *m_host = nullptr;
	void *m_obj = nullptr;
	uint64_t m_budget = 0;
	uint64_t m_bytes = 0;
	std::vector<Segment> m_segments;   /* ordered by anchorFrame, never overlapping */
	bool m_epochOpen = false;          /* an epoch is marked and a delta is wanted */

	/* Defaults for 60 frames a second, and conservative on purpose: they are
	 * what a core gets before anyone has measured it. See the band table in
	 * docs/state-manager.md for what they cost on a heavy one. */
	int64_t m_nearFrames = 120;        /* 2 s of every frame */
	int64_t m_midFrames = 1800;        /* then 30 s of one in a few */
	int64_t m_midStride = 3;
	int64_t m_farStride = 1200;        /* and beyond that, one in 20 s - which,
	                                    * being wider than a segment, collapses
	                                    * an old segment to its anchor */
	int64_t m_anchorSpacing = 600;     /* a new anchor every 10 s */

	std::string m_spillDir;
	std::FILE *m_spill = nullptr;      /* one file, appended to, holes and all */
	uint64_t m_spillBytes = 0;

public:
	~StateHistory();
	StateHistory() = default;
	StateHistory(const StateHistory &) = delete;
	StateHistory &operator=(const StateHistory &) = delete;
};

} // namespace chimera

#endif
