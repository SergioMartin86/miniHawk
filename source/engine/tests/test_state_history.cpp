/* test_state_history.cpp - the history's shape, without a machine.
 *
 * A link spans one frame when it is captured and more once the history has
 * thinned it, so "which frames can this produce" stopped being arithmetic on
 * the anchor and became a search. That search, and the file format that has to
 * carry the strides, is what this pins. Capturing and restoring need a sandbox
 * and are proven end to end by the synthetic witness.
 */

#include "../source/state_history.hpp"

#include <algorithm>
#include <array>
#include <cassert>
#include <climits>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <fcntl.h>
#include <unistd.h>
#include <string>
#include <new>
#include <vector>

namespace
{

const char kPath[] = "test_state_history.tmp";

void put64(std::vector<uint8_t> &out, uint64_t v)
{
	const uint8_t *p = reinterpret_cast<const uint8_t *>(&v);
	out.insert(out.end(), p, p + sizeof v);
}

void putStr(std::vector<uint8_t> &out, const char *s)
{
	out.insert(out.end(), s, s + std::strlen(s));
}

void write(const std::vector<uint8_t> &bytes)
{
	std::FILE *f = std::fopen(kPath, "wb");
	assert(f != nullptr);
	if (!bytes.empty()) assert(std::fwrite(bytes.data(), 1, bytes.size(), f) == bytes.size());
	std::fclose(f);
}

/* One segment with an anchor at `anchorFrame` and links landing exactly where
 * `landings` says - which is how a thinned history looks on disk. */
std::vector<uint8_t> historyFile(const char *magic, const char *machineId,
	int64_t anchorFrame, const std::vector<int64_t> &landings)
{
	std::vector<uint8_t> out;
	putStr(out, magic);
	put64(out, std::strlen(machineId));
	putStr(out, machineId);
	put64(out, 1);                      /* one segment */
	put64(out, static_cast<uint64_t>(anchorFrame));
	put64(out, 4);                      /* an anchor of four bytes; nothing loads it here */
	out.insert(out.end(), { 1, 2, 3, 4 });
	put64(out, 0);                      /* and no note */
	put64(out, landings.size());
	for (int64_t at : landings)
	{
		put64(out, static_cast<uint64_t>(at));
		put64(out, 0);                  /* no note */
		put64(out, 2);
		out.insert(out.end(), { 9, 9 });
	}
	return out;
}

} // namespace

/* ---- a machine small enough to check by hand ----
 *
 * The history's own logic - which landings a band keeps, what a merge does to
 * the tiling, which links a restore walks - can be wrong in ways no core will
 * show you. The synthetic witness cannot: its core rewrites its whole writable
 * set every frame, so every delta fully determines the machine and any
 * subsequence of them lands in the right place. That makes it blind to exactly
 * the mistakes this file is for.
 *
 * So: a machine of numbered cells, where a frame writes only SOME of them. Now
 * a chain that skips a link, or merges the wrong pair, or mislabels where a
 * link lands, produces a machine that differs - and says so.
 *
 * The merge here is the obvious one (union, later value wins) rather than
 * miniBox's. What miniBox's does is its own to prove, and its unit tests do;
 * what is under test here is everything the engine decides around it.
 */
