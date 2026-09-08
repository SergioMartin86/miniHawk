/* seekbench - what a stored frame costs to MAKE and what it costs to GET BACK.
 *
 * Capture cost was measured before (docs/state-manager.md). This measures the
 * other half: restoring a frame that is K deltas from its anchor, which is the
 * number the "a seek should stay inside a second" decision actually rests on.
 *
 * Every frame is captured, so seek's own replay is zero frames and the time
 * reported is purely restore: one anchor load plus K delta applications.
 * CHIMERA_NO_DELTAS=1 makes every frame an anchor, which is the same run
 * against whole states - the A to this B. */
#include "chimera/engine.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <string>
#include <vector>
#include <sys/stat.h>

static double now() { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec / 1e9; }
static bool slurp(const char *p, std::vector<uint8_t> &o) { FILE *f = fopen(p, "rb"); if (!f) return false; uint8_t b[65536]; size_t n; while ((n = fread(b, 1, sizeof b, f))) o.insert(o.end(), b, b + n); fclose(f); return true; }

int main(int argc, char **argv) {
	if (argc < 8) { fprintf(stderr, "usage: seekbench <pkg> <iso> <mcpx> <bios> <hdd> <frames> <budgetMB>\n"); return 2; }
	const char *pkg = argv[1], *iso = argv[2];
	std::vector<uint8_t> fw[3]; const char *ids[3] = { "mcpx", "bios", "hdd" };
	for (int i = 0; i < 3; i++) if (!slurp(argv[3 + i], fw[i])) { fprintf(stderr, "no %s\n", argv[3 + i]); return 2; }
	const uint8_t *fwd[3] = { fw[0].data(), fw[1].data(), fw[2].data() }; uint64_t fwl[3] = { fw[0].size(), fw[1].size(), fw[2].size() };
	long frames = atol(argv[6]);
	uint64_t budget = (uint64_t)atol(argv[7]) * 1024ull * 1024ull;
	const char *mode = getenv("CHIMERA_NO_DELTAS") ? "whole states" : "deltas";

	ce_gl_request(0);
	const char *err = nullptr;
	ce_session *s = ce_session_open(pkg, nullptr, 0, iso, "{}", ids, fwd, fwl, 3, nullptr, nullptr, nullptr, nullptr, 0, &err);
	if (!s) { fprintf(stderr, "open: %s\n", err ? err : "?"); return 1; }
	ce_session_movie_record(s, nullptr);

	/* Get past the boot before timing anything. A BIOS frame and a gameplay
	 * frame are not the same frame, and 100 of the former was what made the
	 * first run of this report a capture that made the machine FASTER. */
	for (long i = 0; i < 200; i++) ce_session_movie_advance(s, 0, nullptr, 0);

	/* the same frames with nothing being stored, so what is reported below is
	 * an overhead and not a total. Measured after the boot, because a BIOS
	 * frame and a gameplay frame are not the same frame. */
	double bare0 = now();
	for (long i = 0; i < 100; i++) ce_session_movie_advance(s, 0, nullptr, 0);
	double bare = (now() - bare0) / 100.0;

	ce_session_greenzone_enable(s, budget);
	double cap0 = now();
	for (long i = 0; i < frames; i++) {
		ce_session_movie_advance(s, 0, nullptr, 0);
		if ((i + 1) % 200 == 0) fprintf(stderr, "  ... %ld frames, %.1fs\n", i + 1, now() - cap0);
	}
	double cap = now() - cap0;
	int64_t stored = ce_session_greenzone_count(s);
	int64_t at = ce_session_frame(s);

	char hp[512]; snprintf(hp, sizeof hp, "%s/seekbench-history.bin", getenv("TMPDIR") ? getenv("TMPDIR") : "/tmp");
	uint64_t hbytes = 0;
	if (ce_session_history_save(s, hp, "seekbench") == 0) { struct stat st; if (stat(hp, &st) == 0) hbytes = st.st_size; }

	printf("\n=== %s: %ld frames, %ld MB budget ===\n", mode, frames, atol(argv[7]));
	printf("frames stored     %lld of %lld\n", (long long)stored, (long long)at);
	printf("history on disk   %.1f MB (%.2f MB per frame)\n", hbytes / 1048576.0, hbytes / 1048576.0 / (stored ? stored : 1));
	printf("frame, bare       %.1f ms\n", bare * 1000);
	printf("frame, capturing  %.1f ms  (+%.1f ms, %+.0f%%)\n", cap / frames * 1000, (cap / frames - bare) * 1000, (cap / frames / bare - 1) * 100);

	/* Seeks, farthest first so no seek is helped by the one before it. Each
	 * target is a stored frame, so the replay is zero frames and what is timed
	 * is the restore alone. */
	/* Every target is a stored frame, so seek's own replay is zero frames and
	 * what is timed is the restore alone. Each is done twice, from a different
	 * place both times: a number that only holds the second time is a number
	 * about the page cache, not about the design. */
	int64_t first = ce_session_greenzone_nearest(s, 0) >= 0 ? 0 : -1;
	printf("\n(first stored frame %lld, last %lld)\nseek to        1st       2nd\n",
		(long long)first, (long long)at);
	long targets[] = { frames / 8, frames / 4, frames * 3 / 8, frames / 2,
	                   frames * 5 / 8, frames * 3 / 4, frames * 7 / 8, frames - 1 };
	double worst = 0; double sum = 0; int n = 0;
	for (long t : targets) {
		long f = (long)(at - frames) + t;   /* targets are offsets into the captured stretch */
		/* not required to be stored: what is timed is "get me to frame N",
		 * which with whole states means restoring the nearest and replaying
		 * the rest. That is the comparison, not a like-for-like restore.
		 *
		 * The distance is read immediately before the seek it describes. A
		 * seek REWRITES the history behind it - the frames it replays are
		 * captured, and capturing evicts - so one read before both passes
		 * describes neither. */
		double el[2]; long back[2] = { 0, 0 };
		for (int pass = 0; pass < 2; pass++) {
			ce_session_seek(s, (long)at - 1);   /* always arrive from the same far end */
			back[pass] = f - (long)ce_session_greenzone_nearest(s, f);
			double t0 = now();
			int rc = ce_session_seek(s, f);
			el[pass] = now() - t0;
			if (rc != 0) { printf("%8ld    FAILED: %s\n", f, ce_session_last_error(s)); el[0] = el[1] = 0; break; }
		}
		if (el[1] == 0) continue;
		printf("%8ld  %7.0f ms %7.0f ms   %ld and %ld frame(s) behind the nearest stored\n",
			f, el[0] * 1000, el[1] * 1000, back[0], back[1]);
		if (el[1] > worst) worst = el[1];
		sum += el[1]; n++;
	}
	printf("\nworst seek        %.0f ms\nmean seek         %.0f ms over %d\n", worst * 1000, n ? sum / n * 1000 : 0, n);
	ce_session_free(s);
	return 0;
}
