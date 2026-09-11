/* The engine's side of the GPU bridge: a real GL context, and the dispatcher a
 * sandboxed core reaches it through.
 *
 * A core that wants a GPU cannot have one. It runs in a sandbox with no
 * syscalls, no libraries and no driver, and the only thing it may call out to
 * is one callback the host registers with miniBox - six integers in, one out.
 * So its renderer's GL calls arrive here as (opcode, pointer to an argument
 * block in GUEST memory), and this answers them on a context the engine owns.
 * The guest's memory is this process's memory, so the arguments - and the
 * vertex data and textures they point at - are read in place, uncopied. That
 * is what makes it affordable.
 *
 * The opcodes are miniBox's (extern/chimera-common-minibox/source/gl),
 * shared by every core and this host, and the list is append-only so a core
 * built against an older copy still means what it meant. A guest asks how long
 * our list is and declines us if we are behind it.
 *
 * TRAFFIC IS ONE-WAY. The guest may hand us pointers into its memory; we may
 * never hand back a pointer into ours, because the sandbox stops the guest
 * reading it - and is right to. Anything GL returns by pointer is copied into a
 * buffer the guest supplied.
 *
 * THE CONTEXT IS BORROWED, NEVER KEPT. "Current context" is one slot per
 * thread, and the frontend draws its own picture through it. Holding ours
 * across a return would take that slot away - and worse, take it away
 * INVISIBLY: a frontend that binds its context through SDL is short-circuited
 * by SDL's own cache ("already current") and never rebinds, so it goes on
 * drawing into a hidden 64x64 window forever. That is a black screen with
 * working sound, and it is what happened on Windows the first time this ran
 * against a real display. So: borrow at the first call of a frame, put back
 * exactly what was there when the frame ends (ce_gl_release).
 *
 * WHAT THIS COSTS. The GPU is outside the sandbox: outside the savestate,
 * outside the determinism the rest of a core is built on, and different on
 * every machine. A session drawn this way says so (ce_session_deterministic
 * turns 0) and a movie recorded on it carries a header saying a GPU drew, so
 * that a replay that desyncs somewhere else can be understood rather than
 * merely suffered.
 */
#include "chimera/engine.h"

#include <cstdio>   /* snprintf: both flavours report why there is no context */
#include <cstdlib>
#include <chrono>
#include <vector>
#include <ctime>    /* one ingredient of a context's identity */

#ifdef CE_GL_BRIDGE

#include <glad/gl.h>

#include "gl-bridge.h"
#include "gl-bridge-ops.h"

#include <cstdio>
#include <cstring>

#ifdef _WIN32
#include <windows.h>
#include <glad/wgl.h>
#else
#include <EGL/egl.h>
#include <EGL/eglext.h>
#ifndef EGL_PLATFORM_SURFACELESS_MESA
#define EGL_PLATFORM_SURFACELESS_MESA 0x31DD
#endif
#endif

