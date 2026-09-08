/* reopen - a GPU core's states must outlive the session that made them.
 *
 * The bug this exists for: the GL bridge minted its context id once per
 * PROCESS, so a second session in the same process looked to a core like the
 * context it already had. Its renderer therefore did not rebuild its GL
 * objects, went on using names that belonged to a context that was gone, and
 * the machine fell over - immediately, or a few frames later, which is worse.
 *
 * Every core that draws on the host's GPU declares gpuStatesSurviveTheContext,
 * and this is what that claim means in practice: open, play, save, close, open
 * again IN THE SAME PROCESS, load the state, and keep playing. A fresh process
 * per run - which chimera-run is - never asks the question.
 */
#include "chimera/engine.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

static bool slurp(const char *p, std::vector<uint8_t> &o)
{
	FILE *f = fopen(p, "rb");
	if (!f) return false;
	uint8_t b[65536];
	size_t n;
	while ((n = fread(b, 1, sizeof b, f))) o.insert(o.end(), b, b + n);
	fclose(f);
	return true;
}

/* how much of the frame is not black - a renderer that quietly stopped drawing
 * is the failure this would otherwise miss */
static long lit(ce_session *s)
{
	const uint32_t *v = ce_session_video(s);
	if (v == nullptr) return -1;
	const long w = ce_session_video_width(s), h = ce_session_video_height(s);
	long n = 0;
	for (long i = 0; i < w * h; i++) if (v[i] & 0xFFFFFF) n++;
	return n;
}

int main(int argc, char **argv)
{
	if (argc < 3)
	{
		fprintf(stderr, "usage: reopen <package> <rom> [--settings <json>]"
			" [--firmware <id>=<path>]... [--frames N] [--save-at K] [--after M]\n");
		return 2;
	}
	const char *pkg = argv[1], *rom = argv[2];
	std::string settings = "{}";
	long frames = 120, saveAt = 60, after = 60;
	bool inSession = false, noState = false, wantGl = true, trace = false;
	std::vector<std::string> fwIds, fwPaths;
	for (int i = 3; i < argc; i++)
	{
		std::string a = argv[i];
		if (a == "--settings" && i + 1 < argc) settings = argv[++i];
		else if (a == "--frames" && i + 1 < argc) frames = atol(argv[++i]);
		else if (a == "--save-at" && i + 1 < argc) saveAt = atol(argv[++i]);
		else if (a == "--after" && i + 1 < argc) after = atol(argv[++i]);
		/* Which half is broken, when something is. --in-session never opens a
		 * second session, so it asks about savestates alone; --no-state opens a
		 * second and loads nothing, so it asks about reopening alone. */
		else if (a == "--in-session") inSession = true;
		else if (a == "--no-state") noState = true;
		/* No host GL context at all, so a core that fails only with one says so */
		else if (a == "--no-gl") wantGl = false;
		/* which frame it died on, which is the difference between "the load
		 * broke it" and "it limped and then fell over" */
		else if (a == "--trace") trace = true;
		else if (a == "--firmware" && i + 1 < argc)
		{
			std::string spec = argv[++i];
			const size_t eq = spec.find('=');
			if (eq == std::string::npos) { fprintf(stderr, "--firmware wants <id>=<path>\n"); return 2; }
			fwIds.push_back(spec.substr(0, eq));
			fwPaths.push_back(spec.substr(eq + 1));
		}
		else { fprintf(stderr, "unknown argument: %s\n", a.c_str()); return 2; }
	}

	std::vector<std::vector<uint8_t>> fw(fwIds.size());
	std::vector<const char *> ids;
	std::vector<const uint8_t *> data;
	std::vector<uint64_t> lens;
	for (size_t i = 0; i < fwIds.size(); i++)
	{
		if (!slurp(fwPaths[i].c_str(), fw[i])) { fprintf(stderr, "cannot read %s\n", fwPaths[i].c_str()); return 2; }
		ids.push_back(fwIds[i].c_str());
		data.push_back(fw[i].data());
		lens.push_back(fw[i].size());
	}

	ce_gl_request(wantGl ? 1 : 0);
	const char *err = nullptr;
	auto open = [&]() {
		return ce_session_open(pkg, nullptr, 0, rom, settings.c_str(),
			ids.empty() ? nullptr : ids.data(),
			data.empty() ? nullptr : data.data(),
			lens.empty() ? nullptr : lens.data(), (int32_t)ids.size(),
			nullptr, nullptr, nullptr, nullptr, 0, &err);
	};

	ce_session *a = open();
	if (a == nullptr) { printf("FAIL  could not open: %s\n", err ? err : "?"); return 1; }
	const int drewA = ce_session_gpu_drew(a);
	std::vector<uint8_t> state;
	for (long i = 0; i < frames; i++)
	{
		ce_session_frame_advance(a, 0, 1);
		if (i == saveAt)
		{
			uint64_t len = 0;
			const uint8_t *p = ce_session_save_state(a, &len);
			if (p == nullptr) { printf("FAIL  could not save a state: %s\n", ce_session_last_error(a)); return 1; }
			state.assign(p, p + len);
		}
	}
	const long litA = lit(a);
	if (state.empty() && !noState) { printf("FAIL  no state was saved\n"); return 1; }
	/* A machine that never drew tells us nothing about whether drawing survives
	 * a reload - it has not booted far enough, and calling that a failure would
	 * be reporting the harness's own impatience as the core's bug. */
	if (litA <= 0)
	{
		printf("hmm   the first session drew nothing in %ld frames - give it more before believing anything\n", frames);
		return 2;
	}

	if (inSession)
	{
		if (ce_session_load_state(a, state.data(), state.size()) != 0)
		{
			printf("FAIL  the state would not load into the session that made it: %s\n", ce_session_last_error(a));
			return 1;
		}
		for (long i = 0; i < after; i++) ce_session_frame_advance(a, 0, 1);
		const long back = lit(a);
		const int drew = ce_session_gpu_drew(a);
		ce_session_free(a);
		printf("%s  gpu %d, in-session reload, lit %ld -> %ld\n", back > 0 ? "ok  " : "FAIL", drew, litA, back);
		return back > 0 ? 0 : 1;
	}
	ce_session_free(a);

	/* the whole point: a SECOND session, in the same process, after the first
	 * has gone. Its GL context is not the one those objects came from. */
	ce_session *b = open();
	if (b == nullptr) { printf("FAIL  could not reopen: %s\n", err ? err : "?"); return 1; }
	if (!noState && ce_session_load_state(b, state.data(), state.size()) != 0)
	{
		printf("FAIL  the state would not load into the second session: %s\n", ce_session_last_error(b));
		return 1;
	}
	for (long i = 0; i < after; i++)
	{
		if (trace) { fprintf(stderr, "[reopen] second session, frame %ld\n", i); fflush(stderr); }
		ce_session_frame_advance(b, 0, 1);
	}
	const long litB = lit(b);
	const int drewB = ce_session_gpu_drew(b);
	ce_session_free(b);

	printf("%s  gpu %d/%d, %ld frames then %ld more %s, lit %ld -> %ld\n",
		litB > 0 ? "ok  " : "FAIL", drewA, drewB, frames, after,
		noState ? "in a second session" : "after a reload", litA, litB);
	if (litB <= 0) { printf("      the second session drew nothing - a renderer that stopped drawing\n"); return 1; }
	return 0;
}
