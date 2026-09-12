# Chimera build: mediated by meson, from a Linux host, for BOTH targets.
#
#   Linux artifacts (.so + the platform-neutral managed build):
#     meson setup build/meson-linux --prefix "$(pwd)/build" --libdir dll
#     meson compile -C build/meson-linux
#     meson install -C build/meson-linux
#
#   Windows artifacts (.dll, cross-built via mingw-w64):
#     meson setup build/meson-windows --prefix "$(pwd)/build" --libdir dll \
#         --cross-file extern/meson/mingw-w64.ini
#     meson compile -C build/meson-windows
#     meson install -C build/meson-windows
#
# The managed solution (dotnet build) is invoked from the native (Linux) dir only:
# its IL output is platform-neutral and serves both targets. Native libraries are
# built per-target. Per the reproducibility pillar, none of this affects movie
# determinism; that belongs to core packages.
project(
  'chimera',
  ['c', 'cpp'],
  version: '0.1',
  default_options: ['buildtype=release', 'cpp_std=c++17'],
  meson_version: '>=1.3',
)

fs = import('fs')
host_windows = host_machine.system() == 'windows'
repo_root = meson.project_source_root()

cc = meson.get_compiler('c')
m_dep = cc.find_library('m', required: false)
dl_dep = cc.find_library('dl', required: false)
thread_dep = dependency('threads')