namespace
{

struct Machine
{
	static constexpr size_t kCells = 64;
	uint8_t cell[kCells] = { 0 };
	std::vector<uint8_t> epochBase;      /* the machine as the open epoch found it */
};

Machine g_machine;

using Cells = std::vector<std::pair<uint8_t, uint8_t>>;   /* index -> value, ascending */

Cells readCells(chimera::WbxReadCb cb, uintptr_t ud)
{
	uint32_t n = 0;
	cb(ud, &n, sizeof n);
	Cells out(n);
	for (auto &c : out) { cb(ud, &c.first, 1); cb(ud, &c.second, 1); }
	return out;
}

void writeCells(chimera::WbxWriteCb cb, uintptr_t ud, const Cells &c)
{
	const uint32_t n = static_cast<uint32_t>(c.size());
	cb(ud, &n, sizeof n);
	for (const auto &e : c) { cb(ud, &e.first, 1); cb(ud, &e.second, 1); }
}

/* Set to have the next delta load refuse, the way a damaged one would. Nothing
 * else here can produce that, and what the history does about it - leave the
 * machine somewhere real rather than half way along a chain - is the point. */
int g_refuseDeltaLoadIn = -1;

/* Set to have the next N captures fail the way a machine out of memory fails.
 * Nothing else here can produce that, and it is the one condition the history
 * is expected to survive rather than report. */
int g_outOfMemoryFor = 0;

void fakeSaveState(void *, chimera::WbxWriteCb cb, uintptr_t ud, chimera::WbxReturn *r)
{
	if (g_outOfMemoryFor > 0) { g_outOfMemoryFor--; throw std::bad_alloc(); }
	*r = {};
	cb(ud, g_machine.cell, Machine::kCells);
}

void fakeLoadState(void *, chimera::WbxReadCb cb, uintptr_t ud, chimera::WbxReturn *r)
{
	*r = {};
	cb(ud, g_machine.cell, Machine::kCells);
}

void fakeEpochBegin(void *, chimera::WbxReturn *r)
{
	*r = {};
	g_machine.epochBase.assign(g_machine.cell, g_machine.cell + Machine::kCells);
}

void fakeSaveDelta(void *, bool forward, chimera::WbxWriteCb cb, uintptr_t ud, chimera::WbxReturn *r)
{
	if (g_outOfMemoryFor > 0) { g_outOfMemoryFor--; throw std::bad_alloc(); }
	*r = {};
	if (g_machine.epochBase.empty()) { std::snprintf(r->errorMessage, sizeof r->errorMessage, "no epoch"); return; }
	Cells changed;
	for (size_t i = 0; i < Machine::kCells; i++)
	{
		if (g_machine.cell[i] == g_machine.epochBase[i]) continue;
		// forward: as the frame left it. reverse: as the frame found it.
		changed.emplace_back(static_cast<uint8_t>(i), forward ? g_machine.cell[i] : g_machine.epochBase[i]);
	}
	writeCells(cb, ud, changed);
}

void fakeLoadDelta(void *, chimera::WbxReadCb cb, uintptr_t ud, chimera::WbxReturn *r)
{
	*r = {};
	if (g_refuseDeltaLoadIn == 0)
	{
		g_refuseDeltaLoadIn = -1;
		/* a damaged delta is not a no-op: it writes part of the machine and
		 * then gives up, which is exactly what makes this worth containing */
		g_machine.cell[0] = 0xDE;
		g_machine.cell[1] = 0xAD;
		std::snprintf(r->errorMessage, sizeof r->errorMessage, "memory delta apply failed");
		return;
	}
	if (g_refuseDeltaLoadIn > 0) g_refuseDeltaLoadIn--;
	for (const auto &c : readCells(cb, ud)) g_machine.cell[c.first] = c.second;
	g_machine.epochBase.clear();
}

void fakeComposeDelta(chimera::WbxReadCb a, uintptr_t aud, chimera::WbxReadCb b, uintptr_t bud,
	chimera::WbxWriteCb out, uintptr_t oud, chimera::WbxReturn *r)
{
	*r = {};
	Cells merged = readCells(a, aud);
	for (const auto &c : readCells(b, bud))
	{
		auto it = std::lower_bound(merged.begin(), merged.end(), c.first,
			[](const std::pair<uint8_t, uint8_t> &e, uint8_t v) { return e.first < v; });
		if (it != merged.end() && it->first == c.first) it->second = c.second;   /* the later wins */
		else merged.insert(it, c);
	}
	writeCells(out, oud, merged);
}

chimera::HostApi fakeHost()
{
	chimera::HostApi api{};
	api.wbx_save_state = fakeSaveState;
	api.wbx_load_state = fakeLoadState;
	api.wbx_epoch_begin = fakeEpochBegin;
	api.wbx_save_delta = fakeSaveDelta;
	api.wbx_load_delta = fakeLoadDelta;
	api.wbx_compose_delta = fakeComposeDelta;
	return api;
}

/* The spill file, whatever it is called: the name carries the process and the
 * instance now, so that two histories handed the same directory cannot open the
 * same file. Tests ask the directory, not the name. */
std::filesystem::path spillFileIn(const std::string &dir)
{
	std::error_code ec;
	for (const auto &entry : std::filesystem::directory_iterator(dir, ec))
	{
		const std::string name = entry.path().filename().string();
		if (name.rfind("history-spill-", 0) == 0) return entry.path();
	}
	return {};
}

/* Frame n writes three cells, which cells depending on n - so a delta is a
 * PART of the machine and the order they are applied in matters. */
void advance(int64_t frame)
{
	for (int k = 0; k < 3; k++)
	{
		g_machine.cell[(frame * 7 + k * 11) % Machine::kCells] = static_cast<uint8_t>(frame);
	}
}

} // namespace

