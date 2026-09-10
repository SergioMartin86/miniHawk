/* What does going BACK cost, and what is it proportional to?
 *
 * epochbench measures the forward half: describing a frame as it happens. This
 * is the other half - rebuilding a frame from an anchor and the deltas since
 * it, which is what every seek, rerecord and TAStudio click does first. The
 * same synthetic arena as epochbench: a block of a chosen size, a slice of it
 * dirty against the baseline, a chosen number of pages written per frame.
 *
 * The number to watch is the cost of ONE delta, against the number of pages it
 * carries. A delta holds a few hundred pages; if applying it costs the same on
 * a 64MB arena as on a 2GB one, it is proportional to the delta. If it grows
 * with the arena, something is walking every page there is.
 *
 * Build and run with tests/perf/run-restorebench.sh. Not part of any gate. */
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

/* a growable byte buffer as the write sink, and a cursor over one as the source */
typedef struct { uint8_t *p; size_t n, cap; } buf;
static int sink(uintptr_t ud, const unsigned char *d, size_t n) {
	buf *b = (buf *)ud;
	if (b->n + n > b->cap) {
		size_t want = b->cap ? b->cap * 2 : 1 << 20;
		while (want < b->n + n) want *= 2;
		b->p = realloc(b->p, want);
		b->cap = want;
	}
	memcpy(b->p + b->n, d, n);
	b->n += n;
	return 0;
}
typedef struct { const uint8_t *p; size_t n, at; } cur;
static intptr_t source(uintptr_t ud, unsigned char *d, size_t n) {
	cur *c = (cur *)ud;
	size_t left = c->n - c->at;
	if (left == 0) return -1;
	if (n > left) n = left;
	memcpy(d, c->p + c->at, n);
	c->at += n;
	return (intptr_t)n;
}

int main(int argc, char **argv) {
	uintptr_t mb = argc > 1 ? (uintptr_t)atoi(argv[1]) : 512;
	size_t touch = argc > 2 ? (size_t)atoi(argv[2]) : 256;
	int frames = argc > 3 ? atoi(argv[3]) : 32;
	uintptr_t size = mb << 20;
	/* how much of the machine the anchor carries: a real one is a few megabytes
	 * of a big arena, and it is the arena's size that the walk must not see */
	uintptr_t dirtyBytes = argc > 4 ? (uintptr_t)atoi(argv[4]) << 20 : size / 8;
	mb_range a = { 0x36f00000000ull, size };
	mb_block *b = mb_block_new(a);
	mb_block_activate(b);
	mb_range r = { b->addr.start, size };
	mb_block_mmap_fixed(b, r, MB_PROT_RW, true);
	mb_block_seal(b);
	for (uintptr_t off = 0; off < dirtyBytes; off += MB_PAGESIZE)
		((volatile uint8_t *)(b->addr.start + off))[0] = 1;

	/* the anchor, then the frames after it, described as the history keeps them */
	buf anchor = { 0 };
	mb_block_save_state(b, sink, (uintptr_t)&anchor);
	buf *deltas = calloc((size_t)frames, sizeof(buf));
	size_t spread = (size / MB_PAGESIZE) / (touch ? touch : 1);
	for (int f = 0; f < frames; f++) {
		mb_block_epoch_begin(b);
		for (size_t i = 0; i < touch; i++) {
			uintptr_t page = ((i * spread) + (size_t)f) % (size / MB_PAGESIZE);
			((volatile uint8_t *)(b->addr.start + (page << MB_PAGESHIFT)))[0] = (uint8_t)(f + 1);
		}
		mb_block_delta_save(b, true, sink, (uintptr_t)&deltas[f]);
	}
	/* one more frame of writes, so the load has something to undo */
	mb_block_epoch_begin(b);
	for (size_t i = 0; i < touch; i++)
		((volatile uint8_t *)(b->addr.start + ((i * spread + 7) % (size / MB_PAGESIZE) << MB_PAGESHIFT)))[0] = 9;

	/* the seek: anchor, then every delta, a few times over - the best of them
	 * is the cost of the work, the rest is the cache warming up */
	double bestLoad = 1e9, bestApply = 1e9;
	for (int rep = 0; rep < 5; rep++) {
		cur c = { anchor.p, anchor.n, 0 };
		double t0 = now();
		if (mb_block_load_state(b, source, (uintptr_t)&c) != 0) { fprintf(stderr, "load failed\n"); return 1; }
		double t1 = now();
		for (int f = 0; f < frames; f++) {
			cur d = { deltas[f].p, deltas[f].n, 0 };
			if (mb_block_delta_apply(b, source, (uintptr_t)&d) != 0) { fprintf(stderr, "apply failed\n"); return 1; }
		}
		double t2 = now();
		if (t1 - t0 < bestLoad) bestLoad = t1 - t0;
		if (t2 - t1 < bestApply) bestApply = t2 - t1;
	}
	/* the machine must be the one the last delta described */
	for (size_t i = 0; i < touch; i++) {
		uintptr_t page = ((i * spread) + (size_t)(frames - 1)) % (size / MB_PAGESIZE);
		if (((volatile uint8_t *)(b->addr.start + (page << MB_PAGESHIFT)))[0] != (uint8_t)frames) {
			fprintf(stderr, "page %zu holds the wrong frame\n", (size_t)page);
			return 1;
		}
	}
	printf("%5lu MB arena, %7zu pages, %4zu written/frame, %3d deltas:"
		" anchor %7.3f ms (%5.1f MB)  apply %7.3f ms/delta (%6.0f KB)  => seek %7.3f ms\n",
		(unsigned long)mb, (size_t)(size / MB_PAGESIZE), touch, frames,
		bestLoad * 1e3, anchor.n / 1048576.0,
		bestApply / frames * 1e3, deltas[0].n / 1024.0,
		(bestLoad + bestApply) * 1e3);
	mb_block_free(b);
	return 0;
}
