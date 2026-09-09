/* What does one frame of greenzoning cost, and what is it proportional to?
 *
 * Not a core and not a game: the epoch and delta machinery alone, on a block of
 * a chosen size with a chosen number of pages written per frame. That is the
 * whole question. A PS2 arena is hundreds of thousands of pages and a frame
 * dirties a few hundred of them, so anything per-frame that is proportional to
 * the ARENA rather than to the frame is the cost - and for a long time all of
 * it was. See docs/state-manager.md, "What a captured frame costs".
 *
 * Writes are scattered as widely as the arena allows, so every run of dirty
 * pages is one page long and every hold costs its own syscall. That is the
 * worst case on purpose: a real machine writes in clusters.
 *
 * Build and run with tests/perf/run-epochbench.sh. Not part of any gate - it
 * answers a question about cost, not about correctness. */
#include "minibox_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static double now(void) {
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return ts.tv_sec + ts.tv_nsec * 1e-9;
}

static size_t g_written;
static int sink(uintptr_t ud, const unsigned char *p, size_t n) { (void)ud; (void)p; g_written += n; return 0; }

int main(int argc, char **argv) {
	uintptr_t mb = argc > 1 ? (uintptr_t)atoi(argv[1]) : 512;
	size_t touch = argc > 2 ? (size_t)atoi(argv[2]) : 256;
	int frames = argc > 3 ? atoi(argv[3]) : 200;
	(void)argc;

	uintptr_t size = mb << 20;
	mb_range a = { 0x36f00000000ull, size };
	mb_block *b = mb_block_new(a);
	mb_block_activate(b);
	mb_range r = { b->addr.start, size };
	mb_block_mmap_fixed(b, r, MB_PROT_RW, true);
	mb_block_seal(b);

	/* a machine that has been running: a slice of it is already dirty against
	 * the sealed baseline, which is what a real one looks like after boot */
	for (uintptr_t off = 0; off < size / 8; off += MB_PAGESIZE)
		((volatile uint8_t *)(b->addr.start + off))[0] = 1;

	double tBegin = 0, tWrite = 0, tDelta = 0;
	size_t spread = (size / MB_PAGESIZE) / (touch ? touch : 1);
	for (int f = 0; f < frames; f++) {
		double t0 = now();
		mb_block_epoch_begin(b);
		double t1 = now();
		/* the frame's writes, scattered the way a machine's are */
		for (size_t i = 0; i < touch; i++) {
			uintptr_t page = ((i * spread) + (size_t)f) % (size / MB_PAGESIZE);
			((volatile uint8_t *)(b->addr.start + (page << MB_PAGESHIFT)))[0] = (uint8_t)f;
		}
		double t2 = now();
		g_written = 0;
		mb_block_delta_save(b, true, sink, 0);
		double t3 = now();
		tBegin += t1 - t0;
		tWrite += t2 - t1;
		tDelta += t3 - t2;
	}
	printf("%5lu MB arena, %6zu pages, %4zu written/frame:"
		" open %6.3f ms  faults %6.3f ms  delta %6.3f ms  => %6.3f ms/frame\n",
		(unsigned long)mb, (size_t)(size / MB_PAGESIZE), touch,
		tBegin / frames * 1e3, tWrite / frames * 1e3, tDelta / frames * 1e3,
		(tBegin + tWrite + tDelta) / frames * 1e3);
	mb_block_free(b);
	return 0;
}
