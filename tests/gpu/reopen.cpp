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
 *
 * What "keep playing" has to mean is the whole of it. Not crashing is not the
 * check: a renderer holding a dead context's objects comes back garbled, or
 * silent, or stuck on one frame, and every one of those survives "it did not
 * crash and something was lit". So the reopened run is compared against a run
 * that never stopped, frame for frame, pixel for pixel and sample for sample.
 * They have to be identical.
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

/* how much of the frame is not black - context for a person reading the output,
 * and nothing more: a machine can draw the wrong thing brightly */
static long lit(ce_session *s)
{
	const uint32_t *v = ce_session_video(s);
	if (v == nullptr) return -1;
	const long w = ce_session_video_width(s), h = ce_session_video_height(s);
	long n = 0;
	for (long i = 0; i < w * h; i++) if (v[i] & 0xFFFFFF) n++;
	return n;
}

/* What the machine actually produced, frame by frame - every pixel and every
 * sample, folded together.
 *
 * This is the check that matters and the one this harness first went without.
 * "It did not crash and something was lit" passes a machine whose picture came
 * back garbled, whose sound came back as noise, or which came back stuck on one
 * frame - which is precisely what a renderer holding a dead context's objects
 * looks like from the outside. A run that stopped and came back has to produce
 * the SAME frames as one that never stopped, and nothing weaker is worth
 * asserting. */
struct Fold
{
	uint64_t audio = 1469598103934665603ull;   /* FNV-1a */
	std::vector<uint32_t> lastFrame;
	int32_t w = 0, h = 0;

	static void mix(uint64_t &acc, const void *p, size_t n)
	{
		const auto *b = static_cast<const uint8_t *>(p);
		for (size_t i = 0; i < n; i++) { acc ^= b[i]; acc *= 1099511628211ull; }
	}

	void frame(ce_session *s)
	{
		int32_t n = 0;
		if (const int16_t *a = ce_session_audio(s, &n))
		{
			mix(audio, &n, sizeof n);
			mix(audio, a, (size_t)n * 2 * sizeof(int16_t));
		}
		w = ce_session_video_width(s);
		h = ce_session_video_height(s);
		if (const uint32_t *v = ce_session_video(s)) lastFrame.assign(v, v + (size_t)w * h);
	}
};

/* The bytes of one domain, for looking at what actually changed. */
static std::vector<uint8_t> domainBytes(ce_session *s, int32_t index)
{
	const int64_t size = ce_session_domain_size(s, index);
	std::vector<uint8_t> buf((size_t)(size > 0 ? size : 0));
	if (!buf.empty()) ce_session_domain_read(s, index, 0, buf.data(), size);
	return buf;
}

/* Where two copies of a domain part company: how many bytes, in how many runs,
 * and where the first one is. A single contiguous block the size of a texture
 * says something very different from scattered words. */
static void reportDiff(const std::vector<uint8_t> &a, const std::vector<uint8_t> &b)
{
	if (a.size() != b.size() || a.empty()) { printf("      (sizes differ)\n"); return; }
	size_t bad = 0, runs = 0, first = 0;
	bool in = false, seen = false;
	size_t runStart = 0, biggest = 0, biggestAt = 0;
	for (size_t i = 0; i < a.size(); i++)
	{
		if (a[i] != b[i])
		{
			bad++;
			if (!seen) { first = i; seen = true; }
			if (!in) { in = true; runs++; runStart = i; }
		}
		else if (in)
		{
			in = false;
			if (i - runStart > biggest) { biggest = i - runStart; biggestAt = runStart; }
		}
	}
	if (in && a.size() - runStart > biggest) { biggest = a.size() - runStart; biggestAt = runStart; }
	printf("      %zu bytes differ (%.3f%% of the domain) in %zu run(s); first at 0x%zx,"
	       " longest run %zu bytes at 0x%zx\n",
		bad, 100.0 * bad / a.size(), runs, first, biggest, biggestAt);
}

/* Per domain, per frame: which memory diverged and on which frame. "The machine
 * came back different" is where a diagnosis starts, not where it ends. */
static std::vector<uint64_t> domainHashes(ce_session *s)
{
	std::vector<uint64_t> out;
	std::vector<uint8_t> buf;
	for (int32_t i = 0; i < ce_session_domain_count(s); i++)
	{
		const int64_t size = ce_session_domain_size(s, i);
		if (size <= 0 || size > (256 << 20) || ce_session_domain_writable(s, i) == 0) continue;
		buf.resize((size_t)size);
		uint64_t h = 1469598103934665603ull;
		if (ce_session_domain_read(s, i, 0, buf.data(), size) == size) Fold::mix(h, buf.data(), buf.size());
		out.push_back(h);
	}
	return out;
}

/* The machine's own memory, which is the thing that must not differ.
 *
 * A hardware renderer's picture can wobble - the same commands on the same
 * driver need not put back the same bytes, and Chimera says so on screen - so
 * comparing frames alone would call ordinary jitter a bug. The MACHINE has no
 * such licence: a TAS is only a TAS because the same inputs from the same state
 * produce the same machine. So RAM is the assertion, and the picture is
 * measured rather than asserted - a wobble is a fraction of a percent, and a
 * renderer drawing through a dead context's objects is not. */
