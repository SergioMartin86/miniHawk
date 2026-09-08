#include "host_dyn.hpp"

#include <cstdio>
#include <cstring>
#include <map>
#include <string>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace chimera {

namespace {

HostApi g_api;
void *g_lib = nullptr;
bool g_ok = false;
std::string g_error;

void *openBesideUs(const char *name)
{
#if defined(_WIN32)
	HMODULE self = nullptr;
	wchar_t path[MAX_PATH];
	if (GetModuleHandleExW(
			GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
			reinterpret_cast<LPCWSTR>(&openBesideUs), &self)
		&& GetModuleFileNameW(self, path, MAX_PATH) != 0)
	{
		std::wstring dir(path);
		auto slash = dir.find_last_of(L"\\/");
		if (slash != std::wstring::npos)
		{
			std::wstring full = dir.substr(0, slash + 1);
			for (const char *c = name; *c != '\0'; c++) full.push_back(static_cast<wchar_t>(*c));
			if (HMODULE h = LoadLibraryW(full.c_str())) return h;
		}
	}
	return LoadLibraryA(name);
#else
	Dl_info info;
	if (dladdr(reinterpret_cast<void *>(&openBesideUs), &info) != 0 && info.dli_fname != nullptr)
	{
		std::string dir(info.dli_fname);
		auto slash = dir.find_last_of('/');
		if (slash != std::string::npos)
		{
			std::string full = dir.substr(0, slash + 1) + name;
			if (void *h = dlopen(full.c_str(), RTLD_NOW | RTLD_LOCAL)) return h;
		}
	}
	return dlopen(name, RTLD_NOW | RTLD_LOCAL);
#endif
}

void *sym(void *lib, const char *name)
{
#if defined(_WIN32)
	return reinterpret_cast<void *>(GetProcAddress(static_cast<HMODULE>(lib), name));
#else
	return dlsym(lib, name);
#endif
}

template <typename T>
bool bind(T &fn, const char *name)
{
	fn = reinterpret_cast<T>(sym(g_lib, name));
	return fn != nullptr;
}

void loadOnce()
{
#if defined(_WIN32)
	g_lib = openBesideUs("libminiboxhost.dll");
#else
	g_lib = openBesideUs("libminiboxhost.so");
#endif
	if (g_lib == nullptr)
	{
		g_error = "libminiboxhost not found beside libchimera";
		return;
	}
	g_ok = bind(g_api.wbx_build_info, "wbx_build_info")
		&& bind(g_api.wbx_create_host, "wbx_create_host")
		&& bind(g_api.wbx_destroy_host, "wbx_destroy_host")
		&& bind(g_api.wbx_activate_host, "wbx_activate_host")
		&& bind(g_api.wbx_deactivate_host, "wbx_deactivate_host")
		&& bind(g_api.wbx_get_proc_addr, "wbx_get_proc_addr")
		&& bind(g_api.wbx_get_callback_addr, "wbx_get_callback_addr")
		&& bind(g_api.wbx_seal, "wbx_seal")
		/* not in the && chain: a host without it still works, it just cannot
		 * tell a cached greenzone that its machine changed underneath it */
		&& (bind(g_api.wbx_machine_hash, "wbx_machine_hash") || true)
		&& bind(g_api.wbx_mount_file, "wbx_mount_file")
		&& bind(g_api.wbx_mount_file_path, "wbx_mount_file_path")
		&& bind(g_api.wbx_save_state, "wbx_save_state")
		&& bind(g_api.wbx_load_state, "wbx_load_state");
	if (!g_ok) g_error = "libminiboxhost is missing expected wbx_ symbols";

	/* The optional four, bound on their own: a host without them is not a
	 * broken host, it is one that predates epochs, and the state history falls
	 * back to whole states. Only offered as a set - half of it is no use. */
	if (g_ok
		&& !(bind(g_api.wbx_epoch_begin, "wbx_epoch_begin")
			&& bind(g_api.wbx_save_delta, "wbx_save_delta")
			&& bind(g_api.wbx_load_delta, "wbx_load_delta")
			&& bind(g_api.wbx_get_epoch_page_count, "wbx_get_epoch_page_count")))
	{
		g_api.wbx_epoch_begin = nullptr;
		g_api.wbx_save_delta = nullptr;
		g_api.wbx_load_delta = nullptr;
		g_api.wbx_get_epoch_page_count = nullptr;
	}
	/* Composition on its own: epochs without it means a history that keeps
	 * every link it captured, which is dense and expensive but not wrong. */
	if (!g_ok || !bind(g_api.wbx_compose_delta, "wbx_compose_delta")) g_api.wbx_compose_delta = nullptr;
}

} // namespace