# ---------------------------------------------------------------------------
# libchimerahash: hardware-accelerated hashing (in-repo sources)
# ---------------------------------------------------------------------------
chimerahash_src = files(
  'extern/LibChimeraHash/chimerainterface.c',
  'extern/LibChimeraHash/common/x86_features.c',
  'extern/LibChimeraHash/crc32/crc32_braid.c',
  'extern/LibChimeraHash/crc32/crc32_fold_pclmulqdq.c',
  'extern/LibChimeraHash/crc32/crc32_fold_vpclmulqdq.c',
  'extern/LibChimeraHash/sha1/sha1_sha.c',
)
shared_library(
  'chimerahash',
  chimerahash_src,
  include_directories: include_directories('extern/LibChimeraHash/common'),
  gnu_symbol_visibility: 'hidden',
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# lua54: the Lua 5.4 runtime (stock upstream sources, pinned submodule)
# ---------------------------------------------------------------------------
lua_src = []
foreach f : [
  'lapi', 'lauxlib', 'lbaselib', 'lcode', 'lcorolib', 'lctype', 'ldblib',
  'ldebug', 'ldo', 'ldump', 'lfunc', 'lgc', 'linit', 'liolib', 'llex',
  'lmathlib', 'lmem', 'loadlib', 'lobject', 'lopcodes', 'loslib', 'lparser',
  'lstate', 'lstring', 'lstrlib', 'ltable', 'ltablib', 'ltm', 'lundump',
  'lutf8lib', 'lvm', 'lzio',
]
  lua_src += files('extern/lua' / f + '.c')
endforeach
lua54 = shared_library(
  'lua54',
  lua_src,
  name_prefix: host_windows ? '' : 'lib',
  c_args: host_windows ? ['-DLUA_BUILD_AS_DLL'] : ['-DLUA_USE_LINUX'],
  dependencies: [m_dep, dl_dep],
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# e_sqlite3: SQLite with the canonical SQLitePCLRaw configuration
# ---------------------------------------------------------------------------
sqlite_args = [
  '-DSQLITE_ENABLE_COLUMN_METADATA', '-DSQLITE_ENABLE_FTS3_PARENTHESIS',
  '-DSQLITE_ENABLE_FTS4', '-DSQLITE_ENABLE_FTS5', '-DSQLITE_ENABLE_JSON1',
  '-DSQLITE_ENABLE_MATH_FUNCTIONS', '-DSQLITE_ENABLE_RTREE',
  '-DSQLITE_ENABLE_SNAPSHOT', '-DSQLITE_DEFAULT_FOREIGN_KEYS=1',
]
if host_windows
  sqlite_args += '-DSQLITE_API=__declspec(dllexport)'
endif
shared_library(
  'e_sqlite3',
  files('extern/sqlite3/sqlite3/sqlite3.c'),
  name_prefix: host_windows ? '' : 'lib',
  c_args: sqlite_args,
  dependencies: [m_dep, dl_dep, thread_dep],
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# cimgui: Dear ImGui C bindings, pinned to ImGui.NET's binding version
# ---------------------------------------------------------------------------
cimgui_args = ['-DIMGUI_DISABLE_OBSOLETE_FUNCTIONS=1']
if host_windows
  cimgui_args += '-DIMGUI_IMPL_API=extern "C" __declspec(dllexport)'
else
  cimgui_args += '-DIMGUI_IMPL_API=extern "C"'
endif
shared_library(
  'cimgui',
  files(
    'extern/cimgui/cimgui.cpp',
    'extern/cimgui/imgui/imgui.cpp',
    'extern/cimgui/imgui/imgui_demo.cpp',
    'extern/cimgui/imgui/imgui_draw.cpp',
    'extern/cimgui/imgui/imgui_tables.cpp',
    'extern/cimgui/imgui/imgui_widgets.cpp',
  ),
  name_prefix: host_windows ? '' : 'lib',
  cpp_args: cimgui_args,
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# luasocket: socket/core + mime/core Lua modules (for user scripts)
# ---------------------------------------------------------------------------
luasocket_common = [
  'luasocket', 'timeout', 'buffer', 'io', 'auxiliar', 'options', 'inet',
  'except', 'select', 'tcp', 'udp', 'compat',
]
luasocket_src = []
foreach f : luasocket_common + [host_windows ? 'wsocket' : 'usocket']
  luasocket_src += files('extern/luasocket/src' / f + '.c')
endforeach
luasocket_args = []
luasocket_deps = []
if host_windows
  luasocket_args += '-DLUASOCKET_API=__declspec(dllexport)'
  luasocket_deps += cc.find_library('ws2_32')
endif
# both modules must be named "core" on disk; meson target names and per-dir
# outputs are unique, so each builds in its own subdir (extern/meson/luasocket-*)
lua_inc = include_directories('extern/lua')
subdir('extern/meson/luasocket-socket')
subdir('extern/meson/luasocket-mime')

# ---------------------------------------------------------------------------
# zstd: savestate compression; upstream's own meson build, nested
# ---------------------------------------------------------------------------
nested_tool = files('extern/meson/nested-build.sh')
cross_arg = host_windows ? '--cross-file=' + repo_root / 'extern/meson/mingw-w64.ini' : ''
zstd_out = host_windows ? 'libzstd.dll' : 'libzstd.so'
custom_target(
  'zstd',
  output: zstd_out,
  command: [
    nested_tool, 'meson',
    repo_root / 'extern/zstd/build/meson',
    '@OUTDIR@' / 'zstd-build',
    '@OUTPUT@',
    cross_arg,
    '-Dbin_programs=false', '-Dbin_tests=false', '-Dbin_contrib=false',
  ],
  console: true,
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# SDL2 + OpenAL Soft: cmake-driven, via the wrapper script
# ---------------------------------------------------------------------------
cmake_toolchain = host_windows ? repo_root / 'extern/meson/mingw-toolchain.cmake' : ''
custom_target(
  'sdl2',
  output: host_windows ? 'SDL2.dll' : 'libSDL2.so',
  command: [
    nested_tool, 'cmake',
    repo_root / 'extern/SDL2',
    '@OUTDIR@' / 'sdl2-build',
    '@OUTPUT@',
    cmake_toolchain,
  ],
  console: true,
  install: true,
  install_dir: 'dll',
)
custom_target(
  'openal',
  output: host_windows ? 'OpenAL32.dll' : 'libopenal.so',
  command: [
    nested_tool, 'cmake',
    repo_root / 'extern/openal-soft',
    '@OUTDIR@' / 'openal-build',
    '@OUTPUT@',
    cmake_toolchain,
    '-DALSOFT_UTILS=OFF', '-DALSOFT_EXAMPLES=OFF', '-DALSOFT_TESTS=OFF',
    '-DLIBTYPE=SHARED',
  ],
  console: true,
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# libminiboxhost: the miniBox waterbox host (from the extern/chimera-common-minibox submodule).
# Chimera is waterbox-only: this host loads every core's core.wbx, so it ships
# with the frontend. It is self-contained C (no external deps, no guest
# toolchain), so we compile it directly here rather than driving miniBox's own
# meson build - the guest toolchain (musl) is a core-author kit, never built here.
# ---------------------------------------------------------------------------
minibox_host = repo_root / 'extern/chimera-common-minibox/source/host'
minibox_host_src = files(
  minibox_host / 'diag.c',
  minibox_host / 'sha256.c',
  minibox_host / 'memblock.c',
  minibox_host / 'tripguard.c',
  minibox_host / 'interop_bin.c',
  minibox_host / 'context.c',
  minibox_host / 'elf.c',
  minibox_host / 'fs.c',
  minibox_host / 'host.c',
  minibox_host / 'threading.c',
  minibox_host / 'cinterface.c',
  minibox_host / 'calling_convention_adapters.c',
  minibox_host / (host_windows ? 'pal_win.c' : 'pal_linux.c'),
)
# Provenance compiled into the host, matching what miniBox's own build stamps: the
# frontend shows it and a movie records it, so every binary in the emulation path can
# say where it came from. Inputs only, never a timestamp.
minibox_commit = run_command(
  'git', '-C', repo_root / 'extern/chimera-common-minibox', 'rev-parse', '--short=12', 'HEAD',
  check: false).stdout().strip()
if minibox_commit == ''
  minibox_commit = 'unknown'
endif
minibox_build_os = run_command(
  'sh', '-c', '. /etc/os-release 2>/dev/null && printf "%s %s" "$ID" "$VERSION_ID" || uname -s',
  check: false).stdout().strip()

shared_library(
  'miniboxhost',
  minibox_host_src,
  name_prefix: 'lib',  # libminiboxhost.{so,dll} on both targets (the adapter resolves this name)
  include_directories: include_directories('extern/chimera-common-minibox/source/host'),
  c_args: [
    '-fno-strict-aliasing',
    '-DMB_BUILD_COMMIT="' + minibox_commit + '"',
    '-DMB_BUILD_COMPILER="' + meson.get_compiler('c').get_id() + ' ' + meson.get_compiler('c').version() + '"',
    '-DMB_BUILD_OS="' + minibox_build_os + '"',
  ],
  dependencies: [dl_dep, thread_dep],
  gnu_symbol_visibility: 'default',
  install: true,
  install_dir: 'dll',
)

# ---------------------------------------------------------------------------
# libchimera: the engine. The functional side of the frontend, migrating out
# of C# component by component (see docs/engine-migration.md). Its C ABI in
# engine/include/chimera/engine.h is a frozen spec; the frontend NativeInvokes it
# the same way it does libminiboxhost.
# ---------------------------------------------------------------------------
engine_inc = include_directories('source/engine/include')
engine_src = files(
  'source/engine/source/engine.cpp',
  'source/engine/source/cache_bridge.cpp',
  'source/engine/source/movie_log.cpp',
  'source/engine/source/movie_text.cpp',
  'source/engine/source/state_container.cpp',
  'source/engine/source/zstd_dyn.cpp',
  'source/engine/source/sha1.cpp',
  'source/engine/source/progress.cpp',
  'source/engine/source/firmware.cpp',
  'source/engine/source/conditions.cpp',
  'source/engine/source/multifile.cpp',
  'source/engine/source/manifest_util.cpp',
  'source/engine/source/project.cpp',
  'source/engine/source/file_io.cpp',
  'source/engine/source/package.cpp',
  'source/engine/source/host_dyn.cpp',
  'source/engine/source/movie_entry.cpp',
  'source/engine/source/session.cpp',
  'source/engine/source/state_history.cpp',
  'source/engine/source/media_maker.cpp',
  'source/engine/source/gl_bridge.cpp',
  'extern/miniz/miniz.c',  # vendored (MIT): the zip half of the state container
  'extern/cjson/cJSON.c',  # vendored (MIT): bundle JSON
)
# Provenance: this repo's commit, since the engine lives in-tree. Inputs only.
engine_commit = run_command(
  'git', '-C', repo_root, 'rev-parse', '--short=12', 'HEAD',
  check: false).stdout().strip()
if engine_commit == ''
  engine_commit = 'unknown'
endif
engine_build_args = [
  '-I' + repo_root / 'extern/chimera-common-minibox/source/cache',
  '-DCE_BUILD_COMMIT="' + engine_commit + '"',
  '-DCE_BUILD_COMPILER="' + meson.get_compiler('cpp').get_id() + ' ' + meson.get_compiler('cpp').version() + '"',
  '-DCE_BUILD_OS="' + minibox_build_os + '"',
  # miniz's zlib-name-compat macros (#define compress mz_compress) leak into
  # everything that includes it; we use the mz_ names
  '-DMINIZ_NO_ZLIB_COMPATIBLE_NAMES',
  # the ce_ ABI is exported explicitly (see engine.h); cJSON must not export
  # its own symbols, which on mingw would also disable auto-exports
  '-DCHIMERA_BUILDING',
  '-DCJSON_HIDE_SYMBOLS',
]

# The GPU bridge (docs/gpu-bridge.md): a real GL context in the engine, so a
# sandboxed core's renderer can drive a GPU instead of a software rasteriser.
# Opt in - it is the one part of the engine that depends on the machine it runs
# on, and a core drawn this way is no longer deterministic.
gl_bridge = get_option('gl_bridge')
gl_bridge_src = []
gl_bridge_deps = []
if gl_bridge
  # Both halves of the bridge are generated from miniBox's master list, which
  # is where the numbering lives so that every core and this host agree.
  gl_bridge_gen = custom_target('ce-gl-bridge',
    output: ['gl-bridge-host.inc', 'gl-bridge-ops.h', 'gl-bridge-guest.cpp'],
    command: [find_program('python3'),
      repo_root / 'extern/chimera-common-minibox/source/gl/gen-gl-bridge.py',
      repo_root / 'extern/glad/include/glad/gl.h',
      repo_root / 'extern/chimera-common-minibox/source/gl/gl-entry-points.txt',
      '@OUTDIR@'],
    # the list and the generator are inputs: without saying so, appending an
    # entry point leaves the host still answering the old numbering
    depend_files: [
      files('extern/chimera-common-minibox/source/gl/gl-entry-points.txt'),
      files('extern/chimera-common-minibox/source/gl/gen-gl-bridge.py'),
    ])

  gl_bridge_src = [gl_bridge_gen[0], files('extern/glad/src/gl.c')]
  engine_build_args += ['-DCE_GL_BRIDGE',
    '-I' + repo_root / 'extern/glad/include',
    '-I' + repo_root / 'extern/chimera-common-minibox/source/gl',
    '-I' + meson.current_build_dir()]

  if host_windows
    gl_bridge_src += files('extern/glad/src/wgl.c')
    gl_bridge_deps = [meson.get_compiler('cpp').find_library('opengl32', required: true)]
  else
    gl_bridge_deps = [meson.get_compiler('cpp').find_library('EGL', required: true)]
  endif
endif
shared_library(
  'chimera',
  engine_src + gl_bridge_src,
  name_prefix: 'lib',  # libchimera.{so,dll} on both targets (the frontend resolves this name)
  include_directories: engine_inc,
  cpp_args: engine_build_args,
  c_args: engine_build_args,
  dependencies: [dl_dep] + gl_bridge_deps,  # zstd_dyn loads libzstd from beside the library
  gnu_symbol_visibility: 'default',
  install: true,
  install_dir: 'dll',
)
executable(
  'chimera-run',
  ['source/engine/tools/chimera-run.cpp'] + engine_src + gl_bridge_src,
  include_directories: engine_inc,
  cpp_args: engine_build_args,
  c_args: engine_build_args,
  dependencies: [dl_dep] + gl_bridge_deps,
  install: true,
  install_dir: 'dll',  # beside libminiboxhost + libzstd, which it loads from its own directory
)

if not meson.is_cross_build()
  foreach engine_test : ['sha1', 'movie_log', 'movie_text', 'movie_entry', 'state_container', 'package', 'multifile', 'project', 'firmware', 'state_history', 'media']
    test(
      'engine_' + engine_test,
      executable(
        'test_' + engine_test,
        ['source/engine/tests/test_' + engine_test + '.cpp'] + engine_src + gl_bridge_src,
        include_directories: engine_inc,
        cpp_args: engine_build_args + ['-UNDEBUG'],  # these tests ARE asserts
        c_args: engine_build_args,
        dependencies: [dl_dep] + gl_bridge_deps,
      ),
      # (the state container test finds libzstd the way the shipped library
      # does: beside its own binary, which in the build dir it is)
    )
  endforeach
endif

# ---------------------------------------------------------------------------
# The managed solution: one platform-neutral build, from the Linux dir only
# ---------------------------------------------------------------------------
if not meson.is_cross_build()
  dotnet = find_program('dotnet', required: false)
  if dotnet.found()
    run_target(
      'frontend',
      # nodeReuse/shared-compilation off: lingering msbuild worker processes
      # otherwise hold the output pipe open ~15 minutes after the build ends,
      # and `meson compile frontend` blocks on them
      command: [dotnet, 'build', repo_root / 'source/gui/Chimera.sln', '-c', 'Release', '/nodeReuse:false', '-p:UseSharedCompilation=false'],
    )
  endif
endif