int main(void)
{
	std::string error;

	{ // an empty history round trips, and an absent one is a cold cache
		chimera::StateHistory h;
		assert(h.saveTo(kPath, "machine", error));
		assert(h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);
		assert(h.nearest(0) == -1);

		std::remove(kPath);
		assert(h.loadFrom(kPath, "machine", error));   /* no file at all is not a failure */
	}

	{ // strides above one: every landing is offered, nothing between them is
		write(historyFile("ChimeraHistory3", "machine", 8, { 10, 12, 16 }));
		chimera::StateHistory h;
		assert(h.loadFrom(kPath, "machine", error));
		assert(h.count() == 4);                        /* the anchor and three links */

		assert(h.nearest(7) == -1);                    /* before the anchor there is nothing */
		assert(h.nearest(8) == 8);
		assert(h.nearest(9) == 8);                     /* inside a span, not at its end */
		assert(h.nearest(10) == 10);
		assert(h.nearest(11) == 10);
		assert(h.nearest(12) == 12);
		assert(h.nearest(15) == 12);                   /* the four frame span */
		assert(h.nearest(16) == 16);
		assert(h.nearest(1000) == 16);                 /* past the end is the end */
	}

	{ // a history of another machine is dropped rather than refused
		write(historyFile("ChimeraHistory3", "one machine", 0, { 1, 2 }));
		chimera::StateHistory h;
		assert(h.loadFrom(kPath, "another machine", error));
		assert(h.count() == 0);
	}

	{ // and so is one an older build wrote: losing a cache costs replaying
		for (const char *older : { "ChimeraHistory1", "ChimeraHistory2" })
		{
			write(historyFile(older, "machine", 0, { 1, 2 }));
			chimera::StateHistory h;
			assert(h.loadFrom(kPath, "machine", error));
			assert(h.count() == 0);
		}
	}

	{ // something that is not a history at all is worth saying out loud
		write(historyFile("NotAHistory!!!!", "machine", 0, { 1, 2 }));
		chimera::StateHistory h;
		error.clear();
		assert(!h.loadFrom(kPath, "machine", error));
		assert(!error.empty());
		assert(h.count() == 0);
	}

	{ // links that stand still or go backwards would offer frames they cannot
	  // walk to, so the file is damaged rather than merely odd
		write(historyFile("ChimeraHistory3", "machine", 0, { 4, 4 }));
		chimera::StateHistory h;
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);

		write(historyFile("ChimeraHistory3", "machine", 0, { 6, 3 }));
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);

		write(historyFile("ChimeraHistory3", "machine", 10, { 9 }));
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);
	}

	{ // a file that stops in the middle is damage, not a short history
		auto bytes = historyFile("ChimeraHistory3", "machine", 0, { 1, 2, 3 });
		bytes.resize(bytes.size() - 5);
		write(bytes);
		chimera::StateHistory h;
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);
	}

	{ // Every frame the history offers must be a frame it can actually produce,
	  // after the bands have merged most of them away.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		chimera::StateHistory h;
		h.configure(&api, nullptr, 64ull << 20);
		/* narrow enough that coarsening runs almost every frame */
		h.bands(2, 6, 3, 12, 1000);

		std::vector<std::array<uint8_t, Machine::kCells>> truth;
		truth.resize(1);
		std::memcpy(truth[0].data(), g_machine.cell, Machine::kCells);
		h.capture(0);

		const int64_t kFrames = 120;
		for (int64_t f = 1; f <= kFrames; f++)
		{
			h.beforeAdvance();
			advance(f);
			const uint8_t note[2] = { static_cast<uint8_t>(f & 0xFF), 0xA5 };
			h.capture(f, note, sizeof note);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}

		/* the bands really did thin it, or the rest of this proves nothing */
		assert(h.count() < kFrames / 2);

		/* but not where the work is: the near band's promise is every frame,
		 * and it is the one somebody actually feels */
		for (int64_t f = kFrames - 1; f <= kFrames; f++) assert(h.nearest(f) == f);

		int64_t checked = 0;
		for (int64_t f = 0; f <= kFrames; f++)
		{
			if (h.nearest(f) != f) continue;   /* not a frame it claims to hold */
			assert(h.restore(f, error));
			assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
			checked++;
		}
		assert(checked > 4);   /* including some the bands merged their way to */

		/* The caller's note rides along, and a merge keeps the note of the frame
		 * the surviving link LANDS on - the note describes that frame, not the
		 * ones composed into it. */
		int64_t withNotes = 0;
		for (int64_t f = 1; f <= kFrames; f++)
		{
			if (h.nearest(f) != f) continue;
			size_t len = 0;
			const uint8_t *note = h.noteFor(f, len);
			assert(note != nullptr && len == 2);
			assert(note[0] == static_cast<uint8_t>(f & 0xFF) && note[1] == 0xA5);
			withNotes++;
		}
		assert(withNotes > 4);
	}

	{ // A pinned frame stays reachable however hard the bands thin around it -
	  // the marker somebody wants to jump to instantly.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		chimera::StateHistory h;
		h.configure(&api, nullptr, 64ull << 20);
		h.bands(2, 6, 3, 12, 1000);

		/* frames deliberately off every band's grid, so nothing but the pin
		 * could keep them */
		const int64_t wanted[] = { 7, 13, 31, 55 };
		for (int64_t f : wanted) h.pin(f, true);

		std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
		h.capture(0);
		for (int64_t f = 1; f <= 120; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}

		for (int64_t f : wanted)
		{
			assert(h.nearest(f) == f);      /* still there */
			assert(h.restore(f, error));
			assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
		}

		/* and unpinning lets the bands have them - checked on a second run,
		 * because coarsening a frame already past is not something that happens
		 * again just for being asked */
		chimera::StateHistory loose;
		g_machine = Machine{};
		loose.configure(&api, nullptr, 64ull << 20);
		loose.bands(2, 6, 3, 12, 1000);
		loose.capture(0);
		for (int64_t f = 1; f <= 120; f++)
		{
			loose.beforeAdvance();
			advance(f);
			loose.capture(f);
		}
		int64_t survived = 0;
		for (int64_t f : wanted) if (loose.nearest(f) == f) survived++;
		assert(survived < 4);   /* the pin was doing the work, not luck */
	}

	{ // A history whose links have been merged still survives a round trip to
	  // disk, landings and all.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		chimera::StateHistory h;
		h.configure(&api, nullptr, 64ull << 20);
		h.bands(2, 6, 3, 12, 1000);
		h.capture(0);
		std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
		for (int64_t f = 1; f <= 60; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}
		assert(h.saveTo(kPath, "fake", error));

		chimera::StateHistory back;
		back.configure(&api, nullptr, 64ull << 20);
		assert(back.loadFrom(kPath, "fake", error));
		assert(back.count() == h.count());
		for (int64_t f = 0; f <= 60; f++)
		{
			if (back.nearest(f) != f) continue;
			assert(back.restore(f, error));
			assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
		}
	}

	{ // A spill that cannot be written says so, and carries on. The history's
	  // fallback is to thin in memory, which is right and completely silent -
	  // from a piano roll it looks like the greenzone going sparse for no
	  // reason, so the fact has to be somewhere a frontend can ask for it.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		chimera::StateHistory h;
		h.configure(&api, nullptr, 512);
		h.bands(2, 6, 3, 12, 8);
		/* a directory that is not there: fopen fails exactly as it does on a
		 * disk with nothing left, without needing one */
		h.spillTo("work-history-nowhere/nor-here");
		h.capture(0);
		assert(!h.spillFailed());   /* nothing has been asked of it yet */

		for (int64_t f = 1; f <= 60; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
		}

		assert(h.spillFailed());
		assert(h.bytes() <= 512);          /* it kept its budget the other way */
		assert(h.count() > 0);             /* and it is still a history */
		assert(h.nearest(60) == 60);       /* with the work still in it */

		/* somewhere it can write is a fresh chance, and the flag says so */
		std::filesystem::create_directories("work-history-elsewhere");
		h.spillTo("work-history-elsewhere");
		assert(!h.spillFailed());
		std::filesystem::remove_all("work-history-elsewhere");
	}

	{ // Running out of memory halves the budget instead of ending the session.
	  //
	  // The history is the biggest thing Chimera holds that it does not need, so
	  // it is where the end of memory is met first - and a greenzone that has
	  // quietly become half as deep is a run that continues, where a throw out of
	  // a capture is a session lost.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		chimera::StateHistory h;
		/* high enough that four halvings still clear the floor */
		h.configure(&api, nullptr, 1024ull * 1024 * 1024);
		h.bands(2, 6, 3, 12, 8);
		h.capture(0);
		const uint64_t before = h.budget();

		/* four failures in a row: one halving is unlikely to be enough on a
		 * machine that has actually run out, so it keeps going */
		g_outOfMemoryFor = 4;
		h.beforeAdvance();
		advance(1);
		h.capture(1);
		assert(g_outOfMemoryFor == 0);        /* every one of them was answered */
		assert(h.budget() == before / 16);    /* halved once per failure */
		assert(h.budget() != 0);              /* and never off: zero is "no history" */

		/* the frame that provoked it is stored, because the last try succeeded */
		assert(h.nearest(1) == 1);

		/* and it stops at the floor rather than halving towards nothing */
		g_outOfMemoryFor = 1000;
		h.beforeAdvance();
		advance(2);
		h.capture(2);
		assert(h.budget() >= 1);
		assert(g_outOfMemoryFor > 0);         /* it gave up rather than spin */
		g_outOfMemoryFor = 0;
	}

	{ // The spill file has a budget of its own, and the oldest goes first.
	  //
	  // The memory budget is met by MOVING bytes to disk, so without this one
	  // half of what a history costs was bounded and the other half was not: six
	  // thousand Game Boy frames put 1.5GB in the file and nothing ever took any
	  // of it back.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		std::filesystem::remove_all("work-history-disk");
		std::filesystem::create_directories("work-history-disk");
		chimera::StateHistory h;
		h.configure(&api, nullptr, 512);
		h.bands(2, 6, 3, 12, 8);
		h.spillTo("work-history-disk");
		const uint64_t kDisk = 8192;   /* what the FILE may weigh */
		h.diskBudget(kDisk);
		h.capture(0);
		for (int64_t f = 1; f <= 400; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			/* held every frame, not merely at the end: a limit that is only true
			 * once is a limit nobody can rely on while they work */
			assert(h.diskBytes() <= kDisk / 2);
		}
		/* and the FILE is held too, not just the count of what is live in it -
		 * dropping the oldest without reclaiming the room it held would be a
		 * limit on paper only */
		const uint64_t onDisk = std::filesystem::file_size(spillFileIn("work-history-disk"));
		assert(onDisk <= kDisk);   /* the number given, on the number `ls` shows */

		/* what is left still works: the newest frames are reachable, which is
		 * the half of the run the oldest was dropped to protect */
		assert(h.restore(h.nearest(400), error));
		assert(h.nearest(1) < h.nearest(400));

		/* and no limit means no limit - the behaviour a year of runs had */
		chimera::StateHistory u;
		u.configure(&api, nullptr, 512);
		u.bands(2, 6, 3, 12, 8);
		std::filesystem::remove_all("work-history-nolimit");
		std::filesystem::create_directories("work-history-nolimit");
		u.spillTo("work-history-nolimit");
		u.capture(0);
		for (int64_t f = 1; f <= 400; f++) { u.beforeAdvance(); advance(f); u.capture(f); }
		/* the same run with no limit keeps everything it ever spilled, which is
		 * the behaviour this budget exists to bound */
		fprintf(stderr, "  [disk budget] bounded %llu, unbounded %llu\n",
			(unsigned long long)h.diskBytes(), (unsigned long long)u.diskBytes());
		assert(u.diskBytes() > h.diskBytes());
	}
	std::filesystem::remove_all("work-history-disk");
	std::filesystem::remove_all("work-history-nolimit");

	{ // A budget too small to hold the run: the far end goes to disk, and the
	  // frames out there are still frames the history can produce.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};

		std::filesystem::create_directories("work-history-spill");
		chimera::StateHistory h;
		/* small enough that it must spill within a few segments, and anchors
		 * often enough that there are segments to spill */
		h.configure(&api, nullptr, 512);
		h.bands(2, 6, 3, 12, 8);
		h.spillTo("work-history-spill");
		h.capture(0);

		const int64_t kFrames = 120;
		std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
		for (int64_t f = 1; f <= kFrames; f++)
		{
			h.beforeAdvance();
			advance(f);
			const uint8_t note[2] = { static_cast<uint8_t>(f & 0xFF), 0xA5 };
			h.capture(f, note, sizeof note);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}

		/* it really did spill, or this proves only that nothing broke */
		assert(!spillFileIn("work-history-spill").empty());
		assert(h.bytes() <= 512);
		assert(std::filesystem::file_size(spillFileIn("work-history-spill")) > 512);

		/* an early frame, which can only be out on disk by now */
		const int64_t old = h.nearest(12);
		assert(old >= 0 && old <= 12);
		assert(h.restore(old, error));
		assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(old)].data(), Machine::kCells) == 0);

		/* and every frame it offers, wherever it is being kept */
		int64_t checked = 0;
		for (int64_t f = 0; f <= kFrames; f++)
		{
			if (h.nearest(f) != f) continue;
			assert(h.restore(f, error));
			assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
			checked++;
		}
		assert(checked > 4);

		/* a saved history carries the spilled stretches through, and what comes
		 * back produces the same frames */
		assert(h.saveTo(kPath, "fake", error));
		chimera::StateHistory back;
		back.configure(&api, nullptr, 64ull << 20);
		assert(back.loadFrom(kPath, "fake", error));
		for (int64_t f = 0; f <= kFrames; f++)
		{
			if (h.nearest(f) != f) continue;
			assert(back.nearest(f) == f);
			assert(back.restore(f, error));
			assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
			/* the notes came through the spill file and the saved history alike */
			size_t len = 0;
			const uint8_t *note = back.noteFor(f, len);
			if (f == 0) continue;
			assert(note != nullptr && len == 2);
			assert(note[0] == static_cast<uint8_t>(f & 0xFF) && note[1] == 0xA5);
		}
	}
	{ // A stretch spilled before the far band reached it is settled onto the far
	  // grid once the band does: read back, composed a few merges a frame, and
	  // rewritten. The same run with a far stride of one - keep everything -
	  // is the control: it offers more old frames and weighs more on disk.
		const chimera::HostApi api = fakeHost();
		auto run = [&](const char *dir, int64_t farStride, chimera::StateHistory &h,
			std::vector<std::array<uint8_t, Machine::kCells>> &truth)
		{
			g_machine = Machine{};
			std::filesystem::remove_all(dir);
			std::filesystem::create_directories(dir);
			h.configure(&api, nullptr, 512);
			h.bands(2, 6, 3, farStride, 8);
			h.spillTo(dir);
			h.capture(0);
			truth.assign(1, {});
			for (int64_t f = 1; f <= 160; f++)
			{
				h.beforeAdvance();
				advance(f);
				h.capture(f);
				std::array<uint8_t, Machine::kCells> at{};
				std::memcpy(at.data(), g_machine.cell, Machine::kCells);
				truth.push_back(at);
			}
		};
		chimera::StateHistory settled, dense;
		std::vector<std::array<uint8_t, Machine::kCells>> truthS, truthD;
		run("work-history-settle", 12, settled, truthS);
		run("work-history-settle-control", 1, dense, truthD);

		/* the far band - everything older than near + mid - offers fewer frames
		 * once settled, and the file is lighter for it */
		int64_t offeredS = 0, offeredD = 0;
		for (int64_t f = 8; f < 160 - 2 - 6 - 8; f++)
		{
			if (settled.nearest(f) == f) offeredS++;
			if (dense.nearest(f) == f) offeredD++;
		}
		assert(offeredS > 0 && offeredS < offeredD);
		assert(settled.diskBytes() < dense.diskBytes());

		/* and every frame either still offers is exact, wherever it is kept */
		for (int64_t f = 0; f <= 160; f++)
		{
			if (settled.nearest(f) == f)
			{
				assert(settled.restore(f, error));
				assert(std::memcmp(g_machine.cell, truthS[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
			}
			if (dense.nearest(f) == f)
			{
				assert(dense.restore(f, error));
				assert(std::memcmp(g_machine.cell, truthD[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
			}
		}
	}
	std::filesystem::remove_all("work-history-settle");
	std::filesystem::remove_all("work-history-settle-control");

	{ // An edit that lands on the last frame of a spilled stretch starts a new
	  // stretch. A delta pushed onto the spilled one would sit in memory while
	  // every restore reads the file, where the old timeline's links still are.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};
		std::filesystem::remove_all("work-history-edit");
		std::filesystem::create_directories("work-history-edit");
		chimera::StateHistory h;
		h.configure(&api, nullptr, 512);
		h.bands(2, 6, 3, 12, 8);
		h.spillTo("work-history-edit");
		h.capture(0);
		std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
		for (int64_t f = 1; f <= 40; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}
		/* frame 8 closes the first stretch, which a 512 byte budget has spilled */
		assert(h.nearest(8) == 8);
		assert(h.restore(8, error));
		h.beforeAdvance();
		g_machine.cell[3] ^= 0x5C;   /* the edit */
		advance(9);
		std::array<uint8_t, Machine::kCells> edited{};
		std::memcpy(edited.data(), g_machine.cell, Machine::kCells);
		h.capture(9);
		assert(h.nearest(INT64_MAX) == 9);
		/* back to 8 and forward to the edited 9: the old 9 must not come back */
		assert(h.restore(8, error));
		assert(std::memcmp(g_machine.cell, truth[8].data(), Machine::kCells) == 0);
		assert(h.restore(9, error));
		assert(std::memcmp(g_machine.cell, edited.data(), Machine::kCells) == 0);

		/* and a saved history does not carry what the edit removed: an edit
		 * inside a spilled stretch, then save and load */
		const int64_t cut = h.nearest(6);   /* a landing the bands kept inside the first stretch */
		assert(cut >= 0 && cut <= 6);
		assert(h.restore(cut, error));
		h.invalidateAfter(cut);
		assert(h.nearest(INT64_MAX) == cut);
		assert(h.saveTo(kPath, "fake", error));
		chimera::StateHistory back;
		back.configure(&api, nullptr, 64ull << 20);
		assert(back.loadFrom(kPath, "fake", error));
		assert(back.nearest(INT64_MAX) == cut);
		assert(back.restore(cut, error));
		assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(cut)].data(), Machine::kCells) == 0);
	}
	std::filesystem::remove_all("work-history-edit");

	{ // A chain that will not walk leaves the machine on a frame that DID exist,
	  // says which, and gives up the stretch - rather than leaving a machine
	  // that never existed for the session to record a movie against.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};
		chimera::StateHistory h;
		h.configure(&api, nullptr, 64ull << 20);
		h.bands(4, 8, 1, 1, 1000);   /* one long stretch, every landing kept */
		h.capture(0);
		std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
		for (int64_t f = 1; f <= 30; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}
		/* the fourth delta of the walk refuses */
		g_refuseDeltaLoadIn = 3;
		int64_t landed = -1;
		assert(!h.restore(25, error, &landed));
		assert(landed == 0);                                   /* the anchor it walked from */
		assert(std::memcmp(g_machine.cell, truth[0].data(), Machine::kCells) == 0);
		assert(g_refuseDeltaLoadIn == -1);
		/* and the stretch is gone rather than waiting to fail again */
		assert(h.count() == 0);
		assert(h.nearest(25) == -1);
		g_refuseDeltaLoadIn = -1;
	}

	{ // Frame zero is always reachable, whatever the budgets do. Going back to a
	  // frame the greenzone no longer covers means starting from the beginning
	  // and replaying, and that is only possible if the beginning is still
	  // there; the encode path says so in as many words. The disk budget used to
	  // drop the oldest stretch in the file whichever it was, the first one
	  // included, and then nothing could reach the early movie at all.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};
		std::filesystem::remove_all("work-history-zero");
		std::filesystem::create_directories("work-history-zero");
		chimera::StateHistory h;
		h.configure(&api, nullptr, 256);            /* tiny: it must spill at once */
		h.bands(2, 6, 3, 12, 6);
		h.spillTo("work-history-zero");
		h.diskBudget(4096);                          /* and the file must overflow too */
		std::array<uint8_t, Machine::kCells> atZero{};
		std::memcpy(atZero.data(), g_machine.cell, Machine::kCells);
		h.capture(0);
		for (int64_t f = 1; f <= 400; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			assert(h.nearest(0) == 0);           /* at every step, not just the end */
		}
		assert(h.restore(0, error));
		assert(std::memcmp(g_machine.cell, atZero.data(), Machine::kCells) == 0);

		/* and the frames it does keep are spread over the run rather than all
		 * huddled at the end: going back to the middle must not mean replaying
		 * everything from zero */
		int64_t covered = 0;
		for (int64_t f = 0; f <= 400; f += 20)
		{
			if (h.nearest(f) >= 0 && f - h.nearest(f) <= 100) covered++;
		}
		assert(covered >= 12);
	}
	std::filesystem::remove_all("work-history-zero");

	{ // Spill files a dead session left behind are swept when a history is
	  // pointed at the directory: they are named for the process that made
	  // them, so nothing else would ever remove them, and they are gigabytes.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};
		std::filesystem::remove_all("work-history-stale");
		std::filesystem::create_directories("work-history-stale");
		{
			std::ofstream dead("work-history-stale/history-spill-999999-1.bin");
			dead << "what a session that died left";
		}
		{
			std::ofstream other("work-history-stale/keep-me.bin");
			other << "not a spill file";
		}
		chimera::StateHistory h;
		h.configure(&api, nullptr, 512);
		h.bands(2, 6, 3, 12, 8);
		h.spillTo("work-history-stale");
		assert(!std::filesystem::exists("work-history-stale/history-spill-999999-1.bin"));
		assert(std::filesystem::exists("work-history-stale/keep-me.bin"));

		/* and the one this history is using is not swept from under it */
		h.capture(0);
		for (int64_t f = 1; f <= 60; f++) { h.beforeAdvance(); advance(f); h.capture(f); }
		const auto mine = spillFileIn("work-history-stale");
		assert(!mine.empty());
		h.spillTo("work-history-stale");                 /* the same directory again */
		assert(std::filesystem::exists(mine));
		assert(h.nearest(0) == 0 && h.restore(0, error));
	}
	std::filesystem::remove_all("work-history-stale");

	{ // Turning the greenzone on again - a budget changed, a project reattached -
	  // starts from nothing, the spill file included. It used to keep the file
	  // open and its live count, so the disk budget was then held against
	  // stretches that no longer existed and the room was never given back.
		const chimera::HostApi api = fakeHost();
		g_machine = Machine{};
		std::filesystem::remove_all("work-history-again");
		std::filesystem::create_directories("work-history-again");
		chimera::StateHistory h;
		h.configure(&api, nullptr, 512);
		h.bands(2, 6, 3, 12, 8);
		h.spillTo("work-history-again");
		h.capture(0);
		for (int64_t f = 1; f <= 60; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
		}
		assert(h.diskBytes() > 0);
		assert(!spillFileIn("work-history-again").empty());

		h.configure(&api, nullptr, 4096);
		assert(h.count() == 0);
		assert(h.bytes() == 0);
		assert(h.diskBytes() == 0);           /* nothing is out there any more */
		assert(spillFileIn("work-history-again").empty());
		/* and it works from cold: a fresh anchor, frames, and every one exact */
		g_machine = Machine{};
		std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
		h.capture(0);
		for (int64_t f = 1; f <= 40; f++)
		{
			h.beforeAdvance();
			advance(f);
			h.capture(f);
			std::array<uint8_t, Machine::kCells> at{};
			std::memcpy(at.data(), g_machine.cell, Machine::kCells);
			truth.push_back(at);
		}
		for (int64_t f = 0; f <= 40; f++)
		{
			if (h.nearest(f) != f) continue;
			assert(h.restore(f, error));
			assert(std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0);
		}
	}
	std::filesystem::remove_all("work-history-again");

	{ // The history under random use, against the truth. Every other block asks
	  // one question of one path; this asks the only one that matters of all
	  // of them at once: after any sequence of frames, restores, edits, pins,
	  // budgets, spills and save/load round trips, does every frame the history
	  // still offers come back exactly as it was? Tiny budgets and small bands,
	  // so that every path runs every few operations. Deterministic per seed.
		const chimera::HostApi api = fakeHost();
		/* the history says so on stderr when its own arithmetic goes wrong;
		 * that is a failure here, not a note */
		std::fflush(stderr);
		/* the trace is somebody debugging: let it through rather than filing it */
		const bool captureErr = getenv("CHIMERA_HISTORY_TRACE") == nullptr;
		const int savedErr = captureErr ? dup(2) : -1;
		const int errFile = captureErr ? open("work-history-fuzz.err", O_WRONLY | O_CREAT | O_TRUNC, 0644) : -1;
		if (captureErr) { assert(errFile >= 0); dup2(errFile, 2); }
		for (int seed = 0; seed < 16; seed++)
		{
			uint64_t rng = 0x9E3779B97F4A7C15ull * static_cast<uint64_t>(seed + 1);
			auto rnd = [&]() { rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; return static_cast<uint32_t>(rng >> 11); };
			g_machine = Machine{};
			std::filesystem::remove_all("work-history-fuzz");
			std::filesystem::remove_all("work-history-fuzz-b");
			std::filesystem::create_directories("work-history-fuzz");
			std::filesystem::create_directories("work-history-fuzz-b");
			chimera::StateHistory h;
			const uint64_t budget = 200 + rnd() % 1500;
			const int64_t near = 1 + rnd() % 4, mid = 2 + rnd() % 8, midStride = 1 + rnd() % 3,
				farStride = 1 + rnd() % 16, spacing = 2 + rnd() % 10;
			h.configure(&api, nullptr, budget);
			h.bands(near, mid, midStride, farStride, spacing);
			if (rnd() % 4 != 0) h.spillTo("work-history-fuzz");
			if (rnd() % 2) h.diskBudget(2048 + rnd() % 8192);

			std::vector<std::array<uint8_t, Machine::kCells>> truth(1);
			std::memcpy(truth[0].data(), g_machine.cell, Machine::kCells);
			h.capture(0);
			int64_t frame = 0;
			int op = 0;
			std::string story;   /* what happened, for the seed that fails */
			auto must = [&](bool ok, const char *what, int64_t f) {
				if (ok) return;
				std::fprintf(stderr, "seed %d op %d frame %lld: %s (frame %lld): %s\nstory:%s\n", seed, op, (long long)frame, what, (long long)f, error.c_str(), story.c_str());
				std::fflush(stderr);
				std::abort();
			};
			auto machineIs = [&](int64_t f) { return std::memcmp(g_machine.cell, truth[static_cast<size_t>(f)].data(), Machine::kCells) == 0; };
			auto putBack = [&]() { std::memcpy(g_machine.cell, truth[static_cast<size_t>(frame)].data(), Machine::kCells); g_machine.epochBase.clear(); };
			auto checkAll = [&](chimera::StateHistory &x) {
				/* nothing beyond the truth: a frame from a timeline an edit ended */
				must(x.nearest(INT64_MAX) < static_cast<int64_t>(truth.size()), "offers a frame past the run's end", x.nearest(INT64_MAX));
				for (int64_t f = 0; f < static_cast<int64_t>(truth.size()); f++)
				{
					if (x.nearest(f) != f) continue;
					must(x.restore(f, error), &x == &h ? "check: restore refused" : "check of the loaded copy: restore refused", f);
					must(machineIs(f), &x == &h ? "check: came back wrong" : "check of the loaded copy: came back wrong", f);
					size_t len = 0;
					const uint8_t *note = x.noteFor(f, len);
					if (f != 0) assert(note != nullptr && len == 2 && note[0] == static_cast<uint8_t>(f & 0xFF) && note[1] == 0x5A);
				}
				putBack();
			};

			for (op = 0; op < 500; op++)
			{
				switch (rnd() % 14)
				{
				case 0:
				case 1:
				{ // back to a frame it offers
					const int64_t f = h.nearest(static_cast<int64_t>(rnd() % truth.size()));
					if (f < 0) break;
					story += " restore" + std::to_string(f);
					must(h.restore(f, error), "restore refused", f);
					must(machineIs(f), "restored wrong", f);
					frame = f;
					break;
				}
				case 2:
				{ // an edit: back to a frame it offers, the input there changes,
				  // and every frame after it is a timeline that never happens
					const int64_t f = h.nearest(static_cast<int64_t>(rnd() % truth.size()));
					if (f < 0) break;
					story += " edit@" + std::to_string(f);
					must(h.restore(f, error), "restore for the edit refused", f);
					must(machineIs(f), "restored wrong before the edit", f);
					frame = f;
					h.invalidateAfter(f);
					truth.resize(static_cast<size_t>(f) + 1);
					/* the edit itself diverges the NEXT frame's state, the way a
					 * changed input does - so it happens inside the epoch, after
					 * beforeAdvance, and lands in the delta that frame records */
					truth.push_back({});
					frame++;
					h.beforeAdvance();
					advance(frame);
					g_machine.cell[rnd() % Machine::kCells] ^= static_cast<uint8_t>(1 + rnd() % 255);
					std::memcpy(truth[static_cast<size_t>(frame)].data(), g_machine.cell, Machine::kCells);
					const uint8_t note[2] = { static_cast<uint8_t>(frame & 0xFF), 0x5A };
					h.capture(frame, note, sizeof note);
					break;
				}
				case 3:
				{
					const int64_t f = static_cast<int64_t>(rnd() % truth.size());
					const bool on = rnd() % 2 == 0;
					story += (on ? " pin" : " unpin") + std::to_string(f);
					h.pin(f, on);
					break;
				}
				case 4:
				{ // saved and loaded back: what comes back must be right
					story += " save";
					assert(h.saveTo(kPath, "fake", error));
					chimera::StateHistory back;
					back.configure(&api, nullptr, budget);
					back.bands(near, mid, midStride, farStride, spacing);
					if (rnd() % 2) back.spillTo("work-history-fuzz-b");
					assert(back.loadFrom(kPath, "fake", error));
					checkAll(back);
					break;
				}
				case 5:
					story += " disk";
					h.diskBudget(rnd() % 2 ? 0 : 1024 + rnd() % 16384);
					break;
				case 6:
					if (rnd() % 8 == 0) { story += " spillto"; h.spillTo(rnd() % 2 ? "work-history-fuzz-b" : "work-history-fuzz"); }
					break;
				default:
				{ // a frame
					story += " f";
					h.beforeAdvance();
					frame++;
					advance(frame);
					if (static_cast<int64_t>(truth.size()) <= frame) truth.resize(static_cast<size_t>(frame) + 1);
					std::memcpy(truth[static_cast<size_t>(frame)].data(), g_machine.cell, Machine::kCells);
					const uint8_t note[2] = { static_cast<uint8_t>(frame & 0xFF), 0x5A };
					h.capture(frame, note, sizeof note);
					break;
				}
				}
				if (op % 40 == 39) checkAll(h);
			}
			checkAll(h);
		}
		std::fflush(stderr);
		if (captureErr)
		{
			dup2(savedErr, 2);
			close(savedErr);
			close(errFile);
		}
		if (captureErr)
		{
			std::ifstream err("work-history-fuzz.err");
			std::string line;
			while (std::getline(err, line))
			{
				if (line.find("count was wrong") != std::string::npos || line.find("gave back") != std::string::npos)
				{
					std::fprintf(stderr, "the history's accounting complained: %s\n", line.c_str());
					assert(false);
				}
			}
		}
		std::filesystem::remove("work-history-fuzz.err");
		std::filesystem::remove_all("work-history-fuzz");
		std::filesystem::remove_all("work-history-fuzz-b");
	}

	/* the spill file belongs to the history and goes with it */
	assert(spillFileIn("work-history-spill").empty());
	std::filesystem::remove_all("work-history-spill");

	std::remove(kPath);
	std::printf("test_state_history: ok\n");
	return 0;
}
