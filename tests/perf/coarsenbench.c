/* What does coarsening cost, and why did it cost that?
 *
 * The history thins its bands by merging a landing into its neighbour, and the
 * neighbour KEEPS the span - so that neighbour accumulates, and every later
 * merge re-reads all of it. Collapsing a stretch that way is quadratic in the
 * bytes, which is invisible until a core's frames stop overlapping neatly.
 *
 * This is that loop with real deltas and no core: a frame writes a working set
 * plus some churn, its delta is composed into the accumulator, and the time and
 * the sizes are reported. `overlap` is the percentage of a frame's pages that
 * are the same ones the last frame wrote, which is the property that decides
 * everything here. `cap` is the engine's rule (state_history.cpp, kMergeCap):
 * refuse a merge whose two sides together exceed it, and leave the landing in
 * place - denser than asked, which is safe.
 *
 * Build and run with tests/perf/run-coarsenbench.sh. Not part of any gate: it
 * answers a question about cost, not about correctness.
 */
#include "minibox_internal.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static double now(void)
{
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return t.tv_sec + t.tv_nsec * 1e-9;
}

typedef struct { uint8_t *p; size_t len, cap, pos; } buf;

static int bwrite(uintptr_t ud, const unsigned char *d, uintptr_t n)
{
	buf *b = (buf *)ud;
	if (b->len + n > b->cap) { b->cap = (b->len + n) * 2; b->p = realloc(b->p, b->cap); }
	memcpy(b->p + b->len, d, n);
	b->len += n;
	return 0;
}

static intptr_t bread(uintptr_t ud, uint8_t *d, uintptr_t n)
{
	buf *b = (buf *)ud;
	size_t left = b->len - b->pos;
	if (n > left) n = left;
	memcpy(d, b->p + b->pos, n);
	b->pos += n;
	return (intptr_t)n;
}

int main(int argc, char **argv)
{
	uintptr_t mb = argc > 1 ? (uintptr_t)atoi(argv[1]) : 2048;
	size_t touch = argc > 2 ? (size_t)atoi(argv[2]) : 256;
	int merges = argc > 3 ? atoi(argv[3]) : 400;
	int overlap = argc > 4 ? atoi(argv[4]) : 90;
	size_t cap = argc > 5 ? (size_t)atoi(argv[5]) * 1048576 : 0;
	const bool streaming = getenv("STREAM") != NULL;

	uintptr_t size = mb << 20;
	mb_range a = { 0x36f00000000ull, size };
	mb_block *b = mb_block_new(a);
	mb_block_activate(b);
	mb_range r = { b->addr.start, size };
	mb_block_mmap_fixed(b, r, MB_PROT_RW, true);
	mb_block_seal(b);
	size_t npages = size / MB_PAGESIZE;

	buf acc = { 0 };
	double t = 0;
	size_t kept = 0;
	int refused = 0, did = 0;
	for (int f = 0; f < merges + 1; f++) {
		mb_block_epoch_begin(b);
		for (size_t i = 0; i < touch; i++) {
			size_t page = (i * 977) % npages;    /* the shared working set */
			if ((int)(i * 100 / touch) >= overlap) page = ((size_t)f * 131 + i * 7919) % npages;
			((volatile uint8_t *)(b->addr.start + (page << MB_PAGESHIFT)))[0] = (uint8_t)f;
		}
		buf cur = { 0 };
		mb_block_delta_save(b, true, bwrite, (uintptr_t)&cur);
		if (!acc.len) { acc = cur; continue; }
		if (cap && acc.len + cur.len > cap) {
			kept += acc.len;
			refused++;
			free(acc.p);
			acc = cur;
			continue;
		}
		buf out = { 0 };
		double t0 = now();
		int rc;
		if (streaming) {
			acc.pos = 0; cur.pos = 0;
			rc = mb_block_delta_compose(bread, (uintptr_t)&acc, bread, (uintptr_t)&cur, bwrite, (uintptr_t)&out);
		} else {
			rc = mb_block_delta_compose_mem(acc.p, acc.len, cur.p, cur.len, bwrite, (uintptr_t)&out, NULL);
		}
		t += now() - t0;
		if (rc != 0) { fprintf(stderr, "compose failed: %d\n", rc); return 1; }
		free(acc.p); free(cur.p);
		acc = out;
		did++;
	}
	printf("%s %4zu pages/frame, %2d%% overlap, cap %4zu MB: %8.1f ms in all, %6.3f ms per frame,"
		" %3d merged / %3d left, band holds %6.1f MB\n",
		streaming ? "stream" : "mem   ", touch, overlap, cap / 1048576,
		t * 1e3, t / merges * 1e3, did, refused, (kept + acc.len) / 1048576.0);
	mb_block_free(b);
	return 0;
}