namespace
{

int g_version;
bool g_ready;
/* Which context is current - see GL_OP_CONTEXT_ID. Zero until one is made. */
uint64_t g_context_id;
char g_description[256];

/* ---------------------------------------------------------------------------
 * Windows: a context from a window nobody sees. WGL has no headless path and a
 * pixel format has to come from somewhere, so it comes from a window that is
 * created, never shown, and never pumped.
 */
#ifdef _WIN32

HWND s_window;
HDC s_dc;
HGLRC s_context;
HMODULE s_opengl32;

GLADapiproc loader(const char *name)
{
	/* wglGetProcAddress answers for the modern entry points and returns null -
	 * or one of several unhelpful small integers - for the 1.1 ones, which live
	 * in opengl32.dll itself. Every GL loader on Windows carries this same
	 * small piece of ugliness; here it is, once. */
	PROC p = wglGetProcAddress(name);
	if (p == nullptr || p == (PROC)1 || p == (PROC)2 || p == (PROC)3 || p == (PROC)-1)
		p = GetProcAddress(s_opengl32, name);
	return (GLADapiproc)p;
}

bool create_context(char *err, int errlen)
{
	WNDCLASSA wc;
	memset(&wc, 0, sizeof wc);
	wc.lpfnWndProc = DefWindowProcA;
	wc.hInstance = GetModuleHandleA(nullptr);
	wc.lpszClassName = "ChimeraGpuBridge";
	RegisterClassA(&wc);

	s_window = CreateWindowExA(0, "ChimeraGpuBridge", "", WS_OVERLAPPEDWINDOW,
		0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
	if (s_window == nullptr)
	{
		snprintf(err, errlen, "could not make a window to take a pixel format from");
		return false;
	}

	s_dc = GetDC(s_window);
	PIXELFORMATDESCRIPTOR pfd;
	memset(&pfd, 0, sizeof pfd);
	pfd.nSize = sizeof pfd;
	pfd.nVersion = 1;
	pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
	pfd.iPixelType = PFD_TYPE_RGBA;
	pfd.cColorBits = 32;
	pfd.cDepthBits = 24;
	pfd.cStencilBits = 8;

	int format = ChoosePixelFormat(s_dc, &pfd);
	if (format == 0 || !SetPixelFormat(s_dc, format, &pfd))
	{
		snprintf(err, errlen, "no pixel format this driver will draw into");
		return false;
	}

	s_context = wglCreateContext(s_dc);
	if (s_context == nullptr || !wglMakeCurrent(s_dc, s_context))
	{
		snprintf(err, errlen, "the driver would not make a context current");
		return false;
	}

	s_opengl32 = LoadLibraryA("opengl32.dll");
	return s_opengl32 != nullptr;
}

void destroy_context()
{
	if (s_context) { wglMakeCurrent(nullptr, nullptr); wglDeleteContext(s_context); s_context = nullptr; }
	if (s_dc) { ReleaseDC(s_window, s_dc); s_dc = nullptr; }
	if (s_window) { DestroyWindow(s_window); s_window = nullptr; }
}

/* Whose context was current before we took the slot. Null is a legitimate
 * answer and must be restored as faithfully as any other. */
HGLRC s_lentContext;
HDC s_lentDC;

void save_current()
{
	s_lentContext = wglGetCurrentContext();
	s_lentDC = wglGetCurrentDC();
}

void bind_ours() { wglMakeCurrent(s_dc, s_context); }

void return_current()
{
	wglMakeCurrent(s_lentDC, s_lentContext);
	s_lentContext = nullptr;
	s_lentDC = nullptr;
}

#else

/* ---------------------------------------------------------------------------
 * Linux: EGL's surfaceless platform, which Mesa provides with or without a GPU.
 * A context with NO surface has no framebuffer 0, which is fine here - a core's
 * renderer draws into framebuffers of its own and the frame is read back from
 * those, never from the default one.
 */
EGLDisplay s_display = EGL_NO_DISPLAY;
EGLContext s_context = EGL_NO_CONTEXT;

GLADapiproc loader(const char *name)
{
	return (GLADapiproc)eglGetProcAddress(name);
}

bool create_context(char *err, int errlen)
{
	auto getPlatformDisplay = (PFNEGLGETPLATFORMDISPLAYEXTPROC)
		eglGetProcAddress("eglGetPlatformDisplayEXT");
	if (getPlatformDisplay != nullptr)
		s_display = getPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, EGL_DEFAULT_DISPLAY, nullptr);
	if (s_display == EGL_NO_DISPLAY)
		s_display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
	if (s_display == EGL_NO_DISPLAY)
	{
		snprintf(err, errlen, "no EGL display");
		return false;
	}

	if (!eglInitialize(s_display, nullptr, nullptr))
	{
		snprintf(err, errlen, "EGL would not initialise");
		return false;
	}

	if (!eglBindAPI(EGL_OPENGL_API))
	{
		snprintf(err, errlen, "this EGL has no desktop OpenGL");
		return false;
	}

	static const EGLint configAttribs[] = {
		EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
		EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
		EGL_NONE
	};
	EGLConfig config;
	EGLint configCount = 0;
	if (!eglChooseConfig(s_display, configAttribs, &config, 1, &configCount) || configCount == 0)
	{
		snprintf(err, errlen, "no EGL config offers desktop OpenGL");
		return false;
	}

	static const EGLint contextAttribs[] = {
		EGL_CONTEXT_MAJOR_VERSION, 3,
		EGL_CONTEXT_MINOR_VERSION, 3,
		EGL_NONE
	};
	s_context = eglCreateContext(s_display, config, EGL_NO_CONTEXT, contextAttribs);
	if (s_context == EGL_NO_CONTEXT)
	{
		snprintf(err, errlen, "the driver would not make an OpenGL 3.3 context");
		return false;
	}

	if (!eglMakeCurrent(s_display, EGL_NO_SURFACE, EGL_NO_SURFACE, s_context))
	{
		snprintf(err, errlen, "the driver would not make the context current");
		return false;
	}

	return true;
}

void destroy_context()
{
	if (s_display != EGL_NO_DISPLAY)
	{
		eglMakeCurrent(s_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
		if (s_context != EGL_NO_CONTEXT) eglDestroyContext(s_display, s_context);
		eglTerminate(s_display);
		s_display = EGL_NO_DISPLAY;
		s_context = EGL_NO_CONTEXT;
	}
}

/* Whose context was current before we took the slot. A frontend drawing
 * through GLX rather than EGL is invisible to eglGetCurrentContext, and there
 * is no way to put a GLX context back from here - so what it gets back is an
 * unbound slot, which is what it would have had if it had released its own. */
EGLContext s_lentContext = EGL_NO_CONTEXT;
EGLSurface s_lentDraw = EGL_NO_SURFACE;
EGLSurface s_lentRead = EGL_NO_SURFACE;
EGLDisplay s_lentDisplay = EGL_NO_DISPLAY;

void save_current()
{
	s_lentContext = eglGetCurrentContext();
	s_lentDraw = eglGetCurrentSurface(EGL_DRAW);
	s_lentRead = eglGetCurrentSurface(EGL_READ);
	s_lentDisplay = eglGetCurrentDisplay();
}

void bind_ours()
{
	eglMakeCurrent(s_display, EGL_NO_SURFACE, EGL_NO_SURFACE, s_context);
}

void return_current()
{
	if (s_lentDisplay != EGL_NO_DISPLAY)
		eglMakeCurrent(s_lentDisplay, s_lentDraw, s_lentRead, s_lentContext);
	else
		eglMakeCurrent(s_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
	s_lentContext = EGL_NO_CONTEXT;
	s_lentDisplay = EGL_NO_DISPLAY;
}

#endif

} // namespace

/* ---------------------------------------------------------------------------
 * The dispatcher.
 *
 * Entered FROM GUEST CODE, so it must be sysv64 on every host - miniBox's
 * MB_GUEST_ABI says the same thing from the other side. On Windows that is not
 * cosmetic: a win64 callee spills its shadow space over the caller's stack.
 */
#if defined(_WIN32) && defined(__GNUC__)
#define BRIDGE_ABI __attribute__((sysv_abi))
#else
#define BRIDGE_ABI
#endif

/* Set between borrow_current and return_current. Reading the driver's own idea
 * of the current context is a thread-local load and cheap, but this is on the
 * path of every single GL call a renderer makes, so it is a bool. */
static bool g_borrowed;

/* The two diagnostics below are read ONCE.
 *
 * They used to be a getenv apiece on every crossing, on the reasoning that a
 * getenv is nothing beside a GL call. It is not nothing: getenv walks the
 * environment comparing strings, and a renderer crossing this bridge does so
 * thousands of times a frame - Ruffle's wgpu backend makes tens of thousands -
 * so the walk was being paid tens of thousands of times a frame to answer a
 * question whose answer cannot change while the process runs. */
static bool glTrace()
{
	static const bool on = getenv("CHIMERA_GL_TRACE") != nullptr;
	return on;
}

static bool glCheck()
{
	static const bool on = getenv("CHIMERA_GL_CHECK") != nullptr;
	return on;
}

/* CHIMERA_GL_TIME: how long a frame spends INSIDE the driver, as against
 * inside the machine that is calling it.
 *
 * A chatty renderer and a slow one look the same from outside: the frame takes
 * 30ms either way. The question that separates them is what share of that 30ms
 * is spent below this function, and the only way to answer it is to time each
 * crossing and add them up. It is off by default because the clock read either
 * side is real - about 20ns a call, so roughly 0.15ms on a six-thousand-call
 * frame, which is worth knowing when reading the number it produces. */
static bool glTime()
{
	static const bool on = getenv("CHIMERA_GL_TIME") != nullptr;
	return on;
}

static uint64_t g_driverNs, g_driverNsAtFrame;

/* CHIMERA_GL_PROFILE: WHICH calls, not just how many.
 *
 * "Fifty thousand GL calls a frame" is not actionable; "forty thousand of them
 * are glUniform4fv" is. Counted per opcode, with the time each opcode's calls
 * took, and dumped every 300 frames. The names are not carried here - the
 * opcodes are the master list's order (miniBox source/gl/gl-entry-points.txt,
 * first entry is opcode 100) and resolving them is a job for whoever reads the
 * dump, not for a hot path. */
static bool glProfile()
{
	static const bool on = getenv("CHIMERA_GL_PROFILE") != nullptr;
	return on;
}

enum { kProfileOps = CHIMERA_GL_OP_LIST_LENGTH + 100 };
static uint64_t *g_opCalls, *g_opNs, *g_opMax, *g_opSlow;

/* A call that averages a hundred microseconds is one of two very different
 * things: every call costing a hundred, or one call in fifty costing five
 * milliseconds while the rest cost nothing. The first is overhead and the
 * second is a BLOCK - the CPU waiting for the GPU - and they want opposite
 * fixes. So the longest call and the number over 50us are kept beside the
 * total, because the average cannot tell them apart. */
static const uint64_t kSlowNs = 50000;

/* CHIMERA_GL_WHY: what the guest was DOING when it blocked.
 *
 * A profile says which call waited; it cannot say what the wait was for. The
 * calls before it can: a fence waited on straight after a readback is a
 * different problem from one waited on straight after a draw. So the last few
 * opcodes are kept in a ring, and a call that blocks prints them.
 *
 * Bounded on purpose - the first few blocks of a run say everything, and a
 * hundred thousand of them would say nothing at all. */
static unsigned g_whyRing[16];
static unsigned g_whyAt;
static int g_whyLeft = -1;

/* CHIMERA_GL_GPUTIME: is the GPU actually the one that is busy?
 *
 * The profile says the guest blocks eighteen times a frame waiting on a fence.
 * That has two readings and they want opposite fixes: the GPU genuinely has
 * that much work, or it does not and the waits are the pipeline being drained
 * for no good reason. A timestamp at the first call of a frame and another at
 * the last, read a frame later when they are certainly available, says which -
 * if the GPU's own span is most of the frame it is saturated, and if it is a
 * fraction the CPU is waiting on something it need not.
 */
static bool glGpuTime()
{
	static const bool on = getenv("CHIMERA_GL_GPUTIME") != nullptr;
	return on;
}

static GLuint g_tsQuery[2];
static bool g_tsPending;
static uint64_t g_gpuNsTotal, g_gpuFrames;

static void gpuTimeFrameBegin(void)
{
	if (!glGpuTime()) return;
	if (g_tsQuery[0] == 0) glGenQueries(2, g_tsQuery);
	if (g_tsPending)
	{
		GLuint64 a = 0, b = 0;
		glGetQueryObjectui64v(g_tsQuery[0], GL_QUERY_RESULT, &a);
		glGetQueryObjectui64v(g_tsQuery[1], GL_QUERY_RESULT, &b);
		if (b > a) { g_gpuNsTotal += (uint64_t)(b - a); g_gpuFrames++; }
		g_tsPending = false;
	}
	glQueryCounter(g_tsQuery[0], GL_TIMESTAMP);
}

static void gpuTimeFrameEnd(void)
{
	if (!glGpuTime() || g_tsQuery[1] == 0) return;
	glQueryCounter(g_tsQuery[1], GL_TIMESTAMP);
	g_tsPending = true;
	if (g_gpuFrames > 0 && g_gpuFrames % 100 == 0)
	{
		fprintf(stderr, "[ce-gl-gpu] %llu frames, GPU span %.3f ms a frame\n",
			(unsigned long long)g_gpuFrames,
			(double)g_gpuNsTotal / 1e6 / (double)g_gpuFrames);
		fflush(stderr);
	}
}

/* CHIMERA_GL_LIFETIME: is the guest binding objects that no longer exist?
 *
 * The bridge's whole contract is that GL object NAMES live in guest memory
 * while the objects themselves live in the driver. A savestate carries the
 * names and not the objects, so after a load the guest can be holding the name
 * of something that was deleted since - and the driver answers a bind on a dead
 * name by doing nothing, silently, which is a picture that is quietly wrong
 * rather than a crash.
 *
 * The driver already knows. glIsTexture and its family answer whether a name is
 * a live object, so every bind is checked against the truth. One extra GL call
 * per bind is far too dear to leave on, which is why this is a diagnostic; what
 * it produces is a per-frame count of binds that named nothing.
 *
 * A freshly generated name that has never been bound is not yet an object and
 * answers false here too, so the FIRST bind of each object counts. That is a
 * constant handful a frame; what matters is whether the number climbs. */
static bool glLifetime()
{
	static const bool on = getenv("CHIMERA_GL_LIFETIME") != nullptr;
	return on;
}

static uint64_t g_deadBinds, g_deadBindsAtFrame, g_liveBinds;

static void lifetimeCheck(uintptr_t op, uintptr_t a)
{
	GLuint name = 0;
	GLboolean live = GL_TRUE;
	switch (op)
	{
		case CHIMERA_GL_OP_glBindTexture:
			name = ((struct ChimeraGlArgs_glBindTexture *)a)->texture;
			if (name != 0) live = glIsTexture(name);
			break;
		case CHIMERA_GL_OP_glBindBuffer:
			name = ((struct ChimeraGlArgs_glBindBuffer *)a)->buffer;
			if (name != 0) live = glIsBuffer(name);
			break;
		case CHIMERA_GL_OP_glBindFramebuffer:
			name = ((struct ChimeraGlArgs_glBindFramebuffer *)a)->framebuffer;
			if (name != 0) live = glIsFramebuffer(name);
			break;
		case CHIMERA_GL_OP_glBindVertexArray:
			name = ((struct ChimeraGlArgs_glBindVertexArray *)a)->array;
			if (name != 0) live = glIsVertexArray(name);
			break;
		case CHIMERA_GL_OP_glUseProgram:
			name = ((struct ChimeraGlArgs_glUseProgram *)a)->program;
			if (name != 0) live = glIsProgram(name);
			break;
		default:
			return;
	}
	if (name == 0) return;
	if (live) g_liveBinds++; else g_deadBinds++;
}

/* ---------------------------------------------------------------------------
 * What a savestate does to the objects the guest is holding.
 *
 * The bridge's contract is that GL object NAMES live in guest memory and the
 * objects live in the driver. A savestate carries the names. It cannot carry
 * the objects, so between saving a state and loading it the driver's world
 * moves on underneath the names, and there are two ways that hurts:
 *
 *  - a name the guest still holds was DELETED since. Every call naming it is
 *    refused, silently, and that part of the picture is whatever was there.
 *  - a name the guest still holds was deleted and HANDED OUT AGAIN since, to a
 *    different object. Every call naming it succeeds and draws the wrong
 *    thing. This is the one nothing can see: no GL error, no crash, just a
 *    picture that is quietly wrong and gets worse with every reload.
 *
 * Both are counted here, exactly, by giving every name a generation that goes
 * up each time the driver hands it out. A snapshot at save time and a
 * comparison at load time is the whole measurement.
 *
 * CHIMERA_GL_STATEAUDIT=1 turns it on. It is a diagnostic, not a fix: knowing
 * the number is what decides whether a fix is worth its cost. */
static bool glAudit()
{
	static const bool on = getenv("CHIMERA_GL_STATEAUDIT") != nullptr;
	return on;
}

enum { kAuditTexture = 0, kAuditBuffer = 1, kAuditFramebuffer = 2, kAuditKinds = 3 };

/* One object's life, in frames. A name is handed out, used, deleted, and later
 * handed out again to something else entirely; each of those is an interval,
 * and the whole question this answers is which interval a savestate believes
 * in. */
struct AuditLife { int64_t born; int64_t died; };  /* died < 0 while it lives */

static std::vector<std::vector<AuditLife>> g_auditLives[kAuditKinds];
static int64_t g_auditFrame;
static uint64_t g_auditLoads, g_auditDead, g_auditReused, g_auditLeaked;

static std::vector<AuditLife> &auditSlot(int kind, GLuint name)
{
	std::vector<std::vector<AuditLife>> &all = g_auditLives[kind];
	if (all.size() <= name) all.resize((size_t)name + 1024);
	return all[name];
}

static void auditGen(int kind, GLuint name)
{
	if (!glAudit() || name == 0) return;
	auditSlot(kind, name).push_back(AuditLife{ g_auditFrame, -1 });
}

static void auditDelete(int kind, GLuint name)
{
	if (!glAudit() || name == 0) return;
	std::vector<AuditLife> &lives = auditSlot(kind, name);
	if (!lives.empty() && lives.back().died < 0) lives.back().died = g_auditFrame;
}

extern "C" void ce_gl_audit_frame(int64_t frame)
{
	if (glAudit()) g_auditFrame = frame;
}

/* What a restore to `to` does to the objects the guest is about to believe in.
 *
 * dead:   it holds the name of something deleted since - every call naming it
 *         is refused silently and that part of the picture is stale.
 * reused: it holds a name that was deleted AND handed out again to a different
 *         object. Every call naming it succeeds and draws the wrong thing.
 *         Nothing can see this one: no GL error, no crash.
 * leaked: made after the frame being restored, so the guest has just forgotten
 *         it and nothing will ever delete it.
 */
extern "C" void ce_gl_state_loaded(int64_t to)
{
	if (!glAudit()) return;
	uint64_t dead = 0, reused = 0, held = 0, leaked = 0;
	for (int k = 0; k < kAuditKinds; k++)
	{
		for (size_t n = 1; n < g_auditLives[k].size(); n++)
		{
			const std::vector<AuditLife> &lives = g_auditLives[k][n];
			if (lives.empty()) continue;
			/* which interval was this name living in at frame `to`? */
			int at = -1;
			for (size_t i = 0; i < lives.size(); i++)
			{
				if (lives[i].born <= to && (lives[i].died < 0 || lives[i].died > to)) { at = (int)i; break; }
			}
			if (at < 0)
			{
				if (lives.back().died < 0 && lives.back().born > to) leaked++;
				continue;
			}
			held++;
			const bool current = (size_t)at + 1 == lives.size();
			if (!current) reused++;             /* died and was handed out again */
			else if (lives[at].died >= 0) dead++;  /* died and never came back */
		}
	}
	g_auditLoads++;
	g_auditDead += dead;
	g_auditReused += reused;
	g_auditLeaked += leaked;
	fprintf(stderr, "[ce-gl-audit] restore %llu to frame %lld: the state believes in"
		" %llu objects - %llu deleted since, %llu handed out again since,"
		" %llu made since and now orphaned\n",
		(unsigned long long)g_auditLoads, (long long)to,
		(unsigned long long)held, (unsigned long long)dead,
		(unsigned long long)reused, (unsigned long long)leaked);
	fflush(stderr);
}

static bool glWhy()
{
	static const bool on = getenv("CHIMERA_GL_WHY") != nullptr;
	if (on && g_whyLeft < 0)
	{
		const char *v = getenv("CHIMERA_GL_WHY");
		g_whyLeft = (v != nullptr && *v >= '1' && *v <= '9') ? atoi(v) : 40;
	}
	return on;
}

static void profileDump(void)
{
	if (g_opCalls == nullptr) return;
	fprintf(stderr, "[ce-gl-profile] op,calls,ms,slow,maxus\n");
	for (int op = 0; op < kProfileOps; op++)
	{
		if (g_opCalls[op] == 0) continue;
		fprintf(stderr, "[ce-gl-profile] %d,%llu,%.3f,%llu,%.1f\n", op,
			(unsigned long long)g_opCalls[op], (double)g_opNs[op] / 1e6,
			(unsigned long long)g_opSlow[op], (double)g_opMax[op] / 1e3);
	}
	fflush(stderr);
}

/* How many calls crossed, and how many frames they were spread over: the two
 * numbers that say whether a core's renderer is chatty. Printed by
 * ce_gl_release under CHIMERA_GL_TRACE, which is once a frame. */
static uint64_t g_calls, g_callsAtFrame, g_frames;

/* Defined below, beside the pool they belong to; needed here because the frame
 * boundary is where a deferred delete comes due. */
static bool glPool();
static bool glLifetime();
static bool glAudit();
static void auditGen(int kind, GLuint name);
static void gpuTimeFrameEnd(void);

extern "C" void ce_gl_release(void)
{
	if (!g_borrowed) return;
	gpuTimeFrameEnd();
	return_current();
	g_borrowed = false;
	if (glTrace())
	{
		g_frames++;
		if (glTime())
			fprintf(stderr,
				"[ce-gl] frame %llu: %llu calls, %.3f ms in the driver"
				" (%llu so far, %.0f a frame, %.3f ms a frame)\n",
				(unsigned long long)g_frames, (unsigned long long)(g_calls - g_callsAtFrame),
				(double)(g_driverNs - g_driverNsAtFrame) / 1e6,
				(unsigned long long)g_calls, (double)g_calls / (double)g_frames,
				(double)g_driverNs / 1e6 / (double)g_frames);
		else
			fprintf(stderr, "[ce-gl] frame %llu: %llu calls (%llu so far, %.0f a frame)\n",
				(unsigned long long)g_frames, (unsigned long long)(g_calls - g_callsAtFrame),
				(unsigned long long)g_calls, (double)g_calls / (double)g_frames);
		fflush(stderr);
	}
	if (glLifetime())
	{
		fprintf(stderr, "[ce-gl-life] frame %llu: %llu binds named nothing"
			" (%llu this frame, %llu live)\n",
			(unsigned long long)g_frames, (unsigned long long)g_deadBinds,
			(unsigned long long)(g_deadBinds - g_deadBindsAtFrame),
			(unsigned long long)g_liveBinds);
		fflush(stderr);
		g_deadBindsAtFrame = g_deadBinds;
	}
	g_callsAtFrame = g_calls;
	g_driverNsAtFrame = g_driverNs;
	if (glProfile() && g_frames > 0 && g_frames % 300 == 0) profileDump();
}

static uintptr_t ce_gl_dispatch_one(uintptr_t op, uintptr_t a, uintptr_t b,
                                    uintptr_t c, uintptr_t d, uintptr_t e);

/* ---------------------------------------------------------------------------
 * Buffer names, recycled.
 *
 * Measured on a GTX 1060 (Ruffle, New Star Soccer): a hundred and thirty-nine
 * buffers created, filled with glBufferData, and deleted EVERY FRAME - the
 * counts pair exactly - costing 1.74 ms in glGenBuffers alone. Twelve
 * microseconds a call, for an entry point that is supposed to do nothing but
 * reserve a name.
 *
 * It does nothing but reserve a name when the driver is idle. It is not idle:
 * the buffers being deleted are ones the GPU is still reading, so the delete
 * is deferred and the next reservation waits behind that queue. The churn pays
 * for itself twice over.
 *
 * So a deleted buffer's name is kept rather than given back to the driver, and
 * the next request for one is answered from that list. Three things make this
 * safe rather than clever:
 *
 *  - a recycled buffer is RE-SPECIFIED before use. Every one of those
 *    hundred and thirty-nine is followed by a glBufferData, which replaces its
 *    size and its contents outright and orphans whatever the GPU still held.
 *    (Immutable storage would not allow that, but a guest across this bridge
 *    cannot have ARB_buffer_storage - it hands out a host pointer - so the
 *    mutable path is the only path here.)
 *  - only names this bridge HANDED OUT are pooled. A guest deleting something
 *    it never generated is a guest with a bug, and passing that through to the
 *    driver is how it stays visible.
 *  - the list is bounded. Past the cap the delete is a real delete, so a guest
 *    that frees far more than it allocates cannot make this a leak.
 *
 * What it does NOT do is textures, and the reason is worth recording: they are
 * created with glTexStorage2D, which is immutable. A recycled texture would
 * have to be handed back for exactly the shape it already has, and the shape is
 * not known until the call AFTER the one that has to choose. Doing it properly
 * means translating texture names throughout the bridge, which is a great deal
 * of surface for the megabyte-a-frame this would save.
 */
static bool glPool()
{
	static const bool on = getenv("CHIMERA_GL_NO_POOL") == nullptr;
	return on;
}

static std::vector<GLuint> g_bufferPool;
static std::vector<bool> g_bufferOurs;   /* indexed by name: did we hand it out? */
static const size_t kBufferPoolMax = 4096;

static void poolMarkOurs(GLuint name)
{
	if (name == 0) return;
	if (g_bufferOurs.size() <= (size_t)name) g_bufferOurs.resize((size_t)name + 1024, false);
	g_bufferOurs[name] = true;
}

static bool poolIsOurs(GLuint name)
{
	return name != 0 && (size_t)name < g_bufferOurs.size() && g_bufferOurs[name];
}


/* Textures are NOT recycled, and the reason is worth keeping.
 *
 * They are created with glTexStorage2D, which is immutable: a recycled texture
 * would have to be handed back for exactly the shape it already has, and the
 * shape is not known until the call AFTER the one that has to choose a name.
 * Doing it properly means translating texture names throughout the bridge,
 * which is a great deal of surface for the megabyte a frame it would save.
 *
 * Deferring the DELETES was tried instead - hold them a few frames so the GPU
 * is finished before the driver is told, on the theory that glGenTextures is
 * dear (eleven microseconds, against sixty nanoseconds for the glTexStorage2D
 * behind it) because it drains a queue of deferred deletes. It is not: measured
 * three ways against buffer pooling alone, it moved nothing outside the noise,
 * and glGenTextures cost the same either way. Removed rather than kept on the
 * strength of a plausible story, because it holds textures the guest has
 * finished with and that is a real cost to carry for nothing.
 */

extern "C" uintptr_t BRIDGE_ABI ce_gl_dispatch(uintptr_t op, uintptr_t a, uintptr_t b,
                                               uintptr_t c, uintptr_t d, uintptr_t e)
{
	(void)d; (void)e;
	g_calls++;

	/* The first GL call of a frame takes the context; ce_gl_release gives it
	 * back when the frame is over. Two calls per frame, not two per GL call. */
	if (!g_borrowed && g_ready)
	{
		save_current();
		bind_ours();
		g_borrowed = true;
		gpuTimeFrameBegin();
		if (glTrace())
			fprintf(stderr, "[ce-gl] borrowed the context (GL_VERSION now %s)\n",
				glGetString(GL_VERSION) ? (const char *)glGetString(GL_VERSION) : "(null)");
	}
	if (glLifetime()) lifetimeCheck(op, a);
	if (glAudit())
	{
		switch (op)
		{
			case CHIMERA_GL_OP_glDeleteTextures:
			{
				struct ChimeraGlArgs_glDeleteTextures *p = (struct ChimeraGlArgs_glDeleteTextures *)a;
				for (GLsizei i = 0; i < p->n; i++) auditDelete(kAuditTexture, p->textures[i]);
				break;
			}
			case CHIMERA_GL_OP_glDeleteBuffers:
			{
				struct ChimeraGlArgs_glDeleteBuffers *p = (struct ChimeraGlArgs_glDeleteBuffers *)a;
				for (GLsizei i = 0; i < p->n; i++) auditDelete(kAuditBuffer, p->buffers[i]);
				break;
			}
			case CHIMERA_GL_OP_glDeleteFramebuffers:
			{
				struct ChimeraGlArgs_glDeleteFramebuffers *p = (struct ChimeraGlArgs_glDeleteFramebuffers *)a;
				for (GLsizei i = 0; i < p->n; i++) auditDelete(kAuditFramebuffer, p->framebuffers[i]);
				break;
			}
			default: break;
		}
	}

	/* Recycled buffer names, before anything else looks at the opcode: see
	 * g_bufferPool. Both of these answer without reaching the driver when the
	 * pool can serve them, which is the whole point. */
	if (glPool() && op == CHIMERA_GL_OP_glGenBuffers)
	{
		struct ChimeraGlArgs_glGenBuffers *args = (struct ChimeraGlArgs_glGenBuffers *)a;
		GLsizei served = 0;
		while (served < args->n && !g_bufferPool.empty())
		{
			args->buffers[served++] = g_bufferPool.back();
			g_bufferPool.pop_back();
		}
		if (served < args->n)
		{
			glGenBuffers(args->n - served, args->buffers + served);
			for (GLsizei i = served; i < args->n; i++) poolMarkOurs(args->buffers[i]);
		}
		/* Including the pooled ones: a name the pool hands back IS a different
		 * object as far as the guest is concerned, and counting it is the whole
		 * point of the audit. */
		for (GLsizei i = 0; i < args->n; i++) auditGen(kAuditBuffer, args->buffers[i]);
		return 0;
	}
	if (glPool() && op == CHIMERA_GL_OP_glDeleteBuffers)
	{
		struct ChimeraGlArgs_glDeleteBuffers *args = (struct ChimeraGlArgs_glDeleteBuffers *)a;
		for (GLsizei i = 0; i < args->n; i++)
		{
			const GLuint name = args->buffers[i];
			if (poolIsOurs(name) && g_bufferPool.size() < kBufferPoolMax)
			{
				g_bufferPool.push_back(name);
				continue;
			}
			/* not ours, or the pool is full: a real delete, so a guest bug
			 * stays visible and a lopsided guest cannot leak */
			glDeleteBuffers(1, &name);
			if (name != 0 && (size_t)name < g_bufferOurs.size()) g_bufferOurs[name] = false;
		}
		return 0;
	}

	/* CHIMERA_GL_CHECK asks the driver, after every crossing, whether that call
	 * upset it. Nothing else can: a core's renderer sees only what this hands
	 * back, so a GL error raised out here is invisible to it and to the user -
	 * the frame simply comes out empty, with nothing anywhere saying why. That
	 * is how a whole class of bridge bugs hides, and this is how they are
	 * found. Off unless asked for; a getenv per call is nothing beside a GL
	 * call. */
	if (glCheck())
	{
		while (glGetError() != GL_NO_ERROR) { }
		const uintptr_t rv = ce_gl_dispatch_one(op, a, b, c, d, e);
		const GLenum err = glGetError();
		if (err != GL_NO_ERROR)
			fprintf(stderr, "[ce-gl!] op=%lu raised %#x\n", (unsigned long)op, (unsigned)err);
		return rv;
	}
	/* Names the driver (or the pool) just handed out, recorded AFTER the call
	 * because that is when the out parameter holds them. */
	if (glAudit())
	{
		switch (op)
		{
			case CHIMERA_GL_OP_glGenTextures:
			{
				const uintptr_t rv = ce_gl_dispatch_one(op, a, b, c, d, e);
				struct ChimeraGlArgs_glGenTextures *p = (struct ChimeraGlArgs_glGenTextures *)a;
				for (GLsizei i = 0; i < p->n; i++) auditGen(kAuditTexture, p->textures[i]);
				return rv;
			}
			case CHIMERA_GL_OP_glGenFramebuffers:
			{
				const uintptr_t rv = ce_gl_dispatch_one(op, a, b, c, d, e);
				struct ChimeraGlArgs_glGenFramebuffers *p = (struct ChimeraGlArgs_glGenFramebuffers *)a;
				for (GLsizei i = 0; i < p->n; i++) auditGen(kAuditFramebuffer, p->framebuffers[i]);
				return rv;
			}
			default: break;
		}
	}

	if (glProfile())
	{
		if (g_opCalls == nullptr)
		{
			g_opCalls = (uint64_t *)calloc(kProfileOps, sizeof(uint64_t));
			g_opNs = (uint64_t *)calloc(kProfileOps, sizeof(uint64_t));
			g_opMax = (uint64_t *)calloc(kProfileOps, sizeof(uint64_t));
			g_opSlow = (uint64_t *)calloc(kProfileOps, sizeof(uint64_t));
			if (g_opCalls == nullptr || g_opNs == nullptr
				|| g_opMax == nullptr || g_opSlow == nullptr)
			{
				free(g_opCalls); free(g_opNs); free(g_opMax); free(g_opSlow);
				g_opCalls = nullptr; g_opNs = nullptr; g_opMax = nullptr; g_opSlow = nullptr;
			}
			/* ce_gl_release marks the frame boundary, and a host that is not
			 * drawing never calls it - so a run with rendering off would
			 * otherwise collect the whole profile and print none of it. */
			else atexit(profileDump);
		}
		const auto at = std::chrono::steady_clock::now();
		const uintptr_t rv = ce_gl_dispatch_one(op, a, b, c, d, e);
		const uint64_t took = (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(
			std::chrono::steady_clock::now() - at).count();
		g_driverNs += took;
		if (g_opCalls != nullptr && op < (uintptr_t)kProfileOps)
		{
			g_opCalls[op]++;
			g_opNs[op] += took;
			if (took > g_opMax[op]) g_opMax[op] = took;
			if (took > kSlowNs) g_opSlow[op]++;
			if (glWhy())
			{
				if (took > kSlowNs && g_whyLeft > 0)
				{
					g_whyLeft--;
					fprintf(stderr, "[ce-gl-why] op %lu blocked %.0f us after:",
						(unsigned long)op, (double)took / 1e3);
					for (unsigned i = 0; i < 16; i++)
					{
						const unsigned at = (g_whyAt + i) % 16;
						if (g_whyRing[at] != 0) fprintf(stderr, " %u", g_whyRing[at]);
					}
					fprintf(stderr, "\n");
					fflush(stderr);
				}
				g_whyRing[g_whyAt] = (unsigned)op;
				g_whyAt = (g_whyAt + 1) % 16;
			}
		}
		return rv;
	}
	if (glTime())
	{
		const auto at = std::chrono::steady_clock::now();
		const uintptr_t rv = ce_gl_dispatch_one(op, a, b, c, d, e);
		g_driverNs += (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(
			std::chrono::steady_clock::now() - at).count();
		return rv;
	}
	return ce_gl_dispatch_one(op, a, b, c, d, e);
}

static uintptr_t ce_gl_dispatch_one(uintptr_t op, uintptr_t a, uintptr_t b,
                                    uintptr_t c, uintptr_t d, uintptr_t e)
{
	(void)d; (void)e;

	switch (op)
	{
		case GL_OP_LIST_LENGTH:
			/* How many entry points this host knows. The guest compares it with
			 * what it was built against and declines us if we are behind; the
			 * list being append-only is what makes that check sufficient. */
			return CHIMERA_GL_OP_LIST_LENGTH;

		case GL_OP_CONTEXT_ID:
			/* Which context these calls are landing on. A renderer keeps its
			 * GL objects by the names this context handed out, and those names
			 * live in guest memory - so they survive a savestate into a session
			 * where they name nothing, and every call using one is refused
			 * without the guest ever hearing about it. Storing this number
			 * beside them is how a renderer can tell, and rebuild. */
			return g_context_id;

		case GL_OP_VERSION:
		{
			/* Never hand a host pointer back: the guest cannot read our memory
			 * and the sandbox is right to stop it. Copied into its buffer. */
			auto *args = reinterpret_cast<GlVersionArgs *>(a);
			if (!args->out || args->size == 0) return 0;
			snprintf(reinterpret_cast<char *>(args->out), args->size, "%s", g_description);
			return 1;
		}

		/* the generated cases: one per GL entry point in the master list */
#include "gl-bridge-host.inc"

		default:
			/* A core asking for something this host has never heard of is a
			 * mismatch the handshake should have caught. Say so rather than
			 * returning a plausible zero. */
			fprintf(stderr, "chimera gl: opcode %llu has no case\n", (unsigned long long)op);
			return 0;
	}
}

/* Whether the CALLER wants hardware acceleration. Asked by the session before
 * it offers a core a bridge, and set by the frontend from the project. It is a
 * process-wide switch because the context is: one GL context, made once, shared
 * by whatever session is open, because there is only ever one.
 *
 * Separate from ce_gl_available on purpose. Wanting it and having it are
 * different facts, and a person who asked for a GPU and did not get one is owed
 * a different answer from one who never asked. */
static int32_t g_requested;

extern "C" void ce_gl_request(int32_t want) { g_requested = want ? 1 : 0; }
extern "C" int32_t ce_gl_requested(void) { return g_requested; }

extern "C" int32_t ce_gl_available(void)
{
	return g_ready ? 1 : 0;
}

extern "C" const char *ce_gl_description(void)
{
	return g_ready ? g_description : "";
}

/* Mint a fresh id. It need only DIFFER from any other - the guest compares it
 * for equality and nothing else - so it is taken from where this process sits
 * in memory (the loader decides that anew every run), the clock, and a
 * monotone count. See ce_gl_start for why it changes per session, not just
 * per context. */
static void mint_context_id()
{
	static uint64_t made;
	g_context_id = (static_cast<uint64_t>(reinterpret_cast<uintptr_t>(&g_context_id)) << 16)
		^ (static_cast<uint64_t>(time(nullptr)) << 8)
		^ (++made);
	if (g_context_id == 0) g_context_id = 1; /* 0 means "cannot tell" */
}

extern "C" int32_t ce_gl_start(char *error_out, int32_t error_len)
{
	if (g_ready)
	{
		/* The context is made once per process and shared by every session,
		 * because there is only ever one machine. But the GL objects a
		 * renderer holds are not the context's, they are that SESSION's: made
		 * during its boot, and meaningless to the next session even though the
		 * driver's context outlived them. So each session that takes the bridge
		 * gets a fresh id, and a state carried across the session boundary
		 * (a greenzone reload after close-and-reopen, all in this one process)
		 * shows the guest an id that has moved, which is its cue to rebuild.
		 * Without this the id stayed put across sessions, the guest kept the
		 * dead session's object names, and the first draw bound a gone
		 * framebuffer and aborted - a crash on reopening a saved project
		 * (issue #43). A fresh process already gets a new id from the block
		 * below; this is the same signal for the in-process case. */
		mint_context_id();
		return 1;
	}

	/* Making a context makes it current, and this runs while the frontend is
	 * on screen with a context of its own. Remember what it had, hand it back
	 * on every path out of here. */
	save_current();

	char err[192] = "";
	if (!create_context(err, (int)sizeof err))
	{
		destroy_context();
		return_current();
		if (error_out && error_len > 0) snprintf(error_out, error_len, "%s", err);
		return 0;
	}

	g_version = gladLoadGL(loader);
	if (g_version == 0)
	{
		destroy_context();
		return_current();
		if (error_out && error_len > 0)
			snprintf(error_out, error_len, "the driver's entry points would not load");
		return 0;
	}

	const char *renderer = reinterpret_cast<const char *>(glGetString(GL_RENDERER));
	const char *version = reinterpret_cast<const char *>(glGetString(GL_VERSION));
	snprintf(g_description, sizeof g_description, "%s on %s",
		version ? version : "?", renderer ? renderer : "?");

	/* A driver offering only the old fixed-function pipeline cannot run any of
	 * these renderers, and saying so here is kinder than a null call in the
	 * middle of a shader compile. */
	if (GLAD_VERSION_MAJOR(g_version) < 3)
	{
		destroy_context();
		return_current();
		if (error_out && error_len > 0)
			snprintf(error_out, error_len, "the driver offers OpenGL %d.%d, and this needs 3.3",
				GLAD_VERSION_MAJOR(g_version), GLAD_VERSION_MINOR(g_version));
		return 0;
	}

	g_ready = true;
	/* An identity the guest stores beside its GL objects and compares. Minted
	 * per session, not merely per context (see the g_ready path above). */
	mint_context_id();
	return_current();
	return 1;
}

extern "C" void ce_gl_stop(void)
{
	if (!g_ready) return;
	ce_gl_release();
	destroy_context();
	g_ready = false;
	g_context_id = 0;
	g_description[0] = 0;
}

#else /* !CE_GL_BRIDGE - a build without a GPU bridge answers honestly */

static int32_t g_requested;
extern "C" void ce_gl_request(int32_t want) { g_requested = want ? 1 : 0; }
/* A build with no bridge can still be ASKED; it simply never delivers, and the
 * session says so once rather than the caller guessing. */
extern "C" int32_t ce_gl_requested(void) { return g_requested; }
extern "C" int32_t ce_gl_available(void) { return 0; }
extern "C" const char *ce_gl_description(void) { return ""; }
extern "C" void ce_gl_release(void) { }
extern "C" void ce_gl_audit_frame(int64_t) { }
extern "C" void ce_gl_state_loaded(int64_t) { }
extern "C" uintptr_t ce_gl_dispatch(uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t) { return 0; }
extern "C" int32_t ce_gl_start(char *error_out, int32_t error_len)
{
	if (error_out && error_len > 0)
		snprintf(error_out, error_len, "this build has no GPU bridge");
	return 0;
}
extern "C" void ce_gl_stop(void) {}

#endif
