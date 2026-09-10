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
#include <cstdio>
#include <cstring>
#include <filesystem>
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
		const uint64_t onDisk = std::filesystem::file_size("work-history-disk/history-spill.bin");
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
		assert(std::filesystem::exists("work-history-spill/history-spill.bin"));
		assert(h.bytes() <= 512);
		assert(std::filesystem::file_size("work-history-spill/history-spill.bin") > 512);

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

	/* the spill file belongs to the history and goes with it */
	assert(!std::filesystem::exists("work-history-spill/history-spill.bin"));
	std::filesystem::remove_all("work-history-spill");

	std::remove(kPath);
	std::printf("test_state_history: ok\n");
	return 0;
}