static uint64_t ramHash(ce_session *s, int64_t *bytesOut = nullptr, int *countOut = nullptr)
{
	uint64_t h = 1469598103934665603ull;
	int64_t total = 0;
	int used = 0;
	std::vector<uint8_t> buf;
	for (int32_t i = 0; i < ce_session_domain_count(s); i++)
	{
		const int64_t size = ce_session_domain_size(s, i);
		/* the machine's own memory, not its disks or its saved data */
		if (size <= 0 || size > (256 << 20) || ce_session_domain_writable(s, i) == 0) continue;
		buf.resize((size_t)size);
		if (ce_session_domain_read(s, i, 0, buf.data(), size) != size) continue;
		Fold::mix(h, buf.data(), buf.size());
		total += size;
		used++;
	}
	if (bytesOut != nullptr) *bytesOut = total;
	if (countOut != nullptr) *countOut = used;
	return h;
}

/* The frame as a file somebody can look at. "The video was compromised" is a
 * claim about what a person sees, and no statistic settles it. */
static void writePpm(const std::string &path, const Fold &f)
{
	if (f.lastFrame.empty()) return;
	FILE *out = fopen(path.c_str(), "wb");
	if (out == nullptr) return;
	fprintf(out, "P6\n%d %d\n255\n", f.w, f.h);
	for (uint32_t px : f.lastFrame)
	{
		const uint8_t rgb[3] = { uint8_t(px >> 16), uint8_t(px >> 8), uint8_t(px) };
		fwrite(rgb, 1, 3, out);
	}
	fclose(out);
}

/* How much of the picture is not the same picture, in percent. */
static double framesDiffer(const Fold &a, const Fold &b)
{
	if (a.w != b.w || a.h != b.h) return 100.0;
	if (a.lastFrame.size() != b.lastFrame.size() || a.lastFrame.empty()) return 100.0;
	size_t bad = 0;
	for (size_t i = 0; i < a.lastFrame.size(); i++)
	{
		if ((a.lastFrame[i] & 0xFFFFFF) != (b.lastFrame[i] & 0xFFFFFF)) bad++;
	}
	return 100.0 * bad / a.lastFrame.size();
}

