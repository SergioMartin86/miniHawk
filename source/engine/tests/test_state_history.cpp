/* test_state_history.cpp - the history's shape, without a machine.
 *
 * A link spans one frame when it is captured and more once the history has
 * thinned it, so "which frames can this produce" stopped being arithmetic on
 * the anchor and became a search. That search, and the file format that has to
 * carry the strides, is what this pins. Capturing and restoring need a sandbox
 * and are proven end to end by the synthetic witness.
 */

#include "../source/state_history.hpp"

#include <cassert>
#include <cstdio>
#include <cstring>
#include <string>
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
	put64(out, landings.size());
	for (int64_t at : landings)
	{
		put64(out, static_cast<uint64_t>(at));
		put64(out, 2);
		out.insert(out.end(), { 9, 9 });
	}
	return out;
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
		write(historyFile("ChimeraHistory2", "machine", 8, { 10, 12, 16 }));
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
		write(historyFile("ChimeraHistory2", "one machine", 0, { 1, 2 }));
		chimera::StateHistory h;
		assert(h.loadFrom(kPath, "another machine", error));
		assert(h.count() == 0);
	}

	{ // and so is one an older build wrote: losing a cache costs replaying
		write(historyFile("ChimeraHistory1", "machine", 0, { 1, 2 }));
		chimera::StateHistory h;
		assert(h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);
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
		write(historyFile("ChimeraHistory2", "machine", 0, { 4, 4 }));
		chimera::StateHistory h;
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);

		write(historyFile("ChimeraHistory2", "machine", 0, { 6, 3 }));
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);

		write(historyFile("ChimeraHistory2", "machine", 10, { 9 }));
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);
	}

	{ // a file that stops in the middle is damage, not a short history
		auto bytes = historyFile("ChimeraHistory2", "machine", 0, { 1, 2, 3 });
		bytes.resize(bytes.size() - 5);
		write(bytes);
		chimera::StateHistory h;
		assert(!h.loadFrom(kPath, "machine", error));
		assert(h.count() == 0);
	}

	std::remove(kPath);
	std::printf("test_state_history: ok\n");
	return 0;
}