const HostApi *hostApi(const char **error)
{
	static const bool loaded = (loadOnce(), true);
	(void)loaded;
	if (!g_ok)
	{
		if (error != nullptr) *error = g_error.c_str();
		return nullptr;
	}
	return &g_api;
}

#if defined(_WIN32)

namespace {

constexpr int MAX_ARGS = 6; // depart0..depart6, matching miniBox
constexpr int STUB_SIZE = 32;
constexpr int STUB_COUNT = 256;

uintptr_t g_departs[MAX_ARGS + 1];
bool g_departsLoaded = false;
uint8_t *g_stubPage = nullptr;
int g_stubsUsed = 0;
std::map<std::pair<uintptr_t, int>, uintptr_t> g_stubs;

bool loadDeparts()
{
	if (g_departsLoaded) return true;
	const char *err = nullptr;
	if (hostApi(&err) == nullptr) return false;
	for (int i = 0; i <= MAX_ARGS; i++)
	{
		char name[16];
		std::snprintf(name, sizeof name, "depart%d", i);
		g_departs[i] = reinterpret_cast<uintptr_t>(sym(g_lib, name));
		if (g_departs[i] == 0) return false;
	}
	g_stubPage = static_cast<uint8_t *>(VirtualAlloc(
		nullptr, STUB_SIZE * STUB_COUNT, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
	if (g_stubPage == nullptr) return false;
	g_departsLoaded = true;
	return true;
}

} // namespace

uintptr_t bridgeGuestCall(uintptr_t guestEntry, int argCount)
{
	if (guestEntry == 0) return 0;
	if (argCount < 0 || argCount > MAX_ARGS) return 0;
	if (!loadDeparts()) return 0;
	auto key = std::make_pair(guestEntry, argCount);
	auto it = g_stubs.find(key);
	if (it != g_stubs.end()) return it->second;
	if (g_stubsUsed >= STUB_COUNT) return 0;

	/* 48 B8 <target>  mov rax, imm64
	 * 49 BB <departN> mov r11, imm64
	 * 41 FF E3        jmp r11
	 * (r11: caller-saved in both conventions, never carries an argument) */
	uint8_t *stub = g_stubPage + g_stubsUsed * STUB_SIZE;
	stub[0] = 0x48; stub[1] = 0xB8;
	std::memcpy(stub + 2, &guestEntry, 8);
	stub[10] = 0x49; stub[11] = 0xBB;
	std::memcpy(stub + 12, &g_departs[argCount], 8);
	stub[20] = 0x41; stub[21] = 0xFF; stub[22] = 0xE3;
	FlushInstructionCache(GetCurrentProcess(), stub, STUB_SIZE);
	g_stubsUsed++;
	g_stubs.emplace(key, reinterpret_cast<uintptr_t>(stub));
	return reinterpret_cast<uintptr_t>(stub);
}

#else

uintptr_t bridgeGuestCall(uintptr_t guestEntry, int argCount)
{
	(void)argCount;
	return guestEntry; // host and guest are both sysv64
}

#endif

} // namespace chimera