int main(int argc, char **argv)
{
	if (argc < 3)
	{
		fprintf(stderr, "usage: reopen <package> <rom> [--settings <json>]"
			" [--firmware <id>=<path>]... [--frames N] [--after M]\n"
			"  --frames N   boot this many frames, then save\n"
			"  --after M    then compare M frames, straight run against reopened\n");
		return 2;
	}
	const char *pkg = argv[1], *rom = argv[2];
	std::string settings = "{}";
	std::string shot;
	long frames = 120, after = 60;
	bool inSession = false, noState = false, wantGl = true, trace = false, perFrame = false;
	std::vector<std::string> fwIds, fwPaths;
	for (int i = 3; i < argc; i++)
	{
		std::string a = argv[i];
		if (a == "--settings" && i + 1 < argc) settings = argv[++i];
		else if (a == "--frames" && i + 1 < argc) frames = atol(argv[++i]);
		else if (a == "--save-at" && i + 1 < argc) frames = atol(argv[++i]);   /* an older spelling of --frames */
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
		/* <prefix>-straight.ppm and <prefix>-reopened.ppm, to be looked at */
		else if (a == "--shot" && i + 1 < argc) shot = argv[++i];
		/* which memory went first, and on which frame */
		else if (a == "--where") perFrame = true;
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
	/* Boot far enough in to be somewhere worth saving. */
	for (long i = 0; i < frames; i++) ce_session_frame_advance(a, 0, 1);

	std::vector<uint8_t> state;
	uint64_t len = 0;
	const uint8_t *saved = ce_session_save_state(a, &len);
	if (saved == nullptr) { printf("FAIL  could not save a state: %s\n", ce_session_last_error(a)); return 1; }
	state.assign(saved, saved + len);

	/* Then EXACTLY the frames the reopened run will be asked for, so the two
	 * are the same question. Getting this wrong - folding a different stretch
	 * on each side - makes every core on earth look broken, which is how the
	 * first version of this file read. */
	Fold straight;
	std::vector<std::vector<uint64_t>> straightPerFrame;
	std::vector<uint8_t> straightFirstFrame;
	for (long i = 0; i < after; i++)
	{
		ce_session_frame_advance(a, 0, 1);
		straight.frame(a);
		if (perFrame)
		{
			straightPerFrame.push_back(domainHashes(a));
			if (i == 0) straightFirstFrame = domainBytes(a, 0);
		}
	}
	int64_t ramBytes = 0;
	int ramDomains = 0;
	const uint64_t straightRam = ramHash(a, &ramBytes, &ramDomains);
	/* A comparison over nothing passes every time, so say what was compared. */
	if (ramBytes == 0) printf("hmm   this core exposes no writable memory - RAM proves nothing here\n");
	else printf("      comparing %d memory domain(s), %.1f MB\n", ramDomains, ramBytes / 1048576.0);
	const long litA = lit(a);
	if (state.empty() && !noState) { printf("FAIL  no state was saved\n"); return 1; }
	/* A machine that never drew tells us nothing about whether drawing survives
	 * a reload - it has not booted far enough, and calling that a failure would
	 * be reporting the harness's own impatience as the core's bug. */
	if (litA <= 0)
	{
		if (!shot.empty()) writePpm(shot + "-straight.ppm", straight);
		printf("hmm   the first session drew nothing in %ld frames - give it more before believing anything\n", frames + after);
		return 2;
	}

	if (inSession)
	{
		/* back to the save point, then the same stretch again */
		if (ce_session_load_state(a, state.data(), state.size()) != 0)
		{
			printf("FAIL  the state would not load into the session that made it: %s\n", ce_session_last_error(a));
			return 1;
		}
		Fold again;
		for (long i = 0; i < after; i++) { ce_session_frame_advance(a, 0, 1); again.frame(a); }
		const uint64_t ram = ramHash(a);
		const long back = lit(a);
		const int drew = ce_session_gpu_drew(a);
		const double px = framesDiffer(straight, again);
		const bool sound = again.audio == straight.audio;
		ce_session_free(a);
		const bool ramOk = ramBytes == 0 || ram == straightRam;
		const bool ok = ramBytes != 0 ? ramOk : (px <= 1.0 && sound);
		printf("%s  gpu %d, in-session reload, lit %ld -> %ld | RAM %s, audio %s, picture %.2f%% different\n",
			ok ? "ok  " : "FAIL", drew, litA, back,
			ramBytes == 0 ? "n/a" : (ramOk ? "same" : "DIFFERS"), sound ? "same" : "differs", px);
		return ok ? 0 : 1;
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
	Fold reopened;
	for (long i = 0; i < after; i++)
	{
		if (trace) { fprintf(stderr, "[reopen] second session, frame %ld\n", i); fflush(stderr); }
		ce_session_frame_advance(b, 0, 1);
		reopened.frame(b);
		if (perFrame && (size_t)i < straightPerFrame.size())
		{
			const auto here = domainHashes(b);
			for (size_t d = 0; d < here.size() && d < straightPerFrame[i].size(); d++)
			{
				if (here[d] == straightPerFrame[i][d]) continue;
				printf("      first divergence: frame %ld after the load, domain %d (%s)\n",
					i + 1, (int)d, ce_session_domain_name(b, (int32_t)d));
				if (i == 0 && d == 0 && !straightFirstFrame.empty())
				{
					reportDiff(straightFirstFrame, domainBytes(b, 0));
				}
				perFrame = false;   /* the first one is the whole story */
				break;
			}
		}
	}
	const uint64_t reopenedRam = ramHash(b);
	if (!shot.empty()) { writePpm(shot + "-straight.ppm", straight); writePpm(shot + "-reopened.ppm", reopened); }
	const long litB = lit(b);
	const int drewB = ce_session_gpu_drew(b);
	const double px = framesDiffer(straight, reopened);
	const bool sound = reopened.audio == straight.audio;
	ce_session_free(b);

	/* With no state loaded the second session started from power-on and has no
	 * business matching anything; there is only the crash to report. */
	if (noState)
	{
		printf("%s  gpu %d/%d, %ld frames then %ld more in a second session, lit %ld -> %ld\n",
			litB > 0 ? "ok  " : "FAIL", drewA, drewB, frames, after, litA, litB);
		return litB > 0 ? 0 : 1;
	}

	/* What may be asserted depends on what the core lets us see. With memory
	 * domains, RAM is the machine and the picture is a symptom. Without them -
	 * ruffle exposes none - the picture and the sound ARE the machine as far as
	 * anyone outside can tell, and a difference there is the whole finding. */
	const bool ramOk = ramBytes == 0 || reopenedRam == straightRam;
	const bool lookOk = px <= 1.0 && sound;
	const bool same = ramBytes != 0 ? ramOk : lookOk;

	printf("%s  gpu %d/%d, booted %ld then %ld compared after a reload, lit %ld -> %ld"
	       " | RAM %s, audio %s, picture %.2f%% different\n",
		same ? "ok  " : "FAIL", drewA, drewB, frames, after, litA, litB,
		ramBytes == 0 ? "n/a" : (ramOk ? "same" : "DIFFERS"), sound ? "same" : "differs", px);

	if (!ramOk)
	{
		printf("      The machine came back DIFFERENT. Not a crash, and not the\n"
		       "      renderer wobbling: the same inputs from the same state did not\n"
		       "      produce the same machine, which is the one thing a TAS cannot\n"
		       "      survive. A run reopened onto this state desyncs.\n");
		return 1;
	}
	if (!lookOk)
	{
		printf("      %s the picture and sound are all this core shows, and they\n"
		       "      came back different. Something the renderer holds did not\n"
		       "      survive the new context.\n",
			ramBytes == 0 ? "With no memory domains to check," : "The machine is right, but");
		return ramBytes == 0 ? 1 : 0;
	}
	return 0;
}
